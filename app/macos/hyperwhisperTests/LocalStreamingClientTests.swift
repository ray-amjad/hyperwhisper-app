//
//  LocalStreamingClientTests.swift
//  hyperwhisperTests
//
//  Issue #807: `LocalParakeetStreamingClient` and `LocalNemotronStreamingClient`
//  are one shared `LocalStreamingClient` now. These tests drive it through the
//  engine/session/capture seams with fakes, so no model and no microphone are
//  needed, and pin:
//  - a non-empty final transcript reaches `onTranscriptUpdate(_, true)`
//    exactly once per `stopSession`, for both engines' final-text delivery;
//  - the early-cancel unwind (cancelled between `setCallbacks` and `start`)
//    calls `session.cancel()` and never starts the recognizer.
//

import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Fakes

private actor FakeLocalStreamingSession: LocalStreamingSession {
    enum Finish {
        /// Nemotron's shape: no callback, the final text is the return value.
        case returnOnly(String)
        /// Parakeet's shape: the tail goes out through onConfirmedDelta and
        /// the return value is the whole accumulated transcript.
        case emitTailThenReturnFull(tail: String, full: String)
    }

    private let finishBehaviour: Finish
    private let cancelTaskInSetCallbacks: Bool
    private var onConfirmedDelta: (@Sendable (String) -> Void)?

    private(set) var startCount = 0
    private(set) var finishCount = 0
    private(set) var cancelCount = 0

    init(finish: Finish, cancelTaskInSetCallbacks: Bool = false) {
        self.finishBehaviour = finish
        self.cancelTaskInSetCallbacks = cancelTaskInSetCallbacks
    }

    func setCallbacks(
        onConfirmedDelta: (@Sendable (String) -> Void)?,
        onVolatile: (@Sendable (String) -> Void)?,
        onError: (@Sendable (Error) -> Void)?
    ) {
        self.onConfirmedDelta = onConfirmedDelta
        if cancelTaskInSetCallbacks {
            // The caller's task (startSession) is the current task here, so
            // this lands the cancel between setCallbacks and session.start().
            withUnsafeCurrentTask { $0?.cancel() }
        }
    }

    func start() async throws { startCount += 1 }

    nonisolated func feed(_ samples: [Float]) {}

    func finish() async throws -> String {
        finishCount += 1
        switch finishBehaviour {
        case .returnOnly(let text):
            return text
        case .emitTailThenReturnFull(let tail, let full):
            onConfirmedDelta?(tail)
            return full
        }
    }

    func cancel() async {
        cancelCount += 1
        onConfirmedDelta = nil
    }

    func elapsedSeconds() -> Double { 2.5 }
}

private final class FakeAudioCapture: StreamingAudioCapturing {
    var onFloat32Samples: (([Float]) -> Void)?
    var onAudioLevel: ((Float) -> Void)?
    var startCount = 0
    var stopCount = 0
    func start() async throws { startCount += 1 }
    func stop() { stopCount += 1 }
}

@MainActor
private protocol FakeEngine: LocalStreamingEngine where Models == Void, Session == FakeLocalStreamingSession {
    var session: FakeLocalStreamingSession { get }
}

extension FakeEngine {
    var providerLabel: String { "Fake (On-Device Streaming)" }
    var logCategory: String { "LocalStreamingClientTests" }
    var logName: String { "Fake" }
    var logVariant: String { "test" }
    var modelsLogName: String { "models" }
    func checkModelsPresent() -> LocalStreamingModelCheck { .present(logDetail: "fake models present") }
    func loadModels() async throws {}
    func makeSession(models: Void) async throws -> FakeLocalStreamingSession { session }
}

/// Delivers like `LocalNemotronStreamingEngine`.
@MainActor
private struct ReturnValueEngine: FakeEngine {
    static var finalTextDelivery: LocalStreamingFinalTextDelivery { .finishReturnValue }
    let session: FakeLocalStreamingSession
}

/// Delivers like `LocalParakeetStreamingEngine`.
@MainActor
private struct CallbackEngine: FakeEngine {
    static var finalTextDelivery: LocalStreamingFinalTextDelivery { .confirmedDeltaCallback }
    let session: FakeLocalStreamingSession
}

/// Every client callback, in arrival order.
@MainActor
private final class ClientEvents {
    private(set) var log: [String] = []

    var finals: [String] {
        log.compactMap { $0.hasPrefix("final:") ? String($0.dropFirst("final:".count)) : nil }
    }

    func attach<E: LocalStreamingEngine>(to client: LocalStreamingClient<E>) {
        client.onTranscriptUpdate = { [unowned self] text, isFinal in
            self.log.append(isFinal ? "final:\(text)" : "volatile:\(text)")
        }
        client.onConnectionStateChange = { [unowned self] state in
            self.log.append("state:\(state)")
        }
        client.onSessionComplete = { [unowned self] _, _ in
            self.log.append("complete")
        }
        client.onError = { [unowned self] error in
            self.log.append("error:\(error)")
        }
    }
}

// MARK: - Tests

@MainActor
@Suite("Local streaming client (#807)")
struct LocalStreamingClientTests {

    private let config = StreamingSessionConfig(
        licenseKey: nil,
        deviceId: nil,
        language: "en",
        vocabulary: nil,
        apiKey: nil,
        model: nil,
        fastFormatting: false
    )

    /// Lets main-actor hops spawned by the session callbacks run, so a late or
    /// duplicate delivery would be seen before the assertions.
    private func drainMainActor() async throws {
        for _ in 0..<20 { await Task.yield() }
        try await Task.sleep(nanoseconds: 50_000_000)
        for _ in 0..<20 { await Task.yield() }
    }

    @Test func realEnginesDeclareTheirFinalTextDelivery() {
        #expect(LocalNemotronStreamingEngine.finalTextDelivery == .finishReturnValue)
        #expect(LocalParakeetStreamingEngine.finalTextDelivery == .confirmedDeltaCallback)
    }

    /// Nemotron: `finish()` returns the final text; the client types it once,
    /// before `.idle` and `onSessionComplete`.
    @Test func returnValueEngineDeliversFinalOncePerStop() async throws {
        let session = FakeLocalStreamingSession(finish: .returnOnly("hello world."))
        let capture = FakeAudioCapture()
        let client = LocalStreamingClient(engine: ReturnValueEngine(session: session), makeAudioCapture: { capture })
        let events = ClientEvents()
        events.attach(to: client)

        try await client.startSession(config: config)
        #expect(await session.startCount == 1)
        #expect(capture.startCount == 1)

        await client.stopSession()
        await client.stopSession() // second stop is a no-op
        try await drainMainActor()

        #expect(events.finals == ["hello world."])
        #expect(await session.finishCount == 1)
        #expect(await session.cancelCount == 0)
        #expect(capture.stopCount == 1)
        let finalIndex = try #require(events.log.firstIndex(of: "final:hello world."))
        let idleIndex = try #require(events.log.lastIndex(of: "state:idle"))
        let completeIndex = try #require(events.log.firstIndex(of: "complete"))
        #expect(finalIndex < idleIndex)
        #expect(idleIndex < completeIndex)
        #expect(events.log.filter { $0 == "complete" }.count == 1)
    }

    /// Parakeet: `finish()` emits the tail through onConfirmedDelta and returns
    /// the whole transcript; the client types the tail once and does NOT type
    /// the returned whole transcript again.
    @Test func callbackEngineDeliversTailOncePerStopAndDropsReturnValue() async throws {
        let session = FakeLocalStreamingSession(
            finish: .emitTailThenReturnFull(tail: "last words.", full: "first words. last words.")
        )
        let capture = FakeAudioCapture()
        let client = LocalStreamingClient(engine: CallbackEngine(session: session), makeAudioCapture: { capture })
        let events = ClientEvents()
        events.attach(to: client)

        try await client.startSession(config: config)
        await client.stopSession()
        await client.stopSession() // second stop is a no-op
        try await drainMainActor()

        #expect(events.finals == ["last words."])
        #expect(await session.finishCount == 1)
        #expect(await session.cancelCount == 0)
        #expect(events.log.filter { $0 == "complete" }.count == 1)
    }

    /// An empty final text types nothing, for the return-value engine.
    @Test func emptyFinalTextIsNotDelivered() async throws {
        let session = FakeLocalStreamingSession(finish: .returnOnly(""))
        let client = LocalStreamingClient(engine: ReturnValueEngine(session: session), makeAudioCapture: { FakeAudioCapture() })
        let events = ClientEvents()
        events.attach(to: client)

        try await client.startSession(config: config)
        await client.stopSession()
        try await drainMainActor()

        #expect(events.finals.isEmpty)
        #expect(events.log.filter { $0 == "complete" }.count == 1)
    }

    /// Cancelled between setCallbacks and session.start(): the session is
    /// cancelled (drift site 1, now the same for both engines), the recognizer
    /// and the capture never start, and the client collapses to idle.
    private func expectEarlyCancelCancelsTheSession<E: FakeEngine>(
        _ makeEngine: (FakeLocalStreamingSession) -> E
    ) async throws {
        let session = FakeLocalStreamingSession(finish: .returnOnly("never"), cancelTaskInSetCallbacks: true)
        let capture = FakeAudioCapture()
        let client = LocalStreamingClient(engine: makeEngine(session), makeAudioCapture: { capture })
        let events = ClientEvents()
        events.attach(to: client)
        let config = self.config

        let result = await Task { @MainActor in try await client.startSession(config: config) }.result
        #expect(throws: CancellationError.self) { try result.get() }

        #expect(await session.cancelCount == 1)
        #expect(await session.startCount == 0)
        #expect(await session.finishCount == 0)
        #expect(capture.startCount == 0)
        #expect(events.log == ["state:warmingUp", "state:idle"])
    }

    @Test func earlyCancelCancelsTheSessionForReturnValueEngine() async throws {
        try await expectEarlyCancelCancelsTheSession { ReturnValueEngine(session: $0) }
    }

    @Test func earlyCancelCancelsTheSessionForCallbackEngine() async throws {
        try await expectEarlyCancelCancelsTheSession { CallbackEngine(session: $0) }
    }
}
