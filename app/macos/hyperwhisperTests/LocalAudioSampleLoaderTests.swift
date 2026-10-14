//
//  LocalAudioSampleLoaderTests.swift
//  hyperwhisperTests
//
//  Regression guard for issue #771: a read fault inside the AVAudioConverter
//  input block of the shared local-provider loader (Nemotron, Qwen3 ASR) must
//  throw, not end the stream and hand back a silently truncated sample array.
//
//  WHY THE READ FAULT IS INJECTED:
//  ===============================
//  A WAV file cut short on disk does not reliably make `AVAudioFile.read(into:)`
//  throw: the file can simply yield fewer frames, which the loader correctly
//  treats as the end of the recording. A test built on such a file could pass
//  for the wrong reason. The loader therefore takes its read step as a seam.
//  These tests write a real WAV, run the real converter over it, and inject a
//  reader that performs the real `AVAudioFile` read for the first chunks and
//  then throws — the exact shape of a disk fault half way through a recording.
//
//  Issue #1516: a WAV that opens with no audio frames (an empty `data` chunk,
//  or a `fmt ` chunk and no `data` chunk) must be refused with
//  `TranscriptionError.invalidAudioFormat`, not loaded as an empty array that
//  Nemotron then reports as an empty, successful transcript. Those fixtures are
//  written byte by byte, as in AppleSpeechAudioInputTests.
//

import AVFoundation
import Foundation
import Testing
import os
@testable import HyperWhisper

struct LocalAudioSampleLoaderTests {

    private struct FixtureError: Error {}
    private struct InjectedReadFault: Error {}

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LocalAudioSampleLoaderTests")

    /// Writes a mono 16-bit PCM WAV holding a 440 Hz tone, and closes it.
    private func makeWAV(sampleRate: Double, frames: AVAudioFrameCount) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("LocalAudioSampleLoaderTests-\(UUID().uuidString).wav")
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

    /// Runs the loader with a reader that throws on read call number `failingCall`.
    /// Returns how many times the reader was called, and records an issue unless
    /// the loader threw the #771 error.
    private func expectReadFaultThrows(sampleRate: Double, frames: AVAudioFrameCount, failingCall: Int) throws -> Int {
        let url = try makeWAV(sampleRate: sampleRate, frames: frames)
        defer { try? FileManager.default.removeItem(at: url) }

        var calls = 0
        let failingReader: LocalAudioSampleLoader.ChunkReader = { file, buffer in
            calls += 1
            if calls >= failingCall {
                throw InjectedReadFault()
            }
            try file.read(into: buffer)
        }

        do {
            let samples = try LocalAudioSampleLoader.loadMono16kSamples(
                from: url,
                providerName: "Nemotron",
                logger: logger,
                readChunk: failingReader
            )
            Issue.record("A read fault returned \(samples.count) samples instead of throwing — the truncation is silent again (#771).")
        } catch let error as TranscriptionError {
            guard case .providerNotAvailable(let provider, let reason) = error else {
                Issue.record("Expected providerNotAvailable, got \(error)")
                return calls
            }
            #expect(provider == "Nemotron")
            #expect(reason?.hasPrefix("Audio read failed:") == true, "reason was \(reason ?? "nil")")
        } catch {
            Issue.record("Expected TranscriptionError.providerNotAvailable, got \(error)")
        }
        return calls
    }

    @Test func aReadFaultMidRecordingThrowsInsteadOfReturningAShortArray() throws {
        // 1 s at 48 kHz is 12 input chunks of 4096 frames; the 4th read fails,
        // after 3 real chunks have already been resampled into the output.
        let calls = try expectReadFaultThrows(sampleRate: 48000, frames: 48000, failingCall: 4)
        #expect(calls == 4, "the converter stopped pulling input after \(calls) reads")
    }

    @Test func aReadFaultOnTheFirstChunkThrows() throws {
        let calls = try expectReadFaultThrows(sampleRate: 16000, frames: 16000, failingCall: 1)
        #expect(calls == 1)
    }

    @Test func aValidWAVAtTheTargetRateLoadsEveryFrame() throws {
        let url = try makeWAV(sampleRate: 16000, frames: 16000)
        defer { try? FileManager.default.removeItem(at: url) }

        let samples = try LocalAudioSampleLoader.loadMono16kSamples(from: url, providerName: "Nemotron", logger: logger)

        #expect(samples.count == 16000, "got \(samples.count) samples")
        #expect(samples.contains { abs($0) > 0.1 }, "the decoded tone is silent")
    }

    @Test func aWAVThatEndsExactlyOnAChunkBoundaryLoadsWithoutAReadAtTheEnd() throws {
        // 8192 frames is exactly 2 input chunks of 4096, so the 2nd real read
        // leaves the file position at its end. AVAudioFile throws on a read
        // from there, so the loader must end the stream without a 3rd read.
        let url = try makeWAV(sampleRate: 16000, frames: 8192)
        defer { try? FileManager.default.removeItem(at: url) }

        var calls = 0
        let countingReader: LocalAudioSampleLoader.ChunkReader = { file, buffer in
            calls += 1
            try file.read(into: buffer)
        }

        let samples = try LocalAudioSampleLoader.loadMono16kSamples(
            from: url,
            providerName: "Nemotron",
            logger: logger,
            readChunk: countingReader
        )

        #expect(samples.count == 8192, "got \(samples.count) samples")
        #expect(calls == 2, "the reader was called \(calls) times for 2 chunks")
    }

    @Test func aValidWAVIsResampledInFull() throws {
        let url = try makeWAV(sampleRate: 48000, frames: 48000)
        defer { try? FileManager.default.removeItem(at: url) }

        let samples = try LocalAudioSampleLoader.loadMono16kSamples(from: url, providerName: "Qwen3 ASR", logger: logger)

        // 1 s of audio at 16 kHz. The resampler may differ by a few frames at
        // the edges; a truncated decode would be thousands of frames short.
        #expect(samples.count >= 15900 && samples.count <= 16100, "got \(samples.count) samples")
    }

    // MARK: - Files with no audio frames (#1516)

    private func le32(_ value: UInt32) -> [UInt8] {
        withUnsafeBytes(of: value.littleEndian) { Array($0) }
    }

    private func le16(_ value: UInt16) -> [UInt8] {
        withUnsafeBytes(of: value.littleEndian) { Array($0) }
    }

    /// A RIFF/WAVE file whose `fmt ` chunk says 16 kHz mono 16-bit PCM,
    /// followed by `chunks`.
    private func writeRIFF(_ chunks: [UInt8]) throws -> URL {
        var fmt: [UInt8] = []
        fmt += le16(1) // PCM
        fmt += le16(1) // mono
        fmt += le32(16000)
        fmt += le32(32000)
        fmt += le16(2)
        fmt += le16(16)
        let payload = Array("WAVE".utf8) + Array("fmt ".utf8) + le32(UInt32(fmt.count)) + fmt + chunks
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("LocalAudioSampleLoaderTests-\(UUID().uuidString).wav")
        try Data(Array("RIFF".utf8) + le32(UInt32(payload.count)) + payload).write(to: url)
        return url
    }

    private func expectRefusedAsInvalidAudioFormat(_ url: URL, providerName: String, _ label: String) {
        do {
            let samples = try LocalAudioSampleLoader.loadMono16kSamples(from: url, providerName: providerName, logger: logger)
            Issue.record("\(label): loaded \(samples.count) samples instead of being refused — \(providerName) would report an empty transcript as a success (#1516).")
        } catch let error as TranscriptionError {
            guard case .invalidAudioFormat = error else {
                Issue.record("\(label): expected invalidAudioFormat, got \(error)")
                return
            }
        } catch {
            Issue.record("\(label): expected TranscriptionError.invalidAudioFormat, got \(error)")
        }
    }

    @Test(arguments: ["Nemotron", "Qwen3 ASR"])
    func aWAVWithNoDataChunkIsRefused(providerName: String) throws {
        // The issue's file: `fmt `, a large `LIST` chunk, and no `data` chunk.
        // It is far past Nemotron's 5,000-byte pre-flight guard.
        var listBody = Array("INFO".utf8) + Array("ISFT".utf8)
        let text = Array(String(repeating: "x", count: 8191).utf8) + [0] // even length, NUL-terminated
        listBody += le32(UInt32(text.count)) + text
        let url = try writeRIFF(Array("LIST".utf8) + le32(UInt32(listBody.count)) + listBody)
        defer { try? FileManager.default.removeItem(at: url) }

        expectRefusedAsInvalidAudioFormat(url, providerName: providerName, "fmt-only WAV")
    }

    @Test func aWAVWithAnEmptyDataChunkIsRefused() throws {
        let url = try writeRIFF(Array("data".utf8) + le32(0))
        defer { try? FileManager.default.removeItem(at: url) }

        expectRefusedAsInvalidAudioFormat(url, providerName: "Nemotron", "header-only WAV")
    }

    @Test func aOneChunkWAVStillLoads() throws {
        // The smallest real input the guard must let through: one 4096-frame chunk.
        let url = try makeWAV(sampleRate: 16000, frames: 4096)
        defer { try? FileManager.default.removeItem(at: url) }

        let samples = try LocalAudioSampleLoader.loadMono16kSamples(from: url, providerName: "Nemotron", logger: logger)

        #expect(samples.count == 4096, "got \(samples.count) samples")
    }
}
