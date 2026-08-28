//
//  RecordingStartTrace.swift
//  hyperwhisper
//
//  Publishes the recording-start critical path as Sentry SCOPE extras.
//
//  WHY THIS EXISTS (Sentry HYPERWHISPER-F7 — "App hanging for at least 10000 ms"):
//  `SentryService.beforeSend` sets `event.breadcrumbs = nil`, so every
//  `SentryService.addBreadcrumb` call in the start path is discarded before the
//  event leaves the machine. An AppHang event is raised by the SDK itself, with
//  no app frames on the stack and no extras at all, so today a hang report
//  cannot say which start step was in flight when the main thread stalled.
//
//  Scope extras survive `beforeSend` and ride along with the next event the SDK
//  raises on its own. Writing the step, the step being entered, the elapsed time
//  and the terminal outcome into the scope means the next hang report answers
//  that question.
//
//  PRIVACY: every value here is a fixed slug, a millisecond count or the
//  app-generated attempt UUID. No transcript, audio, prompt, mode name, file
//  path or window title is ever written.
//

import Foundation

// MARK: - Steps

/// The ordered steps of the recording-start critical path.
///
/// Fixed slugs on purpose: the raw value is what reaches Sentry, so no free
/// text can leak into a report, and the slugs stay queryable across releases.
enum RecordingStartStep: String, CaseIterable {
    case streamingShortcutDetected = "streaming_shortcut_detected"
    case streamingDialogShown = "streaming_dialog_shown"
    case streamingStarted = "streaming_started"
    case microphonePermission = "microphone_permission"
    case audioEngineStarted = "audio_engine_started"
    case recordingDialogShown = "recording_dialog_shown"
    case recordingStateUpdated = "recording_state_updated"
    case criticalPathComplete = "critical_path_complete"
    case backgroundValidationPassed = "background_validation_passed"
    case contextCaptureComplete = "context_capture_complete"
}

/// How a recording-start attempt ended. One case per `return` in
/// `handleStartRecording`, so an attempt that never reports an outcome is a
/// path that hung or crashed before it finished.
enum RecordingStartOutcome: String, CaseIterable {
    case ok
    case superseded
    case cancelledByNewerToggle = "cancelled_by_newer_toggle"
    case microphonePermissionDenied = "microphone_permission_denied"
    case recorderStartFailed = "recorder_start_failed"
    case streamingActive = "streaming_active"
    case streamingFailed = "streaming_failed"
    case streamingPermissionDenied = "streaming_permission_denied"
}

// MARK: - Trace

/// Mirrors recording-start progress into Sentry scope extras.
///
/// A reference type rather than a `struct`: `handleStartRecording` captures it
/// inside the Phase B `Task`, and a captured value copy would drop the later
/// checkpoints.
///
/// `@unchecked Sendable`: all mutable state lives in `snapshot`, and every read
/// and write of it is inside `lock`.
final class RecordingStartTrace: @unchecked Sendable {

    /// Scope-extra key prefix. Every key this type writes starts with it, so a
    /// Sentry search for `recording_start_*` finds the whole trace.
    static let keyPrefix = "recording_start_"

    /// `state` while the attempt is still on the critical path. A hang report
    /// carrying this value stalled DURING the start path.
    static let stateRunning = "running"
    /// `state` once the attempt reached a terminal outcome. A hang report
    /// carrying this value stalled AFTER the start path finished.
    static let stateFinished = "finished"

    /// Written to a slug field that has no value yet. A real slug can never be
    /// this, so a report never has to tell "absent" from "not reached".
    static let slugNone = "none"

    /// The whole published state of one attempt.
    ///
    /// Every publish writes EVERY field. `SentryService.setExtras` only adds and
    /// overwrites keys — it never removes one — so a payload that omitted a key
    /// would leave the PREVIOUS attempt's value on the scope, re-stamped with
    /// this attempt's id. A full key set makes that impossible.
    private struct Snapshot {
        var state: String = RecordingStartTrace.stateRunning
        var step: String = RecordingStartTrace.slugNone
        var stepIndex: Int = 0
        var stepMs: Int = 0
        /// The step that has been ENTERED but not yet completed. This is the
        /// field that answers HYPERWHISPER-F7: a checkpoint only says which step
        /// last finished, and a hang happens inside the step that never did.
        var pendingStep: String = RecordingStartTrace.slugNone
        /// Elapsed ms at the moment `pendingStep` was entered.
        var pendingSinceMs: Int = 0
        var outcome: String = RecordingStartTrace.slugNone
        var elapsedMs: Int = 0
    }

    private let attemptId: String
    private let trigger: RecordingTriggerSource
    private let lock = NSLock()
    private var snapshot = Snapshot()

    init(attemptId: String, trigger: RecordingTriggerSource) {
        self.attemptId = attemptId
        self.trigger = trigger
    }

    /// Milliseconds since `start`, rounded. Shared so every field on the trace
    /// and the os.log lines beside it use one unit.
    static func elapsedMs(since start: Date) -> Int {
        Int((Date().timeIntervalSince(start) * 1_000).rounded())
    }

    // MARK: State transitions

    /// Record that an attempt started. Resets the whole snapshot, so no field of
    /// the previous attempt can survive onto this one.
    func begin() {
        mutate { $0 = Snapshot() }
    }

    /// Record that a step is about to run. Publish this BEFORE a long call, so a
    /// hang inside it is reported against the step it is really in.
    func enter(_ step: RecordingStartStep, elapsedMs: Int) {
        mutate {
            $0.pendingStep = step.rawValue
            $0.pendingSinceMs = elapsedMs
            $0.elapsedMs = elapsedMs
        }
    }

    /// Record that a step completed.
    ///
    /// `state` is deliberately NOT forced back to `running`: two checkpoints
    /// (`background_validation_passed`, `context_capture_complete`) run in
    /// Phase B, after the critical path already finished. Resetting the state
    /// there would leave every healthy launch claiming a start is in flight, and
    /// every later hang would be misread as a recording-start hang.
    func checkpoint(_ step: RecordingStartStep, stepMs: Int, elapsedMs: Int) {
        mutate {
            $0.step = step.rawValue
            $0.stepIndex += 1
            $0.stepMs = stepMs
            $0.elapsedMs = elapsedMs
            $0.pendingStep = RecordingStartTrace.slugNone
            $0.pendingSinceMs = 0
        }
    }

    /// Record the terminal outcome of the attempt.
    func finish(_ outcome: RecordingStartOutcome, elapsedMs: Int) {
        mutate {
            $0.state = RecordingStartTrace.stateFinished
            $0.outcome = outcome.rawValue
            $0.elapsedMs = elapsedMs
            $0.pendingStep = RecordingStartTrace.slugNone
            $0.pendingSinceMs = 0
        }
    }

    // MARK: Payload

    /// The full extras payload for the current snapshot. Exposed so the tests
    /// can drive the real state machine instead of a copy of it.
    func currentPayload() -> [String: Any] {
        lock.lock()
        let current = snapshot
        lock.unlock()
        return payload(for: current)
    }

    private func payload(for values: Snapshot) -> [String: Any] {
        // `attempt` and `trigger` are repeated on every write on purpose. Two
        // starts can overlap — a fast stop/start leaves attempt A's Phase B
        // running while attempt B begins — and both write to one shared scope.
        [
            "\(Self.keyPrefix)state": values.state,
            "\(Self.keyPrefix)attempt": attemptId,
            "\(Self.keyPrefix)trigger": trigger.rawValue,
            "\(Self.keyPrefix)step": values.step,
            "\(Self.keyPrefix)step_index": values.stepIndex,
            "\(Self.keyPrefix)step_ms": values.stepMs,
            "\(Self.keyPrefix)pending_step": values.pendingStep,
            "\(Self.keyPrefix)pending_since_ms": values.pendingSinceMs,
            "\(Self.keyPrefix)outcome": values.outcome,
            "\(Self.keyPrefix)elapsed_ms": values.elapsedMs
        ]
    }

    // MARK: Publishing

    private func mutate(_ change: (inout Snapshot) -> Void) {
        lock.lock()
        change(&snapshot)
        let current = snapshot
        lock.unlock()
        publish(payload(for: current))
    }

    /// Scope extras leave the machine, so they obey the error-logging opt-in.
    private func publish(_ extras: [String: Any]) {
        guard AppLogger.isErrorLoggingEnabled else { return }
        SentryService.setExtras(extras)
    }
}
