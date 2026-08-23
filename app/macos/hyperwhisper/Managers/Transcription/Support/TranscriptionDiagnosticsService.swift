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
//  THRESHOLD PARITY:
//  =================
//  Every threshold mirrors the Windows `TranscriptionDiagnosticsService`
//  (`app/windows/HyperWhisper/Services/TranscriptionDiagnosticsService.cs`),
//  which tuned them against the real HYPERWHISPER-PA/-QB/-VY samples. Keeping
//  them identical is what makes the two platforms comparable in Sentry.
//

import Foundation
import AVFoundation

// MARK: - Outcome

/// What a no-speech failure is reported as, if anything.
enum NoSpeechDiagnosticOutcome {
    /// Expected/benign — capture nothing.
    case skip

    /// Nothing was decoded at all — a recorder failure, reported separately.
    case emptyRecording

    /// Audio exists but produced no transcript — the original diagnostic.
    case noSpeech
}

// MARK: - Audio analysis

/// Privacy-safe signal measurements. Holds no transcript text and no audio.
struct AudioAnalysisDiagnostics {
    let analysisSucceeded: Bool
    let durationSeconds: Double
    let fileSizeBytes: Int64
    var sampleRate: Double?
    var channels: Int?
    var peakDbfs: Double = TranscriptionDiagnosticsService.minimumDbfs
    var rmsDbfs: Double = TranscriptionDiagnosticsService.minimumDbfs
    var nonSilentRatio: Double = 0
    /// nil = unknown (no decode loop ran). Deliberately NOT treated as empty.
    var decodedSampleCount: Int?
    var analysisError: String?
}

// MARK: - Service

enum TranscriptionDiagnosticsService {

    // MARK: Thresholds (parity with Windows — see file header)

    /// Absolute amplitude at or above which a sample counts as non-silent.
    static let silenceThreshold: Float = 0.01

    /// dBFS floor reported for digital silence.
    static let minimumDbfs: Double = -120.0

    /// Below this peak, with a zero non-silent ratio, the clip is confirmed dead silence.
    static let confirmedSilencePeakDbfs: Double = -50.0

    /// Backend-confirmed low-signal skip. BOTH must hold — an OR would let a
    /// single quiet reading suppress a genuine provider-disagreement anomaly.
    static let lowSignalRmsDbfs: Double = -38.0
    static let lowSignalNonSilentRatio: Double = 0.06

    // MARK: Presentation

    /// The Sentry message, tag name and fingerprint root for an outcome. They
    /// travel together so an outcome can never be reported under another
    /// outcome's identity.
    struct DiagnosticPresentation {
        let name: String
        let message: String
        let fingerprintRoot: String
    }

    /// NOTE: these messages are deliberately NOT the Windows strings. The
    /// Windows message is the group identity for eight live issues
    /// (HYPERWHISPER-PA/-QB/-RM/-T6/-VY/-XB/-XR/-W7); reusing it would merge
    /// macOS events into those Windows groups and destroy the per-platform
    /// signal this exists to create.
    static func presentation(for outcome: NoSpeechDiagnosticOutcome) -> DiagnosticPresentation? {
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

    /// Decide what to report. Arm order matters — see the inline notes.
    static func classify(
        _ audio: AudioAnalysisDiagnostics,
        backendNoSpeechDetected: Bool
    ) -> NoSpeechDiagnosticOutcome {
        // MUST stay first: with no usable analysis we can't tell an empty
        // recording from a quiet one, so fall back to the full report.
        guard audio.analysisSucceeded else { return .noSpeech }

        // A zero-sample decode means the recorder produced nothing, which is a
        // different fault from "we recorded audio and got no words back". The
        // discriminator is the decoded sample count, NOT the duration: the
        // caller's wall-clock value is used as a fallback duration, so a
        // header-only file from a 9-second recording still reports 9 seconds.
        if audio.decodedSampleCount == 0 { return .emptyRecording }

        // Confirmed dead silence — the user recorded nothing. Always benign.
        if audio.nonSilentRatio == 0 && audio.peakDbfs < confirmedSilencePeakDbfs {
            return .skip
        }

        // The provider said "no speech" and the signal agrees. Benign.
        if backendNoSpeechDetected
            && audio.nonSilentRatio <= lowSignalNonSilentRatio
            && audio.rmsDbfs <= lowSignalRmsDbfs {
            return .skip
        }

        return .noSpeech
    }

    // MARK: Capture

    /// Measure the audio behind a `.noSpeechDetected` failure and report it if
    /// the signal contradicts the result. Safe to call unconditionally — it
    /// no-ops when error logging is off or the outcome is `.skip`.
    ///
    /// - Parameters:
    ///   - micBoostFailed: `RecordingLifecycle.lastMicBoostFailed`. A quiet
    ///     recording caused by a failed auto-boost is a capture-quality defect,
    ///     not the user staying silent, so it rides along as a tag.
    static func captureNoSpeechDiagnostic(
        audioURL: URL,
        fallbackDurationSeconds: Double,
        mode: String,
        diagnosticStage: String,
        diagnosticSource: String,
        error: Error,
        inputDeviceName: String? = nil,
        micBoostFailed: Bool = false
    ) async {
        guard AppLogger.isErrorLoggingEnabled else { return }

        let audio = await analyzeAudioFile(
            at: audioURL,
            fallbackDurationSeconds: fallbackDurationSeconds
        )

        // Every call site here is a provider-reported no-speech, matching the
        // Windows `BackendNoSpeechDetected` semantics.
        let outcome = classify(audio, backendNoSpeechDetected: true)

        guard let presentation = presentation(for: outcome) else {
            AppLogger.audio.debug(
                "Skipping expected no-speech diagnostic (stage=\(diagnosticStage, privacy: .public), rms=\(audio.rmsDbfs), ratio=\(audio.nonSilentRatio))"
            )
            return
        }

        var tags: [String: String] = [
            "component": "transcription",
            "diagnostic_name": presentation.name,
            "diagnostic_stage": diagnosticStage,
            "diagnostic_source": diagnosticSource,
            "audio_analysis_succeeded": audio.analysisSucceeded ? "true" : "false",
            // Bucketed to 5 dB steps on purpose: a raw float as a tag has
            // near-100% cardinality, which defeats faceting entirely.
            "audio_rms_dbfs_bucket": bucketDbfs(audio.rmsDbfs),
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
            "mic_boost_failed": micBoostFailed
        ]
        if let sampleRate = audio.sampleRate { extras["audio_sample_rate_hz"] = sampleRate }
        if let channels = audio.channels { extras["audio_channels"] = channels }
        if let analysisError = audio.analysisError { extras["audio_analysis_error"] = analysisError }

        SentryService.capture(
            error: error,
            message: presentation.message,
            extras: extras,
            tags: tags,
            fingerprint: [presentation.fingerprintRoot, diagnosticStage, diagnosticSource]
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
                targetSampleRate: 16000.0,
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

        var peak: Float = 0
        var sumSquares: Double = 0
        var nonSilentCount = 0
        for sample in samples {
            let amplitude = abs(sample)
            if amplitude > peak { peak = amplitude }
            sumSquares += Double(amplitude) * Double(amplitude)
            if amplitude >= silenceThreshold { nonSilentCount += 1 }
        }

        let count = samples.count
        let rms = count > 0 ? sqrt(sumSquares / Double(count)) : 0
        let ratio = count > 0 ? Double(nonSilentCount) / Double(count) : 0
        let decodedDuration = Double(count) / 16000.0
        let duration = containerDuration > 0
            ? containerDuration
            : (decodedDuration > 0 ? decodedDuration : fallbackDurationSeconds)

        return AudioAnalysisDiagnostics(
            analysisSucceeded: true,
            durationSeconds: (duration * 1000).rounded() / 1000,
            fileSizeBytes: fileSize,
            sampleRate: sourceSampleRate,
            channels: sourceChannels,
            peakDbfs: toDbfs(Double(peak)),
            rmsDbfs: toDbfs(rms),
            nonSilentRatio: (ratio * 10000).rounded() / 10000,
            decodedSampleCount: count
        )
    }

    // MARK: Helpers

    static func toDbfs(_ linear: Double) -> Double {
        guard linear > 0 else { return minimumDbfs }
        return (20 * log10(linear) * 100).rounded() / 100
    }

    /// Bucket a dBFS value to the nearest 5 dB step for a low-cardinality tag.
    static func bucketDbfs(_ dbfs: Double) -> String {
        guard dbfs > minimumDbfs else { return "silent" }
        let bucket = Int((dbfs / 5.0).rounded(.down) * 5.0)
        return "\(bucket)dbfs"
    }
}
