//
//  RecordingTranscriptionFlow.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation

// MARK: - Recording Trigger Source

/// Describes what initiated a recording start/stop.
enum RecordingTriggerSource: String {
    case shortcut = "shortcut"
    case streamingShortcut = "streaming_shortcut"
    case uiButton = "ui_button"
    case menu = "menu"
    case onboarding = "onboarding"
    case pushToTalk = "push_to_talk"
    case autoStop = "auto_stop"
    case cancelShortcut = "cancel_shortcut"
    case quickCapture = "quick_capture"
    case unknown = "unknown"
}

// MARK: - Quick Capture Context

/// Per-session context for a Quick Capture recording.
///
/// The destination is implicit (Apple Notes) for v1 — adding a second
/// destination is a `case` on this struct, not a refactor of the flow.
///
/// Mode resolution: `modeId == nil` means "use the mode that is active at
/// finish time" (the synthetic "Current mode" option in settings). A concrete
/// id pins a specific Mode regardless of what `AppState.selectedModeId` says.
struct QuickCaptureContext {
    /// Resolved Mode UUID string, or nil to defer to the current active mode.
    let modeId: String?
    /// Resolved Mode name (mirrors `modeId`). Used for the recording dialog
    /// title and as the fallback transcription mode name.
    let modeName: String?
}

/// Coordinates recording with transcription flow
///
/// **Purpose:**
/// Orchestrates the complete workflow from recording start to transcription completion:
/// - Toggle recording logic with race condition protection
/// - Recording start preflight checks (permissions, API keys, licenses, storage)
/// - Application context capture for AI post-processing
/// - Transcription orchestration with error handling
/// - Smart paste with file tag processing
/// - Power activity management
/// - User messaging and error alerts
///
/// **Race Condition Protection:**
/// Uses Task cancellation to prevent multiple simultaneous recordings:
/// ```
/// User presses shortcut → Start recording
///         ↓
/// User quickly presses again → Cancel previous task
///         ↓
/// New task starts → Only one recording active
/// ```
///
/// **Preflight Checks (handleStartRecording):**
/// 1. Check transcription manager is ready (not busy)
/// 2. Verify license allows recording (trial daily limit)
/// 3. Ensure recordings folder is accessible
/// 4. Validate API keys for selected mode
/// 5. Check cloud provider health
/// 6. Request microphone permission
/// 7. Capture frontmost app context (for auto-paste)
/// 8. Begin power activity (prevent App Nap)
///
/// **Transcription Flow (handleStopRecordingWithTranscription):**
/// 1. Stop recording and get audio file
/// 2. Wait for file to be ready (readiness probe)
/// 3. If cancelled: clean up and return
/// 4. Create processing transcript in Core Data
/// 5. Update state to "transcribing"
/// 6. Perform transcription via TranscriptionPipeline
/// 7. Record usage for trial users (daily limit)
/// 8. Update transcript with results
/// 9. Handle auto-paste or keep dialog open
/// 11. End power activity
///
/// **Error Handling:**
/// - Network outage: Silent failure (no alert)
/// - Streaming interrupted: Keep partial text
/// - Generic errors: Show alert with message
/// - License limit exceeded: Show upgrade prompt
/// - Missing API keys: Show key prompt
/// - Provider health failures: Block with message
/// - Microphone in use: Friendly error message
///
/// **Thread Safety:**
/// All methods run on main actor for UI consistency.
@MainActor
class RecordingTranscriptionFlow {

    // MARK: - Static State

    /// Whether cancel shortcut handler has been registered (app lifetime)
    /// We register the handler only once to prevent accumulation
    static var cancelShortcutHandlerRegistered = false

    // MARK: - Dependencies

    weak var transcriptionPipeline: TranscriptionPipeline?
    weak var settingsManager: SettingsManager?
    weak var appState: AppState?
    weak var licenseManager: LicenseManager?
    weak var providerHealthManager: CloudProviderHealthManager?

    let recordingLifecycle: RecordingLifecycle
    let autoPasteHandler: AutoPasteHandler
    let powerActivityManager: PowerActivityManager
    let permissionManager: MicrophonePermissionManager
    let vadProcessingService = VADProcessingService()

    // MARK: - Streaming Transcription

    /// Real-time streaming transcription service (remote WebSocket or
    /// local on-device Parakeet). When active, text is typed directly as
    /// the user speaks instead of waiting for recording to finish.
    var streamingService: (any StreamingClientProtocol)?

    /// Whether streaming transcription is currently active
    /// Used to coordinate the stop flow between streaming and batch modes
    var isStreamingActive = false

    /// Accumulated transcript text from streaming (for final processing)
    /// Collects all final transcript segments for history and cleanup
    var streamingAccumulatedText = ""

    /// Latest text shown in the streaming preview surface.
    /// Unlike `streamingAccumulatedText`, this can include the current interim
    /// provider hypothesis so stop can commit the same tail the user saw.
    var streamingPreviewTextSnapshot = ""

    /// When the current streaming session started (for duration tracking)
    var streamingStartTime: Date?

    /// Safety timer that auto-stops streaming after the max recording duration.
    var streamingMaxDurationTimer: Timer?

    /// Safety timer that auto-stops batch recordings after the max recording duration.
    var recordingMaxDurationTimer: Timer?

    /// How streaming transcript deltas are delivered to the focused app.
    /// - `directInsert`: type/paste each final chunk into the focused app as it arrives.
    /// - `previewOnly`: accumulate text in the floating preview bubble, paste once
    ///   at session end. Used for apps where live HID typing is unreliable (terminals).
    enum StreamingDeliveryMode: String {
        case directInsert
        case previewOnly
    }

    /// Delivery mode captured at streaming session start. Fixed for the life of the
    /// session so mid-session focus changes don't split text between modes.
    var streamingDeliveryMode: StreamingDeliveryMode = .directInsert

    /// Bundle identifier of the target app when the streaming session started.
    /// Used for logging and for the final paste to detect target drift.
    var streamingTargetBundleId: String?

    /// Pending AppStorage write for `streamingLocalNemotronVariant`. Captured
    /// when the Nemotron variant fallback resolves a different variant, then
    /// committed only AFTER `startSession` succeeds — so a startup failure
    /// keeps the user's pinned preference intact.
    var pendingNemotronVariantPreferenceUpdate: String?

    // MARK: - State Properties

    /// Task for current toggle operation (cancellable for race condition fix)
    var toggleTask: Task<Void, Never>?

    /// Correlation ID for the current recording attempt (start → stop/failure).
    var currentRecordingAttemptId: String?

    /// Source that initiated the current recording attempt.
    var currentRecordingTriggerSource: RecordingTriggerSource = .unknown

    /// PID of app that was frontmost before recording (for auto-paste)
    var previousFrontmostPID: pid_t?

    /// Bundle ID of app that was frontmost (for smart file paths)
    var previousFrontmostBundleID: String?

    /// Application context captured before recording (for AI formatting)
    var capturedApplicationContext: ApplicationContext?

    /// Whether a stop/finalization flow is currently in progress.
    /// When true, new toggle requests are ignored to prevent cancelling
    /// the stop flow mid-flight (which causes CancellationError + "Audio file missing").
    var isStopInProgress = false

    /// Active Quick Capture session metadata. Set immediately before
    /// `toggleRecordingWithTranscription` is invoked by the Quick Capture
    /// shortcut handler. Non-nil for the duration of the recording → routes
    /// the final transcription to Notes instead of pasting into the focused app.
    /// Cleared after the stop/cancel flow finishes.
    var quickCaptureContext: QuickCaptureContext?

    // MARK: - Initialization

    init(
        recordingLifecycle: RecordingLifecycle,
        autoPasteHandler: AutoPasteHandler,
        powerActivityManager: PowerActivityManager,
        permissionManager: MicrophonePermissionManager
    ) {
        self.recordingLifecycle = recordingLifecycle
        self.autoPasteHandler = autoPasteHandler
        self.powerActivityManager = powerActivityManager
        self.permissionManager = permissionManager
    }

    /// Configure with dependencies after initialization
    func configure(
        transcriptionPipeline: TranscriptionPipeline?,
        settingsManager: SettingsManager?,
        appState: AppState?,
        licenseManager: LicenseManager?,
        providerHealthManager: CloudProviderHealthManager?
    ) {
        self.transcriptionPipeline = transcriptionPipeline
        self.settingsManager = settingsManager
        self.appState = appState
        self.licenseManager = licenseManager
        self.providerHealthManager = providerHealthManager
    }

    var activeSessionModeName: String {
        appState?.currentSessionModeName ?? "Default"
    }

    var activeSessionModeId: String {
        appState?.currentSessionModeId ?? ""
    }

    func setActiveSessionMode(id: String, name: String) {
        appState?.beginActiveSessionMode(id: id, name: name)
    }

    func clearActiveSessionMode() {
        appState?.clearActiveSessionMode()
    }
}
