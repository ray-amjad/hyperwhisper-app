//
//  AccessibilityHelper+Permissions.swift
//  hyperwhisper
//
//  Created by Assistant on 16/08/2025.
//

import Foundation
import AppKit
import ApplicationServices
import os

// MARK: - Notification Names

extension Notification.Name {
    /// Posted when accessibility permission is granted after polling
    static let accessibilityPermissionGranted = Notification.Name("accessibilityPermissionGranted")
}

extension AccessibilityHelper {

    // MARK: - Accessibility Permission Methods

    /// Check if app has accessibility permission
    /// - Returns: true if permission is granted, false otherwise
    func hasAccessibilityPermission() -> Bool {
        let trusted = AXIsProcessTrusted()

        // Additional diagnostic info (print once to reduce noise)
        if !trusted && !hasLoggedPermissionGuidance {
            hasLoggedPermissionGuidance = true
            logger.warning("⚠️ App is NOT trusted. Check System Settings > Privacy & Security > Accessibility")
            logger.warning("⚠️ Look for entries matching:")
            logger.warning("   - HyperWhisper (from /Applications)")
            logger.warning("   - hyperwhisper (from Xcode DerivedData)")
            logger.warning("⚠️ You may have multiple entries - enable the one matching current path")
        }

        return trusted
    }

    /// Check and optionally prompt for accessibility permission
    /// - Parameter prompt: If true, shows system dialog to user
    /// - Returns: true if permission is granted, false otherwise
    func checkAccessibilityPermission(prompt: Bool = false) -> Bool {
        logger.debug("🔍 AccessibilityHelper.checkAccessibilityPermission(prompt: \(prompt, privacy: .public))")

        if prompt {
            // Prompt user if not already trusted
            // CRITICAL FIX: Use takeRetainedValue() for proper memory management
            // This matches the working implementation in tryswift2024-main
            let promptKey = kAXTrustedCheckOptionPrompt.takeRetainedValue() as String
            logger.debug("   Using prompt key: \(promptKey, privacy: .public)")
            let options: NSDictionary = [promptKey: true]
            let result = AXIsProcessTrustedWithOptions(options)
            logger.debug("   AXIsProcessTrustedWithOptions returned: \(result, privacy: .public)")

            // Double-check with the non-prompt version
            let doubleCheck = AXIsProcessTrusted()
            if result != doubleCheck {
                logger.warning("⚠️ MISMATCH: AXIsProcessTrustedWithOptions=\(result, privacy: .public) vs AXIsProcessTrusted=\(doubleCheck, privacy: .public)")
            }

            return result
        } else {
            // Just check without prompting
            let result = AXIsProcessTrusted()
            // Intentionally avoid repeated logging here to reduce noise
            return result
        }
    }

    /// Open System Settings to the Accessibility pane
    /// This is the standard way to guide users to enable accessibility
    func openAccessibilitySettings() {
        logger.info("🔧 Opening System Settings > Privacy & Security > Accessibility...")

        // Try the modern System Settings URL first (macOS 13+)
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            logger.info("   Using URL: \(url, privacy: .public)")
            let opened = NSWorkspace.shared.open(url)
            logger.info("   Settings opened: \(opened, privacy: .public)")

            if opened {
                logger.info("   ℹ️ IMPORTANT: Look for 'hyperwhisper' or 'HyperWhisper' in the list")
                logger.info("   ℹ️ Enable the entry that matches: \(Bundle.main.bundlePath, privacy: .public)")
            }
        }
    }

    /// Maximum time to keep polling for accessibility permission before giving up
    static let permissionPollingTimeout: TimeInterval = 600 // 10 minutes

    /// Wait for accessibility permission to be granted
    /// This polls every 0.3 seconds until permission is granted, with a timeout
    /// so an abandoned prompt doesn't poll for the rest of the app's lifetime.
    /// Concurrent calls share a single polling loop instead of spawning
    /// parallel timer chains; each caller's completion is queued and fired once.
    /// - Parameter completion: Called with `true` when permission is granted,
    ///   or `false` if polling timed out or was cancelled
    func waitForAccessibilityPermission(completion: @escaping (Bool) -> Void) {
        permissionPollingCompletions.append(completion)
        // Each caller represents a fresh prompt, so restart the shared deadline —
        // a late joiner must not inherit a nearly-expired timeout from an older
        // abandoned prompt
        permissionPollingDeadline = Date().addingTimeInterval(Self.permissionPollingTimeout)

        guard permissionPollingTask == nil else {
            logger.debug("⏳ Accessibility polling already active — queued completion and extended the deadline instead of starting a duplicate loop")
            return
        }

        logger.info("⏳ Starting accessibility permission polling...")
        permissionPollingTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                if AXIsProcessTrusted() {
                    self.finishPermissionPolling(granted: true)
                    return
                }
                if let deadline = self.permissionPollingDeadline, Date() >= deadline {
                    self.logger.warning("⌛️ Accessibility permission polling timed out — stopping")
                    self.finishPermissionPolling(granted: false)
                    return
                }
                try? await Task.sleep(nanoseconds: 300_000_000)
            }
            // Cancelled: cancelAccessibilityPermissionPolling() already tore down
            // the shared state synchronously, so exit silently
        }
    }

    /// Cancel any active permission polling (e.g. when the prompting view goes away)
    /// Queued completions are called with `false`
    func cancelAccessibilityPermissionPolling() {
        guard permissionPollingTask != nil else { return }
        permissionPollingTask?.cancel()
        finishPermissionPolling(granted: false)
    }

    /// Tear down the shared polling loop and fire all queued completions once
    private func finishPermissionPolling(granted: Bool) {
        permissionPollingTask = nil
        let completions = permissionPollingCompletions
        permissionPollingCompletions.removeAll()

        if granted {
            logger.info("✅ Accessibility permission GRANTED! Posting notification...")
            NotificationCenter.default.post(name: .accessibilityPermissionGranted, object: nil)
        }
        completions.forEach { $0(granted) }
    }
}
