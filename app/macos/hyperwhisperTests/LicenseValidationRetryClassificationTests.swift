//
//  LicenseValidationRetryClassificationTests.swift
//  hyperwhisperTests
//
//  #688: `LicenseNetworkService.performValidation` decides which licence-server
//  reply is retried (5xx, 429, a non-HTTP reply) and which is a terminal
//  verdict on the user (any other non-200). `LicenseNetworkResilienceTests`
//  pins the retry numbers; these drive the real service through its `session:`
//  seam with scripted replies and count how many requests each reply costs.
//
//  The retries sleep real backoff (`performWithRetry` has no sleeper seam yet),
//  so an exhausted `.cloud` budget costs ~6-7 s per test.
//

import Foundation
import Testing
@testable import HyperWhisper

/// One scripted reply from the licence server.
private enum ScriptedReply {
    case http(status: Int, body: String)
    /// A plain `URLResponse`, not an `HTTPURLResponse`.
    case nonHTTP
}

/// Answers licence requests from a per-key script. Each test validates its own
/// licence key, so tests can run in parallel without sharing a script. The
/// last reply in a script repeats once the script runs out.
private final class ScriptedLicenseURLProtocol: URLProtocol {
    private static let lock = NSLock()
    private static var scripts: [String: [ScriptedReply]] = [:]
    private static var requests: [String: [URLRequest]] = [:]

    static func makeSession(key: String, replies: [ScriptedReply]) -> URLSession {
        lock.withLock {
            scripts[key] = replies
            requests[key] = []
        }
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ScriptedLicenseURLProtocol.self]
        return URLSession(configuration: config)
    }

    static func requests(for key: String) -> [URLRequest] {
        lock.withLock { requests[key] ?? [] }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url, let key = Self.licenseKey(of: request) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        let reply: ScriptedReply? = Self.lock.withLock {
            guard var script = Self.scripts[key], let next = script.first else { return nil }
            Self.requests[key, default: []].append(request)
            if script.count > 1 {
                script.removeFirst()
                Self.scripts[key] = script
            }
            return next
        }
        guard let reply else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }

        let response: URLResponse
        let body: Data
        switch reply {
        case let .http(status, text):
            guard let http = HTTPURLResponse(
                url: url,
                statusCode: status,
                httpVersion: "HTTP/1.1",
                headerFields: ["Content-Type": "application/json"]
            ) else {
                client?.urlProtocol(self, didFailWithError: URLError(.badURL))
                return
            }
            response = http
            body = Data(text.utf8)
        case .nonHTTP:
            response = URLResponse(url: url, mimeType: nil, expectedContentLength: 0, textEncodingName: nil)
            body = Data()
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    /// A protocol sees the POST body as a stream, not `httpBody`.
    private static func licenseKey(of request: URLRequest) -> String? {
        var data = request.httpBody ?? Data()
        if data.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            defer { stream.close() }
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let read = stream.read(&buffer, maxLength: buffer.count)
                guard read > 0 else { break }
                data.append(buffer, count: read)
            }
        }
        let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        return object?["license_key"] as? String
    }
}

private final class InMemoryLicenseCredentialStore: LicenseCredentialStore {
    private let lock = NSLock()
    private var storage: [LicenseCredentialDescriptor: Data] = [:]

    func read(item: LicenseCredentialDescriptor) throws -> Data? {
        lock.withLock { storage[item] }
    }

    func write(_ data: Data, item: LicenseCredentialDescriptor) throws {
        lock.withLock { storage[item] = data }
    }

    func delete(item: LicenseCredentialDescriptor) throws {
        _ = lock.withLock { storage.removeValue(forKey: item) }
    }
}

struct LicenseValidationRetryClassificationTests {
    /// The budget a probe (and any validation with no cached verdict) runs under.
    private static let standardAttempts = LicenseNetworkService.requestPolicy(
        isLaunchValidation: false,
        hasCachedVerdict: false
    ).retryConfig.maxAttempts

    private static let offlineMessage = "Unable to verify license while offline"

    private func makeStore() -> RustLicenseStore {
        RustLicenseStore(
            defaults: makeDefaults(),
            licenseStore: LicenseKeychainStore(credentialStore: InMemoryLicenseCredentialStore()),
            seedUsage: false
        )
    }

    private func makeService(
        key: String,
        replies: [ScriptedReply],
        store: RustLicenseStore? = nil
    ) -> LicenseNetworkService {
        LicenseNetworkService(
            store: store ?? makeStore(),
            session: ScriptedLicenseURLProtocol.makeSession(key: key, replies: replies)
        )
    }

    private func makeDefaults() -> UserDefaults {
        let suiteName = "LicenseValidationRetryClassificationTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        return defaults
    }

    @Test func serverErrorIsRetriedUntilTheBudgetRunsOut() async {
        let key = "retry-503-\(UUID().uuidString)"
        let service = makeService(key: key, replies: [.http(status: 503, body: #"{"error":"Service Unavailable"}"#)])

        let result = await service.probeLicense(key)

        #expect(ScriptedLicenseURLProtocol.requests(for: key).count == Self.standardAttempts)
        #expect(!result.isValid)
        #expect(result.status == .invalid)
        // An exhausted retry is "could not reach the server", never the 503's own text.
        #expect(result.errorMessage == Self.offlineMessage)
    }

    @Test func rateLimitIsRetriedLikeAServerError() async {
        let key = "retry-429-\(UUID().uuidString)"
        let service = makeService(key: key, replies: [.http(status: 429, body: #"{"error":"Too Many Requests"}"#)])

        let result = await service.probeLicense(key)

        #expect(ScriptedLicenseURLProtocol.requests(for: key).count == Self.standardAttempts)
        #expect(!result.isValid)
        #expect(result.errorMessage == Self.offlineMessage)
    }

    @Test func forbiddenIsATerminalVerdictAfterOneRequest() async {
        let key = "terminal-403-\(UUID().uuidString)"
        let service = makeService(key: key, replies: [
            .http(status: 403, body: #"{"error":"License has been revoked"}"#),
            .http(status: 200, body: #"{"status":"active"}"#)
        ])

        let result = await service.probeLicense(key)

        #expect(ScriptedLicenseURLProtocol.requests(for: key).count == 1)
        #expect(!result.isValid)
        #expect(result.status == .invalid)
        #expect(result.errorMessage == "License has been revoked")
    }

    @Test func nonHTTPReplyIsRetriedAsABadServerResponse() async {
        let key = "non-http-\(UUID().uuidString)"
        let service = makeService(key: key, replies: [.nonHTTP])

        let result = await service.probeLicense(key)

        #expect(ScriptedLicenseURLProtocol.requests(for: key).count == Self.standardAttempts)
        #expect(!result.isValid)
        #expect(result.errorMessage == Self.offlineMessage)
    }

    /// The `.licenseLaunchValidation` preset exists so a transient blip at
    /// launch self-heals: a 503 then a 200 must end Active on attempt 2.
    @Test func launchValidationSelfHealsAfterOneServerError() async throws {
        let key = "self-heal-\(UUID().uuidString)"
        let store = makeStore()
        // A cached Active verdict for this key puts launch validation on the
        // tight launch policy instead of the standard one.
        licensePersistValidationVerdict(
            store: store,
            status: .active,
            attemptedKey: key,
            nowUnixSecs: RustLicenseTime.nowUTC()
        )
        let service = makeService(key: key, replies: [
            .http(status: 503, body: #"{"error":"Service Unavailable"}"#),
            .http(status: 200, body: #"{"status":"active"}"#)
        ], store: store)

        let result = await service.validateLicense(
            key,
            isLaunchValidation: true,
            expectedStoredLicenseKey: key
        )

        let requests = ScriptedLicenseURLProtocol.requests(for: key)
        #expect(requests.count == 2)
        #expect(requests.allSatisfy { $0.timeoutInterval == NetworkConfig.licenseLaunchValidationTimeout })
        #expect(result.isValid)
        #expect(result.status == .active)
        #expect(!result.networkFailureFallback)
    }

    // MARK: - #2916: a 200 that is not the validate JSON is not a verdict

    /// A captive portal answers 200 with its own HTML login page. That is not
    /// the server's word on the key: the cached Active verdict must be served
    /// and the cache left stale, so the next check goes back to the server.
    @Test func captivePortal200ServesTheCachedVerdictAndCachesNothing() async {
        for body in ["<html><body>Please log in to Hotel WiFi</body></html>", ""] {
            let key = "captive-\(UUID().uuidString)"
            let store = makeStore()
            // Validated Active two days ago: past the 24 h cache, inside grace.
            let validatedAt = RustLicenseTime.nowUTC() - 2 * 86_400
            licensePersistValidationVerdict(
                store: store,
                status: .active,
                attemptedKey: key,
                nowUnixSecs: validatedAt
            )
            let service = makeService(key: key, replies: [.http(status: 200, body: body)], store: store)

            let result = await service.validateLicense(
                key,
                isLaunchValidation: true,
                expectedStoredLicenseKey: key
            )

            let now = RustLicenseTime.nowUTC()
            #expect(!ScriptedLicenseURLProtocol.requests(for: key).isEmpty)
            #expect(result.isValid, "\(body.debugDescription)")
            #expect(result.status == .active, "\(body.debugDescription)")
            #expect(licenseCachedStatusWithinGrace(store: store, nowUnixSecs: now) == .active)
            #expect(licenseShouldRevalidate(store: store, nowUnixSecs: now))
            #expect(licenseStoredLicenseKey(store: store) == key)
        }
    }

    /// An onboarding probe has no cache to fall back on: a captive-portal 200
    /// reads as "could not reach the server", not as "this key is invalid".
    @Test func captivePortal200ProbeIsUnreachableNotInvalid() async {
        let key = "captive-probe-\(UUID().uuidString)"
        let service = makeService(key: key, replies: [.http(status: 200, body: "<html>login</html>")])

        let result = await service.probeLicense(key)

        #expect(!result.isValid)
        #expect(result.errorMessage == Self.offlineMessage)
    }

    /// Guard against over-fixing: a real `{"valid":false}` for the stored key is
    /// still a verdict, cached as Invalid and trusted for the cache window.
    @Test func realValidFalseForTheStoredKeyStillCachesInvalid() async {
        let key = "real-invalid-\(UUID().uuidString)"
        let store = makeStore()
        licensePersistValidationVerdict(
            store: store,
            status: .active,
            attemptedKey: key,
            nowUnixSecs: RustLicenseTime.nowUTC() - 2 * 86_400
        )
        let service = makeService(key: key, replies: [.http(status: 200, body: #"{"valid":false}"#)], store: store)

        let result = await service.validateLicense(
            key,
            isLaunchValidation: true,
            expectedStoredLicenseKey: key
        )

        let now = RustLicenseTime.nowUTC()
        #expect(ScriptedLicenseURLProtocol.requests(for: key).count == 1)
        #expect(!result.isValid)
        #expect(result.status == .invalid)
        #expect(licenseCachedStatusWithinGrace(store: store, nowUnixSecs: now) == .invalid)
        #expect(!licenseShouldRevalidate(store: store, nowUnixSecs: now))
    }
}
