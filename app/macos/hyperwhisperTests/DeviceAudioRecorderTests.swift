//
//  DeviceAudioRecorderTests.swift
//  hyperwhisperTests
//
//  The chosen-microphone recorder must write the same WAV `AVAudioRecorder` writes
//  (16 kHz, mono, 16-bit integer, little-endian, interleaved) and meter the way it
//  does. These cover everything short of opening a real device: nothing here starts
//  an AVAudioEngine or reads CoreAudio, so they run on a CI runner with no microphone.
//  No force unwraps either: a nil there traps and takes the whole test host down.
//

import AVFoundation
import Foundation
import Testing
@testable import HyperWhisper

struct DeviceAudioRecorderTests {

    /// The deinterleaved Float32 format the input node delivers. Above two channels
    /// `init(standardFormatWithSampleRate:channels:)` returns nil, so those carry a
    /// discrete layout, as a multi-channel interface's input node does.
    private static func inputFormat(sampleRate: Double, channels: AVAudioChannelCount) throws -> AVAudioFormat {
        if channels <= 2 {
            return try #require(AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: channels))
        }
        let layout = try #require(AVAudioChannelLayout(layoutTag: kAudioChannelLayoutTag_DiscreteInOrder | channels))
        return AVAudioFormat(standardFormatWithSampleRate: sampleRate, channelLayout: layout)
    }

    /// A buffer like the input node delivers, every sample `value`.
    private static func inputBuffer(sampleRate: Double, channels: AVAudioChannelCount, frames: AVAudioFrameCount, value: Float) throws -> AVAudioPCMBuffer {
        let format = try inputFormat(sampleRate: sampleRate, channels: channels)
        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames))
        buffer.frameLength = frames
        let data = try #require(buffer.floatChannelData)
        for channel in 0..<Int(channels) {
            for i in 0..<Int(frames) {
                data[channel][i] = value
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

    @Test func silenceReportsTheFloor() throws {
        let buffer = try Self.inputBuffer(sampleRate: 48_000, channels: 1, frames: 480, value: 0)
        #expect(DeviceAudioRecorder.powerDb(of: buffer) == DeviceAudioRecorder.silentPowerDb)
    }

    @Test func powerIsRMSInDecibels() throws {
        let full = try Self.inputBuffer(sampleRate: 48_000, channels: 2, frames: 480, value: 1)
        #expect(abs(DeviceAudioRecorder.powerDb(of: full)) < 0.01)
        let tenth = try Self.inputBuffer(sampleRate: 48_000, channels: 1, frames: 480, value: 0.1)
        #expect(abs(DeviceAudioRecorder.powerDb(of: tenth) + 20) < 0.01)
    }

    @Test func meteringIsOnByDefaultAndIdleIsSilent() {
        // A fake default device: this test must never read real hardware.
        let recorder = DeviceAudioRecorder(deviceID: 0, url: nil, defaultInputDeviceID: { nil })
        #expect(recorder.isMeteringEnabled)
        #expect(recorder.averagePower(forChannel: 0) == DeviceAudioRecorder.silentPowerDb)
        // Stopping a recorder that never started is a no-op, and a meter-only one has no file.
        recorder.stop()
        #expect(recorder.deleteRecording())
    }

    // MARK: - Conversion

    @Test func stereoInputResamplesTo16kMono() throws {
        let input = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        let pipeline = try #require(DeviceCapturePipeline(inputFormat: input, outputFormat: DeviceAudioRecorder.outputFormat))
        let buffer = try Self.inputBuffer(sampleRate: 48_000, channels: 2, frames: 4_800, value: 0.25)
        let source = try #require(pipeline.monoSource(for: buffer))
        #expect(source === buffer)
        let frames = Self.convertedFrameCount(pipeline, source)
        // 100 ms at 48 kHz is 1,600 frames at 16 kHz, give or take the resampler's edges.
        #expect(abs(frames - 1_600) <= 32)
    }

    @Test func multiChannelInputIsMixedFromEveryChannel() throws {
        let input = try Self.inputFormat(sampleRate: 48_000, channels: 6)
        let pipeline = try #require(DeviceCapturePipeline(inputFormat: input, outputFormat: DeviceAudioRecorder.outputFormat))
        let buffer = try Self.inputBuffer(sampleRate: 48_000, channels: 6, frames: 480, value: 0)
        let channels = try #require(buffer.floatChannelData)
        // Voice only on track 5: AVAudioConverter alone would keep channels 1-2 and lose it.
        for i in 0..<480 {
            channels[4][i] = 0.6
        }
        let mono = try #require(pipeline.monoSource(for: buffer))
        #expect(mono.format.channelCount == 1)
        #expect(mono.frameLength == 480)
        let expected = 0.6 / Float(6).squareRoot()
        let monoData = try #require(mono.floatChannelData)
        #expect(abs(monoData[0][100] - expected) < 0.0001)
    }

    @Test func noFormatMeansNoPipeline() {
        let empty = AVAudioFormat(standardFormatWithSampleRate: 0, channels: 1)
        if let empty {
            #expect(DeviceCapturePipeline(inputFormat: empty, outputFormat: DeviceAudioRecorder.outputFormat) == nil)
        }
    }

    // MARK: - Restarts

    @Test func onlyConsecutiveRestartFailuresEndTheCapture() {
        var budget = CaptureRestartBudget(maxConsecutiveFailures: 3)
        // Many benign configuration changes, each followed by a good restart, never give up.
        for _ in 0..<20 {
            #expect(budget.recordFailure() == false)
            budget.recordSuccess()
        }
        #expect(budget.consecutiveFailures == 0)
        #expect(budget.recordFailure() == false)
        #expect(budget.recordFailure() == false)
        #expect(budget.recordFailure() == true)
    }

    // MARK: - File Shape

    @Test func outputFileIs16kMonoInt16WAV() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("device-recorder-\(UUID().uuidString).wav")
        defer { try? FileManager.default.removeItem(at: url) }

        let input = try Self.inputFormat(sampleRate: 44_100, channels: 1)
        do {
            let file = try DeviceAudioRecorder.makeOutputFile(url: url)
            #expect(file.processingFormat.commonFormat == .pcmFormatInt16)
            #expect(file.processingFormat.sampleRate == 16_000)
            #expect(file.processingFormat.channelCount == 1)
            let pipeline = try #require(DeviceCapturePipeline(inputFormat: input, outputFormat: file.processingFormat))
            let buffer = try Self.inputBuffer(sampleRate: 44_100, channels: 1, frames: 4_410, value: 0.5)
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
