//
//  DeviceAudioRecorderTests.swift
//  hyperwhisperTests
//
//  The chosen-microphone recorder must write the same WAV `AVAudioRecorder` writes
//  (16 kHz, mono, 16-bit integer, little-endian, interleaved) and meter the way it
//  does. These cover everything short of opening a real device.
//

import AVFoundation
import Foundation
import Testing
@testable import HyperWhisper

struct DeviceAudioRecorderTests {

    /// A deinterleaved Float32 buffer like the input node delivers, every sample `value`.
    private static func inputBuffer(sampleRate: Double, channels: AVAudioChannelCount, frames: AVAudioFrameCount, value: Float) -> AVAudioPCMBuffer {
        let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: channels)!
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames)!
        buffer.frameLength = frames
        for channel in 0..<Int(channels) {
            for i in 0..<Int(frames) {
                buffer.floatChannelData![channel][i] = value
            }
        }
        return buffer
    }

    private static func convertedFrameCount(_ pipeline: DeviceCapturePipeline, _ buffer: AVAudioPCMBuffer) -> Int {
        var frames = 0
        pipeline.convert(buffer) { frames += Int($0.frameLength) }
        pipeline.flush { frames += Int($0.frameLength) }
        return frames
    }

    // MARK: - Metering

    @Test func silenceReportsTheFloor() {
        let buffer = Self.inputBuffer(sampleRate: 48_000, channels: 1, frames: 480, value: 0)
        #expect(DeviceAudioRecorder.powerDb(of: buffer) == DeviceAudioRecorder.silentPowerDb)
    }

    @Test func powerIsRMSInDecibels() {
        let full = Self.inputBuffer(sampleRate: 48_000, channels: 2, frames: 480, value: 1)
        #expect(abs(DeviceAudioRecorder.powerDb(of: full)) < 0.01)
        let tenth = Self.inputBuffer(sampleRate: 48_000, channels: 1, frames: 480, value: 0.1)
        #expect(abs(DeviceAudioRecorder.powerDb(of: tenth) + 20) < 0.01)
    }

    @Test func meteringIsOnByDefaultAndIdleIsSilent() {
        let recorder = DeviceAudioRecorder(deviceID: 0, url: nil)
        #expect(recorder.isMeteringEnabled)
        #expect(recorder.averagePower(forChannel: 0) == DeviceAudioRecorder.silentPowerDb)
        // Stopping a recorder that never started is a no-op, and a meter-only one has no file.
        recorder.stop()
        #expect(recorder.deleteRecording())
    }

    // MARK: - Conversion

    @Test func stereoInputResamplesTo16kMono() throws {
        let input = AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 2)!
        let pipeline = try #require(DeviceCapturePipeline(inputFormat: input, outputFormat: DeviceAudioRecorder.outputFormat))
        let buffer = Self.inputBuffer(sampleRate: 48_000, channels: 2, frames: 4_800, value: 0.25)
        let source = try #require(pipeline.monoSource(for: buffer))
        #expect(source === buffer)
        let frames = Self.convertedFrameCount(pipeline, source)
        // 100 ms at 48 kHz is 1,600 frames at 16 kHz, give or take the resampler's edges.
        #expect(abs(frames - 1_600) <= 32)
    }

    @Test func multiChannelInputIsMixedFromEveryChannel() throws {
        let input = AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 6)!
        let pipeline = try #require(DeviceCapturePipeline(inputFormat: input, outputFormat: DeviceAudioRecorder.outputFormat))
        let buffer = Self.inputBuffer(sampleRate: 48_000, channels: 6, frames: 480, value: 0)
        // Voice only on track 5: AVAudioConverter alone would keep channels 1-2 and lose it.
        for i in 0..<480 {
            buffer.floatChannelData![4][i] = 0.6
        }
        let mono = try #require(pipeline.monoSource(for: buffer))
        #expect(mono.format.channelCount == 1)
        #expect(mono.frameLength == 480)
        let expected = 0.6 / Float(6).squareRoot()
        #expect(abs(mono.floatChannelData![0][100] - expected) < 0.0001)
    }

    @Test func noFormatMeansNoPipeline() {
        let empty = AVAudioFormat(standardFormatWithSampleRate: 0, channels: 1)
        if let empty {
            #expect(DeviceCapturePipeline(inputFormat: empty, outputFormat: DeviceAudioRecorder.outputFormat) == nil)
        }
    }

    // MARK: - File Shape

    @Test func outputFileIs16kMonoInt16WAV() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("device-recorder-\(UUID().uuidString).wav")
        defer { try? FileManager.default.removeItem(at: url) }

        let input = AVAudioFormat(standardFormatWithSampleRate: 44_100, channels: 1)!
        do {
            let file = try DeviceAudioRecorder.makeOutputFile(url: url)
            #expect(file.processingFormat.commonFormat == .pcmFormatInt16)
            #expect(file.processingFormat.sampleRate == 16_000)
            #expect(file.processingFormat.channelCount == 1)
            let pipeline = try #require(DeviceCapturePipeline(inputFormat: input, outputFormat: file.processingFormat))
            let buffer = Self.inputBuffer(sampleRate: 44_100, channels: 1, frames: 4_410, value: 0.5)
            pipeline.convert(buffer) { try? file.write(from: $0) }
            pipeline.flush { try? file.write(from: $0) }
        }

        let readBack = try AVAudioFile(forReading: url)
        let format = readBack.fileFormat
        #expect(format.sampleRate == 16_000)
        #expect(format.channelCount == 1)
        #expect(abs(Int(readBack.length) - 1_600) <= 32)

        let description = format.streamDescription.pointee
        #expect(description.mFormatID == kAudioFormatLinearPCM)
        #expect(description.mBitsPerChannel == 16)
        #expect(description.mFormatFlags & kAudioFormatFlagIsFloat == 0)
        #expect(description.mFormatFlags & kAudioFormatFlagIsBigEndian == 0)
        #expect(description.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0)

        // RIFF/WAVE container, like AVAudioRecorder's.
        let header = try Data(contentsOf: url).prefix(12)
        #expect(String(decoding: header.prefix(4), as: UTF8.self) == "RIFF")
        #expect(String(decoding: header.suffix(4), as: UTF8.self) == "WAVE")
    }
}
