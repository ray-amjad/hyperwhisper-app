//
//  StreamingSessionDiagnostics.swift
//  hyperwhisper
//
//  SESSION TELEMETRY FOR THE STREAMING CLIENT
//  The counters, timestamps and payload builder behind every Sentry capture
//  `StreamingTranscriptionClient` makes. Pure bookkeeping: it never touches the
//  socket, the strategy or the session state machine. The client owns one
//  instance, resets it per session, and reads it back through
//  `snapshot(now:client:)`.
//

import Foundation
import os

// MARK: - Session Diagnostics
//
// Every streaming fault the client reports used to arrive as a provider
// sentence and a stack trace, with nothing about what the client had
// actually done by then. "Error committing input audio buffer: buffer too
// small. Expected at least 100ms of audio, but buffer only has 85.00ms"
// (HYPERWHISPER-S8/-S9) is the shape of that: it names the server's
// complaint and says nothing about how long the microphone had been open,
// whether the stop flush was already running, or whether the user had been
// given any transcript at all. The fields below are what answers that, and
// they ride on every capture the client makes plus, through the Sentry scope,
// on the mirror the recording flow raises for the same fault.
//
// All of them are measurements, fixed slugs, or identifiers of code and of
// a provider request. No transcript, audio, prompt or typed text is
// recorded here, and nothing derived from one.
struct StreamingSessionDiagnostics {

    /// Same subsystem and category as the client's own logger, so the
    /// post-stop debug lines land in the same unified-log stream as before.
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "StreamingTranscription")

    /// When `startSession()` began, for `streaming_session_elapsed_ms`.
    var sessionStartedAt = Date()

    /// When the microphone actually started delivering audio, or nil before
    /// `capture.start()` returns. The elapsed time from here is how much audio
    /// the client had sent when a provider complained about how little it had.
    var audioStartedAt: Date?

    /// How many committed and interim transcript events the provider has
    /// delivered this session.
    ///
    /// COUNTS ONLY, AND NAMED SO THEY SURVIVE. `SentryService.beforeSend`
    /// redacts any non-Bool, non-number extra whose KEY contains `transcript`,
    /// `text` or `prompt` without warning the call site (#684 lets a number
    /// through); a type-blind rule is how three Windows fields shipped
    /// `[redacted]` for months (HYPERWHISPER-PA), so keep the names clean.
    var finalsDelivered = 0
    var partialsDelivered = 0

    /// How many audio-chunk sends failed this session, and whether the first
    /// one has been reported. See the client's
    /// `noteAudioSendFailure(generation:domain:code:)`.
    var audioSendFailureCount = 0
    var didReportAudioSendFailure = false

    /// The stage the session has reached, as a stable slug.
    ///
    /// This is the breadcrumb trail the client appeared to have and did not:
    /// `beforeSend` sets `event.breadcrumbs = nil`, so the "Reconnect attempt"
    /// and "Reconnect success" crumbs in the client never leave the machine.
    /// Scope extras do, so the path taken is published there instead.
    var stage = "idle"

    /// Session-scoped receive activity. A cancelled loop from an old session
    /// can resume after `startSession()` resets the diagnostics, so every
    /// mutation checks the generation that created the loop.
    struct ReceiveActivityCounters {
        private(set) var generation = 0
        private(set) var active = 0
        private(set) var pending = 0

        mutating func reset(generation: Int) {
            self.generation = generation
            active = 0
            pending = 0
        }

        mutating func startLoop(generation: Int) {
            guard generation == self.generation else { return }
            active += 1
        }

        mutating func finishLoop(generation: Int) {
            guard generation == self.generation else { return }
            active = max(0, active - 1)
        }

        mutating func startReceive(generation: Int) {
            guard generation == self.generation else { return }
            pending += 1
        }

        mutating func finishReceive(generation: Int) {
            guard generation == self.generation else { return }
            pending = max(0, pending - 1)
        }
    }

    /// Transport and parser measurements collected after a stop frame send
    /// starts. The separate success timestamp keeps a failed send distinguishable
    /// while the earlier boundary includes a provider response that arrives
    /// before `send` resumes.
    var stopFrameSendStartedAt: Date?
    /// These fields contain only counts, byte sizes, booleans, durations and
    /// fixed event slugs. They never retain a WebSocket frame or parsed payload.
    var stopFrameSentAt: Date?
    var receiveActivity = ReceiveActivityCounters()
    var receiveLoopWasActiveAtStop = false
    var receiveWasPendingAtStop = false
    var postStopReceiveAttempts = 0
    var postStopReceiveCompletions = 0
    var postStopReceiveFailures = 0
    var postStopStringFrameCount = 0
    var postStopStringByteCount = 0
    var postStopBinaryFrameCount = 0
    var postStopBinaryByteCount = 0
    var postStopUTF8DecodeFailures = 0
    var postStopParsedEventCounts: [String: Int] = [:]

    // MARK: - Reset

    /// Reset the diagnostics with the rest of the session state, so a report
    /// can never carry a count or an elapsed time belonging to the previous
    /// recording. `stage` is left alone: the client's `noteStage("starting")`
    /// overwrites it straight after, and republishes the whole set to the
    /// Sentry scope, which is what clears the previous session's values there.
    ///
    /// - Parameter generation: the client's freshly bumped `sessionGeneration`,
    ///   which the receive-activity counters compare every mutation against.
    mutating func resetForNewSession(startedAt: Date, generation: Int) {
        sessionStartedAt = startedAt
        audioStartedAt = nil
        finalsDelivered = 0
        partialsDelivered = 0
        audioSendFailureCount = 0
        didReportAudioSendFailure = false
        stopFrameSendStartedAt = nil
        stopFrameSentAt = nil
        receiveLoopWasActiveAtStop = false
        receiveWasPendingAtStop = false
        postStopReceiveAttempts = 0
        postStopReceiveCompletions = 0
        postStopReceiveFailures = 0
        postStopStringFrameCount = 0
        postStopStringByteCount = 0
        postStopBinaryFrameCount = 0
        postStopBinaryByteCount = 0
        postStopUTF8DecodeFailures = 0
        postStopParsedEventCounts.removeAll(keepingCapacity: true)
        receiveActivity.reset(generation: generation)
    }

    // MARK: - Snapshot

    /// The client-owned values the payload reports but this struct does not
    /// own. Each of them is session or socket state the client's control flow
    /// reads (`reconnectCount`, `didInitiateClose`, ...), so it stays on the
    /// client and is passed in at snapshot time rather than duplicated here.
    struct ClientState {
        let providerLabel: String
        let model: String
        let connectionEstablishedAt: Date
        let reconnectCount: Int
        let closeCode: Int
        let sessionCompleteReceived: Bool
        let didInitiateClose: Bool
    }

    /// Whole milliseconds between two instants, for a log field.
    static func elapsedMs(since start: Date, to end: Date = Date()) -> Int {
        Int((end.timeIntervalSince(start) * 1000).rounded())
    }

    /// The shape of the current streaming session, as metadata only.
    ///
    /// Attached to every capture the client makes, so a provider complaint, a
    /// disconnect and a failed flush all answer the same questions: how long had
    /// the session been up, how long had the microphone been open, had anything
    /// been delivered to the user yet, which connection generation is this, and
    /// what had already gone wrong quietly.
    ///
    /// PRIVACY: provider label, model name and stage are constants of the code
    /// or of the model catalog; every other value is a count, a duration in ms,
    /// a boolean or a close code. Nothing here is derived from what the user
    /// said, typed or pasted.
    ///
    /// THE KEYS ARE A CONTRACT. Sentry and Axiom queries read these `streaming_*`
    /// names; renaming one silently breaks every saved query that uses it.
    ///
    /// - Parameter now: the instant every "elapsed until now" field is measured
    ///   to. Production passes `Date()`.
    func snapshot(now: Date, client: ClientState) -> [String: Any] {
        return [
            "streaming_provider": client.providerLabel,
            "streaming_model": client.model,
            "streaming_stage": stage,
            "streaming_session_elapsed_ms": Self.elapsedMs(since: sessionStartedAt, to: now),
            "streaming_connection_elapsed_ms": Self.elapsedMs(since: client.connectionEstablishedAt, to: now),
            "streaming_audio_elapsed_ms": audioStartedAt.map { Self.elapsedMs(since: $0, to: now) } ?? 0,
            "streaming_finals_delivered": finalsDelivered,
            "streaming_partials_delivered": partialsDelivered,
            "streaming_reconnect_count": client.reconnectCount,
            "streaming_audio_send_failures": audioSendFailureCount,
            "streaming_close_code": client.closeCode,
            "streaming_session_complete_received": client.sessionCompleteReceived,
            "streaming_did_initiate_close": client.didInitiateClose,
            "streaming_stop_frame_send_started": stopFrameSendStartedAt != nil,
            "streaming_stop_frame_send_started_elapsed_ms": stopFrameSendStartedAt.map {
                Self.elapsedMs(since: sessionStartedAt, to: $0)
            } ?? 0,
            "streaming_stop_frame_sent": stopFrameSentAt != nil,
            "streaming_stop_frame_sent_elapsed_ms": stopFrameSentAt.map {
                Self.elapsedMs(since: sessionStartedAt, to: $0)
            } ?? 0,
            "streaming_post_stop_elapsed_ms": stopFrameSendStartedAt.map {
                Self.elapsedMs(since: $0, to: now)
            } ?? 0,
            "streaming_receive_loop_active": receiveActivity.active > 0,
            "streaming_receive_pending": receiveActivity.pending > 0,
            "streaming_receive_loop_active_at_stop": receiveLoopWasActiveAtStop,
            "streaming_receive_pending_at_stop": receiveWasPendingAtStop,
            "streaming_post_stop_receive_attempts": postStopReceiveAttempts,
            "streaming_post_stop_receive_completions": postStopReceiveCompletions,
            "streaming_post_stop_receive_failures": postStopReceiveFailures,
            "streaming_post_stop_string_frames": postStopStringFrameCount,
            "streaming_post_stop_string_bytes": postStopStringByteCount,
            "streaming_post_stop_binary_frames": postStopBinaryFrameCount,
            "streaming_post_stop_binary_bytes": postStopBinaryByteCount,
            "streaming_post_stop_utf8_decode_failures": postStopUTF8DecodeFailures,
            "streaming_post_stop_event_session_started": postStopParsedEventCounts["session_started"] ?? 0,
            "streaming_post_stop_event_partial": postStopParsedEventCounts["partial"] ?? 0,
            "streaming_post_stop_event_final": postStopParsedEventCounts["final"] ?? 0,
            "streaming_post_stop_event_final_and_complete": postStopParsedEventCounts["final_and_complete"] ?? 0,
            "streaming_post_stop_event_session_complete": postStopParsedEventCounts["session_complete"] ?? 0,
            "streaming_post_stop_event_error": postStopParsedEventCounts["error"] ?? 0,
            "streaming_post_stop_event_warning": postStopParsedEventCounts["warning"] ?? 0,
            "streaming_post_stop_event_metadata": postStopParsedEventCounts["metadata"] ?? 0,
            "streaming_post_stop_event_ignored": postStopParsedEventCounts["ignored"] ?? 0
        ]
    }

    // MARK: - Post-Stop Bookkeeping
    //
    // `os.Logger` interpolations are escaping autoclosures, and an escaping
    // closure cannot capture `self` inside a `mutating` method of a struct.
    // So every logged field below is copied into a local first; the logged
    // values are exactly the ones the class version logged.

    /// Mark the first stop-frame send before it yields to the transport.
    mutating func noteStopFrameSendStarted(now: Date = Date()) {
        guard stopFrameSendStartedAt == nil else { return }
        stopFrameSendStartedAt = now
        receiveLoopWasActiveAtStop = receiveActivity.active > 0
        receiveWasPendingAtStop = receiveActivity.pending > 0
        let receiveLoopActive = receiveLoopWasActiveAtStop
        let receivePending = receiveWasPendingAtStop
        logger.debug(
            "Stop frame send started: receive_loop_active=\(receiveLoopActive, privacy: .public) receive_pending=\(receivePending, privacy: .public)"
        )
    }

    /// Mark the first successful stop-frame send without moving the boundary.
    mutating func noteStopFrameSent(now: Date = Date()) {
        guard stopFrameSentAt == nil else { return }
        stopFrameSentAt = now
        logger.debug("Stop frame sent")
    }

    /// Record a post-stop wire frame without retaining its contents.
    mutating func notePostStopWireFrame(kind: String, byteCount: Int) {
        guard stopFrameSendStartedAt != nil else { return }

        switch kind {
        case "string":
            postStopStringFrameCount += 1
            postStopStringByteCount += byteCount
        case "binary":
            postStopBinaryFrameCount += 1
            postStopBinaryByteCount += byteCount
        default:
            break
        }

        let sequence = postStopStringFrameCount + postStopBinaryFrameCount
        logger.debug(
            "Post-stop frame received: kind=\(kind, privacy: .public) sequence=\(sequence, privacy: .public) bytes=\(byteCount, privacy: .public)"
        )
    }

    /// Map one parser result to a content-free event shape.
    static func normalizedEventSlug(for event: StreamingProviderEvent?) -> String {
        guard let event else { return "ignored" }
        switch event {
        case .sessionStarted: return "session_started"
        case .partialTranscript: return "partial"
        case .finalTranscript: return "final"
        case .finalTranscriptAndSessionComplete: return "final_and_complete"
        case .sessionComplete: return "session_complete"
        case .error: return "error"
        case .warning: return "warning"
        case .metadata: return "metadata"
        }
    }

    /// Record a fixed parser-result slug after the stop boundary.
    mutating func notePostStopParsedEventSlug(_ slug: String) {
        guard stopFrameSendStartedAt != nil else { return }
        postStopParsedEventCounts[slug, default: 0] += 1
        let sequence = postStopParsedEventCounts.values.reduce(0, +)
        logger.debug(
            "Post-stop parser result: event=\(slug, privacy: .public) sequence=\(sequence, privacy: .public)"
        )
    }

    /// Record a post-stop parser result as a fixed event slug only.
    mutating func notePostStopParsedEvent(_ event: StreamingProviderEvent?) {
        let slug = Self.normalizedEventSlug(for: event)
        notePostStopParsedEventSlug(slug)
    }

    /// Record a binary UTF-8 failure after the stop boundary.
    mutating func notePostStopUTF8DecodeFailure() {
        guard stopFrameSendStartedAt != nil else { return }
        postStopUTF8DecodeFailures += 1
        let failures = postStopUTF8DecodeFailures
        logger.debug(
            "Post-stop binary frame UTF-8 decode failed: failures=\(failures, privacy: .public)"
        )
    }

    // MARK: - Test Seams

    /// Feed content-free frame measurements and a fixed event slug to the
    /// same counter helpers used by the live receive path.
    mutating func recordPostStopDiagnosticsForTesting(
        wireKind: String,
        byteCount: Int,
        normalizedEventSlug: String? = nil,
        utf8DecodeFailed: Bool = false
    ) {
        if stopFrameSendStartedAt == nil {
            noteStopFrameSendStarted()
        }
        if stopFrameSentAt == nil {
            noteStopFrameSent()
        }
        precondition(wireKind == "string" || wireKind == "binary")
        precondition(byteCount >= 0)
        notePostStopWireFrame(kind: wireKind, byteCount: byteCount)
        if let normalizedEventSlug {
            notePostStopParsedEventSlug(normalizedEventSlug)
        }
        if utf8DecodeFailed {
            precondition(wireKind == "binary")
            notePostStopUTF8DecodeFailure()
        }
    }

    /// Return only counts, byte measurements and fixed-slug event counts.
    func postStopDiagnosticSnapshotForTesting() -> [String: Int] {
        var snapshot = [
            "string_frames": postStopStringFrameCount,
            "string_bytes": postStopStringByteCount,
            "binary_frames": postStopBinaryFrameCount,
            "binary_bytes": postStopBinaryByteCount,
            "utf8_decode_failures": postStopUTF8DecodeFailures
        ]
        for (slug, count) in postStopParsedEventCounts {
            snapshot["event_\(slug)"] = count
        }
        return snapshot
    }
}
