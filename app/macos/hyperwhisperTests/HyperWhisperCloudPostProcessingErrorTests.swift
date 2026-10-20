//
//  HyperWhisperCloudPostProcessingErrorTests.swift
//  hyperwhisperTests
//
//  Issue #936: `AIPostProcessor.handleHyperWhisperCloudError` turns a paid
//  HyperWhisper Cloud post-processing failure into a user-facing error — a
//  credit denial, a rate limit, a refused key, a bad request or a server
//  fault — across two ladders: one for a body it can parse, one for a body it
//  cannot. The two ladders disagree on purpose (a 402 with no body has no
//  numbers to report; a 400 with no body is not known to be the client's
//  fault), and nothing held either difference in place.
//
//  The ladder is `static`, so the error tests call it with a status and a
//  body. The 200-path tests drive `performHyperWhisperCloudPostProcessing`
//  through a stubbed `URLSession`, so the real request is built by the shared
//  core and the real response is parsed, with no network.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite(.serialized)
struct HyperWhisperCloudPostProcessingErrorTests {

    // MARK: - The error ladder

    private func thrownError(status: Int, body: String) -> TranscriptionError? {
        do {
            try AIPostProcessor.handleHyperWhisperCloudError(statusCode: status, data: Data(body.utf8))
            Issue.record("HTTP \(status) did not throw")
            return nil
        } catch let error as TranscriptionError {
            return error
        } catch {
            Issue.record("HTTP \(status) threw a non-TranscriptionError: \(error)")
            return nil
        }
    }

    /// A 402 that says the credits are exhausted while reporting MORE remaining
    /// than the request needs is a server contradiction, not a denial: it must
    /// not tell the user to buy credits.
    @Test func a402ThatContradictsItsOwnBalanceIsAnInvalidResponse() throws {
        let body = #"{"message":"Credits exhausted","credits_remaining":12.5,"credits_required":3,"credits_limit":100}"#
        let error = try #require(thrownError(status: 402, body: body))

        let expected = try #require(HyperWhisperCloudCreditDenial(
            errorJson: ["message": "Credits exhausted", "credits_remaining": 12.5, "credits_required": 3, "credits_limit": 100],
            message: "Credits exhausted"
        ).invalidExhaustedBalanceMessage)
        guard case .invalidResponse(let details) = error else {
            Issue.record("expected .invalidResponse, got \(error)")
            return
        }
        #expect(details == expected)
        #expect(details?.contains("12.5 of 100") == true)
    }

    @Test func a402WithABodyReportsTheServersNumbers() throws {
        let body = #"{"message":"Insufficient credits","credits_remaining":2,"credits_required":7}"#
        let error = try #require(thrownError(status: 402, body: body))

        guard case .insufficientCredits(let remaining, let required) = error else {
            Issue.record("expected .insufficientCredits, got \(error)")
            return
        }
        #expect(remaining == 2)
        #expect(required == 7)
    }

    /// The same status with nothing to parse has no numbers, so it reports 0/0.
    @Test func a402WithNoParsableBodyReportsZeroAndZero() throws {
        let error = try #require(thrownError(status: 402, body: "<html>Payment Required</html>"))

        guard case .insufficientCredits(let remaining, let required) = error else {
            Issue.record("expected .insufficientCredits, got \(error)")
            return
        }
        #expect(remaining == 0)
        #expect(required == 0)
    }

    @Test func a500CarriesTheServersMessageOnlyWhenThereIsOne() throws {
        let parsed = try #require(thrownError(status: 500, body: #"{"error":"upstream provider failed"}"#))
        guard case .serverError(let status, let message) = parsed else {
            Issue.record("expected .serverError, got \(parsed)")
            return
        }
        #expect(status == 500)
        #expect(message == "upstream provider failed")

        let unparsed = try #require(thrownError(status: 503, body: "Service Unavailable"))
        guard case .serverError(let status, let message) = unparsed else {
            Issue.record("expected .serverError, got \(unparsed)")
            return
        }
        #expect(status == 503)
        #expect(message == "HyperWhisper Cloud server error")
    }

    /// Only a parsed 400 is the client's bad request; an unparsed one falls to
    /// the generic `HTTP 400` response error.
    @Test func a400IsAnInvalidRequestOnlyWithAParsableBody() throws {
        let parsed = try #require(thrownError(status: 400, body: #"{"message":"transcript is required"}"#))
        guard case .invalidRequest = parsed else {
            Issue.record("expected .invalidRequest, got \(parsed)")
            return
        }

        let unparsed = try #require(thrownError(status: 400, body: ""))
        guard case .invalidResponse(let details) = unparsed else {
            Issue.record("expected .invalidResponse, got \(unparsed)")
            return
        }
        #expect(details == "HTTP 400")
    }

    @Test(arguments: [true, false])
    func rateLimitAndRefusedKeyMatchOnBothLadders(parsable: Bool) throws {
        let body = parsable ? #"{"message":"nope"}"# : "nope"

        let limited = try #require(thrownError(status: 429, body: body))
        guard case .rateLimited(let retryAfter) = limited else {
            Issue.record("expected .rateLimited, got \(limited)")
            return
        }
        #expect(retryAfter == nil)

        for status in [401, 403] {
            let refused = try #require(thrownError(status: status, body: body))
            guard case .unauthorized(let provider, let code) = refused else {
                Issue.record("expected .unauthorized, got \(refused)")
                continue
            }
            #expect(provider == "HyperWhisper Cloud")
            #expect(code == status)
        }

        let other = try #require(thrownError(status: 418, body: body))
        guard case .invalidResponse(let details) = other else {
            Issue.record("expected .invalidResponse, got \(other)")
            return
        }
        #expect(details == "HTTP 418")
    }

    // MARK: - The 200 path

    /// `AIPostProcessor.licenseManager` is `weak`: the caller holds the
    /// returned `LicenseManager` for the whole test.
    private func makeLicensedProcessor() -> (AIPostProcessor, LicenseManager) {
        let processor = AIPostProcessor(settingsManager: SettingsManager.shared)
        let licenseManager = LicenseManager(
            networkService: StoredKeyLicenseNetwork(),
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )
        licenseManager.licenseStatus = .active
        processor.licenseManager = licenseManager
        return (processor, licenseManager)
    }

    private func makeCloudMode(_ persistence: PersistenceController, model: String) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.preset = "hyper"
        mode.postProcessingMode = 1
        mode.postProcessingProvider = PostProcessingProvider.hyperwhisper.rawValue
        mode.cloudPostProcessingModel = model
        return mode
    }

    private static let transcript = "um so the meeting is at three pm tomorrow"

    /// A whitespace-only `corrected` is no correction: the user keeps the
    /// original transcript and the run is not reported as a mutation.
    @Test func aWhitespaceCorrectionReturnsTheOriginalTextAndDoesNotMutate() async throws {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeCloudMode(persistence, model: "groq:openai/gpt-oss-120b")
        let (processor, licenseManager) = makeLicensedProcessor()
        let signal = MutationSignal()
        let stub = StubbedCloudResponse(status: 200, headers: [:], body: #"{"corrected":"  \n\t "}"#)

        let result = try await processor.performHyperWhisperCloudPostProcessing(
            text: Self.transcript,
            mode: mode,
            applicationContext: ApplicationContext.none,
            mutationSignal: signal,
            session: stub.makeSession()
        )

        #expect(stub.requestCount >= 1, "the stubbed session must have answered")
        #expect(result == Self.transcript)
        #expect(processor.didMutateLastRun == false)
        #expect(signal.didMutate == false)
        #expect(signal.resolvedModel == nil)
        #expect(processor.licenseManager === licenseManager)
    }

    /// #314: the reported model is the one the client SENT (`X-LLM-Model`),
    /// never the `X-LLM-Provider` display label the backend answers with.
    @Test func aSuccessReportsTheSentModelNotTheProviderResponseHeader() async throws {
        let storage = "groq:openai/gpt-oss-120b"
        let persistence = PersistenceController(inMemory: true)
        let mode = makeCloudMode(persistence, model: storage)
        let (processor, licenseManager) = makeLicensedProcessor()
        let signal = MutationSignal()
        let servedLabel = "cerebras-gpt-oss-120b"
        let stub = StubbedCloudResponse(
            status: 200,
            headers: ["X-LLM-Provider": servedLabel, "Content-Type": "application/json"],
            body: #"{"corrected":"The meeting is at 3 PM tomorrow.","cost":{"credits":0.02}}"#
        )

        let result = try await processor.performHyperWhisperCloudPostProcessing(
            text: Self.transcript,
            mode: mode,
            applicationContext: ApplicationContext.none,
            mutationSignal: signal,
            session: stub.makeSession()
        )

        let cloudModel = CloudPostProcessingModel.fromStorageValue(storage)
        let sentModel = try #require(stub.lastRequest?.value(forHTTPHeaderField: "X-LLM-Model"))
        #expect(stub.requestCount == 1)
        #expect(result == "The meeting is at 3 PM tomorrow.")
        #expect(processor.didMutateLastRun)
        #expect(signal.didMutate)
        #expect(signal.resolvedProvider == PostProcessingProvider.hyperwhisper.rawValue)
        #expect(signal.resolvedModel == sentModel)
        #expect(signal.resolvedModel == (cloudModel.llmModelHeader ?? cloudModel.modelId))
        #expect(signal.resolvedModel != servedLabel)
        #expect(processor.licenseManager === licenseManager)
    }

    /// The seam reaches the ladder too: a refused key from the stubbed session
    /// is surfaced inline, and the user keeps the original text.
    @Test func aRefusedKeyThroughTheSessionSurfacesInlineAndKeepsTheText() async throws {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeCloudMode(persistence, model: "groq:openai/gpt-oss-120b")
        let (processor, licenseManager) = makeLicensedProcessor()
        var surfaced: [TranscriptionError] = []
        processor.onPostProcessingError = { surfaced.append($0) }
        let signal = MutationSignal()
        let stub = StubbedCloudResponse(status: 401, headers: [:], body: #"{"message":"Invalid license"}"#)

        let result = try await processor.performHyperWhisperCloudPostProcessing(
            text: Self.transcript,
            mode: mode,
            applicationContext: ApplicationContext.none,
            mutationSignal: signal,
            session: stub.makeSession()
        )

        #expect(stub.requestCount == 1, "a 401 is not retried")
        #expect(result == Self.transcript)
        #expect(signal.didMutate == false)
        guard case .unauthorized(let provider, let code)? = surfaced.first else {
            Issue.record("expected .unauthorized surfaced inline, got \(surfaced)")
            return
        }
        #expect(provider == "HyperWhisper Cloud")
        #expect(code == 401)
        #expect(processor.licenseManager === licenseManager)
    }
}

// MARK: - Stubs

/// One canned HyperWhisper Cloud answer, served to a session of its own. The
/// URL protocol class is shared, so the suite is `.serialized`; the current
/// stub is swapped in by `makeSession()`.
private final class StubbedCloudResponse {
    let status: Int
    let headers: [String: String]
    let body: Data
    private let lock = NSLock()
    private var requests: [URLRequest] = []

    init(status: Int, headers: [String: String], body: String) {
        self.status = status
        self.headers = headers
        self.body = Data(body.utf8)
    }

    var requestCount: Int { lock.withLock { requests.count } }
    var lastRequest: URLRequest? { lock.withLock { requests.last } }

    func makeSession() -> URLSession {
        StubbedCloudURLProtocol.setCurrent(self)
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubbedCloudURLProtocol.self]
        return URLSession(configuration: config)
    }

    fileprivate func record(_ request: URLRequest) {
        lock.withLock { requests.append(request) }
    }
}

private final class StubbedCloudURLProtocol: URLProtocol {
    private static let lock = NSLock()
    private static var current: StubbedCloudResponse?

    static func setCurrent(_ stub: StubbedCloudResponse) {
        lock.withLock { current = stub }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let stub = Self.lock.withLock({ Self.current }),
              let url = request.url,
              let response = HTTPURLResponse(
                url: url,
                statusCode: stub.status,
                httpVersion: "HTTP/1.1",
                headerFields: stub.headers
              ) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        stub.record(request)
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: stub.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

/// A stored account key, so `getTranscriptionIdentifier()` answers licensed
/// once the manager's status is `.active`. The key never leaves the stubbed
/// session.
private final class StoredKeyLicenseNetwork: LicenseNetworkServing {
    private let invalid = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: nil
    )

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult { invalid }
    func deactivateLicense() async -> (success: Bool, error: String?) { (true, nil) }
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult { invalid }
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult { invalid }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { "stubbed-account-key" }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
