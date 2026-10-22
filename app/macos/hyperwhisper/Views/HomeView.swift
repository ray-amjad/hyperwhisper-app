//
//  HomeView.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  HOME VIEW - Getting Started Screen
//  This is the landing page that users see when they open the app.
//  It provides quick access to common actions and helpful information.
//
//  Design Goals:
//  - Welcome new users with clear onboarding
//  - Provide quick access to main features
//  - Show recent activity and tips
//  - Guide users through initial setup

import SwiftUI
import KeyboardShortcuts
import AppKit
import Foundation
import os
import CoreData

/// Logger for HomeView (static to work with SwiftUI structs)
private let homeViewLogger = Logger(subsystem: "com.hyperwhisper.app", category: "HomeView")

extension Notification.Name {
    static let shortcutDidChange = Notification.Name("shortcutDidChange")
}

// MARK: - Home View

/// Main home/dashboard view of the application
struct HomeView: View {
    
    // MARK: - Environment
    
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var licenseManager: LicenseManager
    
    // MARK: - State
    
    /// Which Get started steps are really done, read from the app's own state
    /// (issue #652). `nil` until the first read, so the section never flashes
    /// in on an install that has done all four. The old click-toggled
    /// `gettingStartedCompletedSteps` default is not read any more.
    @State private var gettingStarted: GettingStartedProgress?

    /// Bumped by every read, so an older read that finishes late is dropped.
    @State private var gettingStartedGeneration = 0

    /// Whether the Start recording row's shortcut explanation is showing.
    @State private var showRecordingShortcutHelp = false

    /// Track the current shortcut to trigger updates
    @State private var currentShortcut: String? = nil
    
    /// Accessibility trust state for auto-paste
    @State private var isAccessibilityTrusted: Bool = AccessibilityHelper.shared.hasAccessibilityPermission()
    
    /// Timer for periodic accessibility checks
    @State private var accessibilityCheckTimer: Timer?
    
    /// Track if we're actively polling for permission
    @State private var isPollingForPermission: Bool = false

    /// Computed property to check if API key prompt should be shown
    private var shouldShowAPIKeyPrompt: Bool {
        guard let modeSnapshot = appState.selectedModeSnapshot else {
            return false
        }
        
        // Check for missing keys using centralized validation
        let missingKeys = settingsManager.getMissingAPIKeys(for: modeSnapshot)
        
        // Don't show prompt if only post-processing keys are missing (non-blocking)
        let onlyPostProcessingMissing = SettingsManager.onlyPostProcessingKeysMissing(missingKeys)
        
        // Show prompt if there are missing keys that would block recording
        return !missingKeys.isEmpty && !onlyPostProcessingMissing
    }
    
    /// Get display text for missing API keys
    private var missingAPIKeysDisplayText: (title: String, message: String) {
        guard let modeSnapshot = appState.selectedModeSnapshot else {
            return ("api.key.required".localized, "api.key.configure.message".localized)
        }
        
        let missingKeys = settingsManager.getMissingAPIKeys(for: modeSnapshot)
        
        // Special case for offline
        if missingKeys.count == 1, case .offline = missingKeys[0].context {
            return ("api.key.internet.required".localized, "api.key.internet.message".localized)
        }
        
        // Build title
        let title = missingKeys.count > 1 ? "api.keys.required".localized : "api.key.required".localized
        
        // Build message with provider names
        var providers: [String] = []
        var processedProviders = Set<String>()
        
        for key in missingKeys {
            let providerName = key.providerName
            if !processedProviders.contains(providerName) {
                processedProviders.insert(providerName)
                providers.append(providerName)
            }
        }

        let listFormatter = ListFormatter()
        let providerList = listFormatter.string(from: providers) ?? providers.joined(separator: ", ")
        let message: String
        if providers.count > 1 {
            message = "home.api.keys.missing.multiple".localized(arguments: providerList)
        } else if let provider = providers.first {
            message = "home.api.keys.missing.single".localized(arguments: provider)
        } else {
            message = "api.key.configure.message".localized
        }

        return (title, message)
    }
    
    /// Get the formatted string for the toggle recording shortcut
    private var toggleRecordingShortcutString: String? {
        // Use the built-in description from KeyboardShortcuts
        return KeyboardShortcuts.getShortcut(for: .toggleRecordingWithTranscription)?.description
    }
    
    // MARK: - Body
    
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                // API key prompt at the top (generalized for all providers)
                if shouldShowAPIKeyPrompt {
                    apiKeyPrompt
                }
                // Accessibility prompt
                if !isAccessibilityTrusted {
                    accessibilityPrompt
                }
                // MARK: Getting Started Section
                if let gettingStarted, !gettingStarted.isAllDone {
                    gettingStartedSection(gettingStarted)
                }

                // MARK: Home Stats Bar
                HomeStatsBar()

                // MARK: Recent Updates Section
                recentUpdatesSection
            }
            .padding(24)
        }
        .background(VisualEffectBackground())
        .navigationTitle(licenseManager.licenseStatus == .active ? "app.title.pro".localized : "app.title".localized)
        .onAppear {
            homeViewLogger.debug("🏠 HomeView.onAppear()")
            // Update the current shortcut when view appears
            currentShortcut = toggleRecordingShortcutString
            isAccessibilityTrusted = AccessibilityHelper.shared.hasAccessibilityPermission()
            homeViewLogger.debug("   Initial accessibility status: \(isAccessibilityTrusted, privacy: .public)")
            
            // Start periodic check timer (every 2 seconds)
            startAccessibilityCheckTimer()
            refreshGettingStarted()
        }
        .onDisappear {
            // Clean up timer when view disappears
            stopAccessibilityCheckTimer()
        }
        .onReceive(NotificationCenter.default.publisher(for: .shortcutDidChange)) { _ in
            // Update when shortcuts change
            currentShortcut = toggleRecordingShortcutString
            refreshGettingStarted()
        }
        .onReceive(
            NotificationCenter.default.publisher(for: .NSManagedObjectContextDidSave)
                .filter { GettingStartedLoader.saveTouchesSteps($0) }
                .receive(on: RunLoop.main)
        ) { _ in
            // A dictation finished, or a Mode or a word was added or removed.
            refreshGettingStarted()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            // PERFORMANCE FIX: Skip redundant accessibility checks
            // When the recording dialog opens as a floating window, window focus thrashes
            // between Main Window and Recording Dialog, firing this notification repeatedly.
            // Each state update causes parent view rebuilds that cascade to sibling views
            // like HistoryView, causing excessive rerenders.
            //
            // Guard conditions:
            // 1. Already trusted - no need to recheck
            // 2. Already polling - will get result via .accessibilityPermissionGranted notification
            // 3. Recording dialog is open - skip during recording to prevent focus thrashing
            guard !isAccessibilityTrusted && !isPollingForPermission && !appState.showRecordingDialog else { return }

            homeViewLogger.debug("📱 App became active, rechecking accessibility...")
            // Re-check trust when app becomes active
            isAccessibilityTrusted = AccessibilityHelper.shared.hasAccessibilityPermission()
        }
        .onReceive(NotificationCenter.default.publisher(for: .accessibilityPermissionGranted)) { _ in
            homeViewLogger.info("📬 Received accessibilityPermissionGranted notification!")
            // Update immediately when permission is granted through polling
            isAccessibilityTrusted = true
            isPollingForPermission = false
        }
    }

    // MARK: - Recent Updates Section

    /// Display recent software releases from appcast feed
    private var recentUpdatesSection: some View {
        RecentUpdatesView()
            .onAppear {
                // Trigger fetch when section appears
            }
    }

    // MARK: - Getting Started Section

    /// Getting started checklist. A row's tick comes only from `progress`; a
    /// click on a row opens its page and never changes the tick (issue #652).
    private func gettingStartedSection(_ progress: GettingStartedProgress) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            // Section title
            HStack {
                Image(systemName: "sparkles")
                    .foregroundColor(.accentColor)
                Text("home.get.started".localized)
                    .font(.title2)
                    .fontWeight(.medium)
            }
            
            // Getting started cards
            VStack(spacing: 12) {
                // Start recording card
                GettingStartedCard(
                    icon: "mic.circle",
                    iconColor: .blue,
                    title: "home.getting.started.recording.title".localized,
                    description: "home.getting.started.recording.description".localized,
                    shortcut: toggleRecordingShortcutString,
                    isCompleted: progress.isCompleted(.recording)
                ) {
                    // The row does not start a recording: that action was
                    // disabled to avoid a macOS 26.1 crash. It explains the
                    // shortcut instead, and with no shortcut set it opens the
                    // page that sets one.
                    if toggleRecordingShortcutString != nil {
                        showRecordingShortcutHelp = true
                    } else {
                        appState.navigateToSettings(section: "shortcuts")
                    }
                }
                .popover(isPresented: $showRecordingShortcutHelp, arrowEdge: .trailing) {
                    recordingShortcutHelp
                }

                // Customize shortcuts card
                GettingStartedCard(
                    icon: "keyboard",
                    iconColor: .purple,
                    title: "home.getting.started.shortcuts.title".localized,
                    description: "home.getting.started.shortcuts.description".localized,
                    shortcut: nil,
                    isCompleted: progress.isCompleted(.shortcuts)
                ) {
                    // Navigate to shortcuts section in settings
                    appState.navigateToSettings(section: "shortcuts")
                }

                // Create mode card
                GettingStartedCard(
                    icon: "plus.app",
                    iconColor: .green,
                    title: "home.getting.started.mode.title".localized,
                    description: "home.getting.started.mode.description".localized,
                    shortcut: nil,
                    isCompleted: progress.isCompleted(.mode)
                ) {
                    // Navigate to modes
                    appState.selectedNavigationItem = .modes
                }

                // Add vocabulary card
                GettingStartedCard(
                    icon: "text.book.closed",
                    iconColor: .orange,
                    title: "home.getting.started.vocabulary.title".localized,
                    description: "home.getting.started.vocabulary.description".localized,
                    shortcut: nil,
                    isCompleted: progress.isCompleted(.vocabulary)
                ) {
                    // Navigate to vocabulary
                    appState.selectedNavigationItem = .vocabulary
                }
            }
        }
    }

    /// How to record from the keyboard, shown from the Start recording row.
    private var recordingShortcutHelp: some View {
        VStack(alignment: .leading, spacing: 10) {
            // A popover sizes itself to its content's ideal size, so each line
            // must be allowed to wrap and grow tall, or a long one is cut off.
            Text("onboarding.done.detail".localized)
                .font(.headline)
                .fixedSize(horizontal: false, vertical: true)
            if let shortcut = toggleRecordingShortcutString {
                KeyboardShortcutBadge(keys: shortcut)
            }
            Text("onboarding.tryIt.shortcutHint".localized)
                .font(.subheadline)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(width: 280, alignment: .leading)
        .padding(16)
    }

    /// Re-reads the Get started steps off the main thread. A newer read always
    /// wins: each one carries the generation it started in.
    private func refreshGettingStarted() {
        let hasCustomShortcut = GettingStartedProgress.hasCustomShortcut(
            pushToTalkMode: settingsManager.pushToTalkMode,
            shortcut: { KeyboardShortcuts.getShortcut(for: $0) }
        )
        let container = PersistenceController.shared.container
        gettingStartedGeneration += 1
        let generation = gettingStartedGeneration
        Task.detached(priority: .userInitiated) {
            do {
                let progress = try GettingStartedLoader.load(
                    container: container,
                    hasCustomShortcut: hasCustomShortcut
                )
                await MainActor.run {
                    guard generation == gettingStartedGeneration else { return }
                    gettingStarted = progress
                }
            } catch {
                // Keep what is showing; the next appear or save reads again.
                homeViewLogger.error("Get started read failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    // MARK: - API Key Prompt
    private var apiKeyPrompt: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "key.fill")
                .font(.system(size: 16, weight: .semibold))
                .foregroundColor(.orange)
            VStack(alignment: .leading, spacing: 4) {
                Text(missingAPIKeysDisplayText.title)
                    .font(.system(size: 13, weight: .semibold))
                Text(missingAPIKeysDisplayText.message)
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
            }
            Spacer()
            Button(action: {
                // Navigate to the Model Library, which now owns API key management.
                appState.navigateToModelLibraryAPIKeys()
            }) {
                Text("home.add.api.key".localized)
                    .font(.system(size: 12, weight: .medium))
                    .padding(.horizontal, 12)
                    .frame(height: 26)
                    .background(Color.orange.opacity(0.9))
                    .foregroundColor(.white)
                    .cornerRadius(6)
            }
            .buttonStyle(.plain)
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.orange.opacity(0.10)))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.orange.opacity(0.20), lineWidth: 0.5)
        )
    }
    
    // MARK: - Accessibility Prompt
    private var accessibilityPrompt: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "hand.tap")
                .font(.system(size: 16, weight: .semibold))
                .foregroundColor(.accentColor)
            VStack(alignment: .leading, spacing: 4) {
                Text("home.accessibility.enable.title".localized)
                    .font(.system(size: 13, weight: .semibold))
                Text("home.accessibility.enable.description".localized)
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 8) {
                Button(action: {
                    homeViewLogger.info("🔘 User clicked 'Open Settings' button")
                    // Open settings and start polling for permission
                    AccessibilityHelper.shared.openAccessibilitySettings()

                    // Start polling for permission to be granted
                    if !isPollingForPermission {
                        homeViewLogger.debug("   Starting active polling for permission...")
                        isPollingForPermission = true
                        AccessibilityHelper.shared.waitForAccessibilityPermission { granted in
                            if granted {
                                // Permission granted - UI will update via notification
                                homeViewLogger.info("   ✅ Polling detected permission granted!")
                                self.isAccessibilityTrusted = true
                            }
                            self.isPollingForPermission = false
                        }
                    } else {
                        homeViewLogger.debug("   Already polling, skipping...")
                    }
                }) {
                    Text(isPollingForPermission ? "home.accessibility.waiting".localized : "home.accessibility.open.settings".localized)
                        .font(.system(size: 12, weight: .medium))
                        .padding(.horizontal, 12)
                        .frame(height: 26)
                        .background(Color.accentColor.opacity(0.9))
                        .foregroundColor(.white)
                        .cornerRadius(6)
                }
                .buttonStyle(.plain)
                
                if isPollingForPermission {
                    Text("home.accessibility.restart.after.permission".localized)
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                }
            }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.accentColor.opacity(0.10)))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.accentColor.opacity(0.20), lineWidth: 0.5)
        )
    }
    
    // MARK: - Helper Methods
    
    /// Start periodic timer to check accessibility permission
    private func startAccessibilityCheckTimer() {
        homeViewLogger.debug("🕐 HomeView: Starting accessibility check timer (every 2 seconds)")
        // Stop any existing timer
        stopAccessibilityCheckTimer()

        // Create new timer that fires every 2 seconds
        accessibilityCheckTimer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: true) { _ in
            let newStatus = AccessibilityHelper.shared.hasAccessibilityPermission()
            if newStatus != self.isAccessibilityTrusted {
                homeViewLogger.info("🔄 HomeView: Accessibility status changed from \(self.isAccessibilityTrusted, privacy: .public) to \(newStatus, privacy: .public)")
                self.isAccessibilityTrusted = newStatus
                // Stop polling if we detected permission was granted
                if newStatus && self.isPollingForPermission {
                    homeViewLogger.debug("   Stopping active polling since permission was granted")
                    self.isPollingForPermission = false
                }
            }
        }
    }
    
    /// Stop the accessibility check timer
    private func stopAccessibilityCheckTimer() {
        accessibilityCheckTimer?.invalidate()
        accessibilityCheckTimer = nil
    }

}

// MARK: - Getting Started Card

/// Individual card for getting started items
struct GettingStartedCard: View {
    // MARK: Properties
    
    let icon: String
    let iconColor: Color
    let title: String
    let description: String
    let shortcut: String?
    let isCompleted: Bool
    let action: () -> Void
    
    // MARK: State
    
    @State private var isHovered = false
    
    // MARK: Body
    
    var body: some View {
        Button(action: action) {
            HStack(spacing: 16) {
                // Icon
                ZStack {
                    Circle()
                        .fill(iconColor.opacity(0.1))
                        .frame(width: 44, height: 44)
                    
                    Image(systemName: isCompleted ? "checkmark.circle.fill" : icon)
                        .font(.system(size: 20))
                        .foregroundColor(isCompleted ? .green : iconColor)
                }
                
                // Content
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text(title)
                            .font(.headline)
                            .foregroundColor(.primary)
                        
                        if isCompleted {
                            Text("home.getting.started.completed".localized)
                                .font(.caption)
                                .foregroundColor(.green)
                                .padding(.horizontal, 6)
                                .padding(.vertical, 2)
                                .background(Color.green.opacity(0.1))
                                .cornerRadius(4)
                        }
                    }
                    
                    Text(description)
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                        .lineLimit(2)
                }
                
                Spacer()
                
                // Shortcut badge if available
                if let shortcut = shortcut {
                    KeyboardShortcutBadge(keys: shortcut)
                }
                
                // Arrow indicator
                Image(systemName: "chevron.right")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .opacity(isHovered ? 1 : 0.5)
            }
            .padding(16)
            .background(
                RoundedRectangle(cornerRadius: 12)
                    .fill(.thinMaterial)
                    .overlay(
                        RoundedRectangle(cornerRadius: 12)
                            .stroke(isHovered ? iconColor.opacity(0.3) : Color.clear, lineWidth: 1)
                    )
            )
            .shadow(color: .black.opacity(isHovered ? 0.05 : 0), radius: 8, y: 2)
        }
        .buttonStyle(.plain)
        .onHover { hovering in
            withAnimation(.easeInOut(duration: 0.2)) {
                isHovered = hovering
            }
        }
    }
}


// MARK: - Keyboard Shortcut Badge

/// Visual representation of keyboard shortcuts
struct KeyboardShortcutBadge: View {
    let keys: String
    
    var body: some View {
        HStack(spacing: 2) {
            // Parse and display each key
            ForEach(renderedKeys(keys), id: \.self) { key in
                Text(key)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.primary)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 3)
                    .background(
                        RoundedRectangle(cornerRadius: 4)
                            .fill(Color(NSColor.controlBackgroundColor))
                            .overlay(
                                RoundedRectangle(cornerRadius: 4)
                                    .stroke(Color.gray.opacity(0.3), lineWidth: 1)
                            )
                    )
            }
        }
    }
    
    /// The badge draws modifiers as glyphs; only the named primaries are worded.
    private func renderedKeys(_ shortcut: String) -> [String] {
        ShortcutKeyTokens.tokenize(shortcut).map { token in
            switch token {
            case .command: return "⌘"
            case .option: return "⌥"
            case .control: return "⌃"
            case .shift: return "⇧"
            case .capsLock: return "⇪"
            case .escape: return "keyboard.escape".localized
            case .return: return "keyboard.return".localized
            case .space: return "keyboard.space".localized
            case .key(let key): return key.uppercased()
            }
        }
    }
}

// MARK: - Getting Started Progress

/// One row of the Home Get started checklist.
enum GettingStartedStep: CaseIterable {
    case recording
    case shortcuts
    case mode
    case vocabulary
}

/// Which Get started steps are really done (issue #652).
///
/// Each row used to be a click toggle stored in `gettingStartedCompletedSteps`:
/// a click marked it Completed whether or not the user had done anything, and a
/// second click took the tick away. Now a step is done only when the app's own
/// state says so, and nothing a click does can change it.
struct GettingStartedProgress: Equatable {
    /// At least one transcription finished (`status == "completed"`).
    var hasFinishedRecording = false
    /// A shortcut differs from its default, or Push to Talk is on.
    var hasCustomShortcut = false
    /// At least one Mode the user made: not the seeded default, and not a
    /// per-request row the Local API leaked.
    var hasUserMode = false
    /// At least one vocabulary word.
    var hasVocabularyWord = false

    func isCompleted(_ step: GettingStartedStep) -> Bool {
        switch step {
        case .recording: return hasFinishedRecording
        case .shortcuts: return hasCustomShortcut
        case .mode: return hasUserMode
        case .vocabulary: return hasVocabularyWord
        }
    }

    var isAllDone: Bool {
        GettingStartedStep.allCases.allSatisfy(isCompleted)
    }

    /// Whether the user changed a shortcut on Settings › Shortcuts: one that is
    /// not its default (a cleared one counts, and so does a Quick Capture
    /// shortcut, which has no default), or a Push to Talk mode other than the
    /// default `.disabled`. Every shortcut the app names starts, stops or steers
    /// a recording, so all of them count.
    static func hasCustomShortcut(
        pushToTalkMode: PushToTalkMode,
        shortcut: (KeyboardShortcuts.Name) -> KeyboardShortcuts.Shortcut?
    ) -> Bool {
        pushToTalkMode != .disabled
            || KeyboardShortcuts.Name.allCases.contains { shortcut($0) != $0.defaultShortcut }
    }
}

enum GettingStartedLoader {

    /// Reads the three Core Data steps on a fresh background context. Never
    /// call it on the main thread.
    nonisolated static func load(
        container: NSPersistentContainer,
        hasCustomShortcut: Bool
    ) throws -> GettingStartedProgress {
        let context = container.newBackgroundContext()
        var progress = GettingStartedProgress(hasCustomShortcut: hasCustomShortcut)
        var loadError: Error?

        context.performAndWait {
            do {
                progress.hasFinishedRecording = try exists(
                    "Transcript",
                    NSPredicate(format: "status == %@", "completed"),
                    in: context
                )
                progress.hasVocabularyWord = try exists(
                    "Vocabulary",
                    NSPredicate(format: "word != nil AND word != %@", ""),
                    in: context
                )
                progress.hasUserMode = try hasUserMode(in: context)
            } catch {
                loadError = error
            }
        }
        if let loadError { throw loadError }
        return progress
    }

    /// True when a save inserted, changed or deleted a row a step reads.
    nonisolated static func saveTouchesSteps(_ note: Notification) -> Bool {
        guard let info = note.userInfo else { return false }
        if info[NSInvalidatedAllObjectsKey] != nil { return true }
        for key in [NSInsertedObjectsKey, NSUpdatedObjectsKey, NSDeletedObjectsKey] {
            if let objects = info[key] as? Set<NSManagedObject>,
               objects.contains(where: { stepEntities.contains($0.entity.name ?? "") }) {
                return true
            }
        }
        return false
    }

    private static let stepEntities: Set<String> = ["Transcript", "Mode", "Vocabulary"]

    private nonisolated static func exists(
        _ entityName: String,
        _ predicate: NSPredicate,
        in context: NSManagedObjectContext
    ) throws -> Bool {
        let request = NSFetchRequest<NSManagedObjectID>(entityName: entityName)
        request.resultType = .managedObjectIDResultType
        request.predicate = predicate
        request.fetchLimit = 1
        return try !context.fetch(request).isEmpty
    }

    /// Modes are few, so this reads the identity columns of every one and
    /// applies the same tests the rest of the app uses for "built in"
    /// (`Mode.isSeededDefault`) and "leaked by the Local API"
    /// (`LocalAPITransientModeMarker.isLeakedRow`).
    private nonisolated static func hasUserMode(in context: NSManagedObjectContext) throws -> Bool {
        let request = NSFetchRequest<NSDictionary>(entityName: "Mode")
        request.resultType = .dictionaryResultType
        request.propertiesToFetch = ["id", "name", "sortOrder", "isDefault", "isSystemProvided"]
        return try context.fetch(request).contains { row in
            let id = row["id"] as? UUID
            let isSystemProvided = (row["isSystemProvided"] as? NSNumber)?.boolValue ?? false
            if isSystemProvided || id == SeededModeValues.seededID { return false }
            return !LocalAPITransientModeMarker.isLeakedRow(
                name: row["name"] as? String,
                sortOrder: (row["sortOrder"] as? NSNumber)?.int16Value ?? 0,
                isDefault: (row["isDefault"] as? NSNumber)?.boolValue ?? false,
                isSystemProvided: isSystemProvided
            )
        }
    }
}

// Old OnboardingView removed - now using comprehensive OnboardingView.swift

// MARK: - Preview

#Preview {
    HomeView()
        .environmentObject(AppState())
        .environmentObject(AudioRecordingManager())
        .environmentObject(TranscriptionPipeline())
        .environmentObject(SettingsManager())
        .frame(width: 800, height: 600)
}
