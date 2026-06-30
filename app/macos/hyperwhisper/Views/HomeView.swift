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
    
    /// Track which getting started items are completed (persisted via UserDefaults)
    @AppStorage("gettingStartedCompletedSteps") private var completedStepsRaw: String = ""

    private var completedSteps: Set<String> {
        Set(completedStepsRaw.split(separator: ",").map(String.init))
    }

    private func toggleStep(_ step: String) {
        var steps = completedSteps
        if steps.contains(step) {
            steps.remove(step)
        } else {
            steps.insert(step)
        }
        completedStepsRaw = steps.sorted().joined(separator: ",")
    }
    
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
                if completedSteps.count < 4 {
                    gettingStartedSection
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
        }
        .onDisappear {
            // Clean up timer when view disappears
            stopAccessibilityCheckTimer()
        }
        .onReceive(NotificationCenter.default.publisher(for: .shortcutDidChange)) { _ in
            // Update when shortcuts change
            currentShortcut = toggleRecordingShortcutString
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

    /// Interactive getting started checklist
    private var gettingStartedSection: some View {
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
                    isCompleted: completedSteps.contains("recording")
                ) {
                    // Disabled button action to avoid macOS 26.1 crash
                    // Users can use the keyboard shortcut displayed on the card
                    toggleStep("recording")
                }
                
                // Customize shortcuts card
                GettingStartedCard(
                    icon: "keyboard",
                    iconColor: .purple,
                    title: "home.getting.started.shortcuts.title".localized,
                    description: "home.getting.started.shortcuts.description".localized,
                    shortcut: nil,
                    isCompleted: completedSteps.contains("shortcuts")
                ) {
                    // Navigate to shortcuts section in settings
                    appState.navigateToSettings(section: "shortcuts")
                    toggleStep("shortcuts")
                }
                
                // Create mode card
                GettingStartedCard(
                    icon: "plus.app",
                    iconColor: .green,
                    title: "home.getting.started.mode.title".localized,
                    description: "home.getting.started.mode.description".localized,
                    shortcut: nil,
                    isCompleted: completedSteps.contains("mode")
                ) {
                    // Navigate to modes
                    appState.selectedNavigationItem = .modes
                    toggleStep("mode")
                }
                
                // Add vocabulary card
                GettingStartedCard(
                    icon: "text.book.closed",
                    iconColor: .orange,
                    title: "home.getting.started.vocabulary.title".localized,
                    description: "home.getting.started.vocabulary.description".localized,
                    shortcut: nil,
                    isCompleted: completedSteps.contains("vocabulary")
                ) {
                    // Navigate to vocabulary
                    appState.selectedNavigationItem = .vocabulary
                    toggleStep("vocabulary")
                }
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

    // MARK: - What's New Section
    
    /// Display recent updates and features
    private var whatsNewSection: some View {
        VStack(alignment: .leading, spacing: 16) {
            // Section title
            Text("home.whatsnew.title".localized)
                .font(.title2)
                .fontWeight(.medium)
            
            // Update cards
            VStack(spacing: 12) {
                WhatsNewCard(
                    date: "home.whatsnew.update1.date".localized,
                    title: "home.whatsnew.update1.title".localized,
                    description: "home.whatsnew.update1.description".localized,
                    isNew: true
                )
                
                WhatsNewCard(
                    date: "home.whatsnew.update2.date".localized,
                    title: "home.whatsnew.update2.title".localized,
                    description: "home.whatsnew.update2.description".localized,
                    isNew: false
                )
                
                WhatsNewCard(
                    date: "home.whatsnew.update3.date".localized,
                    title: "home.whatsnew.update3.title".localized,
                    description: "home.whatsnew.update3.description".localized,
                    isNew: false
                )
            }
        }
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

// MARK: - What's New Card

/// Card for displaying update information
struct WhatsNewCard: View {
    let date: String
    let title: String
    let description: String
    let isNew: Bool
    
    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            // Date badge
            VStack(spacing: 2) {
                Text(date)
                    .font(.caption)
                    .fontWeight(.medium)
                    .foregroundColor(.secondary)
                
                if isNew {
                    Text("home.whatsnew.badge".localized)
                        .font(.system(size: 9, weight: .bold))
                        .foregroundColor(.white)
                        .padding(.horizontal, 4)
                        .padding(.vertical, 1)
                        .background(Color.red)
                        .cornerRadius(3)
                }
            }
            .frame(width: 50)
            
            // Content
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.headline)
                    .foregroundColor(.primary)
                
                Text(description)
                    .font(.subheadline)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            
            Spacer()
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(.thinMaterial)
        )
    }
}


// MARK: - Keyboard Shortcut Badge

/// Visual representation of keyboard shortcuts
struct KeyboardShortcutBadge: View {
    let keys: String
    
    var body: some View {
        HStack(spacing: 2) {
            // Parse and display each key
            ForEach(parseKeys(keys), id: \.self) { key in
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
    
    /// Parse keyboard shortcut string into individual keys
    private func parseKeys(_ shortcut: String) -> [String] {
        // Normalize a few special glyphs to readable labels
        var s = shortcut.trimmingCharacters(in: .whitespacesAndNewlines)
        let escapeLabel = "keyboard.escape".localized
        let returnLabel = "keyboard.return".localized
        let spaceLabel = "keyboard.space".localized
        s = s.replacingOccurrences(of: "⎋", with: escapeLabel)
        s = s.replacingOccurrences(of: "Escape", with: escapeLabel)
        s = s.replacingOccurrences(of: "↩︎", with: returnLabel)
        s = s.replacingOccurrences(of: "↩", with: returnLabel)

        // Collect known modifiers in order
        var keys: [String] = []
        let modifierMap: [(glyph: String, key: String)] = [("⌘","⌘"),("⌥","⌥"),("⇧","⇧"),("⌃","⌃")]
        for (glyph, key) in modifierMap {
            if s.contains(glyph) { keys.append(key) }
        }

        // Determine the primary key label
        if shortcut.localizedCaseInsensitiveContains("Space") || s.localizedCaseInsensitiveContains(spaceLabel) {
            keys.append(spaceLabel)
        } else if shortcut.localizedCaseInsensitiveContains("Esc") ||
                    shortcut.localizedCaseInsensitiveContains("Escape") ||
                    shortcut.contains("⎋") ||
                    s.localizedCaseInsensitiveContains(escapeLabel) {
            keys.append(escapeLabel)
        } else if shortcut.localizedCaseInsensitiveContains("Return") ||
                    shortcut.contains("↩") ||
                    shortcut.contains("↩︎") ||
                    s.localizedCaseInsensitiveContains(returnLabel) {
            keys.append(returnLabel)
        } else if let lastAlnum = s.reversed().first(where: { $0.isLetter || $0.isNumber }) {
            keys.append(String(lastAlnum).uppercased())
        } else if !s.isEmpty {
            // Fallback: show the normalized string as-is
            keys.append(s)
        }

        return keys
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
