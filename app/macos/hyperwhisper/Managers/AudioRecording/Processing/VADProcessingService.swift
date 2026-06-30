//
//  VADProcessingService.swift
//  hyperwhisper
//
//  Centralized service for VAD (Voice Activity Detection) silence trimming
//  with M4A conversion support.
//
//  PURPOSE:
//  ========
//  Consolidates all VAD processing logic that was previously duplicated across:
//  - RecordingTranscriptionFlow (live recordings)
//  - TranscriptionRetryController (retry transcriptions)
//  - FileTranscriptionFlow (file imports)
//
//  This service provides a single, reusable implementation that:
//  1. Checks if VAD processing should be applied
//  2. Runs SilenceTrimmer to detect and remove silence
//  3. Validates trimmed results meet quality criteria
//  4. Converts large files to M4A for upload efficiency
//  5. Returns a result struct with final audio URL and metadata
//
//  PROCESSING FLOW:
//  ================
//  Input: Audio URL, duration, VAD enabled flag, context string
//    │
//    ├─ Check: VAD enabled AND duration >= 30s?
//    │   └─ NO → Return original URL (wasProcessed = false)
//    │
//    ├─ YES → Create SilenceTrimmer()
//    │        └─ Call trimSilence(from: audioURL)
//    │            │
//    │            ├─ Validate trim result:
//    │            │   ├─ Silence removed > 0.5s?
//    │            │   ├─ Trimmed duration >= 0.3s?
//    │            │   └─ File exists with > 5KB?
//    │            │
//    │            ├─ VALID → Check file size >= 25MB?
//    │            │   ├─ YES → Convert to M4A
//    │            │   └─ Return final URL
//    │            │
//    │            └─ INVALID → Return original URL
//    │
//    └─ Handle errors: Return original URL on failure
//
//  THREAD SAFETY:
//  ==============
//  All methods run on @MainActor for consistency with callers.
//  The service is stateless and thread-safe.
//

import Foundation
import os
import AVFoundation
import CoreMedia

// MARK: - VADProcessingResult

/// Result of VAD processing operation
///
/// Contains all information needed by callers to:
/// - Use the correct audio URL for transcription
/// - Store trimmed path in Core Data if applicable
/// - Log processing statistics
struct VADProcessingResult {

    /// The final audio URL to use for transcription.
    ///
    /// This may be:
    /// - Original audio URL (if VAD skipped or failed)
    /// - Trimmed WAV URL (if VAD succeeded and file < 25MB)
    /// - Trimmed M4A URL (if VAD succeeded and file >= 25MB)
    let finalAudioURL: URL

    /// The trim result if VAD was applied, nil if skipped.
    ///
    /// Contains statistics about the trimming operation:
    /// - originalDuration, trimmedDuration, silenceRemoved
    /// - segmentCount, removalPercentage
    /// - outputURL (trimmed file path)
    let trimResult: TrimResult?

    /// Whether VAD processing was actually performed and resulted in a trimmed file.
    ///
    /// - true: VAD ran successfully and finalAudioURL differs from original
    /// - false: VAD was skipped, failed, or produced invalid results
    let wasProcessed: Bool

    /// The original audio URL before any processing.
    ///
    /// Useful for:
    /// - Logging and debugging
    /// - Cleanup operations
    /// - Fallback scenarios
    let originalAudioURL: URL
}

// MARK: - VADProcessingService

/// Centralized service for VAD silence trimming with M4A conversion support
///
/// USAGE:
/// ```swift
/// let vadService = VADProcessingService()
/// let result = await vadService.processAudioForTranscription(
///     audioURL: recordingURL,
///     duration: 45.0,
///     vadEnabled: true,
///     context: "Recording"
/// )
/// // Use result.finalAudioURL for transcription
/// // Store result.trimResult?.outputURL.path in Core Data if result.wasProcessed
/// ```
///
/// DESIGN DECISIONS:
/// - Stateless: No instance variables that change between calls
/// - Self-contained: Owns its own AudioFileConverter instance
/// - Graceful degradation: Always returns a usable audio URL
/// - Comprehensive logging: Uses AppLogger for debugging
/// - Sentry integration: Adds breadcrumbs for error tracking
@MainActor
class VADProcessingService {

    // MARK: - Dependencies

    /// Audio file converter for WAV→M4A conversion.
    /// Owned by this service to avoid external dependencies.
    private let audioFileConverter = AudioFileConverter()

    // MARK: - Initialization

    init() {}

    // MARK: - Public API

    /// Process audio with VAD trimming if enabled and duration qualifies.
    ///
    /// This is the main entry point for all VAD processing in the app.
    /// It handles the complete flow from silence detection to M4A conversion.
    ///
    /// PROCESSING FLOW FOR IMPORTED FILES:
    /// ====================================
    /// When importing already-compressed files (m4a, mp3, etc.), we need special handling:
    /// 1. VAD can still analyze and trim the audio (creates WAV output)
    /// 2. BUT we skip M4A conversion to avoid re-encoding issues
    /// 3. The trimmed WAV is used directly (cloud providers accept WAV)
    ///
    /// WHY SKIP M4A CONVERSION FOR IMPORTED COMPRESSED FILES:
    /// - Re-encoding compressed→WAV→compressed is lossy and error-prone
    /// - AVAssetWriter can fail on certain AAC configurations from imported files
    /// - Results in "Invalid request: check audio file" errors from Deepgram
    /// - WAV is universally accepted by all transcription providers
    ///
    /// - Parameters:
    ///   - audioURL: URL of the audio file to process
    ///   - duration: Duration of the audio in seconds
    ///   - vadEnabled: Whether VAD is enabled in settings
    ///   - context: Logging context for diagnostics (e.g., "Recording", "Retry", "FileImport")
    ///
    /// - Returns: VADProcessingResult with final audio URL and processing details
    ///
    /// - Note: This method never throws. On any error, it returns the original audio URL.
    func processAudioForTranscription(
        audioURL: URL,
        duration: TimeInterval,
        vadEnabled: Bool,
        context: String = ""
    ) async -> VADProcessingResult {
        // Track original file format to avoid re-encoding compressed formats
        let originalExtension = audioURL.pathExtension.lowercased()
        let isImportedCompressedFormat = AudioConstants.isCloudCompatibleCompressedFormat(originalExtension)

        let logPrefix = context.isEmpty ? "" : "[\(context)] "

        // STEP 1: Check if VAD should be applied
        // ======================================
        // VAD only runs when:
        // - User has enabled VAD in settings
        // - Recording is >= 30 seconds (shorter recordings don't benefit)
        guard vadEnabled && duration >= AudioConstants.vadMinimumDuration else {
            if vadEnabled && duration < AudioConstants.vadMinimumDuration {
                AppLogger.audio.debug("\(logPrefix)VAD skipped - duration too short (\(String(format: "%.1f", duration))s < \(AudioConstants.vadMinimumDuration)s)")
            }
            return VADProcessingResult(
                finalAudioURL: audioURL,
                trimResult: nil,
                wasProcessed: false,
                originalAudioURL: audioURL
            )
        }

        AppLogger.audio.info("🎤 \(logPrefix)VAD enabled - analyzing audio for silence trimming (duration: \(String(format: "%.1f", duration))s)...")

        // STEP 2: Run SilenceTrimmer
        // ==========================
        // Creates a new trimmer instance and processes the audio.
        // The trimmer uses VoiceActivityDetector to identify speech segments.
        do {
            let trimmer = SilenceTrimmer()
            let trimResult = try await trimmer.trimSilence(from: audioURL)

            // STEP 3: Validate trim result
            // ============================
            // Check if trimmed audio meets quality criteria.
            // If not, fall back to original audio.
            if let validatedURL = validateTrimResult(trimResult, context: context) {

                // STEP 4: Convert to M4A if file is large (but NOT for imported compressed formats)
                // ================================================================================
                // Files >= 25MB are converted to M4A for upload efficiency.
                // HOWEVER, if the original was a compressed format (m4a, mp3, etc.), we skip
                // this conversion to avoid re-encoding issues that cause Deepgram errors.
                let finalURL: URL
                if isImportedCompressedFormat {
                    // SKIP M4A CONVERSION: Original was already compressed
                    // The trimmed WAV file will be used directly - all cloud providers accept WAV
                    AppLogger.audio.info("🎵 \(logPrefix)Skipping M4A conversion - original was compressed format (.\(originalExtension))")
                    finalURL = validatedURL
                } else {
                    // Apply M4A conversion for large WAV files from live recordings
                    finalURL = await convertTrimmedToM4AIfNeeded(validatedURL, context: context)
                }

                return VADProcessingResult(
                    finalAudioURL: finalURL,
                    trimResult: trimResult,
                    wasProcessed: true,
                    originalAudioURL: audioURL
                )
            } else {
                // Validation failed, use original audio
                return VADProcessingResult(
                    finalAudioURL: audioURL,
                    trimResult: trimResult,
                    wasProcessed: false,
                    originalAudioURL: audioURL
                )
            }

        } catch TrimError.noSpeechDetected {
            // SPECIAL CASE: No speech detected in audio
            // =========================================
            // This isn't an error - the user may have recorded silence.
            // Return original audio for transcription (will likely result in empty text).
            AppLogger.audio.warning("⚠️ \(logPrefix)VAD: No speech detected, using original file")
            return VADProcessingResult(
                finalAudioURL: audioURL,
                trimResult: nil,
                wasProcessed: false,
                originalAudioURL: audioURL
            )

        } catch {
            // GENERAL ERROR: VAD processing failed
            // ====================================
            // Log the error and fall back to original audio.
            // This ensures transcription can still proceed.
            AppLogger.audio.warning("⚠️ \(logPrefix)VAD analysis failed: \(error.localizedDescription) - using original audio")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "VAD silence trimming failed",
                    category: "audio.vad",
                    level: .warning,
                    data: [
                        "error": error.localizedDescription,
                        "audioPath": audioURL.path,
                        "context": context
                    ]
                )
            }

            return VADProcessingResult(
                finalAudioURL: audioURL,
                trimResult: nil,
                wasProcessed: false,
                originalAudioURL: audioURL
            )
        }
    }

    // MARK: - Private Methods

    /// Validate trim result meets quality criteria.
    ///
    /// VALIDATION CRITERIA:
    /// 1. Minimum silence removed (> 0.5s) - ensures meaningful processing
    /// 2. Minimum trimmed duration (>= 0.3s) - ensures valid speech content
    /// 3. File exists and has content (> 5KB) - ensures valid file output
    ///
    /// - Parameters:
    ///   - result: The TrimResult from SilenceTrimmer
    ///   - context: Logging context string
    ///
    /// - Returns: The trimmed file URL if valid, nil if validation fails
    private func validateTrimResult(_ result: TrimResult, context: String) -> URL? {
        let logPrefix = context.isEmpty ? "" : "[\(context)] "

        // CHECK 1: Minimum silence removed
        // ================================
        // If less than 0.5s of silence was removed, not worth using trimmed file.
        // This prevents unnecessary file duplication for minimal gains.
        guard result.silenceRemoved > AudioConstants.minimumSilenceRemoved else {
            AppLogger.audio.debug("\(logPrefix)VAD: No significant silence to trim")
            return nil
        }

        // CHECK 2: Minimum trimmed duration
        // =================================
        // If trimmed audio is too short, VAD may have trimmed too aggressively.
        // Fall back to original to preserve potential speech content.
        guard result.trimmedDuration >= AudioConstants.minimumTrimmedDuration else {
            AppLogger.audio.warning("⚠️ \(logPrefix)VAD trimmed audio too short (\(String(format: "%.2f", result.trimmedDuration))s) - using original")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "VAD trimmed audio too short",
                    category: "audio.vad",
                    level: .warning,
                    data: [
                        "originalDuration": result.originalDuration,
                        "trimmedDuration": result.trimmedDuration,
                        "context": context
                    ]
                )
            }
            return nil
        }

        // CHECK 3: File exists and has meaningful content
        // ================================================
        // Verify the trimmed file was actually created and has content.
        // Files under 5KB likely contain only WAV header without audio.
        let trimmedFileExists = FileManager.default.fileExists(atPath: result.outputURL.path)
        let trimmedFileSize = (try? FileManager.default.attributesOfItem(atPath: result.outputURL.path)[.size] as? Int64) ?? 0

        guard trimmedFileExists && trimmedFileSize > AudioConstants.minimumTrimmedFileSize else {
            AppLogger.audio.warning("⚠️ \(logPrefix)VAD trimmed file invalid (exists=\(trimmedFileExists), size=\(trimmedFileSize) bytes)")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "VAD trimmed file invalid",
                    category: "audio.vad",
                    level: .warning,
                    data: [
                        "trimmedFileExists": trimmedFileExists,
                        "trimmedFileSizeBytes": trimmedFileSize,
                        "context": context
                    ]
                )
            }
            return nil
        }

        // SUCCESS: All validation checks passed
        // =====================================
        AppLogger.audio.info("✂️ \(logPrefix)VAD trimmed \(String(format: "%.1f", result.silenceRemoved))s of silence (original: \(String(format: "%.1f", result.originalDuration))s → trimmed: \(String(format: "%.1f", result.trimmedDuration))s)")

        if AppLogger.isErrorLoggingEnabled {
            SentryService.addBreadcrumb(
                message: "VAD silence trimming completed",
                category: "audio.vad",
                data: [
                    "originalDuration": result.originalDuration,
                    "trimmedDuration": result.trimmedDuration,
                    "silenceRemoved": result.silenceRemoved,
                    "removalPercentage": result.removalPercentage,
                    "trimmedFileSizeBytes": trimmedFileSize,
                    "context": context
                ]
            )
        }

        return result.outputURL
    }

    /// Convert large WAV files to M4A for upload efficiency.
    ///
    /// WHY M4A CONVERSION:
    /// - OpenAI Whisper API has a 25MB file size limit
    /// - M4A (AAC) achieves 5-10x compression over WAV
    /// - Reduces upload time for large recordings
    ///
    /// CONVERSION STRATEGY:
    /// 1. Primary method: AudioFileConverter.convertAudioToAAC()
    ///    - Uses AVAssetReader/Writer pipeline
    ///    - Preserves audio quality with 64-128 kbps AAC
    ///
    /// 2. Fallback method: AudioFileConverter.convertAudioToM4AWithExportSession()
    ///    - Uses higher-level AVAssetExportSession API
    ///    - Better compatibility for edge cases
    ///
    /// - Parameters:
    ///   - wavURL: URL of the WAV file to potentially convert
    ///   - context: Logging context string
    ///
    /// - Returns: M4A URL if conversion succeeded, original WAV URL otherwise
    private func convertTrimmedToM4AIfNeeded(_ wavURL: URL, context: String) async -> URL {
        let logPrefix = context.isEmpty ? "" : "[\(context)] "

        // CHECK: Only process WAV files
        // =============================
        guard wavURL.pathExtension.lowercased() == "wav" else {
            return wavURL
        }

        // CHECK: File size threshold
        // ==========================
        // Only convert files >= 25MB (OpenAI's limit)
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: wavURL.path),
              let fileSize = attributes[.size] as? Int64,
              fileSize >= AudioConstants.maxWAVFileSizeForUpload else {
            return wavURL
        }

        let fileSizeMB = Double(fileSize) / (1024 * 1024)
        AppLogger.audio.info("🔄 \(logPrefix)Trimmed WAV file is \(String(format: "%.1f", fileSizeMB))MB - converting to M4A")

        let m4aURL = wavURL.deletingPathExtension().appendingPathExtension("m4a")

        // Remove any existing M4A file from previous attempts
        try? FileManager.default.removeItem(at: m4aURL)

        // PRIMARY METHOD: convertAudioToAAC
        // ==================================
        do {
            let (sampleRate, channels, bitrate) = try await audioFileConverter.convertAudioToAAC(
                from: wavURL,
                to: m4aURL
            )

            guard FileManager.default.fileExists(atPath: m4aURL.path) else {
                throw AudioError.exportFailed
            }

            let m4aSize = (try? FileManager.default.attributesOfItem(atPath: m4aURL.path)[.size] as? Int64) ?? 0
            let m4aSizeMB = Double(m4aSize) / (1024 * 1024)

            // VALIDATION: Check M4A file is valid and playable
            // ================================================
            // Sometimes conversion "succeeds" but produces a corrupted file.
            // Verify the output by checking if AVURLAsset can read it.
            if !validateM4AOutput(m4aURL, context: context) {
                AppLogger.audio.warning("⚠️ \(logPrefix)Primary M4A output validation failed - trying fallback")
                try? FileManager.default.removeItem(at: m4aURL)
                throw AudioError.exportFailed
            }

            AppLogger.audio.info("✅ \(logPrefix)Trimmed WAV→M4A conversion succeeded: \(String(format: "%.1f", fileSizeMB))MB → \(String(format: "%.1f", m4aSizeMB))MB")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Trimmed WAV→M4A conversion succeeded",
                    category: "audio.vad",
                    data: [
                        "originalSizeBytes": fileSize,
                        "m4aSizeBytes": m4aSize,
                        "sampleRate": sampleRate,
                        "channels": channels,
                        "bitrate": bitrate,
                        "context": context
                    ]
                )
            }

            return m4aURL

        } catch {
            AppLogger.audio.warning("⚠️ \(logPrefix)Primary M4A conversion failed: \(error.localizedDescription) - trying fallback")
        }

        // FALLBACK METHOD: convertAudioToM4AWithExportSession
        // ====================================================
        do {
            try? FileManager.default.removeItem(at: m4aURL)

            _ = try await audioFileConverter.convertAudioToM4AWithExportSession(
                from: wavURL,
                to: m4aURL
            )

            guard FileManager.default.fileExists(atPath: m4aURL.path) else {
                throw AudioError.exportFailed
            }

            // VALIDATION: Check fallback M4A output is valid
            // ==============================================
            if !validateM4AOutput(m4aURL, context: context) {
                AppLogger.audio.warning("⚠️ \(logPrefix)Fallback M4A output validation failed - using original WAV")
                try? FileManager.default.removeItem(at: m4aURL)
                throw AudioError.exportFailed
            }

            let m4aSize = (try? FileManager.default.attributesOfItem(atPath: m4aURL.path)[.size] as? Int64) ?? 0
            let m4aSizeMB = Double(m4aSize) / (1024 * 1024)

            AppLogger.audio.info("✅ \(logPrefix)Trimmed WAV→M4A fallback succeeded: \(String(format: "%.1f", fileSizeMB))MB → \(String(format: "%.1f", m4aSizeMB))MB")

            return m4aURL

        } catch {
            // BOTH METHODS FAILED: Use original WAV
            // =====================================
            AppLogger.audio.warning("⚠️ \(logPrefix)M4A fallback also failed - using original WAV")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Trimmed WAV→M4A conversion failed",
                    category: "audio.vad",
                    level: .warning,
                    data: [
                        "originalSizeBytes": fileSize,
                        "error": error.localizedDescription,
                        "context": context
                    ]
                )
            }

            return wavURL
        }
    }

    /// Validate that an M4A file is playable and not corrupted.
    ///
    /// VALIDATION CHECKS:
    /// 1. File can be opened by AVURLAsset
    /// 2. Asset has at least one audio track
    /// 3. Track has valid format description (sample rate, channels)
    ///
    /// WHY THIS MATTERS:
    /// - M4A conversion can "succeed" but produce corrupted output
    /// - Deepgram rejects corrupted files with "Invalid data received" (400)
    /// - Better to detect early and fall back to WAV than fail at transcription
    ///
    /// - Parameters:
    ///   - url: URL of the M4A file to validate
    ///   - context: Logging context string
    ///
    /// - Returns: true if file is valid and playable, false if corrupted
    private func validateM4AOutput(_ url: URL, context: String) -> Bool {
        let logPrefix = context.isEmpty ? "" : "[\(context)] "

        let asset = AVURLAsset(url: url)

        // CHECK 1: Asset has audio tracks
        let tracks = asset.tracks(withMediaType: .audio)
        guard let audioTrack = tracks.first else {
            AppLogger.audio.error("❌ \(logPrefix)M4A validation failed: no audio tracks")
            return false
        }

        // CHECK 2: Track has valid format description
        guard let formatDesc = audioTrack.formatDescriptions.first else {
            AppLogger.audio.error("❌ \(logPrefix)M4A validation failed: no format description")
            return false
        }

        let fdesc = formatDesc as! CMAudioFormatDescription
        guard let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(fdesc) else {
            AppLogger.audio.error("❌ \(logPrefix)M4A validation failed: no stream description")
            return false
        }

        let sampleRate = asbd.pointee.mSampleRate
        let channels = asbd.pointee.mChannelsPerFrame

        // CHECK 3: Sample rate and channels are valid
        guard sampleRate > 0 && channels > 0 else {
            AppLogger.audio.error("❌ \(logPrefix)M4A validation failed: invalid format (sampleRate=\(sampleRate), channels=\(channels))")
            return false
        }

        AppLogger.audio.debug("✓ \(logPrefix)M4A validation passed: \(Int(sampleRate))Hz, \(channels)ch")
        return true
    }
}
