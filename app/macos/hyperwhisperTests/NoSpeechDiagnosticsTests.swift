//
//  NoSpeechDiagnosticsTests.swift
//  hyperwhisperTests
//
//  Issue #291: macOS's no-speech diagnostic used to be a hand-mirrored copy of
//  the Windows one and had drifted a whole classification arm and two
//  fingerprint elements. It now delegates to the shared `hw-audio` core. These
//  tests pin the parts that are deliberately NOT shared (the macOS Sentry
//  identity) and the parts that must now match Windows exactly (the arms, the
//  thresholds, the fingerprint shape).
//

import Testing
@testable import HyperWhisper

struct NoSpeechDiagnosticsTests {

    // MARK: - Helpers

    private func audio(
        analysisSucceeded: Bool = true,
        decodedSampleCount: Int? = 48_000,
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
            decodedSampleCount: decodedSampleCount
        )
    }

    // MARK: - Sentry identity (deliberately NOT shared with Windows)

    /// The messages and the fingerprint roots are macOS group identity. Sharing
    /// the classifier must never have shared these — a Windows root here would
    /// merge macOS events into eight live Windows issues.
    @Test func macOSKeepsItsOwnMessagesAndFingerprintRoots() {
        let noSpeech = TranscriptionDiagnosticsService.presentation(for: .noSpeech)
        #expect(noSpeech?.name == "no_speech")
        #expect(noSpeech?.message == "macOS transcription no-speech diagnostic")
        #expect(noSpeech?.fingerprintRoot == "macos-transcription-no-speech")

        let empty = TranscriptionDiagnosticsService.presentation(for: .emptyRecording)
        #expect(empty?.name == "empty_recording")
        #expect(empty?.message == "macOS transcription empty recording diagnostic")
        #expect(empty?.fingerprintRoot == "macos-transcription-empty-recording")

        // Skip is filtered out before anything is reported, so it has no
        // presentation by design.
        #expect(TranscriptionDiagnosticsService.presentation(for: .skip) == nil)
    }

    /// Every reportable outcome needs its own arm, its own name and its own root
    /// — a new outcome that copies an existing identity is the mislabelling this
    /// diagnostic exists to fix.
    @Test func everyReportableOutcomeHasAUniqueIdentity() {
        var names = Set<String>()
        var roots = Set<String>()

        for outcome in [HwNoSpeechOutcome.skip, .emptyRecording, .noSpeech] {
            guard let presentation = TranscriptionDiagnosticsService.presentation(for: outcome) else {
                continue
            }
            #expect(names.insert(presentation.name).inserted,
                    "duplicate diagnostic name '\(presentation.name)'")
            #expect(roots.insert(presentation.fingerprintRoot).inserted,
                    "duplicate fingerprint root '\(presentation.fingerprintRoot)'")
            #expect(!presentation.message.isEmpty)
            #expect(presentation.fingerprintRoot.hasPrefix("macos-"),
                    "a macOS root must stay macOS-scoped, got '\(presentation.fingerprintRoot)'")
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

    // MARK: - Fingerprint (shape shared, root not)

    @Test func theFingerprintHasFiveElementsAndKeepsTheMacOSRoot() {
        let identity = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "parakeet-tdt-0.6b-v3", cloudProvider: "groq")
        let fingerprint = noSpeechFingerprint(
            fingerprintRoot: "macos-transcription-no-speech",
            diagnosticStage: "live_recording",
            diagnosticSource: "provider_no_speech",
            mode: HwModeIdentity(
                providerType: identity.providerType,
                cloudProvider: identity.cloudProvider,
                localEngine: identity.localEngine))

        #expect(fingerprint.count == 5)
        #expect(fingerprint[0] == "macos-transcription-no-speech")
        #expect(fingerprint[1] == "live_recording")
        #expect(fingerprint[2] == "provider_no_speech")
        #expect(fingerprint[3] == "local")
        // A local mode groups on its engine, never on the stale cloud vendor it
        // kept from before the user switched it to local.
        #expect(fingerprint[4] == "parakeet-tdt-0.6b-v3")
    }

    @Test func anAbsentModeIsDistinguishableFromABlankOne() {
        let noMode = noSpeechFingerprint(
            fingerprintRoot: "macos-transcription-no-speech",
            diagnosticStage: "live_recording",
            diagnosticSource: "provider_no_speech",
            mode: nil)
        #expect(noMode[3] == "unknown")
        #expect(noMode[4] == "none")
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

    /// The production regression the provider axis exists to fix: two local
    /// modes on the same engine with different leftover cloud vendors are ONE
    /// condition and must be one Sentry group.
    @Test func twoLocalModesWithDifferentStaleVendorsGroupTogether() {
        func fingerprint(cloudProvider: String) -> String {
            let identity = TranscriptionDiagnosticsService.modeIdentity(
                rawModel: "large-v3-turbo", cloudProvider: cloudProvider)
            return noSpeechFingerprint(
                fingerprintRoot: "macos-transcription-no-speech",
                diagnosticStage: "live_recording",
                diagnosticSource: "provider_no_speech",
                mode: HwModeIdentity(
                    providerType: identity.providerType,
                    cloudProvider: identity.cloudProvider,
                    localEngine: identity.localEngine)).joined(separator: "|")
        }

        #expect(fingerprint(cloudProvider: "groq") == fingerprint(cloudProvider: "gemini"))

        // ...and the cloud_provider tag must not report the stale vendor either.
        let stale = TranscriptionDiagnosticsService.modeIdentity(
            rawModel: "large-v3-turbo", cloudProvider: "groq")
        #expect(noSpeechCloudProviderTag(mode: HwModeIdentity(
            providerType: stale.providerType,
            cloudProvider: stale.cloudProvider,
            localEngine: stale.localEngine)) == "none")
    }

    @Test func twoCloudVendorsKeepGroupingSeparately() {
        func fingerprint(cloudProvider: String) -> String {
            let identity = TranscriptionDiagnosticsService.modeIdentity(
                rawModel: "cloud", cloudProvider: cloudProvider)
            return noSpeechFingerprint(
                fingerprintRoot: "macos-transcription-no-speech",
                diagnosticStage: "live_recording",
                diagnosticSource: "provider_no_speech",
                mode: HwModeIdentity(
                    providerType: identity.providerType,
                    cloudProvider: identity.cloudProvider,
                    localEngine: identity.localEngine)).joined(separator: "|")
        }

        #expect(fingerprint(cloudProvider: "groq") != fingerprint(cloudProvider: "openai"))
    }

    // MARK: - dBFS helpers

    @Test func dbfsConversionAndBucketingComeFromTheCore() {
        #expect(audioToDbfs(0) == audioMinimumDbfs())
        #expect(audioToDbfs(-1) == audioMinimumDbfs())
        #expect(audioToDbfs(1.0) == 0.0)

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
