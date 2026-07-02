//
//  AudioSessionManager.swift
//  hyperwhisper
//
//  AUDIO SESSION MANAGER
//  Manages the audio environment during recording by muting system audio.
//
//  CAPABILITIES:
//  1. MUTE MODE: Mutes system output volume during recording, restores after
//
//  Technical Implementation:
//  - Volume control: Uses CoreAudio directly on the default output device
//    (kAudioDevicePropertyVolumeScalar). This avoids the System Events
//    AppleScript path, which requires Automation (TCC) permission and
//    silently fails with errAEEventNotPermitted (-1743) if the user once
//    clicked "Don't Allow" — leaving "Mute audio during recording" toggled
//    ON but doing nothing forever.
//  - Operates synchronously for predictable state management
//  - Fails gracefully without blocking recording operations
//

import Foundation
import CoreAudio
import AppKit

/// A snapshot of the output device's volume, captured before muting.
///
/// Devices that don't expose a settable master/main volume element are muted
/// and restored per output channel. Capturing each channel's own value (rather
/// than fanning a single scalar back out on restore) preserves the user's
/// original channel balance — e.g. a 0.8/0.2 stereo split is restored as
/// 0.8/0.2, not flattened to 0.8/0.8.
enum OutputVolume {
    /// The device exposes a settable master/main volume element.
    case master(Float)

    /// The device exposes only per-channel volume; each settable output
    /// channel element paired with its captured scalar.
    case channels([(element: AudioObjectPropertyElement, volume: Float)])

    /// A representative 0.0-1.0 scalar, used only for human-readable logging.
    var representativeScalar: Float {
        switch self {
        case .master(let volume):
            return volume
        case .channels(let entries):
            guard !entries.isEmpty else { return 0 }
            return entries.map(\.volume).reduce(0, +) / Float(entries.count)
        }
    }
}

/// AUDIO ENVIRONMENT STATE
/// Represents the saved state of the audio environment before recording.
/// Used to restore everything back to normal after recording completes.
///
/// The timestamp allows tracking how long recording lasted for logging purposes.
struct AudioEnvironmentState {
    /// The system output volume before muting, preserving per-channel values
    /// for devices without a settable master volume element.
    let originalVolume: OutputVolume?

    /// The output device that was muted. Restore writes back to THIS device —
    /// if the default output changed mid-recording (e.g. AirPods connected),
    /// writing the saved volume to the new default would clobber a device we
    /// never muted.
    let outputDeviceID: AudioDeviceID?

    /// Timestamp when the environment was captured
    /// Used to log the duration when restoring
    let timestamp: Date
}

/// AUDIO SESSION MANAGER
/// Manages system audio environment during recording sessions.
///
/// Mutes system audio output during recording and restores after.
///
/// ARCHITECTURE:
/// - Singleton pattern for consistent state management across the app
/// - @MainActor ensures all operations are thread-safe for UI updates
@MainActor
class AudioSessionManager {

    // MARK: - Properties

    /// Shared singleton instance
    static let shared = AudioSessionManager()

    private init() {}

    // MARK: - Public API

    /// Prepares the audio environment for recording by muting system audio
    ///
    /// This method:
    /// 1. Saves the current system volume
    /// 2. Mutes the system output device (sets volume to 0)
    /// 3. Returns a state object for later restoration
    ///
    /// - Returns: AudioEnvironmentState with saved settings, or nil if operation failed
    func prepareAudioEnvironment() -> AudioEnvironmentState? {
        AppLogger.audio.info("🔇 Muting system audio for recording")

        // Capture the current volume (per-channel when there's no master element)
        // AND the device it belongs to, so restore targets the same device even
        // if the default output changes mid-recording.
        guard let captured = captureOutputVolume() else {
            AppLogger.audio.error("  ✗ Failed to read system volume")
            return nil
        }
        let originalVolume = captured.volume

        guard muteOutputVolume(originalVolume) else {
            AppLogger.audio.error("  ✗ Failed to mute system volume")
            return nil
        }

        let percent = originalVolume.representativeScalar * 100
        AppLogger.audio.info("  ✓ System volume saved (\(String(format: "%.0f", percent))%) and muted")

        let state = AudioEnvironmentState(
            originalVolume: originalVolume,
            outputDeviceID: captured.deviceID,
            timestamp: Date()
        )

        AppLogger.audio.info("  ✓ Audio muted for recording")
        return state
    }

    /// Restores the audio environment to its pre-recording state
    ///
    /// This method restores the system volume to its saved value
    ///
    /// - Parameter state: The saved environment state from prepareAudioEnvironment()
    func restoreAudioEnvironment(_ state: AudioEnvironmentState) {
        AppLogger.audio.info("🔊 Restoring system audio")

        // Restore system volume (each channel to its own captured value) on the
        // SAME device that was muted. If that device disconnected mid-recording,
        // skip the write entirely rather than clobber whatever the new default
        // device's volume is (devices like AirPods restore their own volume
        // state on reconnect).
        if let volume = state.originalVolume {
            if let deviceID = state.outputDeviceID, isDeviceAlive(deviceID) {
                if restoreOutputVolume(volume, deviceID: deviceID) {
                    AppLogger.audio.info("  ✓ System volume restored to \(String(format: "%.0f", volume.representativeScalar * 100))%")
                } else {
                    AppLogger.audio.error("  ✗ Failed to restore system volume")
                }
            } else {
                AppLogger.audio.warning("  ⚠️ Muted output device no longer present — skipping volume restore")
            }
        }

        let elapsed = Date().timeIntervalSince(state.timestamp)
        AppLogger.audio.info("  ✓ Audio restored after \(String(format: "%.1f", elapsed))s")
    }

    // MARK: - System Volume Control (CoreAudio)

    /// Mutes the captured output volume (sets every captured element to 0).
    /// Resolves the current default device — mute runs immediately after
    /// capture, so the default is the device the snapshot came from.
    /// - Parameter snapshot: The volume captured by `captureOutputVolume()`
    /// - Returns: true if every targeted element was muted, false otherwise
    private func muteOutputVolume(_ snapshot: OutputVolume) -> Bool {
        return applyOutputVolume(snapshot, muted: true, deviceID: nil)
    }

    /// Restores the captured output volume, writing each channel back to its
    /// own saved value so the original channel balance is preserved.
    /// - Parameters:
    ///   - snapshot: The volume captured by `captureOutputVolume()`
    ///   - deviceID: The device the snapshot was captured from (restore must
    ///     not write to a different device that became default mid-recording)
    /// - Returns: true if every targeted element was restored, false otherwise
    private func restoreOutputVolume(_ snapshot: OutputVolume, deviceID: AudioDeviceID) -> Bool {
        return applyOutputVolume(snapshot, muted: false, deviceID: deviceID)
    }

    /// Whether a device is still present and alive on the system.
    /// Used to skip volume restore after the muted device disconnects.
    private func isDeviceAlive(_ deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceIsAlive,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        var isAlive: UInt32 = 0
        var dataSize = UInt32(MemoryLayout<UInt32>.size)
        let status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &isAlive)
        return status == noErr && isAlive != 0
    }

    /// Resolves the current system default output device.
    ///
    /// Used as the target for volume reads/writes so muting follows whatever
    /// device the user is actually listening through (built-in, AirPods, etc.).
    /// - Returns: The default output `AudioDeviceID`, or nil if it can't be read
    private func getSystemDefaultOutputDeviceID() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        var deviceID = AudioDeviceID(0)
        var dataSize = UInt32(MemoryLayout<AudioDeviceID>.size)

        let status = AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            &dataSize,
            &deviceID
        )

        return status == noErr ? deviceID : nil
    }

    /// Counts the output channels exposed by a device.
    ///
    /// Used when the device has no master/main volume element so we can iterate
    /// every per-channel volume element instead of touching only channel 1.
    /// - Returns: The number of output channels, or 0 if it can't be read
    private func getOutputChannelCount(_ deviceID: AudioDeviceID) -> UInt32 {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioObjectPropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain
        )

        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &dataSize) == noErr,
              dataSize > 0 else {
            return 0
        }

        let bufferListPointer = UnsafeMutableRawPointer.allocate(
            byteCount: Int(dataSize),
            alignment: MemoryLayout<AudioBufferList>.alignment
        )
        defer { bufferListPointer.deallocate() }

        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, bufferListPointer) == noErr else {
            return 0
        }

        let bufferList = bufferListPointer.bindMemory(to: AudioBufferList.self, capacity: 1)
        let mNumberBuffers = Int(bufferList.pointee.mNumberBuffers)
        var channelCount: UInt32 = 0

        if mNumberBuffers > 0 {
            let audioBuffers = UnsafeBufferPointer(start: &bufferList.pointee.mBuffers, count: mNumberBuffers)
            for i in 0..<mNumberBuffers {
                channelCount += audioBuffers[i].mNumberChannels
            }
        }

        return channelCount
    }

    /// Captures the current system output volume via CoreAudio.
    ///
    /// Prefers the settable master/main element. When the device exposes no
    /// settable master volume (some stereo/multichannel devices), it captures
    /// each settable per-channel element individually so restore can reproduce
    /// the exact original balance rather than flattening every channel to one
    /// value. Mirrors `CoreAudioDeviceHelper`'s per-channel volume handling.
    /// - Returns: The captured `OutputVolume` and the device it was read from,
    ///   or nil if no volume is settable
    private func captureOutputVolume() -> (deviceID: AudioDeviceID, volume: OutputVolume)? {
        guard let deviceID = getSystemDefaultOutputDeviceID() else {
            AppLogger.audio.error("Failed to get default output device")
            return nil
        }

        // Prefer the master element when it is actually settable — otherwise we
        // could capture a master value we'd be unable to mute or restore.
        if isOutputVolumeSettable(deviceID, element: kAudioObjectPropertyElementMain),
           let master = readOutputVolume(deviceID, element: kAudioObjectPropertyElementMain) {
            return (deviceID, .master(master))
        }

        // No settable master volume: capture every settable output channel.
        let channels = settableOutputChannels(deviceID)
        guard !channels.isEmpty else {
            AppLogger.audio.error("Failed to read output volume: no settable master element or channels")
            return nil
        }

        var entries: [(element: AudioObjectPropertyElement, volume: Float)] = []
        for channel in channels {
            if let volume = readOutputVolume(deviceID, element: channel) {
                entries.append((channel, volume))
            }
        }

        guard !entries.isEmpty else {
            AppLogger.audio.error("Failed to read output volume across channels")
            return nil
        }

        return (deviceID, .channels(entries))
    }

    /// Writes a captured `OutputVolume` back to an output device, either muted
    /// (every element to 0) or restored (each element to its own captured
    /// value). Requires every targeted element to succeed — a partial write
    /// would leave some channels audible when muting.
    /// - Parameters:
    ///   - snapshot: The volume captured by `captureOutputVolume()`
    ///   - muted: When true, writes 0 to every element instead of its value
    ///   - deviceID: Target device; nil resolves the current default output
    ///     (mute path). Restore passes the captured device explicitly so a
    ///     mid-recording default switch can't clobber the wrong device. The
    ///     partial-mute rollback below uses the same resolved ID, staying
    ///     consistent.
    /// - Returns: true if every targeted element was written, false otherwise
    private func applyOutputVolume(_ snapshot: OutputVolume, muted: Bool, deviceID explicitDeviceID: AudioDeviceID?) -> Bool {
        guard let deviceID = explicitDeviceID ?? getSystemDefaultOutputDeviceID() else {
            AppLogger.audio.error("Failed to get default output device")
            return false
        }

        switch snapshot {
        case .master(let original):
            let target: Float = muted ? 0 : original
            guard writeOutputVolume(deviceID, element: kAudioObjectPropertyElementMain, volume: target) else {
                AppLogger.audio.error("Failed to set master output volume")
                return false
            }
            return true

        case .channels(let entries):
            var written: [(element: AudioObjectPropertyElement, volume: Float)] = []
            for entry in entries {
                let target: Float = muted ? 0 : entry.volume
                if writeOutputVolume(deviceID, element: entry.element, volume: target) {
                    written.append(entry)
                } else {
                    AppLogger.audio.error("Failed to set output volume on channel \(entry.element)")

                    // On a partial MUTE failure, roll back the channels already
                    // set to 0 to their captured values before bailing. Otherwise
                    // prepareAudioEnvironment() returns nil, RecordingLifecycle
                    // never stores the state, restore is skipped, and those
                    // channels stay muted after recording. Rolling back leaves the
                    // device in its pre-mute state so recording proceeds unmuted —
                    // matching the "fail gracefully" contract.
                    if muted {
                        for done in written {
                            _ = writeOutputVolume(deviceID, element: done.element, volume: done.volume)
                        }
                    }

                    AppLogger.audio.error("Failed to set output volume across all channels")
                    return false
                }
            }
            return true
        }
    }

    /// Lists the output channel elements that expose a settable volume.
    /// - Returns: Settable channel elements (1-based), or [] if none
    private func settableOutputChannels(_ deviceID: AudioDeviceID) -> [AudioObjectPropertyElement] {
        let channelCount = getOutputChannelCount(deviceID)
        guard channelCount > 0 else { return [] }

        var result: [AudioObjectPropertyElement] = []
        for channel in 1...channelCount where isOutputVolumeSettable(deviceID, element: channel) {
            result.append(channel)
        }
        return result
    }

    /// Whether `kAudioDevicePropertyVolumeScalar` is settable on an element.
    private func isOutputVolumeSettable(_ deviceID: AudioDeviceID, element: AudioObjectPropertyElement) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeScalar,
            mScope: kAudioObjectPropertyScopeOutput,
            mElement: element
        )

        var isSettable = DarwinBoolean(false)
        return AudioObjectIsPropertySettable(deviceID, &address, &isSettable) == noErr && isSettable.boolValue
    }

    /// Reads `kAudioDevicePropertyVolumeScalar` for a single output element.
    /// - Returns: Volume (0.0-1.0 scalar), or nil if the read failed
    private func readOutputVolume(_ deviceID: AudioDeviceID, element: AudioObjectPropertyElement) -> Float? {
        var volume = Float32(0)
        var dataSize = UInt32(MemoryLayout<Float32>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeScalar,
            mScope: kAudioObjectPropertyScopeOutput,
            mElement: element
        )

        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &volume) == noErr else {
            return nil
        }

        return max(0, min(volume, 1)) // Clamp to 0-1 range
    }

    /// Writes `kAudioDevicePropertyVolumeScalar` for a single output element.
    /// - Returns: true if the write succeeded, false otherwise
    private func writeOutputVolume(_ deviceID: AudioDeviceID, element: AudioObjectPropertyElement, volume: Float) -> Bool {
        var newVolume = Float32(max(0, min(volume, 1))) // Clamp to 0-1 range
        let dataSize = UInt32(MemoryLayout<Float32>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeScalar,
            mScope: kAudioObjectPropertyScopeOutput,
            mElement: element
        )

        return AudioObjectSetPropertyData(deviceID, &address, 0, nil, dataSize, &newVolume) == noErr
    }
}
