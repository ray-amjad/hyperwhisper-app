//
//  AudioFileConverter.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import AVFoundation
import CoreMedia
import Atomics

/// Handles audio format conversion (CAF → M4A AAC)
///
/// **Purpose:**
/// Converts raw PCM recordings (CAF) to compressed AAC format (M4A) for:
/// 1. **Smaller file sizes**: ~10x compression vs raw PCM
/// 2. **Better compatibility**: M4A works with more transcription services
/// 3. **Faster uploads**: Smaller files transfer quicker to cloud APIs
///
/// **Conversion Strategy:**
/// We use a "preserve source format" approach for reliability:
/// - Keep original sample rate (no resampling)
/// - Keep original channel count (no mixdown/upmix)
/// - Only compress to AAC codec
///
/// **Why Preserve Format?**
/// Resampling can introduce artifacts and edge cases. By preserving the hardware's
/// native format, we avoid complex audio processing and potential quality loss.
///
/// **Bitrate Selection:**
/// - Mono: 64 kbps (sufficient for speech)
/// - Stereo: 128 kbps (standard quality)
/// - Multi-channel: 64 kbps per channel (minimum 128 kbps)
///
/// **Thread Safety:**
/// All methods are async and can be called from any actor context.
class AudioFileConverter {

    // MARK: - Public Methods

    /// Compress audio to AAC M4A using source sample rate and channels
    ///
    /// **What This Does:**
    /// Takes a raw PCM CAF file and converts it to a compressed AAC M4A file
    /// using AVAssetReader/AVAssetWriter pipeline.
    ///
    /// **Conversion Pipeline:**
    /// 1. **Read source format**: Inspect CAF file to determine sample rate/channels
    /// 2. **Configure reader**: Set up AVAssetReader to extract PCM samples
    /// 3. **Configure writer**: Set up AVAssetWriter with AAC encoder
    /// 4. **Stream samples**: Copy samples from reader to writer
    /// 5. **Finalize**: Complete writing and return metadata
    ///
    /// **Why Not AVAssetExportSession?**
    /// We use the lower-level Reader/Writer approach because:
    /// - More control over output settings
    /// - Better error handling
    /// - Ability to preserve exact source format
    /// - Fallback option available if needed
    ///
    /// **Parameters:**
    /// - `sourceURL`: Path to the raw CAF file
    /// - `destinationURL`: Path where M4A should be written
    ///
    /// **Returns:**
    /// Tuple containing:
    /// - `sampleRate`: Source sample rate (e.g., 48000.0)
    /// - `channels`: Number of channels (1 = mono, 2 = stereo)
    /// - `bitrate`: Selected AAC bitrate in bits/sec
    ///
    /// **Throws:**
    /// - `AudioError.exportFailed`: If conversion fails at any stage
    ///
    /// **Performance:**
    /// - Typical conversion time: 0.1-0.5 seconds for 1 minute of audio
    /// - Non-blocking: Uses async/await
    /// - Memory efficient: Streams samples instead of loading entire file
    func convertAudioToAAC(from sourceURL: URL, to destinationURL: URL) async throws -> (sampleRate: Double, channels: Int, bitrate: Int) {
        // STEP 1: Clean up any previous failed attempts
        // If destination file exists from a previous crash, remove it
        try? FileManager.default.removeItem(at: destinationURL)

        let asset = AVURLAsset(url: sourceURL)

        // STEP 2: Load audio tracks (async for macOS 12+ compatibility)
        let tracks: [AVAssetTrack]
        if #available(macOS 12.0, *) {
            tracks = try await asset.loadTracks(withMediaType: .audio)
        } else {
            // Fallback for older macOS versions
            tracks = asset.tracks(withMediaType: .audio)
        }

        guard let audioTrack = tracks.first else {
            AppLogger.audio.error("convertAudioToAAC: no audio tracks in \(sourceURL.lastPathComponent, privacy: .public)")
            throw AudioError.exportFailed
        }

        // STEP 3: Determine source sample rate and channel count
        // We read this from the track's format description for accuracy
        var sampleRate: Double = 0
        var channels: Int = 0

        if let fdescAny = audioTrack.formatDescriptions.first {
            let fdesc = fdescAny as! CMAudioFormatDescription
            if let asbdPtr = CMAudioFormatDescriptionGetStreamBasicDescription(fdesc) {
                let asbd = asbdPtr.pointee
                sampleRate = asbd.mSampleRate
                channels = Int(asbd.mChannelsPerFrame)
            }
        }

        // STEP 4: Fallback format detection if primary method fails
        if sampleRate <= 0 || channels <= 0 {
            // Try reading via AVAudioFile as backup
            if let file = try? AVAudioFile(forReading: sourceURL) {
                sampleRate = file.fileFormat.sampleRate
                channels = Int(file.fileFormat.channelCount)
            }
        }

        // Validate we got valid format info
        guard sampleRate > 0, channels > 0 else {
            AppLogger.audio.error("convertAudioToAAC: invalid source format sampleRate=\(sampleRate) channels=\(channels)")
            throw AudioError.exportFailed
        }

        // STEP 5: Configure AVAssetReader to read Linear PCM
        // We read as 16-bit PCM (no resampling, no channel changes)
        let assetReader: AVAssetReader
        do {
            assetReader = try AVAssetReader(asset: asset)
        } catch {
            AppLogger.audio.error("convertAudioToAAC: AVAssetReader init failed: \(error.localizedDescription, privacy: .public)")
            throw AudioError.exportFailed
        }

        let readerOutput = AVAssetReaderTrackOutput(
            track: audioTrack,
            outputSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVLinearPCMIsFloatKey: false,           // 16-bit integers
                AVLinearPCMBitDepthKey: 16,
                AVLinearPCMIsNonInterleaved: false      // Interleaved samples
            ]
        )

        guard assetReader.canAdd(readerOutput) else {
            AppLogger.audio.error("convertAudioToAAC: reader.canAdd(output) returned false; readerError=\(assetReader.error?.localizedDescription ?? "nil", privacy: .public)")
            throw AudioError.exportFailed
        }
        assetReader.add(readerOutput)

        // STEP 6: Configure AVAssetWriter for M4A output
        let assetWriter: AVAssetWriter
        do {
            assetWriter = try AVAssetWriter(outputURL: destinationURL, fileType: .m4a)
        } catch {
            AppLogger.audio.error("convertAudioToAAC: AVAssetWriter init failed: \(error.localizedDescription, privacy: .public)")
            throw AudioError.exportFailed
        }
        // Optimize file for streaming (moves moov atom to start)
        assetWriter.shouldOptimizeForNetworkUse = true

        // STEP 7: Choose bitrate based on channel count
        // Speech doesn't need high bitrates. At low sample rates (≤16 kHz) the AAC encoder
        // clamps to its own ceiling regardless of what we ask for, so request a value the
        // encoder won't have to renegotiate — 32 kbps mono / 64 kbps stereo are safe defaults
        // for telephony-grade speech and match what Core Audio picks internally.
        let bitrate: Int
        if channels <= 1 {
            bitrate = 32_000
        } else if channels == 2 {
            bitrate = 64_000
        } else {
            bitrate = max(32_000 * channels, 64_000)
        }

        // STEP 8: Configure AAC encoder settings.
        // AVEncoderAudioQualityKey is omitted on purpose: when AVEncoderBitRateKey is set,
        // mixing the two produced "Cannot Encode Media" on the first append on macOS 26.
        let outputSettings: [String: Any] = [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: channels,
            AVEncoderBitRateKey: bitrate
        ]

        let writerInput = AVAssetWriterInput(mediaType: .audio, outputSettings: outputSettings)
        writerInput.expectsMediaDataInRealTime = false  // We're converting a file, not streaming

        guard assetWriter.canAdd(writerInput) else {
            AppLogger.audio.error("convertAudioToAAC: writer.canAdd(input) returned false for AAC \(Int(sampleRate))Hz \(channels)ch @ \(bitrate)bps; writerError=\(assetWriter.error?.localizedDescription ?? "nil", privacy: .public)")
            throw AudioError.exportFailed
        }
        assetWriter.add(writerInput)

        // STEP 9: Start reading and writing
        guard assetReader.startReading() else {
            AppLogger.audio.error("convertAudioToAAC: reader.startReading() failed; status=\(assetReader.status.rawValue) error=\(assetReader.error?.localizedDescription ?? "nil", privacy: .public)")
            throw AudioError.exportFailed
        }

        guard assetWriter.startWriting() else {
            AppLogger.audio.error("convertAudioToAAC: writer.startWriting() failed; status=\(assetWriter.status.rawValue) error=\(assetWriter.error?.localizedDescription ?? "nil", privacy: .public)")
            throw AudioError.exportFailed
        }

        assetWriter.startSession(atSourceTime: .zero)

        // STEP 10: Stream audio samples from reader to writer
        // This is an async operation that processes samples in chunks
        let success = await withCheckedContinuation { continuation in
            let queue = DispatchQueue(label: "audio.conversion")

            // ATOMIC GUARD FOR CONTINUATION SAFETY:
            // ManagedAtomic ensures the continuation is only resumed once, even if
            // the callback is invoked multiple times or from different code paths.
            // The atomic exchange is a single CPU instruction - faster than locks.
            let isFinished = ManagedAtomic(false)

            // Request media data and append samples as they become available
            writerInput.requestMediaDataWhenReady(on: queue) {
                // Early exit if already finished (another path resumed the continuation)
                if isFinished.load(ordering: .acquiring) { return }

                while writerInput.isReadyForMoreMediaData {
                    // Check again inside loop in case we finished during iteration
                    if isFinished.load(ordering: .acquiring) { return }

                    if let sampleBuffer = readerOutput.copyNextSampleBuffer() {
                        // Append the sample buffer to the writer
                        if !writerInput.append(sampleBuffer) {
                            // Append failed - atomically mark as finished and cancel
                            if isFinished.exchange(true, ordering: .acquiring) == false {
                                AppLogger.audio.error("convertAudioToAAC: writerInput.append() returned false; writerStatus=\(assetWriter.status.rawValue) writerError=\(assetWriter.error?.localizedDescription ?? "nil", privacy: .public)")
                                assetReader.cancelReading()
                                assetWriter.cancelWriting()
                                continuation.resume(returning: false)
                            }
                            return
                        }
                    } else {
                        // No more samples (end of file or error)
                        // Atomically mark as finished before resuming continuation
                        if isFinished.exchange(true, ordering: .acquiring) == false {
                            writerInput.markAsFinished()

                            // Check reader status to distinguish success from failure
                            if assetReader.status == .failed || assetReader.status == .cancelled {
                                AppLogger.audio.error("convertAudioToAAC: reader ended in status=\(assetReader.status.rawValue) error=\(assetReader.error?.localizedDescription ?? "nil", privacy: .public)")
                                assetWriter.cancelWriting()
                                continuation.resume(returning: false)
                            } else {
                                // Reader finished successfully, finalize writing
                                assetWriter.finishWriting {
                                    let completed = assetWriter.status == .completed
                                    if !completed {
                                        AppLogger.audio.error("convertAudioToAAC: writer.finishWriting completed with status=\(assetWriter.status.rawValue) error=\(assetWriter.error?.localizedDescription ?? "nil", privacy: .public)")
                                    }
                                    continuation.resume(returning: completed)
                                }
                            }
                        }
                        return
                    }
                }
            }
        }

        // STEP 11: Check conversion result
        if !success {
            // Clean up failed output file
            try? FileManager.default.removeItem(at: destinationURL)
            throw AudioError.exportFailed
        }

        // Return format metadata for logging and verification
        return (sampleRate: sampleRate, channels: channels, bitrate: bitrate)
    }

    // MARK: - AVAssetExportSession Fallback

    /// Convert audio to M4A using AVAssetExportSession as a fallback
    ///
    /// **Purpose:**
    /// Provides an alternative M4A conversion path when the primary AVAssetReader/Writer
    /// approach fails. Uses Apple's higher-level AVAssetExportSession API which may
    /// succeed in cases where the lower-level approach fails.
    ///
    /// **Why This Fallback Exists:**
    /// Some audio configurations can cause AVAssetWriter to fail even with valid audio.
    /// AVAssetExportSession uses different internal code paths and may handle these
    /// edge cases better. Having two conversion methods increases reliability.
    ///
    /// **Trade-offs:**
    /// - Less control over output settings than AVAssetWriter approach
    /// - May produce slightly different output (different AAC encoder settings)
    /// - Still provides ~10x compression vs WAV
    ///
    /// **Parameters:**
    /// - `sourceURL`: Path to the source audio file (WAV or CAF)
    /// - `destinationURL`: Path where M4A should be written
    ///
    /// **Returns:**
    /// Tuple containing:
    /// - `sampleRate`: Source sample rate
    /// - `channels`: Number of channels
    ///
    /// **Throws:**
    /// - `AudioError.exportFailed`: If conversion fails
    func convertAudioToM4AWithExportSession(from sourceURL: URL, to destinationURL: URL) async throws -> (sampleRate: Double, channels: Int) {
        // Clean up any previous failed attempts
        try? FileManager.default.removeItem(at: destinationURL)

        let asset = AVURLAsset(url: sourceURL)

        // Get source format info for return value
        var sampleRate: Double = 0
        var channels: Int = 0

        if let file = try? AVAudioFile(forReading: sourceURL) {
            sampleRate = file.fileFormat.sampleRate
            channels = Int(file.fileFormat.channelCount)
        }

        guard sampleRate > 0, channels > 0 else {
            AppLogger.audio.error("AVAssetExportSession: Could not determine source format")
            throw AudioError.exportFailed
        }

        // Create export session with Apple Lossless preset (then configure for AAC)
        // Note: We use .appleM4A preset which produces AAC-encoded M4A files
        guard let exportSession = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetAppleM4A) else {
            AppLogger.audio.error("AVAssetExportSession: Failed to create export session")
            throw AudioError.exportFailed
        }

        exportSession.outputURL = destinationURL
        exportSession.outputFileType = .m4a
        exportSession.shouldOptimizeForNetworkUse = true

        // Perform the export
        // Note: On modern macOS/iOS SDKs, export() is async throws
        do {
            try await exportSession.export()
        } catch {
            AppLogger.audio.error("AVAssetExportSession: export() threw error - \(error.localizedDescription, privacy: .public)")
            throw error
        }

        // Check export status (belt-and-suspenders - export() should throw on failure)
        switch exportSession.status {
        case .completed:
            // Verify output file was created
            guard FileManager.default.fileExists(atPath: destinationURL.path) else {
                AppLogger.audio.error("AVAssetExportSession: Output file not created despite success status")
                throw AudioError.exportFailed
            }
            AppLogger.audio.info("AVAssetExportSession conversion successful: \(Int(sampleRate))Hz \(channels)ch → \(destinationURL.lastPathComponent, privacy: .public)")
            return (sampleRate: sampleRate, channels: channels)

        case .failed:
            let errorDesc = exportSession.error?.localizedDescription ?? "unknown error"
            AppLogger.audio.error("AVAssetExportSession: Export failed - \(errorDesc, privacy: .public)")
            throw exportSession.error ?? AudioError.exportFailed

        case .cancelled:
            AppLogger.audio.warning("AVAssetExportSession: Export was cancelled")
            throw AudioError.exportFailed

        default:
            AppLogger.audio.error("AVAssetExportSession: Unexpected status \(exportSession.status.rawValue)")
            throw AudioError.exportFailed
        }
    }

    // MARK: - WAV Fallback Conversion

    /// Convert audio to WAV format as a fallback when M4A/AAC conversion fails
    ///
    /// **Purpose:**
    /// WAV is a simple uncompressed format that Groq accepts. Since CAF and WAV are both
    /// PCM-based, this conversion is essentially just rewrapping the audio data in a
    /// different container - very reliable with minimal processing.
    ///
    /// **Why WAV as Fallback?**
    /// - CAF → WAV is trivial (both are PCM containers)
    /// - No codec encoding required (unlike AAC)
    /// - Almost impossible to fail
    /// - Groq accepts WAV: [flac mp3 mp4 mpeg mpga m4a ogg opus wav webm]
    ///
    /// **Trade-off:**
    /// WAV files are ~10x larger than M4A, but for a fallback scenario,
    /// reliability is more important than file size.
    ///
    /// **Parameters:**
    /// - `sourceURL`: Path to the source audio file (typically CAF)
    /// - `destinationURL`: Path where WAV should be written
    ///
    /// **Returns:**
    /// Tuple containing:
    /// - `sampleRate`: Source sample rate (e.g., 48000.0)
    /// - `channels`: Number of channels (1 = mono, 2 = stereo)
    ///
    /// **Throws:**
    /// - `AudioError.exportFailed`: If conversion fails
    ///
    /// **Performance:**
    /// - Typical conversion time: 0.05-0.2 seconds for 1 minute of audio
    /// - Faster than AAC since no encoding is required
    func convertAudioToWAV(from sourceURL: URL, to destinationURL: URL) async throws -> (sampleRate: Double, channels: Int) {
        // Clean up any previous failed attempts
        try? FileManager.default.removeItem(at: destinationURL)

        // Read source file
        let sourceFile: AVAudioFile
        do {
            sourceFile = try AVAudioFile(forReading: sourceURL)
        } catch {
            AppLogger.audio.error("WAV conversion: Failed to read source file: \(error.localizedDescription, privacy: .public)")
            throw AudioError.exportFailed
        }

        let sourceFormat = sourceFile.processingFormat
        let sampleRate = sourceFormat.sampleRate
        let channels = Int(sourceFormat.channelCount)

        // Create WAV output settings (16-bit PCM)
        // Using standard WAV format that's universally compatible
        guard let wavFormat = AVAudioFormat(
            commonFormat: .pcmFormatInt16,
            sampleRate: sampleRate,
            channels: AVAudioChannelCount(channels),
            interleaved: true
        ) else {
            AppLogger.audio.error("WAV conversion: Failed to create output format")
            throw AudioError.exportFailed
        }

        // Create destination file
        let destinationFile: AVAudioFile
        do {
            destinationFile = try AVAudioFile(
                forWriting: destinationURL,
                settings: wavFormat.settings,
                commonFormat: .pcmFormatInt16,
                interleaved: true
            )
        } catch {
            AppLogger.audio.error("WAV conversion: Failed to create destination file: \(error.localizedDescription, privacy: .public)")
            throw AudioError.exportFailed
        }

        // Read and write in chunks to be memory efficient
        let bufferSize: AVAudioFrameCount = 65536  // 64K frames per chunk
        // CRITICAL: Buffer must match destination's processingFormat, not source format
        // AVAudioFile.read() converts from source format to buffer format automatically
        // If we use sourceFormat (Float32) but destination expects Int16, we get static/garbage
        guard let buffer = AVAudioPCMBuffer(pcmFormat: destinationFile.processingFormat, frameCapacity: bufferSize) else {
            AppLogger.audio.error("WAV conversion: Failed to create buffer")
            throw AudioError.exportFailed
        }

        // Stream copy from source to destination
        do {
            while true {
                try sourceFile.read(into: buffer)
                if buffer.frameLength == 0 {
                    break  // End of file
                }
                try destinationFile.write(from: buffer)
            }
        } catch {
            // Check if we hit end of file (expected) or actual error
            if sourceFile.framePosition >= sourceFile.length {
                // Successfully read entire file - this is expected
            } else {
                AppLogger.audio.error("WAV conversion: Error during copy: \(error.localizedDescription, privacy: .public)")
                try? FileManager.default.removeItem(at: destinationURL)
                throw AudioError.exportFailed
            }
        }

        // Verify output file was created
        guard FileManager.default.fileExists(atPath: destinationURL.path) else {
            AppLogger.audio.error("WAV conversion: Output file not created")
            throw AudioError.exportFailed
        }

        AppLogger.audio.info("WAV conversion successful: \(Int(sampleRate))Hz \(channels)ch → \(destinationURL.lastPathComponent, privacy: .public)")

        return (sampleRate: sampleRate, channels: channels)
    }

    // MARK: - Helper Methods

    /// Extracts sample rate and channel count from an audio file URL
    ///
    /// **Purpose:**
    /// Utility method to inspect audio file metadata without loading the entire file.
    ///
    /// **Use Cases:**
    /// - Verify conversion output matches source
    /// - Log audio format for debugging
    /// - Validate transcription requirements
    ///
    /// **Parameters:**
    /// - `url`: Path to audio file
    ///
    /// **Returns:**
    /// Optional tuple of (sampleRate, channelCount), or nil if info cannot be determined
    nonisolated func getAudioFormatInfo(url: URL) -> (Double, Int)? {
        let asset = AVURLAsset(url: url)

        // Try reading from track format descriptions first (most reliable)
        if let track = asset.tracks(withMediaType: .audio).first {
            if let fdescAny = track.formatDescriptions.first {
                let fdesc = fdescAny as! CMAudioFormatDescription
                if let asbdPtr = CMAudioFormatDescriptionGetStreamBasicDescription(fdesc) {
                    let asbd = asbdPtr.pointee
                    return (asbd.mSampleRate, Int(asbd.mChannelsPerFrame))
                }
            }
        }

        // Fallback: try reading via AVAudioFile
        if let file = try? AVAudioFile(forReading: url) {
            return (file.fileFormat.sampleRate, Int(file.fileFormat.channelCount))
        }

        // Could not determine format
        return nil
    }

    // MARK: - Video Audio Extraction

    /// Extracts audio track from a video file and saves as M4A
    ///
    /// **Purpose:**
    /// Enables transcription of video files (MP4, MOV) by extracting their audio
    /// track to a standalone M4A file that can be sent to any transcription provider.
    ///
    /// **How It Works:**
    /// 1. Load video file as AVAsset
    /// 2. Verify it contains at least one audio track
    /// 3. Use existing AVAssetReader/Writer pipeline to extract audio
    /// 4. Output as AAC-encoded M4A for optimal size/compatibility
    ///
    /// **Why M4A Output?**
    /// - Smaller than WAV (~10x compression)
    /// - Accepted by all cloud transcription providers
    /// - Works with local Whisper models
    ///
    /// **Parameters:**
    /// - `videoURL`: Source video file (MP4, MOV, M4V, etc.)
    /// - `outputURL`: Destination for extracted audio (should have .m4a extension)
    ///
    /// **Returns:**
    /// Tuple containing:
    /// - `sampleRate`: Audio sample rate (e.g., 48000.0)
    /// - `channels`: Number of audio channels (1 = mono, 2 = stereo)
    /// - `duration`: Audio duration in seconds
    ///
    /// **Throws:**
    /// - `AudioError.noAudioTrack`: If video file contains no audio
    /// - `AudioError.exportFailed`: If extraction fails
    ///
    /// **Performance:**
    /// - Extraction is fast since we're only copying/encoding audio, not video
    /// - Memory efficient: streams samples instead of loading entire file
    /// - Typical time: 1-5 seconds for a 10-minute video
    func extractAudioFromVideo(from videoURL: URL, to outputURL: URL) async throws -> (sampleRate: Double, channels: Int, duration: TimeInterval) {
        AppLogger.audio.info("🎬 Extracting audio from video: \(videoURL.lastPathComponent, privacy: .public)")

        // STEP 1: Load video as AVAsset and verify it has audio
        let asset = AVURLAsset(url: videoURL)

        // Check for audio tracks before attempting extraction
        let hasAudio = await hasAudioTrack(url: videoURL)
        guard hasAudio else {
            AppLogger.audio.error("🎬 Video has no audio track: \(videoURL.lastPathComponent, privacy: .public)")
            throw AudioError.noAudioTrack
        }

        // STEP 2: Get video duration for return value
        let duration: TimeInterval
        if #available(macOS 12.0, *) {
            let durationCM = try await asset.load(.duration)
            duration = CMTimeGetSeconds(durationCM)
        } else {
            duration = CMTimeGetSeconds(asset.duration)
        }

        // STEP 3: Extract audio using existing AAC conversion pipeline
        // The convertAudioToAAC method already handles:
        // - Loading audio tracks from any AVAsset (including video containers)
        // - Streaming samples through AVAssetReader → AVAssetWriter
        // - AAC encoding with appropriate bitrate
        let result = try await convertAudioToAAC(from: videoURL, to: outputURL)

        AppLogger.audio.info("🎬 Audio extraction complete: \(String(format: "%.1f", duration))s, \(Int(result.sampleRate))Hz, \(result.channels)ch")

        return (sampleRate: result.sampleRate, channels: result.channels, duration: duration)
    }

    /// Checks if a media file contains at least one audio track
    ///
    /// **Purpose:**
    /// Validates that a video file has audio before attempting extraction.
    /// Provides early failure with a helpful error message rather than
    /// cryptic errors during the extraction process.
    ///
    /// **Use Cases:**
    /// - Validate video files before extraction
    /// - Show user-friendly error for silent videos (e.g., screen recordings without audio)
    /// - Pre-check imported files in FileTranscriptionFlow
    ///
    /// **Parameters:**
    /// - `url`: Path to the media file to check
    ///
    /// **Returns:**
    /// `true` if file contains at least one audio track, `false` otherwise
    ///
    /// **Note:**
    /// This method works with both audio and video files. For audio files,
    /// it will always return true (assuming valid audio format).
    func hasAudioTrack(url: URL) async -> Bool {
        let asset = AVURLAsset(url: url)

        // Load audio tracks asynchronously
        let tracks: [AVAssetTrack]
        if #available(macOS 12.0, *) {
            do {
                tracks = try await asset.loadTracks(withMediaType: .audio)
            } catch {
                AppLogger.audio.warning("Failed to load audio tracks: \(error.localizedDescription, privacy: .public)")
                return false
            }
        } else {
            tracks = asset.tracks(withMediaType: .audio)
        }

        return !tracks.isEmpty
    }
}
