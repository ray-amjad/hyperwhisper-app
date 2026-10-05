//
//  StageExtrasKeySetTests.swift
//  hyperwhisperTests
//
//  Issue #784: the failure publish of the `stage_*` Sentry scope extras wrote 6
//  keys and the slow-success publish wrote 10. `SentryService.setExtras` never
//  removes a key, so the 4 missing keys carried an earlier recording's values
//  onto the failure event. Every publish (pre-transcribe, success, cancel,
//  failure) now goes through one builder; the first tests pin that every shape
//  writes the same full key set. The pre-transcribe publish is the one the
//  pipeline's own failure event carries, because the pipeline captures that
//  event before it rethrows into the flow's `catch`.
//
//  The builder tests only prove `stageExtras` echoes its arguments, so the
//  last tests read the flow's own source (the `ProductionSource` trick the
//  Local API tests use) to pin the call sites: `handleStopRecordingWithTranscription`
//  is `@MainActor` and needs a live recording, pipeline and Core Data row to
//  run, so it cannot be called here.
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

    // MARK: - Call sites, read from the production source

    private static let flowPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+StopRecording.swift"

    private static let publish = "SentryService.setExtras(Self.stageExtras("

    /// The comment-free body of `handleStopRecordingWithTranscription`, up to the builder.
    private static func flowBody() throws -> String {
        try ProductionSource.slice(
            of: flowPath,
            from: "func handleStopRecordingWithTranscription(",
            to: "static func stageExtras("
        )
    }

    /// The pipeline captures its failure event inside `transcribeWithDetails`,
    /// before it rethrows, so this recording's block must be on the scope
    /// BEFORE that call, with the not-yet-known stages at -1.
    @Test func theFlowPublishesThePreTranscribeBlockBeforeTheCall() throws {
        let flow = try Self.flowBody()
        let call = try #require(flow.range(of: "transcribeWithDetails("),
                                "transcribeWithDetails( not found in the flow")
        let beforeCall = flow[..<call.lowerBound]
        let pre = try #require(beforeCall.range(of: Self.publish, options: .backwards),
                               "no stage_* publish before transcribeWithDetails(")
        let afterPre = beforeCall[pre.upperBound...]
        let close = try #require(afterPre.range(of: "))"), "the pre-transcribe publish does not close")
        let preCall = String(afterPre[..<close.lowerBound])
        #expect(preCall.contains("transcribeMs: -1"), "\(preCall)")
        #expect(preCall.contains("coreDataUpdateMs: -1"), "\(preCall)")
        #expect(preCall.contains("effectiveUIThresholdMs: -1"), "\(preCall)")
    }

    /// Success (slow AND fast), cancel and failure each overwrite the
    /// pre-transcribe block, so no later event carries `stage_transcribe_ms` = -1
    /// after a transcription that finished or stopped.
    @Test func everyExitOfTheTranscribeCallRepublishesTheBlock() throws {
        let flow = try Self.flowBody()
        #expect(flow.components(separatedBy: Self.publish).count - 1 == 4,
                "expected 4 stage_* publishes: pre-transcribe, success, cancel, failure")

        // Success: after the threshold is known, and before the slow/fast `if`,
        // so a fast success publishes too.
        let success = try ProductionSource.slice(
            of: Self.flowPath,
            from: "let effectiveUIThreshold",
            to: "if transcribingUIElapsedMs >= effectiveUIThreshold"
        )
        #expect(success.contains(Self.publish), "the success publish must sit outside the slow-path if")
        #expect(success.contains("transcribeMs: transcribeMs"))
        #expect(success.contains("effectiveUIThresholdMs: effectiveUIThreshold"))

        let cancel = try ProductionSource.slice(
            of: Self.flowPath,
            from: "} catch is CancellationError {",
            to: "} catch {"
        )
        #expect(cancel.contains(Self.publish), "the cancel catch must republish the stage_* block")
        #expect(cancel.contains("transcribeMs = transcribeStart.map"))
        #expect(cancel.contains("transcribeMs: transcribeMs"))

        let failure = try ProductionSource.slice(
            of: Self.flowPath,
            from: "} catch {",
            to: "static func stageExtras("
        )
        #expect(failure.contains(Self.publish), "the failure catch must republish the stage_* block")
        #expect(failure.contains("transcribeMs: transcribeMs"))
    }

    /// No hand-written `stage_*` dictionary anywhere in the app: a literal key
    /// outside `stageExtras` is a publish that can drop a key again (#784).
    @Test func noStageKeyIsWrittenOutsideTheBuilder() throws {
        let files = try ProductionSource.swiftFiles(under: ProductionSource.url("app/macos/hyperwhisper"))
        #expect(files.count >= 100, "the app source tree was not found where this test expects it")

        var offenders: [String] = []
        for file in files {
            var inBuilder = false
            let lines = try ProductionSource.text(of: file).components(separatedBy: .newlines)
            for (offset, line) in lines.enumerated() {
                // The builder's own dictionary is the one allowed writer. It ends
                // at its closing brace, the first line that is exactly "    }".
                if line.contains("static func stageExtras(") { inBuilder = true }
                if inBuilder {
                    if line == "    }" { inBuilder = false }
                    continue
                }
                guard !line.trimmingCharacters(in: .whitespaces).hasPrefix("//") else { continue }
                if line.contains("\"stage_") {
                    offenders.append("\(file.lastPathComponent):\(offset + 1)")
                }
            }
        }
        #expect(offenders.isEmpty, "stage_* key written outside stageExtras at \(offenders.joined(separator: ", "))")
    }
}
