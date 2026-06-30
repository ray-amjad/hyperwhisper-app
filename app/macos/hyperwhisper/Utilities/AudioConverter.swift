//
//  AudioConverter.swift
//  hyperwhisper
//
//  Robust audio conversion utility for whisper.cpp
//  Handles any common audio format and converts to Float32 mono at 16kHz
//

import Foundation
import AVFoundation
import CoreMedia
import os

/// Robust audio converter for whisper.cpp input preparation
/// 
/// KEY FEATURES:
/// - Chunked processing for low memory usage
/// - Handles all common formats (WAV/AIFF/MP3/AAC/M4A)
/// - Smart channel downmixing (stereo/5.1/7.1 → mono)
/// - Optional peak normalization
/// - Comprehensive error handling
/// 
/// DESIGN PRINCIPLES:
/// - Stream-based: Never loads entire file into memory
/// - Deterministic: Same input always produces same output
/// - Resilient: Handles VBR, unknown durations, corrupt files gracefully
/// - Observable: Progress callbacks for long conversions
class AudioConverter {
    
    // MARK: - Types
    
    /// Options for audio conversion
    struct ConversionOptions {
        /// Chunk size for reading audio (32K frames = ~0.7s @ 44.1kHz)
        let chunkSize: AVAudioFrameCount
        
        /// Target sample rate for whisper.cpp (must be 16kHz)
        let targetSampleRate: Double
        
        /// Whether to normalize audio to prevent clipping
        let normalize: Bool
        
        /// Optional progress callback (0.0 to 1.0)
        let progressHandler: ((Float) -> Void)?

        /// If true, a mid-file read error throws instead of returning the
        /// already-decoded prefix. Callers that must never silently drop the
        /// tail of a recording (e.g. SilenceTrimmer, which would otherwise
        /// write a truncated trimmed file) should set this so the failure
        /// propagates and they can fall back to the original audio. Defaults
        /// to false to preserve the lenient best-effort behavior used by
        /// transcription, where a partial result is better than none.
        let failOnPartialRead: Bool

        init(
            chunkSize: AVAudioFrameCount,
            targetSampleRate: Double,
            normalize: Bool,
            progressHandler: ((Float) -> Void)?,
            failOnPartialRead: Bool = false
        ) {
            self.chunkSize = chunkSize
            self.targetSampleRate = targetSampleRate
            self.normalize = normalize
            self.progressHandler = progressHandler
            self.failOnPartialRead = failOnPartialRead
        }

        /// Default options optimized for whisper.cpp
        static let `default` = ConversionOptions(
            chunkSize: 32768,
            targetSampleRate: 16000.0,
            normalize: false,
            progressHandler: nil
        )
    }
    
    /// Errors specific to audio conversion
    enum ConversionError: LocalizedError {
        case invalidAudioFile(URL)
        case unsupportedFormat(String)
        case conversionFailed(String)
        case emptyAudioFile

        var errorDescription: String? {
            switch self {
            case .invalidAudioFile(let url):
                return "Invalid audio file: \(url.lastPathComponent)"
            case .unsupportedFormat(let format):
                return "Unsupported audio format: \(format)"
            case .conversionFailed(let reason):
                return "Audio conversion failed: \(reason)"
            case .emptyAudioFile:
                return "Audio file is empty or corrupt"
            }
        }
    }
    
    // MARK: - Properties
    
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "AudioConverter")
    
    // MARK: - Public API
    
    /// Convert audio file to Float32 mono at 16kHz
    /// 
    /// CONVERSION PIPELINE:
    /// 1. Open file with AVAudioFile
    /// 2. Check for fast-path (already optimal format)
    /// 3. Create converter with appropriate settings
    /// 4. Process in chunks to minimize memory
    /// 5. Apply downmixing if multi-channel
    /// 6. Optionally normalize to prevent clipping
    /// 
    /// - Parameters:
    ///   - url: URL of the audio file to convert
    ///   - options: Conversion options (defaults to whisper.cpp optimal settings)
    /// - Returns: Audio samples as Float32 array at 16kHz mono
    /// - Throws: ConversionError or system errors
    func convert(from url: URL, options: ConversionOptions = .default) async throws -> [Float] {
        return try await withCheckedThrowingContinuation { continuation in
            Task {
                do {
                    let samples = try await performConversion(from: url, options: options)
                    continuation.resume(returning: samples)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }
    
    // MARK: - Private Implementation
    
    /// Perform the actual audio conversion
    private func performConversion(from url: URL, options: ConversionOptions) async throws -> [Float] {
        // STEP 1: Open audio file and validate
        let audioFile: AVAudioFile
        do {
            audioFile = try AVAudioFile(forReading: url)
        } catch {
            // AVAudioFile uses the strict ExtAudioFile/AudioFile parser, which rejects
            // many otherwise-playable m4a/mp4 containers with kAudioFileInvalidFileError
            // ('dta?'). The AVAssetReader (MediaToolbox) pipeline is far more lenient and
            // decodes these, so fall back to it before giving up (Sentry HYPERWHISPER-EX).
            logger.error("Failed to open audio file: \(error)")
            logger.warning("Falling back to AVAssetReader decode path")
            return try await convertViaAssetReader(from: url, options: options)
        }
        
        let sourceFormat = audioFile.processingFormat
        let totalFrames = audioFile.length
        
        // Validate file is not empty
        guard totalFrames > 0 else {
            logger.error("Audio file has zero frames")
            throw ConversionError.emptyAudioFile
        }

        logger.info("📂 Source: \(sourceFormat.sampleRate)Hz, \(sourceFormat.channelCount)ch, \(totalFrames) frames")
        
        // STEP 2: Create target format (16kHz mono Float32)
        guard let targetFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: options.targetSampleRate,
            channels: 1,
            interleaved: false
        ) else {
            throw ConversionError.conversionFailed("Failed to create target format")
        }
        
        // STEP 3: Check for fast-path (already optimal format)
        let needsConversion = sourceFormat.sampleRate != targetFormat.sampleRate ||
                             sourceFormat.channelCount != targetFormat.channelCount ||
                             sourceFormat.commonFormat != targetFormat.commonFormat
        
        if !needsConversion {
            logger.info("✨ Fast-path: Source already in optimal format")
            return try await readDirectly(from: audioFile, chunkSize: options.chunkSize)
        }
        
        // STEP 4: Create audio converter
        guard let converter = AVAudioConverter(from: sourceFormat, to: targetFormat) else {
            throw ConversionError.unsupportedFormat("\(sourceFormat)")
        }
        
        // Configure converter for quality
        converter.sampleRateConverterQuality = .max
        converter.sampleRateConverterAlgorithm = AVSampleRateConverterAlgorithm_Normal
        
        // For multi-channel sources, let converter handle initial downmix
        // We'll apply additional mixing if needed
        if sourceFormat.channelCount > 2 {
            // Use standard downmix for 5.1/7.1 to stereo first
            // CoreAudio will use default channel mapping automatically
            // No need to set channelMap as it's handled internally
        }
        
        // STEP 5: Chunked conversion loop
        var outputSamples: [Float] = []
        var framesRead: AVAudioFramePosition = 0
        let chunkSize = options.chunkSize
        
        // Reserve capacity for better performance
        let estimatedOutputFrames = Float(totalFrames) * Float(targetFormat.sampleRate / sourceFormat.sampleRate)
        outputSamples.reserveCapacity(Int(estimatedOutputFrames))
        
        logger.info("🔄 Starting chunked conversion...")
        
        while framesRead < totalFrames {
            // Calculate chunk size (don't exceed remaining frames)
            let remainingFrames = totalFrames - framesRead
            let framesToRead = min(AVAudioFrameCount(remainingFrames), chunkSize)
            
            // Read source chunk
            guard let sourceBuffer = AVAudioPCMBuffer(
                pcmFormat: sourceFormat,
                frameCapacity: framesToRead
            ) else {
                throw ConversionError.conversionFailed("Failed to allocate source buffer")
            }
            
            do {
                try audioFile.read(into: sourceBuffer, frameCount: framesToRead)
            } catch {
                logger.error("Failed to read audio chunk at frame \(framesRead): \(error)")
                // Callers that cannot tolerate a silently-truncated result opt out
                // of the lenient partial-return path and get the real failure.
                if options.failOnPartialRead {
                    throw ConversionError.conversionFailed("Read error: \(error)")
                }
                // Otherwise, if we've read some data, return what we have (best effort).
                if !outputSamples.isEmpty {
                    logger.warning("Returning partial conversion: \(outputSamples.count) samples")
                    break
                }
                throw ConversionError.conversionFailed("Read error: \(error)")
            }
            
            framesRead += AVAudioFramePosition(sourceBuffer.frameLength)
            let isFinalChunk = framesRead >= totalFrames
            
            // Convert chunk
            let convertedChunk = try convertChunk(
                sourceBuffer,
                using: converter,
                targetFormat: targetFormat,
                isFinalChunk: isFinalChunk
            )
            
            outputSamples.append(contentsOf: convertedChunk)
            
            // Report progress if handler provided
            if let progressHandler = options.progressHandler {
                let progress = Float(framesRead) / Float(totalFrames)
                progressHandler(progress)
            }
            
            // Log progress periodically (every 10%)
            let percentComplete = Int((Float(framesRead) / Float(totalFrames)) * 100)
            if percentComplete % 10 == 0 && percentComplete > 0 {
                logger.debug("Conversion progress: \(percentComplete)%")
            }
        }
        
        logger.info("✅ Conversion complete: \(outputSamples.count) samples @ \(targetFormat.sampleRate)Hz")
        
        // STEP 6: Apply normalization if requested
        if options.normalize && !outputSamples.isEmpty {
            normalizeAudio(&outputSamples)
        }
        
        // Final validation
        guard !outputSamples.isEmpty else {
            throw ConversionError.emptyAudioFile
        }
        
        return outputSamples
    }
    
    /// Fallback decode path for files that `AVAudioFile` cannot open.
    ///
    /// Some valid m4a/mp4/caf files fail `AVAudioFile(forReading:)` with
    /// `kAudioFileInvalidFileError` even though they decode fine through the
    /// AVAsset/MediaToolbox stack (this is the same observation that makes
    /// `AudioFileConverter` prefer `AVAssetReader`). We ask the reader to emit
    /// the whisper-ready format directly — 16 kHz mono Float32 — letting
    /// AVAssetReader handle sample-rate conversion and downmixing internally.
    private func convertViaAssetReader(from url: URL, options: ConversionOptions) async throws -> [Float] {
        let asset = AVURLAsset(url: url)

        let tracks: [AVAssetTrack]
        if #available(macOS 12.0, *) {
            tracks = (try? await asset.loadTracks(withMediaType: .audio)) ?? []
        } else {
            tracks = asset.tracks(withMediaType: .audio)
        }

        guard let audioTrack = tracks.first else {
            logger.error("AVAssetReader fallback: no audio tracks in file")
            throw ConversionError.invalidAudioFile(url)
        }

        let reader: AVAssetReader
        do {
            reader = try AVAssetReader(asset: asset)
        } catch {
            logger.error("AVAssetReader fallback: reader init failed: \(error.localizedDescription)")
            throw ConversionError.invalidAudioFile(url)
        }

        // Request whisper-ready PCM directly; AVAssetReader resamples/downmixes for us.
        let outputSettings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: options.targetSampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]

        let readerOutput = AVAssetReaderTrackOutput(track: audioTrack, outputSettings: outputSettings)
        readerOutput.alwaysCopiesSampleData = false

        guard reader.canAdd(readerOutput) else {
            logger.error("AVAssetReader fallback: cannot add reader output")
            throw ConversionError.invalidAudioFile(url)
        }
        reader.add(readerOutput)

        guard reader.startReading() else {
            logger.error("AVAssetReader fallback: startReading failed: \(reader.error?.localizedDescription ?? "unknown")")
            throw ConversionError.invalidAudioFile(url)
        }

        var outputSamples: [Float] = []
        logger.info("🔄 AVAssetReader fallback: decoding audio...")

        while reader.status == .reading {
            guard let sampleBuffer = readerOutput.copyNextSampleBuffer() else { break }
            defer { CMSampleBufferInvalidate(sampleBuffer) }

            guard let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { continue }

            let length = CMBlockBufferGetDataLength(blockBuffer)
            guard length > 0 else { continue }

            let floatCount = length / MemoryLayout<Float>.size
            var chunk = [Float](repeating: 0, count: floatCount)
            let copyStatus = chunk.withUnsafeMutableBytes { rawBuffer -> OSStatus in
                guard let base = rawBuffer.baseAddress else { return kCMBlockBufferStructureAllocationFailedErr }
                return CMBlockBufferCopyDataBytes(blockBuffer, atOffset: 0, dataLength: length, destination: base)
            }
            if copyStatus == kCMBlockBufferNoErr {
                outputSamples.append(contentsOf: chunk)
            }
        }

        if reader.status == .failed {
            logger.error("AVAssetReader fallback: read failed: \(reader.error?.localizedDescription ?? "unknown")")
            throw ConversionError.invalidAudioFile(url)
        }

        guard !outputSamples.isEmpty else {
            logger.error("AVAssetReader fallback: produced no samples")
            throw ConversionError.emptyAudioFile
        }

        if options.normalize {
            normalizeAudio(&outputSamples)
        }

        logger.info("✅ AVAssetReader fallback complete: \(outputSamples.count) samples @ \(options.targetSampleRate)Hz")
        return outputSamples
    }

    /// Convert a single chunk of audio
    private func convertChunk(
        _ sourceBuffer: AVAudioPCMBuffer,
        using converter: AVAudioConverter,
        targetFormat: AVAudioFormat,
        isFinalChunk: Bool
    ) throws -> [Float] {
        // Calculate output buffer size based on sample rate ratio
        let ratio = targetFormat.sampleRate / sourceBuffer.format.sampleRate
        let outputFrameCapacity = max(
            1,
            AVAudioFrameCount(Double(sourceBuffer.frameLength) * ratio * 1.1) // 10% margin
        )
        
        guard let outputBuffer = AVAudioPCMBuffer(
            pcmFormat: targetFormat,
            frameCapacity: outputFrameCapacity
        ) else {
            throw ConversionError.conversionFailed("Failed to allocate output buffer")
        }
        
        var error: NSError?
        var inputProvided = false
        var convertedSamples: [Float] = []
        
        // Conversion loop - pump until the converter has drained all available
        // output for this chunk. `convert` overwrites outputBuffer each pass.
        while true {
            outputBuffer.frameLength = 0

            let status = converter.convert(to: outputBuffer, error: &error) { inNumberOfPackets, outStatus in
                if !inputProvided {
                    // Provide input buffer
                    inputProvided = true
                    outStatus.pointee = .haveData
                    return sourceBuffer
                } else {
                    // No more input
                    outStatus.pointee = isFinalChunk ? .endOfStream : .noDataNow
                    return nil
                }
            }
            
            // Check for conversion errors
            if let error = error {
                throw ConversionError.conversionFailed(error.localizedDescription)
            }
            
            // Extract samples from output buffer
            let outputFrameLength = outputBuffer.frameLength
            if outputBuffer.frameLength > 0 {
                let samples = extractSamples(from: outputBuffer)
                convertedSamples.append(contentsOf: samples)
            }
            
            // Check conversion status
            switch status {
            case .haveData:
                guard outputFrameLength > 0 else {
                    throw ConversionError.conversionFailed("Converter reported data without output frames")
                }
                continue
            case .inputRanDry, .endOfStream:
                break
            case .error:
                throw ConversionError.conversionFailed("Converter returned error status")
            @unknown default:
                logger.warning("Unknown converter status: \(status.rawValue)")
                break
            }
            break
        }
        
        return convertedSamples
    }
    
    /// Extract Float samples from PCM buffer (handles multi-channel downmixing)
    private func extractSamples(from buffer: AVAudioPCMBuffer) -> [Float] {
        let frameLength = Int(buffer.frameLength)
        let channelCount = Int(buffer.format.channelCount)
        
        guard frameLength > 0,
              let channelData = buffer.floatChannelData else {
            return []
        }
        
        // CASE 1: Already mono
        if channelCount == 1 {
            return Array(UnsafeBufferPointer(start: channelData[0], count: frameLength))
        }
        
        // CASE 2: Multi-channel - downmix to mono
        var monoSamples = [Float](repeating: 0, count: frameLength)
        
        // DOWNMIX ALGORITHM:
        // - Stereo: (L + R) / 2
        // - 5.1/7.1: Average all channels equally
        // This preserves overall energy while preventing clipping
        let scaleFactor = 1.0 / Float(channelCount)
        
        for frame in 0..<frameLength {
            var sum: Float = 0
            for channel in 0..<channelCount {
                sum += channelData[channel][frame]
            }
            monoSamples[frame] = sum * scaleFactor
        }
        
        return monoSamples
    }
    
    /// Fast-path: Read audio directly when no conversion needed
    private func readDirectly(from audioFile: AVAudioFile, chunkSize: AVAudioFrameCount) async throws -> [Float] {
        var samples: [Float] = []
        let totalFrames = audioFile.length
        samples.reserveCapacity(Int(totalFrames))
        
        var framesRead: AVAudioFramePosition = 0
        
        while framesRead < totalFrames {
            let framesToRead = min(AVAudioFrameCount(totalFrames - framesRead), chunkSize)
            
            guard let buffer = AVAudioPCMBuffer(
                pcmFormat: audioFile.processingFormat,
                frameCapacity: framesToRead
            ) else {
                throw ConversionError.conversionFailed("Failed to allocate buffer")
            }
            
            try audioFile.read(into: buffer, frameCount: framesToRead)
            
            if let channelData = buffer.floatChannelData {
                let frameLength = Int(buffer.frameLength)
                samples.append(contentsOf: UnsafeBufferPointer(start: channelData[0], count: frameLength))
            }
            
            framesRead += AVAudioFramePosition(buffer.frameLength)
        }
        
        return samples
    }
    
    /// Normalize audio to prevent clipping (optional)
    /// 
    /// NORMALIZATION ALGORITHM:
    /// 1. Find peak absolute value in samples
    /// 2. If peak > 0.95, scale down to prevent clipping
    /// 3. If peak < 0.1, scale up for better SNR
    /// 4. Otherwise, leave unchanged
    private func normalizeAudio(_ samples: inout [Float]) {
        guard !samples.isEmpty else { return }
        
        // Find peak absolute value
        let peak = samples.reduce(0) { max($0, abs($1)) }
        
        guard peak > 0 else {
            logger.warning("Audio is silent (peak = 0)")
            return
        }
        
        // Determine scaling factor
        let targetPeak: Float = 0.95 // Leave 5% headroom
        let scaleFactor: Float
        
        if peak > 0.95 {
            // Prevent clipping
            scaleFactor = targetPeak / peak
            logger.info("📊 Normalizing: peak \(peak) → \(targetPeak) (preventing clipping)")
        } else if peak < 0.1 {
            // Boost quiet audio
            scaleFactor = min(targetPeak / peak, 10.0) // Cap at 10x gain
            logger.info("📊 Normalizing: peak \(peak) → \(peak * scaleFactor) (boosting quiet audio)")
        } else {
            // Audio is in good range, no normalization needed
            logger.debug("📊 No normalization needed: peak = \(peak)")
            return
        }
        
        // Apply scaling
        for i in 0..<samples.count {
            samples[i] *= scaleFactor
        }
    }
}
