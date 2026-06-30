//
//  LaunchAtLoginManager.swift
//  hyperwhisper
//
//  Native launch-at-login manager using SMAppService.
//  Replaces the third-party LaunchAtLogin-Modern package.
//
//  WHY NATIVE IMPLEMENTATION:
//  The LaunchAtLogin package uses computed Binding(get:set:) patterns
//  that trigger Swift Concurrency executor isolation checks during
//  SwiftUI's layout computation phase on macOS 26.2 (Build 25C56).
//  This causes an infinite recursion in SerialExecutor.isMainExecutor.getter,
//  resulting in a stack overflow crash.
//
//  SOLUTION:
//  Using a simple enum with static methods avoids the computed binding
//  pattern entirely. Views use local @State with onAppear/onChange to
//  sync with this manager, preventing the executor isolation check loop.
//
//  Sentry Issue: HYPERWHISPER-3V
//

import Foundation
import ServiceManagement
import os

/// Native launch-at-login manager using SMAppService
///
/// ARCHITECTURE:
/// This is a stateless utility enum that wraps SMAppService.mainApp.
/// It provides simple synchronous get/set operations for launch-at-login state.
///
/// USAGE:
/// ```swift
/// // Check current state
/// let isEnabled = LaunchAtLoginManager.isEnabled
///
/// // Enable/disable
/// LaunchAtLoginManager.setEnabled(true)
/// ```
///
/// INTEGRATION WITH SWIFTUI:
/// Views should NOT create computed bindings to this manager.
/// Instead, use local @State with onAppear/onChange:
///
/// ```swift
/// @State private var launchAtLoginEnabled = false
///
/// Toggle(isOn: $launchAtLoginEnabled) { ... }
///     .onAppear {
///         launchAtLoginEnabled = LaunchAtLoginManager.isEnabled
///     }
///     .onChange(of: launchAtLoginEnabled) { _, newValue in
///         LaunchAtLoginManager.setEnabled(newValue)
///     }
/// ```
enum LaunchAtLoginManager {

    // MARK: - Logger

    private static let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LaunchAtLogin")

    // MARK: - Public API

    /// Whether launch at login is currently enabled
    ///
    /// IMPLEMENTATION:
    /// Checks SMAppService.mainApp.status directly.
    ///
    /// Both `.enabled` and `.requiresApproval` count as enabled: in the latter,
    /// register() succeeded and the login item exists, but macOS is gating
    /// activation behind the user's approval in System Settings → Login Items &
    /// Extensions (common after Migration Assistant or an app rename).
    /// Collapsing `.requiresApproval` to false made the toggle bounce back to OFF
    /// with no way forward (#288); reporting true keeps the toggle ON while
    /// setEnabled(true) routes the user to the approval UI.
    ///
    /// `.notRegistered` and `.notFound` remain disabled.
    static var isEnabled: Bool {
        switch SMAppService.mainApp.status {
        case .enabled, .requiresApproval:
            return true
        default:
            return false
        }
    }

    /// Enable or disable launch at login
    ///
    /// IMPLEMENTATION:
    /// - Calls SMAppService.mainApp.register() to enable
    /// - Calls SMAppService.mainApp.unregister() to disable
    ///
    /// ERROR HANDLING:
    /// Errors are logged but not thrown. This matches the behavior of
    /// the LaunchAtLogin package, which also silently handles errors.
    /// Common errors include:
    /// - User denied permission in System Preferences
    /// - App is in a location that doesn't support login items
    ///
    /// - Parameter enabled: Whether to enable or disable launch at login
    static func setEnabled(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
                logger.info("✅ Launch at login enabled")

                // register() can succeed while macOS still requires the user to
                // approve the login item in System Settings (e.g. after Migration
                // Assistant or an app rename). Without surfacing this, the feature
                // silently never activates and the user has no way forward (#288).
                // Open the approval pane so they can complete enabling.
                if SMAppService.mainApp.status == .requiresApproval {
                    logger.info("Launch at login requires approval — opening Login Items settings")
                    openLoginItemsSettings()
                }
            } else {
                try SMAppService.mainApp.unregister()
                logger.info("✅ Launch at login disabled")
            }
        } catch {
            // Log error but don't throw - matches LaunchAtLogin package behavior
            // The setting may fail silently if user denies permission or app
            // is in an unsupported location
            logger.error("Failed to \(enabled ? "enable" : "disable") launch at login: \(error.localizedDescription)")
        }
    }

    // MARK: - Private Helpers

    /// Opens System Settings → General → Login Items & Extensions.
    ///
    /// Dispatched to the main queue because it presents UI; setEnabled stays
    /// synchronous so callers can read `isEnabled` immediately afterwards.
    private static func openLoginItemsSettings() {
        DispatchQueue.main.async {
            SMAppService.openSystemSettingsLoginItems()
        }
    }
}
