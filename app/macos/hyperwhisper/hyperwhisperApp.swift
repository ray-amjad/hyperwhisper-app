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
    
    /// Tracks whether this is the first launch (used for onboarding)
    /// TEMPORARILY DISABLED - Set to false to enable onboarding
    @AppStorage("hasCompletedOnboarding") private var hasCompletedOnboarding: Bool = true

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

    // One-time bootstrap flag to avoid duplicate initialization work
    @State private var didBootstrapModels: Bool = false

    /// Tracks whether the active recording session was initiated via Push to Talk.
    /// Prevents push-to-talk release handlers from interfering with manual recordings
    /// and allows us to handle key-up events even if the recording hasn't started yet.
    @State private var isPushToTalkSessionActive = false

    
    // MARK: - Initialization
    
    init() {
        let sharedLicenseManager = LicenseManager()
        _licenseManager = StateObject(wrappedValue: sharedLicenseManager)
        _hyperWhisperCloudManager = StateObject(wrappedValue: HyperWhisperCloudManager(licenseManager: sharedLicenseManager))

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

                    // Code that runs when the main window appears
                    handleMainWindowAppear()

                    // Initialize models and restore mode selection
                    Task {
                        await bootstrapModelsOnce()
                        await MainActor.run {
                            initializeSelectedModeLightweight()
                        }
                    }
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
    
    /// Handles initial setup when the main window appears
    private func handleMainWindowAppear() {
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

        // Ensure the app is activated so global event monitors initialize properly.
        // Without this, KeyboardShortcuts' NSEvent monitors don't receive events
        // when the app launches as a login item with .accessory activation policy.
        NSApp.activate(ignoringOtherApps: true)

        // Set up global hotkeys (must happen while app is activated)
        // These work even when the app isn't focused
        setupGlobalHotkeys()

        // LAUNCH MINIMIZED: Hide main window if preference is set
        // This allows the app to run in menu bar only mode by default
        // Users can still access the window via Menu Bar > Settings
        if launchMinimized && hasCompletedOnboarding {
            // Delay to ensure window is fully created and event monitors initialize before hiding
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                if let mainWindow = NSApplication.shared.windows.first {
                    mainWindow.orderOut(nil)
                    AppLogger.ui.debug("🪟 Main window hidden on launch (launchMinimized enabled)")
                    // Return focus to previous app after hiding our window
                    NSApp.deactivate()
                }
            }
        }

        // Show onboarding if this is the first launch
        // The hasCompletedOnboarding check is done here to trigger the sheet
        if !hasCompletedOnboarding {
            // Small delay to ensure the main window is ready before showing sheet
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                appState.showOnboarding = true
            }
        }

        // Note: Microphone permission is now requested only when user tries to record
        // This prevents the permission dialog from appearing on every app launch

        // Initialize Sparkle updater based on user's auto-update setting
        // This syncs Sparkle's state with the UserDefaults setting and performs
        // an immediate background check if auto-update is enabled
        appDelegate.initializeUpdater()

        // Prepare recordings folder with a user-friendly permission flow
        settingsManager.prepareRecordingsFolderIfNeeded()

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

            // Explicitly prepare the model — the Combine $selectedModeId sink may not
            // fire (due to .removeDuplicates or pipeline being nil during init)
            Task { @MainActor in
                await transcriptionPipeline.prepareModel(for: mode)
                await transcriptionPipeline.prepareLocalRuntime(for: mode)
            }
        }
    }

    // DEPRECATED: Old initializer that performed redundant rescans and preloading.
    // Left temporarily for reference; no longer invoked.
    // private func initializeSelectedMode() { ... }

    /// Perform one-time model bootstrap work on startup.
    /// Moves heavy installation/extraction off the main thread and avoids redundant rescans.
    @MainActor
    private func bootstrapModelsOnce() async {
        guard !didBootstrapModels else { return }
        didBootstrapModels = true
        // Models are downloaded on demand when needed
        // TranscriptionPipeline updates in response to download changes
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
