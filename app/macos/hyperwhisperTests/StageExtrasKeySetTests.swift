//
//  StageExtrasKeySetTests.swift
//  hyperwhisperTests
//
//  Issue #784: the failure publish of the `stage_*` Sentry scope extras wrote 6
//  keys and the slow-success publish wrote 10. `SentryService.setExtras` never
//  removes a key, so the 4 missing keys carried an earlier recording's values
//  onto the failure event. Both publishes now go through one builder; these
//  tests pin that both shapes write the same full key set.
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

    /// The failure call shape: duration, allowance and threshold are -1, as the
    /// `catch` passes them, and the stages never reached keep their -1 too.
    private static var failure: [String: Any] { RecordingTranscriptionFlow.stageExtras(
        wavReadyMs: 12, fileCheckMs: 3, vadTrimMs: -1, createRowMs: -1, transcribeMs: -1,
        coreDataUpdateMs: -1, flowMs: 60, audioDurationSeconds: -1,
        durationAllowanceMs: -1, effectiveUIThresholdMs: -1
    ) }

    @Test func failureAndSuccessWriteTheIdenticalKeySet() {
        #expect(Set(Self.failure.keys) == Set(Self.success.keys))
        #expect(Set(Self.success.keys) == Self.originalKeys.union(["stage_reported_at"]))
    }

    @Test func failureSentinelsAreMinusOne() {
        #expect(Self.failure["stage_core_data_update_ms"] as? Int == -1)
        #expect(Self.failure["stage_audio_duration_seconds"] as? Double == -1)
        #expect(Self.failure["stage_duration_allowance_ms"] as? Int == -1)
        #expect(Self.failure["stage_effective_ui_threshold_ms"] as? Int == -1)
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
