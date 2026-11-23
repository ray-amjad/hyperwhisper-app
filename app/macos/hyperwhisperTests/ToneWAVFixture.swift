//
//  ToneWAVFixture.swift
//  hyperwhisperTests
//
//  The real, audible WAV file in one place: mono 16-bit PCM holding a 440 Hz
//  tone. Five test files had their own copy of this writer (issue #2852); one
//  writer, so a fix to it reaches all of them. The zero-frame shapes live in
//  `EmptyWAVFixture`, which writes RIFF bytes by hand on purpose.
//

import AVFoundation
import Foundation

enum ToneWAVFixture {

    struct BufferUnavailable: Error {}

    /// Writes a mono 16-bit PCM WAV of a 440 Hz tone at amplitude 0.25 to
    /// `url`, with its RIFF header finalised on return.
    @discardableResult
    static func write(to url: URL, sampleRate: Double = 16_000, frames: AVAudioFrameCount) throws -> URL {
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        // The scope releases the writer, which is what finalises the RIFF
        // header (AVAudioFile.close() is macOS 15 only).
        try autoreleasepool {
            let file = try AVAudioFile(forWriting: url, settings: settings)
            guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames),
                  let channel = buffer.floatChannelData?[0] else {
                throw BufferUnavailable()
            }
            buffer.frameLength = frames
            for index in 0..<Int(frames) {
                channel[index] = 0.25 * sinf(2 * Float.pi * 440 * Float(index) / Float(sampleRate))
            }
            try file.write(from: buffer)
        }
        return url
    }

    /// The same tone, `seconds` long, in the temporary directory as
    /// `<prefix>-<UUID>.wav`.
    static func write(prefix: String, sampleRate: Double = 16_000, seconds: Double) throws -> URL {
        try write(prefix: prefix, sampleRate: sampleRate, frames: AVAudioFrameCount(seconds * sampleRate))
    }

    /// The same tone, `frames` long, in the temporary directory as
    /// `<prefix>-<UUID>.wav`.
    static func write(prefix: String, sampleRate: Double = 16_000, frames: AVAudioFrameCount) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(prefix)-\(UUID().uuidString).wav")
        return try write(to: url, sampleRate: sampleRate, frames: frames)
    }

    /// The same tone, `seconds` long, as `name` in a new temporary folder
    /// `<folderPrefix>-<UUID>/`, for tests that read the file's name.
    static func write(named name: String, inFolderPrefixed folderPrefix: String, sampleRate: Double = 16_000, seconds: Double) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(folderPrefix)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return try write(
            to: folder.appendingPathComponent(name),
            sampleRate: sampleRate,
            frames: AVAudioFrameCount(seconds * sampleRate)
        )
    }
}
