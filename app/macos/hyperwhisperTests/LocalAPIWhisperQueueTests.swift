//
//  LocalAPIWhisperQueueTests.swift
//  hyperwhisperTests
//
//  Issue #1465: overlapping Local API `/transcribe` calls on local Whisper
//  cancelled each other, because the router hands every request the one
//  `LibWhisperProvider` and each new pass supersedes the running one. They now
//  queue on that provider. An in-app dictation still wins over a running API
//  call, and a queued API call waits for a running dictation instead of
//  superseding it.
//
//  `LibWhisperProvider` needs a real `WhisperModelManager` and a real
//  `whisper_context`, so it cannot be built here. The gate it uses is tested
//  directly; the wiring is pinned by reading the production source.
//

import Foundation
import Testing
@testable import HyperWhisper

/// Records the order things happened in, across tasks.
private actor EventLog {
    private(set) var events: [String] = []
    func record(_ event: String) { events.append(event) }
}

@Suite struct LocalAPIWhisperQueueTests {

    private static let providerSource =
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Local/LibWhisperProvider.swift"
    private static let endpointSource =
        "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"
    private static let routerSource =
        "app/macos/hyperwhisper/Managers/Transcription/Coordinators/TranscriptionProviderRouter.swift"

    // MARK: - The gate

    /// A Local API pass that arrives while a dictation runs waits for it to
    /// end, rather than starting (and superseding it).
    @Test func anAPIPassWaitsForARunningDictation() async throws {
        let gate = LibWhisperPassGate()
        let log = EventLog()

        await gate.begin()  // a dictation is running
        let api = Task {
            await gate.beginWhenIdle()
            await log.record("api started")
            await gate.end()
        }
        // Give the API pass every chance to start early.
        for _ in 0..<50 { await Task.yield() }
        try await Task.sleep(nanoseconds: 50_000_000)
        await log.record("dictation ended")
        await gate.end()
        await api.value

        #expect(await log.events == ["dictation ended", "api started"])
    }

    /// A dictation starts at once, even while an API pass is running: the
    /// person at the Mac wins (#1425).
    @Test func aDictationDoesNotWaitForAnAPIPass() async {
        let gate = LibWhisperPassGate()
        await gate.beginWhenIdle()  // an API pass is running
        await gate.begin()          // returns at once; would hang otherwise
        await gate.end()
        await gate.end()
    }

    /// An idle gate lets an API pass straight in, and a dictation that starts
    /// while an API pass waits keeps it waiting until both are done.
    @Test func anAPIPassWaitsUntilEveryRunningPassHasEnded() async throws {
        let gate = LibWhisperPassGate()
        let log = EventLog()

        await gate.begin()  // dictation 1
        let api = Task {
            await gate.beginWhenIdle()
            await log.record("api started")
            await gate.end()
        }
        try await Task.sleep(nanoseconds: 20_000_000)
        await gate.begin()  // dictation 2 overlaps dictation 1
        await gate.end()    // dictation 1 done
        try await Task.sleep(nanoseconds: 50_000_000)
        await log.record("dictation 2 ended")
        await gate.end()
        await api.value

        #expect(await log.events == ["dictation 2 ended", "api started"])

        // Idle now: the next API pass does not wait.
        await gate.beginWhenIdle()
        await gate.end()
    }

    // MARK: - The wiring

    /// Every Local API Whisper request goes through the queued pass, and the
    /// queue is held across the pass and the reads of its per-pass state.
    @Test func theEndpointRoutesWhisperThroughTheQueue() throws {
        let handle = try ProductionSource.slice(
            of: Self.endpointSource,
            from: "let whisper = resolution.provider as? LibWhisperProvider",
            to: "let response = TranscribeResponse("
        )
        #expect(handle.contains("whisper.transcribeQueuedForLocalAPI("))
        #expect(handle.contains("model: resolution.whisperModel,"))
        #expect(handle.contains("detectedLanguage = pass.detectedLanguage"))
        #expect(handle.contains("whisperTimestamps = pass.timestamps"))

        let queued = try ProductionSource.slice(
            of: Self.providerSource,
            from: "func transcribeQueuedForLocalAPI(",
            to: "private func performTranscribe("
        )
        #expect(queued.contains("localAPIQueue.withLock"))
        #expect(queued.contains("passGate.beginWhenIdle()"))
        #expect(queued.contains("requiredModel: model"))
    }

    /// No Local API resolution stages a Whisper model on the shared provider:
    /// a request resolving while another pass runs must not change its model.
    @Test func localAPIResolutionNeverStagesAWhisperModel() throws {
        let endpoint = try ProductionSource.code(of: Self.endpointSource)
        #expect(!endpoint.contains("setModel("))
        let selectCalls = endpoint.components(separatedBy: "router.selectProvider(for:").count - 1
        let unstaged = endpoint.components(separatedBy: "stageWhisperModel: false)").count - 1
        #expect(selectCalls == 2)
        #expect(unstaged == selectCalls)

        let resolve = try ProductionSource.slice(
            of: Self.routerSource,
            from: "func resolveProvider(engine: String, model: String?, language: String?)",
            to: "private func selectLocalProvider("
        )
        #expect(resolve.contains("stageWhisperModel: false)"))
    }

    /// An API pass that swaps the resident model does not leave the next
    /// dictation on it: `setModel` records the app's choice, and an in-app pass
    /// loads it back when the resident model differs.
    @Test func anInAppPassRestoresTheAppsModelAfterAnAPIPass() throws {
        let setModel = try ProductionSource.slice(
            of: Self.providerSource,
            from: "func setModel(_ model: WhisperModel) {",
            to: "if currentModel?.name == model.rawValue"
        )
        #expect(setModel.contains("inAppModel = model"))

        let pass = try ProductionSource.slice(
            of: Self.providerSource,
            from: "if let requiredModel {",
            to: "let acquisition: ResidentRuntimeClaim.Acquisition"
        )
        #expect(pass.contains("resident != inAppModel.rawValue"))
        #expect(pass.contains("try await loadModel(named: inAppModel.rawValue)"))
    }

    // MARK: - The error

    /// A cancelled call keeps the closed-enum code, with a message that names
    /// the cause instead of "Streaming interrupted; partial text shown".
    @Test func aCancelledCallSaysWhy() throws {
        let cancelled = TranscribeEndpoint.whisperPassCancelled
        #expect(cancelled.code == .transcriptionFailed)
        #expect(cancelled.message == "Cancelled by a newer transcription request.")
        #expect(!cancelled.message.localizedCaseInsensitiveContains("stream"))

        let handle = try ProductionSource.slice(
            of: Self.endpointSource,
            from: "let whisper = resolution.provider as? LibWhisperProvider",
            to: "let latencyMs ="
        )
        #expect(handle.contains("case .streamingInterrupted = te"))
        #expect(handle.contains("Self.whisperPassCancelled"))
    }
}
