//
//  MainAppView.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  MAIN APP VIEW
//  The primary view of the application containing the sidebar navigation
//  and content area. Uses NavigationSplitView for a standard macOS layout.
//

import SwiftUI
import AppKit
import KeyboardShortcuts
import CoreData
import os

/// Logger for MainAppView (static to work with SwiftUI structs)
private let mainAppViewLogger = Logger(subsystem: "com.hyperwhisper.app", category: "MainAppView")

// MARK: - Main App View

/// Root view containing sidebar and content area
struct MainAppView: View {
    // MARK: - Environment Objects
    
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var licenseManager: LicenseManager
    @EnvironmentObject var whisperModelManager: WhisperModelManager
    
    // MARK: - State
    
    /// Search text for filtering (if needed)
    @State private var searchText: String = ""
    /// Controls the visibility of the split view columns
    @State private var columnVisibility: NavigationSplitViewVisibility = .doubleColumn
    
    // MARK: - Body
    
    var body: some View {
        ZStack {
            // Global blur across the whole window (ignores safe area)
            // so the hidden titlebar region under the traffic lights is blurred too.
            VisualEffectBackground()
                .ignoresSafeArea()

            NavigationSplitView(columnVisibility: $columnVisibility) {
                // MARK: Sidebar
                sidebar
            } detail: {
                // MARK: Content Area
                VStack(spacing: 0) {
                    NavigationStack {
                        // Content view based on selection
                        contentView
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .background(VisualEffectBackground())
                    }

                    ModelStatusBar()
                }
            }
        }
        .navigationSplitViewStyle(.balanced)
        // Monitor recording state changes and manage floating window
        .onChange(of: audioManager.isRecording) { _, isRecording in
            mainAppViewLogger.debug("📊 Recording state changed: \(isRecording, privacy: .public)")
            // Show dialog when recording starts, hide when it stops
            if isRecording {
                mainAppViewLogger.debug("📊 Should show recording dialog")
                appState.showRecordingDialog = true
            } else {
                // Do not close here; keep the dialog visible for loading/transcription/results
            }
        }
        .onChange(of: appState.showRecordingDialog) { oldValue, newValue in
            mainAppViewLogger.debug("📊 showRecordingDialog changed: \(oldValue, privacy: .public) → \(newValue, privacy: .public)")
            if newValue {
                mainAppViewLogger.debug("🔍 Opening recording dialog window")
                Task { @MainActor in
                    RecordingWindowManager.shared.open(
                        appState: appState,
                        audioManager: audioManager,
                        transcriptionPipeline: transcriptionPipeline,
                        settingsManager: settingsManager
                    )
                }
            } else {
                mainAppViewLogger.debug("🔍 Closing recording dialog window")
                Task { @MainActor in
                    RecordingWindowManager.shared.close()
                }
            }
        }
        
        // MARK: Toolbar
        .toolbar {
            // Toolbar kept minimal for clean design
        }
        
        // MARK: Alert for Errors
        .alert("common.error".localized, isPresented: $appState.showErrorAlert) {
            Button {
                appState.showErrorAlert = false
            } label: {
                Text(localized: "common.ok")
            }
        } message: {
            Text(appState.errorMessage ?? "app.unknown.error".localized)
        }
        
        // MARK: Alert for Microphone Permission Denied
        .alert("alerts.microphone.permission.title".localized, isPresented: $audioManager.showPermissionDeniedAlert) {
            Button {
                // Open System Settings to Privacy & Security > Microphone
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") {
                    NSWorkspace.shared.open(url)
                }
                audioManager.showPermissionDeniedAlert = false
            } label: {
                Text(localized: "home.open.system.settings")
            }
            Button(role: .cancel) {
                audioManager.showPermissionDeniedAlert = false
            } label: {
                Text(localized: "common.cancel")
            }
        } message: {
            Text("app.microphone.permission.message".localized)
        }
        
        // MARK: Alert for API Key Setup (Generalized for all providers)
        .alert(appState.apiKeyAlertTitle, isPresented: $appState.showAPIKeyAlert) {
            Button {
                // Navigate to Settings view
                appState.selectedNavigationItem = .settings
                appState.showAPIKeyAlert = false
                // Clear missing keys state when alert is dismissed
                appState.missingAPIKeys = []
            } label: {
                Text(localized: "common.open.settings")
            }
            
            // Show "Use Local Mode" option for new installs that have the local default mode
            if appState.showLocalModeSuggestion {
                Button {
                    appState.switchToLocalDefault()
                    // Clear missing keys state when switching to local
                    appState.missingAPIKeys = []
                } label: {
                    Text(localized: "alerts.api.local.mode")
                }
            }
            
            Button(role: .cancel) {
                appState.showAPIKeyAlert = false
                // Clear missing keys state when alert is dismissed
                appState.missingAPIKeys = []
            } label: {
                Text(localized: "common.cancel")
            }
        } message: {
            Text(appState.apiKeyAlertMessage)
        }

        // MARK: Alert explaining Documents access
        .alert("alerts.documents.permission.title".localized, isPresented: $settingsManager.showDocumentsPermissionAlert) {
            Button {
                settingsManager.proceedWithDocumentsAccess()
            } label: {
                Text(localized: "common.continue")
            }
            Button(role: .cancel) {
                settingsManager.useAlternateStorageInstead()
            } label: {
                Text(localized: "alerts.documents.permission.use.another")
            }
        } message: {
            Text("app.recordings.location.message".localized)
        }
        

        // MARK: Keyboard Shortcuts
        .onAppear {
            setupKeyboardShortcuts()
        }
        
        // MARK: Onboarding Sheet
        .sheet(isPresented: $appState.showOnboarding) {
            OnboardingView(isPresented: $appState.showOnboarding)
                .environmentObject(appState)
                .environmentObject(audioManager)
                .environmentObject(transcriptionPipeline)
                .environmentObject(settingsManager)
                .environmentObject(whisperModelManager)
                .environmentObject(licenseManager)
                .interactiveDismissDisabled() // Prevent accidental dismissal
        }
    }
    
    // MARK: - Visible Navigation Items

    private var visibleNavigationItems: [NavigationItem] {
        NavigationItem.allCases
    }

    // MARK: - Content View

    @ViewBuilder
    private var contentView: some View {
        switch appState.selectedNavigationItem {
        case .home:
            HomeView()
        case .modes:
            ModesView()
        case .vocabulary:
            VocabularyView()
        case .modelLibrary:
            ModelLibraryView()
        case .streaming:
            StreamingView()
        case .history:
            HistoryView()
        case .settings:
            SettingsView()
        }
    }
    
    // MARK: - Keyboard Shortcuts
    
    private func setupKeyboardShortcuts() {
        // This would integrate with the HotKey library for global shortcuts
        mainAppViewLogger.debug("Setting up keyboard shortcuts...")
    }
    
    // MARK: - Recording Dialog Window Management
    
    private func openRecordingDialogWindow() {
        mainAppViewLogger.info("🪟 Opening recording dialog window...")

        // Respect user's preference to hide the recording window
        if !settingsManager.showRecordingWindow {
            mainAppViewLogger.info("🪟 Recording window is disabled by settings; skipping window creation")
            return
        }
        
        let recordingDialogTitle = "recording.dialog.window.title".localized

        // First, close any existing recording dialog windows to prevent duplicates
        let existingWindows = NSApplication.shared.windows.filter { window in
            window.title == recordingDialogTitle
        }
        
        if !existingWindows.isEmpty {
            mainAppViewLogger.debug("🪟 Found \(existingWindows.count, privacy: .public) existing recording dialog window(s), closing them first...")
            for window in existingWindows {
                window.close()
            }
        }

        // Always create a fresh window to avoid frozen/stale windows
        mainAppViewLogger.debug("🪟 Creating new recording dialog panel...")
        
        // Create a binding to showRecordingDialog
        let showBinding = Binding<Bool>(
            get: { self.appState.showRecordingDialog },
            set: { self.appState.showRecordingDialog = $0 }
        )
        
        // Create new window controller for the recording dialog
        let recordingView = RecordingDialog(isPresented: showBinding)
            .environmentObject(audioManager)
            .environmentObject(appState)
            .environmentObject(settingsManager)
            .environmentObject(transcriptionPipeline)
            .environment(\.managedObjectContext, PersistenceController.shared.container.viewContext)
        
        let hostingController = NSHostingController(rootView: recordingView)
        let panel = NSPanel(contentViewController: hostingController)

        panel.title = recordingDialogTitle
        
        // WINDOW STYLE CONFIGURATION:
        // .nonactivatingPanel - Prevents the panel from becoming the key window
        //                      This allows it to stay visible without stealing focus
        //                      from the user's current application
        panel.styleMask = [.nonactivatingPanel]
        
        // WINDOW LEVEL - CRITICAL FOR ALWAYS-ON-TOP BEHAVIOR:
        // We use .screenSaver level for the strongest always-on-top behavior
        // Window levels (from lowest to highest):
        // - .normal: Regular windows
        // - .floating: Above normal windows (e.g., inspectors)
        // - .modalPanel: Modal dialogs
        // - .popUpMenu: Popup menus and strong always-on-top windows
        // - .screenSaver: Above everything including full-screen apps
        //
        // .screenSaver ensures the recording dialog stays above ALL windows,
        // including full-screen applications like IDEs
        panel.level = .screenSaver
        
        // COLLECTION BEHAVIOR - WINDOW MANAGEMENT:
        // .canJoinAllSpaces - Window appears on all Mission Control spaces/desktops
        // .fullScreenAuxiliary - Can appear alongside full-screen apps
        // .stationary - Window doesn't move when spaces change
        // .ignoresCycle - Window is not included in the window cycling order (Cmd+`)
        // Together these ensure the recording dialog is always accessible
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        
        // VISUAL PROPERTIES:
        panel.isMovableByWindowBackground = true  // User can drag the window by its background
        panel.backgroundColor = .clear            // Transparent background for visual effects
        panel.isOpaque = false                   // Allow translucency
        panel.hasShadow = true                   // Drop shadow for depth
        panel.isFloatingPanel = true             // Mark as floating panel
        
        // DO NOT HIDE ON DEACTIVATE - CRITICAL
        // This ensures the panel stays visible even when the app is not in focus
        panel.hidesOnDeactivate = false
        
        panel.becomesKeyOnlyIfNeeded = true      // Only become key window when necessary
        
        panel.setContentSize(NSSize(width: 680, height: 320))
        panel.center()

        // WINDOW ACTIVATION STRATEGY:
        // We need to make the panel visible WITHOUT activating the entire app
        // This prevents the main window from appearing when using shortcuts
        //
        // Use orderFrontRegardless() to show the panel without app activation
        // This method forces the window to the front regardless of which app is active
        // and crucially does NOT activate the app or show other windows
        panel.orderFrontRegardless()
        
        // The panel is now visible and can receive events without showing the main window
        // No need to hide other windows since they won't appear

        mainAppViewLogger.info("🪟 Recording dialog panel created and shown")
    }
    
    private func closeRecordingDialogWindow() {
        // Close ALL recording dialog windows (not just the first one)
        // This prevents orphaned/frozen windows
        DispatchQueue.main.async {
            let recordingDialogTitle = "recording.dialog.window.title".localized
            let recordingWindows = NSApplication.shared.windows.filter { window in
                window.title == recordingDialogTitle
            }
            
            // Close all matching windows
            for window in recordingWindows {
                window.close()
            }
            
            if !recordingWindows.isEmpty {
                mainAppViewLogger.debug("🪟 Closed \(recordingWindows.count, privacy: .public) recording dialog window(s)")
            }
        }
    }
}

// MARK: - Sidebar View

extension MainAppView {
    var sidebar: some View {
        VStack(spacing: 0) {
            // App header
            VStack(spacing: 6) {
                HStack(spacing: 8) {
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 6) {
                            Text(localized: "app.title")
                                .font(.system(size: 16, weight: .semibold))
                        }
                    }
                    
                    Spacer()
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
                
                Divider()
                    .opacity(0.5)
            }
            
            // Navigation items
            ScrollView {
                VStack(spacing: 1) {
                    ForEach(visibleNavigationItems) { item in
                        NavigationButton(
                            item: item,
                            isSelected: appState.selectedNavigationItem == item
                        ) {
                            appState.selectedNavigationItem = item
                        }
                    }
                }
                .padding(10)
                .background(
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(.thinMaterial)
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .stroke(Color.primary.opacity(0.06), lineWidth: 0.5)
                )
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
            }
            
            Spacer()
            
            // Bottom section
            VStack(alignment: .leading, spacing: 6) {
                Divider()
                    .opacity(0.5)

                // Show upgrade button/pro status for all users
                UpgradeSidebarCTA()
                    .environmentObject(licenseManager)
                    .environmentObject(appState)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 12)
            }
        }
        .frame(minWidth: 220, idealWidth: 260, maxWidth: 300, maxHeight: .infinity, alignment: .top)
        .background(VisualEffectBackground())
    }
    
    /// Check for app updates
    private func checkForUpdates() {
        (NSApp.delegate as? AppDelegate)?.checkForUpdates()
    }
}

// MARK: - Navigation Button

struct NavigationButton: View {
    let item: NavigationItem
    let isSelected: Bool
    let action: () -> Void
    
    @State private var isHovering = false
    
    var body: some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: item.icon)
                    .font(.system(size: 14, weight: .medium))
                    .foregroundStyle(iconColor)
                    .frame(width: 20)
                
                Text(item.localizedTitle)
                    .font(.system(size: 13, weight: isSelected ? .medium : .regular))
                    .foregroundColor(textColor)
                
                Spacer()
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(backgroundView)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(item.helpText)
        .onHover { hovering in
            withAnimation(.easeInOut(duration: 0.15)) {
                isHovering = hovering
            }
        }
    }
    
    private var iconColor: some ShapeStyle {
        if isSelected {
            return AnyShapeStyle(.linearGradient(
                colors: [.white.opacity(0.95), .white.opacity(0.85)],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            ))
        } else {
            return AnyShapeStyle(Color.accentColor.opacity(isHovering ? 0.9 : 0.7))
        }
    }
    
    private var textColor: Color {
        if isSelected {
            return .white
        } else {
            return .primary.opacity(isHovering ? 1 : 0.85)
        }
    }
    
    @ViewBuilder
    private var backgroundView: some View {
        if isSelected {
            RoundedRectangle(cornerRadius: 8)
                .fill(.linearGradient(
                    colors: [Color.accentColor, Color.accentColor.opacity(0.8)],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                ))
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(.white.opacity(0.1), lineWidth: 0.5)
                )
                .shadow(color: Color.accentColor.opacity(0.3), radius: 3, x: 0, y: 1)
        } else if isHovering {
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.primary.opacity(0.06))
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(Color.primary.opacity(0.05), lineWidth: 0.5)
                )
        }
    }
}

// MARK: - Upgrade Sidebar CTA

/// Compact blurred/translucent CTA for upgrading to Pro in the sidebar footer
struct UpgradeSidebarCTA: View {
    @EnvironmentObject var licenseManager: LicenseManager
    @EnvironmentObject var appState: AppState
    @State private var isHovering = false

    var body: some View {
        // Show different content based on license status
        if licenseManager.licenseStatus == .active {
            // PRO STATUS INDICATOR (for licensed users)
            HStack(spacing: 8) {
                Image(systemName: "checkmark.seal.fill")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(.green.opacity(0.8))
                
                Text(localized: "app.title")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(.primary.opacity(0.8))
                
                Text(localized: "app.badge.pro")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundColor(.primary.opacity(0.9))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(
                        Capsule()
                            .fill(.ultraThinMaterial)
                    )
                    .overlay(
                        Capsule()
                            .strokeBorder(.white.opacity(0.1), lineWidth: 0.5)
                    )
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity)
            .background(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(.ultraThinMaterial)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5)
            )
        } else {
            // CLOUD CREDITS CTA (for users without an active Cloud license)
            // Local transcription is free & unlimited (open source); this is a
            // discoverable path to HyperWhisper Cloud (paid, credit-based).
            Button(action: {
                // Navigate to the combined Cloud account / credits panel.
                appState.selectedNavigationItem = .settings
                appState.selectedSettingsSection = "license"
            }) {
                HStack(spacing: 6) {
                    Image(systemName: "cloud.fill")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundColor(.primary.opacity(0.7))

                    Text(localized: "app.cloud.cta.title")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(.primary.opacity(0.85))

                    Spacer(minLength: 0)

                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundColor(.primary.opacity(0.5))
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .frame(maxWidth: .infinity)
                .background(
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(.ultraThinMaterial)
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .strokeBorder(
                            isHovering ?
                            Color.primary.opacity(0.15) :
                            Color.primary.opacity(0.08),
                            lineWidth: 0.5
                        )
                )
                .scaleEffect(isHovering ? 1.02 : 1.0)
                .shadow(
                    color: .black.opacity(isHovering ? 0.15 : 0.1),
                    radius: isHovering ? 8 : 4,
                    x: 0,
                    y: 2
                )
            }
            .buttonStyle(.plain)
            .onHover { hovering in
                withAnimation(.spring(response: 0.25, dampingFraction: 0.9)) {
                    isHovering = hovering
                }
            }
            .accessibilityLabel(Text(localized: "accessibility.cloud.cta"))
        }
    }
}


// MARK: - Recording Button

/// Floating recording button for the toolbar
struct RecordingButton: View {
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var cloudProviderHealthManager: CloudProviderHealthManager
    @State private var isHovering = false
    
    private struct ProviderGate {
        enum Kind { case transcription(CloudProvider), postProcessing(PostProcessingProvider) }
        let kind: Kind
        let status: ProviderHealth

        var shouldBlock: Bool { status.shouldBlockTranscription }

        var description: String {
            switch kind {
            case .transcription(let provider):
                return provider.displayName
            case .postProcessing(let provider):
                return provider.displayName
            }
        }
    }

    private var providerGates: [ProviderGate] {
        guard !audioManager.isRecording,
              let mode = appState.selectedModeSnapshot else { return [] }

        var gates: [ProviderGate] = []

        if mode.model.lowercased() == "cloud",
           let providerRaw = mode.rawCloudProvider,
           let provider = CloudProvider(rawValue: providerRaw) {
            let status = cloudProviderHealthManager.status(for: provider)
            gates.append(.init(kind: .transcription(provider), status: status))
        }

        let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
        if processingMode.requiresHealthCheck,
           let postProviderId = mode.rawPostProcessingProvider ?? processingMode.defaultProvider?.rawValue,
           let postProvider = PostProcessingProvider(rawValue: postProviderId) {
            let status = cloudProviderHealthManager.status(for: postProvider)
            gates.append(.init(kind: .postProcessing(postProvider), status: status))
        }

        if processingMode == .local {
            let status = cloudProviderHealthManager.status(for: .localLLM)
            gates.append(.init(kind: .postProcessing(.localLLM), status: status))
        }

        return gates
    }

    private func disableReason(from gates: [ProviderGate]) -> String? {
        guard let blocker = gates.first(where: { $0.shouldBlock }) else {
            if let checkingGate = gates.first(where: { $0.status == .checking }) {
                return "recording.button.disable.checking".localized(arguments: checkingGate.description)
            }
            return nil
        }

        switch blocker.status {
        case .unauthorized:
            return "recording.button.disable.invalid".localized(arguments: blocker.description)
        case .unreachable:
            return "recording.button.disable.unreachable".localized(arguments: blocker.description)
        case .unknown:
            return "recording.button.disable.missing".localized(arguments: blocker.description)
        case .checking:
            return "recording.button.disable.checking".localized(arguments: blocker.description)
        case .healthy:
            return nil
        case .notInstalled:
            return "recording.button.disable.install.local".localized
        }
    }
    
    var body: some View {
        let gates = providerGates
        let shouldDisable = gates.contains { $0.shouldBlock }
        let reason = disableReason(from: gates)

        return Button(action: {
            // Toggle recording which will trigger the dialog via onChange
            audioManager.toggleRecordingWithTranscription(trigger: .uiButton)
        }) {
            HStack(spacing: 8) {
                Image(systemName: audioManager.isRecording ? "stop.circle.fill" : "mic.circle.fill")
                    .font(.system(size: 18))
                    .symbolRenderingMode(.hierarchical)
                
                let titleKey = audioManager.isRecording ? "recording.button.stop" : "recording.button.start"
                Text(LocalizedStringKey(titleKey))
                    .font(.system(size: 13, weight: .medium))
            }
            .foregroundColor(audioManager.isRecording ? .white : (shouldDisable ? .secondary : .primary))
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(audioManager.isRecording ? 
                          Color.red :
                          (shouldDisable ? Color.accentColor.opacity(0.4) : Color.accentColor.opacity(isHovering ? 1 : 0.9))
                    )
            )
            .shadow(radius: audioManager.isRecording ? 4 : 2)
        }
        .buttonStyle(.plain)
        .disabled(shouldDisable)
        .help(reason ?? "menu.bar.recording.help".localized)
        .onHover { hovering in
            withAnimation(.easeInOut(duration: 0.2)) {
                isHovering = shouldDisable ? false : hovering
            }
        }
    }
}

// MARK: - Optional Keyboard Shortcut Modifier

/// A view modifier that conditionally applies a keyboard shortcut only when provided.
/// This allows menu items to show no shortcut when nil, instead of a hardcoded default.
struct OptionalKeyboardShortcut: ViewModifier {
    let shortcut: SwiftUI.KeyboardShortcut?

    func body(content: Content) -> some View {
        if let shortcut {
            content.keyboardShortcut(shortcut)
        } else {
            content
        }
    }
}

// MARK: - Menu Bar Button Style

/// Custom button style for menu bar items with hover effect
struct MenuBarButtonStyle: ButtonStyle {
    @State private var isHovered = false
    
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(
                RoundedRectangle(cornerRadius: 4)
                    .fill(isHovered ? Color.accentColor : Color.clear)
            )
            .foregroundColor(isHovered ? .white : .primary)
            .onHover { hovering in
                isHovered = hovering
            }
    }
}

// MARK: - Menu Bar Content

/// Content view for the menu bar extra
struct MenuBarContentView: View {
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var settingsManager: SettingsManager
    
    var body: some View {
        MenuBarItems()
            // Mirror the same Documents explanation alert in the menu bar popover
            .alert("alerts.documents.permission.title".localized, isPresented: $settingsManager.showDocumentsPermissionAlert) {
                Button {
                    settingsManager.proceedWithDocumentsAccess()
                } label: {
                    Text(localized: "common.continue")
                }
                Button(role: .cancel) {
                    settingsManager.useAlternateStorageInstead()
                } label: {
                    Text(localized: "alerts.documents.permission.use.another")
                }
            } message: {
                Text("app.recordings.location.message".localized)
            }
    }
}

// MARK: - Menu Bar Components

/// Main menu items
struct MenuBarItems: View {
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var cloudProviderHealthManager: CloudProviderHealthManager
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject var licenseManager: LicenseManager
    @Environment(\.openWindow) private var openWindow
    @State private var selectedMicrophone: String = "menu.microphone.default".localized
    // SHORTCUT DISPLAY REFRESH TRIGGER:
    // MenuBarExtra content doesn't reliably re-render when @State changes via notifications.
    // This trigger forces view identity to change when shortcuts are updated, ensuring
    // the menu always shows the current shortcut configuration.
    @State private var shortcutRefreshTrigger = false

    // SwiftUI keyboard shortcut derived from the user's configured global hotkey.
    private var shouldDisableRecording: Bool {
        guard !audioManager.isRecording else { return false }
        guard let mode = appState.selectedModeSnapshot else { return false }
        if mode.model.lowercased() == "cloud",
           let providerRaw = mode.rawCloudProvider,
           let provider = CloudProvider(rawValue: providerRaw),
           cloudProviderHealthManager.status(for: provider).shouldBlockTranscription {
            return true
        }

        let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
        if processingMode.requiresHealthCheck,
           let postProviderId = mode.rawPostProcessingProvider ?? processingMode.defaultProvider?.rawValue,
           let postProvider = PostProcessingProvider(rawValue: postProviderId),
           cloudProviderHealthManager.status(for: postProvider).shouldBlockTranscription {
            return true
        }

        if processingMode == .local,
           cloudProviderHealthManager.status(for: .localLLM).shouldBlockTranscription {
            return true
        }

        return false
    }

    // This controls only how the glyphs render on the right side of the menu item
    // and which in-app combo will trigger the action while the menu is open.
    // The actual global key registration is handled in hyperwhisperApp.setupGlobalHotkeys().
    @State private var menuKeyboardShortcut: SwiftUI.KeyboardShortcut? = nil

    // SHORTCUT HINT DISPLAY LOGIC:
    // Determines what shortcut hint to show on the "Start/Stop Recording" menu item.
    // Priority order:
    // 1. If a toggle recording shortcut is configured → show that shortcut via .keyboardShortcut()
    // 2. If Push-to-Talk is enabled (no toggle shortcut) → show PTT key hint as text (e.g., "Hold FN")
    // 3. Neither configured → show no shortcut hint
    @State private var pttHintText: String? = nil

    /// Updates the menu shortcut display based on current configuration.
    /// Called on appear and when shortcuts change.
    private func updateShortcutDisplay() {
        // First, check if there's a configured toggle recording shortcut
        if let shortcut = KeyboardShortcuts.getShortcut(for: .toggleRecordingWithTranscription),
           let keyboardShortcut = makeKeyboardShortcut(from: shortcut) {
            // User has a valid toggle shortcut configured - show it
            menuKeyboardShortcut = keyboardShortcut
            pttHintText = nil
        } else if settingsManager.pushToTalkMode != .disabled {
            // No toggle shortcut, but Push-to-Talk is enabled - show PTT hint
            menuKeyboardShortcut = nil
            pttHintText = pttModeHintText(for: settingsManager.pushToTalkMode)
        } else {
            // Neither configured - show nothing
            menuKeyboardShortcut = nil
            pttHintText = nil
        }

        // FORCE VIEW REFRESH:
        // Toggle the refresh trigger to force SwiftUI to re-evaluate the view.
        // MenuBarExtra content doesn't always re-render when @State changes via
        // notifications, so changing view identity ensures the UI updates.
        shortcutRefreshTrigger.toggle()
    }

    /// Returns a user-friendly hint text for the given Push-to-Talk mode.
    /// Example: "Hold FN" for .fn mode, "Hold ⌃" for .control mode
    private func pttModeHintText(for mode: PushToTalkMode) -> String? {
        switch mode {
        case .disabled:
            return nil
        case .fn:
            return "Hold FN"
        case .control:
            return "Hold ⌃"
        case .leftOption:
            return "Hold ⌥"
        case .rightOption:
            return "Hold ⌥"
        case .fnControl:
            return "Hold FN+⌃"
        case .fnOption:
            return "Hold FN+⌥"
        case .custom:
            // For custom PTT shortcut, try to get its description
            if let shortcut = KeyboardShortcuts.getShortcut(for: .pushToTalk) {
                return "Hold \(shortcut.description)"
            }
            return nil
        }
    }

    var body: some View {
        Group {
            // Start/Stop Recording (show current shortcut dynamically)
            // MENU ITEM SHORTCUT DISPLAY:
            // - If toggle shortcut is set: uses .keyboardShortcut() modifier for native display
            // - If PTT is enabled without toggle shortcut: shows hint text in label (e.g., "Hold FN")
            // - Neither: shows no shortcut hint
            Button {
                // Use the unified method with the selected mode
                // Use the selected mode from appState (single source of truth)
                audioManager.toggleRecordingWithTranscription(mode: appState.selectedModeName, trigger: .uiButton)
            } label: {
                let baseTitle = audioManager.isRecording ? "menu.recording.stop".localized : "menu.recording.toggle".localized
                if let hint = pttHintText, menuKeyboardShortcut == nil {
                    // Show PTT hint as right-aligned text when no keyboard shortcut is configured
                    HStack {
                        Text(baseTitle)
                        Spacer()
                        Text(hint)
                            .foregroundStyle(.secondary)
                    }
                } else {
                    Text(baseTitle)
                }
            }
            .disabled(shouldDisableRecording)
            // Display the user's configured shortcut on the right in the menu (only if set).
            // Note: This does not register a new global shortcut; it mirrors the
            // user's setting so the menu UI stays in sync with KeyboardShortcuts.
            .modifier(OptionalKeyboardShortcut(shortcut: menuKeyboardShortcut))
            // FORCE VIEW IDENTITY CHANGE:
            // When shortcutRefreshTrigger toggles, SwiftUI treats this as a new view,
            // ensuring the menu item fully re-renders with updated shortcut display.
            // This fixes MenuBarExtra not updating when shortcuts change via notifications.
            .id(shortcutRefreshTrigger)
            .onAppear {
                // Read the current global shortcut and convert it to SwiftUI's
                // KeyboardShortcut so the menu shows the right glyphs.
                updateShortcutDisplay()

                // Load saved microphone selection
                restoreSavedMicrophoneIfNeeded(devices: audioManager.availableDevices)

                // Mode is already loaded in appState from settings
            }
            // Keep menu glyphs updated if the user changes the shortcut in Settings
            .onReceive(NotificationCenter.default.publisher(for: .shortcutDidChange)) { _ in
                updateShortcutDisplay()
            }
            .onReceive(audioManager.$availableDevices) { devices in
                restoreSavedMicrophoneIfNeeded(devices: devices)
            }
            .onReceive(audioManager.$selectedDevice) { device in
                if let device {
                    selectedMicrophone = device.name
                } else {
                    // No explicit selection - show the system default device name
                    selectedMicrophone = audioManager.activeInputDeviceName
                }
            }
            
            Divider()
            
            // History
            Button {
                openMainWindow()
                appState.selectedNavigationItem = .history
            } label: {
                Text(localized: "menu.history")
            }
            
            // Settings
            Button {
                openMainWindow()
                appState.selectedNavigationItem = .settings
            } label: {
                Text(localized: "menu.settings")
            }
            // Remove shortcut from menu bar item; access via main app menu instead
            
            Divider()
            
            // Microphone submenu
            // Shows all available input devices with "(Default)" suffix on the system default device
            Menu {
                ForEach(audioManager.availableDevices, id: \.self) { device in
                    // Check if this device is the system's default input device
                    let isSystemDefault = device.uid == audioManager.systemDefaultDeviceUID
                    // Show checkmark if this device is explicitly selected, OR if no device is
                    // selected and this is the system default (which will be used for recording)
                    let isSelected = audioManager.selectedDevice?.id == device.id ||
                                     (audioManager.selectedDevice == nil && isSystemDefault)
                    Button(action: {
                        audioManager.selectDevice(device)
                        selectedMicrophone = device.name
                        // Update the saved preference
                        settingsManager.selectedMicrophoneId = device.id
                    }) {
                        HStack {
                            // Show "(Default)" suffix for the macOS system default input device
                            Text(isSystemDefault ? "\(device.name) (\("menu.microphone.system.default".localized))" : device.name)
                            if isSelected {
                                Spacer()
                                Image(systemName: "checkmark")
                            }
                        }
                    }
                }
            } label: {
                Text(localized: "menu.microphone")
            }
            
            // Select Mode submenu - dynamically show all available modes.
            // Reads AppState's cached, sorted modes instead of calling
            // fetchAllModes() (a synchronous viewContext fetch). SwiftUI
            // re-evaluates this Menu builder repeatedly during rapid state
            // changes — e.g. the burst of @Published updates at recording
            // start — and a DB fetch here lands a "SELECT 'Mode' SORT BY
            // sortOrder" query on the main thread inside the "Recording
            // Start" transaction. Fixes Sentry HYPERWHISPER-R0.
            Menu {
                let allModes = appState.cachedSortedModeSnapshots
                ForEach(allModes, id: \.id) { mode in
                    Button(action: {
                        // Update app state and settings
                        appState.selectMode(mode, persist: true)
                    }) {
                        HStack {
                            Text(mode.name)
                                .fixedSize() // Prevent text truncation in menu
                            if appState.selectedModeId == mode.id.uuidString {
                                Spacer()
                                Image(systemName: "checkmark")
                            }
                        }
                    }
                }
            } label: {
                Text(localized: "menu.select.mode")
            }

            // MARK: - Transcribe File Submenu
            // Opens file picker and transcribes selected audio file with the chosen mode
            // Dynamically shows all available modes as submenu items.
            // Uses AppState's cached sorted modes rather than a synchronous
            // fetchAllModes() — see the Select Mode submenu above and Sentry
            // HYPERWHISPER-R0 (DB on main thread during Recording Start).
            Menu {
                let allModes = appState.cachedSortedModeSnapshots
                ForEach(allModes, id: \.id) { mode in
                    Button(action: {
                        transcribeFile(with: mode)
                    }) {
                        Text(mode.name)
                            .fixedSize() // Prevent text truncation in menu
                    }
                }
            } label: {
                Text(localized: "menu.transcribe.file")
            }

            Divider()

            // MARK: - Resources Section
            // External links to help center and feedback portal
            Button {
                if let url = URL(string: "https://hyperwhisper.com/docs") {
                    NSWorkspace.shared.open(url)
                }
            } label: {
                Label("settings.resources.help.center".localized, systemImage: "link")
            }

            Button {
                if let url = URL(string: "https://www.hyperwhisper.com/en/support") {
                    NSWorkspace.shared.open(url)
                }
            } label: {
                Label("settings.resources.contact.support".localized, systemImage: "link")
            }

            Button {
                if let url = URL(string: "https://hyperwhisper.userjot.com") {
                    NSWorkspace.shared.open(url)
                }
            } label: {
                Label("settings.resources.feedback".localized, systemImage: "link")
            }

            Divider()

            // Version
            let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0.0"
            Text("menu.version.label".localized(arguments: version))
                .foregroundColor(.secondary)


            // Quit
            Button {
                NSApplication.shared.terminate(nil)
            } label: {
                Text(localized: "common.quit")
            }
        }
    }
    
    /// Opens the main window and brings the app to front
    private func openMainWindow() {
        // Activate the app
        NSApp.activate(ignoringOtherApps: true)

        // STEP 1: Check the stored window reference first (most reliable)
        // This avoids timing issues where the window exists but hasn't had its identifier set yet
        if let mainWindow = MainWindowStore.window {
            mainWindow.makeKeyAndOrderFront(nil)
            return
        }

        // STEP 2: Fallback - search by identifier
        if let mainWindow = NSApplication.shared.windows.first(where: { window in
            window.identifier == .hyperwhisperMainWindow
        }) {
            mainWindow.makeKeyAndOrderFront(nil)
            return
        }

        // STEP 3: If no main window exists (e.g., when showInDock is false and the last window was closed),
        // ask SwiftUI to create/open the WindowGroup identified as "mainWindow".
        openWindow(id: "mainWindow")
    }

    /// Opens file picker and starts transcription with the selected mode
    ///
    /// Creates a FileTranscriptionFlow instance to handle:
    /// 1. File selection via NSOpenPanel
    /// 2. File size validation against provider limits
    /// 3. Copying file to recordings folder
    /// 4. Creating processing transcript
    /// 5. Navigation to History view (immediately, showing "Processing..." status)
    /// 6. Transcription and result handling
    ///
    /// - Parameter mode: The transcription mode to use
    private func transcribeFile(with mode: Mode) {
        let fileTranscriptionFlow = FileTranscriptionFlow(
            transcriptionPipeline: transcriptionPipeline,
            settingsManager: settingsManager,
            appState: appState,
            licenseManager: licenseManager,
            onOpenMainWindow: { [self] in
                // This callback allows FileTranscriptionFlow to open the main window
                // even when no window exists (e.g., app launched minimized to menu bar)
                openMainWindow()
            }
        )
        fileTranscriptionFlow.openFilePickerAndTranscribe(for: mode)
    }

    private func transcribeFile(with snapshot: ModeSnapshot) {
        Task { @MainActor in
            guard let mode = await PersistenceController.shared.fetchModeInBackground(withId: snapshot.id.uuidString) else {
                AppLogger.coreData.error("Unable to resolve mode for file transcription: \(snapshot.name, privacy: .public)")
                return
            }
            transcribeFile(with: mode)
        }
    }
}

// MARK: - Preview

#Preview {
    MainAppView()
        .environmentObject(AppState())
        .environmentObject(AudioRecordingManager())
        .environmentObject(TranscriptionPipeline())
        .environmentObject(SettingsManager())
        .frame(width: 1000, height: 700)
}

// MARK: - Shortcut Mapping Helpers

extension MenuBarItems {
    /// Convert a `KeyboardShortcuts.Shortcut` into the SwiftUI equivalent so the
    /// menu accurately mirrors the user's configured global shortcut.
    fileprivate func makeKeyboardShortcut(from shortcut: KeyboardShortcuts.Shortcut) -> SwiftUI.KeyboardShortcut? {
        guard let key = shortcut.key else { return nil }
        guard let keyEquivalent = keyEquivalent(for: key) else { return nil }
        let modifiers = eventModifiers(from: shortcut.modifiers)
        return SwiftUI.KeyboardShortcut(keyEquivalent, modifiers: modifiers)
    }

    /// Map NSEvent modifier flags to SwiftUI event modifiers so glyphs render correctly.
    private func eventModifiers(from flags: NSEvent.ModifierFlags) -> EventModifiers {
        var modifiers: EventModifiers = []
        if flags.contains(.command) { modifiers.insert(.command) }
        if flags.contains(.option) { modifiers.insert(.option) }
        if flags.contains(.shift) { modifiers.insert(.shift) }
        if flags.contains(.control) { modifiers.insert(.control) }
        if flags.contains(.capsLock) { modifiers.insert(.capsLock) }
        // .function modifier is deprecated in macOS 12.0+ and reserved for system applications
        return modifiers
    }

    /// Translate the strongly-typed KeyboardShortcuts key into a SwiftUI key equivalent.
    private func keyEquivalent(for key: KeyboardShortcuts.Key) -> KeyEquivalent? {
        // Character-based keys (letters, numbers, punctuation)
        let characterMap: [KeyboardShortcuts.Key: Character] = [
            .a: "a", .b: "b", .c: "c", .d: "d", .e: "e", .f: "f", .g: "g", .h: "h",
            .i: "i", .j: "j", .k: "k", .l: "l", .m: "m", .n: "n", .o: "o", .p: "p",
            .q: "q", .r: "r", .s: "s", .t: "t", .u: "u", .v: "v", .w: "w", .x: "x",
            .y: "y", .z: "z",
            .zero: "0", .one: "1", .two: "2", .three: "3", .four: "4",
            .five: "5", .six: "6", .seven: "7", .eight: "8", .nine: "9",
            .comma: ",", .period: ".", .slash: "/", .semicolon: ";", .quote: "'",
            .backslash: "\\", .minus: "-", .equal: "=", .backtick: "`",
            .leftBracket: "[", .rightBracket: "]",
            .keypad0: "0", .keypad1: "1", .keypad2: "2", .keypad3: "3", .keypad4: "4",
            .keypad5: "5", .keypad6: "6", .keypad7: "7", .keypad8: "8", .keypad9: "9",
            .keypadDecimal: ".", .keypadDivide: "/", .keypadEquals: "=",
            .keypadMinus: "-", .keypadMultiply: "*", .keypadPlus: "+"
        ]

        if let character = characterMap[key] {
            return KeyEquivalent(character)
        }

        switch key {
        case .space:
            return .space
        case .tab:
            return .tab
        case .return, .keypadEnter:
            return .return
        case .escape:
            return .escape
        case .delete:
            return .delete
        case .deleteForward:
            return .deleteForward
        case .home:
            return .home
        case .end:
            return .end
        case .pageUp:
            return .pageUp
        case .pageDown:
            return .pageDown
        case .upArrow:
            return .upArrow
        case .downArrow:
            return .downArrow
        case .leftArrow:
            return .leftArrow
        case .rightArrow:
            return .rightArrow
        case .f1, .f2, .f3, .f4, .f5, .f6, .f7, .f8, .f9, .f10,
             .f11, .f12, .f13, .f14, .f15, .f16, .f17, .f18, .f19, .f20:
            return nil // Function keys cannot be represented as KeyEquivalents in SwiftUI menus
        case .volumeUp, .volumeDown, .mute, .capsLock, .shift, .function, .control,
                .option, .command, .rightCommand, .rightOption, .rightControl, .rightShift:
            return nil // These keys are modifiers or system toggles that SwiftUI menus cannot display directly.
        default:
            return nil
        }
    }

    /// Attempt to reapply the persisted microphone selection whenever devices change or the menu reappears.
    private func restoreSavedMicrophoneIfNeeded(devices: [AudioDevice]) {
        let savedId = settingsManager.selectedMicrophoneId
        guard !savedId.isEmpty else { return }

        // No-op if selection already matches the saved preference
        if audioManager.selectedDevice?.id == savedId {
            if let current = audioManager.selectedDevice, selectedMicrophone != current.name {
                selectedMicrophone = current.name
            }
            return
        }

        guard let savedDevice = devices.first(where: { $0.id == savedId }) else {
            AppLogger.audio.debug("Saved microphone ID not present in device list: \(savedId, privacy: .public)")
            // AudioDeviceManager already clears the persisted preference via its invalidation callback.
            return
        }

        AppLogger.audio.info("Restoring saved microphone selection: \(savedDevice.name, privacy: .public)")
        audioManager.selectDevice(savedDevice)
        selectedMicrophone = savedDevice.name
    }
}
