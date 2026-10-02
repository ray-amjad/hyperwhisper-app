//
//  StageExtrasKeySetTests.swift
//  hyperwhisperTests
//
//  Issue #784: the failure publish of the `stage_*` Sentry scope extras wrote 6
//  keys and the slow-success publish wrote 10. `SentryService.setExtras` never
//  removes a key, so the 4 missing keys carried an earlier recording's values
//  onto the failure event. Every publish (pre-transcribe, slow success,
//  failure) now goes through one builder; these tests pin that every shape
//  writes the same full key set. The pre-transcribe publish is the one the
//  pipeline's own failure event carries, because the pipeline captures that
//  event before it rethrows into the flow's `catch`.
//

import Foundation
import Testing
@testable import HyperWhisper

struct StageExtrasKeySetTests {

    private static let originalKeys: Set<String> = [
        "stage_wav_ready_ms", "stage_file_check_ms", "stage_vad_trim_ms",
        "stage_create_row_ms", "stage_transcribe_ms", "stage_core_data_update_ms",
        "stage_flow_ms", "stage_audio_duration_seconds", "stage_duration_allowance_ms",
        "stage_effective_ui_threshold_ms"
    ]

    /// The slow-success call shape: every stage reached, every value real.
    private static var success: [String: Any] { RecordingTranscriptionFlow.stageExtras(
        wavReadyMs: 12, fileCheckMs: 3, vadTrimMs: 40, createRowMs: 8, transcribeMs: 9_000,
        coreDataUpdateMs: 15, flowMs: 9_200, audioDurationSeconds: 12.5,
        durationAllowanceMs: 1_875, effectiveUIThresholdMs: 9_875
    ) }

    /// The pre-transcribe call shape: the stages before the call are real, and
    /// transcribe, Core Data update and the UI threshold are -1, as the flow
    /// passes them right before `transcribeWithDetails`.
    private static var preTranscribe: [String: Any] { RecordingTranscriptionFlow.stageExtras(
        wavReadyMs: 12, fileCheckMs: 3, vadTrimMs: 40, createRowMs: 8, transcribeMs: -1,
        coreDataUpdateMs: -1, flowMs: 70, audioDurationSeconds: 12.5,
        durationAllowanceMs: 1_875, effectiveUIThresholdMs: -1
    ) }

    /// The failure call shape: the threshold needs the transcription result, so
    /// it is -1, as the `catch` passes it; Core Data update is never reached.
    private static var failure: [String: Any] { RecordingTranscriptionFlow.stageExtras(
        wavReadyMs: 12, fileCheckMs: 3, vadTrimMs: 40, createRowMs: 8, transcribeMs: 2_500,
        coreDataUpdateMs: -1, flowMs: 2_600, audioDurationSeconds: 12.5,
        durationAllowanceMs: 1_875, effectiveUIThresholdMs: -1
    ) }

    @Test func everyPublishWritesTheIdenticalKeySet() {
        #expect(Set(Self.preTranscribe.keys) == Set(Self.success.keys))
        #expect(Set(Self.failure.keys) == Set(Self.success.keys))
        #expect(Set(Self.success.keys) == Self.originalKeys.union(["stage_reported_at"]))
    }

    @Test func notYetKnownStagesAreMinusOne() {
        #expect(Self.preTranscribe["stage_transcribe_ms"] as? Int == -1)
        #expect(Self.preTranscribe["stage_core_data_update_ms"] as? Int == -1)
        #expect(Self.preTranscribe["stage_effective_ui_threshold_ms"] as? Int == -1)
        #expect(Self.preTranscribe["stage_audio_duration_seconds"] as? Double == 12.5)
        #expect(Self.failure["stage_core_data_update_ms"] as? Int == -1)
        #expect(Self.failure["stage_effective_ui_threshold_ms"] as? Int == -1)
        #expect(Self.failure["stage_duration_allowance_ms"] as? Int == 1_875)
    }

    @Test func reportedAtIsAnISO8601Timestamp() throws {
        let when = Date(timeIntervalSince1970: 1_790_000_000)
        let extras = RecordingTranscriptionFlow.stageExtras(
            wavReadyMs: 1, fileCheckMs: 1, vadTrimMs: 1, createRowMs: 1, transcribeMs: 1,
            coreDataUpdateMs: 1, flowMs: 1, audioDurationSeconds: 1,
            durationAllowanceMs: 1, effectiveUIThresholdMs: 1, reportedAt: when
        )
        let stamp = try #require(extras["stage_reported_at"] as? String)
        #expect(ISO8601DateFormatter().date(from: stamp) == when)
    }

    /// Every value is a number or the timestamp, and no key is one the
    /// `beforeSend` deny-list would rewrite.
    @Test func noValueCarriesContentAndNoKeyIsRedacted() {
        for (key, value) in Self.success {
            #expect(value is Int || value is Double || key == "stage_reported_at", "\(key)")
            #expect(!SentryService.isRedactedExtraKey(key), "\(key)")
        }
    }
}
