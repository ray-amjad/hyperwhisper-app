//
//  AudioDeviceManager.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import Combine
import CoreAudio

// MARK: - Off-Main-Actor Scan Results
//
// Both types are deliberately declared at FILE SCOPE rather than nested inside
// `AudioDeviceManager`. That type is `@MainActor`, and a global actor annotation on a
// nominal type is inferred by the declarations nested inside it — a nested struct would
// pick up main-actor-isolated initializers, which the `nonisolated static` producers below
// could not call. File scope sidesteps the question entirely. Do not move them inside.

/// One complete, off-main-actor CoreAudio device scan.
///
/// `Sendable` because `AudioDevice` is three immutable `String`s (now stated explicitly on
/// the type). Carrying both values together means the main actor performs a single batch of
/// `@Published` writes per scan instead of interleaving writes with blocking HAL reads.
struct AudioDeviceScanSnapshot: Sendable {
    let devices: [AudioDevice]
    let systemDefaultUID: String?
}

/// Everything one volume-metric refresh reads from the HAL, in a single Sendable value.
///
/// `resolvedDeviceID == nil` means no input device could be resolved at all — the caller
/// clears the published metrics, exactly as the synchronous version did.
struct AudioInputDeviceProbe: Sendable {
    let resolvedDeviceID: AudioDeviceID?
    let volumeScalar: Float?
    let uid: String?
    let name: String?
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
/// The type is `@MainActor` and every `@Published` write happens on the main actor.
/// The blocking CoreAudio reads do NOT: `fetchSnapshot()` and
/// `probeActiveInputDevice(selectedUID:)` are `nonisolated static` and run detached, and
/// the device-switching calls go through the `CoreAudioDeviceHelper` async wrappers. That
/// is why `updateAvailableDevices(reason:)`, `updateInputVolumeMetrics()` and
/// `applySelectedInputDeviceIfNeeded()` are `async` (Sentry HYPERWHISPER-HP). Do not make
/// them synchronous again.
///
/// The one remaining synchronous CoreAudio call in this type is `restoreInputVolume(_:deviceID:)`,
/// on the recording-STOP path. It is not part of HYPERWHISPER-HP's listener fan-out and was
/// left alone deliberately; moving it belongs with the rest of the stop path.
@MainActor
class AudioDeviceManager {

    /// Describes why a device scan was triggered so slow scans can be correlated later.
    enum DeviceScanOrigin: String {
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
    // 4. Each callback schedules updateAvailableDevices() on @MainActor, guaranteeing safe @Published updates.
    //    That scan reads CoreAudio off the main actor and hops back to publish (HYPERWHISPER-HP).
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

    init() {
        registerForCoreAudioNotifications()
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
            // THIS IS THE HYPERWHISPER-HP FIX. The comment that used to sit here claimed
            // updateAvailableDevices() was "idempotent and cheap". It is idempotent; it was
            // never cheap. It is a synchronous CoreAudio fan-out (device roster + default
            // input UID + the volume-metric probe), i.e. many mach_msg round trips to
            // coreaudiod — and this listener fires during exactly the route change that
            // makes coreaudiod slow to answer. Running that on the main actor is what hung
            // the app for 10+ seconds.
            //
            // updateAvailableDevices(reason:) is now async: it does the HAL reads off the
            // main actor and only the @Published writes on it. The Task stays
            // fire-and-forget so the CoreAudio callback queue is never blocked.
            Task { @MainActor in
                await self.updateAvailableDevices(reason: origin)
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

    /// The scan that is currently reading the hardware, if any.
    /// See the SINGLE-FLIGHT note on `updateAvailableDevices(reason:)`.
    private var scanTask: Task<[AudioDevice], Never>?

    /// Reason of a scan that was requested while `scanTask` was already running. At most
    /// one is kept: several requests arriving during one scan collapse into a single
    /// re-run, which is the whole point.
    private var pendingScanReason: DeviceScanOrigin?

    /// Read the device roster and the system default input UID off the main actor.
    ///
    /// **Sentry HYPERWHISPER-HP** ("App hanging for at least 10000 ms"): both reads are
    /// synchronous `AudioObjectGetPropertyData` calls — `mach_msg` round trips to
    /// `coreaudiod` — and they were reached from `@MainActor` code driven by a CoreAudio
    /// property-listener block. During the route change that fires that listener,
    /// `coreaudiod` is precisely the process that is slow to answer, so the main thread
    /// parked in `mach_msg` for the full duration.
    ///
    /// **Why `nonisolated` alone is not the fix:** the `CoreAudioDeviceHelper` methods are
    /// already `nonisolated`, and a synchronous `nonisolated` method called from
    /// `@MainActor` code still runs on the caller's thread. The `offMainActor` hop is what
    /// actually leaves the main thread. Both reads share one hop: they are independent,
    /// and a scan should cost one thread transition, not one per property.
    nonisolated static func fetchSnapshot() async -> AudioDeviceScanSnapshot {
        await offMainActor {
            AudioDeviceScanSnapshot(
                devices: CoreAudioDeviceHelper.fetchCoreAudioInputDevices(),
                systemDefaultUID: CoreAudioDeviceHelper.getSystemDefaultInputDeviceUID()
            )
        }
    }

    /// Update list of available audio devices
    ///
    /// **What This Does:**
    /// 1. Reads the device roster and system default UID off the main actor (`fetchSnapshot()`)
    /// 2. Updates availableDevices array
    /// 3. Refreshes volume metrics for current device
    ///
    /// **When to Call:**
    /// - On app launch
    /// - When device list may have changed (device connected/disconnected)
    /// - When refreshing UI
    ///
    /// **SINGLE-FLIGHT.**
    /// CoreAudio coalesces notifications, and a bursty route change (AirPods reconnect,
    /// dock hot-plug) can put several scans in flight at once — something that could not
    /// happen while this method was synchronous. Rather than let them overlap and then
    /// referee the winner, only one scan ever touches the hardware at a time: a request
    /// arriving while a scan is running records its reason and awaits the running scan,
    /// which re-runs exactly once at the end to pick up whatever changed.
    ///
    /// That is strictly better than a newest-wins guard for two reasons. Scans cannot
    /// interleave, so the roster and the volume metrics can never come from two different
    /// reads of the hardware. And no scan is ever discarded after doing the work, so
    /// **every** hardware read publishes its result and emits its timing breadcrumb — the
    /// signal HYPERWHISPER-HP is diagnosed from. A guard would have dropped scans exactly
    /// when they were slowest, which is exactly when the breadcrumb matters.
    ///
    /// **Do not call this from inside a scan.** A call made from within `performDeviceScan`
    /// (or anything it awaits) would await the very task it is running in and hang. The
    /// invalidation callback is deliberately dispatched into its own `Task` for this
    /// reason; `applySelectedInputDeviceIfNeeded()` re-scans on its fallback path and must
    /// therefore never be awaited from a scan either.
    ///
    /// **Returns** the roster that was published — always a real hardware read, and always
    /// the same array that landed in `availableDevices`. Awaiting this and then reading
    /// `availableDevices` is equally correct; `AudioRecordingManager.configure(...)` uses
    /// the return value only so the microphone-restoration block cannot be reordered away
    /// from the scan it depends on.
    @discardableResult
    func updateAvailableDevices(reason: DeviceScanOrigin = .manual) async -> [AudioDevice] {
        if let running = scanTask {
            // A scan is already inside CoreAudio. Ask it to run once more when it lands
            // (several requests collapse into that one re-run) and wait for the result.
            pendingScanReason = reason
            AppLogger.audio.debug("🔍 Device scan (reason=\(reason.rawValue, privacy: .public)) coalesced into the scan in flight")
            return await running.value
        }

        let task: Task<[AudioDevice], Never> = Task { @MainActor in
            var devices = await self.performDeviceScan(reason: reason)
            while let queued = self.pendingScanReason {
                self.pendingScanReason = nil
                devices = await self.performDeviceScan(reason: queued)
            }
            self.scanTask = nil
            return devices
        }
        scanTask = task
        return await task.value
    }

    /// One complete device scan: read the hardware off the main actor, publish on it.
    ///
    /// Every call that reaches this method publishes and emits its breadcrumbs; there is
    /// no early return. Coalescing happens in `updateAvailableDevices(reason:)` *before*
    /// any hardware read, so nothing is ever measured and then thrown away.
    private func performDeviceScan(reason: DeviceScanOrigin) async -> [AudioDevice] {
        let scanStart = Date()
        AppLogger.audio.debug("🔍 Scanning audio devices (reason=\(reason.rawValue, privacy: .public))")

        let snapshot = await Self.fetchSnapshot()
        let devices = snapshot.devices

        availableDevices = devices

        // Update the system default device UID for UI display
        // This allows the menu to show "(Default)" next to the system's default input device
        systemDefaultDeviceUID = snapshot.systemDefaultUID

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

        await updateInputVolumeMetrics()

        if AppLogger.isErrorLoggingEnabled,
           reason == .coreAudioDeviceList || reason == .coreAudioDefaultInput {
            SentryService.addBreadcrumb(
                message: "Audio device change detected",
                category: "audio.devices",
                data: [
                    "reason": reason.rawValue,
                    "deviceCount": self.availableDevices.count,
                    "defaultDeviceUID": self.systemDefaultDeviceUID ?? "unknown",
                    "activeDeviceName": self.activeInputDeviceName
                ]
            )
        }

        // Same 250ms threshold as before. It now measures the off-actor scan rather than a
        // main-thread stall, which is still the signal we want: a slow scan means coreaudiod
        // is struggling, and that is what correlates with the device-change reports.
        let durationMs = Int(Date().timeIntervalSince(scanStart) * 1000)
        if durationMs > 250 {
            AppLogger.audio.warning("⚠️ 📱 Device scan (\(reason.rawValue)) finished in \(durationMs)ms · devices=\(self.availableDevices.count)")
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Slow audio device scan",
                    category: "audio.devices",
                    level: .warning,
                    data: [
                        "reason": reason.rawValue,
                        "durationMs": durationMs,
                        "deviceCount": self.availableDevices.count
                    ]
                )
            }
        } else {
            AppLogger.audio.debug("📱 Device scan (\(reason.rawValue)) finished in \(durationMs)ms · devices=\(self.availableDevices.count)")
        }

        return devices
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
    ///
    /// **Deliberately still synchronous.** Menu items and picker bindings call this from
    /// SwiftUI button actions (`MainAppView`, `OnboardingSourceViews`); making it `async`
    /// would push `Task {}` wrappers into every one of those call sites for no benefit.
    /// `selectedDevice` is still assigned synchronously, so the UI updates on the same
    /// runloop turn it always did. Only the CoreAudio work is deferred.
    func selectDevice(_ device: AudioDevice?) {
        selectedDevice = device

        if let device = device {
            AppLogger.audio.info("Selected input device: \(device.name)")
        } else {
            AppLogger.audio.info("Selected system default input device")
        }

        Task { @MainActor in
            await self.updateInputVolumeMetrics()
            // Apply immediately so Bluetooth devices have time to connect before recording starts.
            await self.applySelectedInputDeviceIfNeeded()
        }
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
    ///
    /// **Off the main actor (HYPERWHISPER-HP):** every CoreAudio call below is now awaited
    /// through a `CoreAudioDeviceHelper` async wrapper. `findAudioDeviceID(byUID:)` alone
    /// enumerates every device and reads a UID per device, so this was as expensive as a
    /// full scan. The `@Published` writes and the invalidation callback still run on the
    /// main actor. The return contract and the fallback ordering are unchanged.
    ///
    /// **STALE-SELECTION GUARD.** This is the only newly-async path that writes *global
    /// system state*, so it is the one that must re-check what it is acting on. The
    /// selection can change while it is suspended — the user picks a different microphone
    /// from the menu, or a scan invalidates the old one — and without the re-check a stale
    /// resume would either set the system default input back to the microphone the user
    /// just moved away from, or clear a selection (and, via `onSelectedDeviceInvalidated`,
    /// the persisted preference) that is newer than the one it looked up. So the selected
    /// UID is captured once up front and re-validated after every await, before any
    /// mutation and before the default-device write. A superseded call returns `true`: it
    /// invalidated nothing, and the newer selection schedules its own apply.
    @discardableResult
    func applySelectedInputDeviceIfNeeded() async -> Bool {
        guard let selected = selectedDevice else { return true }
        let appliedUID = selected.uid

        /// The selection this call was launched for is still the selection in effect.
        func selectionIsStillCurrent() -> Bool {
            selectedDevice?.uid == appliedUID
        }

        // Find CoreAudio device ID from UID
        // DEVICE VALIDATION: If the UID lookup fails, the device is no longer available
        // This can happen when:
        // - Bluetooth device disconnected and reconnected (may get new UID)
        // - USB device unplugged
        // - Device list in menu was stale when user selected it
        let resolvedID = await offMainActor { CoreAudioDeviceHelper.findAudioDeviceID(byUID: appliedUID) }

        guard selectionIsStillCurrent() else {
            AppLogger.audio.info("Input-device apply for \(selected.name, privacy: .public) superseded by a newer selection - not applying")
            return true
        }

        guard let desiredID = resolvedID else {
            AppLogger.audio.warning("⚠️ Unable to resolve AudioDeviceID for UID: \(appliedUID) - device may have disconnected, falling back to system default")

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
            await updateAvailableDevices(reason: .manual)

            return false
        }

        // Get current system default
        guard let currentID = await offMainActor({ CoreAudioDeviceHelper.getSystemDefaultInputDeviceID() }) else {
            AppLogger.audio.warning("⚠️ Unable to read current default input device")
            return true // Not a device selection failure, just can't read current default
        }

        guard selectionIsStillCurrent() else {
            AppLogger.audio.info("Input-device apply for \(selected.name, privacy: .public) superseded before the default-device write")
            return true
        }

        // Only switch if different
        if currentID != desiredID {
            if await offMainActor({ CoreAudioDeviceHelper.setSystemDefaultInputDevice(to: desiredID) }) {
                AppLogger.audio.info("🎚️ Switched system default input to: \(selected.name)")
            } else {
                AppLogger.audio.warning("⚠️ Failed to switch system default input to: \(selected.name)")
            }
        }

        return true
    }

    // MARK: - Volume Metrics

    /// Resolve the active input device and read its volume, UID and name — off the main actor.
    ///
    /// **This is the largest single piece of HYPERWHISPER-HP.** Per call it can make ~5+N
    /// synchronous `mach_msg` round trips for N devices: `findAudioDeviceID(byUID:)`
    /// enumerates every device and reads a UID per device, then `readInputVolumeScalar`
    /// makes up to two property reads, then `copyDeviceUID` and `copyDeviceName` make one
    /// each. It is called from every device scan, from `selectDevice(_:)` and from
    /// `AudioRecordingManager`'s `selectedDevice` `didSet`, so it ran on the main actor
    /// several times per route change.
    ///
    /// **One hop for the whole fan-out.** Up to five blocking reads run inside a single
    /// `offMainActor` block rather than one wrapped call each; the thread transition costs
    /// microseconds, the `mach_msg` round trips it wraps cost seconds.
    ///
    /// `selectedUID` is passed in rather than read here because `selectedDevice` is
    /// `@MainActor` state; the caller captures it before suspending so the probe and the
    /// name fallback describe the same selection.
    nonisolated static func probeActiveInputDevice(selectedUID: String?) async -> AudioInputDeviceProbe {
        await offMainActor { () -> AudioInputDeviceProbe in
            // Determine active device ID — same precedence as before: an explicitly
            // selected device that still resolves, otherwise the system default.
            let resolvedDeviceID: AudioDeviceID? = {
                if let selectedUID,
                   let id = CoreAudioDeviceHelper.findAudioDeviceID(byUID: selectedUID) {
                    return id
                }
                return CoreAudioDeviceHelper.getSystemDefaultInputDeviceID()
            }()

            guard let deviceID = resolvedDeviceID else {
                return AudioInputDeviceProbe(resolvedDeviceID: nil, volumeScalar: nil, uid: nil, name: nil)
            }

            return AudioInputDeviceProbe(
                resolvedDeviceID: deviceID,
                volumeScalar: CoreAudioDeviceHelper.readInputVolumeScalar(for: deviceID),
                uid: CoreAudioDeviceHelper.copyDeviceUID(for: deviceID),
                name: CoreAudioDeviceHelper.copyDeviceName(for: deviceID)
            )
        }
    }

    /// Refresh cached information about the active input device and volume
    ///
    /// **What This Does:**
    /// 1. Determine which device is active (selected or system default)
    /// 2. Read its volume scalar
    /// 3. Read its UID and name
    /// 4. Update published properties
    ///
    /// Steps 1-3 happen off the main actor in `probeActiveInputDevice(selectedUID:)`;
    /// only step 4 runs here.
    ///
    /// **When to Call:**
    /// - After device selection changes
    /// - After device enumeration
    /// - Periodically to keep volume metrics fresh
    ///
    /// **A probe only publishes for the selection it was launched for.** Probe durations
    /// vary by device — a Bluetooth headset that is still negotiating can take seconds
    /// while the built-in microphone answers instantly — so a probe started for microphone
    /// A can easily land after a probe started for microphone B and overwrite all three
    /// published fields with A's values. Nothing would correct that until the next scan,
    /// which may never come. So the selection is captured before suspending and re-checked
    /// after; a probe whose selection has moved on publishes nothing and lets the newer
    /// probe stand.
    ///
    /// Declining is safe because every path that changes `selectedDevice` schedules a
    /// fresh probe: `selectDevice(_:)` and the `AudioRecordingManager` mirror both do,
    /// `performDeviceScan` awaits one after its invalidation branch, and
    /// `applySelectedInputDeviceIfNeeded()`'s fallback re-scans.
    func updateInputVolumeMetrics() async {
        // Captured before suspending so the probe, the publish decision and the name
        // fallback below all agree on which device was selected when this refresh started.
        let selected = selectedDevice
        let probedUID = selected?.uid

        let probe = await Self.probeActiveInputDevice(selectedUID: probedUID)

        guard selectedDevice?.uid == probedUID else {
            AppLogger.audio.debug("Input volume probe superseded by a newer device selection - not publishing")
            return
        }

        guard probe.resolvedDeviceID != nil else {
            // No device available
            inputVolumeScalar = nil
            activeInputDeviceIdentifier = nil
            activeInputDeviceName = "audio.device.default".localized
            return
        }

        // Publish device properties
        inputVolumeScalar = probe.volumeScalar
        activeInputDeviceIdentifier = probe.uid

        if let name = probe.name {
            activeInputDeviceName = name
        } else if let selected = selected {
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
