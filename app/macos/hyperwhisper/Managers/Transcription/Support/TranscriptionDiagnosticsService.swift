//
//  TranscriptionDiagnosticsService.swift
//  hyperwhisper
//
//  Privacy-safe Sentry diagnostics for `TranscriptionError.noSpeechDetected`.
//
//  WHY THIS EXISTS:
//  ================
//  `TranscriptionPipeline+ErrorClassification` deliberately excludes
//  `.noSpeechDetected` from the blanket Sentry capture as "user-recoverable"
//  (a user who says nothing is not a defect). That exclusion also hid the
//  opposite case: a recording with healthy speech energy that a provider
//  returned an empty transcript for. On Windows that cohort is real and large
//  — 57 backend-confirmed events over 90 days, median peak -18.47 dBFS —
//  spread across Deepgram and ElevenLabs, so it is not vendor-specific.
//  macOS had no equivalent signal at all.
//
//  This service is the narrow reporting path: it measures the audio and
//  reports ONLY when the signal contradicts "no speech". Genuine silence is
//  still skipped, so the blanket exclusion stays in place and no duplicate
//  event is produced for the common case.
//
//  THRESHOLD PARITY (issue #291):
//  ==============================
//  The thresholds, the dBFS maths, the five classification arms and the
//  fingerprint shape are no longer mirrored by hand from the Windows
//  `TranscriptionDiagnosticsService` — they live in the shared Rust core
//  (`shared-core-rs/crates/hw-audio`) and both platforms call it. This file is
//  the macOS shim: it owns the decode loop (it already owns `AudioConverter`),
//  the Sentry payload, and the deliberately platform-distinct diagnostic
//  name / message / fingerprint root.
//

import Foundation
import AVFoundation
import CoreData

// MARK: - Mode identity

/// The three mode facts the shared fingerprint groups on.
///
/// A value type, and explicitly `Sendable`, because the capture runs on a
/// detached task: the Core Data `Mode` behind it must never cross off the main
/// actor. Build it with ``TranscriptionDiagnosticsService/modeIdentity(for:)``
/// while still on the main actor, then hand the value over.
///
/// NOTE: macOS has no `providerType` and no `localEngine` column — both are
/// Windows-only mode fields (`shared-backup/AGENTS.md` lists them under
/// `platformExtensions.windows`). The two macOS values below are derived from
/// the field macOS actually dispatches on, `Mode.model`, using the same rule
/// `TranscriptionProviderRouter.selectProvider(for:vocabulary:)` uses.
struct NoSpeechModeIdentity: Sendable, Equatable {
    let providerType: String?
    let cloudProvider: String?
    let localEngine: String?
}

// MARK: - Audio analysis

/// Privacy-safe signal measurements. Holds no transcript text and no audio.
struct AudioAnalysisDiagnostics {
    let analysisSucceeded: Bool
    let durationSeconds: Double
    let fileSizeBytes: Int64
    var sampleRate: Double?
    var channels: Int?
    var peakDbfs: Double = audioMinimumDbfs()
    var rmsDbfs: Double = audioMinimumDbfs()
    var nonSilentRatio: Double = 0
    /// nil = unknown (no decode loop ran). Deliberately NOT treated as empty.
    var decodedSampleCount: Int?
    var analysisError: String?
}

// MARK: - Service

enum TranscriptionDiagnosticsService {

    /// The decode target. Also the divisor for `decodedDuration` below.
    private static let analysisSampleRate: Double = 16000.0

    // MARK: Presentation

    /// The Sentry message, tag name and fingerprint root for an outcome. They
    /// travel together so an outcome can never be reported under another
    /// outcome's identity.
    struct DiagnosticPresentation {
        let name: String
        let message: String
        let fingerprintRoot: String
    }

    /// NOTE: these messages and fingerprint roots are deliberately NOT the
    /// Windows strings, and #291's shared classifier deliberately does not
    /// unify them. The Windows message is the group identity for eight live
    /// issues (HYPERWHISPER-PA/-QB/-RM/-T6/-VY/-XB/-XR/-W7); reusing either
    /// would merge macOS events into those Windows groups and destroy the
    /// per-platform signal this exists to create. Only the fingerprint SHAPE is
    /// shared. Both strings are group identity here too — keep them
    /// character-identical.
    static func presentation(for outcome: HwNoSpeechOutcome) -> DiagnosticPresentation? {
        switch outcome {
        case .skip:
            return nil
        case .noSpeech:
            return DiagnosticPresentation(
                name: "no_speech",
                message: "macOS transcription no-speech diagnostic",
                fingerprintRoot: "macos-transcription-no-speech")
        case .emptyRecording:
            return DiagnosticPresentation(
                name: "empty_recording",
                message: "macOS transcription empty recording diagnostic",
                fingerprintRoot: "macos-transcription-empty-recording")
        }
    }

    // MARK: Classification

    /// Decide what to report. The arms, their order and their thresholds are the
    /// core's (`hw_audio::no_speech::classify`) — this used to be a hand-mirrored
    /// copy of the Windows shape that had drifted a whole arm.
    ///
    /// - Parameters:
    ///   - backendNoSpeechDetected: what the provider actually reported. Supplied
    ///     by the caller, never assumed here.
    ///   - emptyTranscriptWithoutFlag: the provider returned an empty transcript
    ///     *without* setting its no-speech flag — a provider anomaly, reported
    ///     whatever the signal looks like. This is arm 3, which macOS never had.
    ///     See ``captureNoSpeechDiagnostic(audioURL:fallbackDurationSeconds:mode:modeIdentity:diagnosticStage:diagnosticSource:error:backendNoSpeechDetected:emptyTranscriptWithoutFlag:inputDeviceName:micBoostFailed:)``
    ///     for why it is not reachable on macOS today.
    static func classify(
        _ audio: AudioAnalysisDiagnostics,
        backendNoSpeechDetected: Bool,
        emptyTranscriptWithoutFlag: Bool = false
    ) -> HwNoSpeechOutcome {
        noSpeechClassify(input: HwNoSpeechInput(
            analysisSucceeded: audio.analysisSucceeded,
            // A negative count cannot come from a decode loop; it takes the
            // "unknown" answer rather than wrapping into an enormous one.
            decodedSampleCount: audio.decodedSampleCount.flatMap { (count: Int) -> UInt64? in
                count >= 0 ? UInt64(count) : nil
            },
            emptyTranscriptWithoutFlag: emptyTranscriptWithoutFlag,
            backendNoSpeechDetected: backendNoSpeechDetected,
            peakDbfs: audio.peakDbfs,
            rmsDbfs: audio.rmsDbfs,
            nonSilentRatio: audio.nonSilentRatio
        ))
    }

    // MARK: Mode identity

    /// Snapshot the mode facts the fingerprint groups on, on the main actor,
    /// before the capture detaches.
    ///
    /// `nil` in, `nil` out: "no mode at all" is a different fact from "a mode
    /// with nothing written on it", and the core fingerprints them differently.
    @MainActor
    static func modeIdentity(for mode: Mode?) -> NoSpeechModeIdentity? {
        guard let mode else { return nil }
        return modeIdentity(rawModel: mode.model, cloudProvider: mode.cloudProvider)
    }

    /// The pure half of ``modeIdentity(for:)``, over the two Core Data fields it
    /// reads. Split out so it can be tested without a managed object context.
    ///
    /// The local/cloud rule is copied from
    /// `TranscriptionProviderRouter.selectProvider(for:vocabulary:)`: an empty
    /// model id means cloud (legacy/imported modes), the literal `"cloud"` means
    /// cloud, and everything else routes to a local engine. `localEngine` is
    /// `Mode.model` itself, because that is the id `selectLocalProvider` picks
    /// the engine from — macOS has no separate engine column the way Windows
    /// does, so this is the closest honest analogue rather than a constant.
    ///
    /// It is lowercased for the same reason the router lowercases it before
    /// `selectLocalProvider`: a non-canonically-cased id from a hand-edited or
    /// cross-platform backup reaches this code, and it selects the same engine
    /// whatever its casing. Reporting the raw casing would split one condition
    /// into two Sentry groups — `"Parakeet"` and `"parakeet"` are one engine.
    static func modeIdentity(rawModel: String?, cloudProvider: String?) -> NoSpeechModeIdentity {
        let trimmed = (rawModel ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        let isCloud = trimmed.isEmpty || trimmed == "cloud"
        return NoSpeechModeIdentity(
            providerType: isCloud ? "cloud" : "local",
            cloudProvider: cloudProvider,
            // A cloud mode has no local engine; leaving the model id here would
            // put "cloud" in the engine tag for every cloud event.
            localEngine: isCloud ? nil : trimmed
        )
    }

    private static func coreIdentity(_ identity: NoSpeechModeIdentity?) -> HwModeIdentity? {
        guard let identity else { return nil }
        return HwModeIdentity(
            providerType: identity.providerType,
            cloudProvider: identity.cloudProvider,
            localEngine: identity.localEngine
        )
    }

    // MARK: Capture

    /// Measure the audio behind a `.noSpeechDetected` failure and report it if
    /// the signal contradicts the result. Safe to call unconditionally — it
    /// no-ops when error logging is off or the outcome is `.skip`.
    ///
    /// - Parameters:
    ///   - modeIdentity: snapshot from ``modeIdentity(for:)``, taken on the main
    ///     actor by the caller. `nil` when the mode could not be resolved.
    ///   - backendNoSpeechDetected: whether the provider itself reported
    ///     no-speech. Every current caller passes `true`, and that is honest:
    ///     the only entry point is `TranscriptionError.noSpeechDetected`, which
    ///     is exactly the provider's own no-speech signal. It is a parameter
    ///     rather than a literal here so the value comes from the call site that
    ///     knows it, not from the classifier.
    ///   - emptyTranscriptWithoutFlag: **currently unreachable on macOS.**
    ///     `TranscriptionError.noSpeechDetected` is a case with no associated
    ///     values, and both producers collapse into it — `RustRetry` maps the
    ///     provider's `.NoSpeech` to it, and `LibWhisperProvider` throws it for
    ///     an empty local transcript — so nothing downstream can tell "the
    ///     backend said no-speech" from "the transcript was empty and no flag was
    ///     set". Arm 3 therefore never fires here. Widening the error case to
    ///     carry the flag is a transport change on a shipped path and is out of
    ///     scope for #291; the parameter exists so that when the transport does
    ///     carry it, this is a one-line call-site change and not another
    ///     divergence from the shared classifier.
    ///   - micBoostFailed: `RecordingLifecycle.lastMicBoostFailed`. A quiet
    ///     recording caused by a failed auto-boost is a capture-quality defect,
    ///     not the user staying silent, so it rides along as a tag.
    static func captureNoSpeechDiagnostic(
        audioURL: URL,
        fallbackDurationSeconds: Double,
        mode: String,
        modeIdentity: NoSpeechModeIdentity?,
        diagnosticStage: String,
        diagnosticSource: String,
        error: Error,
        backendNoSpeechDetected: Bool,
        emptyTranscriptWithoutFlag: Bool = false,
        inputDeviceName: String? = nil,
        micBoostFailed: Bool = false
    ) async {
        guard AppLogger.isErrorLoggingEnabled else { return }

        let audio = await analyzeAudioFile(
            at: audioURL,
            fallbackDurationSeconds: fallbackDurationSeconds
        )

        let outcome = classify(
            audio,
            backendNoSpeechDetected: backendNoSpeechDetected,
            emptyTranscriptWithoutFlag: emptyTranscriptWithoutFlag
        )

        guard let presentation = presentation(for: outcome) else {
            AppLogger.audio.debug(
                "Skipping expected no-speech diagnostic (stage=\(diagnosticStage, privacy: .public), rms=\(audio.rmsDbfs), ratio=\(audio.nonSilentRatio))"
            )
            return
        }

        let coreMode = coreIdentity(modeIdentity)

        var tags: [String: String] = [
            "component": "transcription",
            "diagnostic_name": presentation.name,
            "diagnostic_stage": diagnosticStage,
            "diagnostic_source": diagnosticSource,
            // The provider axis, matching the Windows tag set so the two
            // platforms stay comparable in Sentry. The cloud/engine tags are the
            // core's, which masks a local mode's stale cloud vendor off.
            "provider_type": modeIdentity?.providerType ?? "unknown",
            "cloud_provider": noSpeechCloudProviderTag(mode: coreMode),
            "local_engine": noSpeechLocalEngineTag(mode: coreMode),
            "backend_no_speech_detected": backendNoSpeechDetected ? "true" : "false",
            "audio_analysis_succeeded": audio.analysisSucceeded ? "true" : "false",
            // Bucketed to 5 dB steps on purpose: a raw float as a tag has
            // near-100% cardinality, which defeats faceting entirely.
            "audio_rms_dbfs_bucket": audioBucketDbfs(dbfs: audio.rmsDbfs),
            "mic_boost_failed": micBoostFailed ? "true" : "false"
        ]
        if let inputDeviceName {
            tags["selected_input_device_name"] = inputDeviceName
        }

        var extras: [String: Any] = [
            "audio_file_exists": FileManager.default.fileExists(atPath: audioURL.path),
            "audio_file_extension": audioURL.pathExtension,
            "audio_file_size_bytes": audio.fileSizeBytes,
            "audio_duration_seconds": audio.durationSeconds,
            "audio_peak_dbfs": audio.peakDbfs,
            "audio_rms_dbfs": audio.rmsDbfs,
            "audio_non_silent_ratio": audio.nonSilentRatio,
            // The honest "was anything captured" signal — audio_duration_seconds
            // above falls back to the caller's wall-clock value.
            "audio_decoded_sample_count": audio.decodedSampleCount.map { String($0) } ?? "unknown",
            "mode_name": mode,
            "backend_empty_transcript_without_flag": emptyTranscriptWithoutFlag,
            "mic_boost_failed": micBoostFailed
        ]
        // The SOURCE container's format, not the measurement basis (16 kHz mono).
        if let sampleRate = audio.sampleRate { extras["audio_sample_rate_hz"] = sampleRate }
        if let channels = audio.channels { extras["audio_channels"] = channels }
        if let analysisError = audio.analysisError { extras["audio_analysis_error"] = analysisError }

        SentryService.capture(
            error: error,
            message: presentation.message,
            extras: extras,
            tags: tags,
            // Five elements, from the shared builder. This was three, so every
            // existing macOS no-speech issue re-groups once — an accepted,
            // one-time cost of gaining the provider axis Windows already had.
            fingerprint: noSpeechFingerprint(
                fingerprintRoot: presentation.fingerprintRoot,
                diagnosticStage: diagnosticStage,
                diagnosticSource: diagnosticSource,
                mode: coreMode
            )
        )
    }

    // MARK: Measurement

    /// Decode to 16 kHz mono Float32 and measure peak, RMS and non-silent ratio.
    /// PRIVACY: reads sample amplitudes only. No audio and no text leaves here.
    static func analyzeAudioFile(
        at audioURL: URL,
        fallbackDurationSeconds: Double
    ) async -> AudioAnalysisDiagnostics {
        let fileSize = (try? FileManager.default.attributesOfItem(atPath: audioURL.path)[.size] as? Int64) ?? 0

        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            return AudioAnalysisDiagnostics(
                analysisSucceeded: false,
                durationSeconds: fallbackDurationSeconds,
                fileSizeBytes: 0,
                analysisError: "Audio file not found"
            )
        }

        // Container metadata first — cheap, and it survives a decode failure.
        var sourceSampleRate: Double?
        var sourceChannels: Int?
        var containerDuration: Double = 0
        if let file = try? AVAudioFile(forReading: audioURL) {
            sourceSampleRate = file.fileFormat.sampleRate
            sourceChannels = Int(file.fileFormat.channelCount)
            if file.fileFormat.sampleRate > 0 {
                containerDuration = Double(file.length) / file.fileFormat.sampleRate
            }
        }

        let samples: [Float]
        do {
            // `failOnPartialRead: false` on purpose: a truncated decode still
            // measures real signal, and a hard throw here would report the
            // less specific `analysisSucceeded: false` instead.
            let converter = AudioConverter()
            let options = AudioConverter.ConversionOptions(
                chunkSize: 32768,
                targetSampleRate: analysisSampleRate,
                normalize: false,
                progressHandler: nil
            )
            samples = try await converter.convert(from: audioURL, options: options)
        } catch {
            return AudioAnalysisDiagnostics(
                analysisSucceeded: false,
                durationSeconds: containerDuration > 0 ? containerDuration : fallbackDurationSeconds,
                fileSizeBytes: fileSize,
                sampleRate: sourceSampleRate,
                channels: sourceChannels,
                analysisError: error.localizedDescription
            )
        }

        // Read the threshold once, outside the loop: every core call crosses the
        // FFI boundary, so reading it per sample would be one call per sample.
        let threshold = audioSilenceThreshold()
        var peak: Float = 0
        var sumSquares: Double = 0
        var nonSilentCount: UInt64 = 0
        for sample in samples {
            let amplitude = abs(sample)
            if amplitude > peak { peak = amplitude }
            sumSquares += Double(amplitude) * Double(amplitude)
            if amplitude >= threshold { nonSilentCount += 1 }
        }

        let count = samples.count
        // dBFS conversion, RMS and the ratio rounding are the core's — see
        // `hw_audio::no_speech::summarize`.
        let summary = audioSummarizeSignal(accumulation: HwSignalAccumulation(
            sampleCount: UInt64(count),
            nonSilentCount: nonSilentCount,
            sumSquares: sumSquares,
            peak: Double(peak)
        ))

        let decodedDuration = Double(count) / analysisSampleRate
        let duration = containerDuration > 0
            ? containerDuration
            : (decodedDuration > 0 ? decodedDuration : fallbackDurationSeconds)

        return AudioAnalysisDiagnostics(
            analysisSucceeded: true,
            durationSeconds: (duration * 1000).rounded() / 1000,
            fileSizeBytes: fileSize,
            sampleRate: sourceSampleRate,
            channels: sourceChannels,
            peakDbfs: summary.peakDbfs,
            rmsDbfs: summary.rmsDbfs,
            nonSilentRatio: summary.nonSilentRatio,
            decodedSampleCount: count
        )
    }
}
