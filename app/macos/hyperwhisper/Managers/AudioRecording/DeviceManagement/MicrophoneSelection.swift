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
//  automatic pick meanwhile and returns to the chosen device as soon as it reappears.
//
//  The automatic pick (no choice, or the chosen device unplugged) is the macOS default
//  input, except when that default is a virtual device (BlackHole, Loopback, VB-CABLE…)
//  and a non-virtual input is connected: then it is the first non-virtual input, because
//  a virtual device carries app audio, not the user's voice (issue #2408).
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
    /// No choice saved: record from the automatic pick (`AutomaticMicrophonePick`) —
    /// the macOS default input unless that is a virtual device.
    case systemDefault
    /// The chosen device is connected and is the one HyperWhisper records from.
    case chosen(AudioDevice)
    /// The chosen device is not connected. HyperWhisper records from the automatic pick
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
    /// - A connected chosen device is always used, virtual or not. One that IS the
    ///   current macOS default needs no pinning: the existing `AVAudioRecorder` path
    ///   already records from the default, and keeping that path for it leaves the
    ///   most-used configuration on the long-proven capture code.
    /// - Otherwise (no choice, or the chosen device unplugged) the automatic pick is
    ///   used, pinned only when it is not the default — that is, when a virtual default
    ///   is skipped.
    func pinnedDeviceUID(systemDefaultUID: String?, devices: [AudioDevice]) -> String? {
        if case .chosen(let device) = self {
            if let systemDefaultUID, device.uid == systemDefaultUID { return nil }
            return device.uid
        }
        return AutomaticMicrophonePick.resolve(devices: devices, systemDefaultUID: systemDefaultUID)
            .pinnedDeviceUID
    }

    /// The saved choice whose device is missing, if that is the situation.
    var missingChoice: MicrophoneChoice? {
        if case .chosenNotConnected(let choice) = self { return choice }
        return nil
    }
}

// MARK: - Virtual Inputs

/// Inputs that carry app audio (or nothing) instead of the user's voice: loopback
/// drivers, meeting-app capture devices, "Stereo Mix". They stay in every picker and a
/// user may choose one by hand, but the automatic pick skips them.
enum VirtualMicrophoneDetector {
    /// Matched against the device NAME, case-insensitive substring, in this order.
    ///
    /// KEEP IN SYNC: the Windows and Linux heads share one copy of this list in
    /// `app/shared-dotnet/HyperWhisper.Application/MicrophoneSelection.cs` (issue #2408). Change both.
    ///
    /// Not virtual on purpose, because they carry the user's own processed voice: Krisp,
    /// NVIDIA Broadcast, Elgato Wave Link and any aggregate device. That is also why the
    /// CoreAudio transport type is not used: Krisp reports `virtual`.
    static let virtualNameMarkers: [String] = [
        "BlackHole",
        "Soundflower",
        "Loopback Audio",
        "VB-Audio",
        "VB-Cable",
        "CABLE Output",
        "CABLE-A Output",
        "CABLE-B Output",
        "VoiceMeeter",
        "Virtual Audio Cable",
        "ZoomAudioDevice",
        "Microsoft Teams Audio",
        "Background Music",
        "eqMac",
        "Stereo Mix",
        "What U Hear",
        "Wave Out Mix",
    ]

    static func isVirtual(name: String) -> Bool {
        virtualNameMarkers.contains { name.range(of: $0, options: .caseInsensitive) != nil }
    }
}

// MARK: - Automatic Pick

/// The device HyperWhisper records from when no chosen microphone is connected: the
/// macOS default input, unless it is virtual and a non-virtual input is connected, in
/// which case the first non-virtual input (CoreAudio enumeration order). An all-virtual
/// Mac still records from its default.
struct AutomaticMicrophonePick: Equatable, Sendable {
    /// The device used, or `nil` when the default is unknown or not in the list (then
    /// macOS routes the default and nothing is pinned).
    let device: AudioDevice?
    /// The virtual default that was passed over, if one was.
    let skippedVirtualDefault: AudioDevice?

    static func resolve(devices: [AudioDevice], systemDefaultUID: String?) -> AutomaticMicrophonePick {
        guard let systemDefaultUID,
              let defaultDevice = devices.first(where: { $0.uid == systemDefaultUID }) else {
            return AutomaticMicrophonePick(device: nil, skippedVirtualDefault: nil)
        }
        guard VirtualMicrophoneDetector.isVirtual(name: defaultDevice.name),
              let physical = devices.first(where: { !VirtualMicrophoneDetector.isVirtual(name: $0.name) }) else {
            return AutomaticMicrophonePick(device: defaultDevice, skippedVirtualDefault: nil)
        }
        return AutomaticMicrophonePick(device: physical, skippedVirtualDefault: defaultDevice)
    }

    /// The UID to open instead of the default, or `nil` when the default is the pick.
    var pinnedDeviceUID: String? {
        skippedVirtualDefault == nil ? nil : device?.uid
    }
}

// MARK: - Capture Plan

/// What a capture opens, in order, and whether it may end up on the macOS default.
///
/// The ONE rule for every open-failure fallback (recording start, mid-recording
/// restart, streaming, level preview): a capture that was pinned because a virtual
/// default was skipped never falls back to that virtual default. It tries the next
/// non-virtual input instead, and fails through the existing open-failure path when
/// none opens (#2408). Pure, so it is unit tested without CoreAudio.
struct MicrophoneCapturePlan: Equatable, Sendable {
    /// The inputs to open, in order. Empty: record from the macOS default.
    let deviceUIDs: [String]
    /// Whether the macOS default may be recorded when every input above fails to open
    /// (or when there is none).
    let fallsBackToSystemDefault: Bool

    static let systemDefault = MicrophoneCapturePlan(deviceUIDs: [], fallsBackToSystemDefault: true)

    /// The plan for a saved choice: a connected chosen device first (none when it IS the
    /// macOS default — the default path records it, even a virtual one), then the
    /// automatic pick. Its first UID is `MicrophoneResolution.pinnedDeviceUID`.
    static func resolve(
        _ resolution: MicrophoneResolution,
        devices: [AudioDevice],
        systemDefaultUID: String?
    ) -> MicrophoneCapturePlan {
        if case .chosen(let device) = resolution {
            if let systemDefaultUID, device.uid == systemDefaultUID { return .systemDefault }
            return resolve(chosenUID: device.uid, devices: devices, systemDefaultUID: systemDefaultUID)
        }
        return resolve(chosenUID: nil, devices: devices, systemDefaultUID: systemDefaultUID)
    }

    /// - Parameters:
    ///   - chosenUID: a connected chosen device that is NOT the macOS default, opened
    ///     first. A user's choice is used as is, virtual or not.
    ///   - avoidingVirtualDefault: the capture being restarted was pinned because a
    ///     virtual default was skipped, so the default stays off-limits while it is
    ///     virtual (or unknown) even when no physical input is left to skip it for.
    static func resolve(
        chosenUID: String?,
        devices: [AudioDevice],
        systemDefaultUID: String?,
        avoidingVirtualDefault: Bool = false
    ) -> MicrophoneCapturePlan {
        let pick = AutomaticMicrophonePick.resolve(devices: devices, systemDefaultUID: systemDefaultUID)
        let skipsVirtualDefault = pick.skippedVirtualDefault != nil
        // The stand-ins for a skipped virtual default: every non-virtual input, the
        // automatic pick first (enumeration order), so a failed open tries the next.
        let standIns = skipsVirtualDefault
            ? devices.filter { !VirtualMicrophoneDetector.isVirtual(name: $0.name) }.map(\.uid)
            : []

        var uids: [String] = []
        if let chosenUID, !chosenUID.isEmpty {
            uids.append(chosenUID)
        }
        for uid in standIns where !uids.contains(uid) {
            uids.append(uid)
        }

        let defaultDevice = systemDefaultUID.flatMap { uid in devices.first(where: { $0.uid == uid }) }
        let defaultIsVirtualOrUnknown = defaultDevice.map { VirtualMicrophoneDetector.isVirtual(name: $0.name) } ?? true
        let fallsBack = !skipsVirtualDefault && !(avoidingVirtualDefault && defaultIsVirtualOrUnknown)
        return MicrophoneCapturePlan(deviceUIDs: uids, fallsBackToSystemDefault: fallsBack)
    }
}

/// `MicrophoneCapturePlan` resolved to CoreAudio device IDs, for the capture code.
struct MicrophoneCaptureRoute: Equatable, Sendable {
    /// The devices to open, in order. Empty: record from the macOS default.
    let deviceIDs: [AudioDeviceID]
    /// Whether the macOS default may be recorded when every device above fails to open.
    let fallsBackToSystemDefault: Bool

    static let systemDefault = MicrophoneCaptureRoute(deviceIDs: [], fallsBackToSystemDefault: true)

    /// The device opened first, or `nil` for the macOS default.
    var primaryDeviceID: AudioDeviceID? { deviceIDs.first }

    /// A capture that may not fall back to the default was pinned to skip a virtual
    /// default; its mid-capture restarts keep avoiding it.
    var avoidsVirtualDefault: Bool { !fallsBackToSystemDefault }
}

// MARK: - Labels

/// User-facing labels for the microphone picker, shared by Settings and the menu bar.
enum MicrophonePickerLabels {
    /// The name of the device the "Default" row records from — the automatic pick, so
    /// a skipped virtual default is not named — or a generic name when unknown.
    static func defaultDeviceName(devices: [AudioDevice], systemDefaultUID: String?) -> String {
        if let device = AutomaticMicrophonePick.resolve(devices: devices, systemDefaultUID: systemDefaultUID).device {
            return device.name
        }
        return "audio.device.default".localized
    }

    /// "Default · <automatic pick>"
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
    /// What a capture should open for `preferredUID` (`MicrophoneCapturePlan`).
    ///
    /// - The chosen device while it is connected — `.systemDefault` when it is the
    ///   current default (see `MicrophoneResolution.pinnedDeviceUID`).
    /// - Otherwise (no choice, or the chosen device unplugged) the automatic pick: the
    ///   default, or the non-virtual inputs in order when the default is a virtual
    ///   device (`AutomaticMicrophonePick`) — and then never the default.
    ///
    /// BLOCKS: CoreAudio reads, including a full input enumeration. Call it off the main
    /// actor (`await offMainActor { … }`) or from code already off it.
    nonisolated static func captureRoute(preferredUID: String?) -> MicrophoneCaptureRoute {
        var chosen: (uid: String, id: AudioDeviceID)?
        if let preferredUID, !preferredUID.isEmpty {
            if let deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: preferredUID) {
                if let defaultID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceID(), defaultID == deviceID {
                    return .systemDefault
                }
                chosen = (preferredUID, deviceID)
            } else {
                AppLogger.audio.notice("Chosen microphone is not connected - recording from the automatic pick")
            }
        }

        let enumeration = CoreAudioDeviceHelper.fetchCoreAudioInputDevicesWithIDs()
        let systemDefaultUID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceUID()
        let plan = MicrophoneCapturePlan.resolve(
            chosenUID: chosen?.uid,
            devices: enumeration.devices,
            systemDefaultUID: systemDefaultUID
        )
        if chosen == nil {
            logSkippedVirtualDefault(
                AutomaticMicrophonePick.resolve(devices: enumeration.devices, systemDefaultUID: systemDefaultUID)
            )
        }
        var deviceIDs: [AudioDeviceID] = []
        for uid in plan.deviceUIDs {
            let id = uid == chosen?.uid ? chosen?.id : enumeration.deviceIDsByUID[uid]
            if let id, !deviceIDs.contains(id) {
                deviceIDs.append(id)
            }
        }
        return MicrophoneCaptureRoute(deviceIDs: deviceIDs, fallsBackToSystemDefault: plan.fallsBackToSystemDefault)
    }

    /// The devices a capture falls back to, in order, when its own device stops opening
    /// mid-capture: the automatic pick's non-virtual stand-ins when the default is
    /// virtual, else the macOS default. With `avoidingVirtualDefault` (the capture was
    /// pinned to skip a virtual default) the default is left out while it is virtual,
    /// even when no physical input remains — the capture is then lost, not moved onto
    /// BlackHole (#2408).
    ///
    /// BLOCKS: CoreAudio reads. Call it off the main actor.
    nonisolated static func fallbackInputDeviceIDs(avoidingVirtualDefault: Bool) -> [AudioDeviceID] {
        let enumeration = CoreAudioDeviceHelper.fetchCoreAudioInputDevicesWithIDs()
        let plan = MicrophoneCapturePlan.resolve(
            chosenUID: nil,
            devices: enumeration.devices,
            systemDefaultUID: CoreAudioDeviceHelper.getSystemDefaultInputDeviceUID(),
            avoidingVirtualDefault: avoidingVirtualDefault
        )
        var deviceIDs = plan.deviceUIDs.compactMap { enumeration.deviceIDsByUID[$0] }
        if plan.fallsBackToSystemDefault, let defaultID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceID() {
            deviceIDs.append(defaultID)
        }
        return deviceIDs
    }

    /// One line whenever the automatic pick passes over a virtual default. Device names
    /// are logged `.public`, as everywhere else in the audio log (`selectDevice`,
    /// keep-warm); they never go to Sentry from here.
    nonisolated static func logSkippedVirtualDefault(_ pick: AutomaticMicrophonePick) {
        guard let skipped = pick.skippedVirtualDefault, let device = pick.device else { return }
        let skippedName = skipped.name
        let usedName = device.name
        AppLogger.audio.notice("Default input \(skippedName, privacy: .public) looks like a virtual device - recording from \(usedName, privacy: .public) instead")
    }
}
