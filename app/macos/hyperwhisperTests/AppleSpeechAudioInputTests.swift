//
//  AppleSpeechAudioInputTests.swift
//  hyperwhisperTests
//
//  Regression guard for issue #1515: Apple Speech awaited forever on a WAV with
//  no audio frames, because `AVAudioFile(forReading:)` opens such a file and
//  `SpeechAnalyzer.analyzeSequence` then never finishes. The provider now opens
//  its input through `AppleSpeechAudioInput.openForAnalysis`, which refuses a
//  0-frame file with `TranscriptionError.invalidAudioFormat` (Local API code
//  `AUDIO_DECODE_FAILED`) before the analyzer runs.
//
//  The fixtures are the two shapes from the issue, written byte by byte: a
//  44-byte header-only WAV whose `data` chunk is empty, and a WAV with a `fmt `
//  chunk, a `LIST` chunk and no `data` chunk at all. A short real WAV is the
//  control. No Speech assets and no macOS 26 are needed.
//

import AVFoundation
import Foundation
import Testing
import os
@testable import HyperWhisper

struct AppleSpeechAudioInputTests {

    private struct FixtureError: Error {}

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "AppleSpeechAudioInputTests")

    // MARK: - Fixtures

    private func temporaryURL() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("AppleSpeechAudioInputTests-\(UUID().uuidString).wav")
    }

    private func le32(_ value: UInt32) -> [UInt8] {
        withUnsafeBytes(of: value.littleEndian) { Array($0) }
    }

    private func le16(_ value: UInt16) -> [UInt8] {
        withUnsafeBytes(of: value.littleEndian) { Array($0) }
    }

    /// A 16-byte PCM `fmt ` chunk body: 16 kHz, mono, 16-bit.
    private func pcmFormatChunk() -> [UInt8] {
        let sampleRate: UInt32 = 16000
        let channels: UInt16 = 1
        let bitsPerSample: UInt16 = 16
        let blockAlign = channels * bitsPerSample / 8
        var body: [UInt8] = []
        body += le16(1) // PCM
        body += le16(channels)
        body += le32(sampleRate)
        body += le32(sampleRate * UInt32(blockAlign))
        body += le16(blockAlign)
        body += le16(bitsPerSample)
        return Array("fmt ".utf8) + le32(UInt32(body.count)) + body
    }

    private func riff(_ chunks: [UInt8]) -> Data {
        let payload = Array("WAVE".utf8) + chunks
        return Data(Array("RIFF".utf8) + le32(UInt32(payload.count)) + payload)
    }

    /// 44 bytes: RIFF header, `fmt `, and a `data` chunk of length 0.
    private func writeHeaderOnlyWAV() throws -> URL {
        let url = temporaryURL()
        let data = riff(pcmFormatChunk() + Array("data".utf8) + le32(0))
        #expect(data.count == 44)
        try data.write(to: url)
        return url
    }

    /// `fmt ` plus a sizeable `LIST` chunk, and no `data` chunk at all — the
    /// "audio bytes: 0" file from the issue.
    private func writeFormatOnlyWAV() throws -> URL {
        let url = temporaryURL()
        var listBody = Array("INFO".utf8) + Array("ISFT".utf8)
        let text = Array(String(repeating: "x", count: 4095).utf8) + [0] // even length, NUL-terminated
        listBody += le32(UInt32(text.count)) + text
        let list = Array("LIST".utf8) + le32(UInt32(listBody.count)) + listBody
        try riff(pcmFormatChunk() + list).write(to: url)
        return url
    }

    /// A real mono 16-bit PCM WAV holding a 440 Hz tone, closed on return.
    private func writeToneWAV(frames: AVAudioFrameCount) throws -> URL {
        let url = temporaryURL()
        let sampleRate = 16000.0
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
                throw FixtureError()
            }
            buffer.frameLength = frames
            for index in 0..<Int(frames) {
                channel[index] = 0.25 * sinf(2 * Float.pi * 440 * Float(index) / Float(sampleRate))
            }
            try file.write(from: buffer)
        }
        return url
    }

    private func expectRefusedAsInvalidAudioFormat(_ url: URL, _ label: String) {
        do {
            let file = try AppleSpeechAudioInput.openForAnalysis(url, logger: logger)
            Issue.record("\(label): opened with \(file.length) frames instead of being refused — Apple Speech would hang on it again (#1515).")
        } catch let error as TranscriptionError {
            guard case .invalidAudioFormat = error else {
                Issue.record("\(label): expected invalidAudioFormat, got \(error)")
                return
            }
        } catch {
            Issue.record("\(label): expected TranscriptionError.invalidAudioFormat, got \(error)")
        }
    }

    // MARK: - Tests

    @Test func aHeaderOnlyWAVIsRefused() throws {
        let url = try writeHeaderOnlyWAV()
        defer { try? FileManager.default.removeItem(at: url) }
        expectRefusedAsInvalidAudioFormat(url, "header-only WAV")
    }

    @Test func aWAVWithNoDataChunkIsRefused() throws {
        let url = try writeFormatOnlyWAV()
        defer { try? FileManager.default.removeItem(at: url) }
        expectRefusedAsInvalidAudioFormat(url, "fmt-only WAV")
    }

    @Test func aFileThatIsNotAudioIsStillRefused() throws {
        let url = temporaryURL()
        try Data("not audio".utf8).write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        expectRefusedAsInvalidAudioFormat(url, "text file")
    }

    @Test func aShortRealWAVOpensWithEveryFrame() throws {
        // A quarter of a second: well short of anything a speech model needs,
        // but it holds frames, so it must reach the analyzer.
        let url = try writeToneWAV(frames: 4000)
        defer { try? FileManager.default.removeItem(at: url) }

        let file = try AppleSpeechAudioInput.openForAnalysis(url, logger: logger)

        #expect(file.length == 4000, "length was \(file.length)")
    }

    /// The guard must run before the analyzer: the provider opens its input
    /// through the helper, and no longer opens the file a second way.
    @Test func theProviderOpensItsInputThroughTheGuardBeforeTheAnalyzer() throws {
        let source = "app/macos/hyperwhisper/Managers/Transcription/Providers/Local/AppleSpeechAnalyzerProvider.swift"
        let code = try ProductionSource.code(of: source)
        #expect(!code.contains("AVAudioFile(forReading:"), "the provider opens the file without the frame check")
        let open = try #require(code.range(of: "AppleSpeechAudioInput.openForAnalysis("))
        let analyze = try #require(code.range(of: "analyzer.analyzeSequence(from: audioFile)"))
        #expect(open.lowerBound < analyze.lowerBound)
    }
}
