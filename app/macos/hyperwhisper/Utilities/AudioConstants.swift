//
//  AudioConstants.swift
//  hyperwhisper
//
//  Centralized constants for audio processing throughout the app.
//  These constants are used by VADProcessingService, RecordingTranscriptionFlow,
//  TranscriptionRetryController, and FileTranscriptionFlow.
//
//  WHY CENTRALIZED:
//  ================
//  Previously these constants were duplicated across 3+ files, leading to:
//  - Risk of values drifting out of sync
//  - Harder to update thresholds consistently
//  - No single source of truth for audio processing limits
//
//  CONSTANT CATEGORIES:
//  ====================
//  1. File Size Limits - Thresholds for format conversion
//  2. VAD Processing - Minimum durations for silence trimming
//  3. Validation Thresholds - Quality checks for trimmed audio
//

import Foundation

/// Centralized constants for audio processing
///
/// These constants define the thresholds and limits used throughout the audio
/// processing pipeline, including VAD (Voice Activity Detection), file size
/// limits, and quality validation criteria.
enum AudioConstants {

    // MARK: - File Size Limits

    /// Maximum WAV file size (25MB) before M4A conversion is triggered.
    ///
    /// WHY 25MB:
    /// - Matches OpenAI Whisper API's file size limit for transcription uploads
    /// - Larger files require compression to avoid API rejection
    /// - M4A (AAC) typically achieves 5-10x compression ratio over WAV
    static let maxWAVFileSizeForUpload: Int64 = 25 * 1024 * 1024

    // MARK: - VAD Processing Thresholds

    /// Minimum recording duration (30 seconds) for VAD silence trimming.
    ///
    /// WHY 30 SECONDS:
    /// - Short recordings don't benefit enough from VAD processing overhead
    /// - VAD adds ~1-2 seconds of processing time
    /// - Silence trimming has diminishing returns on short recordings
    /// - Most dictation recordings under 30s have minimal silence
    static let vadMinimumDuration: TimeInterval = 30.0

    // MARK: - Trimmed Audio Validation

    /// Minimum file size (5KB) for valid trimmed audio content.
    ///
    /// WHY 5KB:
    /// - WAV header alone is ~44 bytes
    /// - Files under 5KB likely contain only header without meaningful audio
    /// - Prevents transcription of essentially empty files
    /// - Catches edge cases where VAD removes all content
    static let minimumTrimmedFileSize: Int64 = 5_000

    /// Minimum silence removed (0.5 seconds) to consider VAD trimming worthwhile.
    ///
    /// WHY 0.5 SECONDS:
    /// - Removing less than 0.5s of silence isn't worth the processing
    /// - Keeps original file when VAD provides negligible benefit
    /// - Prevents unnecessary file duplication for minimal gains
    static let minimumSilenceRemoved: TimeInterval = 0.5

    /// Minimum trimmed duration (0.3 seconds) for valid speech content.
    ///
    /// WHY 0.3 SECONDS:
    /// - Shortest meaningful speech segment is ~0.3 seconds
    /// - Catches edge cases where VAD incorrectly trims too aggressively
    /// - Prevents transcription of audio that's too short to contain speech
    /// - Falls back to original audio when trimmed result is too short
    static let minimumTrimmedDuration: TimeInterval = 0.3

    // MARK: - Supported Formats

    /// Cloud-compatible compressed audio formats that don't need re-encoding.
    ///
    /// These formats are already compressed and widely supported by transcription APIs:
    /// - Most cloud providers accept m4a, mp3, mp4, webm, ogg
    /// - Re-encoding these formats can introduce quality loss or failures
    /// - Better to send the original file when possible
    ///
    /// WHY THIS MATTERS:
    /// - Re-encoding already-compressed audio (e.g., M4A→WAV→M4A) is lossy and error-prone
    /// - The AVAssetReader/Writer pipeline can fail on certain AAC configurations
    /// - Skipping re-encoding avoids these issues and preserves quality
    static let cloudCompatibleCompressedFormats: Set<String> = [
        "m4a", "mp3", "mp4", "aac", "webm", "ogg", "opus"
    ]

    /// Check if a file extension represents a cloud-compatible compressed format.
    ///
    /// - Parameter pathExtension: The file extension to check (e.g., "m4a", "mp3")
    /// - Returns: true if the format is already compressed and cloud-compatible
    static func isCloudCompatibleCompressedFormat(_ pathExtension: String) -> Bool {
        cloudCompatibleCompressedFormats.contains(pathExtension.lowercased())
    }
}
