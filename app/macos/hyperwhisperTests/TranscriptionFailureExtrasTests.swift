//
//  TranscriptionFailureExtrasTests.swift
//  hyperwhisperTests
//
//  Issue #795: every macOS transcription failure used to send the Mode's `name`
//  to Sentry as the `modeName` extra. A Mode's name is free text the user typed
//  — the app itself only ever writes the one name a fresh install seeds — so
//  the field was user content, not metadata, and the `isRedactedExtraKey`
//  deny-list (`transcript` / `text` / `prompt`) never matched it.
//
//  This is the macOS twin of the Windows assertion in
//  `app/windows/HyperWhisper.SmokeTests/Program.cs`:
//
//      Assert(!extras.ContainsKey("mode_name"), ...)
//
//  It reads the real payload builder, so a reinstated key fails here rather
//  than in production Sentry.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Transcription failure extras")
struct TranscriptionFailureExtrasTests {

    // MARK: - Fixtures

    /// A distinct error type so `errorType` has something to report. The name is
    /// invented; no fixture in this file uses a real Mode name.
    private struct FixtureTranscriptionError: Error {}

    /// One realistic failure, so every test below reads the real dictionary.
    ///
    /// The default `audioURL` lives under `/Users/…` on purpose: the builder must
    /// never put the recording path in the payload.
    private static func extras(
        modePreset: String = "custom",
        modeIsSystemProvided: Bool = false,
        audioURL: URL = URL(fileURLWithPath: "/Users/example/Documents/hyperwhisper/recordings/recording-1.wav"),
        httpStatus: Int? = nil
    ) -> [String: Any] {
        TranscriptionPipeline.transcriptionFailureExtras(
            modePreset: modePreset,
            modeIsSystemProvided: modeIsSystemProvided,
            actualProvider: "openai",
            modelString: "cloud",
            useCloud: true,
            language: "auto",
            postProcessingMode: "cloud",
            postProcessingProvider: "hyperwhisper",
            shouldRunPostProcessing: true,
            isHyperwhisperTranscription: false,
            audioURL: audioURL,
            fileExists: true,
            fileReadable: true,
            fileSizeBytes: 65_536,
            error: FixtureTranscriptionError(),
            classification: TranscriptionPipeline.TranscriptionErrorClassification(
                category: "network",
                kind: "url_timedOut",
                retryable: true,
                httpStatus: httpStatus
            ),
            stage: "transcribe",
            stageElapsedMs: 1_200,
            totalElapsedMs: 4_500,
            stageTimeline: ["start=10ms@10ms", "transcribe=1200ms@4500ms (failed)"],
            transcriptionState: "error",
            recordingSessionID: "nil"
        )
    }

    // MARK: - The Mode's name must never be reported

    /// The assertion this file exists for. `modeName` is user-typed text.
    ///
    /// Warning: do not "fix" a failure here by truncating or hashing the name. A
    /// truncated mode name is still the mode name, and a hash is a stable
    /// per-person identifier. Report `modePreset` instead, or report nothing.
    @Test func modeNameIsNotReported() {
        let extras = Self.extras()

        #expect(extras["modeName"] == nil)
        let nameShapedKeys = extras.keys.filter { $0.lowercased().contains("modename") }
        #expect(nameShapedKeys.isEmpty, "no extra may carry the Mode's name")
    }

    /// The replacement field, and the question it answers: which KIND of Mode
    /// was running, and was it one the app seeded or one the person made.
    @Test func modePresetAndSystemProvidedAreReported() {
        let extras = Self.extras(modePreset: "custom", modeIsSystemProvided: false)

        #expect(extras["modePreset"] as? String == "custom")
        #expect(extras["modeIsSystemProvided"] as? Bool == false)

        let seeded = Self.extras(modePreset: "hyper", modeIsSystemProvided: true)
        #expect(seeded["modePreset"] as? String == "hyper")
        #expect(seeded["modeIsSystemProvided"] as? Bool == true)
    }

    /// The call site passes `"unknown"` when the Mode could not be resolved. The
    /// builder must pass that through untouched — a diagnostics payload that
    /// substitutes a value of its own reports a fact that is not true.
    @Test func anUnresolvedModeIsReportedAsUnknown() {
        let extras = Self.extras(modePreset: "unknown", modeIsSystemProvided: false)

        #expect(extras["modePreset"] as? String == "unknown")
        #expect(extras["modeName"] == nil)
    }

    // MARK: - The recording path stays out (HYPERWHISPER-T2)

    /// The recording path carries the account name, so it is absent by design.
    /// `fileExtension` (plus `fileExists` / `fileReadable` / `fileSizeBytes`)
    /// answers every question the path was there to answer.
    @Test func theRecordingPathIsAbsentButTheExtensionIsPresent() {
        let extras = Self.extras()

        #expect(extras["fileExtension"] as? String == "wav")
        #expect(extras["fileExists"] as? Bool == true)
        #expect(extras["fileReadable"] as? Bool == true)
        #expect(extras["fileSizeBytes"] as? Int64 == 65_536)
        #expect(extras["usedCAFFallback"] as? Bool == false)

        let leaked = extras.filter { String(describing: $0.value).contains("/Users/") }
        #expect(leaked.isEmpty, "no extra may carry the recording path")
    }

    /// The CAF fallback flag is derived from the extension, not from the path.
    @Test func theCAFFallbackIsFlaggedFromTheExtension() {
        let extras = Self.extras(
            audioURL: URL(fileURLWithPath: "/Users/example/Documents/hyperwhisper/recordings/recording-1.CAF")
        )

        #expect(extras["usedCAFFallback"] as? Bool == true)
        let leaked = extras.filter { String(describing: $0.value).contains("/Users/") }
        #expect(leaked.isEmpty, "no extra may carry the recording path")
    }

    // MARK: - The rest of the payload survived the lift out of the catch block

    /// The HTTP status is conditional: present when the classification has one,
    /// absent otherwise. This was a separate write on the dictionary before the
    /// builder existed, and it is the easiest part of the lift to drop.
    @Test func theHTTPStatusIsReportedOnlyWhenTheClassificationHasOne() {
        #expect(Self.extras(httpStatus: nil)["errorHttpStatus"] == nil)
        #expect(Self.extras(httpStatus: 429)["errorHttpStatus"] as? Int == 429)
    }

    /// The provider/error axes the Sentry issues are triaged on.
    @Test func theProviderAndErrorAxesAreReported() {
        let extras = Self.extras()

        #expect(extras["actualProvider"] as? String == "openai")
        #expect(extras["modelString"] as? String == "cloud")
        #expect(extras["useCloud"] as? Bool == true)
        #expect(extras["errorCategory"] as? String == "network")
        #expect(extras["errorKind"] as? String == "url_timedOut")
        #expect(extras["errorRetryable"] as? Bool == true)
        #expect(extras["errorStage"] as? String == "transcribe")
        // Matched as a substring: `String(describing:)` on a metatype may or may
        // not qualify a nested type, and that is not what this test is about.
        let errorType = extras["errorType"] as? String
        #expect(errorType?.contains("FixtureTranscriptionError") == true)
        #expect(extras["totalElapsedMs"] as? Int == 4_500)
        #expect(extras["stageElapsedMs"] as? Int == 1_200)
    }
}
