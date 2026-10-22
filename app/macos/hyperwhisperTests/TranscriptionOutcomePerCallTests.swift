//
//  TranscriptionOutcomePerCallTests.swift
//  hyperwhisperTests
//
//  Issue #754: `transcribe(...)` returned only the text, and the detected
//  language, the timestamps, the attempt diagnostics and HyperWhisper Cloud's
//  server-corrected text were parked on the provider instance and read after
//  the `await`. The router caches one instance per provider and the GUI
//  pipeline and the Local API share it, so two overlapping calls read each
//  other's results. `transcribeDetailed(...)` now returns them per call.
//
//  The HyperWhisper Cloud cases drive the REAL provider: its licence check, the
//  request the Rust core builds, the core's response parser and its
//  /post-process call all run. Only the URLSession is swapped for one whose
//  URLProtocol answers like the backend, so nothing leaves the process.
//
//  Local Whisper cannot be driven the same way: a pass needs a loaded
//  whisper.cpp model, which this target has no fixture for (see
//  LocalAPIWhisperQueueTests). Its per-call timestamps are proven through
//  `any TranscriptionProvider` with a conformer below, plus the type system:
//  the pass task now returns a `TranscriptionOutcome`, and no stored
//  timestamp property is left to read.
//

import AVFoundation
import CoreData
import Foundation
import Testing
@testable import HyperWhisper

// MARK: - A scripted HyperWhisper Cloud backend

/// What the stub backend does. One shared script, because URLSession builds
/// the URLProtocol instances itself; the suite is `.serialized`, and the stub
/// is only reachable through the sessions this file builds.
private final class ScriptedCloudBackend: @unchecked Sendable {
    static let shared = ScriptedCloudBackend()

    private let lock = NSLock()
    private var holdFirstPostProcess = false
    private var answerNoSpeech = false
    private var held: [ScriptedCloudURLProtocol] = []
    private var transcribeLanguages: [String] = []
    private var postProcessTexts: [String] = []

    func reset(holdFirstPostProcess: Bool = false, answerNoSpeech: Bool = false) {
        lock.withLock {
            self.holdFirstPostProcess = holdFirstPostProcess
            self.answerNoSpeech = answerNoSpeech
            held = []
            transcribeLanguages = []
            postProcessTexts = []
        }
    }

    var heldCount: Int { lock.withLock { held.count } }
    var languagesSeen: [String] { lock.withLock { transcribeLanguages } }
    var postProcessTextsSeen: [String] { lock.withLock { postProcessTexts } }

    enum Reply {
        case now(status: Int, headers: [String: String], body: Data)
        case hold
    }

    func reply(to request: URLRequest, body: Data) -> Reply {
        let path = request.url?.path ?? ""
        if path.hasSuffix("/transcribe") {
            let query = request.url.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems } ?? []
            let language = query.first { $0.name == "language" }?.value ?? "none"
            let noSpeech = lock.withLock { () -> Bool in
                transcribeLanguages.append(language)
                return answerNoSpeech
            }
            let json: [String: Any] = noSpeech
                ? ["no_speech_detected": true, "text": ""]
                : ["text": "speech \(language)", "language": language]
            return .now(
                status: 200,
                headers: [
                    "Content-Type": "application/json",
                    "X-Request-ID": noSpeech ? "req-no-speech" : "req-\(language)",
                    "X-STT-Provider": "deepgram",
                ],
                body: (try? JSONSerialization.data(withJSONObject: json)) ?? Data()
            )
        }
        if path.hasSuffix(NetworkConfig.hyperwhisperCloudPostProcessEndpoint) {
            let text = ((try? JSONSerialization.jsonObject(with: body)) as? [String: Any])?["text"] as? String ?? ""
            let hold = lock.withLock { () -> Bool in
                postProcessTexts.append(text)
                if holdFirstPostProcess {
                    holdFirstPostProcess = false
                    return true
                }
                return false
            }
            return hold ? .hold : Self.corrected(text)
        }
        return .now(status: 404, headers: [:], body: Data())
    }

    static func corrected(_ text: String) -> Reply {
        let json = ["corrected": "corrected \(text)"]
        return .now(
            status: 200,
            headers: ["Content-Type": "application/json"],
            body: (try? JSONSerialization.data(withJSONObject: json)) ?? Data()
        )
    }

    func park(_ request: ScriptedCloudURLProtocol) {
        lock.withLock { held.append(request) }
    }

    /// Answers every held /post-process request, on its own loading thread.
    func releaseHeld() {
        let toRelease = lock.withLock { () -> [ScriptedCloudURLProtocol] in
            let parked = held
            held = []
            return parked
        }
        toRelease.forEach { $0.answerHeldRequest() }
    }
}

/// Answers HyperWhisper Cloud's /transcribe and /post-process the way the
/// backend does. A held request is answered later on the thread that started
/// it, the way Apple's custom-protocol sample delivers asynchronously.
private final class ScriptedCloudURLProtocol: URLProtocol {
    static func makeSession() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ScriptedCloudURLProtocol.self]
        return URLSession(configuration: config)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    private var clientThread: Thread?
    private var clientModes: [String] = [RunLoop.Mode.default.rawValue]
    private var heldBody = Data()

    override func startLoading() {
        let body = Self.readBody(of: request)
        switch ScriptedCloudBackend.shared.reply(to: request, body: body) {
        case .now(let status, let headers, let data):
            deliver(status: status, headers: headers, body: data)
        case .hold:
            clientThread = Thread.current
            if let mode = RunLoop.current.currentMode {
                clientModes = [mode.rawValue]
            }
            heldBody = body
            ScriptedCloudBackend.shared.park(self)
        }
    }

    override func stopLoading() {}

    func answerHeldRequest() {
        guard let thread = clientThread else { return }
        perform(#selector(deliverHeld), on: thread, with: nil, waitUntilDone: false, modes: clientModes)
    }

    @objc private func deliverHeld() {
        let text = ((try? JSONSerialization.jsonObject(with: heldBody)) as? [String: Any])?["text"] as? String ?? ""
        if case .now(let status, let headers, let data) = ScriptedCloudBackend.corrected(text) {
            deliver(status: status, headers: headers, body: data)
        }
    }

    private func deliver(status: Int, headers: [String: String], body: Data) {
        guard let url = request.url,
              let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    /// A URLProtocol sees a POST body as a stream, not as `httpBody`.
    private static func readBody(of request: URLRequest) -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 16 * 1024)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: buffer.count)
            if read <= 0 { break }
            data.append(buffer, count: read)
        }
        return data
    }
}

/// Reports a stored account key, so the real entitlement check passes; every
/// network call is a no-op refusal, so nothing leaves the process.
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
    func getStoredLicenseKey() -> String? { "fixture-account-key-754" }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}

// MARK: - A conformer for the protocol-level contract

/// One shared instance whose German pass is held until released, so a French
/// pass runs start to finish inside it. Each pass returns timings of its own.
private final class PerCallResultProvider: TranscriptionProvider, @unchecked Sendable {
    let name = "Per-call results"
    var isAvailable: Bool { true }
    var supportsConcurrentPasses: Bool { true }

    private let lock = NSLock()
    private var heldStarted = false
    private var heldReleased = false

    var germanPassIsHeld: Bool { lock.withLock { heldStarted } }
    func releaseGermanPass() { lock.withLock { heldReleased = true } }

    static func timestamps(for language: String, start: Double) -> TranscriptionTimestamps {
        TranscriptionTimestamps(
            segments: [TranscriptionSegmentTimestamp(id: 0, start: start, end: start + 1, text: "speech \(language)")],
            words: [TranscriptionWordTimestamp(word: language, start: start, end: start + 0.5, probability: nil)],
            rawText: "speech \(language)"
        )
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        try await transcribeDetailed(audioURL: audioURL, language: language, mode: mode, vocabulary: vocabulary).text
    }

    func transcribeDetailed(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> TranscriptionOutcome {
        let language = language ?? "none"
        if language == "de" {
            lock.withLock { heldStarted = true }
            while !lock.withLock({ heldReleased }) {
                try await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        return TranscriptionOutcome(
            text: "speech \(language)",
            detectedLanguage: language,
            timestamps: Self.timestamps(for: language, start: language == "de" ? 10 : 20)
        )
    }
}

/// Implements only `transcribe`, like sixteen of the app's conformers.
private final class TextOnlyProvider: TranscriptionProvider {
    let name = "Text only"
    var isAvailable: Bool { true }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        "plain text"
    }
}

// MARK: - Tests

@MainActor
@Suite(.serialized)
struct TranscriptionOutcomePerCallTests {

    /// Waits up to `seconds` for `condition`. The full suite runs in parallel
    /// in one process, so the deadline is generous.
    private static func eventually(_ seconds: Double = 20, _ condition: () -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if condition() { return true }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        return condition()
    }

    /// A short mono tone in its own temporary folder (a real, non-empty WAV).
    private static func toneWAV() throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("hw-754-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent("tone.wav")
        let sampleRate = 16_000.0
        let frames = AVAudioFrameCount(0.5 * sampleRate)
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        try autoreleasepool {
            let file = try AVAudioFile(forWriting: url, settings: settings)
            let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames))
            let channel = try #require(buffer.floatChannelData?[0])
            buffer.frameLength = frames
            for index in 0..<Int(frames) {
                channel[index] = 0.25 * sinf(2 * Float.pi * 440 * Float(index) / Float(sampleRate))
            }
            try file.write(from: buffer)
        }
        return url
    }

    private static func licensedManager() -> LicenseManager {
        let manager = LicenseManager(
            networkService: StoredKeyLicenseNetwork(),
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )
        manager.licenseStatus = .active
        return manager
    }

    /// The real provider on the scripted backend.
    private static func cloudProvider() -> HyperWhisperCloudProvider {
        let licenseManager = licensedManager()
        let provider = HyperWhisperCloudProvider(
            licenseManager: licenseManager,
            creditManager: HyperWhisperCloudManager(
                licenseManager: licenseManager,
                transport: { _ in throw URLError(.notConnectedToInternet) }
            ),
            settingsManager: nil,
            session: ScriptedCloudURLProtocol.makeSession()
        )
        // Pre-captured, so the post-process prompt never gathers the screen.
        provider.applicationContext = ApplicationContext.none
        return provider
    }

    private static func mode(_ persistence: PersistenceController, postProcessing: Bool) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = "Test 754"
        mode.preset = "hyper"
        mode.model = "cloud"
        mode.language = "en"
        mode.postProcessingMode = postProcessing ? 1 : 0
        mode.postProcessingProvider = postProcessing ? PostProcessingProvider.hyperwhisper.rawValue : nil
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        persistence.save()
        return mode
    }

    // MARK: HyperWhisper Cloud, the real provider

    /// Call A (German) is parked inside its /post-process, after it has
    /// received its own /transcribe answer. Call B (French) then runs start to
    /// finish on the SAME instance. Before #754, A read B's detected language
    /// and diagnostics back off the instance after its await.
    @Test func overlappingCloudCallsEachGetTheirOwnResults() async throws {
        let backend = ScriptedCloudBackend.shared
        backend.reset(holdFirstPostProcess: true)
        defer { backend.releaseHeld() }

        let persistence = PersistenceController(inMemory: true)
        let mode = Self.mode(persistence, postProcessing: true)
        let provider = Self.cloudProvider()
        let shared: any TranscriptionProvider = provider
        let audio = try Self.toneWAV()
        defer { try? FileManager.default.removeItem(at: audio.deletingLastPathComponent()) }

        let callA = Task {
            try await shared.transcribeDetailed(audioURL: audio, language: "de", mode: mode, vocabulary: [])
        }
        #expect(await Self.eventually { backend.heldCount == 1 }, "call A never reached its /post-process")

        let outcomeB = try await shared.transcribeDetailed(audioURL: audio, language: "fr", mode: mode, vocabulary: [])
        #expect(backend.heldCount == 1, "call A must still be in flight while B finishes")

        backend.releaseHeld()
        let outcomeA = try await callA.value

        #expect(backend.languagesSeen == ["de", "fr"])
        #expect(backend.postProcessTextsSeen == ["speech de", "speech fr"])

        #expect(outcomeA.text == "speech de")
        #expect(outcomeA.detectedLanguage == "de")
        #expect(outcomeA.diagnostics?.backendRequestId == "req-de")
        #expect(outcomeA.serverCorrectedText == "corrected speech de")
        #expect(outcomeA.timestamps == nil, "HyperWhisper Cloud returns no timestamps")

        #expect(outcomeB.text == "speech fr")
        #expect(outcomeB.detectedLanguage == "fr")
        #expect(outcomeB.diagnostics?.backendRequestId == "req-fr")
        #expect(outcomeB.serverCorrectedText == "corrected speech fr")
        #expect(outcomeB.timestamps == nil)
    }

    /// A no-speech answer still reaches `lastFailedAttemptDiagnostics` through
    /// the real pipeline, now on the thrown `TranscriptionAttemptFailure`, and
    /// the pipeline's caller sees the ORIGINAL error, not the wrapper.
    @Test func noSpeechDiagnosticsReachThePipelineAndTheOriginalErrorIsThrown() async throws {
        let backend = ScriptedCloudBackend.shared
        backend.reset(answerNoSpeech: true)

        let persistence = PersistenceController(inMemory: true)
        let mode = Self.mode(persistence, postProcessing: false)
        let provider = Self.cloudProvider()
        let audio = try Self.toneWAV()
        defer { try? FileManager.default.removeItem(at: audio.deletingLastPathComponent()) }

        let pipeline = TranscriptionPipeline()
        pipeline.providerCoordinator.engineOverrideForTesting = provider
        pipeline.providerCoordinator.selectionOverrideForTesting = { _ in provider }

        var thrown: Error?
        do {
            _ = try await pipeline.transcribeWithDetails(audioURL: audio, mode: mode)
            Issue.record("a no-speech answer returned a result")
        } catch {
            thrown = error
        }
        #expect(!(thrown is TranscriptionAttemptFailure), "the wrapper escaped the pipeline")
        guard case .noSpeechDetected? = thrown as? TranscriptionError else {
            Issue.record("expected noSpeechDetected, got \(String(describing: thrown))")
            return
        }

        let diagnostics = try #require(pipeline.lastFailedAttemptDiagnostics)
        #expect(diagnostics.attemptSource == "cloud_instrumented", "the provider's own diagnostics, not the uninstrumented fallback")
        #expect(diagnostics.backendRequestId == "req-no-speech")
        #expect(diagnostics.backendSTTProvider == "deepgram")
        #expect(diagnostics.backendNoSpeechDetected == true)
        #expect(diagnostics.httpStatusCode == 200)
        #expect(diagnostics.providerAttemptMs != nil)
    }

    /// The plain `transcribe(...)` on HyperWhisper Cloud throws the original
    /// error too, and `transcribeDetailed` carries this attempt's diagnostics.
    @Test func cloudNoSpeechThrowsTheOriginalErrorFromTranscribe() async throws {
        let backend = ScriptedCloudBackend.shared
        backend.reset(answerNoSpeech: true)

        let persistence = PersistenceController(inMemory: true)
        let mode = Self.mode(persistence, postProcessing: false)
        let provider = Self.cloudProvider()
        let audio = try Self.toneWAV()
        defer { try? FileManager.default.removeItem(at: audio.deletingLastPathComponent()) }

        do {
            _ = try await provider.transcribe(audioURL: audio, language: "en", mode: mode, vocabulary: [])
            Issue.record("transcribe returned text for a no-speech answer")
        } catch let error as TranscriptionError {
            guard case .noSpeechDetected = error else {
                Issue.record("expected noSpeechDetected, got \(error)")
                return
            }
        } catch {
            Issue.record("expected the original TranscriptionError, got \(error)")
        }

        do {
            _ = try await provider.transcribeDetailed(audioURL: audio, language: "en", mode: mode, vocabulary: [])
            Issue.record("transcribeDetailed returned an outcome for a no-speech answer")
        } catch let failure as TranscriptionAttemptFailure {
            guard case .noSpeechDetected? = failure.underlying as? TranscriptionError else {
                Issue.record("expected noSpeechDetected underneath, got \(failure.underlying)")
                return
            }
            #expect(failure.diagnostics?.backendRequestId == "req-no-speech")
        } catch {
            Issue.record("expected TranscriptionAttemptFailure, got \(error)")
        }
    }

    // MARK: The protocol contract

    /// Two interleaved calls on one shared instance, through
    /// `any TranscriptionProvider`: each gets its own language and timings.
    /// This also proves `transcribeDetailed` is a protocol requirement — an
    /// extension-only method would dispatch to the text-only default here and
    /// return nil timings.
    @Test func interleavedCallsThroughTheProtocolEachGetTheirOwnTimestamps() async throws {
        let provider = PerCallResultProvider()
        let shared: any TranscriptionProvider = provider
        let audio = URL(fileURLWithPath: "/dev/null")

        let callA = Task {
            try await shared.transcribeDetailed(audioURL: audio, language: "de", mode: nil, vocabulary: [])
        }
        #expect(await Self.eventually { provider.germanPassIsHeld })
        let outcomeB = try await shared.transcribeDetailed(audioURL: audio, language: "fr", mode: nil, vocabulary: [])
        provider.releaseGermanPass()
        let outcomeA = try await callA.value

        #expect(outcomeA.detectedLanguage == "de")
        #expect(outcomeA.timestamps?.segments.map(\.start) == [10])
        #expect(outcomeA.timestamps?.words?.map(\.word) == ["de"])
        #expect(outcomeA.timestamps?.rawText == "speech de")

        #expect(outcomeB.detectedLanguage == "fr")
        #expect(outcomeB.timestamps?.segments.map(\.start) == [20])
        #expect(outcomeB.timestamps?.words?.map(\.word) == ["fr"])
        #expect(outcomeB.timestamps?.rawText == "speech fr")
    }

    /// A conformer that implements only `transcribe` gets the extension
    /// default: its text, and nil for every per-call result.
    @Test func theDefaultOutcomeIsTheTextAlone() async throws {
        let shared: any TranscriptionProvider = TextOnlyProvider()
        let outcome = try await shared.transcribeDetailed(
            audioURL: URL(fileURLWithPath: "/dev/null"),
            language: nil,
            mode: nil,
            vocabulary: []
        )
        #expect(outcome.text == "plain text")
        #expect(outcome.detectedLanguage == nil)
        #expect(outcome.timestamps == nil)
        #expect(outcome.diagnostics == nil)
        #expect(outcome.serverCorrectedText == nil)
    }

    // MARK: Wiring that a call cannot reach

    /// Local Whisper's pass returns its timings in its outcome. A real pass
    /// needs a loaded model this target has no fixture for, so this reads the
    /// source: the kept run's timings go into the returned outcome, and no
    /// stored timing is left on the shared instance.
    @Test func localWhisperReturnsItsTimingsWithThePass() throws {
        let path = "app/macos/hyperwhisper/Managers/Transcription/Providers/Local/LibWhisperProvider.swift"
        let code = try ProductionSource.code(of: path)
        #expect(!code.contains("lastTimestamps"))
        #expect(code.contains("let task = Task<TranscriptionOutcome, Error> {"))
        let kept = try ProductionSource.slice(
            of: path,
            from: "logger.info(\"🔇 Whisper output remained empty after retries\")",
            to: "transcriptionTask = task"
        )
        #expect(kept.contains("return TranscriptionOutcome(text: text, timestamps: capturedTimed)"))

        let queued = try ProductionSource.slice(
            of: path,
            from: "func transcribeQueuedForLocalAPI(",
            to: "private func performTranscribe("
        )
        #expect(queued.contains("async throws -> TranscriptionOutcome {"))
        #expect(!queued.contains("self.detectedLanguage"))
    }

    /// Neither caller reads a result off the provider instance any more, and
    /// the server-corrected text is no longer reached through a downcast.
    @Test func neitherCallerReadsPerCallStateOffTheProvider() throws {
        let pipeline = try ProductionSource.code(
            of: "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift"
        )
        #expect(!pipeline.contains("aiEnhancedText"))
        #expect(!pipeline.contains("provider.detectedLanguage"))
        #expect(!pipeline.contains("provider.lastTimestamps"))
        #expect(!pipeline.contains("provider.lastAttemptDiagnostics"))
        #expect(pipeline.contains("outcome.serverCorrectedText"))
        // Ray 2026-10-08: the applicationContext input and the `is` check stay.
        #expect(pipeline.contains("if let hwProvider = provider as? HyperWhisperCloudProvider {"))
        #expect(pipeline.contains("let isHyperwhisperTranscription = provider is HyperWhisperCloudProvider"))

        let endpoint = try ProductionSource.code(
            of: "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"
        )
        #expect(!endpoint.contains("resolution.provider.detectedLanguage"))
        #expect(!endpoint.contains("resolution.provider.lastTimestamps"))

        // Both callers unwrap the failure wrapper at the call site.
        for code in [pipeline, endpoint] {
            #expect(code.contains("catch let failure as TranscriptionAttemptFailure {"))
            #expect(code.contains("throw failure.underlying"))
        }
    }
}
