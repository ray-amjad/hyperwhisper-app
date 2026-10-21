//
//  MicrophoneSelection.swift
//  hyperwhisper
//
//  HyperWhisper's own microphone choice, kept apart from the macOS default input.
//
//  Picking a microphone (Settings → Sound, the menu-bar Microphone submenu, or the
//  onboarding microphone step) saves ONE choice for HyperWhisper and never changes the
//  Mac's system-default input, so Zoom and every other app keep theirs. Recording, the
//  level preview, streaming capture and keep-warm then open the chosen device directly.
//
//  The saved choice is kept while its device is unplugged: recording falls back to the
//  macOS default meanwhile and returns to the chosen device as soon as it reappears.
//  Everything in this file is pure or lock-guarded, so it can be unit tested without
//  CoreAudio and read from any thread.
//

import Foundation
import CoreAudio

// MARK: - Saved Choice

/// The microphone a user picked for HyperWhisper.
///
/// `uid` is the CoreAudio device UID (`kAudioDevicePropertyDeviceUID`), which is what
/// `selectedMicrophoneId` has always held (`AudioDevice.id == AudioDevice.uid`), so
/// values saved by earlier builds keep working. An `AudioDeviceID` is not stored: it is
/// reassigned on replug and reboot.
///
/// `name` is saved alongside it so an unplugged choice can still be shown by name.
struct MicrophoneChoice: Equatable, Hashable, Sendable {
    let uid: String
    let name: String
}

// MARK: - Resolution

/// What the saved choice means against the devices that are connected right now.
enum MicrophoneResolution: Equatable, Sendable {
    /// No choice saved: follow the macOS default input, whatever it is at the time.
    case systemDefault
    /// The chosen device is connected and is the one HyperWhisper records from.
    case chosen(AudioDevice)
    /// The chosen device is not connected. HyperWhisper records from the macOS default
    /// meanwhile and keeps the choice, so the device is used again when it returns.
    case chosenNotConnected(MicrophoneChoice)

    /// Resolve a saved choice against the connected input devices.
    ///
    /// - Parameters:
    ///   - savedUID: The saved device UID. `nil` or empty means "Default".
    ///   - savedName: The saved display name, used only while the device is absent.
    ///     Falls back to the UID when empty (a choice saved before names were stored).
    ///   - devices: The connected input devices.
    static func resolve(
        savedUID: String?,
        savedName: String?,
        devices: [AudioDevice]
    ) -> MicrophoneResolution {
        guard let savedUID, !savedUID.isEmpty else { return .systemDefault }
        if let device = devices.first(where: { $0.uid == savedUID }) {
            return .chosen(device)
        }
        let name = (savedName?.isEmpty == false ? savedName : nil) ?? savedUID
        return .chosenNotConnected(MicrophoneChoice(uid: savedUID, name: name))
    }

    /// The UID HyperWhisper must open itself, or `nil` to let macOS route the default.
    ///
    /// A chosen device that IS the current macOS default needs no pinning: the existing
    /// `AVAudioRecorder` path already records from the default, and keeping that path
    /// for it leaves the most-used configuration on the long-proven capture code.
    func pinnedDeviceUID(systemDefaultUID: String?) -> String? {
        guard case .chosen(let device) = self else { return nil }
        if let systemDefaultUID, device.uid == systemDefaultUID { return nil }
        return device.uid
    }

    /// The saved choice whose device is missing, if that is the situation.
    var missingChoice: MicrophoneChoice? {
        if case .chosenNotConnected(let choice) = self { return choice }
        return nil
    }
}

// MARK: - Labels

/// User-facing labels for the microphone picker, shared by Settings and the menu bar.
enum MicrophonePickerLabels {
    /// The name of the current macOS default input, or a generic name when unknown.
    static func defaultDeviceName(devices: [AudioDevice], systemDefaultUID: String?) -> String {
        if let systemDefaultUID,
           let device = devices.first(where: { $0.uid == systemDefaultUID }) {
            return device.name
        }
        return "audio.device.default".localized
    }

    /// "Default · <current default device>"
    static func defaultOption(defaultDeviceName: String) -> String {
        "microphone.picker.default".localized(arguments: defaultDeviceName)
    }

    /// "<name> (not connected)"
    static func notConnected(_ choice: MicrophoneChoice) -> String {
        "microphone.picker.notConnected".localized(arguments: choice.name)
    }

    /// "Using <fallback> until <device> is reconnected."
    static func fallbackNotice(fallbackName: String, choice: MicrophoneChoice) -> String {
        "microphone.picker.fallback".localized(arguments: fallbackName, choice.name)
    }
}

// MARK: - Level Bar

enum MicrophoneLevelMeter {
    /// How many of `segmentCount` segments a normalized level (0...1) lights.
    /// A level of exactly 0, or anything not finite, lights nothing.
    static func litSegments(level: Float, segmentCount: Int) -> Int {
        guard segmentCount > 0, level.isFinite, level > 0 else { return 0 }
        let clamped = min(level, 1)
        return min(segmentCount, Int((clamped * Float(segmentCount)).rounded(.up)))
    }
}

// MARK: - Routing Store

/// The saved choice's UID, readable from any thread.
///
/// `AudioDeviceManager` (main actor) writes it whenever the choice changes. Capture code
/// that is not on the main actor and has no reference to the device manager —
/// `StreamingAudioCapture.start()` — reads it to open the same device the recording uses.
final class MicrophoneRoutingStore: @unchecked Sendable {
    static let shared = MicrophoneRoutingStore()

    private let lock = NSLock()
    private var uid: String?

    var preferredDeviceUID: String? {
        get {
            lock.lock()
            defer { lock.unlock() }
            return uid
        }
        set {
            lock.lock()
            uid = newValue
            lock.unlock()
        }
    }
}

// MARK: - Capture Device Lookup

enum MicrophoneCaptureRouting {
    /// The `AudioDeviceID` a capture should open for `preferredUID`, or `nil` to record
    /// from the macOS default.
    ///
    /// `nil` when there is no choice, when the chosen device is not connected (the
    /// fallback), or when it is the current default (see
    /// `MicrophoneResolution.pinnedDeviceUID`).
    ///
    /// BLOCKS: two CoreAudio reads. Call it off the main actor
    /// (`await offMainActor { … }`) or from code already off it.
    nonisolated static func captureDeviceID(preferredUID: String?) -> AudioDeviceID? {
        guard let preferredUID, !preferredUID.isEmpty else { return nil }
        guard let deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: preferredUID) else {
            AppLogger.audio.notice("Chosen microphone is not connected - recording from the macOS default input")
            return nil
        }
        if let defaultID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceID(), defaultID == deviceID {
            return nil
        }
        return deviceID
    }
}
