//
//  AudioDeviceManager.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import Combine
import CoreAudio

/// The immutable result of one read-only scan started by a CoreAudio notification.
/// Keeping the selected UID in the value lets the main actor reject metrics captured
/// for a selection that changed while CoreAudio was blocked.
struct AudioDeviceNotificationSnapshot: Sendable {
    let devices: [AudioDevice]
    let systemDefaultDeviceUID: String?
    let selectedDeviceUID: String?
    let inputVolumeScalar: Float?
    let activeInputDeviceIdentifier: String?
    let activeInputDeviceName: String?
}

struct AudioDeviceNotificationScanReport: Sendable {
    let origin: AudioDeviceManager.DeviceScanOrigin
    let durationMs: Int
    let didPublish: Bool
}

private struct AudioDeviceNotificationRefreshRequest: Sendable {
    let generation: Int
    let origin: AudioDeviceManager.DeviceScanOrigin
    let selectedDeviceUID: String?
}

private struct TimedAudioDeviceNotificationSnapshot: Sendable {
    let snapshot: AudioDeviceNotificationSnapshot
    let durationMs: Int
}

/// High-level audio device management
///
/// **Purpose:**
/// Manages the list of available audio input devices and handles device selection.
/// Provides @Published properties for UI binding and coordinates with CoreAudioDeviceHelper
/// for low-level device operations.
///
/// **Responsibilities:**
/// - Enumerate available input devices
/// - Track selected device
/// - Apply/restore system default device
/// - Monitor input volume metrics
/// - Update active device information
///
/// **State Management:**
/// All device state is published via @Published properties for reactive UI updates.
/// The AudioRecordingManager mirrors these properties for view binding.
///
/// **Thread Safety:**
/// All methods run on main actor for UI consistency.
@MainActor
class AudioDeviceManager {

    typealias NotificationSnapshotProvider = @Sendable (String?) -> AudioDeviceNotificationSnapshot
    typealias NotificationScanReporter = @MainActor @Sendable (AudioDeviceNotificationScanReport) -> Void

    /// Describes why a device scan was triggered so slow scans can be correlated later.
    enum DeviceScanOrigin: String, Sendable {
        case manual = "manual"
        case initialBootstrap = "initial_bootstrap"
        case coreAudioDeviceList = "coreaudio.device_list"
        case coreAudioDefaultInput = "coreaudio.default_input"
    }

    // MARK: - CoreAudio Change Monitoring

    // REAL-TIME DEVICE DISCOVERY SYSTEM
    // ---------------------------------
    // Problem: Users had to restart the app to surface newly connected microphones (AirPods, USB).
    // Solution Overview:
    // 1. During init() we register CoreAudio property listeners for the device roster and default input.
    // 2. CoreAudio fires those listeners any time hardware is added/removed or the default input changes.
    // 3. Callbacks arrive on deviceListenerQueue (background) so we avoid touching UI state off the main thread.
    // 4. Each callback starts a bounded read off-main, then publishes the newest result on @MainActor.
    // 5. SwiftUI views react immediately, so the microphone picker/menu reflects AirPods the moment they connect.
    // Threading: CoreAudio may invoke listeners on arbitrary threads; confining them to our serial queue keeps
    // ordering deterministic while still offloading processing away from the audio subsystem.
    // Testing tip: Launch the app, open the microphone menu, then pair/unpair AirPods or plug/unplug a USB mic;
    // the device list should update within ~1 frame without restarting the app.
    //
    // ACTOR ISOLATION: we mark the queue nonisolated(unsafe) so listener registration/removal helpers
    // can use it from deinit (which is nonisolated for @MainActor classes). The queue is self-contained
    // and only posts work back to the main actor, so escaping isolation here is safe.
    nonisolated(unsafe) private let deviceListenerQueue = DispatchQueue(label: "com.hyperwhisper.audio-device-listener")

    /// Property address for monitoring additions/removals in the global CoreAudio device list
    nonisolated(unsafe) private var devicesPropertyAddress = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDevices,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain
    )

    /// Property address for monitoring the system's default input device (needed when users switch defaults)
    nonisolated(unsafe) private var defaultDeviceAddress = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDefaultInputDevice,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain
    )
    nonisolated(unsafe) private var didRegisterDevicesListener = false
    nonisolated(unsafe) private var didRegisterDefaultListener = false
    nonisolated(unsafe) private var devicesListenerBlock: AudioObjectPropertyListenerBlock?
    nonisolated(unsafe) private var defaultDeviceListenerBlock: AudioObjectPropertyListenerBlock?

    /// Callback invoked whenever a previously selected device disappears and we fall back to system default.
    var onSelectedDeviceInvalidated: ((AudioDevice) -> Void)?

    private let notificationSnapshotProvider: NotificationSnapshotProvider
    private let notificationScanReporter: NotificationScanReporter?
    private var notificationRefreshGeneration = 0
    /// The newest notification generation that published, or that a synchronous scan invalidated.
    /// A scan can publish after a newer request starts, but never after a newer result or sync scan wins.
    private var notificationRefreshCompletionWatermark = 0
    /// Keep route-change bursts bounded without making one blocked HAL read a process-wide gate.
    /// Two reads may run at once; further callbacks collapse into the newest pending request.
    private let maximumConcurrentNotificationScans = 2
    private var activeNotificationScanCount = 0
    private var pendingNotificationRefresh: AudioDeviceNotificationRefreshRequest?

    init(
        registerNotificationListeners: Bool = true,
        notificationSnapshotProvider: @escaping NotificationSnapshotProvider = AudioDeviceManager.readNotificationSnapshot,
        notificationScanReporter: NotificationScanReporter? = nil
    ) {
        self.notificationSnapshotProvider = notificationSnapshotProvider
        self.notificationScanReporter = notificationScanReporter

        if registerNotificationListeners {
            registerForCoreAudioNotifications()
        }
    }

    deinit {
        // Safe to call from deinit: CoreAudio listener removal is thread-safe and does not require main thread.
        unregisterCoreAudioNotifications()
    }

    // MARK: - Published Properties

    /// List of available audio input devices
    @Published private(set) var availableDevices: [AudioDevice] = []

    /// Currently selected device (nil = system default)
    @Published var selectedDevice: AudioDevice?

    /// UID of the system's default input device (for UI display)
    /// This updates whenever the device list is refreshed or when
    /// CoreAudio notifies us of a default device change.
    @Published private(set) var systemDefaultDeviceUID: String?

    /// Input volume scalar (0.0 - 1.0) for active device
    @Published private(set) var inputVolumeScalar: Float?

    /// Name of the currently active input device
    @Published private(set) var activeInputDeviceName: String = "audio.device.default".localized

    /// UID of the currently active input device
    @Published private(set) var activeInputDeviceIdentifier: String?

    // MARK: - Private Properties

    // MARK: - CoreAudio Notifications

    /// Listen for system device list and default device changes so late-connected hardware (e.g. AirPods)
    /// automatically appears in the UI without requiring an app restart.
    private func registerForCoreAudioNotifications() {
        registerListener(
            address: &devicesPropertyAddress,
            flag: &didRegisterDevicesListener,
            description: "device list",
            origin: .coreAudioDeviceList,
            blockStorage: &devicesListenerBlock
        )

        registerListener(
            address: &defaultDeviceAddress,
            flag: &didRegisterDefaultListener,
            description: "default input",
            origin: .coreAudioDefaultInput,
            blockStorage: &defaultDeviceListenerBlock
        )
    }

    nonisolated private func unregisterCoreAudioNotifications() {
        unregisterListener(
            address: &devicesPropertyAddress,
            flag: &didRegisterDevicesListener,
            blockStorage: &devicesListenerBlock
        )
        unregisterListener(
            address: &defaultDeviceAddress,
            flag: &didRegisterDefaultListener,
            blockStorage: &defaultDeviceListenerBlock
        )
    }

    nonisolated private func registerListener(
        address: inout AudioObjectPropertyAddress,
        flag: inout Bool,
        description: String,
        origin: DeviceScanOrigin,
        blockStorage: inout AudioObjectPropertyListenerBlock?
    ) {
        guard !flag else { return }

        let listenerBlock: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self else { return }
            // CoreAudio reads can block in mach_msg during a route change. The task enters the
            // main actor only to capture and publish state; the complete read runs detached.
            Task { @MainActor in
                self.requestNotificationRefresh(reason: origin)
            }
        }

        // CoreAudio C APIs expect a stable pointer to the AudioObjectPropertyAddress structure.
        let status = withUnsafePointer(to: &address) { pointer in
            AudioObjectAddPropertyListenerBlock(
                AudioObjectID(kAudioObjectSystemObject),
                pointer,
                deviceListenerQueue,
                listenerBlock
            )
        }

        if status == noErr {
            flag = true
            blockStorage = listenerBlock
            AppLogger.audio.debug("Registered CoreAudio listener for \(description) changes")
        } else {
            AppLogger.audio.error("Failed to register CoreAudio listener for \(description) changes (status: \(status))")
        }
    }

    nonisolated private func unregisterListener(
        address: inout AudioObjectPropertyAddress,
        flag: inout Bool,
        blockStorage: inout AudioObjectPropertyListenerBlock?
    ) {
        guard flag, let block = blockStorage else { return }

        // See comment above: pointer stability is required while CoreAudio reads the address value.
        let status = withUnsafePointer(to: &address) { pointer in
            AudioObjectRemovePropertyListenerBlock(
                AudioObjectID(kAudioObjectSystemObject),
                pointer,
                deviceListenerQueue,
                block
            )
        }

        if status != noErr {
            AppLogger.audio.error("Failed to remove CoreAudio listener (status: \(status))")
        }

        // Even if removal fails, drop our references so CoreAudio can release the block when this object dies.
        flag = false
        blockStorage = nil
    }

    // MARK: - Device Enumeration

    /// Run one read-only notification scan away from the main actor, then publish only
    /// if no newer notification scan has completed first. Public/manual scans remain
    /// synchronous because launch restoration and selection depend on that contract.
    private func makeNotificationRefreshRequest(reason: DeviceScanOrigin) -> AudioDeviceNotificationRefreshRequest {
        notificationRefreshGeneration += 1
        return AudioDeviceNotificationRefreshRequest(
            generation: notificationRefreshGeneration,
            origin: reason,
            selectedDeviceUID: selectedDevice?.uid
        )
    }

    private func requestNotificationRefresh(reason: DeviceScanOrigin) {
        let request = makeNotificationRefreshRequest(reason: reason)
        guard activeNotificationScanCount < maximumConcurrentNotificationScans else {
            pendingNotificationRefresh = request
            return
        }

        startNotificationRefresh(request)
    }

    private func startNotificationRefresh(_ request: AudioDeviceNotificationRefreshRequest) {
        activeNotificationScanCount += 1
        Task { @MainActor in
            await performNotificationRefresh(request)
            activeNotificationScanCount -= 1

            if let pendingNotificationRefresh {
                self.pendingNotificationRefresh = nil
                startNotificationRefresh(pendingNotificationRefresh)
            }
        }
    }

    private func performNotificationRefresh(_ request: AudioDeviceNotificationRefreshRequest) async {

        AppLogger.audio.debug("🔍 Scanning audio devices (reason=\(request.origin.rawValue, privacy: .public))")

        let provider = notificationSnapshotProvider
        let timedSnapshot = await offMainActor {
            let start = DispatchTime.now().uptimeNanoseconds
            let snapshot = provider(request.selectedDeviceUID)
            let elapsedNanoseconds = DispatchTime.now().uptimeNanoseconds - start
            let durationMs = Int(min(elapsedNanoseconds / 1_000_000, UInt64(Int.max)))
            return TimedAudioDeviceNotificationSnapshot(snapshot: snapshot, durationMs: durationMs)
        }
        let snapshot = timedSnapshot.snapshot

        let didPublish = request.generation > notificationRefreshCompletionWatermark
        if didPublish {
            notificationRefreshCompletionWatermark = request.generation
            applyNotificationSnapshot(snapshot)
        }

        reportNotificationScan(
            snapshot,
            reason: request.origin,
            durationMs: timedSnapshot.durationMs,
            didPublish: didPublish
        )
    }

    /// Internal entry point for hardware-free notification refresh tests.
    func refreshAvailableDevicesFromNotificationForTesting(reason: DeviceScanOrigin) async {
        await performNotificationRefresh(makeNotificationRefreshRequest(reason: reason))
    }

    /// Internal entry points for burst and synchronous-scan ordering tests.
    func requestNotificationRefreshForTesting(reason: DeviceScanOrigin) {
        requestNotificationRefresh(reason: reason)
    }

    func invalidateNotificationRefreshesForTesting() {
        invalidateNotificationRefreshes()
    }

    private func invalidateNotificationRefreshes() {
        notificationRefreshGeneration += 1
        notificationRefreshCompletionWatermark = notificationRefreshGeneration
        pendingNotificationRefresh = nil
    }

    nonisolated static func readNotificationSnapshot(
        selectedDeviceUID: String?
    ) -> AudioDeviceNotificationSnapshot {
        let enumeration = CoreAudioDeviceHelper.fetchCoreAudioInputDevicesWithIDs()
        let devices = enumeration.devices
        let defaultDeviceID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceID()
        let defaultDeviceUID = defaultDeviceID.flatMap(CoreAudioDeviceHelper.copyDeviceUID)

        let selectedDeviceID: AudioDeviceID? = {
            guard let selectedDeviceUID,
                  devices.contains(where: { $0.uid == selectedDeviceUID }) else {
                return nil
            }
            return enumeration.deviceIDsByUID[selectedDeviceUID]
        }()
        let activeDeviceID = selectedDeviceID ?? defaultDeviceID
        let activeDevice = {
            if selectedDeviceID != nil, let selectedDeviceUID {
                return devices.first(where: { $0.uid == selectedDeviceUID })
            }
            guard let defaultDeviceUID else { return nil }
            return devices.first(where: { $0.uid == defaultDeviceUID })
        }()

        return AudioDeviceNotificationSnapshot(
            devices: devices,
            systemDefaultDeviceUID: defaultDeviceUID,
            selectedDeviceUID: selectedDeviceUID,
            inputVolumeScalar: activeDeviceID.flatMap(CoreAudioDeviceHelper.readInputVolumeScalar),
            activeInputDeviceIdentifier: activeDevice?.uid
                ?? activeDeviceID.flatMap(CoreAudioDeviceHelper.copyDeviceUID),
            activeInputDeviceName: activeDevice?.name
                ?? activeDeviceID.flatMap(CoreAudioDeviceHelper.copyDeviceName)
        )
    }

    private func applyNotificationSnapshot(_ snapshot: AudioDeviceNotificationSnapshot) {
        availableDevices = snapshot.devices
        systemDefaultDeviceUID = snapshot.systemDefaultDeviceUID

        // Do not let an old scan replace metrics that belong to a newer user selection.
        if selectedDevice?.uid == snapshot.selectedDeviceUID {
            let capturedSelectionStillExists = snapshot.selectedDeviceUID.map { selectedUID in
                snapshot.devices.contains(where: { $0.uid == selectedUID })
            } ?? true
            inputVolumeScalar = snapshot.inputVolumeScalar
            activeInputDeviceIdentifier = snapshot.activeInputDeviceIdentifier
            activeInputDeviceName = snapshot.activeInputDeviceName
                ?? (capturedSelectionStillExists ? selectedDevice?.name : nil)
                ?? "audio.device.default".localized
        }

        // Re-check current state. A selected device can change while the detached scan runs.
        if let selected = selectedDevice,
           selected.uid == snapshot.selectedDeviceUID,
           snapshot.devices.contains(where: { $0.uid == selected.uid }) == false {
            AppLogger.audio.warning("Selected microphone \(selected.name, privacy: .public) disappeared - reverting to system default")
            selectedDevice = nil
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Selected audio device invalidated",
                    category: "audio.devices",
                    level: .warning,
                    data: [
                        "selectedDeviceName": selected.name,
                        "selectedDeviceUID": selected.uid
                    ]
                )
            }
            onSelectedDeviceInvalidated?(selected)
        }
    }

    private func reportNotificationScan(
        _ snapshot: AudioDeviceNotificationSnapshot,
        reason: DeviceScanOrigin,
        durationMs: Int,
        didPublish: Bool
    ) {
        finishDeviceScan(
            reason: reason,
            durationMs: durationMs,
            deviceCount: snapshot.devices.count,
            defaultDeviceUID: snapshot.systemDefaultDeviceUID,
            activeDeviceName: snapshot.activeInputDeviceName ?? "audio.device.default".localized,
            didPublish: didPublish
        )

        notificationScanReporter?(
            AudioDeviceNotificationScanReport(
                origin: reason,
                durationMs: durationMs,
                didPublish: didPublish
            )
        )
    }

    /// Keep synchronous and notification scans on one telemetry contract.
    private func finishDeviceScan(
        reason: DeviceScanOrigin,
        durationMs: Int,
        deviceCount: Int,
        defaultDeviceUID: String?,
        activeDeviceName: String,
        didPublish: Bool? = nil
    ) {
        if AppLogger.isErrorLoggingEnabled,
           reason == .coreAudioDeviceList || reason == .coreAudioDefaultInput {
            var changeData: [String: Any] = [
                "reason": reason.rawValue,
                "deviceCount": deviceCount,
                "defaultDeviceUID": defaultDeviceUID ?? "unknown",
                "activeDeviceName": activeDeviceName
            ]
            if let didPublish {
                changeData["didPublish"] = didPublish
            }
            SentryService.addBreadcrumb(
                message: "Audio device change detected",
                category: "audio.devices",
                data: changeData
            )
        }

        if durationMs > 250 {
            AppLogger.audio.warning("⚠️ 📱 Device scan (\(reason.rawValue)) finished in \(durationMs)ms · devices=\(deviceCount)")
            if AppLogger.isErrorLoggingEnabled {
                var slowData: [String: Any] = [
                    "reason": reason.rawValue,
                    "durationMs": durationMs,
                    "deviceCount": deviceCount
                ]
                if let didPublish {
                    slowData["didPublish"] = didPublish
                }
                SentryService.addBreadcrumb(
                    message: "Slow audio device scan",
                    category: "audio.devices",
                    level: .warning,
                    data: slowData
                )
            }
        } else {
            AppLogger.audio.debug("📱 Device scan (\(reason.rawValue)) finished in \(durationMs)ms · devices=\(deviceCount)")
        }
    }

    /// Update list of available audio devices
    ///
    /// **What This Does:**
    /// 1. Calls CoreAudioDeviceHelper to enumerate devices
    /// 2. Updates availableDevices array
    /// 3. Refreshes volume metrics for current device
    ///
    /// **When to Call:**
    /// - On app launch
    /// - When device list may have changed (device connected/disconnected)
    /// - When refreshing UI
    func updateAvailableDevices(reason: DeviceScanOrigin = .manual) {
        // A synchronous scan is authoritative over every notification read that started earlier.
        invalidateNotificationRefreshes()
        let scanStart = Date()
        AppLogger.audio.debug("🔍 Scanning audio devices (reason=\(reason.rawValue, privacy: .public))")

        let devices = CoreAudioDeviceHelper.fetchCoreAudioInputDevices()
        availableDevices = devices

        // Update the system default device UID for UI display
        // This allows the menu to show "(Default)" next to the system's default input device
        systemDefaultDeviceUID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceUID()

        // If the previously selected device is no longer available, fall back to system default.
        if let selected = selectedDevice,
           devices.first(where: { $0.id == selected.id }) == nil {
            AppLogger.audio.warning("Selected microphone \(selected.name, privacy: .public) disappeared - reverting to system default")
            selectedDevice = nil
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Selected audio device invalidated",
                    category: "audio.devices",
                    level: .warning,
                    data: [
                        "selectedDeviceName": selected.name,
                        "selectedDeviceUID": selected.uid
                    ]
                )
            }

            // Dispatch on the main actor to ensure UI/storage observers mutate state safely.
            if let callback = onSelectedDeviceInvalidated {
                Task { @MainActor in
                    callback(selected)
                }
            }
        }

        updateInputVolumeMetrics()

        let durationMs = Int(Date().timeIntervalSince(scanStart) * 1000)
        finishDeviceScan(
            reason: reason,
            durationMs: durationMs,
            deviceCount: availableDevices.count,
            defaultDeviceUID: systemDefaultDeviceUID,
            activeDeviceName: activeInputDeviceName
        )
    }

    // MARK: - Device Selection

    /// Select a specific input device
    ///
    /// **What This Does:**
    /// Updates the selected device property and immediately applies it at the system level
    /// so Bluetooth devices have time to connect before the next recording.
    ///
    /// **Parameters:**
    /// - `device`: The device to select, or nil for system default
    func selectDevice(_ device: AudioDevice?) {
        selectedDevice = device
        updateInputVolumeMetrics()

        if let device = device {
            AppLogger.audio.info("Selected input device: \(device.name)")
        } else {
            AppLogger.audio.info("Selected system default input device")
        }

        // Apply immediately so Bluetooth devices have time to connect before recording starts.
        applySelectedInputDeviceIfNeeded()
    }

    // MARK: - Device Switching

    /// Apply the selected device by temporarily setting system default
    ///
    /// **What This Does:**
    /// 1. If a device is selected, find its CoreAudio ID
    /// 2. Get current system default device ID
    /// 3. If different, store previous ID and switch to selected device
    ///
    /// **Why System Default:**
    /// AVAudioEngine uses the system default input device. To use a specific device,
    /// we temporarily change the system default, then restore it after recording.
    ///
    /// **Important:**
    /// The selected device becomes the system default until the user chooses a different
    /// input. This avoids expensive device swaps on every recording toggle (especially for
    /// Bluetooth microphones like AirPods).
    ///
    /// **Fallback Behavior:**
    /// If the selected device's UID cannot be resolved (device disconnected, Bluetooth
    /// device reconnected with different UID, etc.), this method will:
    /// 1. Clear the selectedDevice to nil (falling back to system default)
    /// 2. Trigger the onSelectedDeviceInvalidated callback to clear persisted preference
    /// 3. Return false to indicate the fallback occurred
    ///
    /// **Returns:**
    /// - `true` if no device was selected (using system default) or device was successfully applied
    /// - `false` if the selected device was invalid and we fell back to system default
    @discardableResult
    func applySelectedInputDeviceIfNeeded() -> Bool {
        guard let selected = selectedDevice else { return true }

        // Find CoreAudio device ID from UID
        // DEVICE VALIDATION: If the UID lookup fails, the device is no longer available
        // This can happen when:
        // - Bluetooth device disconnected and reconnected (may get new UID)
        // - USB device unplugged
        // - Device list in menu was stale when user selected it
        guard let desiredID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: selected.uid) else {
            AppLogger.audio.warning("⚠️ Unable to resolve AudioDeviceID for UID: \(selected.uid) - device may have disconnected, falling back to system default")

            // FALLBACK: Clear the invalid selection and revert to system default
            // This ensures recording will use whatever macOS considers the current default
            // input device, rather than failing silently with no audio
            selectedDevice = nil

            // Notify listeners (e.g., AudioRecordingManager) to clear persisted preference
            // This prevents the app from trying to restore an invalid device on next launch
            if let callback = onSelectedDeviceInvalidated {
                callback(selected)
            }

            // Refresh device list to ensure UI shows current available devices
            updateAvailableDevices(reason: .manual)

            return false
        }

        // Get current system default
        guard let currentID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceID() else {
            AppLogger.audio.warning("⚠️ Unable to read current default input device")
            return true // Not a device selection failure, just can't read current default
        }

        // Only switch if different
        if currentID != desiredID {
            if CoreAudioDeviceHelper.setSystemDefaultInputDevice(to: desiredID) {
                AppLogger.audio.info("🎚️ Switched system default input to: \(selected.name)")
            } else {
                AppLogger.audio.warning("⚠️ Failed to switch system default input to: \(selected.name)")
            }
        }

        return true
    }

    // MARK: - Volume Metrics

    /// Refresh cached information about the active input device and volume
    ///
    /// **What This Does:**
    /// 1. Determine which device is active (selected or system default)
    /// 2. Read its volume scalar
    /// 3. Read its UID and name
    /// 4. Update published properties
    ///
    /// **When to Call:**
    /// - After device selection changes
    /// - After device enumeration
    /// - Periodically to keep volume metrics fresh
    func updateInputVolumeMetrics() {
        // Determine active device ID
        let resolvedDeviceID: AudioDeviceID? = {
            if let selected = selectedDevice,
               let id = CoreAudioDeviceHelper.findAudioDeviceID(byUID: selected.uid) {
                return id
            }
            return CoreAudioDeviceHelper.getSystemDefaultInputDeviceID()
        }()

        guard let deviceID = resolvedDeviceID else {
            // No device available
            inputVolumeScalar = nil
            activeInputDeviceIdentifier = nil
            activeInputDeviceName = "audio.device.default".localized
            return
        }

        // Read device properties
        inputVolumeScalar = CoreAudioDeviceHelper.readInputVolumeScalar(for: deviceID)
        activeInputDeviceIdentifier = CoreAudioDeviceHelper.copyDeviceUID(for: deviceID)

        if let name = CoreAudioDeviceHelper.copyDeviceName(for: deviceID) {
            activeInputDeviceName = name
        } else if let selected = selectedDevice {
            activeInputDeviceName = selected.name
        } else {
            activeInputDeviceName = "audio.device.default".localized
        }
    }

    /// Restore input volume to a previously saved value
    ///
    /// **What This Does:**
    /// Restores the microphone input volume to its original level after recording completes.
    /// Called from RecordingLifecycle.stopRecording() when auto-increase mic volume is enabled.
    ///
    /// **Parameters:**
    /// - `volume`: The volume level (0.0 to 1.0) to restore
    /// - `deviceID`: The device whose volume was originally changed. Restoration targets this
    ///   exact device — the system default at stop time may be a different device (failed
    ///   default switch, mid-recording device change), and writing to it would clobber the
    ///   wrong device's volume (issue #235). Falls back to system default if nil.
    func restoreInputVolume(_ volume: Float, deviceID: AudioDeviceID? = nil) {
        guard let deviceID = deviceID ?? CoreAudioDeviceHelper.getSystemDefaultInputDeviceID() else {
            AppLogger.audio.warning("Restore mic volume failed - unable to get system default device ID")
            return
        }

        let success = CoreAudioDeviceHelper.setInputVolumeScalar(for: deviceID, volume: volume)

        if success {
            AppLogger.audio.info("Restored microphone input volume to \(String(format: "%.0f%%", volume * 100), privacy: .public)")
        } else {
            AppLogger.audio.warning("Restore mic volume failed - unable to set volume")
        }
    }
}
