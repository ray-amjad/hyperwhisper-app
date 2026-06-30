//
//  AutoPasteHandler.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import AppKit

/// Handles smart paste of transcribed text
///
/// **Purpose:**
/// Coordinates automatic pasting of transcribed text into the previously focused application.
///
/// **Smart Paste Flow:**
/// 1. Check accessibility permission
/// 2. If missing: show alert, copy to clipboard, keep dialog open
/// 3. Check if text input field is focused
/// 4. If no focus: copy to clipboard, keep dialog open
/// 5. If focused: paste text, reactivate previous app, close dialog
/// 6. Optionally restore previous clipboard (if enabled in settings)
///
/// **Thread Safety:**
/// All methods run on main actor for UI and accessibility API safety.
@MainActor
class AutoPasteHandler {

    // MARK: - Dependencies

    private weak var settingsManager: SettingsManager?
    private var previousFrontmostPID: pid_t?
    private var previousFrontmostBundleID: String?

    // MARK: - Initialization

    init() {}

    /// Configure with dependencies after initialization.
    ///
    /// Paste target identity is intentionally set only through
    /// `setPreviousFrontmostApp(pid:bundleID:)` so PID and bundle ID stay paired.
    func configure(settingsManager: SettingsManager?) {
        self.settingsManager = settingsManager
        self.previousFrontmostPID = nil
        self.previousFrontmostBundleID = nil
    }

    /// Update the previous frontmost app identity used for paste targeting.
    /// The bundle identifier defends against PID reuse: if the captured app quits
    /// and macOS reassigns its PID to an unrelated app before the paste fires, the
    /// bundle-ID check prevents leaking the transcript into that other app.
    func setPreviousFrontmostApp(pid: pid_t?, bundleID: String?) {
        self.previousFrontmostPID = pid
        self.previousFrontmostBundleID = bundleID
    }

    // MARK: - Smart Paste

    /// Main smart paste function that coordinates the paste operation
    ///
    /// **What This Does:**
    /// 1. Checks if accessibility permission is granted
    /// 2. If missing: shows alert and copies to clipboard (dialog stays open)
    /// 3. Checks if a text input field is currently focused
    /// 4. If no focus: copies to clipboard (dialog stays open)
    /// 5. If focused:
    ///    - Pastes text into focused field
    ///    - Reactivates previous app
    ///    - Optionally restores previous clipboard
    ///    - Returns true to close dialog
    ///
    /// **Why Async:**
    /// Paste operations require delays for apps to register input.
    /// Using async prevents blocking the UI during paste.
    ///
    /// **Parameters:**
    /// - `textToPaste`: The transcribed text to paste
    ///
    /// **Returns:**
    /// - true: Successfully pasted into focused field (close dialog)
    /// - false: No paste target or error (keep dialog open)
    func handleAutoPaste(_ textToPaste: String) async -> Bool {
        // HIGH-LEVEL ORCHESTRATOR:
        // This function handles UI-level decisions while delegating the actual
        // paste operations to AccessibilityHelper.executePasteAsync()
        //
        // RESPONSIBILITIES:
        // - Calls low-level paste implementation
        // - Interprets SmartPasteResult for UI decisions
        // - Shows alerts when permissions are missing
        // - Returns bool to control recording dialog visibility
        //
        // BEHAVIOR:
        // - Delegates to AccessibilityHelper for the actual paste operation

        // SUSPEND MONITOR: Prevent PTT monitor from seeing the simulated Command key press
        // This is critical when Command is used as the PTT modifier
        BareModifierKeyMonitor.shared.setSuspended(true)
        
        // Ensure we resume monitoring even if paste fails/crashes
        defer {
            // Add a small delay before resuming to let any key-up events process
            Task {
                try? await Task.sleep(nanoseconds: 100_000_000) // 100ms
                BareModifierKeyMonitor.shared.setSuspended(false)
            }
        }
        
        let result = await AccessibilityHelper.shared.executePasteAsync(
            textToPaste,
            previousAppPID: previousFrontmostPID,
            previousAppBundleID: previousFrontmostBundleID,
            settings: settingsManager
        )

        // INTERPRET RESULT FOR UI:
        // Convert SmartPasteResult enum to simple bool for dialog control
        switch result {
        case .success:
            AppLogger.audio.info("✅ Auto-paste successful")
            return true  // Close dialog

        case .noPermission:
            AppLogger.audio.info("📋 Copying to clipboard as fallback (no accessibility permission)")
            AccessibilityHelper.shared.copyToClipboard(textToPaste)
            showMissingAccessAlertAsync()
            return false  // Keep dialog open

        case .noFocusedField, .secureField:
            // Text is on clipboard, restoration scheduled if enabled
            AppLogger.audio.info("ℹ️ No valid paste target. Text on clipboard.")
            return false  // Keep dialog open

        case .failed(let error):
            // Check if it was cancelled (not an error to report)
            if error is CancellationError {
                AppLogger.audio.info("ℹ️ Paste operation was cancelled")
            } else {
                AppLogger.audio.warning("⚠️ Paste failed: \(error.localizedDescription)")
            }
            return false  // Keep dialog open
        }
    }

    // MARK: - Accessibility Helpers

    /// Checks and optionally prompts for Accessibility permission
    ///
    /// **Parameters:**
    /// - `prompt`: Whether to show system permission dialog if not granted
    ///
    /// **Returns:**
    /// true if permission is already granted, false otherwise
    func hasAccessibilityAccess(prompt: Bool = true) -> Bool {
        return AccessibilityHelper.shared.checkAccessibilityPermission(prompt: prompt)
    }

    /// Copies text to the general pasteboard
    ///
    /// **Parameters:**
    /// - `text`: The text to copy
    /// - `autoRestore`: Whether to auto-restore previous clipboard (default: true)
    func putOnPasteboard(_ text: String, autoRestore: Bool = true) {
        if autoRestore {
            AccessibilityHelper.shared.copyToClipboard(text, respectSettings: settingsManager)
        } else {
            AccessibilityHelper.shared.copyToClipboard(text)
        }
    }

    // MARK: - Alerts

    /// Shows an alert when Accessibility permission is missing
    ///
    /// **What This Does:**
    /// Displays a system alert explaining that Accessibility permission is needed
    /// for auto-paste. Provides buttons to:
    /// - Open System Settings > Privacy & Security > Accessibility
    /// - Cancel and copy to clipboard instead
    /// Shows the accessibility permission alert without blocking the main thread.
    ///
    /// **Why beginSheetModal instead of runModal (HYPERWHISPER-FE, 8 users):**
    /// `runModal()` creates a nested run loop that blocks the main thread until
    /// the user dismisses the dialog. When the app is in the background (typical
    /// during auto-paste), the dialog appears behind other windows and goes unseen,
    /// causing a 10+ second app hang reported by Sentry.
    ///
    /// `beginSheetModal(for:)` returns immediately and delivers the response via
    /// completion handler, keeping the main thread free to process events.
    /// `withCheckedContinuation` bridges this to async/await cleanly.
    private func showMissingAccessAlertAsync() {
        Task { @MainActor in
            NSApp.activate(ignoringOtherApps: true)

            let alert = NSAlert()
            alert.messageText = "audio.alert.accessibility.title".localized
            alert.informativeText = "audio.alert.accessibility.message".localized
            alert.addButton(withTitle: "audio.alert.accessibility.open".localized)
            alert.addButton(withTitle: "common.cancel".localized)

            let response: NSApplication.ModalResponse

            if let window = RecordingWindowManager.shared.panel ?? NSApp.keyWindow ?? NSApp.windows.first(where: { $0.isVisible }) {
                // beginSheetModal returns immediately — does NOT block the main thread
                response = await withCheckedContinuation { continuation in
                    alert.beginSheetModal(for: window) { resp in
                        continuation.resume(returning: resp)
                    }
                }
            } else {
                // No visible window (rare — recording dialog should be open).
                // Fall back to runModal; since the app was just activated, the
                // dialog will be front-and-center so the user can dismiss it quickly.
                response = alert.runModal()
            }

            if response == .alertFirstButtonReturn {
                AccessibilityHelper.shared.openAccessibilitySettings()
            }
        }
    }

    /// Shows an alert when automatic paste fails
    ///
    /// **What This Does:**
    /// Displays a system alert informing the user that auto-paste failed
    /// and the text was copied to clipboard instead.
    ///
    /// **Parameters:**
    /// - `text`: The text that failed to paste (unused in current implementation)
    private func showPasteFailedAlert(_ text: String) {
        let alert = NSAlert()
        alert.messageText = "audio.alert.pasteFailed.title".localized
        alert.informativeText = "audio.alert.pasteFailed.message".localized
        alert.addButton(withTitle: "common.ok".localized)
        alert.runModal()
    }
}
