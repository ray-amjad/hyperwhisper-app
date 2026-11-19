//
//  AudioFileConverterPumpTests.swift
//  hyperwhisperTests
//
//  Issue #1116: convertAudioToAAC and convertAudioToWAV share one reader → writer
//  sample pump. These tests drive both converters end to end through their
//  public API, so they pass the same before and after the pump was shared:
//
//  - a real tone CAF converts to a readable file with the returned format and
//    about the source's duration (every sample went through the pump);
//  - a failure throws and leaves no file at the destination.
//
//  Every file lives in a fresh temporary folder that the test removes. Never a
//  real user path.
//

import AVFoundation
import Foundation
import Testing
@testable import HyperWhisper

struct AudioFileConverterPumpTests {

    private struct FixtureError: Error {}

    // MARK: - Fixtures

    /// A fresh, empty folder under the temporary directory.
    private static func makeFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("AudioFileConverterPumpTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// Writes a 16-bit PCM CAF holding a 440 Hz tone on every channel, and closes it.
    private static func writeToneCAF(to url: URL, sampleRate: Double, channels: AVAudioChannelCount, seconds: Double) throws {
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: Int(channels),
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ]
        let frames = AVAudioFrameCount(sampleRate * seconds)
        // The scope releases the writer, which is what finalises the file
        // (AVAudioFile.close() is macOS 15 only).
        try autoreleasepool {
            let file = try AVAudioFile(forWriting: url, settings: settings)
            guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames),
                  let channelData = buffer.floatChannelData else {
                throw FixtureError()
            }
            buffer.frameLength = frames
            for channel in 0..<Int(channels) {
                for index in 0..<Int(frames) {
                    channelData[channel][index] = 0.25 * sinf(2 * Float.pi * 440 * Float(index) / Float(sampleRate))
                }
            }
            try file.write(from: buffer)
        }
    }

    /// Duration in seconds of a file AVAudioFile can open.
    private static func duration(of file: AVAudioFile) -> Double {
        Double(file.length) / file.fileFormat.sampleRate
    }

    /// Runs `convert` and records an issue unless it threw `AudioError.exportFailed`.
    private static func expectExportFailed(_ label: String, _ convert: () async throws -> Void) async {
        do {
            try await convert()
            Issue.record("\(label): converted instead of throwing")
        } catch AudioError.exportFailed {
            // Expected.
        } catch {
            Issue.record("\(label): expected AudioError.exportFailed, got \(error)")
        }
    }

    // MARK: - AAC

    private func expectAACConversion(sampleRate: Double, channels: AVAudioChannelCount, expectedBitrate: Int) async throws {
        let folder = try Self.makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = folder.appendingPathComponent("tone.caf")
        let destination = folder.appendingPathComponent("tone.m4a")
        let seconds = 1.0
        try Self.writeToneCAF(to: source, sampleRate: sampleRate, channels: channels, seconds: seconds)

        let result = try await AudioFileConverter().convertAudioToAAC(from: source, to: destination)

        #expect(result.sampleRate == sampleRate)
        #expect(result.channels == Int(channels))
        #expect(result.bitrate == expectedBitrate)

        #expect(FileManager.default.fileExists(atPath: destination.path))
        let output = try AVAudioFile(forReading: destination)
        #expect(output.fileFormat.sampleRate == sampleRate)
        #expect(output.fileFormat.channelCount == channels)
        // AAC encodes in 1024-frame packets with encoder priming, so the length can
        // differ by a few packets; a pump that dropped buffers would be far shorter.
        let outputSeconds = Self.duration(of: output)
        #expect(abs(outputSeconds - seconds) <= 0.1, "output is \(outputSeconds) s for a \(seconds) s source")
    }

    @Test func aMonoToneConvertsToAACWithTheMonoBitrate() async throws {
        try await expectAACConversion(sampleRate: 48_000, channels: 1, expectedBitrate: 32_000)
    }

    @Test func aStereoToneConvertsToAACWithTheStereoBitrate() async throws {
        try await expectAACConversion(sampleRate: 44_100, channels: 2, expectedBitrate: 64_000)
    }

    // MARK: - WAV

    private func expectWAVConversion(sampleRate: Double, channels: AVAudioChannelCount) async throws {
        let folder = try Self.makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = folder.appendingPathComponent("tone.caf")
        let destination = folder.appendingPathComponent("tone.wav")
        let seconds = 1.0
        try Self.writeToneCAF(to: source, sampleRate: sampleRate, channels: channels, seconds: seconds)

        let result = try await AudioFileConverter().convertAudioToWAV(from: source, to: destination)

        // The format actually written, not the source format.
        #expect(result.sampleRate == 16_000)
        #expect(result.channels == 1)

        #expect(FileManager.default.fileExists(atPath: destination.path))
        let output = try AVAudioFile(forReading: destination)
        #expect(output.fileFormat.sampleRate == 16_000)
        #expect(output.fileFormat.channelCount == 1)
        #expect(output.processingFormat.sampleRate == 16_000)
        #expect(output.processingFormat.channelCount == 1)
        // The resampler may differ by a few frames at the edges; a pump that
        // dropped buffers would be thousands of frames short.
        let outputSeconds = Self.duration(of: output)
        #expect(abs(outputSeconds - seconds) <= 0.05, "output is \(outputSeconds) s for a \(seconds) s source")

        // The tone survived the resample and downmix: the output is not silence.
        let frames = AVAudioFrameCount(output.length)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: output.processingFormat, frameCapacity: frames) else {
            throw FixtureError()
        }
        try output.read(into: buffer, frameCount: frames)
        guard let samples = buffer.floatChannelData?[0] else {
            throw FixtureError()
        }
        let peak = (0..<Int(buffer.frameLength)).reduce(Float(0)) { max($0, abs(samples[$1])) }
        #expect(peak > 0.1, "the converted tone peaks at \(peak)")
    }

    @Test func aStereoToneConvertsTo16kMonoWAV() async throws {
        try await expectWAVConversion(sampleRate: 44_100, channels: 2)
    }

    @Test func aMonoToneConvertsTo16kMonoWAV() async throws {
        try await expectWAVConversion(sampleRate: 48_000, channels: 1)
    }

    // MARK: - Failures

    /// The destination's parent is a regular file, so no writer can create the
    /// output. Each converter must throw exportFailed — from the writer's init or
    /// from the pump's startWriting guard — and leave nothing at the destination.
    @Test func anUnwritableDestinationThrowsExportFailedFromBothConverters() async throws {
        let folder = try Self.makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = folder.appendingPathComponent("tone.caf")
        try Self.writeToneCAF(to: source, sampleRate: 48_000, channels: 1, seconds: 0.5)
        let notAFolder = folder.appendingPathComponent("not-a-folder")
        try Data("a regular file".utf8).write(to: notAFolder)

        let aacDestination = notAFolder.appendingPathComponent("out.m4a")
        await Self.expectExportFailed("AAC") {
            _ = try await AudioFileConverter().convertAudioToAAC(from: source, to: aacDestination)
        }
        #expect(!FileManager.default.fileExists(atPath: aacDestination.path))

        let wavDestination = notAFolder.appendingPathComponent("out.wav")
        await Self.expectExportFailed("WAV") {
            _ = try await AudioFileConverter().convertAudioToWAV(from: source, to: wavDestination)
        }
        #expect(!FileManager.default.fileExists(atPath: wavDestination.path))
    }

    /// A text file named .caf is not audio. Each converter must throw and leave
    /// no file at the destination — not even the stale one a previous failed
    /// attempt left there. AVFoundation may refuse the file while loading its
    /// tracks, before the converter's own checks run, so its own error is
    /// accepted too; any AudioError must be exportFailed.
    @Test(arguments: ["m4a", "wav"])
    func aNonAudioSourceThrowsAndLeavesNoFile(destinationExtension: String) async throws {
        let folder = try Self.makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = folder.appendingPathComponent("not-audio.caf")
        let text = String(repeating: "This file holds text, not audio samples.\n", count: 200)
        try Data(text.utf8).write(to: source)
        let destination = folder.appendingPathComponent("out.\(destinationExtension)")
        try Data("stale output from an earlier attempt".utf8).write(to: destination)

        let converter = AudioFileConverter()
        do {
            if destinationExtension == "m4a" {
                _ = try await converter.convertAudioToAAC(from: source, to: destination)
            } else {
                _ = try await converter.convertAudioToWAV(from: source, to: destination)
            }
            Issue.record("a text file converted to \(destinationExtension) instead of throwing")
        } catch AudioError.exportFailed {
            // Expected.
        } catch let error as AudioError {
            Issue.record("expected AudioError.exportFailed, got \(error)")
        } catch {
            // AVFoundation's own refusal of a non-media file: still a throw.
        }

        #expect(!FileManager.default.fileExists(atPath: destination.path))
    }
}
