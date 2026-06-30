//
//  SilenceTrimmer.swift
//  hyperwhisper
//
//  Trims silence from audio files using Voice Activity Detection.
//  Uses VoiceActivityDetector to identify speech segments, then
//  creates a new audio file containing only those segments.
//
//  TRIMMING FLOW:
//  ==============
//  1. Load audio file and extract samples
//  2. Run VAD to detect speech segments
//  3. Extract audio samples for each segment
//  4. Concatenate segments with padding
//  5. Write new audio file
//
//  AUDIO FORMAT:
//  =============
//  - Input: WAV or CAF (16-bit PCM or compatible)
//  - Output: WAV (16-bit PCM, 16kHz, mono)
//  - This matches the format expected by transcription providers
//

import Foundation
import AVFoundation
import os

// MARK: - TrimResult

/// Result of silence trimming operation
struct TrimResult {
    /// URL of the trimmed audio file
    let outputURL: URL

    /// Original audio duration in seconds
    let originalDuration: TimeInterval

    /// Trimmed audio duration in seconds
    let trimmedDuration: TimeInterval

    /// Amount of silence removed in seconds
    var silenceRemoved: TimeInterval {
        originalDuration - trimmedDuration
    }

    /// Percentage of audio removed
    var removalPercentage: Double {
        guard originalDuration > 0 else { return 0 }
        return silenceRemoved / originalDuration * 100
    }

    /// Number of speech segments found
    let segmentCount: Int
}

// MARK: - TrimError

/// Errors that can occur during silence trimming
enum TrimError: Error, LocalizedError {
    case fileNotFound
    case audioLoadFailed(String)
    case vadAnalysisFailed(Error)
    case noSpeechDetected
    case outputWriteFailed(String)
    case invalidAudioFormat

    var errorDescription: String? {
        switch self {
        case .fileNotFound:
            return "Audio file not found"
        case .audioLoadFailed(let detail):
            return "Failed to load audio: \(detail)"
        case .vadAnalysisFailed(let error):
            return "VAD analysis failed: \(error.localizedDescription)"
        case .noSpeechDetected:
            return "No speech detected in audio"
        case .outputWriteFailed(let detail):
            return "Failed to write output: \(detail)"
        case .invalidAudioFormat:
            return "Invalid audio format"
        }
    }
}

// MARK: - SilenceTrimmer

/// Trims silence from audio files using Voice Activity Detection.
///
/// USAGE:
/// ```swift
/// let trimmer = SilenceTrimmer()
/// let result = try await trimmer.trimSilence(from: audioURL)
/// // result.outputURL contains the trimmed audio
/// ```
///
/// THREAD SAFETY:
/// This class is thread-safe. All AVFoundation operations run on
/// background threads, and VAD runs via the VoiceActivityDetector actor.
///
/// MEMORY MANAGEMENT:
/// The source file is stream-decoded in chunks (via AudioConverter) and never
/// held in memory in its native format. Only the converted 16kHz mono Float32
/// samples are retained, since VAD and segment extraction both operate on the
/// full sample array.
class SilenceTrimmer {

    // MARK: - Properties

    /// Logger for debugging
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "SilenceTrimmer")

    /// Sample rate for audio processing (Whisper standard)
    private let sampleRate: Double = 16000

    /// Extra padding (ms) to add around segments beyond VAD's speech_pad_ms
    /// This provides a safety margin to avoid clipping
    var extraPaddingMs: Int = 50

    /// Minimum gap between segments to merge (ms)
    /// If two segments are closer than this, they're merged into one
    var mergeGapMs: Int = 200

    // MARK: - Public Methods

    /// Trim silence from an audio file.
    ///
    /// TRIMMING PROCESS:
    /// 1. Load audio from file
    /// 2. Convert to 16kHz mono Float32 samples
    /// 3. Run VAD to detect speech segments
    /// 4. Merge close segments to avoid choppy audio
    /// 5. Extract audio for each segment
    /// 6. Write new audio file with speech only
    ///
    /// - Parameters:
    ///   - inputURL: URL of the audio file to trim
    ///   - outputURL: Optional custom output URL. If nil, creates a new file next to input.
    /// - Returns: TrimResult with output URL and statistics
    /// - Throws: TrimError if trimming fails
    func trimSilence(from inputURL: URL, to outputURL: URL? = nil) async throws -> TrimResult {
        logger.info("Starting silence trimming for: \(inputURL.lastPathComponent)")

        // STEP 1: Verify input file exists
        guard FileManager.default.fileExists(atPath: inputURL.path) else {
            throw TrimError.fileNotFound
        }

        // STEP 2: Load audio samples
        let (samples, originalDuration) = try await loadAudioSamples(from: inputURL)
        logger.debug("Loaded \(samples.count) samples (\(String(format: "%.2f", originalDuration))s)")

        // STEP 3: Ensure VAD is ready
        let vad = VoiceActivityDetector.shared
        if await !vad.isReady {
            try await vad.loadModel()
        }

        // STEP 4: Run VAD analysis
        let vadResult: VADResult
        do {
            vadResult = try await vad.analyzeAudio(samples: samples, sampleRate: Int(sampleRate))
        } catch {
            throw TrimError.vadAnalysisFailed(error)
        }

        // STEP 5: Handle no speech case
        guard vadResult.hasSpeech else {
            logger.warning("No speech detected in audio")
            throw TrimError.noSpeechDetected
        }

        // STEP 6: Merge close segments
        let mergedSegments = mergeCloseSegments(vadResult.segments)
        logger.debug("Merged \(vadResult.segments.count) segments into \(mergedSegments.count)")

        // STEP 7: Add extra padding and extract samples
        let paddedSegments = addExtraPadding(mergedSegments, totalDuration: Float(originalDuration))
        let speechSamples = extractSpeechSamples(
            from: samples,
            segments: paddedSegments
        )
        let trimmedDuration = Double(speechSamples.count) / sampleRate

        logger.info("Extracted \(speechSamples.count) samples (\(String(format: "%.2f", trimmedDuration))s)")

        // STEP 8: Determine output URL
        let finalOutputURL = outputURL ?? generateOutputURL(for: inputURL)

        // STEP 9: Write trimmed audio
        try await writeAudioFile(samples: speechSamples, to: finalOutputURL)
        logger.info("Wrote trimmed audio to: \(finalOutputURL.lastPathComponent)")

        return TrimResult(
            outputURL: finalOutputURL,
            originalDuration: originalDuration,
            trimmedDuration: trimmedDuration,
            segmentCount: mergedSegments.count
        )
    }

    // MARK: - Private Methods - Audio Loading

    /// Load audio from file and convert to Float32 samples at 16kHz mono.
    ///
    /// CONVERSION PROCESS:
    /// 1. Open AVAudioFile to read its native format and duration
    /// 2. Stream-decode + convert to 16kHz mono Float32 via AudioConverter
    ///    (chunked — never loads the whole file into one PCM buffer)
    /// 3. Return Float32 array
    ///
    /// MEMORY: AudioConverter processes the file in small chunks, so a long
    /// recording no longer requires a single full-file AVAudioPCMBuffer in the
    /// source's native format (e.g. ~1GB for 45min of 48kHz stereo). Peak
    /// transient memory is bounded by one chunk; the returned 16kHz mono array
    /// is the only large allocation, which VAD and segment extraction require.
    private func loadAudioSamples(from url: URL) async throws -> ([Float], TimeInterval) {
        // Open the file once to read its native format and compute duration.
        let duration: TimeInterval
        do {
            let audioFile = try AVAudioFile(forReading: url)
            let originalFormat = audioFile.processingFormat
            duration = Double(audioFile.length) / originalFormat.sampleRate
        } catch {
            throw TrimError.audioLoadFailed(error.localizedDescription)
        }

        // Stream-decode and convert to 16kHz mono Float32 in chunks.
        // This avoids allocating the entire recording as a single PCM buffer.
        //
        // CRITICAL: failOnPartialRead.
        //
        // On a mid-file read failure (corrupt/truncated or concurrently-written
        // recording), AudioConverter's default behavior is to log "Returning
        // partial conversion" and return only the decoded prefix. If we accepted
        // that prefix, VAD would run on it and we'd write a trimmed file that
        // silently drops the tail of the recording — bypassing VADProcessingService's
        // fallback to the original audio. We can't reliably detect that shortfall
        // from the sample count alone: AVAudioFile.length is exact for LinearPCM
        // but only an estimate for VBR/compressed sources, so a sample-count
        // tolerance would either be too loose (still dropping seconds of a long
        // recording) or false-positive-reject valid compressed audio. Instead we
        // ask AudioConverter to surface the read failure directly so any genuine
        // partial decode throws and the caller falls back to the original file.
        let samples: [Float]
        do {
            samples = try await AudioConverter().convert(
                from: url,
                options: AudioConverter.ConversionOptions(
                    chunkSize: 32768,
                    targetSampleRate: self.sampleRate,
                    normalize: false,
                    progressHandler: nil,
                    failOnPartialRead: true
                )
            )
        } catch {
            throw TrimError.audioLoadFailed(error.localizedDescription)
        }

        // CRITICAL: Check conversion actually produced samples.
        // If conversion fails silently, we'd send empty audio to VAD.
        if samples.isEmpty {
            throw TrimError.audioLoadFailed("Audio format conversion failed - no samples produced")
        }

        return (samples, duration)
    }

    // MARK: - Private Methods - Segment Processing

    /// Merge segments that are close together to avoid choppy audio.
    ///
    /// If two segments are separated by less than mergeGapMs, they are
    /// combined into a single segment. This produces smoother audio
    /// when there are brief pauses in speech.
    private func mergeCloseSegments(_ segments: [TimeRange]) -> [TimeRange] {
        guard segments.count > 1 else { return segments }

        let mergeThreshold = Float(mergeGapMs) / 1000.0
        var merged: [TimeRange] = []
        var current = segments[0]

        for segment in segments.dropFirst() {
            let gap = segment.start - current.end

            if gap <= mergeThreshold {
                // Merge: extend current to include next segment
                current = TimeRange(start: current.start, end: segment.end)
            } else {
                // Gap is too large: save current and start new
                merged.append(current)
                current = segment
            }
        }

        // Don't forget the last segment
        merged.append(current)

        return merged
    }

    /// Add extra padding to segments for safety margin.
    ///
    /// This adds additional padding beyond what VAD's speech_pad_ms provides.
    /// Ensures we don't clip the start or end of speech.
    private func addExtraPadding(_ segments: [TimeRange], totalDuration: Float) -> [TimeRange] {
        let padding = Float(extraPaddingMs) / 1000.0

        return segments.map { segment in
            TimeRange(
                start: max(0, segment.start - padding),
                end: min(totalDuration, segment.end + padding)
            )
        }
    }

    /// Extract audio samples for the given speech segments.
    ///
    /// Concatenates all speech segments into a single continuous array.
    /// Adds a small crossfade at segment boundaries to prevent clicks.
    private func extractSpeechSamples(
        from samples: [Float],
        segments: [TimeRange]
    ) -> [Float] {
        var result: [Float] = []
        let samplesPerSecond = Float(sampleRate)
        let totalSamples = samples.count
        let audioDuration = Float(totalSamples) / samplesPerSecond

        logger.debug("Extracting speech from \(segments.count) segment(s), totalSamples=\(totalSamples), audioDuration=\(String(format: "%.2f", audioDuration))s")

        for (index, segment) in segments.enumerated() {
            let startSample = Int(segment.start * samplesPerSecond)
            let endSample = Int(segment.end * samplesPerSecond)

            logger.debug("Segment \(index): \(segment) → samples \(startSample)-\(endSample)")

            // Validate segment is within audio bounds
            if startSample >= totalSamples {
                logger.warning("⚠️ Segment \(index) startSample (\(startSample)) >= totalSamples (\(totalSamples)) - segment is beyond audio end!")
            }
            if endSample > totalSamples {
                logger.warning("⚠️ Segment \(index) endSample (\(endSample)) > totalSamples (\(totalSamples)) - segment extends beyond audio end")
            }

            // Clamp to valid range
            let clampedStart = max(0, min(startSample, samples.count - 1))
            let clampedEnd = max(clampedStart, min(endSample, samples.count))

            if clampedStart != startSample || clampedEnd != endSample {
                logger.debug("Segment \(index) clamped: \(startSample)-\(endSample) → \(clampedStart)-\(clampedEnd)")
            }

            // Extract samples for this segment
            let segmentSampleCount = clampedEnd - clampedStart
            let segmentSamples = Array(samples[clampedStart..<clampedEnd])
            result.append(contentsOf: segmentSamples)

            logger.debug("Segment \(index): extracted \(segmentSampleCount) samples (\(String(format: "%.2f", Float(segmentSampleCount) / samplesPerSecond))s)")
        }

        logger.info("Total extracted: \(result.count) samples (\(String(format: "%.2f", Float(result.count) / samplesPerSecond))s) from \(segments.count) segment(s)")

        return result
    }

    // MARK: - Private Methods - Audio Writing

    /// Generate output URL for trimmed audio.
    ///
    /// Creates a new filename by appending "_trimmed" before the extension.
    /// Example: recording.wav -> recording_trimmed.wav
    private func generateOutputURL(for inputURL: URL) -> URL {
        let directory = inputURL.deletingLastPathComponent()
        let filename = inputURL.deletingPathExtension().lastPathComponent
        let ext = inputURL.pathExtension

        return directory.appendingPathComponent("\(filename)_trimmed.\(ext)")
    }

    /// Write audio samples to a WAV file.
    ///
    /// OUTPUT FORMAT:
    /// - Sample rate: 16000 Hz
    /// - Channels: 1 (mono)
    /// - Bit depth: 16-bit PCM
    /// - Format: WAV (LPCM)
    private func writeAudioFile(samples: [Float], to url: URL) async throws {
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                do {
                    // Create format for 16kHz mono 16-bit PCM
                    guard let format = AVAudioFormat(
                        commonFormat: .pcmFormatFloat32,
                        sampleRate: self.sampleRate,
                        channels: 1,
                        interleaved: false
                    ) else {
                        continuation.resume(throwing: TrimError.invalidAudioFormat)
                        return
                    }

                    // Create buffer
                    guard let buffer = AVAudioPCMBuffer(
                        pcmFormat: format,
                        frameCapacity: AVAudioFrameCount(samples.count)
                    ) else {
                        continuation.resume(throwing: TrimError.outputWriteFailed("Failed to create buffer"))
                        return
                    }

                    buffer.frameLength = AVAudioFrameCount(samples.count)

                    // Copy samples to buffer
                    if let channelData = buffer.floatChannelData?[0] {
                        for (i, sample) in samples.enumerated() {
                            channelData[i] = sample
                        }
                    }

                    // Delete existing file if present
                    if FileManager.default.fileExists(atPath: url.path) {
                        try FileManager.default.removeItem(at: url)
                    }

                    // Create output file (AVFoundation will write as WAV)
                    let audioFile = try AVAudioFile(
                        forWriting: url,
                        settings: [
                            AVFormatIDKey: Int(kAudioFormatLinearPCM),
                            AVSampleRateKey: self.sampleRate,
                            AVNumberOfChannelsKey: 1,
                            AVLinearPCMBitDepthKey: 16,
                            AVLinearPCMIsFloatKey: false,
                            AVLinearPCMIsBigEndianKey: false
                        ]
                    )

                    try audioFile.write(from: buffer)

                    continuation.resume()
                } catch {
                    continuation.resume(throwing: TrimError.outputWriteFailed(error.localizedDescription))
                }
            }
        }
    }
}
