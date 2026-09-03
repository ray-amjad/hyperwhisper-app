//
//  AppState.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  CENTRAL APPLICATION STATE MANAGER
//  This class serves as the single source of truth for the app's UI state.
//  It uses the ObservableObject protocol to notify SwiftUI views of changes.
//  
//  Design Pattern: This follows the MVVM (Model-View-ViewModel) pattern where
//  this class acts as a ViewModel, managing state between the Model (data) and View (UI).

import SwiftUI
import Combine  // For advanced reactive programming features
import CoreData
import AppKit  // For app activation checks
import os  // For structured logging with Logger

// MARK: - Navigation Item Enum

/// Represents the different sections in the sidebar navigation
/// Using an enum ensures type safety - we can't accidentally navigate to a non-existent section
enum NavigationItem: String, CaseIterable, Identifiable {
    case home = "Home"
    case modes = "Modes"
    case vocabulary = "Vocabulary"
    case modelLibrary = "Model Library"
    case streaming = "Streaming"
    case history = "History"
    case settings = "Settings"

    /// Identifiable conformance allows this to be used in SwiftUI ForEach
    var id: String { self.rawValue }

    /// SF Symbol icon for each navigation item
    /// These are Apple's built-in scalable icons that look great at any size
    var icon: String {
        switch self {
        case .home:
            return "house.fill"  // Filled house icon for home
        case .modes:
            return "square.stack.3d.up.fill"  // Stack icon for multiple modes
        case .vocabulary:
            return "character.book.closed.fill"  // Book icon for vocabulary
        case .modelLibrary:
            return "books.vertical.fill"
        case .streaming:
            return "waveform.badge.mic"  // Waveform icon for real-time streaming
        case .history:
            return "clock.fill"  // Clock icon for history
        case .settings:
            return "gearshape.fill"  // Gear icon for settings
        }
    }

    /// Localized title for display in the UI
    var localizedTitle: String {
        switch self {
        case .home: return "sidebar.home".localized
        case .modes: return "sidebar.modes".localized
        case .vocabulary: return "sidebar.vocabulary".localized
        case .modelLibrary: return "sidebar.modelLibrary".localized
        case .streaming: return "sidebar.streaming".localized
        case .history: return "sidebar.history".localized
        case .settings: return "sidebar.settings".localized
        }
    }

    /// Help text shown when hovering over the navigation item
    var helpText: String {
        switch self {
        case .home:
            return "sidebar.home.help".localized
        case .modes:
            return "sidebar.modes.help".localized
        case .vocabulary:
            return "sidebar.vocabulary.help".localized
        case .modelLibrary:
            return "sidebar.modelLibrary.help".localized
        case .streaming:
            return "sidebar.streaming.help".localized
        case .history:
            return "sidebar.history.help".localized
        case .settings:
            return "sidebar.settings.help".localized
        }
    }
}

// MARK: - Recording State Enum

/// Represents the different states of the recording process
/// This helps us manage the UI and logic flow during recording
enum RecordingState: Equatable {
    case idle  // Not recording
    case recording  // Currently recording audio
    case processing  // Processing the recorded audio
    case transcribing  // Converting audio to text
    case postProcessing  // AI post-processing the transcribed text
    case complete(String)  // Transcription complete with result
    case error(String)  // An error occurred
    
    /// Human-readable description of the current state
    var description: String {
        switch self {
        case .idle:
            return "recording.state.ready".localized
        case .recording:
            return "recording.state.recording".localized
        case .processing:
            return "recording.state.processing".localized
        case .transcribing:
            return "recording.state.transcribing".localized
        case .postProcessing:
            return "recording.state.postprocessing".localized
        case .complete(let text):
            return "recording.state.complete".localized(arguments: text)
        case .error(let message):
            return "recording.state.error".localized(arguments: message)
        }
    }
    
}

// MARK: - Streaming Connection State Enum

/// Represents the lifecycle states of a streaming transcription WebSocket connection
/// This enum helps provide visual feedback to users during the connection setup phase
/// and prevents lost audio by showing when the system is ready to capture speech
enum StreamingConnectionState: Equatable {
    case idle                    // Not streaming
    case warmingUp               // Local model/session warmup before capture starts
    case connecting              // WebSocket connecting, waiting for "ready"
    case ready                   // "ready" received, audio engine starting
    case streaming               // Audio actively streaming to server
    case reconnecting            // WebSocket dropped unexpectedly, attempting auto-reconnect (amber indicator)
    case disconnecting           // Graceful shutdown
    case error(String)           // Connection/streaming error
}

// MARK: - Mode Preparation Keys

// Issue #318. Model preparation used to be keyed on `selectedModeId`, which is
// an *identity*, not a *value*. Onboarding's source commit
// (`LiveOnboardingSourceCommitter.apply`) reconfigures the existing Default Mode
// **in place** and re-selects the same UUID, so an id-keyed `removeDuplicates()`
// swallowed the emission and the model never re-prepared. The two keys below
// replace that id with the Mode content each half of preparation reads.
//
// They are TWO keys, not one, because the two halves have disjoint inputs:
//
//   - `TranscriptionModelManager.prepareModel` reads the Mode's `model` and
//     `extractLanguage(from:)`. It cancels in-flight transcription work and
//     drives `modelReadyState` (the status bar's left indicator).
//   - `TranscriptionModelManager.prepareLocalRuntime` reads
//     `postProcessingMode`, `postProcessingProvider` and `languageModel`. It
//     starts or stops llama-server and drives the local-runtime indicator.
//
// Fusing them into one key would make every post-processing edit tear down and
// reload the ASR model — a visible "(Loading…)" flicker in the Modes editor,
// and a wholly wasted reload. Neither key includes `name`, `sortOrder`,
// `enableScreenOCR`, `cloudProvider` or the accuracy tier: a rename must not
// tear down a resident model, and the cloud branch of `prepareModel` reports
// `.ready(name: "Cloud")` regardless of which cloud provider or tier is
// selected.
//
// Keying on content is also what makes `ModePreparationGate` below necessary.
// Under the old id key an in-place edit of the ALREADY-SELECTED Mode could not
// reach preparation at all, so neither prepare function ever ran against a Mode
// the user was, at that moment, dictating with. Now both can — see the gate.

/// What `prepareModel` reads off the selected Mode.
struct ASRPreparationKey: Equatable, Sendable {
    /// Kept in the key so switching between two Modes that happen to share a
    /// transcription signature still re-prepares.
    let modeId: String
    let model: String
    /// Normalized exactly the way `TranscriptionModelManager.extractLanguage(from:)`
    /// normalizes it, so this key changes when — and only when — the language
    /// value that actually reaches a provider changes. It is a preparation
    /// input because `preloadExclusively(_:language:preferEnglishOptimized:)`
    /// swaps to a different weights file for English-only models.
    let language: String?

    init?(_ snapshot: ModeSnapshot?) {
        guard let snapshot else { return nil }
        self.modeId = snapshot.id.uuidString
        self.model = snapshot.model
        if let raw = snapshot.language?.lowercased() {
            self.language = raw == "auto" ? nil : raw
        } else {
            self.language = nil
        }
    }
}

/// What `prepareLocalRuntime` reads off the selected Mode.
///
/// `postProcessingProvider` is read from `rawPostProcessingProvider` rather than
/// the defaulted `postProcessingProvider`, so "unset" and "explicitly set to the
/// default" stay distinguishable.
struct LocalRuntimePreparationKey: Equatable, Sendable {
    /// Kept in the key so switching between two Modes that happen to share a
    /// post-processing signature still re-prepares.
    let modeId: String
    let postProcessingMode: Int16
    let postProcessingProvider: String?
    let languageModel: String?

    init?(_ snapshot: ModeSnapshot?) {
        guard let snapshot else { return nil }
        self.modeId = snapshot.id.uuidString
        self.postProcessingMode = snapshot.postProcessingMode
        self.postProcessingProvider = snapshot.rawPostProcessingProvider
        self.languageModel = snapshot.languageModel
    }
}

// MARK: - Mode Preparation Gate

/// Whether a model-preparation pass may run against a given pipeline state.
///
/// Both halves of preparation tear down a resource an in-flight dictation may
/// still be using:
///
///   - `prepareModel` calls `cancelTranscription()` unconditionally
///     (`TranscriptionModelManager.swift`), which cancels
///     `TranscriptionPipeline.currentTask` — and that one task spans
///     transcription AND post-processing, so cancelling it during
///     `.postProcessing` loses the AI cleanup and, on the rethrowing branch,
///     the transcript with it.
///   - `prepareLocalRuntime` takes no state at all and falls through to
///     `llamaServerController.stop(...)` — a `SIGTERM` at
///     `LlamaServerController.swift` — or to an `ensureRunning` that restarts
///     the server when the model URL moved. Either kills the chat-completion
///     request a local post-processing pass is waiting on, and the user gets
///     un-post-processed text via `.localRuntimeUnavailable`.
///
/// So the answer is stated here, once, for both halves, rather than borrowed
/// from `TranscriptionPipeline.state_isReadyForTranscription()`: that function
/// answers "may a NEW transcription start", which happens to have the same
/// truth table today, and `prepareModel`'s own `if case .transcribing` bail-out
/// answers a THIRD question and gets it wrong — it does not cover
/// `.postProcessing`. Widening that bail-out was rejected: it is shared by four
/// other callers, and it would still leave `prepareLocalRuntime`, which has no
/// bail-out to widen, unprotected.
///
/// The `switch` is exhaustive on purpose. A new `TranscriptionState` case must
/// not compile until someone decides which side of this gate it falls on.
enum ModePreparationGate {

    /// - Parameter state: the transcription pipeline's current state, or `nil`
    ///   when no pipeline is wired yet. `nil` allows preparation: at launch the
    ///   sinks fire before `bootstrapAppServices()` wires the pipeline, where
    ///   preparation is a no-op on the optional anyway, and nothing would ever
    ///   replay a request deferred against a pipeline that does not exist.
    static func allowsPreparation(during state: TranscriptionState?) -> Bool {
        guard let state else { return true }
        switch state {
        case .idle, .error:
            return true
        case .transcribing, .postProcessing:
            return false
        }
    }
}

// MARK: - Main App State Class

/// ObservableObject allows SwiftUI to watch for changes
/// @MainActor ensures all UI updates happen on the main thread
@MainActor
class AppState: ObservableObject {

    // MARK: - Logger

    /// Structured logger for AppState operations
    /// Uses subsystem identifier and category for filtering logs in Console.app
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "AppState")

    // MARK: - Published Properties
    // @Published creates a publisher that emits when the value changes
    // SwiftUI automatically re-renders views that depend on these values
    
    /// Currently selected navigation item in the sidebar
    @Published var selectedNavigationItem: NavigationItem = .home
    
    /// Current state of the recording process
    @Published var recordingState: RecordingState = .idle
    
    /// Whether the app is currently recording
    /// This is computed from recordingState for convenience
    var isRecording: Bool {
        if case .recording = recordingState {
            return true
        }
        return false
    }
    
    /// Whether to show the mini recording window
    @Published var showMiniWindow: Bool = false
    
    /// Whether to show the recording dialog
    @Published var showRecordingDialog: Bool = false

    /// Whether to show the cancel recording confirmation dialog
    @Published var showCancelConfirmation: Bool = false
    
    /// Whether to show the onboarding flow.
    ///
    /// Mirrored into `TextDeliveryGate` so every transcript-delivery sink
    /// suppresses pasting/typing into other apps for the whole sheet lifetime —
    /// onboarding shows transcripts inline only.
    @Published var showOnboarding: Bool = false {
        didSet { TextDeliveryGate.setSuppressed(showOnboarding) }
    }
    
    /// Track onboarding completion state
    @Published var hasCompletedOnboarding: Bool = UserDefaults.standard.bool(forKey: "hasCompletedOnboarding")
    
    /// Currently selected mode ID (Core Data UUID string)
    ///
    /// Seeded here with the well-known Default Mode id and **no matching
    /// snapshot** — `selectedModeSnapshot` below starts `nil` on purpose. The
    /// seed still reaches model preparation, just indirectly (#318): `@Published`
    /// replays its current value to a new subscriber, so the `$selectedModeId`
    /// sink in `setupSubscriptions()` fires on the seeded id at construction and
    /// back-fills `selectedModeSnapshot`, which is what the preparation triggers
    /// are keyed on. That back-fill is therefore load-bearing — see the sink
    /// comments below before removing it.
    @Published var selectedModeId: String = "00000000-0000-0000-0000-000000000001"
    
    /// Currently selected mode name (for display)
    @Published var selectedModeName: String = "Default"

    /// Thread-safe snapshot of the selected Mode's properties.
    /// Fed by a background fetch on selectedModeId change or a filtered
    /// NSManagedObjectContextDidSave notification that affects this Mode.
    /// Views consume this instead of calling fetchMode on the main thread.
    /// Fixes Sentry HYPERWHISPER-KP (DB on Main Thread during Recording Start).
    @Published var selectedModeSnapshot: ModeSnapshot?

    /// The publisher the ASR-preparation sink is keyed on (#318).
    ///
    /// Stored so there is exactly one *definition* of this chain, named once and
    /// subscribed to by name. It is deliberately NOT shared: there is no
    /// `.share()`/`.multicast()`, so every subscriber gets its own `Map` and its
    /// own `RemoveDuplicates` state, and the test that observes it neither sees
    /// nor perturbs production's dedup. That is the property we want — `share()`
    /// would drop the `@Published` current-value replay a late subscriber
    /// depends on. What storedness buys is therefore an identity a test can name,
    /// not a shared subscription; the source-text test is what pins that
    /// `setupSubscriptions()` really subscribes to this property.
    ///
    /// `private(set)` shuts the setter while leaving the getter internal, which
    /// is all `@testable import` needs. `lazy` because the chain reads
    /// `$selectedModeSnapshot` off `self`.
    private(set) lazy var asrPreparationTrigger: AnyPublisher<ASRPreparationKey?, Never> =
        $selectedModeSnapshot
            .map { ASRPreparationKey($0) }
            .removeDuplicates()
            .eraseToAnyPublisher()

    /// The publisher the local-runtime-preparation sink is keyed on (#318).
    /// Separate from `asrPreparationTrigger` on purpose — see the key types for
    /// why the two halves of preparation must not share one trigger. Same
    /// storedness and sharing notes apply.
    private(set) lazy var localRuntimePreparationTrigger: AnyPublisher<LocalRuntimePreparationKey?, Never> =
        $selectedModeSnapshot
            .map { LocalRuntimePreparationKey($0) }
            .removeDuplicates()
            .eraseToAnyPublisher()

    /// Cached list of all modes sorted by sortOrder as value snapshots. Seeded
    /// synchronously once at launch, then refreshed off-main on Mode saves.
    /// Recording UI reads this instead of Mode managed objects so SwiftUI
    /// re-evaluation cannot fault-fill Core Data on the main thread.
    @Published private(set) var cachedSortedModeSnapshots: [ModeSnapshot] = []

    /// Mode pinned to the current recording/transcription session.
    /// This prevents stop/retry flows from drifting when the user changes
    /// the selected mode while a session is already in flight.
    @Published var activeSessionModeId: String?
    @Published var activeSessionModeName: String?
    
    /// Search query for history view
    @Published var historySearchQuery: String = ""

    /// Whether the settings window is open
    @Published var showSettings: Bool = false

    /// Tracks whether the toggle recording shortcut keys are currently held down
    @Published var isToggleRecordingShortcutHeld: Bool = false
    
    /// Audio level for visual feedback (0.0 to 1.0)
    @Published var currentAudioLevel: Float = 0.0
    
    /// Last transcribed text
    @Published var lastTranscription: String = ""

    /// Whether auto-paste did not occur and the dialog should offer manual copy.
    @Published var transcriptionPasteFailed: Bool = false

    /// When true, the last successful delivery was a Quick Capture → Apple Notes
    /// send, not an auto-paste. Lets the success toast show "Saved to Notes!"
    /// instead of "Pasted!". Reset to false at the start of every new recording.
    @Published var lastDeliveryWasQuickCapture: Bool = false

    /// Whether AI post‑processing is currently streaming
    @Published var isStreaming: Bool = false

    /// Dedicated high-frequency object for the streaming preview text. Kept off
    /// `AppState`'s own `@Published` storage so rapid streaming deltas don't
    /// invalidate every view observing `AppState` — see `StreamingLiveText` for the
    /// full rationale (HYPERWHISPER-F7).
    let liveStreamingText = StreamingLiveText()

    /// Partial text produced by streaming post‑processing.
    /// Computed passthrough to `liveStreamingText` for backward compatibility — reads
    /// and writes still work everywhere, but this no longer republishes through
    /// `AppState.objectWillChange`. Views needing reactive updates as streaming text
    /// changes should observe `liveStreamingText` directly via `@EnvironmentObject`.
    var streamingText: String {
        get { liveStreamingText.text }
        set { liveStreamingText.text = newValue }
    }

    /// True when the current recording was started via the streaming shortcut (Option+Shift+Space)
    /// Used to show "Streaming" badge in RecordingDialog instead of the mode name
    @Published var isStreamingShortcutTriggered: Bool = false

    /// Streaming connection lifecycle states
    /// Tracks the WebSocket connection state for streaming transcription to provide visual feedback
    /// This helps users know when they can start speaking (avoiding lost audio during connection setup)
    @Published var streamingConnectionState: StreamingConnectionState = .idle

    /// Whether the floating preview bubble should be shown above the recording dialog.
    /// Set when a streaming session targets a preview-only app (e.g. terminals) — text is
    /// shown in the bubble during speech and pasted in one shot at session end.
    @Published var showStreamingPreview: Bool = false

    /// Error message to display (nil if no error)
    @Published var errorMessage: String?
    
    /// Whether to show the error alert
    @Published var showErrorAlert: Bool = false

    /// The most recent failed transcript (for error alert retry button)
    /// This allows the RecordingDialog to reference the failed transcript when showing error alerts
    /// and provide a retry button that navigates to History and retries the transcription
    @Published var lastFailedTranscript: Transcript?

    /// Pending audio file path to retry transcription if the initial attempt fails before processing
    @Published var pendingRetryAudioPath: String?

    /// Whether to show the API key setup alert
    @Published var showAPIKeyAlert: Bool = false
    
    /// Missing API keys that need to be configured
    @Published var missingAPIKeys: [MissingAPIKey] = []
    
    /// Whether only post-processing keys are missing (non-blocking)
    @Published var postProcessingKeyMissing: Bool = false
    
    /// Whether to show the local mode suggestion in API key alert (for new installs)
    @Published var showLocalModeSuggestion: Bool = false

    /// One-shot request consumed by ModelLibraryView to present the API keys manager.
    @Published var shouldOpenModelLibraryAPIKeys: Bool = false


    /// Currently selected section in the Settings view
    @Published var selectedSettingsSection: String = "general"
    
    // MARK: - Private Properties
    
    /// Cancellables for Combine subscriptions
    /// These need to be stored to keep the subscriptions alive
    private var cancellables = Set<AnyCancellable>()

    /// The ASR-preparation key that has been requested but not run yet (#318).
    ///
    /// Non-nil means one of two things: the two triggers fired in the same main-
    /// actor turn and the run has not started yet, or `ModePreparationGate`
    /// deferred the work because a dictation was in flight. Either way it is a
    /// promise: `runPendingModePreparation()` clears it only by running it, and
    /// `requeueModePreparation(asr:localRuntime:)` puts it back if it could not.
    /// Dropping it instead would leave the status bar advertising a source the
    /// selected Mode no longer uses — which is #318 itself.
    private var pendingASRPreparationKey: ASRPreparationKey?

    /// The local-runtime-preparation key that has been requested but not run
    /// yet (#318). Same contract as `pendingASRPreparationKey` above.
    private var pendingLocalRuntimePreparationKey: LocalRuntimePreparationKey?

    /// Prevents SwiftUI body reads from queueing duplicate mode-cache refreshes
    /// while the initial background fetch is still in flight.
    private var isRefreshingModeCache = false

    /// Records a missed refresh request while a background refresh is running.
    private var needsModeCacheRefresh = false
    
    // MARK: - Initialization
    
    init() {
        // Set up any initial subscriptions or observers
        setupSubscriptions()
        
        // Load saved state from UserDefaults if needed
        loadSavedState()
    }
    
    // MARK: - Public Methods
    
    /// Navigate to a specific section
    /// - Parameter item: The navigation item to select
    func navigate(to item: NavigationItem) {
        // Add animation for smooth transition
        withAnimation(.easeInOut(duration: 0.2)) {
            selectedNavigationItem = item
        }
        
        // Log navigation for analytics (optional)
        logNavigation(to: item)
    }
    
    /// Navigate to a specific configuration section
    /// - Parameters:
    ///   - section: The configuration section to select (e.g., "apikeys", "general", "shortcuts")
    func navigateToSettings(section: String) {
        // First navigate to the settings view
        navigate(to: .settings)
        
        // Then set the selected section
        withAnimation(.easeInOut(duration: 0.2)) {
            selectedSettingsSection = section
        }
    }

    /// Navigate to Model Library and open the centralized API keys manager.
    func navigateToModelLibraryAPIKeys() {
        shouldOpenModelLibraryAPIKeys = true
        navigate(to: .modelLibrary)
    }
    
    /// Show an error message to the user
    ///
    /// **What This Does:**
    /// - Shows a compact, auto-dismissing error pill above the recording dialog (if open)
    /// - Or positions it at the default location if no recording dialog is visible
    /// - Auto-dismisses after 8 second countdown
    ///
    /// **Note:**
    /// This now uses the inline error toast instead of the large modal ErrorToastManager.
    /// The inline toast is less intrusive and doesn't require user interaction to dismiss.
    ///
    /// - Parameter message: The error message to display
    func showError(_ message: String) {
        // Ensure UI mutations happen on the main thread
        if !Thread.isMainThread {
            DispatchQueue.main.async { [weak self] in
                self?.showError(message)
            }
            return
        }

        errorMessage = message

        // Determine if we should show settings button based on message content.
        // Show for errors the user can fix in settings (API keys, auth, credits,
        // billing).
        //
        // Kept in step with `StreamingProviderErrorPolicy.terminalMarkers`: the
        // streaming client classifies exactly these conditions as user-fixable
        // and suppresses the reconnect on that basis, so the toast that replaces
        // the retry has to offer the fix. Five hand-written predicates missed
        // most of them — "exceeded your current quota" is not "quota exceeded" —
        // which left the app deciding a fault was the user's to fix and then
        // hiding where to fix it.
        let settingsActionableMarkers = [
            "api key",
            "api_key",
            "unauthorized",
            "authentication failed",
            "authentication_error",
            "forbidden",
            "permission denied",
            "permission_denied",
            "credit balance exhausted",
            "no credits remaining",
            "insufficient credits",
            "insufficient quota",
            "insufficient_quota",
            "quota exceeded",
            "exceeded your current quota",
            "billing",
            "payment required",
            "account is not active",
            // `TranscriptionError.cloudAccountRequired` — the client-side
            // HyperWhisper Cloud entitlement refusal (HYPERWHISPER-T2). Its
            // message says "requires an account key", which matched none of the
            // markers above, so the new error arrived with NO settings button at
            // all where the `.unauthorized` it replaced had one.
            //
            // Scope of that fix, stated plainly: the button comes back in
            // English only. Every entry in this list is an English literal, but
            // `message` reaches us already localized, so in the other 39 locales
            // nothing here matches at all — the German string reads "erfordert
            // einen Kontoschlüssel" and still gets no button. A typed,
            // locale-independent answer already exists and is simply bypassed on
            // this path: `TranscriptionError.showSettingsButton`, reached via
            // `showInlineError(_ error: TranscriptionError)` →
            // `InlineErrorToastManager.show(error:)`. Both call sites that raise
            // this error throw the typed value away and hand over only
            // `error.localizedDescription` (`RecordingTranscriptionFlow+ErrorHandling`
            // and the dialog's own matcher), leaving the message as the only
            // evidence available to decide on. So this is parity with the
            // `.unauthorized` case it replaces — the same English-only behaviour,
            // not a regression introduced here — and routing those call sites
            // through the typed overload, not lengthening this list, is the
            // actual fix. Deliberately not attempted in this change.
            //
            // Deliberately absent from `StreamingProviderErrorPolicy.terminalMarkers`: that
            // list classifies messages a provider sent over the wire, and this
            // refusal never reaches a provider.
            "account key"
        ]
        let showSettings = settingsActionableMarkers.contains { message.localizedCaseInsensitiveContains($0) }

        // Show the inline error toast (compact, auto-dismissing)
        // KEEP recording dialog open if it's visible - the toast appears ABOVE it
        InlineErrorToastManager.shared.show(
            message: message,
            showSettingsButton: showSettings,
            appState: self
        )

        // Do not show the main alert (avoid duplicates)
        showErrorAlert = false
    }

    /// Show a warning message to the user without interrupting the current flow
    ///
    /// **What This Does:**
    /// - Shows a compact, auto-dismissing warning pill above the recording dialog
    /// - Does NOT close the recording dialog
    /// - Auto-dismisses after 8 second countdown
    ///
    /// **Use For:**
    /// - Non-blocking warnings (e.g., compression failed but transcription continues)
    /// - User is informed but the operation proceeds
    ///
    /// - Parameter message: The warning message to display
    func showWarning(_ message: String) {
        // Ensure UI mutations happen on the main thread
        if !Thread.isMainThread {
            DispatchQueue.main.async { [weak self] in
                self?.showWarning(message)
            }
            return
        }

        // Show the inline toast WITHOUT closing the recording dialog
        // This allows transcription to continue while the user sees the warning
        InlineErrorToastManager.shared.show(
            message: message,
            showSettingsButton: false,
            appState: self
        )
    }

    /// Show an inline error toast above the recording dialog
    ///
    /// **What This Does:**
    /// - Shows a compact, auto-dismissing error pill above the recording dialog
    /// - Recording dialog stays open (does NOT close)
    /// - Toast auto-dismisses after 5 second countdown
    /// - Optional "Open Settings" button for actionable errors
    ///
    /// **Use For:**
    /// - Transcription errors that occur during recording/transcription flow
    /// - Errors like "No speech detected", "API key missing", etc.
    ///
    /// **Difference from showError:**
    /// - `showError` shows a large modal toast and closes the recording dialog
    /// - `showInlineError` shows a compact pill above the recording dialog which stays open
    ///
    /// - Parameter error: The TranscriptionError to display
    func showInlineError(_ error: TranscriptionError) {
        // Ensure UI mutations happen on the main thread
        if !Thread.isMainThread {
            DispatchQueue.main.async { [weak self] in
                self?.showInlineError(error)
            }
            return
        }

        // KEEP recording dialog open - do NOT set showRecordingDialog = false
        // The inline toast appears ABOVE the dialog

        // Show the compact inline toast using the error's own properties
        InlineErrorToastManager.shared.show(error: error, appState: self)

        // Store the error message for reference
        errorMessage = error.localizedDescription

        // Do not show the main alert (avoid duplicates)
        showErrorAlert = false
    }

    /// Show an inline error toast with a custom message and settings button flag
    ///
    /// **Use For:**
    /// - Generic error messages that don't map to a TranscriptionError case
    /// - Custom error scenarios
    ///
    /// - Parameters:
    ///   - message: The error message to display
    ///   - showSettingsButton: Whether to show the "Open Settings" button
    func showInlineError(message: String, showSettingsButton: Bool) {
        // Ensure UI mutations happen on the main thread
        if !Thread.isMainThread {
            DispatchQueue.main.async { [weak self] in
                self?.showInlineError(message: message, showSettingsButton: showSettingsButton)
            }
            return
        }

        // KEEP recording dialog open - do NOT set showRecordingDialog = false
        // The inline toast appears ABOVE the dialog

        // Show the compact inline toast
        InlineErrorToastManager.shared.show(
            message: message,
            showSettingsButton: showSettingsButton,
            appState: self
        )

        // Store the error message for reference
        errorMessage = message

        // Do not show the main alert (avoid duplicates)
        showErrorAlert = false
    }

    /// Ensures the main window is visible and in front (even if minimized or hidden).
    func bringMainWindowToFront() {
        let windows = NSApplication.shared.windows

        // Try to find our main window by identifier or content type
        let mainWindow = windows.first(where: { window in
            if window.identifier == .hyperwhisperMainWindow {
                return true
            }
            if let hosting = window.contentViewController as? NSHostingController<MainAppView> {
                return hosting.rootView is MainAppView
            }
            return false
        }) ?? windows.first

        guard let window = mainWindow else { return }

        if window.isMiniaturized {
            window.deminiaturize(nil)
        }

        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    /// Expose a helper for the toast action to take the user to Settings.
    func openSettingsFromErrorToast() {
        selectedNavigationItem = .settings
        bringMainWindowToFront()
    }
    
    // MARK: - API Key Alert Properties
    
    /// Dynamic title for the API key alert based on number of missing keys
    var apiKeyAlertTitle: String {
        if missingAPIKeys.isEmpty {
            return "api.key.required".localized
        } else if missingAPIKeys.count == 1 {
            // Special case for offline
            if case .offline = missingAPIKeys[0].context {
                return "api.key.internet.required".localized
            }
            return "api.key.required".localized
        } else {
            return "api.keys.required".localized
        }
    }
    
    /// Dynamic message for the API key alert listing all missing keys
    var apiKeyAlertMessage: String {
        guard !missingAPIKeys.isEmpty else {
            return "alerts.api.configure.generic".localized
        }

        // Special case for offline
        if missingAPIKeys.count == 1, case .offline = missingAPIKeys[0].context {
            return "alerts.api.internet.offline".localized
        }

        // Build message with provider names and contexts
        var message = missingAPIKeys.count > 1 ? "alerts.api.configure.header.multiple".localized : "alerts.api.configure.header.single".localized
        message += "\n\n"
        
        // Check for deduplication - if same provider is used for both transcription and post-processing
        var processedProviders = Set<String>()
        var dedupedKeys: [String] = []
        
        for key in missingAPIKeys {
            let providerName = key.providerName
            
            // Check if we've already processed this provider
            if !processedProviders.contains(providerName) {
                processedProviders.insert(providerName)
                
                // Check if this provider is used for both transcription and post-processing
                let isUsedForBoth = missingAPIKeys.contains { otherKey in
                    otherKey.providerName == providerName && otherKey.context != key.context
                }
                
                let bulletLabel = isUsedForBoth ? providerName : key.displayName
                dedupedKeys.append("alerts.api.configure.bullet".localized(arguments: bulletLabel))
            }
        }

        message += dedupedKeys.joined(separator: "\n")
        message += "\n\n"
        message += "alerts.api.configure.footer".localized

        return message
    }
    
    /// Switch to the local default mode (for new installs)
    /// This is called when user chooses "Use Local Mode" from the API key alert
    func switchToLocalDefault() {
        // Find the local default mode by its well-known UUID
        if let localDefault = PersistenceController.shared.fetchMode(withId: "00000000-0000-0000-0000-000000000002") {
            // Use centralized mode selection method
            selectMode(localDefault, persist: true)
            
            // Dismiss the API key alert
            showAPIKeyAlert = false
            
            AppLogger.ui.info("Switched to local default mode")
        } else {
            AppLogger.ui.warning("Local default mode not found")
        }
    }
    
    /// Reset the app to initial state
    func reset() {
        recordingState = .idle
        currentAudioLevel = 0.0
        lastTranscription = ""
        errorMessage = nil
        showErrorAlert = false
        showMiniWindow = false
    }
    
    // Mode selection handled through selectedModeId and selectedModeName properties
    
    // Reference to transcription manager (set during app initialization)
    weak var transcriptionPipeline: TranscriptionPipeline?
    
    // Reference to settings manager (set during app initialization)
    weak var settingsManager: SettingsManager?
    
    /// Get available modes filtered by network status
    /// When offline, excludes modes that require cloud services
    /// - Returns: Array of mode snapshots that are available in current network state
    func getAvailableModes() -> [ModeSnapshot] {
        if cachedSortedModeSnapshots.isEmpty {
            refreshCachedSortedModes()
        }
        let allModes = cachedSortedModeSnapshots

        // If online, return all modes
        guard !NetworkStatus.shared.isOnline else {
            return allModes
        }

        // When offline, filter out cloud-dependent modes
        return allModes.filter { mode in
            // Mode is offline-capable if:
            // 1. NOT using cloud model AND
            // 2. NOT using cloud post-processing
            let isCloudModel = mode.model.lowercased() == "cloud"
            let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
            let hasCloudPostProcessing = processingMode.requiresInternet
            return !isCloudModel && !hasCloudPostProcessing
        }
    }
    
    /// Select a mode and optionally persist to settings
    /// This centralizes mode selection logic to prevent drift between UI components
    /// - Parameters:
    ///   - mode: The Mode entity to select
    ///   - persist: Whether to persist to SettingsManager (default: true)
    func selectMode(_ mode: Mode, persist: Bool = true) {
        // Update AppState properties
        selectedModeId = mode.id?.uuidString ?? ""
        selectedModeName = mode.name ?? "Default"
        selectedModeSnapshot = ModeSnapshot(mode)

        // While the user is still actively recording, keep the in-flight session
        // aligned with the latest selected mode regardless of whether the change
        // came from the global shortcut or a manual click in the Modes view.
        if recordingState == .recording {
            beginActiveSessionMode(id: selectedModeId, name: selectedModeName)
        }
        
        // Persist to settings if requested
        if persist, let settingsManager = settingsManager {
            settingsManager.currentModeId = mode.id?.uuidString ?? ""
            settingsManager.currentMode = mode.name ?? "Default"
        }
        
        // Log the selection for debugging
        AppLogger.ui.debug("📝 Selected mode: \(mode.name ?? "Default") (persist: \(persist))")
    }

    /// Select a mode from a value snapshot without materializing a Core Data
    /// object on the main context. Used by recording UI and menu mode selectors.
    func selectMode(_ snapshot: ModeSnapshot, persist: Bool = true) {
        selectedModeId = snapshot.id.uuidString
        selectedModeName = snapshot.name
        selectedModeSnapshot = snapshot

        if recordingState == .recording {
            beginActiveSessionMode(id: selectedModeId, name: selectedModeName)
        }

        if persist, let settingsManager = settingsManager {
            settingsManager.currentModeId = snapshot.id.uuidString
            settingsManager.currentMode = snapshot.name
        }

        AppLogger.ui.debug("📝 Selected mode: \(snapshot.name, privacy: .public) (persist: \(persist))")
    }

    /// Counterpart to `selectMode` for when the selected Mode no longer exists.
    /// Leaving a stale id behind would point the app at a deleted row, so callers
    /// that delete the selected Mode without a replacement must clear it here.
    func clearModeSelection() {
        selectedModeId = ""
        selectedModeName = ""
        selectedModeSnapshot = nil

        if let settingsManager = settingsManager {
            settingsManager.currentModeId = ""
            settingsManager.currentMode = ""
        }
    }

    /// The mode currently relevant to the active recording/transcription session.
    /// Falls back to the selected mode when no session is active.
    var currentSessionModeName: String {
        activeSessionModeName ?? selectedModeName
    }

    var currentSessionModeId: String {
        activeSessionModeId ?? selectedModeId
    }

    func beginActiveSessionMode(id: String, name: String) {
        activeSessionModeId = id
        activeSessionModeName = name
    }

    func clearActiveSessionMode() {
        activeSessionModeId = nil
        activeSessionModeName = nil
    }
    
    /// Cycle to the next available mode (used when recording dialog is open)
    /// This method fetches available modes (filtered by network status), finds the current one, and selects the next
    /// If at the end of the list, it wraps around to the first mode
    func cycleToNextMode() {
        // STEP 1: Get only available modes (filtered by network status)
        let modes = getAvailableModes()

        // STEP 2: Ensure we have modes to cycle through
        guard !modes.isEmpty else {
            AppLogger.ui.warning("No available modes to cycle through in current network state")
            return
        }

        // STEP 3: Find the index of the currently selected mode
        let currentIndex = modes.firstIndex(where: { $0.id.uuidString == selectedModeId })

        // STEP 4: Calculate the next index with wraparound
        let nextIndex: Int
        if let currentIndex = currentIndex {
            // Move to next mode, wrap to 0 if at the end
            nextIndex = (currentIndex + 1) % modes.count
        } else {
            // If current mode not found (might be filtered out), start with the first available mode
            nextIndex = 0
        }

        // STEP 5: Select the next mode using centralized method
        let nextMode = modes[nextIndex]
        selectMode(nextMode, persist: true)

        // STEP 6: Log the cycle action
        AppLogger.ui.info("🔄 Cycled to mode: \(nextMode.name, privacy: .public)")
    }
    
    // MARK: - Private Methods
    
    /// Set up Combine subscriptions for reactive updates
    private func setupSubscriptions() {
        // Example: Subscribe to recording state changes
        $recordingState
            .sink { [weak self] state in
                // Perform actions based on state changes
                self?.handleRecordingStateChange(state)
            }
            .store(in: &cancellables)
        
        // Example: Debounce search query to avoid too many updates
        $historySearchQuery
            .debounce(for: .milliseconds(300), scheduler: RunLoop.main)
            .sink { [weak self] query in
                // Perform search with debounced query
                self?.performHistorySearch(query)
            }
            .store(in: &cancellables)

        // Preload the ASR model ASAP when the selected Mode's TRANSCRIPTION
        // inputs change.
        //
        // Keyed on `asrPreparationTrigger` — the Mode's CONTENT — and not on
        // `$selectedModeId`, because the id is an identity and this sink cares
        // about a value. Issue #318: onboarding's source commit reconfigures the
        // seeded Default Mode in place and re-selects the same UUID, so an
        // id-keyed `removeDuplicates()` swallowed the emission, `prepareModel`
        // never re-ran, and the status bar kept advertising the pre-onboarding
        // source until the next launch.
        //
        // The local runtime has its OWN key and its own sink below. Do not merge
        // the KEYS: `prepareModel` tears down and reloads the ASR model, so it
        // must not run for an edit that only touched post-processing. See the
        // key types for the full argument.
        //
        // Both sinks only RECORD what needs preparing and hand off to
        // `scheduleModePreparation()`, which owns the two things neither sink
        // can decide alone:
        //   - whether it is safe to prepare at all right now
        //     (`ModePreparationGate` — a dictation may be in flight), and
        //   - that a Mode SWITCH, which moves both keys in the same main-actor
        //     turn, resolves the Mode once instead of twice and runs the two
        //     halves in a defined order.
        //
        // A nil key means no Mode is selected. Nothing prepares for a nil Mode
        // today (`clearModeSelection()` leaves the llama server running — a
        // pre-existing hole, tracked separately), so assigning the nil through
        // is exactly right: it must not START anything, and it MUST cancel a
        // queued request for the Mode that just went away.
        //
        // Two consequences worth stating so they don't read as accidents:
        //   1. At launch the first key arrives from the snapshot back-fill the
        //      `$selectedModeId` sink below kicks off (the seeded id ships with a
        //      nil snapshot), with the explicit prepare in
        //      `initializeSelectedModeLightweight()` covering the branch where
        //      that back-fill lands before the pipeline is wired. Both are
        //      load-bearing now; neither may be removed.
        //   2. In-place edits made in the Modes editor now reach preparation too,
        //      via the save notification below → `refreshSelectedModeSnapshot`.
        //      That is deliberate: an editor edit that changes the model or the
        //      language has to reload the model, which under the old id key it
        //      never did. It is also why the gate exists — that same edit can be
        //      saved while the Mode is mid-dictation.
        asrPreparationTrigger
            .sink { [weak self] key in
                guard let self else { return }
                self.pendingASRPreparationKey = key
                self.scheduleModePreparation()
            }
            .store(in: &cancellables)

        // Start or stop the local LLM runtime when the selected Mode's
        // POST-PROCESSING inputs change — a disjoint set of fields from the one
        // above, which is why this is a second key rather than more fields on
        // the first (#318).
        //
        // This is what stops the local-runtime indicator showing a green llama
        // server for a Mode that no longer uses local post-processing:
        // `prepareLocalRuntime` is the only thing that stops the server, and
        // under the old id key an in-place edit never reached it.
        localRuntimePreparationTrigger
            .sink { [weak self] key in
                guard let self else { return }
                self.pendingLocalRuntimePreparationKey = key
                self.scheduleModePreparation()
            }
            .store(in: &cancellables)

        // Keep a background-fetched ModeSnapshot of the selected Mode in sync.
        // Refresh on selectedModeId change, and on viewContext saves that
        // actually touch the selected Mode object (filtered, not blanket).
        $selectedModeId
            .removeDuplicates()
            .sink { [weak self] modeId in
                self?.refreshSelectedModeSnapshot(for: modeId)
            }
            .store(in: &cancellables)

        NotificationCenter.default
            .publisher(for: .NSManagedObjectContextDidSave,
                       object: PersistenceController.shared.container.viewContext)
            .sink { [weak self] note in
                guard let self else { return }
                if self.saveTouchesSelectedMode(note) {
                    self.refreshSelectedModeSnapshot(for: self.selectedModeId)
                }
                if self.saveTouchesAnyMode(note) {
                    self.refreshCachedSortedModes()
                }
            }
            .store(in: &cancellables)

        // Warm the sorted-mode snapshot cache synchronously, once, at launch.
        // This runs on the uncontended main thread during init — never inside
        // the "Recording Start" transaction. Subsequent mode edits refresh the
        // cache off-main via the save subscription above.
        cachedSortedModeSnapshots = PersistenceController.shared.fetchAllModes().map(ModeSnapshot.init)
    }

    // MARK: - Mode Preparation (#318)

    /// True when no dictation is in flight, so preparation may run now.
    private func canRunModePreparationNow() -> Bool {
        ModePreparationGate.allowsPreparation(during: transcriptionPipeline?.state)
    }

    /// Runs whatever the two triggers have left pending, if it is safe to.
    ///
    /// Called synchronously by both sinks, and again by
    /// `resumeModePreparationIfPending()` when the pipeline goes back to idle.
    /// Cheap and idempotent: with nothing pending it returns immediately, which
    /// is what makes the second of two sinks firing in the same turn free.
    private func scheduleModePreparation() {
        guard pendingASRPreparationKey != nil || pendingLocalRuntimePreparationKey != nil else { return }

        guard canRunModePreparationNow() else {
            // Deferred, NOT dropped. The keys stay pending and the pipeline's
            // `state` observer calls `resumeModePreparationIfPending()` the
            // moment the dictation ends.
            AppLogger.models.info("Mode preparation deferred: a dictation is in flight. It will run when the pipeline goes idle.")
            return
        }

        Task { @MainActor in
            await self.runPendingModePreparation()
        }
    }

    /// Entry point for `TranscriptionPipeline`: a dictation just ended (or
    /// failed), so anything the gate held back may run now (#318).
    ///
    /// Safe to call on every state change — it costs one nil check when there is
    /// nothing pending.
    func resumeModePreparationIfPending() {
        scheduleModePreparation()
    }

    /// Resolves the pending Mode once and runs the halves that asked for it.
    ///
    /// One fetch, not two: both keys carry `modeId`, so a plain Mode switch
    /// moves both of them and would otherwise open two background contexts for
    /// the same row. And one `await` chain, not two: `prepareModel` can preload
    /// multi-gigabyte whisper weights while `prepareLocalRuntime` spawns
    /// llama-server and loads a GGUF, and there is no reason to make the machine
    /// do both at once.
    private func runPendingModePreparation() async {
        // Claim the work up front. Anything that arrives from here on lands in a
        // fresh pending key and is picked up by its own `scheduleModePreparation`
        // pass, so no request can be lost behind this one, and this function
        // always terminates.
        let asrKey = pendingASRPreparationKey
        let runtimeKey = pendingLocalRuntimePreparationKey
        pendingASRPreparationKey = nil
        pendingLocalRuntimePreparationKey = nil

        guard let modeId = asrKey?.modeId ?? runtimeKey?.modeId, !modeId.isEmpty else { return }
        guard canRunModePreparationNow() else {
            requeueModePreparation(asr: asrKey, localRuntime: runtimeKey)
            return
        }

        // Resolve the Mode on a background context so the SQL fetch doesn't land
        // inside the recording start transaction on the main thread. A missing
        // row is dropped rather than requeued: the Mode is gone, and retrying
        // would never resolve it.
        guard let mode = await PersistenceController.shared.fetchModeInBackground(withId: modeId) else { return }

        // The fetch above suspends, so ask the gate again — a dictation can have
        // started inside that window.
        guard canRunModePreparationNow() else {
            requeueModePreparation(asr: asrKey, localRuntime: runtimeKey)
            return
        }

        let modeName = mode.name ?? "Unknown"

        if let asrKey, asrKey.modeId == modeId {
            let modelId = (mode.model ?? "").isEmpty ? "base" : (mode.model ?? "base")
            AppLogger.models.info("Preparing ASR model for mode: \(modeName, privacy: .public) → model: \(modelId, privacy: .public)")
            await transcriptionPipeline?.prepareModel(for: mode)
        }

        if let runtimeKey, runtimeKey.modeId == modeId {
            // `prepareModel` above can take seconds, which is long enough for a
            // recording to finish and a dictation to start behind it.
            guard canRunModePreparationNow() else {
                requeueModePreparation(asr: nil, localRuntime: runtimeKey)
                return
            }
            let postProcessing = String(runtimeKey.postProcessingMode)
            AppLogger.models.info("Preparing local runtime for mode: \(modeName, privacy: .public) → postProcessingMode: \(postProcessing, privacy: .public)")
            await transcriptionPipeline?.prepareLocalRuntime(for: mode)
        }
    }

    /// Puts claimed-but-unrun preparation back, without clobbering a newer key.
    ///
    /// A key that arrived while this pass was suspended is by definition more
    /// current than the one being handed back, so a non-nil pending key always
    /// wins.
    private func requeueModePreparation(
        asr: ASRPreparationKey?,
        localRuntime: LocalRuntimePreparationKey?
    ) {
        if pendingASRPreparationKey == nil {
            pendingASRPreparationKey = asr
        }
        if pendingLocalRuntimePreparationKey == nil {
            pendingLocalRuntimePreparationKey = localRuntime
        }
    }

    /// Kicks off a background fetch and publishes the resulting snapshot on main.
    private func refreshSelectedModeSnapshot(for modeId: String) {
        guard !modeId.isEmpty else {
            selectedModeSnapshot = nil
            return
        }
        Task { [weak self] in
            let snapshot = await PersistenceController.shared.fetchModeSnapshotInBackground(withId: modeId)
            await MainActor.run {
                guard let self else { return }
                // Ignore stale responses if the selection changed mid-flight.
                guard modeId == self.selectedModeId else { return }
                self.selectedModeSnapshot = snapshot
            }
        }
    }

    /// Refreshes the sorted mode cache without executing the sorted Mode fetch
    /// on the main context. The cache backs mode cycling and the recording
    /// dialog's mode picker during recording start.
    private func refreshCachedSortedModes() {
        guard !isRefreshingModeCache else {
            needsModeCacheRefresh = true
            return
        }
        isRefreshingModeCache = true
        needsModeCacheRefresh = false

        Task { [weak self] in
            let snapshots = await PersistenceController.shared.fetchAllModeSnapshotsInBackground()
            await MainActor.run {
                guard let self else { return }
                self.cachedSortedModeSnapshots = snapshots
                self.isRefreshingModeCache = false
                if self.needsModeCacheRefresh {
                    self.refreshCachedSortedModes()
                }
            }
        }
    }

    /// Returns true if the save notification includes any Mode entity change.
    private func saveTouchesAnyMode(_ note: Notification) -> Bool {
        let keys: [String] = [
            NSInsertedObjectsKey,
            NSUpdatedObjectsKey,
            NSRefreshedObjectsKey,
            NSDeletedObjectsKey
        ]
        for key in keys {
            guard let objects = note.userInfo?[key] as? Set<NSManagedObject> else { continue }
            if objects.contains(where: { $0 is Mode }) { return true }
        }
        return false
    }

    /// Returns true if the save notification includes an inserted/updated/deleted
    /// Mode whose UUID matches the currently selected mode. This prevents
    /// refetching on every unrelated save (e.g. RecordingSession inserts on
    /// recording start), which is the DB-on-main issue tracked by HYPERWHISPER-KP.
    private func saveTouchesSelectedMode(_ note: Notification) -> Bool {
        guard let uuid = UUID(uuidString: selectedModeId) else { return false }
        let keys: [String] = [
            NSInsertedObjectsKey,
            NSUpdatedObjectsKey,
            NSRefreshedObjectsKey,
            NSDeletedObjectsKey
        ]
        for key in keys {
            guard let objects = note.userInfo?[key] as? Set<NSManagedObject> else { continue }
            for object in objects {
                guard let mode = object as? Mode else { continue }
                if mode.id == uuid { return true }
            }
        }
        return false
    }
    
    /// Handle recording state changes
    private func handleRecordingStateChange(_ state: RecordingState) {
        // Update UI based on state
        switch state {
        case .recording:
            logger.debug("Started recording")
        case .complete(let text):
            logger.debug("Completed with transcript: chars=\(text.count, privacy: .public)")
        case .error(let error):
            logger.debug("Error occurred: \(error, privacy: .public)")
        default:
            break
        }
    }
    
    /// Perform history search
    private func performHistorySearch(_ query: String) {
        // Avoid noisy logs for empty queries
        guard !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        // This would trigger a search in the history database
        logger.info("Searching history for: \(query, privacy: .public)")
    }
    
    /// Load saved state from UserDefaults
    private func loadSavedState() {
        // Load last selected navigation item
        if let savedNavItem = UserDefaults.standard.string(forKey: "lastNavigationItem"),
           let navItem = NavigationItem(rawValue: savedNavItem) {
            selectedNavigationItem = navItem
        }

        // Load last selected mode
        if let savedModeId = UserDefaults.standard.string(forKey: "lastSelectedMode") {
            // This would load the mode from the database
            logger.debug("Loading saved mode: \(savedModeId, privacy: .public)")
        }
    }
    
    /// Log navigation for analytics
    private func logNavigation(to item: NavigationItem) {
        // This could send analytics events
        logger.info("User navigated to: \(item.rawValue, privacy: .public)")

        // Save to UserDefaults for persistence
        UserDefaults.standard.set(item.rawValue, forKey: "lastNavigationItem")
    }
    
    // MARK: - Deinit
    
    deinit {
        // Clean up timers and subscriptions
        cancellables.removeAll()
    }
}

// MARK: - Transcription Mode
// TranscriptionMode struct removed - now using Core Data Mode entity
