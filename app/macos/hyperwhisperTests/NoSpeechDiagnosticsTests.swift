//
//  NoSpeechDiagnosticsTests.swift
//  hyperwhisperTests
//
//  Issue #291: macOS's no-speech diagnostic used to be a hand-mirrored copy of
//  the Windows one and had drifted a whole classification arm and two
//  fingerprint elements. It now delegates to the shared `hw-audio` core. These
//  tests pin the parts that are deliberately NOT shared (the macOS Sentry
//  identity) and the parts that must now match Windows exactly (the arms, the
//  thresholds, and provider-axis tags).
//

import Foundation
import Testing
@testable import HyperWhisper

struct NoSpeechDiagnosticsTests {

    // MARK: - Helpers

    private func audio(
        analysisSucceeded: Bool = true,
        decodedSampleCount: Int? = 48_000,
        measuredSampleCount: Int? = 16_000,
        peakDbfs: Double = -12.0,
        rmsDbfs: Double = -20.0,
        nonSilentRatio: Double = 0.4
    ) -> AudioAnalysisDiagnostics {
        AudioAnalysisDiagnostics(
            analysisSucceeded: analysisSucceeded,
            durationSeconds: 3.0,
            fileSizeBytes: 65_536,
            peakDbfs: peakDbfs,
            rmsDbfs: rmsDbfs,
            nonSilentRatio: nonSilentRatio,
            decodedSampleCount: decodedSampleCount,
            measuredSampleCount: measuredSampleCount
        )
    }

    private func payload(
        modeIdentity: NoSpeechModeIdentity?,
        outcome: HwNoSpeechOutcome = .noSpeech
    ) -> TranscriptionDiagnosticsService.DiagnosticPayload {
        TranscriptionDiagnosticsService.buildPayload(
            audio: audio(),
            audioFileExists: true,
            audioFileExtension: "wav",
            modeIdentity: modeIdentity,
            attemptDiagnostics: nil,
            responseNoSpeechDetected: true,
            diagnosticStage: "live_recording",
            diagnosticSource: "provider_no_speech",
            presentation: TranscriptionDiagnosticsService.presentation(for: outcome)!
        )
    }

    // MARK: - Sentry identity (deliberately NOT shared with Windows)

    /// The messages are the stable query identity for the macOS Logs.
    @Test func macOSKeepsItsOwnLogMessages() {
        let noSpeech = TranscriptionDiagnosticsService.presentation(for: .noSpeech)
        #expect(noSpeech?.name == "no_speech")
        #expect(noSpeech?.message == "macOS transcription no-speech diagnostic")

        let empty = TranscriptionDiagnosticsService.presentation(for: .emptyRecording)
        #expect(empty?.name == "empty_recording")
        #expect(empty?.message == "macOS transcription empty recording diagnostic")

        // Skip is filtered out before anything is reported, so it has no
        // presentation by design.
        #expect(TranscriptionDiagnosticsService.presentation(for: .skip) == nil)
    }

    /// Every reportable outcome needs its own arm, name and message. A new
    /// outcome that copies an existing identity is the mislabelling this
    /// diagnostic exists to fix.
    @Test func everyReportableOutcomeHasAUniqueIdentity() {
        var names = Set<String>()
        var messages = Set<String>()

        for outcome in [HwNoSpeechOutcome.skip, .emptyRecording, .noSpeech] {
            guard let presentation = TranscriptionDiagnosticsService.presentation(for: outcome) else {
                continue
            }
            #expect(names.insert(presentation.name).inserted,
                    "duplicate diagnostic name '\(presentation.name)'")
            #expect(messages.insert(presentation.message).inserted,
                    "duplicate diagnostic message '\(presentation.message)'")
            #expect(presentation.message.hasPrefix("macOS "))
        }
    }

    // MARK: - Classification (the five shared arms)

    @Test func failedAnalysisIsAlwaysReportedAsNoSpeech() {
        // Arm 1 must stay first: with no usable analysis a zero sample count is
        // meaningless, so this must NOT come back as an empty recording.
        let outcome = TranscriptionDiagnosticsService.classify(
            audio(analysisSucceeded: false, decodedSampleCount: 0),
            backendNoSpeechDetected: true
        )
        #expect(outcome == .noSpeech)
    }

    @Test func zeroDecodedSamplesIsAnEmptyRecordingAndNilIsNot() {
        #expect(TranscriptionDiagnosticsService.classify(
            audio(decodedSampleCount: 0, peakDbfs: -120, rmsDbfs: -120, nonSilentRatio: 0),
            backendNoSpeechDetected: true) == .emptyRecording)

        // nil means "no decode loop ran", never "empty" — it must fall through
        // to the ordinary arms, which for dead silence means Skip.
        #expect(TranscriptionDiagnosticsService.classify(
            audio(decodedSampleCount: nil, peakDbfs: -120, rmsDbfs: -120, nonSilentRatio: 0),
            backendNoSpeechDetected: true) == .skip)
    }

    /// The arm reads the PRE-conversion count, as Windows does. `AudioConverter`
    /// emits nothing at all for a decodable-but-very-short file, so classifying
    /// on the post-conversion count called such a file "the recorder produced
    /// nothing" — the false report #291 removed on Windows, which macOS was still
    /// making because its `audio_decoded_sample_count` was the converter's output.
    @Test func theEmptyRecordingArmReadsThePreConversionCount() {
        #expect(TranscriptionDiagnosticsService.classify(
            audio(decodedSampleCount: 8, measuredSampleCount: 0,
                  peakDbfs: -120, rmsDbfs: -120, nonSilentRatio: 0),
            backendNoSpeechDetected: true) != .emptyRecording)

        // Nothing decoded at all is still an empty recording.
        #expect(TranscriptionDiagnosticsService.classify(
            audio(decodedSampleCount: 0, measuredSampleCount: 0,
                  peakDbfs: -120, rmsDbfs: -120, nonSilentRatio: 0),
            backendNoSpeechDetected: true) == .emptyRecording)
    }

    /// Arm 3, which macOS never had before #291. It is currently unreachable
    /// from the app (see `captureNoSpeechDiagnostic`'s doc comment), so this test
    /// is what keeps it honest until the error transport can carry the flag.
    @Test func emptyTranscriptWithoutFlagIsAlwaysReportedEvenWhenTheSignalLooksSilent() {
        let deadSilence = audio(peakDbfs: -95, rmsDbfs: -100, nonSilentRatio: 0)

        // Without the flag this is a benign skip...
        #expect(TranscriptionDiagnosticsService.classify(
            deadSilence, backendNoSpeechDetected: true) == .skip)

        // ...but a provider that returned an empty transcript without saying
        // "no speech" is an anomaly whatever the signal looks like.
        #expect(TranscriptionDiagnosticsService.classify(
            deadSilence,
            backendNoSpeechDetected: true,
            emptyTranscriptWithoutFlag: true) == .noSpeech)
    }

    @Test func confirmedDeadSilenceIsSkippedEvenWithoutABackendFlag() {
        // Arm 4 does not consult the backend flag at all.
        #expect(TranscriptionDiagnosticsService.classify(
            audio(peakDbfs: -80, rmsDbfs: -90, nonSilentRatio: 0),
            backendNoSpeechDetected: false) == .skip)

        // Just above the confirmed-silence peak, with no backend agreement, is
        // still reported.
        #expect(TranscriptionDiagnosticsService.classify(
            audio(peakDbfs: -40, rmsDbfs: -90, nonSilentRatio: 0),
            backendNoSpeechDetected: false) == .noSpeech)
    }

    @Test func theRealHyperwhisperPaSampleIsSkippedAndALoudDisagreementIsNot() {
        // The actual HYPERWHISPER-PA/-QB/-VY values the shared thresholds were
        // tuned against: quiet room tone the backend correctly called no-speech.
        #expect(TranscriptionDiagnosticsService.classify(
            audio(peakDbfs: -30.0, rmsDbfs: -39.64, nonSilentRatio: 0.046),
            backendNoSpeechDetected: true) == .skip)

        // The cohort this diagnostic exists to catch: healthy speech energy, and
        // the provider still returned nothing.
        #expect(TranscriptionDiagnosticsService.classify(
            audio(peakDbfs: -18.47, rmsDbfs: -22.0, nonSilentRatio: 0.35),
            backendNoSpeechDetected: true) == .noSpeech)

        // Both low-signal conditions must hold. One of them alone must not skip.
        #expect(TranscriptionDiagnosticsService.classify(
            audio(peakDbfs: -30.0, rmsDbfs: -39.64, nonSilentRatio: 0.5),
            backendNoSpeechDetected: true) == .noSpeech)
        #expect(TranscriptionDiagnosticsService.classify(
            audio(peakDbfs: -30.0, rmsDbfs: -10.0, nonSilentRatio: 0.046),
            backendNoSpeechDetected: true) == .noSpeech)
    }

    @Test func theLowSignalSkipIsInclusiveAtBothThresholds() {
        #expect(TranscriptionDiagnosticsService.classify(
            audio(peakDbfs: -30.0,
                  rmsDbfs: noSpeechLowSignalRmsDbfs(),
                  nonSilentRatio: noSpeechLowSignalNonSilentRatio()),
            backendNoSpeechDetected: true) == .skip)
    }

    @Test func responseFlagAndMetadataReachTheRealPayload() {
        let diagnostics = TranscriptionAttemptDiagnostics(
            attemptSource: "cloud_instrumented",
            providerDisplayName: "HyperWhisper Cloud",
            backendRequestId: "request-123",
            backendSTTProvider: "deepgram",
            backendSTTModel: "nova-3",
            backendNoSpeechDetected: true,
            httpStatusCode: 200,
            responseLatencyMs: 420,
            providerAttemptMs: 510
        )
        let signal = audio(peakDbfs: -18, rmsDbfs: -22, nonSilentRatio: 0.4)
        #expect(TranscriptionDiagnosticsService.classify(
            signal,
            backendNoSpeechDetected: diagnostics.backendNoSpeechDetected ?? false
        ) == .noSpeech)

        let payload = TranscriptionDiagnosticsService.buildPayload(
            audio: signal,
            audioFileExists: true,
            audioFileExtension: "wav",
            modeIdentity: TranscriptionDiagnosticsService.modeIdentity(
                rawModel: "cloud", cloudProvider: "hyperwhisper"),
            attemptDiagnostics: diagnostics,
            responseNoSpeechDetected: diagnostics.backendNoSpeechDetected,
            diagnosticStage: "live_recording",
            diagnosticSource: "provider_no_speech",
            presentation: TranscriptionDiagnosticsService.presentation(for: .noSpeech)!
        )

        #expect(payload.tags["provider_attempt_source"] == "cloud_instrumented")
        #expect(payload.tags["backend_no_speech_detected"] == "true")
        let expectedStrings = [
            "provider_display_name": "HyperWhisper Cloud", "backend_request_id": "request-123",
            "backend_stt_provider": "deepgram", "backend_stt_model": "nova-3"
        ]
        for (key, value) in expectedStrings { #expect(payload.extras[key] as? String == value) }
        #expect(payload.extras["backend_http_status"] as? Int == 200)
        #expect(payload.extras["backend_response_latency_ms"] as? Int == 420)
        #expect(payload.extras["provider_attempt_ms"] as? Int == 510)
        for key in ["backend_empty_transcript_without_flag", "mode_name"] {
            #expect(payload.extras[key] == nil)
        }
        #expect(payload.extras.keys.allSatisfy { !SentryService.isRedactedExtraKey($0) })
    }

    @Test func missingResponseMetadataUsesUnknown() {
        let payload = TranscriptionDiagnosticsService.buildPayload(
            audio: audio(),
            audioFileExists: true,
            audioFileExtension: "wav",
            modeIdentity: nil,
            attemptDiagnostics: nil,
            responseNoSpeechDetected: nil,
            diagnosticStage: "live_recording",
            diagnosticSource: "provider_no_speech",
            presentation: TranscriptionDiagnosticsService.presentation(for: .noSpeech)!
        )

        #expect(payload.tags["provider_attempt_source"] == "unknown" &&
                payload.tags["backend_no_speech_detected"] == "unknown")
        for key in [
            "provider_display_name", "backend_request_id", "backend_stt_provider",
            "backend_stt_model", "backend_http_status", "backend_response_latency_ms",
            "provider_attempt_ms"
        ] {
            #expect(payload.extras[key] as? String == "unknown")
        }
    }

    @Test func uninstrumentedProviderFailureKeepsMeasuredAttemptTime() {
        let diagnostics = TranscriptionAttemptDiagnostics.failureSnapshot(
            providerDiagnostics: nil,
            providerDisplayName: "Local Provider",
            providerAttemptMs: 321
        )

        #expect(diagnostics.attemptSource == "provider_uninstrumented")
        #expect(diagnostics.providerDisplayName == "Local Provider")
        #expect(diagnostics.providerAttemptMs == 321)
        #expect(diagnostics.httpStatusCode == nil)
        #expect(diagnostics.responseLatencyMs == nil)
        #expect(diagnostics.backendNoSpeechDetected == nil)
    }

    @Test func audioAnalysisFailurePayloadContainsOnlySafeCauseMetadata() {
        var failedAudio = audio(analysisSucceeded: false)
        failedAudio.analysisFailure = "audio_decode_failed"
        failedAudio.analysisErrorDomain = NSCocoaErrorDomain
        failedAudio.analysisErrorCode = 260

        let payload = TranscriptionDiagnosticsService.buildPayload(
            audio: failedAudio,
            audioFileExists: true,
            audioFileExtension: "wav",
            modeIdentity: nil,
            attemptDiagnostics: nil,
            responseNoSpeechDetected: nil,
            diagnosticStage: "live_recording",
            diagnosticSource: "provider_no_speech",
            presentation: TranscriptionDiagnosticsService.presentation(for: .noSpeech)!
        )

        #expect(payload.extras["audio_analysis_failure"] as? String == "audio_decode_failed")
        #expect(payload.extras["audio_analysis_error_domain"] as? String == NSCocoaErrorDomain)
        #expect(payload.extras["audio_analysis_error_code"] as? Int == 260)
        #expect(payload.extras["audio_analysis_error"] == nil)
    }

    @Test func errorIdentityContainsOnlyFixedSafeFields() {
        let error = NSError(
            domain: "HyperWhisper.Transcription",
            code: 17,
            userInfo: [
                NSLocalizedDescriptionKey: "private words",
                "private_field": "private value"
            ]
        )

        let attributes = TranscriptionDiagnosticsService.errorIdentityAttributes(for: error)

        #expect(attributes["error_type"] as? String == "NSError")
        #expect(attributes["error_domain"] as? String == "HyperWhisper.Transcription")
        #expect(attributes["error_kind"] as? String == "no_speech_detected")
        #expect(attributes["error_code"] == nil)
        #expect(attributes.count == 3)
        #expect(!attributes.values.contains { String(describing: $0).contains("private") })
    }

    // MARK: - Log dimensions

    @Test func theRealPayloadKeepsTheDiagnosticAndProviderDimensions() {
        let identity = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "parakeet-tdt-0.6b-v3", cloudProvider: "groq")
        let result = payload(modeIdentity: identity)

        #expect(result.tags["diagnostic_name"] == "no_speech")
        #expect(result.tags["diagnostic_stage"] == "live_recording")
        #expect(result.tags["diagnostic_source"] == "provider_no_speech")
        #expect(result.tags["provider_type"] == "local")
        #expect(result.tags["local_engine"] == "parakeet-tdt-0.6b-v3")
    }

    @Test func anAbsentModeIsDistinguishableFromABlankOne() {
        let noMode = payload(modeIdentity: nil)
        let blankMode = payload(modeIdentity: TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "", cloudProvider: nil))

        #expect(noMode.tags["provider_type"] == "unknown")
        #expect(blankMode.tags["provider_type"] == "cloud")
    }

    // MARK: - Mode identity derivation

    /// macOS has no `providerType` and no `localEngine` column — both are
    /// Windows-only mode fields. They are derived from `Mode.model` with the same
    /// rule `TranscriptionProviderRouter.selectProvider` routes on.
    @Test func modeIdentityDerivesTheProviderAxisFromTheModelId() {
        let cloud = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "cloud", cloudProvider: "deepgram")
        #expect(cloud.providerType == "cloud")
        #expect(cloud.cloudProvider == "deepgram")
        #expect(cloud.localEngine == nil)

        // Legacy / imported modes with no model id route to cloud, matching
        // `selectProvider`'s own fallback.
        let legacy = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "  ", cloudProvider: "openai")
        #expect(legacy.providerType == "cloud")

        // Case and surrounding whitespace must not flip the routing.
        let shouty = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: " Cloud ", cloudProvider: "openai")
        #expect(shouty.providerType == "cloud")

        let local = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "large-v3-turbo", cloudProvider: "groq")
        #expect(local.providerType == "local")
        #expect(local.localEngine == "large-v3-turbo")
    }

    /// The engine tag is lowercased for the same reason the router lowercases the
    /// model id before `selectLocalProvider`: a non-canonically-cased id from a
    /// hand-edited or cross-platform backup selects the same engine, so it is one
    /// condition and must be one Sentry group.
    @Test func theEngineTagIsCaseInsensitive() {
        let shouty = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: " Parakeet-TDT-0.6B-V3 ", cloudProvider: nil)
        #expect(shouty.providerType == "local")
        #expect(shouty.localEngine == "parakeet-tdt-0.6b-v3")

        func engineTag(rawModel: String) -> String? {
            let identity = TranscriptionDiagnosticsService.modeIdentity(
                rawModel: rawModel, cloudProvider: nil)
            return payload(modeIdentity: identity).tags["local_engine"]
        }
        #expect(engineTag(rawModel: "Parakeet") == engineTag(rawModel: "parakeet"))
    }

    /// The production regression the provider axis exists to fix: two local
    /// modes on the same engine with different leftover cloud vendors are ONE
    /// condition and must have the same query dimensions.
    @Test func twoLocalModesWithDifferentStaleVendorsGroupTogether() {
        func providerTags(cloudProvider: String) -> [String: String] {
            let identity = TranscriptionDiagnosticsService.modeIdentity(
                rawModel: "large-v3-turbo", cloudProvider: cloudProvider)
            let tags = payload(modeIdentity: identity).tags
            return [
                "provider_type": tags["provider_type"]!,
                "cloud_provider": tags["cloud_provider"]!,
                "local_engine": tags["local_engine"]!
            ]
        }

        #expect(providerTags(cloudProvider: "groq") == providerTags(cloudProvider: "gemini"))

        // ...and the cloud_provider tag must not report the stale vendor either.
        let stale = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "large-v3-turbo", cloudProvider: "groq")
        #expect(noSpeechCloudProviderTag(mode: HwModeIdentity(
            providerType: stale.providerType,
            cloudProvider: stale.cloudProvider,
            localEngine: stale.localEngine)) == "none")
    }

    @Test func twoCloudVendorsKeepGroupingSeparately() {
        func cloudProviderTag(cloudProvider: String) -> String? {
            let identity = TranscriptionDiagnosticsService.modeIdentity(
                rawModel: "cloud", cloudProvider: cloudProvider)
            return payload(modeIdentity: identity).tags["cloud_provider"]
        }

        #expect(cloudProviderTag(cloudProvider: "groq") != cloudProviderTag(cloudProvider: "openai"))
    }

    // MARK: - dBFS helpers

    @Test func dbfsConversionAndBucketingComeFromTheCore() {
        #expect(audioToDbfs(linear: 0) == audioMinimumDbfs())
        #expect(audioToDbfs(linear: -1) == audioMinimumDbfs())
        #expect(audioToDbfs(linear: 1.0) == 0.0)

        // Floors, does not truncate: a negative buckets DOWNWARD.
        #expect(audioBucketDbfs(dbfs: -38.2) == "-40dbfs")
        #expect(audioBucketDbfs(dbfs: audioMinimumDbfs()) == "silent")
    }

    @Test func summarizingAnEmptyAccumulationDoesNotDivideByZero() {
        let summary = audioSummarizeSignal(accumulation: HwSignalAccumulation(
            sampleCount: 0, nonSilentCount: 0, sumSquares: 0, peak: 0))
        #expect(summary.peakDbfs == audioMinimumDbfs())
        #expect(summary.rmsDbfs == audioMinimumDbfs())
        #expect(summary.nonSilentRatio == 0)
    }

    @Test func summarizingFullScaleAudioReportsZeroDbfs() {
        let summary = audioSummarizeSignal(accumulation: HwSignalAccumulation(
            sampleCount: 4, nonSilentCount: 4, sumSquares: 4.0, peak: 1.0))
        #expect(summary.peakDbfs == 0.0)
        #expect(summary.rmsDbfs == 0.0)
        #expect(summary.nonSilentRatio == 1.0)
    }
}
