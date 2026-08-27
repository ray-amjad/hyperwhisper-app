//
//  RecordingStartTraceTests.swift
//  hyperwhisperTests
//
//  `RecordingStartTrace` writes Sentry scope extras, and those extras leave the
//  machine. These tests drive the real state machine and pin three properties:
//  every write carries the full key set, the state never lies about whether a
//  start is in flight, and no value can carry user content.
//

import Testing
@testable import HyperWhisper

struct RecordingStartTraceTests {

    private let attempt = "11111111-2222-3333-4444-555555555555"

    private func makeTrace() -> RecordingStartTrace {
        RecordingStartTrace(attemptId: attempt, trigger: .shortcut)
    }

    /// `SentryService.setExtras` only adds and overwrites keys — it never
    /// removes one. A payload that omitted a key would leave the previous
    /// attempt's value on the scope under this attempt's id.
    @Test func everyWriteCarriesTheSameFullKeySet() {
        let trace = makeTrace()
        var keySets: [Set<String>] = [Set(trace.currentPayload().keys)]

        trace.begin()
        keySets.append(Set(trace.currentPayload().keys))
        trace.enter(.audioEngineStarted, elapsedMs: 30)
        keySets.append(Set(trace.currentPayload().keys))
        trace.checkpoint(.audioEngineStarted, stepMs: 12, elapsedMs: 42)
        keySets.append(Set(trace.currentPayload().keys))
        trace.finish(.ok, elapsedMs: 60)
        keySets.append(Set(trace.currentPayload().keys))

        for keys in keySets {
            #expect(keys == keySets[0])
            for key in keys {
                #expect(key.hasPrefix(RecordingStartTrace.keyPrefix))
            }
        }
    }

    @Test func everyWriteNamesItsAttemptAndTrigger() {
        let trace = makeTrace()
        trace.begin()
        trace.checkpoint(.microphonePermission, stepMs: 900, elapsedMs: 950)

        let payload = trace.currentPayload()
        #expect(payload["recording_start_attempt"] as? String == attempt)
        #expect(payload["recording_start_trigger"] as? String == "shortcut")
    }

    @Test func enterNamesTheStepInFlight() {
        let trace = makeTrace()
        trace.begin()
        trace.enter(.audioEngineStarted, elapsedMs: 30)

        let payload = trace.currentPayload()
        #expect(payload["recording_start_state"] as? String == RecordingStartTrace.stateRunning)
        #expect(payload["recording_start_pending_step"] as? String == "audio_engine_started")
        #expect(payload["recording_start_pending_since_ms"] as? Int == 30)
        // The step has been entered, not completed.
        #expect(payload["recording_start_step"] as? String == RecordingStartTrace.slugNone)
    }

    @Test func checkpointCompletesThePendingStep() {
        let trace = makeTrace()
        trace.begin()
        trace.enter(.microphonePermission, elapsedMs: 10)
        trace.checkpoint(.microphonePermission, stepMs: 900, elapsedMs: 910)

        let payload = trace.currentPayload()
        #expect(payload["recording_start_step"] as? String == "microphone_permission")
        #expect(payload["recording_start_step_index"] as? Int == 1)
        #expect(payload["recording_start_step_ms"] as? Int == 900)
        #expect(payload["recording_start_elapsed_ms"] as? Int == 910)
        #expect(payload["recording_start_pending_step"] as? String == RecordingStartTrace.slugNone)
    }

    /// Phase B fires two checkpoints AFTER the critical path finishes. If those
    /// reset the state, every healthy launch would leave the scope claiming a
    /// start is in flight, and every later hang would be misread as a
    /// recording-start hang — the exact failure this trace exists to prevent.
    @Test func aCheckpointAfterFinishKeepsTheAttemptFinished() {
        let trace = makeTrace()
        trace.begin()
        trace.checkpoint(.criticalPathComplete, stepMs: 5, elapsedMs: 45)
        trace.finish(.ok, elapsedMs: 46)
        trace.checkpoint(.contextCaptureComplete, stepMs: 300, elapsedMs: 400)

        let payload = trace.currentPayload()
        #expect(payload["recording_start_state"] as? String == RecordingStartTrace.stateFinished)
        #expect(payload["recording_start_outcome"] as? String == "ok")
        #expect(payload["recording_start_step"] as? String == "context_capture_complete")
    }

    /// A new attempt must not inherit any field of the previous one.
    @Test func beginClearsThePreviousAttempt() {
        let trace = makeTrace()
        trace.begin()
        trace.enter(.microphonePermission, elapsedMs: 10)
        trace.checkpoint(.microphonePermission, stepMs: 900, elapsedMs: 910)
        trace.finish(.microphonePermissionDenied, elapsedMs: 920)

        trace.begin()

        let payload = trace.currentPayload()
        #expect(payload["recording_start_state"] as? String == RecordingStartTrace.stateRunning)
        #expect(payload["recording_start_outcome"] as? String == RecordingStartTrace.slugNone)
        #expect(payload["recording_start_step"] as? String == RecordingStartTrace.slugNone)
        #expect(payload["recording_start_pending_step"] as? String == RecordingStartTrace.slugNone)
        #expect(payload["recording_start_step_index"] as? Int == 0)
        #expect(payload["recording_start_step_ms"] as? Int == 0)
        #expect(payload["recording_start_elapsed_ms"] as? Int == 0)
    }

    /// The slugs are the whole privacy argument: a report can only ever contain
    /// one of these strings, so no mode name, transcript or path can reach it.
    @Test func slugsAreLowerSnakeCaseAndUnique() {
        let allowed = Set("abcdefghijklmnopqrstuvwxyz0123456789_")
        let slugs = RecordingStartStep.allCases.map(\.rawValue)
            + RecordingStartOutcome.allCases.map(\.rawValue)
            + [RecordingStartTrace.slugNone, RecordingStartTrace.stateRunning, RecordingStartTrace.stateFinished]

        for slug in slugs {
            #expect(!slug.isEmpty)
            #expect(slug.allSatisfy { allowed.contains($0) })
        }
        #expect(Set(slugs).count == slugs.count)
    }
}
