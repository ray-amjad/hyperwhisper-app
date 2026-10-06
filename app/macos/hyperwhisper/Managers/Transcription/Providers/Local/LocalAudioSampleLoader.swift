import Foundation
import AVFoundation
import os

// LOCAL AUDIO SAMPLE LOADER:
//
// Decodes a recording into 16 kHz mono Float32 samples for the on-device
// providers that take a raw sample array (Nemotron, Qwen3 ASR). Both used to
// carry an identical private copy of this recipe; it lives here once so a fix
// lands in both.
//
// READ FAULTS ARE NOT END-OF-STREAM (#771):
// `.endOfStream` is the converter's word for "the audio finished normally", so
// `convert` returns without setting its `error` out-parameter. If a read fault
// is only reported as `.endOfStream`, the caller gets the frames converted so
// far and a silently truncated transcript. The input block therefore captures
// the read error, and the loader throws it after `convert` returns. A read that
// succeeds with zero frames is the real end of the file and stays silent.
//
// This is deliberately NOT `Utilities/AudioConverter.swift`: its chunked,
// downmixing path can produce different samples. Keep this recipe as it is.
enum LocalAudioSampleLoader {

    /// Fills `buffer` with the next chunk of `file`. Production reads the file;
    /// tests inject a reader that throws part way through the recording.
    typealias ChunkReader = (_ file: AVAudioFile, _ buffer: AVAudioPCMBuffer) throws -> Void

    static let targetSampleRate: Double = 16000

    /// Load `url` and convert it to 16 kHz mono Float32 samples.
    ///
    /// - Parameters:
    ///   - providerName: the `provider:` value for every `TranscriptionError` thrown.
    ///   - logger: the calling provider's logger. A read fault logs one line with
    ///     the error domain/code and the frame count only — never the path or audio.
    ///   - readChunk: the read step. Defaults to `AVAudioFile.read(into:)`.
    static func loadMono16kSamples(
        from url: URL,
        providerName: String,
        logger: Logger,
        readChunk: @escaping ChunkReader = { file, buffer in try file.read(into: buffer) }
    ) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: targetSampleRate, channels: 1, interleaved: false)!

        guard let converter = AVAudioConverter(from: file.processingFormat, to: targetFormat) else {
            throw TranscriptionError.providerNotAvailable(provider: providerName, reason: "Cannot create audio converter")
        }

        let ratio = targetSampleRate / file.processingFormat.sampleRate
        let estimatedFrames = AVAudioFrameCount(Double(file.length) * ratio) + 1024
        guard let outputBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: estimatedFrames) else {
            throw TranscriptionError.providerNotAvailable(provider: providerName, reason: "Cannot allocate audio buffer")
        }

        guard let inputBuffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: 4096) else {
            throw TranscriptionError.providerNotAvailable(provider: providerName, reason: "Cannot allocate input buffer for processing format \(file.processingFormat)")
        }

        var error: NSError?
        var readError: Error?
        var framesRead = 0
        converter.convert(to: outputBuffer, error: &error) { _, outStatus in
            do {
                try readChunk(file, inputBuffer)
                if inputBuffer.frameLength == 0 {
                    // Real end of file: silent by design.
                    outStatus.pointee = .endOfStream
                    return nil
                }
                framesRead += Int(inputBuffer.frameLength)
                outStatus.pointee = .haveData
                return inputBuffer
            } catch {
                // Stop the converter, but remember why: the throw below turns
                // this into a failure instead of a short sample array.
                readError = error
                let nsError = error as NSError
                logger.error("\(providerName, privacy: .public) audio read failed after \(framesRead) frames; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
                outStatus.pointee = .endOfStream
                return nil
            }
        }

        if let readError {
            throw TranscriptionError.providerNotAvailable(provider: providerName, reason: "Audio read failed: \(readError.localizedDescription)")
        }

        if let error {
            throw error
        }

        guard let channelData = outputBuffer.floatChannelData?[0] else {
            throw TranscriptionError.providerNotAvailable(provider: providerName, reason: "No audio data after conversion")
        }

        return Array(UnsafeBufferPointer(start: channelData, count: Int(outputBuffer.frameLength)))
    }
}
