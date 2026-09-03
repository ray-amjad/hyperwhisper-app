//
//  hyperwhisperApp.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  MAIN APPLICATION ENTRY POINT
//  This file defines the main structure of the HyperWhisper application.
//  It sets up both the main window and the menu bar functionality.

import SwiftUI
import Combine
import KeyboardShortcuts
import AppKit  // Required for NSApplication and menu bar functionality
import CoreData  // Required for Core Data persistence

// Centralized identifiers for app windows to avoid duplicate window creation
extension NSUserInterfaceItemIdentifier {
    static let hyperwhisperMainWindow = NSUserInterfaceItemIdentifier("hyperwhisper.mainWindow")
}

@MainActor
enum AppActivationPolicyController {
    static func apply(_ policy: NSApplication.ActivationPolicy) {
        let currentPolicy = NSApplication.shared.activationPolicy()
        guard currentPolicy != policy else { return }

        NSApplication.shared.setActivationPolicy(policy)
    }

    static func deactivateIfActive() {
        guard NSApplication.shared.isActive else { return }
        NSApplication.shared.deactivate()
    }
}

// MARK: - Main Window Reference
/// Static weak reference to the main window for reliable window reuse.
///
/// **Why this is needed:**
/// The `WindowConfigurator` that sets `window.identifier = .hyperwhisperMainWindow` runs
/// asynchronously when the view appears. If `FileTranscriptionFlow.openMainWindowWithHistory()`
/// searches for an existing window before the identifier is set, it won't find the window
/// and will create a duplicate.
///
/// By storing a direct reference when the window is configured, we can reliably find
/// and reuse the existing window regardless of timing.
enum MainWindowStore {
    static weak var window: NSWindow?
}

// MARK: - Main App Structure

/// @main attribute marks this as the entry point of the application
/// When the app launches, SwiftUI will instantiate this struct
@main
struct HyperWhisperApp: App {
    
    // Sparkle App Delegate
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    
    // MARK: - App Storage Properties
    
    /// @AppStorage is a property wrapper that automatically syncs with UserDefaults
    /// This means these values persist between app launches
    /// The string in quotes is the key used in UserDefaults
    
    /// Controls whether the app shows in the dock (like regular apps) or only in menu bar
    @AppStorage("showInDock") private var showInDock: Bool = true

    /// Controls whether the app launches with the main window hidden (menu bar only)
    @AppStorage("launchMinimized") private var launchMinimized: Bool = false
    
    /// Tracks whether this is the first launch (used for onboarding).
    /// Defaults to `false` so new installs see the first-run onboarding flow.
    @AppStorage("hasCompletedOnboarding") private var hasCompletedOnboarding: Bool = false

    /// Durable "onboarding still owed" signal. Set true on the launch that seeds
    /// the default modes (a genuine fresh install) and kept until onboarding is
    /// completed, so an interrupted first run is re-shown on the next launch —
    /// `didSeedDefaultModesOnLaunch` alone is only true on the seeding launch.
    @AppStorage("onboardingPending") private var onboardingPending: Bool = false

    /// Tracks whether we've shown the one-time Gemma removal migration alert
    @AppStorage("didShowGemmaMigrationAlert") private var didShowGemmaMigrationAlert: Bool = false

    /// Tracks whether we've migrated local_qwen → local_llm and cleaned up old Qwen model files
    @AppStorage("didMigrateQwenToLocalLLM") private var didMigrateQwenToLocalLLM: Bool = false

    /// Tracks whether we've migrated stored Gemma 4 12B language model ids from uppercase to
    /// lowercase. Unsloth ships the GGUF as `gemma-4-12b-it-Q4_K_M.gguf` (lowercase b); the
    /// initial catalog entry used uppercase `12B`. Modes saved against the old id break the
    /// Picker on launch until rewritten.
    @AppStorage("didMigrateGemma12bIdCasing") private var didMigrateGemma12bIdCasing: Bool = false
    
    // MARK: - State Objects
    
    /// @StateObject creates and owns this object for the entire app lifetime
    /// This is our central state manager that coordinates all app functionality
    @StateObject private var appState = AppState()
    
    /// Manages all audio recording functionality
    @StateObject private var audioManager = AudioRecordingManager()
    
    /// Manages transcription (both local and cloud)
    @StateObject private var transcriptionPipeline = TranscriptionPipeline()
    
    /// Manages app settings and preferences
    /// Initialize this first as AppState will need the loaded modes
    /// Uses the shared singleton so non-View code (e.g. BackupManager imports)
    /// mutates the same instance the UI observes
    @StateObject private var settingsManager = SettingsManager.shared

    /// Manages whisper.cpp models from Hugging Face
    @StateObject private var whisperModelManager = WhisperModelManager()

    /// Manages local language model downloads
    @StateObject private var localModelManager = LocalModelManager()

    /// Manages FluidAudio Parakeet models
    @StateObject private var parakeetModelManager = ParakeetModelManager()

    /// Manages FluidAudio Qwen3 ASR models
    @StateObject private var qwen3AsrModelManager = Qwen3AsrModelManager()

    /// Manages FluidAudio Nemotron 3.5 ASR models (latin + multilingual)
    @StateObject private var nemotronModelManager = NemotronModelManager()

    /// Manages license validation and usage tracking
    @StateObject private var licenseManager: LicenseManager
    
    /// Tracks HyperWhisper Cloud credit balance (depends on LicenseManager)
    @StateObject private var hyperWhisperCloudManager: HyperWhisperCloudManager
    @StateObject private var cloudProviderHealthManager = CloudProviderHealthManager()

    /// Manages custom OpenAI-compatible endpoints for post-processing
    @StateObject private var customPostProcessingManager = CustomPostProcessingManager()

    /// Aggregates cloud + local models into a single Library list
    @StateObject private var modelLibraryManager = ModelLibraryManager()
    
    /// Core Data persistence controller
    let persistenceController = PersistenceController.shared

    /// Auto-delete cleanup service for automatic deletion of old recordings
    /// This service runs on app launch and periodically to clean up old transcripts
    @State private var autoDeleteCleanupService: AutoDeleteCleanupService?

    // MARK: - Initialization
    
    init() {
        let sharedLicenseManager = LicenseManager()
        _licenseManager = StateObject(wrappedValue: sharedLicenseManager)
        _hyperWhisperCloudManager = StateObject(wrappedValue: HyperWhisperCloudManager(licenseManager: sharedLicenseManager))
        // Backup imports that carry a license key force a revalidation through
        // the same manager instance the rest of the app observes.
        BackupManager.shared.licenseManager = sharedLicenseManager

        // Register default preferences at first launch
        UserDefaults.registerHyperWhisperDefaults()

        // Migration: clear removed window shortcut defaults (Cmd+Shift+M, Ctrl+Cmd+H)
        // These were hidden global hotkeys that blocked other apps
        let key1 = "KeyboardShortcuts_toggleMiniWindow"
        let key2 = "KeyboardShortcuts_toggleMainWindow"
        if UserDefaults.standard.object(forKey: key1) != nil {
            UserDefaults.standard.removeObject(forKey: key1)
        }
        if UserDefaults.standard.object(forKey: key2) != nil {
            UserDefaults.standard.removeObject(forKey: key2)
        }

        // Configure the app's dock behavior based on user preference
        // This must be done early in the app lifecycle
        configureAppAppearance()
    }

// MARK: - Menu Bar Helper Views

// MENU BAR ICON VIEW - TINTS ICON RED WHEN RECORDING
// Only subscribes to recordingState via .onReceive to avoid re-rendering on every
// AppState change. Using @ObservedObject here caused setImage: to fire on unrelated
// property updates, triggering synchronous XPC hangs (HYPERWHISPER-F7).
@MainActor
struct MenuBarIconView: View {
    let appState: AppState
    @State private var recordingState: RecordingState = .idle
    @State private var cachedIdleImage: NSImage?
    @State private var cachedRecordingImage: NSImage?

    private let iconSize: CGFloat = 18
    private let containerSize: CGFloat = 22

    var body: some View {
        Group {
            if recordingState == .recording, let cachedRecordingImage {
                Image(nsImage: cachedRecordingImage)
                    .renderingMode(.original)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
            } else if let cachedIdleImage {
                Image(nsImage: cachedIdleImage)
                    .renderingMode(.template)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
            } else {
                Image(systemName: "circle.fill")
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .foregroundColor(recordingState == .recording ? .red : .primary)
            }
        }
        .frame(width: iconSize, height: iconSize)
        .frame(width: containerSize, height: containerSize)
        .onAppear {
            cachedRecordingImage = makeMenuBarImage(tint: NSColor.systemRed, isTemplate: false)
            cachedIdleImage = makeMenuBarImage(tint: NSColor.labelColor, isTemplate: true)
            recordingState = appState.recordingState
        }
        .onReceive(appState.$recordingState) { newState in
            guard recordingState != newState else { return }
            recordingState = newState
        }
        .accessibilityLabel(accessibilityLabel(for: recordingState))
        .allowsHitTesting(false)
    }

    private func makeMenuBarImage(tint: NSColor, isTemplate: Bool) -> NSImage? {
        guard let source = NSImage(named: "MenuBarIcon") else { return nil }
        let targetSize = NSSize(width: iconSize, height: iconSize)
        let image = NSImage(size: targetSize)
        image.lockFocus()
        NSGraphicsContext.current?.imageInterpolation = .high
        let rect = NSRect(origin: .zero, size: targetSize)
        let fillColor = isTemplate ? NSColor.white : tint
        fillColor.setFill()
        NSBezierPath(rect: rect).fill()
        source.draw(in: rect, from: NSRect(origin: .zero, size: source.size), operation: .destinationIn, fraction: 1.0)
        image.unlockFocus()
        image.isTemplate = isTemplate
        return image
    }

    private func accessibilityLabel(for state: RecordingState) -> String {
        switch state {
        case .recording:
            return "menu.bar.state.recording".localized
        case .transcribing, .postProcessing:
            return "menu.bar.state.processing".localized
        default:
            return "menu.bar.state.idle".localized
        }
    }
}

    // MARK: - Scene Configuration
    
    /// The body property defines all the scenes (windows) our app can display
    /// A Scene in SwiftUI represents a window or group of windows
    var body: some Scene {
        
        // MARK: Main Window
        /// WindowGroup creates the main application window
        /// It automatically handles window management (closing, minimizing, etc.)
        WindowGroup(licenseManager.licenseStatus == .active ? "app.title.pro".localized : "app.title".localized, id: "mainWindow") {  // Dynamic localized window title based on license

            // This is the root view of our main window
            MainAppView()
                // Inject our state objects into the environment
                // This makes them available to all child views
                .environmentObject(appState)
                .environmentObject(audioManager)
                .environmentObject(transcriptionPipeline)
                .environmentObject(settingsManager)
                .environmentObject(whisperModelManager)
                .environmentObject(parakeetModelManager)
                .environmentObject(qwen3AsrModelManager)
                .environmentObject(nemotronModelManager)
                .environmentObject(localModelManager)
                .environmentObject(licenseManager)
                .environmentObject(hyperWhisperCloudManager)
                .environmentObject(cloudProviderHealthManager)
                .environmentObject(customPostProcessingManager)
                .environmentObject(modelLibraryManager)
                .environmentObject(settingsManager.apiKeys)
                // High-frequency metrics isolated for performance (prevents MainAppView invalidation at 30 FPS)
                .environmentObject(audioManager.liveMetrics)
                // Inject Core Data context
                .environment(\.managedObjectContext, persistenceController.container.viewContext)

                // Set fixed window size (wider)
                .frame(width: 1000, height: 600)
                // Extend content under the hidden title bar and make the window itself translucent
                // so our VisualEffectBackground can blur behind the traffic-light controls as well.
                .background(
                    WindowConfigurator { window in
                        // Tag the main window so we can reliably re-use it instead of spawning duplicates
                        window.identifier = .hyperwhisperMainWindow
                        // Store reference for reliable window reuse (avoids timing issues with identifier lookup)
                        MainWindowStore.window = window
                        window.titleVisibility = .hidden                 // Hide window title text
                        window.titlebarAppearsTransparent = true         // Blend titlebar with content
                        window.isOpaque = false                          // Allow translucency
                        window.backgroundColor = .clear                  // Let blur show through
                        window.styleMask.insert(.fullSizeContentView)    // Extend content under titlebar
                        window.isMovableByWindowBackground = true        // Keep drag-to-move behavior
                        window.tabbingMode = .disallowed                 // Disable native tab bar behavior
                    }
                )
                // Event-driven updates: reflect newly installed/removed models
                .onReceive(whisperModelManager.$downloadedModels) { _ in
                    // Update transcription manager when models change
                    transcriptionPipeline.rescanAvailableLocalModels()
                }
                .onReceive(parakeetModelManager.$availableModels) { _ in
                    transcriptionPipeline.rescanAvailableLocalModels()
                }
                .onReceive(
                    parakeetModelManager.$availableModels
                        .dropFirst()
                        .removeDuplicates()
                ) { _ in
                    Task { @MainActor in
                        await transcriptionPipeline.refreshParakeetReadiness(
                            forModeId: appState.selectedModeId
                        )
                    }
                }
                .onReceive(qwen3AsrModelManager.$isDownloaded) { _ in
                    transcriptionPipeline.rescanAvailableLocalModels()
                }
                .onReceive(nemotronModelManager.$availableModels) { _ in
                    transcriptionPipeline.rescanAvailableLocalModels()
                }
                .onReceive(localModelManager.$downloadedModels) { _ in
                    cloudProviderHealthManager.refreshAllPostProcessing(force: true)
                    Task { @MainActor in
                        await transcriptionPipeline.refreshLocalRuntime(forModeId: appState.selectedModeId)
                    }
                }

                // Handle app lifecycle events
                .onAppear {
                    // Code that runs when the main window appears.
                    // Launch work that must not wait for the window lives in
                    // bootstrapAppServices(), which this calls first.
                    handleMainWindowAppear()
                }
                .onChange(of: settingsManager.checkForUpdatesAutomatically) { _, newValue in
                    // Keep Sparkle's automatic checks in sync with user setting
                    appDelegate.configureAutomaticChecks(enabled: newValue)
                }
                // Reflect Dock visibility changes immediately without restart
                .onChange(of: showInDock) { _, _ in
                    configureAppAppearance()
                }
                // Mode persistence now handled through Core Data
        }
        // Hide the standard title bar so we can extend our own blurred content to the very top
        .windowStyle(HiddenTitleBarWindowStyle())
        // Keep unified toolbar appearance for any future toolbars
        .windowToolbarStyle(.unified)
        
        // Disable window resizing
        .windowResizability(.contentSize)
        
        // Add keyboard shortcuts for the window
        .commands {
            // Remove "New Window" item
            CommandGroup(replacing: .newItem) { }
            
            // Remove "Edit" menu items
            CommandGroup(replacing: .undoRedo) { }

            // Remove "Window" menu items (Show All Tabs)
            CommandGroup(replacing: .windowList) { }

            // Clean up View menu
            // Remove "Show Tab Bar" / Toolbar items
            CommandGroup(replacing: .toolbar) { }
            // Remove "Enter Full Screen"
            CommandGroup(replacing: .windowSize) { }

            // Replace standard sidebar items with our custom toggle
            CommandGroup(replacing: .sidebar) { }

            
            // Add "Check for Updates…" to the application menu
            CommandGroup(after: .appInfo) {
                Button("menu.command.check.updates".localized) {
                    appDelegate.checkForUpdates()
                }
            }
            
            // Add items to Help menu
            CommandGroup(replacing: .help) {
                // Help Website - opens help documentation
                Button("menu.command.help.website".localized) {
                    if let helpURL = URL(string: "https://hyperwhisper.com/docs") {
                        NSWorkspace.shared.open(helpURL)
                    }
                }
                
                Divider()
                
                // Contact Support - opens support page
                Button("menu.command.contact.support".localized) {
                    if let supportURL = URL(string: "https://www.hyperwhisper.com/support") {
                        NSWorkspace.shared.open(supportURL)
                    }
                }
            }

            // Debug utilities available in production builds for support
            CommandMenu("menu.debug.title".localized) {
                Button("menu.command.export.logs".localized) {
                    exportUpdateLogs()
                }
                Button("menu.command.export.diagnostics".localized) {
                    exportAllDiagnostics()
                }
            }
            
        }
        
        // MARK: Menu Bar Extra
        /// MenuBarExtra creates an icon in the system menu bar (top-right of screen)
        /// This provides quick access even when the main window is closed
        #if os(macOS)  // MenuBarExtra is macOS-only
        MenuBarExtra {
            // The content shown when clicking the menu bar icon
            MenuBarContentView()
                .environmentObject(appState)
                .environmentObject(audioManager)
                .environmentObject(transcriptionPipeline)
                .environmentObject(settingsManager)
                .environmentObject(licenseManager)
                .environmentObject(hyperWhisperCloudManager)
                .environmentObject(cloudProviderHealthManager)
                .environmentObject(parakeetModelManager)
                .environmentObject(qwen3AsrModelManager)
                .environmentObject(nemotronModelManager)
                .environmentObject(localModelManager)
                .environmentObject(customPostProcessingManager)
        } label: {
            // FIX: Use MenuBarIconView with proper observation
            // The previous inline implementation couldn't properly observe appState changes
            // MenuBarIconView mirrors AppState updates so the status item refreshes reliably
            MenuBarIconView(appState: appState)
                // LOGIN-ITEM SAFETY NET: the menu bar label is the only scene
                // content macOS always renders. A login-item launch with
                // `launchMinimized` on never renders the WindowGroup, so the
                // window's .onAppear never fires (issue #142). Bootstrap here
                // so the global hotkeys exist without the window.
                .onAppear {
                    bootstrapAppServices()
                }
                // Ensure overlay window opens/closes even when main window is closed
                .onChange(of: appState.showRecordingDialog) { _, newValue in
                if newValue {
                    Task { @MainActor in
                        RecordingWindowManager.shared.open(
                            appState: appState,
                            audioManager: audioManager,
                            transcriptionPipeline: transcriptionPipeline,
                            settingsManager: settingsManager
                        )
                    }
                } else {
                    Task { @MainActor in
                        RecordingWindowManager.shared.close()
                    }
                }
            }
        }
        // Use native menu style so submenus open to the right with system hover highlight
        .menuBarExtraStyle(.menu)
        #endif
        
        // Settings removed - using SettingsView in sidebar instead
    }
    
    // MARK: - Helper Methods
    
    /// Configures the app's appearance in the dock and menu bar
    private func configureAppAppearance() {
        // NSApplication.shared is the singleton that represents our running app
        // activationPolicy determines how the app appears to the user

        let targetPolicy: NSApplication.ActivationPolicy = showInDock ? .regular : .accessory
        AppActivationPolicyController.apply(targetPolicy)
    }
    
    /// ONE-TIME LAUNCH BOOTSTRAP GUARD:
    /// `bootstrapAppServices()` runs from two scenes (the menu bar label and the
    /// main window). Static, not `@State`, so the guard survives any scene
    /// re-evaluation — same reason `hotkeysConfigured` is static.
    private static var didBootstrapAppServices = false

    /// PENDING-DEFERRAL MARKER:
    /// Set the moment the pre-launch deferral below registers its observer.
    /// Both callers can hit that path on the same cold launch, and without this
    /// they would each register their own `didFinishLaunchingNotification`
    /// observer. Static for the same reason as `didBootstrapAppServices`.
    private static var didDeferBootstrap = false

    /// Token for the deferral's `didFinishLaunchingNotification` observer.
    /// Held statically so that whichever resumption path wins — the notification
    /// or the backstop below — can unregister it; a callback that never runs
    /// cannot clean itself up, and would keep the registration (and everything it
    /// captured) alive for the process lifetime.
    private static var deferredBootstrapObserver: NSObjectProtocol?

    /// Re-arm budget for the deferral backstop. The backstop re-enqueues itself
    /// while launch is still incomplete, so the defence-in-depth survives more
    /// than one turn — bounded so a launch that never completes cannot spin the
    /// main queue forever.
    private static var deferredBootstrapRetries = 0
    private static let maxDeferredBootstrapRetries = 20
    private static let deferredBootstrapRetryDelay: TimeInterval = 0.1

    /// Schedule one more pre-launch bootstrap re-check, within the retry budget.
    private static func armDeferredBootstrapBackstop(_ recheck: @escaping () -> Void) {
        guard deferredBootstrapRetries < maxDeferredBootstrapRetries else {
            AppLogger.ui.warning("⚠️ Bootstrap backstop budget exhausted — waiting on didFinishLaunchingNotification alone")
            return
        }
        deferredBootstrapRetries += 1
        DispatchQueue.main.asyncAfter(deadline: .now() + deferredBootstrapRetryDelay) {
            recheck()
        }
    }

    /// Launch work that must not depend on the main window existing.
    ///
    /// The main window is NOT guaranteed to be created at launch. When the app
    /// starts as a login item with `launchMinimized` on, macOS 26 never renders
    /// the `WindowGroup` content, so its `.onAppear` never fires. Everything
    /// below used to live in that `.onAppear` — including `setupGlobalHotkeys()`
    /// — so the record shortcut stayed dead until the user clicked the Dock icon
    /// and forced the window into existence (issue #142).
    ///
    /// Callers: the `MenuBarExtra` label (always renders) and
    /// `handleMainWindowAppear()`. The guard makes the second call a no-op.
    private func bootstrapAppServices() {
        guard !Self.didBootstrapAppServices else { return }

        // The menu bar label can render before AppKit finishes launching. Sparkle
        // and the Local API server below both expect a fully launched app, so
        // wait for the delegate rather than assume an ordering.
        guard AppDelegate.didFinishLaunching else {
            // Only ever arm the observer once — see didDeferBootstrap. Re-entry
            // (the backstop, or the second caller) still re-arms the backstop
            // below, so the notification never becomes the only way back in.
            guard !Self.didDeferBootstrap else {
                Self.armDeferredBootstrapBackstop { self.bootstrapAppServices() }
                return
            }
            Self.didDeferBootstrap = true

            AppLogger.ui.debug("⏳ Bootstrap requested before launch completed — deferring")
            Self.deferredBootstrapObserver = NotificationCenter.default.addObserver(
                forName: NSApplication.didFinishLaunchingNotification,
                object: nil,
                queue: .main
            ) { _ in
                Task { @MainActor in
                    // The body below removes the observer for every path.
                    self.bootstrapAppServices()
                }
            }

            // MISSED-NOTIFICATION BACKSTOP:
            // `didFinishLaunchingNotification` is one-shot and is the only
            // resumption path above. An observer registered while AppKit is
            // mid-dispatch of that notification is never called, which would
            // leave a login-item launch with no hotkeys again (issue #142).
            // Re-check on a later main-queue turn: by then the delegate has
            // set its flag and this runs the body. Re-entry is harmless —
            // whichever of the two arrives first sets didBootstrapAppServices
            // and the other returns at the guard above.
            Self.armDeferredBootstrapBackstop { self.bootstrapAppServices() }
            return
        }

        Self.didBootstrapAppServices = true

        // Tear the deferral down unconditionally: the notification callback is
        // NOT guaranteed to be the path that got us here (the backstop may have
        // won the race), and an observer that only unregisters from inside its
        // own callback then leaks itself and everything it captured.
        if let observer = Self.deferredBootstrapObserver {
            NotificationCenter.default.removeObserver(observer)
            Self.deferredBootstrapObserver = nil
        }

        AppLogger.ui.debug("🚀 Bootstrapping app services")

        // DEPENDENCY INJECTION: Connect the model manager to transcription manager
        // This must be done early to ensure LibWhisperProvider gets the shared instance
        transcriptionPipeline.setModelManager(whisperModelManager)
        transcriptionPipeline.setParakeetModelManager(parakeetModelManager)
        transcriptionPipeline.setQwen3AsrModelManager(qwen3AsrModelManager)
        transcriptionPipeline.setNemotronModelManager(nemotronModelManager)
        transcriptionPipeline.setLocalModelManager(localModelManager)
        transcriptionPipeline.setSpeechAnalyzerProvider()
        localModelManager.refreshCatalog()

        // Wire the Library aggregator to the live data sources.
        modelLibraryManager.configure(
            cloudHealth: cloudProviderHealthManager,
            apiKeys: settingsManager.apiKeys,
            whisperManager: whisperModelManager,
            parakeetManager: parakeetModelManager,
            qwen3AsrManager: qwen3AsrModelManager,
            nemotronManager: nemotronModelManager,
            localLLMManager: localModelManager
        )

        // Configure AudioRecordingManager with dependencies
        audioManager.configure(
            transcriptionPipeline: transcriptionPipeline,
            settingsManager: settingsManager,
            providerHealthManager: cloudProviderHealthManager,
            appState: appState,
            licenseManager: licenseManager
        )

        // CRASH RECOVERY: Attempt to recover any incomplete recordings from previous crashes
        // This must run after audioManager.configure() so recordingsDirectory is available
        // and before user can start new recordings
        Task {
            await audioManager.recoverOrphanedRecordings()
        }

        // Connect transcriptionPipeline to appState for model preloading
        // This allows AppState to trigger model loading when mode changes
        appState.transcriptionPipeline = transcriptionPipeline
        appState.settingsManager = settingsManager

        // Connect settingsManager to transcriptionPipeline
        transcriptionPipeline.settingsManager = settingsManager
        transcriptionPipeline.providerHealthManager = cloudProviderHealthManager
        transcriptionPipeline.licenseManager = licenseManager
        transcriptionPipeline.creditManager = hyperWhisperCloudManager
        transcriptionPipeline.customPostProcessingManager = customPostProcessingManager
        transcriptionPipeline.appState = appState

        cloudProviderHealthManager.configure(apiKeyProvider: settingsManager)
        cloudProviderHealthManager.refreshAll()
        cloudProviderHealthManager.refreshAllPostProcessing()

        // LOCAL API SERVER: wire dependencies, then start the server if the
        // user toggle is on. The server stays off by default — Settings →
        // API Server flips `localAPIServerEnabled` and calls start()/stop().
        LocalAPIServer.shared.configure(
            transcriptionPipeline: transcriptionPipeline,
            cloudHealth: cloudProviderHealthManager,
            modelLibrary: modelLibraryManager,
            settingsManager: settingsManager,
            whisperModelManager: whisperModelManager,
            parakeetModelManager: parakeetModelManager,
            qwen3AsrModelManager: qwen3AsrModelManager,
            nemotronModelManager: nemotronModelManager,
            localModelManager: localModelManager
        )
        if UserDefaults.standard.bool(forKey: LocalAPIServerEnabledKey) {
            LocalAPIServer.shared.start()
        }

        // Set up global hotkeys. These work even when the app isn't focused, and
        // they do not need the app to be active: KeyboardShortcuts registers
        // Carbon hotkeys, and the bare-modifier path uses a CGEvent tap. Both
        // depend on Accessibility permission, not on activation.
        setupGlobalHotkeys()

        // Note: Microphone permission is now requested only when user tries to record
        // This prevents the permission dialog from appearing on every app launch

        // Initialize Sparkle updater based on user's auto-update setting
        // This syncs Sparkle's state with the UserDefaults setting and performs
        // an immediate background check if auto-update is enabled
        appDelegate.initializeUpdater()

        // GEMMA MIGRATION: Show one-time alert if user had Gemma selected or has leftover files
        if !didShowGemmaMigrationAlert {
            let gemmaDir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
                .appendingPathComponent("hyperwhisper/gemma")
            let hasGemmaFiles = FileManager.default.fileExists(atPath: gemmaDir.path)

            let modes = PersistenceController.shared.fetchAllModes()
            let hasGemmaMode = modes.contains { ($0.languageModel ?? "").contains("gemma-3") }

            if hasGemmaFiles || hasGemmaMode {
                // Disable post-processing on any modes that were using Gemma
                for mode in modes where (mode.languageModel ?? "").contains("gemma-3") {
                    mode.postProcessingMode = 0  // PostProcessingMode.off
                    AppLogger.ui.info("🔄 Disabled post-processing on mode '\(mode.name ?? "unknown", privacy: .public)' (was using Gemma)")
                }
                try? persistenceController.container.viewContext.save()

                // Silently clean up leftover Gemma model files
                if hasGemmaFiles {
                    try? FileManager.default.removeItem(at: gemmaDir)
                    AppLogger.ui.info("🗑️ Cleaned up Gemma model files at \(gemmaDir.path, privacy: .public)")
                }

                didShowGemmaMigrationAlert = true
            } else {
                // No Gemma usage found — mark as done silently
                didShowGemmaMigrationAlert = true
            }
        }

        // QWEN → LOCAL LLM MIGRATION: Rewrite provider values and clean up old model files
        if !didMigrateQwenToLocalLLM {
            let context = persistenceController.container.viewContext
            let modes = PersistenceController.shared.fetchAllModes()
            var didChange = false

            for mode in modes where mode.postProcessingProvider == "local_qwen" {
                mode.postProcessingMode = 0  // PostProcessingMode.off — user must download Gemma 4 and re-enable
                mode.postProcessingProvider = nil
                mode.languageModel = nil
                AppLogger.ui.info("Migrated mode '\(mode.name ?? "unknown", privacy: .public)' from local_qwen to off (cleared local model selection)")
                didChange = true
            }

            // Update transcript records
            let fetchRequest = NSFetchRequest<NSManagedObject>(entityName: "Transcript")
            fetchRequest.predicate = NSPredicate(format: "postProcessingProvider == %@", "local_qwen")
            if let transcripts = try? context.fetch(fetchRequest) {
                for transcript in transcripts {
                    transcript.setValue("local_llm", forKey: "postProcessingProvider")
                }
                if !transcripts.isEmpty { didChange = true }
            }

            if didChange {
                try? context.save()
            }

            // Clean up old Qwen model directory
            let oldQwenDir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
                .appendingPathComponent("hyperwhisper/qwen")
            if FileManager.default.fileExists(atPath: oldQwenDir.path) {
                try? FileManager.default.removeItem(at: oldQwenDir)
                AppLogger.ui.info("Cleaned up old Qwen model directory at \(oldQwenDir.path, privacy: .public)")
            }

            didMigrateQwenToLocalLLM = true
        }

        // GEMMA 4 12B ID CASING MIGRATION: rewrite stored uppercase model id to lowercase
        // so Modes saved against the original `gemma-4-12B-it-Q4_K_M.gguf` no longer crash the
        // Picker validation on launch (case-sensitive `contains` failed against the lowercase
        // canonical catalog entry `gemma-4-12b-it-Q4_K_M.gguf`).
        if !didMigrateGemma12bIdCasing {
            let context = persistenceController.container.viewContext
            let modes = PersistenceController.shared.fetchAllModes()
            let oldId = "gemma-4-12B-it-Q4_K_M.gguf"
            let newId = "gemma-4-12b-it-Q4_K_M.gguf"
            var didChange = false

            for mode in modes where mode.languageModel == oldId {
                mode.languageModel = newId
                AppLogger.ui.info("Migrated mode '\(mode.name ?? "unknown", privacy: .public)' Gemma 12B languageModel id to lowercase canonical")
                didChange = true
            }

            if didChange {
                try? context.save()
            }

            didMigrateGemma12bIdCasing = true
        }

        // AUTO-DELETE CLEANUP SERVICE:
        // Initialize and start the service that automatically deletes old recordings
        // based on user settings. Runs immediately on launch and at intervals based on time unit.
        if autoDeleteCleanupService == nil {
            autoDeleteCleanupService = AutoDeleteCleanupService(
                settingsManager: settingsManager.autoDelete,
                persistenceController: persistenceController
            )
            // Set reference so settings UI can access next cleanup date
            settingsManager.autoDelete.cleanupService = autoDeleteCleanupService
            autoDeleteCleanupService?.startPeriodicCleanup()
            AppLogger.ui.debug("🗑️ Auto-delete cleanup service started")
        }

        // Restore the selected mode. Deferred one turn so this appear pass isn't
        // mutating AppState from inside it — that hop is all the removed
        // `bootstrapModelsOnce()` await was buying, since its body was empty and
        // only flipped a `@State` flag nothing else read.
        //
        // This covers the windowless login-item launch. `handleMainWindowAppear()`
        // re-runs it on every LATER window appearance, because it is also the only
        // repair for a dangling `currentModeId` — see the note there.
        Task { @MainActor in
            initializeSelectedModeLightweight()
        }
    }

    /// How long after `applicationDidFinishLaunching` a main-window appearance
    /// still counts as part of launch. Generous on purpose: the only thing on the
    /// other side of it is a window the user asked for by hand, which no plausible
    /// launch takes this long to reach.
    private static let launchAppearanceGracePeriod: TimeInterval = 10

    /// Handles setup that genuinely needs the main window to exist.
    ///
    /// Only onboarding (which presents a sheet in this window) and the
    /// launch-minimized hide belong here. Everything else moved to
    /// `bootstrapAppServices()`, which this calls first so a normal launch keeps
    /// the original ordering.
    private func handleMainWindowAppear() {
        // Whether the bootstrap had already run before this appear pass. Used at
        // the end to avoid restoring the selected mode twice on a normal launch.
        let wasAlreadyBootstrapped = Self.didBootstrapAppServices
        bootstrapAppServices()

        // Is this the window appearance that belongs to app launch?
        //
        // Since #182 the main window may be created for the first time long after
        // launch — the user picks Settings from the menu bar and SwiftUI builds the
        // WindowGroup right then. `didResolveLaunchMinimizedHide` does not catch
        // that: a login-item launch never renders the window, so the hide is still
        // unresolved and would fire on the window the user just asked for. Gate on
        // elapsed time instead of on "first appearance", which would also cancel
        // the retry the no-window-yet path below depends on.
        let isLaunchAppearance: Bool
        if let launchedAt = AppDelegate.didFinishLaunchingAt {
            isLaunchAppearance = Date().timeIntervalSince(launchedAt) < Self.launchAppearanceGracePeriod
        } else {
            // Launch hasn't even finished — this can only be the launch window.
            isLaunchAppearance = true
        }

        // Ensure the app is activated so the window comes forward on a normal
        // (non-minimized) launch. Hotkeys no longer depend on this — see
        // setupGlobalHotkeys() in bootstrapAppServices().
        NSApp.activate(ignoringOtherApps: true)

        // Resolve onboarding before launch-minimized behavior. Existing users may
        // not have a persisted completion key because older builds defaulted it to
        // true; marking them complete here ensures their first upgraded launch
        // still honors the menu-bar-only preference.
        if !hasCompletedOnboarding {
            if PersistenceController.shared.didSeedDefaultModesOnLaunch {
                onboardingPending = true
            }
            if onboardingPending {
                // Small delay to ensure the main window is ready before showing sheet
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    appState.showOnboarding = true
                }
            } else {
                // Existing user (modes already present): treat onboarding as done.
                hasCompletedOnboarding = true
            }
        }

        // LAUNCH MINIMIZED: Hide main window if preference is set
        // This allows the app to run in menu bar only mode by default
        // Users can still access the window via Menu Bar > Settings
        if isLaunchAppearance && launchMinimized && hasCompletedOnboarding && !Self.didResolveLaunchMinimizedHide {
            // Delay to ensure window is fully created before hiding
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                // MainWindowStore, not NSApp.windows.first: hotkeys are now live
                // before this window exists, so the recording overlay panel can
                // already be in NSApp.windows and its order is unspecified.
                // Identifier fallback for the same reason as
                // MainAppView.openMainWindow(): MainWindowStore is published from
                // WindowConfigurator's own deferred main-queue hop, which skips
                // any turn where the NSView is not yet in a window hierarchy.
                let mainWindow = MainWindowStore.window
                    ?? NSApplication.shared.windows.first(where: { $0.identifier == .hyperwhisperMainWindow })
                guard let mainWindow else {
                    // Do NOT mark the hide resolved here: nothing was hidden, and
                    // a later appearance must still be able to apply it. Both
                    // lookups above fail together (WindowConfigurator sets the
                    // store and the identifier in one closure), so this is a real
                    // "no window yet", not a half-configured one.
                    AppLogger.ui.warning("⚠️ launchMinimized: no main window found to hide — retrying on the next appearance")
                    return
                }
                Self.didResolveLaunchMinimizedHide = true
                mainWindow.orderOut(nil)
                AppLogger.ui.debug("🪟 Main window hidden on launch (launchMinimized enabled)")
                // Return focus to previous app after hiding our window
                NSApp.deactivate()
            }
        } else {
            // Prepare the recordings folder with a user-friendly permission flow.
            // ONLY on a pass where the window is staying: this can ask about
            // Documents access via the SwiftUI `.alert` in MainAppView's window
            // body, and raising that question 300ms before the branch above orders
            // the same window out means the user sees a flash and is never asked.
            // A launch that minimizes therefore doesn't ask at all — the next
            // appearance (the user opening the window) takes this branch and asks
            // then, and a recording started before that gets the window-independent
            // presentStorageRecoveryPrompt() from its caller.
            //
            // canPresentInWindow: true because we ARE the window's appear pass.
            // MainWindowStore.window is published from WindowConfigurator's own
            // deferred hop and can still be nil right here, so the manager's
            // visibility probe would otherwise wrongly skip the question.
            settingsManager.prepareRecordingsFolderIfNeeded(canPresentInWindow: true)
        }

        // MODE REPAIR — per appearance, not once per process.
        // `initializeSelectedModeLightweight()` is the only code that maps
        // `settingsManager.currentModeId` onto `appState.selectedModeId`, and the
        // only thing that repairs a `currentModeId` pointing at a mode that no
        // longer exists — restoring a backup with `.replace` recreates every mode
        // under new UUIDs, after which recording fails with
        // `recording.error.modeMissing`. The `$selectedModeId` sinks in AppState
        // react to changes but never repair a stale value, so this must not sit
        // behind the bootstrap once-guard the way it did when it moved out of this
        // `.onAppear`. `bootstrapAppServices()` runs it for the windowless
        // login-item launch; this covers every later appearance, as origin/main's
        // `.onAppear` did.
        if wasAlreadyBootstrapped {
            Task { @MainActor in
                initializeSelectedModeLightweight()
            }
        }
    }

    /// Whether the launch-minimized hide has been settled for this process —
    /// either because it ran, or because the user asked for the window and it must
    /// never run.
    ///
    /// The main window can appear many times per process (menu-bar mode closes it,
    /// `MainAppView.openMainWindow()` re-opens it) while
    /// `launchMinimized`/`hasCompletedOnboarding` are `@AppStorage` and stay true
    /// for the whole run. Static for the same reason as `didBootstrapAppServices`.
    private static var didResolveLaunchMinimizedHide = false

    /// Record that the user deliberately asked for the main window, so
    /// `launchMinimized` will not take it away from them.
    ///
    /// The hide must only ever apply to the window LAUNCH created. On the
    /// login-item launch this PR exists for, macOS never renders the
    /// `WindowGroup`, so the FIRST main-window appearance of the process can be a
    /// user-driven one minutes later — Menu Bar → Settings, a Dock click, or a
    /// dropped audio file opening History — and hiding that is the misfire. There
    /// is no wall-clock answer to this (a slow cold boot pushes the real launch
    /// window past any bound, and a fast menu-bar click lands inside it), but the
    /// app already knows: every deliberate open goes through a named function, and
    /// those functions say so here.
    static func suppressLaunchMinimizedHide() {
        guard !didResolveLaunchMinimizedHide else { return }
        didResolveLaunchMinimizedHide = true
        AppLogger.ui.debug("🪟 Main window opened on request — launchMinimized hide no longer applies")
    }

    /// Lightweight initializer that restores selection state only.
    /// Preloading is handled by AppState to avoid duplicate work and races on startup.
    private func initializeSelectedModeLightweight() {
        let modes = PersistenceController.shared.fetchAllModes()

        let resolvedMode: Mode?

        // By ID if present
        if !settingsManager.currentModeId.isEmpty,
           let byId = modes.first(where: { $0.id?.uuidString == settingsManager.currentModeId }) {
            resolvedMode = byId
            AppLogger.ui.debug("📝 Loaded saved mode by ID: \(byId.name ?? "Default")")
        } else if !settingsManager.currentMode.isEmpty,
                  let byName = modes.first(where: { $0.name == settingsManager.currentMode }) {
            // By name fallback
            resolvedMode = byName
            settingsManager.currentModeId = byName.id?.uuidString ?? ""
            AppLogger.ui.debug("📝 Loaded saved mode by name: \(byName.name ?? "Default")")
        } else if let fallback = PersistenceController.shared.findDefaultMode() ?? modes.first {
            // Default fallback
            resolvedMode = fallback
            settingsManager.currentModeId = fallback.id?.uuidString ?? ""
            settingsManager.currentMode = fallback.name ?? "Default"
            AppLogger.ui.debug("📝 Using fallback mode: \(fallback.name ?? "Default")")
        } else {
            resolvedMode = nil
        }

        if let mode = resolvedMode {
            appState.selectedModeId = mode.id?.uuidString ?? ""
            appState.selectedModeName = mode.name ?? "Default"
            appState.selectedModeSnapshot = ModeSnapshot(mode)

            // Explicitly prepare the model. LOAD-BEARING, not belt-and-braces —
            // do not delete this as redundant with AppState's preparation sink.
            //
            // That sink is keyed on the selected Mode's content (#318), and its
            // first value at launch comes from AppState's asynchronous snapshot
            // back-fill. If the back-fill lands BEFORE `bootstrapAppServices()`
            // wires `appState.transcriptionPipeline`, the sink fires into a nil
            // pipeline and its `removeDuplicates()` memoizes that key — the
            // assignments just above then publish an equal key and are swallowed,
            // leaving this `Task` as the only thing that prepares a model at
            // launch. (If the back-fill lands after, both prepare and
            // `preparationGeneration` makes the second one a no-op.)
            Task { @MainActor in
                await transcriptionPipeline.prepareModel(for: mode)
                await transcriptionPipeline.prepareLocalRuntime(for: mode)
            }
        }
    }

    /// DEBOUNCE MECHANISM: Track last toggle time to prevent rapid shortcut presses
    /// Problem: Users pressing shortcuts rapidly (within 300ms) could trigger multiple
    /// recording sessions before the first one initializes, causing file conflicts
    /// Solution: Ignore toggle requests that come within 300ms of the previous one
    private static var lastToggleTime: Date?
    private static let debounceInterval: TimeInterval = 0.3 // 300ms between toggles

    /// DUPLICATE HANDLER PREVENTION:
    /// KeyboardShortcuts library APPENDS handlers instead of replacing them.
    /// If setupGlobalHotkeys() is called multiple times (e.g., via SwiftUI's .onAppear),
    /// handlers accumulate and each keypress executes ALL of them.
    /// This flag ensures handlers are only registered once.
    private static var hotkeysConfigured = false

    /// Sets up global keyboard shortcuts that work system-wide
    private func setupGlobalHotkeys() {
        // Guard against duplicate handler registration
        guard !Self.hotkeysConfigured else {
            AppLogger.ui.debug("🔧 Global hotkeys already configured, skipping duplicate setup")
            return
        }
        Self.hotkeysConfigured = true

        AppLogger.ui.debug("🔧 Setting up global hotkeys...")
        
        // MARK: Toggle Recording Shortcut
        KeyboardShortcuts.onKeyDown(for: .toggleRecordingWithTranscription) { [weak appState, weak transcriptionPipeline] in
            Task { @MainActor in
                appState?.isToggleRecordingShortcutHeld = true
                transcriptionPipeline?.prewarmCloudConnectionIfActive()
            }
        }

        KeyboardShortcuts.onKeyUp(for: .toggleRecordingWithTranscription) { [weak appState, weak audioManager] in
            Task { @MainActor in
                defer { appState?.isToggleRecordingShortcutHeld = false }

                AppLogger.ui.debug("⌨️ Toggle recording shortcut pressed")

                // DEBOUNCE CHECK: Prevent race conditions from rapid key presses
                // When shortcuts are pressed faster than 300ms apart, subsequent presses
                // are ignored to allow the first operation to complete properly
                if let lastTime = Self.lastToggleTime {
                    let timeSinceLastToggle = Date().timeIntervalSince(lastTime)
                    if timeSinceLastToggle < Self.debounceInterval {
                        AppLogger.ui.debug("🚫 Ignoring rapid toggle (pressed \(Int(timeSinceLastToggle * 1000))ms after previous)")
                        return
                    }
                }
                Self.lastToggleTime = Date()

                guard let audioManager = audioManager else {
                    AppLogger.ui.error("Error: AudioManager not available")
                    return
                }

                // Use the unified method with current mode from settings
                audioManager.toggleRecordingFromShortcut(trigger: .shortcut)
                AppLogger.ui.debug("⌨️ Toggled recording via keyboard shortcut")
            }
        }

        // MARK: Push to Talk
        // Logic moved to AudioRecordingManager
        audioManager.setupPushToTalk()

        // MARK: Change Mode Shortcut
        // Cycles through available modes regardless of recording state
        // Shows a brief toast notification to confirm the mode change
        KeyboardShortcuts.onKeyDown(for: .changeMode) {
            Task { @MainActor in
                self.appState.cycleToNextMode()
                ModeChangeToastManager.shared.show(modeName: self.appState.selectedModeName)
                AppLogger.ui.debug("🔄 Cycled to mode: \(self.appState.selectedModeName) via keyboard shortcut")
            }
        }

        // MARK: Start Streaming Shortcut
        // Dedicated shortcut for streaming transcription (Option+Shift+Space)
        // Uses the language configured in streaming settings (independent of modes)
        // Only works when streaming is enabled in settings
        KeyboardShortcuts.onKeyUp(for: .startStreaming) {
            Task { @MainActor in
                // Check if streaming is enabled - if not, ignore the shortcut
                guard self.settingsManager.streamingEnabled else {
                    AppLogger.ui.debug("📡 Streaming shortcut pressed but streaming is disabled")
                    return
                }

                // If already recording, toggle off
                if self.audioManager.isRecording {
                    self.audioManager.toggleRecordingFromShortcut(trigger: .streamingShortcut)
                    return
                }

                // Mark that this recording was triggered by streaming shortcut
                // RecordingTranscriptionFlow will check this flag and use streaming flow
                self.appState.isStreamingShortcutTriggered = true

                // Start recording (will use streaming flow due to isStreamingShortcutTriggered flag)
                self.audioManager.toggleRecordingFromShortcut(trigger: .streamingShortcut)
                let language = self.settingsManager.streamingLanguageEffective
                AppLogger.ui.info("📡 Started streaming transcription with language: \(language, privacy: .public)")
            }
        }

        // MARK: Quick Capture Shortcut
        // Records and routes the transcription to Apple Notes (new note).
        // No default key combo — the user must bind one in Settings → Shortcuts.
        // The feature is gated by `quickCaptureEnabled` so a stray binding from a
        // disabled state doesn't trigger.
        KeyboardShortcuts.onKeyUp(for: .quickCapture) { [weak settingsManager, weak audioManager] in
            Task { @MainActor in
                guard let settings = settingsManager, settings.quickCaptureEnabled else {
                    AppLogger.ui.debug("📝 Quick Capture shortcut pressed but feature is disabled")
                    return
                }
                guard let audioManager else {
                    AppLogger.ui.error("Quick Capture: AudioManager not available")
                    return
                }

                // Resolve the pinned mode. Empty sentinel / not-found returns nil,
                // which causes the flow to fall back to AppState's active mode
                // at the moment the shortcut fires. Uses the background fetch
                // helper to keep Core Data work off the main thread (see Sentry
                // HYPERWHISPER-KP).
                let storedId = settings.quickCaptureModeId
                let modeOverride: Mode?
                if storedId.isEmpty {
                    modeOverride = nil
                } else {
                    modeOverride = await PersistenceController.shared.fetchModeInBackground(withId: storedId)
                    if modeOverride == nil {
                        AppLogger.ui.warning("Quick Capture: pinned mode id \(storedId, privacy: .public) not found, falling back to current mode")
                    }
                }

                audioManager.toggleQuickCapture(modeOverride: modeOverride)
                AppLogger.ui.info("📝 Quick Capture toggled via shortcut (mode=\(modeOverride?.name ?? "current", privacy: .public))")
            }
        }

        // Attaching the onKeyUp handlers above registers each system-wide
        // hotkey as a side effect, even when its feature is off — which steals
        // the key combo from other apps. Release feature-gated hotkeys when
        // disabled, and keep OS-level registration in sync with the settings
        // toggles (settings change paths post .shortcutDidChange).
        syncFeatureGatedHotkeys()
        NotificationCenter.default.addObserver(
            forName: .shortcutDidChange,
            object: nil,
            queue: .main
        ) { _ in
            Task { @MainActor in
                self.syncFeatureGatedHotkeys()
            }
        }

        AppLogger.ui.debug("✅ Global hotkeys setup complete")
    }

    /// Last enabled-state applied per gated shortcut, so the sync only logs on
    /// real transitions rather than on every .shortcutDidChange post.
    private static var gatedHotkeyState: [KeyboardShortcuts.Name: Bool] = [:]

    /// Registers or unregisters feature-gated hotkeys at the OS level so a
    /// disabled feature doesn't hold its key combo hostage from other apps.
    /// The guards inside the onKeyUp handlers stay as a second line of defense.
    @MainActor
    private func syncFeatureGatedHotkeys() {
        let gated: [(name: KeyboardShortcuts.Name, isEnabled: Bool)] = [
            (.quickCapture, settingsManager.quickCaptureEnabled),
            (.startStreaming, settingsManager.streamingEnabled),
        ]
        for (name, isEnabled) in gated {
            if isEnabled {
                KeyboardShortcuts.enable(name)
            } else if !Self.comboIsShared(by: name) {
                // Carbon registration is keyed by key combo, not by name —
                // unregistering a combo shared with another shortcut would tear
                // that shortcut down too. Leave shared combos registered; the
                // handler guard ignores the press.
                KeyboardShortcuts.disable(name)
            }
            if Self.gatedHotkeyState[name] != isEnabled {
                Self.gatedHotkeyState[name] = isEnabled
                let action = isEnabled ? "registered" : "released"
                AppLogger.ui.debug("⌨️ Feature-gated hotkey \(name.rawValue, privacy: .public) \(action, privacy: .public)")
            }
        }
    }

    /// True when `name`'s key combo is also bound to another shortcut name.
    private static func comboIsShared(by name: KeyboardShortcuts.Name) -> Bool {
        guard let combo = KeyboardShortcuts.getShortcut(for: name) else { return false }
        return KeyboardShortcuts.Name.allCases.contains { other in
            other != name && KeyboardShortcuts.getShortcut(for: other) == combo
        }
    }
    
    /// Checks for app updates
    private func checkForUpdates() {
        appDelegate.checkForUpdates()
    }
    
    // Mode persistence now handled through Core Data
    
    // MARK: - Debug Menu Helpers
    
    /// Exports update logs to a file for support
    private func exportUpdateLogs() {
        guard let exportURL = UpdateLogger.shared.exportLogs() else {
            // Show error alert if export failed
            let alert = NSAlert()
            alert.messageText = "alerts.export.failed.title".localized
            alert.informativeText = "alerts.export.logs.failed.message".localized
            alert.alertStyle = .warning
            alert.runModal()
            return
        }
        
        // Open save panel for user to choose destination
        let savePanel = NSSavePanel()
        savePanel.nameFieldStringValue = exportURL.lastPathComponent
        savePanel.allowedContentTypes = [.plainText]
        savePanel.message = "panels.export.logs.message".localized
        
        savePanel.begin { response in
            if response == .OK, let destination = savePanel.url {
                do {
                    // Copy exported file to user's chosen location
                    try FileManager.default.copyItem(at: exportURL, to: destination)
                    
                    // Clean up temporary file
                    try? FileManager.default.removeItem(at: exportURL)
                    
                    // Show success alert
                    let successAlert = NSAlert()
                    successAlert.messageText = "alerts.export.logs.success.title".localized
                    successAlert.informativeText = "alerts.export.logs.success.message".localized
                    successAlert.alertStyle = .informational
                    successAlert.runModal()
                } catch {
                    // Show error alert
                    let alert = NSAlert()
                    alert.messageText = "alerts.export.failed.title".localized
                    alert.informativeText = "alerts.export.logs.saveFailed.message".localized(arguments: error.localizedDescription)
                    alert.alertStyle = .warning
                    alert.runModal()
                }
            }
        }
    }
    
    /// Exports all diagnostics (combines update logs and system logs)
    private func exportAllDiagnostics() {
        AppLogger.exportDiagnostics { exportURL in
            guard let exportURL = exportURL else {
                // Show error alert if export failed
                let alert = NSAlert()
                alert.messageText = "alerts.export.failed.title".localized
                alert.informativeText = "alerts.export.diagnostics.failed.message".localized
                alert.alertStyle = .warning
                alert.runModal()
                return
            }
            
            // Open save panel for user to choose destination
            let savePanel = NSSavePanel()
            savePanel.nameFieldStringValue = exportURL.lastPathComponent
            savePanel.allowedContentTypes = [.zip]
            savePanel.message = "panels.export.diagnostics.message".localized
            
            savePanel.begin { response in
                if response == .OK, let destination = savePanel.url {
                    do {
                        // Move exported file to user's chosen location
                        if FileManager.default.fileExists(atPath: destination.path) {
                            try FileManager.default.removeItem(at: destination)
                        }
                        try FileManager.default.moveItem(at: exportURL, to: destination)
                        
                        // Show success alert
                        let successAlert = NSAlert()
                        successAlert.messageText = "alerts.export.diagnostics.success.title".localized
                        successAlert.informativeText = "alerts.export.diagnostics.success.message".localized
                        successAlert.alertStyle = .informational
                        successAlert.runModal()
                    } catch {
                        // Show error alert
                        let alert = NSAlert()
                        alert.messageText = "alerts.export.failed.title".localized
                        alert.informativeText = "alerts.export.diagnostics.saveFailed.message".localized(arguments: error.localizedDescription)
                        alert.alertStyle = .warning
                        alert.runModal()
                    }
                } else {
                    // Clean up temporary file if user cancelled
                    try? FileManager.default.removeItem(at: exportURL)
                }
            }
        }
    }
}
