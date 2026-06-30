//
//  CoreAudioDeviceHelper.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import CoreAudio

/// Low-level CoreAudio API wrapper for device management
///
/// **Purpose:**
/// Provides a Swift-friendly interface to macOS CoreAudio APIs for:
/// - Enumerating audio input devices
/// - Getting/setting system default input device
/// - Reading device properties (name, UID, volume)
/// - Finding devices by UID
///
/// **Why This Exists:**
/// CoreAudio uses C-style APIs with unsafe pointers and complex property addressing.
/// This helper encapsulates all that complexity into clean Swift methods.
///
/// **Thread Safety:**
/// All methods are `nonisolated` and safe to call from any thread.
/// This allows device enumeration on background queues without blocking the UI.
///
/// **Important Note:**
/// These methods interact with the system's audio hardware directly.
/// They require proper audio permissions and may fail if hardware is unavailable.
class CoreAudioDeviceHelper {

    // MARK: - Stream Format Info

    /// Lightweight representation of an input stream format.
    struct AudioStreamFormatInfo {
        let sampleRate: Double
        let channels: UInt32
        let bitDepth: UInt32
        let isFloat: Bool
    }

    // MARK: - Transport Type

    /// Get the transport type for a device (USB, Bluetooth, Built-in, etc.).
    nonisolated static func transportTypeString(for deviceID: AudioDeviceID) -> String? {
        var transport: UInt32 = 0
        var dataSize = UInt32(MemoryLayout<UInt32>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyTransportType,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        let status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &transport)
        guard status == noErr else { return nil }

        return transportTypeLabel(transport)
    }

    nonisolated private static func transportTypeLabel(_ transport: UInt32) -> String {
        switch transport {
        case kAudioDeviceTransportTypeBuiltIn:
            return "built_in"
        case kAudioDeviceTransportTypeAggregate:
            return "aggregate"
        case kAudioDeviceTransportTypeVirtual:
            return "virtual"
        case kAudioDeviceTransportTypeUSB:
            return "usb"
        case kAudioDeviceTransportTypeBluetooth:
            return "bluetooth"
        case kAudioDeviceTransportTypeAirPlay:
            return "airplay"
        case kAudioDeviceTransportTypePCI:
            return "pci"
        case kAudioDeviceTransportTypeHDMI:
            return "hdmi"
        case kAudioDeviceTransportTypeDisplayPort:
            return "display_port"
        default:
            return String(format: "unknown(0x%X)", transport)
        }
    }

    // MARK: - Stream Format

    /// Get the current input stream format for a device.
    nonisolated static func copyInputStreamFormat(for deviceID: AudioDeviceID) -> AudioStreamFormatInfo? {
        var format = AudioStreamBasicDescription()
        var dataSize = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamFormat,
            mScope: kAudioObjectPropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain
        )

        var status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &format)
        if status != noErr {
            address.mElement = 1
            dataSize = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
            status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &format)
        }

        guard status == noErr else { return nil }

        return AudioStreamFormatInfo(
            sampleRate: format.mSampleRate,
            channels: format.mChannelsPerFrame,
            bitDepth: format.mBitsPerChannel,
            isFloat: (format.mFormatFlags & kAudioFormatFlagIsFloat) != 0
        )
    }

    // MARK: - Device Enumeration

    /// Enumerate all Core Audio input devices
    ///
    /// **What This Does:**
    /// Queries the CoreAudio system object for all audio devices, then filters
    /// for those that have input streams (microphones).
    ///
    /// **Filtering Logic:**
    /// 1. Check if device has input streams (excludes output-only devices)
    /// 2. Verify input channel count > 0
    /// 3. Exclude system aggregate devices (CADefaultDeviceAggregate, etc.)
    /// 4. Map to AudioDevice struct with name and UID
    ///
    /// **Returns:**
    /// Array of AudioDevice structs representing available microphones
    ///
    /// **Performance:**
    /// This is a relatively expensive operation (5-50ms depending on device count).
    /// Cache results when possible instead of calling repeatedly.
    nonisolated static func fetchCoreAudioInputDevices() -> [AudioDevice] {
        var result: [AudioDevice] = []

        // STEP 1: Get all audio devices from system
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        var dataSize: UInt32 = 0
        var status = AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            &dataSize
        )

        guard status == noErr, dataSize >= UInt32(MemoryLayout<AudioObjectID>.size) else {
            return []
        }

        let count = Int(dataSize) / MemoryLayout<AudioObjectID>.size
        var deviceIDs = [AudioObjectID](repeating: 0, count: count)

        status = AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            &dataSize,
            &deviceIDs
        )

        guard status == noErr else { return [] }

        // STEP 2: Filter for input devices and extract metadata
        for dev in deviceIDs {
            // Check if device has input streams
            var streamCfgAddr = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyStreamConfiguration,
                mScope: kAudioObjectPropertyScopeInput,
                mElement: kAudioObjectPropertyElementWildcard
            )

            var propertySize: UInt32 = 0
            var devCopy = dev
            var cfgStatus = AudioObjectGetPropertyDataSize(
                devCopy,
                &streamCfgAddr,
                0,
                nil,
                &propertySize
            )

            if cfgStatus != noErr || propertySize == 0 { continue }

            // Allocate buffer for stream configuration
            let bufferListPointer = UnsafeMutableRawPointer.allocate(
                byteCount: Int(propertySize),
                alignment: MemoryLayout<AudioBufferList>.alignment
            )
            defer { bufferListPointer.deallocate() }

            cfgStatus = AudioObjectGetPropertyData(
                devCopy,
                &streamCfgAddr,
                0,
                nil,
                &propertySize,
                bufferListPointer
            )

            if cfgStatus != noErr { continue }

            // Check input channel count
            let bufferList = bufferListPointer.bindMemory(to: AudioBufferList.self, capacity: 1)
            let mNumberBuffers = Int(bufferList.pointee.mNumberBuffers)
            var inputChannelCount = 0

            if mNumberBuffers > 0 {
                let audioBuffers = UnsafeBufferPointer(start: &bufferList.pointee.mBuffers, count: mNumberBuffers)
                for i in 0..<mNumberBuffers {
                    inputChannelCount += Int(audioBuffers[i].mNumberChannels)
                }
            }

            // Skip devices with no input channels
            if inputChannelCount == 0 { continue }

            // Read device name
            guard let deviceName = copyDeviceName(for: dev) else { continue }

            // Read device UID
            guard let uid = copyDeviceUID(for: dev) else { continue }

            // Filter out system aggregate devices
            if deviceName.contains("CADefaultDeviceAggregate") ||
               (deviceName.contains("CA") && deviceName.contains("Aggregate")) ||
               deviceName.contains("System") ||
               (deviceName.contains("Default") && deviceName.contains("Aggregate")) {
                continue
            }

            result.append(AudioDevice(id: uid, name: deviceName, uid: uid))
        }

        return result
    }

    // MARK: - System Default Device

    /// Get current system default input device ID
    ///
    /// **What This Does:**
    /// Queries CoreAudio for the current default input device (the device
    /// that receives input when no specific device is selected).
    ///
    /// **Returns:**
    /// Optional AudioDeviceID, or nil if query fails
    nonisolated static func getSystemDefaultInputDeviceID() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultInputDevice,
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

    /// Get current system default input device UID
    ///
    /// **What This Does:**
    /// Returns the UID of the system's default input device. This is useful for
    /// identifying which device in a list is the current system default.
    ///
    /// **Returns:**
    /// Optional UID string, or nil if query fails
    nonisolated static func getSystemDefaultInputDeviceUID() -> String? {
        guard let deviceID = getSystemDefaultInputDeviceID() else { return nil }
        return copyDeviceUID(for: deviceID)
    }

    /// Set system default input device
    ///
    /// **What This Does:**
    /// Changes the system's default input device. This affects all applications
    /// that use the default input device.
    ///
    /// **Important:**
    /// This is a system-wide change. Always restore the previous device after
    /// recording to avoid affecting other apps.
    ///
    /// **Parameters:**
    /// - `deviceID`: The CoreAudio device ID to set as default
    ///
    /// **Returns:**
    /// true if successful, false otherwise
    nonisolated static func setSystemDefaultInputDevice(to deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultInputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        var id = deviceID
        let dataSize = UInt32(MemoryLayout<AudioDeviceID>.size)

        let status = AudioObjectSetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            dataSize,
            &id
        )

        return status == noErr
    }

    // MARK: - Device Lookup

    /// Find AudioDeviceID by UID string
    ///
    /// **What This Does:**
    /// Searches all audio devices for one matching the given UID.
    ///
    /// **Use Case:**
    /// We store device UIDs in settings/Core Data. When we need to use a device,
    /// we look up its AudioDeviceID by UID.
    ///
    /// **Parameters:**
    /// - `uid`: The device UID string
    ///
    /// **Returns:**
    /// Optional AudioDeviceID, or nil if device not found
    nonisolated static func findAudioDeviceID(byUID uid: String) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        var dataSize: UInt32 = 0
        var status = AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            &dataSize
        )

        guard status == noErr, dataSize >= UInt32(MemoryLayout<AudioObjectID>.size) else { return nil }

        let count = Int(dataSize) / MemoryLayout<AudioObjectID>.size
        var deviceIDs = [AudioObjectID](repeating: 0, count: count)

        status = AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            &dataSize,
            &deviceIDs
        )

        guard status == noErr else { return nil }

        // Search for matching UID
        for dev in deviceIDs {
            if let deviceUID = copyDeviceUID(for: dev), deviceUID == uid {
                return dev
            }
        }

        return nil
    }

    // MARK: - Device Validation

    /// Check if an audio device ID is still valid and available
    ///
    /// **What This Does:**
    /// Verifies that a device ID still exists in the system by attempting to read
    /// its UID property. This is the minimal property query that CoreAudio supports.
    ///
    /// **Use Case:**
    /// Before restoring a previously-saved device ID, we should verify the device
    /// still exists. Devices can disappear if:
    /// - Bluetooth devices disconnect
    /// - USB devices are unplugged
    /// - The system reassigns device IDs
    ///
    /// **Parameters:**
    /// - `deviceID`: The device ID to validate
    ///
    /// **Returns:**
    /// true if the device exists and is accessible, false otherwise
    nonisolated static func isDeviceAvailable(_ deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        // Check if device exists by querying its UID property
        var dataSize: UInt32 = 0
        let status = AudioObjectGetPropertyDataSize(
            deviceID,
            &address,
            0,
            nil,
            &dataSize
        )

        // Device exists if we can query its property size
        return status == noErr && dataSize > 0
    }

    // MARK: - Device Properties

    /// Read input volume scalar (0.0 - 1.0) for a device
    ///
    /// **What This Does:**
    /// Reads the system input volume slider value for a specific device.
    ///
    /// **Fallback Strategy:**
    /// Some devices don't support volume on the main element. If main fails,
    /// we try channel 1 (often supported even when main isn't).
    ///
    /// **Parameters:**
    /// - `deviceID`: The device to query
    ///
    /// **Returns:**
    /// Optional Float (0.0 to 1.0), or nil if volume not available
    nonisolated static func readInputVolumeScalar(for deviceID: AudioDeviceID) -> Float? {
        var volume = Float32(0)
        var dataSize = UInt32(MemoryLayout<Float32>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeScalar,
            mScope: kAudioObjectPropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain
        )

        var status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &volume)

        if status != noErr {
            // Try channel 1 if main element not supported
            address.mElement = 1
            dataSize = UInt32(MemoryLayout<Float32>.size)
            status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &volume)
        }

        guard status == noErr else { return nil }
        return max(0, min(volume, 1)) // Clamp to 0-1 range
    }

    /// Set input volume scalar (0.0 - 1.0) for a device
    ///
    /// **What This Does:**
    /// Sets the system input volume slider value for a specific device.
    /// This is the counterpart to `readInputVolumeScalar`.
    ///
    /// **Fallback Strategy:**
    /// Some devices don't support volume on the main element. If main fails,
    /// we try channel 1 (often supported even when main isn't).
    ///
    /// **Parameters:**
    /// - `deviceID`: The device to modify
    /// - `volume`: The volume level (0.0 to 1.0), will be clamped to this range
    ///
    /// **Returns:**
    /// true if volume was set successfully, false otherwise
    nonisolated static func setInputVolumeScalar(for deviceID: AudioDeviceID, volume: Float) -> Bool {
        var newVolume = Float32(max(0, min(volume, 1))) // Clamp to 0-1 range
        let dataSize = UInt32(MemoryLayout<Float32>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeScalar,
            mScope: kAudioObjectPropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain
        )

        var status = AudioObjectSetPropertyData(deviceID, &address, 0, nil, dataSize, &newVolume)

        if status != noErr {
            // Try channel 1 if main element not supported
            address.mElement = 1
            status = AudioObjectSetPropertyData(deviceID, &address, 0, nil, dataSize, &newVolume)
        }

        return status == noErr
    }

    /// Get device name for display
    ///
    /// **Parameters:**
    /// - `deviceID`: The device to query
    ///
    /// **Returns:**
    /// Optional device name string
    nonisolated static func copyDeviceName(for deviceID: AudioDeviceID) -> String? {
        var name: CFString = "" as CFString
        var dataSize = UInt32(MemoryLayout<CFString>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioObjectPropertyName,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        let status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &name)
        guard status == noErr else { return nil }
        return name as String
    }

    /// Get device UID for persistence
    ///
    /// **Parameters:**
    /// - `deviceID`: The device to query
    ///
    /// **Returns:**
    /// Optional device UID string
    nonisolated static func copyDeviceUID(for deviceID: AudioDeviceID) -> String? {
        var uid: CFString = "" as CFString
        var dataSize = UInt32(MemoryLayout<CFString>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )

        let status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &uid)
        guard status == noErr else { return nil }
        return uid as String
    }
}
