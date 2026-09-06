//
//  StreamingTurnBoundaryTests.swift
//  hyperwhisperTests
//
//  The turn-boundary rule, pinned where it now lives: in
//  StreamingTranscriptionClient, not in a provider strategy.
//
//  Gemini's live socket emits `serverContent.generationComplete` every time it
//  finishes generating for an utterance, so a two-sentence dictation sees one
//  mid-stream with more audio still to come. Read as terminal it releases the
//  stop sequence's `waitForSessionComplete` at the first pause and the LAST
//  utterance's final never arrives — the user says two sentences and one lands.
//
//  macOS used to enforce that inside `GeminiStreamingStrategy`, with a private
//  `clientRequestedStop` flag the strategy set from `stopSequence()`. The shared
//  core deliberately does not model it (`hw-net/src/live/gemini.rs`,
//  `complete_ends_session_before_stop`): it reports the frame faithfully and
//  pushes the decision to the head, because "has the user let go of the key
//  yet?" is the client's state and only the client's. Windows already keys on
//  exactly this (`StreamingTranscriptionClient.cs:648-678`), as does the backend
//  proxy's `complete` arm. These cases are the macOS half of that contract, and
//  they inherit the three assertions that used to pin the strategy's flag.
//
//  No socket, no key, no sleep: the client's event handler is driven directly
//  with a stub strategy.
//

import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Stub Strategy

/// A strategy with no wire protocol at all: it maps sentinel strings onto
/// normalized events so a test can hand the client any frame sequence it likes.
///
/// `buildWebSocketURL` answers nil on purpose. That makes `startSession` throw
/// `StreamingError.invalidURL` at its STEP 1 guard, which is *after* the block
/// that resets per-session state — so a test can re-arm the client for a second
/// session without a socket, a URLSession or an audio engine.
private final class TurnBoundaryStubStrategy: StreamingProviderStrategy {

    /// Sentinel frames. The stub does no parsing; the client does not care.
    static let partialFrame = "partial"
    static let finalFrame = "final"
    static let completeFrame = "complete"
    static let finalAndCompleteFrame = "final+complete"

    let completeEndsSessionBeforeStop: Bool

    init(completeEndsSessionBeforeStop: Bool) {
        self.completeEndsSessionBeforeStop = completeEndsSessionBeforeStop
    }

    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? { nil }

    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message { .data(pcmData) }

    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        switch text {
        case Self.partialFrame:
            return .partialTranscript(text: "second")
        case Self.finalFrame:
            return .finalTranscript(text: "first utterance.")
        case Self.completeFrame:
            return .sessionComplete(durationSeconds: 4, creditsUsed: 0)
        case Self.finalAndCompleteFrame:
            return .finalTranscriptAndSessionComplete(
                text: "tail.",
                durationSeconds: 4,
                creditsUsed: 0
            )
        default:
            return nil
        }
    }

    /// Empty on purpose: `stopSession()` must be reachable from a test without
    /// a socket to send a stop message on or a completion to wait for. The stop
    /// sequences themselves are pinned in `RustLiveStreamingStrategyTests`.
    func stopSequence() -> [StreamingStopStep] { [] }

    var transcriptionProviderLabel: String { "Stub (Streaming)" }
}

/// A strategy that overrides nothing beyond the protocol's required members, to
/// prove the default implementation is the one the five non-Gemini providers get
/// for free. This is what keeps the six shipped strategies compiling untouched.
private struct DefaultingStubStrategy: StreamingProviderStrategy {
    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? { nil }
    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message { .data(pcmData) }
    func parseMessage(_ text: String) -> StreamingProviderEvent? { nil }
    func stopSequence() -> [StreamingStopStep] { [] }
    var transcriptionProviderLabel: String { "Defaulting (Streaming)" }
}

// MARK: - Recorder

/// Records what the client told its owner.
///
/// `completions` is the assertion that matters: firing `onSessionComplete` and
/// latching the private `didReceiveSessionComplete` — the flag the stop
/// sequence's `waitForSessionComplete` polls — happen in the same block, so a
/// completion the owner never saw is a completion the stop wait never saw
/// either.
private final class TurnBoundaryRecorder {
    struct Completion: Equatable {
        let durationSeconds: Double
        let creditsUsed: Double
    }

    var finals: [String] = []
    var partials: [String] = []
    var completions: [Completion] = []
}

// MARK: - Tests

@MainActor
@Suite("Streaming turn boundaries")
struct StreamingTurnBoundaryTests {

    private func config() -> StreamingSessionConfig {
        StreamingSessionConfig(
            licenseKey: nil,
            deviceId: nil,
            language: "en",
            vocabulary: nil,
            apiKey: "test-key",
            model: nil,
            fastFormatting: false
        )
    }

    private func makeClient(
        completeEndsSessionBeforeStop: Bool
    ) -> (StreamingTranscriptionClient, TurnBoundaryRecorder) {
        let strategy = TurnBoundaryStubStrategy(
            completeEndsSessionBeforeStop: completeEndsSessionBeforeStop
        )
        let client = StreamingTranscriptionClient(strategy: strategy)
        let recorder = TurnBoundaryRecorder()
        client.onTranscriptUpdate = { text, isFinal in
            if isFinal { recorder.finals.append(text) } else { recorder.partials.append(text) }
        }
        client.onSessionComplete = { duration, credits in
            recorder.completions.append(
                TurnBoundaryRecorder.Completion(durationSeconds: duration, creditsUsed: credits)
            )
        }
        return (client, recorder)
    }

    /// Inherited from `GeminiStreamingStrategyTests`' two-utterance walk-through.
    /// The combined-frame half of that case is
    /// `combinedFinalAndCompleteBeforeStopCommitsTextOnly`, below.
    @Test("A completion before stop is a turn boundary and the session continues")
    func aCompleteBeforeStopDoesNotEndTheSession() async {
        let (client, recorder) = makeClient(completeEndsSessionBeforeStop: false)

        await client.processServerMessage(TurnBoundaryStubStrategy.finalFrame)
        #expect(recorder.finals == ["first utterance."])

        // The turn boundary BETWEEN the two utterances. It must report nothing:
        // the turn's text already reached the document on the frame above, and
        // reporting a completion here ends the session one utterance early.
        await client.processServerMessage(TurnBoundaryStubStrategy.completeFrame)
        #expect(
            recorder.completions.isEmpty,
            "a complete arriving before the client asked to stop is a turn boundary, not the end"
        )

        // The session must still be live enough to preview and commit the next
        // turn — the half of the bug the user actually notices.
        await client.processServerMessage(TurnBoundaryStubStrategy.partialFrame)
        #expect(recorder.partials == ["second"])
        await client.processServerMessage(TurnBoundaryStubStrategy.finalFrame)
        #expect(recorder.finals.count == 2)
        #expect(recorder.completions.isEmpty)
    }

    /// Inherited from `GeminiStreamingStrategyTests.postStopCombinedFrameReportsTextAndCompletion`.
    @Test("A completion after stop ends the session")
    func aCompleteAfterStopEndsTheSession() async {
        let (client, recorder) = makeClient(completeEndsSessionBeforeStop: false)

        await client.processServerMessage(TurnBoundaryStubStrategy.finalFrame)
        #expect(recorder.completions.isEmpty)

        // The user releases the key. From here a completion is the real thing —
        // and it has to be, or `waitForSessionComplete` burns its whole budget
        // on an answer the provider already gave.
        await client.stopSession()

        await client.processServerMessage(TurnBoundaryStubStrategy.completeFrame)
        #expect(
            recorder.completions == [TurnBoundaryRecorder.Completion(durationSeconds: 4, creditsUsed: 0)],
            "the stop wait is released by this event and by nothing else"
        )
    }

    /// Inherited from `GeminiStreamingStrategyTests.startMessagesClearsTheStopFlagFromAPreviousSession`.
    /// The flag moved from the strategy to the client, so the thing that re-arms
    /// it moved from `startMessages` to `startSession`.
    @Test("A fresh session re-arms the turn-boundary rule")
    func aFreshSessionReArmsTheTurnBoundaryRule() async {
        let (client, recorder) = makeClient(completeEndsSessionBeforeStop: false)

        await client.stopSession()
        await client.processServerMessage(TurnBoundaryStubStrategy.completeFrame)
        #expect(recorder.completions.count == 1, "precondition: a post-stop complete ends the first session")

        // A client that survived one stop must not treat the NEXT session's
        // first turn boundary as terminal. The stub refuses to build a URL, so
        // this start throws at STEP 1 — after the reset block that clears the
        // flag, which is the ordering being pinned.
        do {
            try await client.startSession(config: config())
            Issue.record("the stub builds no URL, so the start must fail before any socket exists")
        } catch {
            #expect(error is StreamingError)
        }

        await client.processServerMessage(TurnBoundaryStubStrategy.completeFrame)
        #expect(
            recorder.completions.count == 1,
            "the new session's first turn boundary must not be terminal"
        )
    }

    /// The no-regression half. Five of the six remote providers emit a
    /// completion once, at the end, and must keep the unconditional behaviour
    /// this arm has always had.
    @Test("A provider whose completion is terminal still completes before stop")
    func aTerminalCompleteStillEndsTheSessionBeforeStop() async {
        let (client, recorder) = makeClient(completeEndsSessionBeforeStop: true)

        await client.processServerMessage(TurnBoundaryStubStrategy.completeFrame)
        #expect(recorder.completions.count == 1)
    }

    /// Inherited from `GeminiStreamingStrategyTests.preStopCombinedFrameOnlyCommitsText`,
    /// which is the case this suite originally replaced with a weaker one.
    ///
    /// Gemini answers `audio_stream_end` with ONE `serverContent` carrying the
    /// last committed segment and `generationComplete` together, and the core
    /// now reports both halves. The completion half is the same boundary as a
    /// standalone completion and answers to the same gate; the TEXT half is
    /// committed either way, because a turn's committed segment belongs in the
    /// document whether or not the turn was the last one.
    @Test("A combined final-and-complete frame before stop commits its text only")
    func combinedFinalAndCompleteBeforeStopCommitsTextOnly() async {
        let (client, recorder) = makeClient(completeEndsSessionBeforeStop: false)

        await client.processServerMessage(TurnBoundaryStubStrategy.finalAndCompleteFrame)
        #expect(recorder.finals == ["tail."], "the committed segment is never dropped")
        #expect(
            recorder.completions.isEmpty,
            "a completion riding on a final is still a turn boundary before the user asks to stop"
        )

        // And the session is still live for the next utterance.
        await client.processServerMessage(TurnBoundaryStubStrategy.partialFrame)
        #expect(recorder.partials == ["second"])
    }

    /// Inherited from `GeminiStreamingStrategyTests.postStopCombinedFrameReportsTextAndCompletion`.
    ///
    /// THE REGRESSION THIS PINS: with the completion dropped, Gemini's
    /// `.waitForSessionComplete(timeout: 5.0)` sat out its whole budget on every
    /// ordinary dictation and the client then reported a
    /// `wait_for_session_complete` stop failure to Sentry.
    @Test("A combined final-and-complete frame after stop ends the session")
    func combinedFinalAndCompleteAfterStopEndsTheSession() async {
        let (client, recorder) = makeClient(completeEndsSessionBeforeStop: false)

        await client.stopSession()
        await client.processServerMessage(TurnBoundaryStubStrategy.finalAndCompleteFrame)

        #expect(recorder.finals == ["tail."])
        #expect(
            recorder.completions == [TurnBoundaryRecorder.Completion(durationSeconds: 4, creditsUsed: 0)],
            "this frame is the only completion the stop wait will ever be sent"
        )
    }

    /// The no-regression half for the five providers that answer `true`: their
    /// combined frame is never gated, which is what xAI's `.done` event relies
    /// on.
    @Test("A combined final-and-complete frame from a terminal-completion provider is never gated")
    func combinedFinalAndCompleteIsNotGatedForTerminalProviders() async {
        let (client, recorder) = makeClient(completeEndsSessionBeforeStop: true)

        await client.processServerMessage(TurnBoundaryStubStrategy.finalAndCompleteFrame)
        #expect(recorder.finals == ["tail."])
        #expect(recorder.completions.count == 1)
    }

    /// The default implementation is what lets every strategy that predates this
    /// bit compile untouched, and `true` is the behaviour they all had.
    @Test("The protocol default is the old unconditional behaviour")
    func theProtocolDefaultIsTrue() {
        #expect(DefaultingStubStrategy().completeEndsSessionBeforeStop)
    }

    /// And the adapter answers from the shared capability table rather than from
    /// a second list on this head, so macOS cannot drift from Windows and Linux.
    @Test("The Rust adapter answers false for Gemini alone")
    func theAdapterReadsTheSharedCapabilityTable() {
        #expect(!RustLiveStreamingStrategy(provider: .gemini).completeEndsSessionBeforeStop)

        let terminal: [StreamingTranscriptionProvider] = [
            .hyperwhisperCloud, .deepgram, .elevenLabs, .openAI, .xai
        ]
        for provider in terminal {
            #expect(
                RustLiveStreamingStrategy(provider: provider).completeEndsSessionBeforeStop,
                "\(provider.rawValue) emits its completion once, at the end of the session"
            )
        }
    }
}
