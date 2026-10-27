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
        let recorder = DeviceAudioRecorder(deviceID: 0, url: nil, fallbackInputDeviceIDs: { [] })
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

    @Test func aChangeThatLeavesTheEngineRunningOnTheSameFormatIsNotRestarted() throws {
        // Every start on a non-default device posts one of these with the engine still
        // running; restarting on it posted another, looping the device open and shut.
        let built = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let current = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        #expect(built !== current)
        #expect(DeviceAudioRecorder.configurationChangeNeedsRestart(
            engineIsRunning: true, captureFormat: built, currentFormat: current
        ) == false)
    }

    @Test func aStoppedEngineOrANewFormatIsRestarted() throws {
        let built = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        // The engine stopped itself: a real hardware change, or the device went away.
        #expect(DeviceAudioRecorder.configurationChangeNeedsRestart(
            engineIsRunning: false, captureFormat: built, currentFormat: built
        ))
        // Still running, but the device now delivers another rate or channel count.
        let newRate = try Self.inputFormat(sampleRate: 16_000, channels: 1)
        #expect(DeviceAudioRecorder.configurationChangeNeedsRestart(
            engineIsRunning: true, captureFormat: built, currentFormat: newRate
        ))
        let newChannels = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        #expect(DeviceAudioRecorder.configurationChangeNeedsRestart(
            engineIsRunning: true, captureFormat: built, currentFormat: newChannels
        ))
        // The device reports no format at all (mid-reconfiguration, or gone).
        #expect(DeviceAudioRecorder.configurationChangeNeedsRestart(
            engineIsRunning: true, captureFormat: built, currentFormat: nil
        ))
    }

    // MARK: - Device Removal (issue #2284)

    @Test func aRemovedChosenDeviceFallsBackToTheDefault() {
        let gone: Set<AudioDeviceID> = [41]
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [73], isGone: { gone.contains($0) }) == [73])
    }

    @Test func aLiveChosenDeviceIsTriedBeforeTheDefault() {
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [73], isGone: { _ in false }) == [41, 73])
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [41], isGone: { _ in false }) == [41])
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [AudioDeviceID(kAudioObjectUnknown)], isGone: { _ in false }) == [41])
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [], isGone: { _ in false }) == [41])
    }

    @Test func everyFallbackIsTriedOnceInOrder() {
        // The non-virtual stand-ins for a skipped virtual default (#2408).
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [41, 52, 73, 52], isGone: { _ in false }) == [41, 52, 73])
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [52, 73], isGone: { $0 == 52 }) == [41, 73])
    }

    @Test func noLiveDeviceMeansNoCandidate() {
        // An empty list makes the restart fail, so the retry budget ends in capture lost.
        #expect(DeviceAudioRecorder.restartCandidates(chosen: 41, fallbacks: [73], isGone: { _ in true }).isEmpty)
    }

    @Test func onlyPositiveEvidenceMeansADeviceIsGone() {
        #expect(DeviceAudioRecorder.isGone(aliveFlag: 0, isListed: nil))
        #expect(!DeviceAudioRecorder.isGone(aliveFlag: 1, isListed: nil))
        #expect(DeviceAudioRecorder.isGone(aliveFlag: nil, isListed: false))
        // A failed alive read on a device that is still listed, or with no list at all,
        // must not move the recording off the chosen microphone.
        #expect(!DeviceAudioRecorder.isGone(aliveFlag: nil, isListed: true))
        #expect(!DeviceAudioRecorder.isGone(aliveFlag: nil, isListed: nil))
    }

    @Test func aRemovedActiveDeviceRestartsCapture() {
        #expect(DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: true, hasEngine: true, activeDeviceID: 41, isGone: { $0 == 41 }))
    }

    @Test func aLiveActiveDeviceKeepsCapture() {
        // Another device coming or going fires the same listener: no restart for it.
        #expect(!DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: true, hasEngine: true, activeDeviceID: 41, isGone: { _ in false }))
        #expect(!DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: true, hasEngine: true, activeDeviceID: 41, isGone: { $0 == 99 }))
    }

    @Test func noRunningCaptureMeansNoRestart() {
        let allGone: (AudioDeviceID) -> Bool = { _ in true }
        #expect(!DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: false, hasEngine: true, activeDeviceID: 41, isGone: allGone))
        // Torn down, or a retry pending: the restart path owns capture already.
        #expect(!DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: true, hasEngine: false, activeDeviceID: 41, isGone: allGone))
        // Teardown clears the active device, so a stale listener block does nothing.
        #expect(!DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: true, hasEngine: true, activeDeviceID: nil, isGone: allGone))
    }

    @Test func afterAFallbackTheDecisionFollowsTheDefaultDevice() {
        // Capture fell back from the chosen device 41 to the default 73.
        let chosenGone: (AudioDeviceID) -> Bool = { $0 == 41 }
        #expect(!DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: true, hasEngine: true, activeDeviceID: 73, isGone: chosenGone))
        let defaultGone: (AudioDeviceID) -> Bool = { $0 == 41 || $0 == 73 }
        #expect(DeviceAudioRecorder.deviceRemovalNeedsRestart(isRunning: true, hasEngine: true, activeDeviceID: 73, isGone: defaultGone))
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

    // MARK: - Tap format (#2577)

    @Test func tapFormatKeepsAnOutputFormatThatMatchesTheDevice() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let hardware = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let tap = try #require(DeviceAudioRecorder.tapFormat(output: output, hardware: hardware))
        #expect(tap.isEqual(output))
    }

    /// The Sentry HYPERWHISPER-11K crash: the node's output side said 1 channel at
    /// 48 kHz while the device side did not, and `installTap` refused it.
    @Test func tapFormatFollowsTheDeviceWhenTheChannelCountsDiffer() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let hardware = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        let tap = try #require(DeviceAudioRecorder.tapFormat(output: output, hardware: hardware))
        #expect(tap.sampleRate == hardware.sampleRate)
        #expect(tap.channelCount == hardware.channelCount)
    }

    @Test func tapFormatFollowsAMultiChannelDeviceLayout() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        let hardware = try Self.inputFormat(sampleRate: 48_000, channels: 4)
        let tap = try #require(DeviceAudioRecorder.tapFormat(output: output, hardware: hardware))
        #expect(tap.channelCount == 4)
    }

    /// A layout-less format above two channels, if a device reports one: the tap still
    /// gets a format with the device's channel count rather than nil.
    @Test func tapFormatBuildsALayoutForAMultiChannelDeviceWithNone() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        var description = AudioStreamBasicDescription(
            mSampleRate: 48_000,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagsNativeFloatPacked | kAudioFormatFlagIsNonInterleaved,
            mBytesPerPacket: 4,
            mFramesPerPacket: 1,
            mBytesPerFrame: 4,
            mChannelsPerFrame: 8,
            mBitsPerChannel: 32,
            mReserved: 0
        )
        // AVFoundation may refuse to build one; then nothing can hand tapFormat one either.
        guard let hardware = AVAudioFormat(streamDescription: &description), hardware.channelLayout == nil else { return }
        let tap = try #require(DeviceAudioRecorder.tapFormat(output: output, hardware: hardware))
        #expect(tap.channelCount == 8)
        #expect(tap.sampleRate == 48_000)
    }

    @Test func tapFormatFollowsTheDeviceRate() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        let hardware = try Self.inputFormat(sampleRate: 44_100, channels: 2)
        let tap = try #require(DeviceAudioRecorder.tapFormat(output: output, hardware: hardware))
        #expect(tap.sampleRate == 44_100)
        #expect(tap.channelCount == 2)
    }

    /// A channel-only change gets the node's own output format as a second try, so a
    /// device that legitimately outputs fewer channels still records as it did before.
    @Test func aChannelOnlyChangeKeepsTheOutputFormatAsASecondTry() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let hardware = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        let candidates = DeviceAudioRecorder.tapFormatCandidates(output: output, hardware: hardware)
        #expect(candidates.count == 2)
        #expect(candidates.first?.channelCount == 2)
        #expect(candidates.last?.isEqual(output) == true)
    }

    @Test func aRateChangeHasNoSecondTry() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let hardware = try Self.inputFormat(sampleRate: 44_100, channels: 2)
        let candidates = DeviceAudioRecorder.tapFormatCandidates(output: output, hardware: hardware)
        #expect(candidates.count == 1)
        #expect(candidates.first?.sampleRate == 44_100)
    }

    @Test func aMatchingFormatIsTheOnlyCandidate() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        let candidates = DeviceAudioRecorder.tapFormatCandidates(output: output, hardware: output)
        #expect(candidates.count == 1)
        #expect(candidates.first?.isEqual(output) == true)
    }

    /// A capture that fell back to the output format is still "the same format" on a
    /// benign configuration change, so it is not restarted in a loop.
    @Test func aCaptureOnTheFallbackFormatNeedsNoRestart() throws {
        let output = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let hardware = try Self.inputFormat(sampleRate: 48_000, channels: 2)
        let candidates = DeviceAudioRecorder.tapFormatCandidates(output: output, hardware: hardware)
        let current = DeviceAudioRecorder.currentTapFormat(matching: output, in: candidates)
        #expect(DeviceAudioRecorder.configurationChangeNeedsRestart(engineIsRunning: true, captureFormat: output, currentFormat: current) == false)
        // A format the device no longer offers still restarts.
        let stale = try Self.inputFormat(sampleRate: 44_100, channels: 1)
        let moved = DeviceAudioRecorder.currentTapFormat(matching: stale, in: candidates)
        #expect(DeviceAudioRecorder.configurationChangeNeedsRestart(engineIsRunning: true, captureFormat: stale, currentFormat: moved))
    }

    @Test func tapFormatExtrasAreNumbersAndBoolsOnly() throws {
        let format = try Self.inputFormat(sampleRate: 48_000, channels: 1)
        let extras = DeviceAudioRecorder.tapFormatExtras(requested: format, output: format, hardware: format)
        #expect(extras.count == 12)
        #expect(extras["tapRequestedChannelCount"] as? Int == 1)
        #expect(extras["tapNodeHardwareSampleRateHz"] as? Double == 48_000)
        #expect(extras.values.allSatisfy { !($0 is String) })
    }

    // MARK: - Objective-C exception guard (#2577)

    @Test func exceptionGuardTurnsAnObjCExceptionIntoAnError() {
        var thrown: NSError?
        do {
            try ObjCExceptionGuard.run {
                NSException(name: .invalidArgumentException, reason: "Failed to create tap due to format mismatch", userInfo: nil).raise()
            }
        } catch {
            thrown = error as NSError
        }
        #expect(thrown?.domain == "HyperWhisper.ObjCException")
        #expect(thrown?.localizedFailureReason == "Failed to create tap due to format mismatch")
        #expect(thrown?.userInfo["ObjCExceptionName"] as? String == NSExceptionName.invalidArgumentException.rawValue)
    }

    @Test func exceptionGuardRunsABodyThatDoesNotRaise() throws {
        var ran = false
        try ObjCExceptionGuard.run { ran = true }
        #expect(ran)
    }
}
