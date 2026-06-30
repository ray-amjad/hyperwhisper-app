//
//  StreamingAudioCapture.swift
//  hyperwhisper
//
//  STREAMING AUDIO CAPTURE
//  Manages the AVAudioEngine lifecycle for capturing microphone audio during
//  real-time streaming transcription sessions.
//
//  ARCHITECTURE:
//  This class is extracted from StreamingTranscriptionClient to provide a reusable
//  audio capture component shared by all streaming providers (HyperWhisper Cloud,
//  Deepgram, ElevenLabs). Each provider shares the same audio pipeline — they only
//  differ in how audio data is encoded and sent over the WebSocket.
//
//  AUDIO PIPELINE:
//  ┌────────────────┐     ┌────────────────────┐     ┌─────────────────┐
//  │  Mic Input     │────▶│  Channel Mixing     │────▶│  PCM Conversion │
//  │  (any format)  │     │  (if >2 channels)   │     │  (16kHz Int16)  │
//  └────────────────┘     └────────────────────┘     └─────────────────┘
//                                                           │
//                                                           ▼
//                                                    onAudioData callback
//
//  AUDIO FORMAT:
//  - Output: 16kHz mono Int16 PCM (linear16)
//  - Chunk size: ~100ms buffers (1600 samples)
//  - This format is optimal for all supported STT providers (Deepgram, ElevenLabs)
//
//  MULTI-CHANNEL AUDIO HANDLING:
//  AVAudioConverter cannot properly downmix >2 channels — it just takes channels 1-2,
//  which may be silent on multi-track recorders like Portacapture X6.
//
//  For multi-channel input (>2 channels):
//  1. Manually mix all channels to mono (sum with 1/sqrt(N) scaling to prevent clipping)
//  2. Convert mono float32 → 16kHz mono Int16
//
//  For mono/stereo input:
//  - Direct conversion to 16kHz mono Int16
//
//  KEY BEHAVIOR DURING RECONNECT:
//  Audio capture continues running during WebSocket reconnection.
//  Audio data produced while disconnected is discarded by the client (not buffered).
//  This keeps the engine warm so reconnection latency is minimized.
//

import Foundation
import AVFAudio
import os

// MARK: - Streaming Audio Capture

/// Captures microphone audio and delivers 16kHz mono Int16 PCM data via callback.
///
/// USAGE:
/// ```swift
/// let capture = StreamingAudioCapture()
/// capture.onAudioData = { pcmData in
///     webSocket.send(.data(pcmData)) { _ in }
/// }
/// try await capture.start()
/// // ... audio flows via onAudioData ...
/// capture.stop()
/// ```
///
/// THREAD SAFETY:
/// - `start()` must be called from an async context (sets up engine on current thread)
/// - `stop()` can be called from any thread (AVAudioEngine.stop() is thread-safe)
/// - `onAudioData` is called from the audio tap's real-time thread
/// - Consumers should dispatch to appropriate queues if needed
class StreamingAudioCapture {
    private let targetSampleRate: Double

    // MARK: - Public Interface

    /// Callback invoked with each chunk of 16kHz mono Int16 PCM data.
    ///
    /// Called from the audio engine's real-time tap thread (~every 21ms at 48kHz input).
    /// The data is ready to send directly to providers that accept raw PCM (HW Cloud, Deepgram),
    /// or to be base64-encoded for providers that require JSON framing (ElevenLabs).
    var onAudioData: ((Data) -> Void)?

    /// Optional callback invoked with the raw input `AVAudioPCMBuffer` before
    /// any channel mixing or rate conversion. Used by consumers that want to
    /// do their own resampling (e.g. FluidAudio's `StreamingAsrManager`, which
    /// accepts any format and resamples internally).
    ///
    /// Called on the audio engine's real-time tap thread. Must not block.
    /// Fires for every tapped buffer regardless of `onAudioData`.
    var onAudioBuffer: ((AVAudioPCMBuffer) -> Void)?

    /// Callback invoked with 16kHz mono Float32 PCM samples.
    ///
    /// Used by the on-device Parakeet streaming session, which feeds these
    /// samples into FluidAudio's stateless `AsrManager.transcribe(_:source:)`.
    /// Called on the audio engine's real-time tap thread. Must not block.
    /// Fires only when this callback is non-nil — no RT work is done otherwise.
    var onFloat32Samples: (([Float]) -> Void)?

    /// Callback invoked with normalized input levels for UI metering.
    ///
    /// Called from the audio engine's real-time tap thread at most ~30 FPS.
    /// The receiver must hop to the main actor before touching UI state.
    var onAudioLevel: ((Float) -> Void)?

    /// Whether the audio engine is currently running and capturing audio.
    var isRunning: Bool {
        audioEngine?.isRunning ?? false
    }

    // MARK: - Private Properties

    /// Logger for audio capture operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "StreamingAudioCapture")

    /// Audio engine for microphone input
    private var audioEngine: AVAudioEngine?

    /// Converter for resampling to 16kHz mono Int16 PCM
    private var audioConverter: AVAudioConverter?

    /// Target format for all streaming providers (16kHz mono Int16)
    ///
    /// WHY 16kHz Int16:
    /// - 16kHz is the optimal sample rate for speech recognition (Deepgram, ElevenLabs)
    /// - Int16 (linear16) is the most widely supported PCM encoding
    /// - Mono reduces bandwidth by 50% vs stereo with no quality loss for speech
    private let targetFormat: AVAudioFormat

    /// Buffer for converted audio samples (16kHz mono Int16)
    private var pcmBuffer: AVAudioPCMBuffer?

    /// Target format for on-device streaming (FluidAudio expects 16kHz mono Float32).
    private let float32TargetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: 16000,
        channels: 1,
        interleaved: false
    )!

    /// Converter for resampling to 16kHz mono Float32 PCM (FluidAudio path).
    private var float32Converter: AVAudioConverter?

    /// Buffer for converted Float32 samples (16kHz mono Float32).
    private var float32Buffer: AVAudioPCMBuffer?

    /// Intermediate mono format for multi-channel downmixing (before sample rate conversion).
    ///
    /// Only set when input has >2 channels. Uses Float32 at the input's native sample rate
    /// for manual channel mixing before the final 16kHz Int16 conversion.
    private var intermediateFormat: AVAudioFormat?

    /// Buffer for intermediate mono audio after manual channel mixing.
    /// Only allocated when input has >2 channels.
    private var intermediateBuffer: AVAudioPCMBuffer?

    /// Number of input channels detected at setup time.
    /// Used to decide between direct conversion (1-2ch) and manual mixing (>2ch).
    private var inputChannelCount: UInt32 = 0

    /// Throttle for UI meter updates. Matches `SimpleRecorder`'s ~30 FPS cadence.
    private let audioLevelUpdateIntervalNanos: UInt64 = 33_000_000

    /// Last time an audio level was emitted, in monotonic nanoseconds.
    private var lastAudioLevelEmitTimeNanos: UInt64 = 0

    private let minMeterDb: Float = -60.0
    private let maxMeterDb: Float = 0.0

    init(targetSampleRate: Double = 16000) {
        self.targetSampleRate = targetSampleRate
        self.targetFormat = AVAudioFormat(
            commonFormat: .pcmFormatInt16,
            sampleRate: targetSampleRate,
            channels: 1,
            interleaved: true
        )!
    }

    // MARK: - Public Methods

    /// Start capturing audio from the microphone.
    ///
    /// FLOW:
    /// 1. Create AVAudioEngine instance
    /// 2. Detect input format and channel count
    /// 3. Create appropriate converter (direct or via intermediate mono format)
    /// 4. Install tap on input node to capture audio buffers
    /// 5. Start the engine
    ///
    /// After this method returns, audio data flows via the `onAudioData` callback.
    ///
    /// - Throws: `StreamingError.audioEngineError` if engine setup or start fails
    func start() async throws {
        logger.debug("Setting up audio engine...")

        // STEP 1: Create audio engine
        audioEngine = AVAudioEngine()

        guard let engine = audioEngine else {
            throw StreamingError.audioEngineError("Failed to create audio engine")
        }

        // STEP 2: Detect input format
        let inputNode = engine.inputNode
        let inputFormat = inputNode.outputFormat(forBus: 0)

        logger.debug("Input format: \(inputFormat.sampleRate, privacy: .public) Hz, \(inputFormat.channelCount, privacy: .public) channels, commonFormat: \(inputFormat.commonFormat.rawValue, privacy: .public)")

        inputChannelCount = inputFormat.channelCount

        // STEP 3: Create converter based on channel count
        //
        // MULTI-CHANNEL PATH (>2 channels):
        // Input (Nch float32 @ native rate) → Manual mix to mono → Convert to 16kHz Int16
        //
        // DIRECT PATH (1-2 channels):
        // Input (1-2ch @ native rate) → Convert directly to 16kHz mono Int16
        if inputFormat.channelCount > 2 {
            logger.info("Multi-channel input detected (\(inputFormat.channelCount, privacy: .public) channels), will manually mix to mono")

            // Create intermediate mono format at original sample rate (Float32 for mixing)
            guard let monoFormat = AVAudioFormat(
                commonFormat: .pcmFormatFloat32,
                sampleRate: inputFormat.sampleRate,
                channels: 1,
                interleaved: true
            ) else {
                throw StreamingError.audioEngineError("Failed to create mono format for mixing")
            }

            intermediateFormat = monoFormat

            // Buffer for intermediate mono audio (~100ms at input sample rate)
            let monoFrames = max(AVAudioFrameCount(inputFormat.sampleRate * 0.1), AVAudioFrameCount(16384))
            intermediateBuffer = AVAudioPCMBuffer(pcmFormat: monoFormat, frameCapacity: monoFrames)

            // Converter: mono float32 → 16kHz mono Int16
            audioConverter = AVAudioConverter(from: monoFormat, to: targetFormat)
        } else {
            // Direct conversion for mono/stereo input
            audioConverter = AVAudioConverter(from: inputFormat, to: targetFormat)
        }

        // Validate converter was created successfully
        guard audioConverter != nil else {
            throw StreamingError.audioEngineError("Failed to create audio converter from \(inputFormat) to \(targetFormat)")
        }

        // Create buffer for final converted samples (16kHz mono)
        // Buffer size for ~100ms of audio at 16kHz
        let bufferFrames = AVAudioFrameCount(targetSampleRate * 0.1)
        pcmBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: bufferFrames)

        // Create the Float32 converter for streaming consumers. Its source format
        // must match whatever bufferToConvert is in processAudioBuffer — the
        // intermediate mono format when channels > 2, else the raw input format.
        let float32SourceFormat = (inputFormat.channelCount > 2) ? intermediateFormat! : inputFormat
        float32Converter = AVAudioConverter(from: float32SourceFormat, to: float32TargetFormat)
        guard float32Converter != nil else {
            throw StreamingError.audioEngineError("Failed to create Float32 converter from \(float32SourceFormat) to \(float32TargetFormat)")
        }
        // Match the Int16 path's ~100ms capacity at 16kHz.
        float32Buffer = AVAudioPCMBuffer(pcmFormat: float32TargetFormat, frameCapacity: 1600)

        // STEP 4: Install tap on input node to capture audio
        // Buffer size of 1024 samples at ~48kHz = ~21ms of audio
        inputNode.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, _ in
            self?.processAudioBuffer(buffer)
        }

        // STEP 5: Start the engine
        try engine.start()
        logger.info("Audio capture started")
    }

    /// Stop audio capture and clean up all resources.
    ///
    /// CLEANUP ORDER:
    /// 1. Remove tap from input node (stops audio callback)
    /// 2. Stop the engine
    /// 3. Nil out all resources (engine, converter, buffers)
    ///
    /// Safe to call even if not currently running (no-op).
    func stop() {
        guard let engine = audioEngine else { return }

        engine.inputNode.removeTap(onBus: 0)
        engine.stop()

        audioEngine = nil
        audioConverter = nil
        pcmBuffer = nil
        float32Converter = nil
        float32Buffer = nil

        // Clean up multi-channel mixing resources
        intermediateBuffer = nil
        intermediateFormat = nil
        inputChannelCount = 0

        logger.info("Audio capture stopped")
    }

    // MARK: - Private Methods

    /// Process an audio buffer from the microphone tap.
    ///
    /// Called on the audio engine's real-time thread for every captured buffer.
    /// Converts the input to 16kHz mono Int16 PCM and delivers via onAudioData.
    ///
    /// MULTI-CHANNEL PATH (>2 channels):
    /// 1. Manually mix all channels to mono (sum with 1/sqrt(N) scaling to prevent clipping)
    /// 2. Convert mono float32 → 16kHz mono Int16
    ///
    /// DIRECT PATH (mono/stereo):
    /// - Convert directly to 16kHz mono Int16
    ///
    /// WHY MANUAL MIXING INSTEAD OF AVAudioConverter:
    /// AVAudioConverter's automatic downmix from 6ch to stereo/mono just takes channels 1-2,
    /// it doesn't actually mix all channels together. On multi-track recorders like the
    /// Portacapture X6, the main audio might be on any track (not necessarily 1-2), so
    /// channels 1-2 could be silent. By manually summing ALL channels, we capture audio
    /// from whichever track(s) the user is recording to.
    private func processAudioBuffer(_ inputBuffer: AVAudioPCMBuffer) {
        // Hand a COPY of the raw buffer to any consumer that wants its own
        // conversion (e.g. FluidAudio streaming). The tap's buffer is only
        // valid for the duration of this callback — async consumers would
        // otherwise read overwritten samples. ~4 KB memcpy per ~21 ms is
        // negligible versus CoreML inference.
        if let sink = onAudioBuffer, let copy = Self.copyBuffer(inputBuffer) {
            sink(copy)
        }

        // Skip all downstream conversion work if neither the Int16 cloud path
        // nor the Float32 on-device path is listening.
        let wantsInt16 = onAudioData != nil
        let wantsFloat32 = onFloat32Samples != nil
        let wantsAudioLevel = onAudioLevel != nil
        guard wantsInt16 || wantsFloat32 || wantsAudioLevel else { return }

        var bufferToConvert: AVAudioPCMBuffer = inputBuffer

        // If multi-channel, manually mix all channels to mono. Runs
        // unconditionally (for either consumer) so both the Int16 and
        // Float32 converters see the same mixed source buffer.
        if inputChannelCount > 2,
           let intBuffer = intermediateBuffer {

            // Get the input's float channel data (non-interleaved)
            guard let floatChannelData = inputBuffer.floatChannelData else {
                logger.warning("Multi-channel input buffer has no float channel data")
                return
            }

            guard let monoData = intBuffer.floatChannelData else {
                logger.warning("Intermediate buffer has no float channel data")
                return
            }

            let frameCount = Int(inputBuffer.frameLength)
            let channelCount = Int(inputChannelCount)

            // Clamp to destination capacity — OOB on RT thread otherwise (issue #249)
            let frames = min(frameCount, Int(intBuffer.frameCapacity))

            // Scale factor to prevent clipping: 1/sqrt(N) for N channels
            let scale = 1.0 / sqrt(Float(channelCount))

            // Sum all channels into mono with scaling
            let monoPtr = monoData[0]

            // First, zero out the mono buffer
            for i in 0..<frames {
                monoPtr[i] = 0
            }

            // Sum all channels
            for ch in 0..<channelCount {
                let channelPtr = floatChannelData[ch]
                for i in 0..<frames {
                    monoPtr[i] += channelPtr[i] * scale
                }
            }

            intBuffer.frameLength = AVAudioFrameCount(frames)
            bufferToConvert = intBuffer
        }

        emitAudioLevelIfNeeded(from: bufferToConvert)

        // Float32 emission path (on-device Parakeet / FluidAudio). Runs before
        // the Int16 conversion so it sees the same (already-mixed) source
        // buffer. Gated on the callback being set to avoid wasted RT work.
        //
        // Drain the converter in a loop: when the source buffer is larger
        // than the output capacity (oversized tap frames, sample-rate jumps),
        // a single convert() call only emits the first output chunk and
        // leaves the remainder queued inside AVAudioConverter — that audio
        // would otherwise be emitted on a later, possibly silent callback,
        // delaying or losing samples (issue #249 follow-up).
        if let f32Sink = onFloat32Samples,
           let f32Converter = float32Converter,
           let f32Buffer = float32Buffer {
            var providedInput = false
            while true {
                f32Buffer.frameLength = 0
                var f32Error: NSError?
                let status = f32Converter.convert(to: f32Buffer, error: &f32Error) { _, outStatus in
                    if providedInput {
                        outStatus.pointee = .noDataNow
                        return nil
                    }
                    providedInput = true
                    outStatus.pointee = .haveData
                    return bufferToConvert
                }
                if status == .error {
                    logger.warning("Float32 audio conversion error: \(f32Error?.localizedDescription ?? "unknown", privacy: .public)")
                    break
                }
                guard let channelData = f32Buffer.floatChannelData else { break }
                let frames = Int(f32Buffer.frameLength)
                if frames == 0 { break }
                let samples = Array(UnsafeBufferPointer(start: channelData[0], count: frames))
                f32Sink(samples)
            }
        }

        // Int16 emission path (cloud streaming providers). Skip if no listener.
        guard wantsInt16,
              let finalConverter = audioConverter,
              let outputBuffer = pcmBuffer else {
            return
        }

        // Convert to final format (16kHz mono Int16). Drain loop — same
        // reasoning as the Float32 path above (issue #249 follow-up).
        var providedInput = false
        while true {
            outputBuffer.frameLength = 0
            var error: NSError?
            let status = finalConverter.convert(to: outputBuffer, error: &error) { _, outStatus in
                if providedInput {
                    outStatus.pointee = .noDataNow
                    return nil
                }
                providedInput = true
                outStatus.pointee = .haveData
                return bufferToConvert
            }
            if status == .error {
                logger.warning("Audio conversion error: \(error?.localizedDescription ?? "unknown", privacy: .public)")
                return
            }
            guard let int16Data = outputBuffer.int16ChannelData else { return }
            let frameLength = Int(outputBuffer.frameLength)
            if frameLength == 0 { return }

            let byteCount = frameLength * MemoryLayout<Int16>.size
            let data = Data(bytes: int16Data[0], count: byteCount)
            onAudioData?(data)
        }
    }

    /// Emit a normalized meter value without doing UI work on the audio thread.
    private func emitAudioLevelIfNeeded(from buffer: AVAudioPCMBuffer) {
        guard let sink = onAudioLevel else { return }

        let now = DispatchTime.now().uptimeNanoseconds
        if lastAudioLevelEmitTimeNanos != 0,
           now - lastAudioLevelEmitTimeNanos < audioLevelUpdateIntervalNanos {
            return
        }
        lastAudioLevelEmitTimeNanos = now

        guard let rms = Self.rmsLevel(from: buffer), rms > 0 else {
            sink(0)
            return
        }

        let power = 20.0 * log10(max(rms, Float.leastNonzeroMagnitude))
        let normalized: Float
        if power <= minMeterDb {
            normalized = 0.0
        } else if power >= maxMeterDb {
            normalized = 1.0
        } else {
            normalized = (power - minMeterDb) / (maxMeterDb - minMeterDb)
        }

        sink(normalized)
    }

    /// Compute RMS from the tapped buffer without allocating on the audio thread.
    private static func rmsLevel(from buffer: AVAudioPCMBuffer) -> Float? {
        let frameCount = Int(buffer.frameLength)
        let channelCount = Int(buffer.format.channelCount)
        guard frameCount > 0, channelCount > 0 else { return nil }

        switch buffer.format.commonFormat {
        case .pcmFormatFloat32:
            guard let channelData = buffer.floatChannelData else { return nil }
            var sumSquares: Double = 0
            let sampleCount = frameCount * channelCount

            if buffer.format.isInterleaved {
                let samples = channelData[0]
                for i in 0..<sampleCount {
                    let sample = Double(samples[i])
                    sumSquares += sample * sample
                }
            } else {
                for channel in 0..<channelCount {
                    let samples = channelData[channel]
                    for frame in 0..<frameCount {
                        let sample = Double(samples[frame])
                        sumSquares += sample * sample
                    }
                }
            }

            return Float(sqrt(sumSquares / Double(sampleCount)))

        case .pcmFormatInt16:
            guard let channelData = buffer.int16ChannelData else { return nil }
            var sumSquares: Double = 0
            let sampleCount = frameCount * channelCount
            let scale = Double(Int16.max)

            if buffer.format.isInterleaved {
                let samples = channelData[0]
                for i in 0..<sampleCount {
                    let sample = Double(samples[i]) / scale
                    sumSquares += sample * sample
                }
            } else {
                for channel in 0..<channelCount {
                    let samples = channelData[channel]
                    for frame in 0..<frameCount {
                        let sample = Double(samples[frame]) / scale
                        sumSquares += sample * sample
                    }
                }
            }

            return Float(sqrt(sumSquares / Double(sampleCount)))

        case .pcmFormatInt32:
            guard let channelData = buffer.int32ChannelData else { return nil }
            var sumSquares: Double = 0
            let sampleCount = frameCount * channelCount
            let scale = Double(Int32.max)

            if buffer.format.isInterleaved {
                let samples = channelData[0]
                for i in 0..<sampleCount {
                    let sample = Double(samples[i]) / scale
                    sumSquares += sample * sample
                }
            } else {
                for channel in 0..<channelCount {
                    let samples = channelData[channel]
                    for frame in 0..<frameCount {
                        let sample = Double(samples[frame]) / scale
                        sumSquares += sample * sample
                    }
                }
            }

            return Float(sqrt(sumSquares / Double(sampleCount)))

        default:
            return nil
        }
    }

    /// Deep-copy an `AVAudioPCMBuffer` so async consumers don't read
    /// samples after the tap's buffer memory has been recycled.
    private static func copyBuffer(_ src: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard let copy = AVAudioPCMBuffer(pcmFormat: src.format, frameCapacity: src.frameCapacity) else {
            return nil
        }
        copy.frameLength = src.frameLength
        let frames = Int(src.frameLength)
        let channels = Int(src.format.channelCount)
        if let s = src.floatChannelData, let d = copy.floatChannelData {
            let bytes = frames * MemoryLayout<Float>.size
            for ch in 0..<channels { memcpy(d[ch], s[ch], bytes) }
        } else if let s = src.int16ChannelData, let d = copy.int16ChannelData {
            let bytes = frames * MemoryLayout<Int16>.size
            for ch in 0..<channels { memcpy(d[ch], s[ch], bytes) }
        } else if let s = src.int32ChannelData, let d = copy.int32ChannelData {
            let bytes = frames * MemoryLayout<Int32>.size
            for ch in 0..<channels { memcpy(d[ch], s[ch], bytes) }
        }
        return copy
    }
}
