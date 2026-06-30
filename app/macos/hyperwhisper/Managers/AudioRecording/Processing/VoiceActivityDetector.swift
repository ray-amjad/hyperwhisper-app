//
//  VoiceActivityDetector.swift
//  hyperwhisper
//
//  Voice Activity Detection using whisper.cpp's standalone Silero VAD API.
//  Detects speech segments in audio to enable silence trimming before transcription.
//
//  VAD FLOW OVERVIEW:
//  ==================
//  1. Load Silero VAD model (ggml-silero-v5.1.2.bin) via whisper_vad_init_from_file_with_params
//  2. Analyze audio samples using whisper_vad_segments_from_samples
//  3. Get speech segment timestamps (start/end times)
//  4. Use segments to trim silence from audio
//
//  WHISPER.CPP VAD API:
//  ====================
//  - whisper_vad_default_params() - Get default VAD parameters
//  - whisper_vad_init_from_file_with_params() - Load VAD model
//  - whisper_vad_segments_from_samples() - Analyze audio and get speech segments
//  - whisper_vad_segments_n_segments() - Get number of segments
//  - whisper_vad_segments_get_segment_t0() - Get segment start time
//  - whisper_vad_segments_get_segment_t1() - Get segment end time
//  - whisper_vad_free_segments() - Free segments memory
//  - whisper_vad_free() - Free VAD context
//

import Foundation
import os

// MARK: - TimeRange

/// Represents a time range in seconds
struct TimeRange: Equatable, CustomStringConvertible {
    let start: Float
    let end: Float

    var duration: Float { end - start }

    var description: String {
        String(format: "%.2fs - %.2fs (%.2fs)", start, end, duration)
    }
}

// MARK: - VADResult

/// Result of voice activity detection analysis
struct VADResult {
    /// Whether any speech was detected
    let hasSpeech: Bool

    /// Total duration of detected speech segments
    let speechDuration: TimeInterval

    /// Total duration of silence (non-speech)
    let silenceDuration: TimeInterval

    /// Detected speech segments with start/end times
    let segments: [TimeRange]

    /// Total audio duration analyzed
    var totalDuration: TimeInterval {
        speechDuration + silenceDuration
    }

    /// Percentage of audio that is speech
    var speechPercentage: Double {
        guard totalDuration > 0 else { return 0 }
        return speechDuration / totalDuration * 100
    }
}

// MARK: - VADError

/// Errors that can occur during VAD operations
enum VADError: Error, LocalizedError {
    case modelNotFound
    case modelLoadFailed
    case analysisFailedInvalidSamples
    case analysisFailedContextNotReady
    case segmentExtractionFailed

    var errorDescription: String? {
        switch self {
        case .modelNotFound:
            return "VAD model file not found in app bundle"
        case .modelLoadFailed:
            return "Failed to load VAD model"
        case .analysisFailedInvalidSamples:
            return "VAD analysis failed: invalid audio samples"
        case .analysisFailedContextNotReady:
            return "VAD analysis failed: context not initialized"
        case .segmentExtractionFailed:
            return "Failed to extract speech segments"
        }
    }
}

// MARK: - VoiceActivityDetector

/// Voice Activity Detection using whisper.cpp's Silero VAD.
///
/// THREAD SAFETY:
/// Uses an actor to ensure thread-safe access to the VAD context.
/// The whisper.cpp VAD API is not thread-safe, so all operations
/// are serialized through the actor.
///
/// MEMORY MANAGEMENT:
/// - VAD context is loaded once and reused
/// - Segments are freed after each analysis
/// - Context is freed when actor is deallocated
///
/// USAGE:
/// ```swift
/// let vad = VoiceActivityDetector.shared
/// try await vad.loadModel()
/// let result = try await vad.analyzeAudio(samples: audioSamples)
/// // result.segments contains speech time ranges
/// ```
actor VoiceActivityDetector {

    // MARK: - Singleton

    /// Shared instance for app-wide access
    static let shared = VoiceActivityDetector()

    // MARK: - Properties

    /// The underlying whisper VAD context pointer
    private var context: OpaquePointer?

    /// Logger for debugging
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "VoiceActivityDetector")

    // MARK: - VAD Parameters

    /// Voice probability threshold (0.0 - 1.0)
    /// Higher = more strict, fewer false positives
    /// Default: 0.50
    var threshold: Float = 0.50

    /// Minimum duration for a valid speech segment (milliseconds)
    /// Segments shorter than this are ignored
    /// Default: 250ms
    var minSpeechDurationMs: Int32 = 250

    /// Minimum silence duration to consider speech as ended (milliseconds)
    /// Default: 100ms
    var minSilenceDurationMs: Int32 = 100

    /// Maximum duration of a speech segment before forcing a new segment (seconds)
    /// Default: Float.greatestFiniteMagnitude (unlimited)
    var maxSpeechDurationS: Float = Float.greatestFiniteMagnitude

    /// Padding added before and after speech segments (milliseconds)
    /// Helps avoid clipping the start/end of speech
    /// Default: 30ms
    var speechPadMs: Int32 = 30

    /// Overlap in seconds when copying audio samples from speech segment
    /// Default: 0.1 (100ms)
    var samplesOverlap: Float = 0.1

    // MARK: - Initialization

    /// Private initializer to enforce singleton pattern
    private init() {}

    deinit {
        // Free VAD context when actor is deallocated
        if let context = context {
            whisper_vad_free(context)
        }
    }

    // MARK: - Model Loading

    /// Load the VAD model from the app bundle.
    ///
    /// MODEL LOADING FLOW:
    /// 1. Get model path from VADModelManager
    /// 2. Configure VAD context parameters
    /// 3. Load model via whisper_vad_init_from_file_with_params
    /// 4. Store context for reuse
    ///
    /// IMPORTANT: Call this before using analyzeAudio().
    /// The model is loaded once and reused for all analyses.
    ///
    /// - Throws: VADError if model cannot be loaded
    func loadModel() async throws {
        // Skip if already loaded
        if context != nil {
            logger.debug("VAD model already loaded")
            return
        }

        // Get model path
        guard let modelPath = await VADModelManager.shared.getModelPath() else {
            logger.error("VAD model path not found")
            throw VADError.modelNotFound
        }

        // Configure context parameters
        var contextParams = whisper_vad_default_context_params()
        // Default context params are fine for most cases

        // Load model
        // WHISPER.CPP FUNCTION: whisper_vad_init_from_file_with_params
        // Loads the Silero VAD model from disk and returns an opaque context pointer
        let ctx = whisper_vad_init_from_file_with_params(modelPath, contextParams)

        guard let ctx = ctx else {
            logger.error("Failed to load VAD model from: \(modelPath)")
            throw VADError.modelLoadFailed
        }

        context = ctx
        logger.info("VAD model loaded successfully")
    }

    /// Check if the VAD model is loaded and ready
    var isReady: Bool {
        context != nil
    }

    // MARK: - Audio Analysis

    /// Analyze audio samples to detect speech segments.
    ///
    /// ANALYSIS FLOW:
    /// 1. Validate context is loaded
    /// 2. Configure VAD parameters
    /// 3. Run whisper_vad_segments_from_samples
    /// 4. Extract segment timestamps
    /// 5. Calculate speech/silence durations
    /// 6. Free segments memory
    ///
    /// AUDIO FORMAT:
    /// - Sample rate: 16000 Hz (standard for Whisper)
    /// - Format: Float32 normalized to [-1.0, 1.0]
    /// - Channels: Mono
    ///
    /// - Parameters:
    ///   - samples: Audio samples in Float32 format, 16kHz mono
    ///   - sampleRate: Sample rate (default 16000 Hz)
    /// - Returns: VADResult containing speech segments
    /// - Throws: VADError if analysis fails
    func analyzeAudio(samples: [Float], sampleRate: Int = 16000) throws -> VADResult {
        guard let context = context else {
            logger.error("VAD context not initialized")
            throw VADError.analysisFailedContextNotReady
        }

        guard !samples.isEmpty else {
            logger.warning("Empty audio samples provided")
            return VADResult(
                hasSpeech: false,
                speechDuration: 0,
                silenceDuration: 0,
                segments: []
            )
        }

        // Calculate total audio duration
        let totalDuration = Double(samples.count) / Double(sampleRate)
        logger.debug("Analyzing \(samples.count) samples (\(String(format: "%.2f", totalDuration))s)")

        // Configure VAD parameters
        // WHISPER.CPP STRUCT: whisper_vad_params
        var params = whisper_vad_default_params()
        params.threshold = threshold
        params.min_speech_duration_ms = minSpeechDurationMs
        params.min_silence_duration_ms = minSilenceDurationMs
        params.max_speech_duration_s = maxSpeechDurationS
        params.speech_pad_ms = speechPadMs
        params.samples_overlap = samplesOverlap

        // Run VAD analysis
        // WHISPER.CPP FUNCTION: whisper_vad_segments_from_samples
        // Analyzes audio samples and returns detected speech segments
        var segments: OpaquePointer?
        samples.withUnsafeBufferPointer { buffer in
            segments = whisper_vad_segments_from_samples(
                context,
                params,
                buffer.baseAddress,
                Int32(buffer.count)
            )
        }

        guard let segments = segments else {
            logger.error("VAD segmentation failed")
            throw VADError.segmentExtractionFailed
        }

        // CRITICAL: Use defer to ensure segments are freed even if we return early
        defer {
            // WHISPER.CPP FUNCTION: whisper_vad_free_segments
            // Frees memory allocated for speech segments
            whisper_vad_free_segments(segments)
        }

        // Extract segment timestamps
        // WHISPER.CPP FUNCTIONS:
        // - whisper_vad_segments_n_segments: Get number of detected segments
        // - whisper_vad_segments_get_segment_t0: Get segment start time (seconds)
        // - whisper_vad_segments_get_segment_t1: Get segment end time (seconds)
        let segmentCount = whisper_vad_segments_n_segments(segments)
        logger.debug("Detected \(segmentCount) speech segment(s)")

        var timeRanges: [TimeRange] = []
        var totalSpeechDuration: Float = 0
        let audioEnd = Float(totalDuration)

        for i in 0..<segmentCount {
            // CRITICAL: whisper.cpp VAD API returns timestamps in CENTISECONDS (1/100th second),
            // not seconds! We must divide by 100 to convert to seconds.
            // See: https://github.com/ggml-org/whisper.cpp/issues/3370
            let rawT0 = whisper_vad_segments_get_segment_t0(segments, i)
            let rawT1 = whisper_vad_segments_get_segment_t1(segments, i)
            let t0 = rawT0 / 100.0
            let t1 = rawT1 / 100.0

            // Log both raw and converted values for debugging
            logger.debug("Segment \(i) raw (centiseconds): t0=\(rawT0), t1=\(rawT1)")
            logger.debug("Segment \(i) converted (seconds): t0=\(String(format: "%.2f", t0))s, t1=\(String(format: "%.2f", t1))s")

            // Validate segment times are within audio duration.
            // whisper.cpp's Silero VAD can return a t1 slightly past the audio end at
            // file-boundary segments. Clamp t1 to the audio duration (and skip the
            // segment entirely if it starts at/after the end) so out-of-bounds ranges
            // never inflate totalSpeechDuration and poison silenceDuration below.
            guard t0 < audioEnd else {
                logger.warning("⚠️ Segment \(i) starts at/after audio end (t0=\(t0)s, audioDuration=\(totalDuration)s) - skipping")
                continue
            }

            if t1 > audioEnd {
                logger.warning("⚠️ Segment \(i) end exceeds audio duration! t0=\(t0)s, t1=\(t1)s, audioDuration=\(totalDuration)s - clamping end to audio duration")
            }

            let range = TimeRange(start: t0, end: min(t1, audioEnd))
            timeRanges.append(range)
            totalSpeechDuration += range.duration

            logger.info("Segment \(i): \(range)")
        }

        let speechDuration = TimeInterval(totalSpeechDuration)
        let rawSilenceDuration = totalDuration - speechDuration

        if rawSilenceDuration < 0 {
            logger.warning("⚠️ raw silenceDuration is negative (\(rawSilenceDuration)s) before clamping - likely residual Float/Double rounding at file boundary")
        }

        // Floor silence at zero: clamping segment ends above keeps speechDuration <= totalDuration,
        // but guard against any residual floating-point overshoot poisoning downstream validation.
        let silenceDuration = max(0, rawSilenceDuration)
        let speechPercentage = totalDuration > 0 ? (speechDuration / totalDuration * 100) : 0

        logger.info("VAD complete: \(timeRanges.count) segments, speech=\(String(format: "%.1f", speechDuration))s (\(String(format: "%.1f", speechPercentage))%), silence=\(String(format: "%.1f", silenceDuration))s, total=\(String(format: "%.1f", totalDuration))s")

        // Sanity check: warn if speech duration exceeds total duration before clamping.
        if speechDuration > totalDuration {
            logger.warning("⚠️ speechDuration (\(speechDuration)s) exceeds totalDuration (\(totalDuration)s) before silence clamping - likely residual Float/Double rounding at file boundary")
        }

        return VADResult(
            hasSpeech: !timeRanges.isEmpty,
            speechDuration: speechDuration,
            silenceDuration: silenceDuration,
            segments: timeRanges
        )
    }

    // MARK: - Resource Management

    /// Release the VAD model and free resources.
    ///
    /// Call this when VAD is no longer needed to free memory.
    /// The model can be reloaded by calling loadModel() again.
    func unloadModel() {
        if let context = context {
            // WHISPER.CPP FUNCTION: whisper_vad_free
            // Frees all resources associated with the VAD context
            whisper_vad_free(context)
            self.context = nil
            logger.info("VAD model unloaded")
        }
    }
}
