//
//  AudioRecordingManager.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//  Refactored to modular architecture on 2025-10-19.
//
//  AUDIO RECORDING MANAGER (ORCHESTRATOR)
//  This class coordinates all audio recording functionality through specialized sub-managers.
//  It maintains the same public API as before, but now delegates to focused components.
//
//  **Architecture:**
//  ```
//  AudioRecordingManager (orchestrator)
//    ├── MicrophonePermissionManager    - Permission state and requests
//    ├── AudioDeviceManager             - Device enumeration and selection
//    ├── RecordingLifecycle             - Start/stop recording logic
//    ├── RecordingSessionManager        - Core Data session tracking
//    ├── CrashRecoveryManager           - Orphaned recording recovery
//    ├── RecordingTranscriptionFlow       - Recording + transcription flow
//    ├── AutoPasteHandler               - Smart paste with file tags
//    ├── SimpleRecorder                 - AVAudioRecorder-based capture (16kHz mono)
//    ├── MicrophoneKeepWarmManager      - Idle mic keep-warm session for low-latency start
//    ├── AudioFileConverter             - WAV to M4A conversion
//    ├── FileWatcher                    - File readiness monitoring
//    └── PowerActivityManager           - App Nap prevention
//  ```
//
//  **Benefits of This Architecture:**
//  - **Maintainability**: Each component has a single, focused responsibility
//  - **Testability**: Individual components can be unit tested in isolation
//  - **Reusability**: Components can be used by other managers if needed
//  - **Readability**: Shorter files with clear purpose and boundaries
//
//  **Public API Preservation:**
//  All @Published properties and public methods remain unchanged.
//  Views continue to work without modification.
//
//  **Thread Safety:**
//  All methods run on main actor for UI consistency and Core Data safety.

import Foundation
import AVFoundation
import Combine
import SwiftUI
import CoreData
import AppKit
import KeyboardShortcuts

// MARK: - Audio Recording Manager (Orchestrator)

/// Main orchestrator for audio recording operations
/// @MainActor ensures UI updates happen on the main thread
@MainActor
class AudioRecordingManager: NSObject, ObservableObject {

    /// Shared threshold for low input volume warnings (system slider 0-1)
    static let lowInputVolumeWarningThreshold: Float = 0.25

    // MARK: - Published Properties (UI Binding)

    /// Current recording status
    /// Updated by RecordingLifecycle via Combine bindings
    @Published var isRecording: Bool = false

    // MARK: - Live Metrics (Isolated for Performance)

    /// Dedicated metrics object for high-frequency UI updates (audioLevel, recordingDuration).
    /// Views needing real-time audio data should observe this instead of AudioRecordingManager.
    ///
    /// **Why This Exists:**
    /// SwiftUI invalidates ALL views observing an ObservableObject when ANY @Published
    /// property changes. By moving 30 FPS audioLevel updates to a separate object, we prevent
    /// MainAppView and HistoryView from re-evaluating during recording, which was causing
    /// the RecordingDialog waveform to lag.
    let liveMetrics = RecordingLiveMetrics()

    /// Current audio input level (0.0 to 1.0) for UI visualization.
    /// Computed getter for backward compatibility - reads from liveMetrics.
    /// Views should observe `liveMetrics` directly for reactive updates.
    var audioLevel: Float { liveMetrics.audioLevel }

    /// Available audio input devices (microphones)
    /// Updated by AudioDeviceManager when system devices change
    @Published var availableDevices: [AudioDevice] = []

    /// Currently selected audio input device
    /// When changed, device manager updates volume metrics
    @Published var selectedDevice: AudioDevice? {
        didSet {
            guard selectedDevice?.id != oldValue?.id else { return }
            deviceManager.updateInputVolumeMetrics()
        }
    }

    /// Current system input volume slider value (0.0 - 1.0)
    /// Read by AudioDeviceManager from CoreAudio APIs
    @Published var inputVolumeScalar: Float?

    /// Human-readable name of the active input device
    /// Updated by AudioDeviceManager during device enumeration
    @Published var activeInputDeviceName: String = "audio.device.default".localized

    /// Identifier for the active input device (used for dismissing warnings)
    /// Updated by AudioDeviceManager alongside device name
    @Published var activeInputDeviceIdentifier: String?

    /// UID of the system's default input device
    /// Used to show "(Default)" indicator next to the system default in the microphone menu
    @Published var systemDefaultDeviceUID: String?

    /// Duration of current recording in seconds.
    /// Computed getter for backward compatibility - reads from liveMetrics.
    /// Views should observe `liveMetrics` directly for reactive updates.
    var recordingDuration: TimeInterval { liveMetrics.recordingDuration }

    /// Whether microphone permission has been granted
    /// Updated by MicrophonePermissionManager
    @Published var hasMicrophonePermission: Bool = false

    /// Whether to show the permission denied alert
    /// Set by MicrophonePermissionManager when permission is denied
    @Published var showPermissionDeniedAlert: Bool = false

    /// Error message if something goes wrong
    /// Set by various managers when errors occur
    @Published var errorMessage: String?

    /// Last recording URL for retry functionality
    /// Updated by RecordingLifecycle after each recording
    @Published var lastRecordingURL: URL?

    /// Live input level (0.0–1.0) for the onboarding microphone step's meter.
    ///
    /// **Why a separate signal from `audioLevel`:**
    /// `audioLevel`/`liveMetrics` only publish while an actual recording is in
    /// flight. The onboarding "Set up your microphone" screen needs a level
    /// preview *without* starting a real recording, so it runs a dedicated,
    /// short-lived metering session (see `startInputLevelPreview()`), publishing
    /// here. Kept off `liveMetrics` because this drives a single, isolated screen.
    @Published var idleInputLevel: Float = 0

    // MARK: - Dependencies (Weak References)

    /// Reference to transcription manager for handling transcription after recording
    /// Weak to avoid retain cycle (TranscriptionPipeline may reference this)
    weak var transcriptionPipeline: TranscriptionPipeline?

    /// Reference to settings manager for accessing user preferences
    /// Weak to avoid retain cycle (injected from app root)
    weak var settingsManager: SettingsManager?

    /// Reference to provider health monitor for cloud readiness gating
    /// Weak to avoid retain cycle (shared singleton)
    weak var providerHealthManager: CloudProviderHealthManager?

    /// Reference to app state for UI coordination
    /// Weak to avoid retain cycle (app-wide state)
    weak var appState: AppState?

    /// Reference to license manager for usage tracking and limits
    /// Weak to avoid retain cycle (shared singleton)
    weak var licenseManager: LicenseManager?

    // MARK: - Sub-Managers (Strong References)

    /// Handles microphone permission checks and requests
    private let permissionManager = MicrophonePermissionManager()

    /// Manages audio device enumeration and selection
    private let deviceManager = AudioDeviceManager()

    /// Handles Core Data recording session lifecycle
    private let sessionManager = RecordingSessionManager()

    /// Handles recovery of incomplete recordings from crashes
    private let recoveryManager: CrashRecoveryManager

    /// Coordinates recording start/stop with audio engine
    private let lifecycleManager: RecordingLifecycle

    /// Coordinates recording toggle with transcription flow
    private let recordingTranscriptionFlow: RecordingTranscriptionFlow

    /// Handles smart paste with file tag processing
    private let autoPasteHandler = AutoPasteHandler()

    /// Simple audio recorder using AVAudioRecorder at 16kHz mono
    private let simpleRecorder = SimpleRecorder()

    /// Keeps the microphone input warm between recordings to reduce startup latency
    private let keepWarmManager = MicrophoneKeepWarmManager()

    /// Handles WAV to M4A conversion
    private let audioFileConverter = AudioFileConverter()

    /// Monitors files for write completion
    private let fileWatcher = FileWatcher()

    /// Prevents App Nap during recording/transcription
    private let powerManager = PowerActivityManager()

    /// Manages audio environment (system volume, media players) during recording
    private let audioSessionManager = AudioSessionManager.shared

    // MARK: - Private Cancellables for Combine Bindings

    /// Store subscriptions to prevent deallocation
    private var cancellables = Set<AnyCancellable>()

    /// Lightweight metering recorder for the onboarding input-level preview.
    /// Writes to /dev/null with metering enabled; never produces a file.
    private var inputLevelPreviewRecorder: AVAudioRecorder?

    /// Polling task that samples `inputLevelPreviewRecorder` at ~30 FPS.
    private var inputLevelPreviewTask: Task<Void, Never>?
    
    /// Observer for UserDefaults changes
    private var defaultsObserver: NSObjectProtocol?

    /// Observer for settings changes that affect microphone keep-warm
    private var keepWarmSettingsObserver: NSObjectProtocol?

    /// Set when a Push to Talk reconfiguration is requested while a recording is
    /// in flight. Tearing down the active `BareModifierKeyMonitor` mid-recording
    /// would orphan a latched (double-tap locked) session and leave the user
    /// unable to stop it via the modifier, so we defer the reconfigure until the
    /// recording finishes and the monitor is safely idle.
    private var needsPushToTalkReconfigure = false

    // MARK: - Initialization

    /// Initialize the orchestrator and all sub-managers
    ///
    /// **What This Does:**
    /// 1. Creates all sub-manager instances with proper dependency injection
    /// 2. Sets up Combine bindings to mirror sub-manager state to published properties
    /// 3. Checks initial microphone permission status
    ///
    /// **Architecture:**
    /// Sub-managers are created with dependencies passed via constructors or configure() methods.
    /// This allows for clean dependency injection and testability.
    override init() {
        // STEP 1: Create sub-managers with dependencies
        // Some managers need references to other managers, so we create them in dependency order

        // RecoveryManager needs audioFileConverter for CAF to M4A conversion
        self.recoveryManager = CrashRecoveryManager(audioFileConverter: audioFileConverter)

        // RecordingLifecycle needs multiple dependencies
        self.lifecycleManager = RecordingLifecycle(
            simpleRecorder: simpleRecorder,
            audioFileConverter: audioFileConverter,
            deviceManager: deviceManager,
            sessionManager: sessionManager,
            audioSessionManager: audioSessionManager
        )

        // RecordingTranscriptionFlow needs most dependencies
        self.recordingTranscriptionFlow = RecordingTranscriptionFlow(
            recordingLifecycle: lifecycleManager,
            autoPasteHandler: autoPasteHandler,
            powerActivityManager: powerManager,
            fileWatcher: fileWatcher,
            permissionManager: permissionManager
        )

        super.init()

        // STEP 2: Set up Combine bindings to mirror sub-manager state
        // This ensures our @Published properties stay in sync with sub-manager state
        setupStateBindings()

        // STEP 3: Check initial microphone permission
        permissionManager.checkMicrophonePermission()

        AppLogger.audio.info("AudioRecordingManager initialized with modular architecture")
    }

    /// Deinitialize and clean up resources
    deinit {
        // Cancel shortcut is managed by RecordingTranscriptionFlow
        // No cleanup needed here since shortcut handler is static
        if let observer = defaultsObserver {
            NotificationCenter.default.removeObserver(observer)
        }
        if let observer = keepWarmSettingsObserver {
            NotificationCenter.default.removeObserver(observer)
        }
        keepWarmManager.setEnabled(false)

        AppLogger.audio.info("AudioRecordingManager deinitialized")
    }

    // MARK: - Configuration

    /// Configure with external dependencies
    ///
    /// **What This Does:**
    /// Injects weak dependencies (transcriptionPipeline, settingsManager, etc.) into
    /// sub-managers that need them. Called after initialization by the app.
    ///
    /// **When to Call:**
    /// During app startup after all managers are created but before any recording.
    ///
    /// **Parameters:**
    /// - `transcriptionPipeline`: Manager for transcribing audio files
    /// - `settingsManager`: Manager for user preferences and configuration
    /// - `providerHealthManager`: Monitor for cloud provider availability
    /// - `appState`: Central app state for UI coordination
    /// - `licenseManager`: Manager for license validation and usage tracking
    func configure(
        transcriptionPipeline: TranscriptionPipeline?,
        settingsManager: SettingsManager?,
        providerHealthManager: CloudProviderHealthManager?,
        appState: AppState?,
        licenseManager: LicenseManager?
    ) {
        // Store weak references
        self.transcriptionPipeline = transcriptionPipeline
        self.settingsManager = settingsManager
        self.providerHealthManager = providerHealthManager
        self.appState = appState
        self.licenseManager = licenseManager

        // Pass dependencies to sub-managers that need them
        lifecycleManager.configure(settingsManager: settingsManager)
        recoveryManager.configure(settingsManager: settingsManager)
        autoPasteHandler.configure(settingsManager: settingsManager)

        recordingTranscriptionFlow.configure(
            transcriptionPipeline: transcriptionPipeline,
            settingsManager: settingsManager,
            appState: appState,
            licenseManager: licenseManager,
            providerHealthManager: providerHealthManager
        )

        // Clear saved preference if the selected microphone disappears (e.g., Bluetooth device removed).
        deviceManager.onSelectedDeviceInvalidated = { [weak self] lostDevice in
            AppLogger.audio.warning("Clearing persisted microphone preference for missing device: \(lostDevice.name, privacy: .public)")
            self?.settingsManager?.selectedMicrophoneId = ""
        }

        // Update available devices on launch
        updateAvailableDevices(reason: .initialBootstrap)

        // MICROPHONE RESTORATION: Restore the user's previously selected microphone if still available
        // This runs on app launch to ensure the same device is used across sessions
        // The savedId is persisted via @AppStorage in SettingsManager
        if let savedId = settingsManager?.selectedMicrophoneId, !savedId.isEmpty {
            if let savedDevice = availableDevices.first(where: { $0.id == savedId }) {
                AppLogger.audio.info("Restoring saved microphone selection on launch: \(savedDevice.name, privacy: .public)")
                selectDevice(savedDevice)
            } else {
                // Device no longer available (disconnected Bluetooth, unplugged USB, etc.)
                // Clear the preference so we don't keep trying to restore a missing device
                AppLogger.audio.warning("Saved microphone not found on launch, clearing preference: \(savedId, privacy: .public)")
                settingsManager?.selectedMicrophoneId = ""
            }
        }

        // Setup Push to Talk
        setupPushToTalkObserver()
        setupPushToTalk()
        setupKeepWarmObserver()
        syncKeepWarmConfiguration()

        AppLogger.audio.info("AudioRecordingManager configured with dependencies")
    }

    // MARK: - Push to Talk Configuration

    private func setupPushToTalkObserver() {
        // Remove existing observer if any
        if let observer = defaultsObserver {
            NotificationCenter.default.removeObserver(observer)
        }
        
        // Observe shortcut changes notification
        defaultsObserver = NotificationCenter.default.addObserver(
            forName: .shortcutDidChange,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.setupPushToTalk()
            }
        }
    }

    private func setupKeepWarmObserver() {
        if let observer = keepWarmSettingsObserver {
            NotificationCenter.default.removeObserver(observer)
        }

        keepWarmSettingsObserver = NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.syncKeepWarmConfiguration()
            }
        }
    }

    private func syncKeepWarmConfiguration() {
        keepWarmManager.updateActiveInputDevice(uid: activeInputDeviceIdentifier, name: activeInputDeviceName)
        keepWarmManager.setPermissionGranted(hasMicrophonePermission)
        keepWarmManager.setEnabled(settingsManager?.keepMicrophoneWarm ?? false)
    }

    /// DUPLICATE HANDLER PREVENTION:
    /// The KeyboardShortcuts library APPENDS handlers instead of replacing them.
    /// `setupPushToTalk()` re-runs on every `.shortcutDidChange`, so without this
    /// guard the custom-shortcut branch would register a fresh `onKeyDown`/`onKeyUp`
    /// pair each time the binding changes, stacking N callbacks per press and racing
    /// duplicate start/stop attempts. The handlers resolve the current binding and
    /// state lazily at dispatch time, so registering them once is sufficient.
    /// (Mirrors `hotkeysConfigured` in hyperwhisperApp and
    /// `cancelShortcutHandlerRegistered` in RecordingTranscriptionFlow.)
    private static var customPushToTalkHandlersConfigured = false

    /// Configure Push to Talk monitors based on current settings
    public func setupPushToTalk() {
        guard let settingsManager = settingsManager else { return }

        // Never tear down the monitor while a recording is in flight. `stop()`
        // invalidates the event tap and resets state to `.idle`, which orphans a
        // latched (double-tap locked) recording — the user would no longer be
        // able to stop it via the modifier. Defer the reconfigure until the
        // recording finishes; it is reapplied from the recording-end binding
        // once the monitor is safely idle.
        if isRecording {
            AppLogger.audio.info("Deferring Push to Talk reconfigure — recording in progress")
            needsPushToTalkReconfigure = true
            return
        }

        let pushToTalkMode = settingsManager.pushToTalkMode
        AppLogger.audio.debug("Configuring Push to Talk for mode: \(pushToTalkMode.rawValue)")

        // Stop existing monitoring (both bare modifier and ensure clean slate)
        BareModifierKeyMonitor.shared.stop()
        
        // Configure double press behavior
        // Combo modes (FN+Control, FN+Option) don't support double-press-to-lock —
        // a quick press-and-release should silently exit, not start recording.
        let isComboMode = pushToTalkMode == .fnControl || pushToTalkMode == .fnOption
        BareModifierKeyMonitor.shared.doublePressEnabled = isComboMode ? false : settingsManager.pushToTalkDoublePressEnabled

        if pushToTalkMode == .disabled {
            AppLogger.audio.debug("📴 Push to Talk is disabled")
        } else if [.fn, .control, .leftOption, .rightOption, .fnControl, .fnOption].contains(pushToTalkMode) {
            // BARE MODIFIER MODE (single key or combo)

            if AccessibilityHelper.shared.hasAccessibilityPermission() {
                BareModifierKeyMonitor.shared.onModifierDown = { [weak self] in
                    Task { @MainActor in
                        guard let self = self else { return }
                        self.transcriptionPipeline?.prewarmCloudConnectionIfActive()
                        if self.appState?.isToggleRecordingShortcutHeld == true {
                            AppLogger.ui.debug("🚫 Bare modifier pressed but toggle shortcut is held - ignoring")
                            return
                        }

                        // CRITICAL GUARD: Prevent Push to Talk from interfering with existing recordings
                        // If recording is already active (started by toggle shortcut or other means),
                        // reset the PTT monitor to prevent its timers from interfering.
                        if self.isRecording {
                            AppLogger.ui.debug("🚫 Bare modifier pressed but already recording - resetting monitor")
                            BareModifierKeyMonitor.shared.resetToIdle()
                            return
                        }

                        // SENTRY BREADCRUMB: Track PTT start callback
                        SentryService.addBreadcrumb(
                            message: "PTT onModifierDown callback - starting recording",
                            category: "ptt.callback",
                            data: [
                                "mode": pushToTalkMode.rawValue,
                                "isRecording": self.isRecording
                            ]
                        )

                        AppLogger.ui.debug("⌨️ Bare modifier \(pushToTalkMode.rawValue) start signal received - starting recording")
                        self.startPushToTalkRecording()
                    }
                }

                BareModifierKeyMonitor.shared.onModifierUp = { [weak self] in
                    Task { @MainActor in
                        guard let self = self else { return }
                        if self.appState?.isToggleRecordingShortcutHeld == true {
                            AppLogger.ui.debug("🚫 Bare modifier stop signal while toggle shortcut held - ignoring")
                            return
                        }
                        // Only stop if we're actually recording
                        guard self.isRecording else {
                            AppLogger.ui.debug("🚫 Bare modifier stop signal but not recording - ignoring")
                            return
                        }

                        // SENTRY BREADCRUMB: Track PTT stop callback
                        SentryService.addBreadcrumb(
                            message: "PTT onModifierUp callback - stopping recording",
                            category: "ptt.callback",
                            data: [
                                "mode": pushToTalkMode.rawValue,
                                "isRecording": self.isRecording,
                                "recordingDuration": self.recordingDuration
                            ]
                        )

                        AppLogger.ui.debug("⌨️ Bare modifier \(pushToTalkMode.rawValue) stop signal received")
                        self.stopPushToTalkRecordingWithTranscription()
                    }
                }

                BareModifierKeyMonitor.shared.onInterferenceDetected = { [weak self] in
                    Task { @MainActor in
                        guard let self = self else { return }
                        if self.appState?.isToggleRecordingShortcutHeld == true {
                            AppLogger.ui.debug("🚫 Bare modifier interference ignored - toggle shortcut active")
                            return
                        }
                        AppLogger.ui.debug("⚠️ Other key pressed while modifier held - cancelling recording")
                        if self.isRecording {
                            self.stopPushToTalkRecordingWithoutTranscription()
                        }
                    }
                }

                Task { @MainActor in
                    switch pushToTalkMode {
                    case .fnControl: BareModifierKeyMonitor.shared.start(combo: [.fn, .control])
                    case .fnOption: BareModifierKeyMonitor.shared.start(combo: [.fn, .leftOption])
                    case .fn: BareModifierKeyMonitor.shared.start(mode: .fn)
                    case .control: BareModifierKeyMonitor.shared.start(mode: .control)
                    case .leftOption: BareModifierKeyMonitor.shared.start(mode: .leftOption)
                    case .rightOption: BareModifierKeyMonitor.shared.start(mode: .rightOption)
                    default: return
                    }
                    AppLogger.ui.debug("✅ Bare modifier monitor started for \(pushToTalkMode.rawValue)")
                }
            } else {
                AppLogger.ui.warning("🚫 Bare modifier mode selected but Accessibility permission not granted")
            }
        } else if pushToTalkMode == .custom {
            // CUSTOM SHORTCUT MODE
            AppLogger.ui.debug("⌨️ Push to Talk set to custom shortcut mode")

            // Register the custom-shortcut handlers only once. The KeyboardShortcuts
            // library appends (does not replace) handlers, and `setupPushToTalk()`
            // re-runs on every `.shortcutDidChange`; re-registering here would stack
            // duplicate start/stop callbacks. The closures look up the current
            // `.pushToTalk` binding at dispatch time, so a single registration
            // continues to work after the user edits the shortcut.
            guard !Self.customPushToTalkHandlersConfigured else {
                AppLogger.ui.debug("🔧 Custom Push to Talk handlers already configured, skipping duplicate setup")
                return
            }
            Self.customPushToTalkHandlersConfigured = true

            KeyboardShortcuts.onKeyDown(for: .pushToTalk) { [weak self] in
                Task { @MainActor in
                    guard let self = self else { return }
                    self.transcriptionPipeline?.prewarmCloudConnectionIfActive()
                    if self.appState?.isToggleRecordingShortcutHeld == true {
                        AppLogger.ui.debug("🚫 Push to Talk pressed while toggle shortcut held - ignoring")
                        return
                    }
                    if self.isRecording {
                        AppLogger.ui.debug("🚫 Push to Talk pressed but already recording - ignoring")
                        return
                    }

                    AppLogger.ui.debug("⌨️ Push to Talk (custom) pressed - starting recording")
                    self.startPushToTalkRecording()
                }
            }

            KeyboardShortcuts.onKeyUp(for: .pushToTalk) { [weak self] in
                Task { @MainActor in
                    guard let self = self else { return }
                    
                    if !self.isRecording {
                        AppLogger.ui.debug("🚫 Push to Talk released but not recording - ignoring")
                        return
                    }

                    AppLogger.ui.debug("⌨️ Push to Talk (custom) released")

                    // Minimum duration check is now handled universally in RecordingTranscriptionFlow
                    // (1.0 second minimum). Just stop and let the coordinator handle short recordings.
                    AppLogger.ui.info("⌨️ Push to Talk (custom) released - stopping recording (\(String(format: "%.2f", self.recordingDuration))s)")
                    self.stopPushToTalkRecordingWithTranscription()
                }
            }
        }
    }

    // MARK: - State Binding Setup

    /// Set up Combine bindings to mirror sub-manager state to published properties
    ///
    /// **What This Does:**
    /// Creates subscriptions that automatically sync sub-manager @Published properties
    /// to this orchestrator's @Published properties. This ensures:
    /// 1. Views can bind to AudioRecordingManager properties (stable API)
    /// 2. Updates from sub-managers propagate to UI automatically
    /// 3. No manual state synchronization needed
    ///
    /// **Why This Pattern:**
    /// Allows sub-managers to be independent while maintaining a single source of truth
    /// for UI binding. Views don't need to know about sub-manager existence.
    private func setupStateBindings() {
        // Mirror recording lifecycle state
        lifecycleManager.$isRecording
            .assign(to: &$isRecording)

        // RECORDING END CLEANUP:
        // When recording ends (for any reason), reset the PTT monitor to idle.
        // This ensures the monitor doesn't have stale state that could interfere
        // with the next recording session.
        lifecycleManager.$isRecording
            .sink { [weak self] isRecording in
                guard !isRecording else { return }
                BareModifierKeyMonitor.shared.resetToIdle()

                // Apply any Push to Talk reconfiguration that was deferred because
                // a recording was in flight. The monitor is now idle, so rebuilding
                // it is safe.
                if self?.needsPushToTalkReconfigure == true {
                    self?.needsPushToTalkReconfigure = false
                    self?.setupPushToTalk()
                }
            }
            .store(in: &cancellables)

        lifecycleManager.$isRecording
            .sink { [weak self] isRecording in
                guard let self else { return }
                if isRecording {
                    // Never let the onboarding metering preview contend with a
                    // real recording — tear it down the moment one begins.
                    self.stopInputLevelPreview()
                    self.keepWarmManager.suspendForActiveRecording()
                } else {
                    self.keepWarmManager.resumeAfterRecording()
                }
            }
            .store(in: &cancellables)

        // PERFORMANCE FIX: Bind high-frequency metrics to isolated liveMetrics object
        // instead of AudioRecordingManager's @Published properties.
        // This prevents MainAppView and HistoryView from re-evaluating at 30 FPS during recording.
        lifecycleManager.$audioLevel
            .assign(to: \.audioLevel, on: liveMetrics)
            .store(in: &cancellables)

        lifecycleManager.$recordingDuration
            .assign(to: \.recordingDuration, on: liveMetrics)
            .store(in: &cancellables)

        // Mirror last recording URL
        lifecycleManager.$lastRecordingURL
            .assign(to: &$lastRecordingURL)

        // Mirror permission state
        permissionManager.$hasMicrophonePermission
            .assign(to: &$hasMicrophonePermission)

        permissionManager.$showPermissionDeniedAlert
            .assign(to: &$showPermissionDeniedAlert)

        permissionManager.$errorMessage
            .map { $0.isEmpty ? nil : $0 }
            .assign(to: &$errorMessage)

        // Mirror device manager state
        deviceManager.$availableDevices
            .assign(to: &$availableDevices)

        deviceManager.$selectedDevice
            .assign(to: &$selectedDevice)

        deviceManager.$inputVolumeScalar
            .assign(to: &$inputVolumeScalar)

        deviceManager.$activeInputDeviceName
            .assign(to: &$activeInputDeviceName)

        deviceManager.$activeInputDeviceIdentifier
            .assign(to: &$activeInputDeviceIdentifier)

        deviceManager.$systemDefaultDeviceUID
            .assign(to: &$systemDefaultDeviceUID)

        // Sync lifecycle manager's permission state from permission manager
        permissionManager.$hasMicrophonePermission
            .sink { [weak self] hasPermission in
                self?.lifecycleManager.hasMicrophonePermission = hasPermission
                self?.keepWarmManager.setPermissionGranted(hasPermission)
            }
            .store(in: &cancellables)

        deviceManager.$activeInputDeviceIdentifier
            .combineLatest(deviceManager.$activeInputDeviceName)
            .sink { [weak self] identifier, name in
                self?.keepWarmManager.updateActiveInputDevice(uid: identifier, name: name)
            }
            .store(in: &cancellables)
    }

    // MARK: - Public API: Recording Control

    /// Toggle recording with automatic transcription
    ///
    /// **What This Does:**
    /// Starts or stops recording based on current state, then transcribes the result.
    /// Delegates to RecordingTranscriptionFlow which handles the complete flow.
    ///
    /// **Recording Flow:**
    /// 1. Start: Preflight checks → capture context → start recording → show dialog
    /// 2. Stop: Stop recording → wait for file → transcribe → auto-paste → close dialog
    ///
    /// **Race Condition Protection:**
    /// Uses Task cancellation to prevent multiple simultaneous recordings.
    /// See RecordingTranscriptionFlow for implementation details.
    ///
    /// **Parameters:**
    /// - `mode`: Optional transcription mode ID to use (nil = use selected mode)
    /// - `stopOnly`: If true, only stops recording (doesn't start a new one)
    /// - `trigger`: Source of the user/system action that initiated the toggle
    ///
    /// **When to Call:**
    /// From keyboard shortcut handlers or UI buttons
    func toggleRecordingWithTranscription(
        mode: String? = nil,
        stopOnly: Bool = false,
        trigger: RecordingTriggerSource = .unknown
    ) {
        recordingTranscriptionFlow.toggleRecordingWithTranscription(
            mode: mode,
            stopOnly: stopOnly,
            trigger: trigger
        )
    }

    /// Entry point for the global toggle shortcut.
    /// Simply toggles recording on/off - works regardless of how recording was started.
    func toggleRecordingFromShortcut(trigger: RecordingTriggerSource = .shortcut) {
        toggleRecordingWithTranscription(trigger: trigger)
    }

    /// Entry point for the Quick Capture shortcut.
    ///
    /// Same toggle semantics as `toggleRecordingWithTranscription`, but the
    /// session is tagged so the final transcription is sent to Apple Notes
    /// instead of being pasted into the focused app. The mode override (if
    /// any) replaces the active mode for this session only.
    ///
    /// - Parameter modeOverride: A specific Mode pinned in Quick Capture
    ///   settings, or nil to use the mode that is active when the shortcut
    ///   fires ("Current mode").
    func toggleQuickCapture(modeOverride: Mode?) {
        // If a stop is already in progress, `toggleRecordingWithTranscription`
        // will ignore this call. Skip setting context too, otherwise it leaks
        // onto the next normal recording and silently routes it to Notes.
        if recordingTranscriptionFlow.isStopInProgress {
            AppLogger.audio.info("Quick Capture ignored — stop already in progress")
            return
        }
        let context = QuickCaptureContext(
            modeId: modeOverride?.id?.uuidString,
            modeName: modeOverride?.name
        )
        recordingTranscriptionFlow.quickCaptureContext = context
        recordingTranscriptionFlow.toggleRecordingWithTranscription(
            mode: modeOverride?.name,
            trigger: .quickCapture
        )
    }

    /// Stop recording and return the recorded audio file
    ///
    /// **What This Does:**
    /// Stops the current recording session and returns the file URL and duration.
    /// This is a direct pass-through to RecordingLifecycle.stopRecording().
    ///
    /// **Recording Stop Flow:**
    /// 1. Removes audio tap to stop capturing buffers
    /// 2. Stops and resets audio engine
    /// 3. Closes raw audio file to flush buffers
    /// 4. Converts CAF to M4A AAC
    /// 5. Updates RecordingSession with final details
    /// 6. Restores previous system default input device
    /// 7. Returns final M4A URL and duration
    ///
    /// **Returns:**
    /// Optional tuple of (url: final M4A URL, duration: recording length in seconds)
    /// Returns nil if not currently recording
    ///
    /// **When to Call:**
    /// - From UI "Stop" button (without transcription)
    /// - When manually stopping without triggering transcription flow
    /// - For testing or custom recording workflows
    ///
    /// **Note:**
    /// For normal recording with transcription, use toggleRecordingWithTranscription() instead.
    /// This method is for cases where you need the recording file without transcription.
    ///
    /// **Returns:**
    /// Optional tuple containing:
    /// - `url`: Final audio file URL (M4A or WAV)
    /// - `duration`: Recording length in seconds
    /// - `conversionWarning`: Optional warning message if compression failed
    func stopRecording() async -> (url: URL?, duration: TimeInterval, conversionWarning: String?)? {
        guard let result = await lifecycleManager.stopRecording() else {
            return nil
        }

        return (
            url: result.url,
            duration: result.duration,
            conversionWarning: result.conversionWarning
        )
    }

    /// Handle cancel shortcut to abort current recording
    ///
    /// **What This Does:**
    /// Cancels the current recording/transcription without saving or transcribing.
    /// This is a direct pass-through to RecordingTranscriptionFlow.handleCancelShortcut().
    ///
    /// **Cancel Flow:**
    /// 1. Cancels any in-progress toggle task
    /// 2. Stops recording if active
    /// 3. Discards audio file
    /// 4. Cleans up UI state
    /// 5. Does NOT trigger transcription
    ///
    /// **When to Call:**
    /// - When user presses cancel keyboard shortcut (Escape)
    /// - When user clicks "Cancel" button in recording dialog
    /// - When aborting a recording due to error
    ///
    /// **UI Behavior:**
    /// Recording dialog closes immediately without transcribing.
    func handleCancelShortcut() {
        recordingTranscriptionFlow.handleCancelShortcut()
    }

    /// Retry transcription for a previously recorded file that failed before transcription
    func retryTranscriptionFromPendingFile() {
        recordingTranscriptionFlow.retryPendingFile()
    }

    // MARK: - Public API: Push to Talk

    /// Start recording for Push to Talk feature
    ///
    /// **What This Does:**
    /// Initiates audio recording when Push to Talk key is pressed.
    /// Uses the current selected mode for transcription.
    /// This is similar to toggleRecordingWithTranscription but only starts recording.
    ///
    /// **When to Call:**
    /// - When Push to Talk key is pressed (onKeyDown)
    /// - RecordingDialog will appear automatically showing waveform visualization
    ///
    /// **Note:**
    /// The actual recording file is created immediately and the recording dialog appears.
    /// When the key is released, call either stopPushToTalkRecordingWithTranscription() or
    /// stopPushToTalkRecordingWithoutTranscription() depending on duration validation.
    func startPushToTalkRecording() {
        toggleRecordingWithTranscription(trigger: .pushToTalk)
    }

    /// Stop recording and transcribe (used when recording meets minimum duration)
    ///
    /// **What This Does:**
    /// Stops recording and initiates transcription of the audio.
    ///
    /// **Push to Talk Flow:**
    /// 1. Stop recording
    /// 2. Create processing transcript
    /// 3. Send to transcription provider
    /// 4. Display results in recording dialog
    /// 5. Auto-paste if enabled
    ///
    /// **When to Call:**
    /// - When Push to Talk key is released AND recording is active
    func stopPushToTalkRecordingWithTranscription() {
        // Stop recording and transcribe using the standard toggle flow
        toggleRecordingWithTranscription(trigger: .pushToTalk)
    }

    /// Stop recording without transcribing (used when recording is too short or cancelled)
    ///
    /// **What This Does:**
    /// Cancels the recording without transcribing it.
    ///
    /// **Push to Talk Flow:**
    /// 1. Stop recording
    /// 2. Discard audio file without saving
    /// 3. Close recording dialog
    ///
    /// **When to Call:**
    /// - When recording should be discarded (too short, interference detected, etc.)
    func stopPushToTalkRecordingWithoutTranscription() {
        handleCancelShortcut()
    }

    // MARK: - Public API: Device Management

    /// Update the list of available audio input devices
    ///
    /// **What This Does:**
    /// Queries CoreAudio for all input devices and updates availableDevices.
    /// Automatically called on app launch and when devices change.
    ///
    /// **When to Call:**
    /// - During app initialization
    /// - When user plugs/unplugs audio devices
    /// - When refreshing device list in settings
    func updateAvailableDevices(reason: AudioDeviceManager.DeviceScanOrigin = .manual) {
        deviceManager.updateAvailableDevices(reason: reason)
    }

    /// Select a specific audio input device for recording
    ///
    /// **What This Does:**
    /// Sets the selected device which will be used for next recording.
    /// Updates volume metrics for the newly selected device.
    ///
    /// **Parameters:**
    /// - `device`: The device to select (nil restores system default)
    ///
    /// **When to Call:**
    /// When user selects a device from the picker in settings
    func selectDevice(_ device: AudioDevice?) {
        deviceManager.selectDevice(device)
    }

    /// Refresh input volume metrics for the current device
    ///
    /// **What This Does:**
    /// Reads the current system volume level for the active input device.
    /// Updates inputVolumeScalar which is used for UI warnings.
    ///
    /// **When to Call:**
    /// - After selecting a new device
    /// - Periodically while recording dialog is open
    /// - When user adjusts system volume
    func refreshInputVolumeMetrics() {
        deviceManager.updateInputVolumeMetrics()
    }

    // MARK: - Public API: Onboarding Input-Level Preview

    /// Start a lightweight metering session so the onboarding microphone step can
    /// show a live input level **without** starting a real recording.
    ///
    /// Backed by an `AVAudioRecorder` writing to `/dev/null` with metering
    /// enabled, sampled at ~30 FPS and normalized (-60…0 dB → 0…1) exactly like
    /// `SimpleRecorder.updateMeter()`. The recorder captures whatever the system
    /// default input is at start time; `selectDevice(_:)` switches that default,
    /// so call `startInputLevelPreview()` again after changing the device to
    /// re-point it.
    ///
    /// No-ops (and releases any prior session) if microphone permission is
    /// missing or a real recording is active — the preview must never contend
    /// with the recording capture path or leak the mic.
    func startInputLevelPreview() {
        // Restart cleanly if already running (e.g. the device changed).
        stopInputLevelPreview()

        guard hasMicrophonePermission else {
            AppLogger.audio.info("Input-level preview skipped — microphone permission not granted")
            return
        }
        guard !isRecording else {
            AppLogger.audio.info("Input-level preview skipped — a recording is in progress")
            return
        }

        // Ensure the chosen device is the active system default before we open
        // the metering recorder, so the preview reflects the picked device.
        deviceManager.applySelectedInputDeviceIfNeeded()

        let settings: [String: Any] = [
            AVFormatIDKey: Int(kAudioFormatLinearPCM),
            AVSampleRateKey: 16000.0,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false
        ]

        do {
            let recorder = try AVAudioRecorder(url: URL(fileURLWithPath: "/dev/null"), settings: settings)
            recorder.isMeteringEnabled = true
            guard recorder.record() else {
                AppLogger.audio.warning("Input-level preview recorder failed to start")
                return
            }
            inputLevelPreviewRecorder = recorder
            inputLevelPreviewTask = Task { [weak self] in
                while !Task.isCancelled {
                    self?.sampleInputLevelPreview()
                    try? await Task.sleep(nanoseconds: 33_000_000) // ~30 FPS
                }
            }
            AppLogger.audio.info("Input-level preview started")
        } catch {
            AppLogger.audio.error("Failed to start input-level preview: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Stop the onboarding input-level preview and release the microphone.
    /// Safe to call repeatedly; also invoked when onboarding is dismissed and
    /// whenever a real recording starts.
    func stopInputLevelPreview() {
        inputLevelPreviewTask?.cancel()
        inputLevelPreviewTask = nil
        inputLevelPreviewRecorder?.stop()
        inputLevelPreviewRecorder = nil
        idleInputLevel = 0
    }

    /// Sample the preview recorder's average power and publish a normalized level.
    /// Mirrors `SimpleRecorder.updateMeter()` (-60 dB silence floor, 0 dB ceiling).
    private func sampleInputLevelPreview() {
        guard let recorder = inputLevelPreviewRecorder else { return }
        recorder.updateMeters()
        let power = recorder.averagePower(forChannel: 0)
        let minDb: Float = -60
        let maxDb: Float = 0
        let normalized: Float
        if power <= minDb {
            normalized = 0
        } else if power >= maxDb {
            normalized = 1
        } else {
            normalized = (power - minDb) / (maxDb - minDb)
        }
        idleInputLevel = normalized
    }

    // MARK: - Public API: Crash Recovery

    /// Recover incomplete recordings from previous app crashes
    ///
    /// **What This Does:**
    /// Scans Core Data for RecordingSessions with no endTime (crashed during recording).
    /// For each orphaned session:
    /// 1. Validates the CAF file is readable
    /// 2. Converts to M4A
    /// 3. Updates session status to "processing"
    /// 4. Triggers transcription
    ///
    /// **When to Call:**
    /// During app initialization (once per launch)
    ///
    /// **Implementation:**
    /// Delegates to CrashRecoveryManager which handles the recovery flow.
    func recoverOrphanedRecordings() async {
        await recoveryManager.recoverOrphanedRecordings(
            currentSessionID: sessionManager.currentRecordingSession?.id
        )
    }

    // MARK: - Public API: Permission Management

    /// Request microphone permission from the user
    ///
    /// **What This Does:**
    /// Shows the system permission dialog if not yet determined.
    /// Updates hasMicrophonePermission based on user response.
    ///
    /// **Returns:**
    /// true if permission granted, false otherwise
    ///
    /// **When to Call:**
    /// - Before starting first recording
    /// - When user clicks "Grant Permission" in settings
    @discardableResult
    func requestMicrophonePermission() async -> Bool {
        return await permissionManager.requestMicrophonePermission()
    }

    /// Check current microphone permission status without requesting
    ///
    /// **What This Does:**
    /// Queries the authorization status and updates hasMicrophonePermission.
    /// Does NOT show any dialogs.
    ///
    /// **When to Call:**
    /// - During initialization
    /// - When app becomes active (to detect permission changes)
    func checkMicrophonePermission() {
        permissionManager.checkMicrophonePermission()
    }
}
