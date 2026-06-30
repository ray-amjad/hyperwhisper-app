//
//  GeneralSettingsManager.swift
//  hyperwhisper
//
//  GENERAL SETTINGS MANAGER
//  Manages general app behavior settings including launch preferences,
//  dock visibility, update checking, and error logging.
//
//  RESPONSIBILITIES:
//  - Launch at login configuration (via LaunchAtLogin package)
//  - Dock visibility toggle
//  - Window display preferences
//  - Automatic update checks
//  - Error logging via Sentry
//
//  ARCHITECTURE:
//  - @AppStorage for automatic UserDefaults persistence
//  - Observable for reactive UI updates
//  - LaunchAtLogin package for reliable login item management
//

import Foundation
import SwiftUI
import Combine
import os

/// Manages general application settings and behavior
@MainActor
class GeneralSettingsManager: ObservableObject {

    // MARK: - Logger

    /// Logger for general settings operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "GeneralSettings")

    // MARK: - Launch & Startup Settings

    /// Whether to launch the app at login
    /// Routes through LaunchAtLoginManager (native SMAppService wrapper) to avoid the
    /// LaunchAtLogin package's Binding(get:set:) pattern, which infinite-recurses through
    /// SerialExecutor.isMainExecutor.getter on macOS 26.2 (Sentry HYPERWHISPER-3V).
    var launchAtLogin: Bool {
        get { LaunchAtLoginManager.isEnabled }
        set { LaunchAtLoginManager.setEnabled(newValue) }
    }

    /// Whether to show the app in the dock
    /// When disabled, app runs as menu bar only application
    @AppStorage("showInDock") var showInDock: Bool = true {
        didSet {
            updateDockVisibility()
        }
    }

    /// Whether to launch with the main window hidden (menu bar only)
    /// Allows app to start minimized without showing main window
    @AppStorage("launchMinimized") var launchMinimized: Bool = false

    // MARK: - Window & UI Settings

    /// Whether to show the recording window during capture/processing
    /// When disabled, transcription happens in background
    @AppStorage("showRecordingWindow") var showRecordingWindow: Bool = true

    // MARK: - Recording Dialog Position
    //
    // Position stored as ratios (0.0-1.0) of available screen space rather than absolute pixels.
    // This allows the dialog to maintain relative position across resolution/monitor changes.

    /// X ratio (0.0 = left, 0.5 = centered, 1.0 = right)
    @AppStorage("recordingDialogPositionXRatio") var recordingDialogPositionXRatio: Double?

    /// Y ratio (0.0 = bottom, 0.5 = centered, 1.0 = top)
    @AppStorage("recordingDialogPositionYRatio") var recordingDialogPositionYRatio: Double?

    // MARK: - Update & Maintenance Settings

    /// Whether to check for updates automatically
    /// Controls automatic update checking on app launch
    @AppStorage("checkForUpdatesAutomatically") var checkForUpdatesAutomatically: Bool = true

    /// Whether to enable error logging
    /// When enabled, errors are sent to Sentry for diagnostics
    @AppStorage("enableErrorLogging") var enableErrorLogging: Bool = true {
        didSet {
            // Initialize Sentry on enable so errors start flowing without app restart
            if enableErrorLogging {
                SentryService.initialize()
                let env = Bundle.main.object(forInfoDictionaryKey: "SentryEnvironment") as? String
                SentryService.setTag("environment", env ?? (NetworkConfig.isDevelopment ? "development" : "production"))
            }
        }
    }

    // MARK: - Voice Activity Detection Settings

    /// Whether to enable Voice Activity Detection (VAD) for silence trimming
    ///
    /// VAD FEATURE:
    /// When enabled, audio is analyzed using Silero VAD before transcription.
    /// Leading and trailing silence is removed, which:
    /// - Reduces API costs (less audio to process)
    /// - Improves transcription speed
    /// - May improve accuracy (less noise for the model)
    ///
    /// IMPLEMENTATION:
    /// Uses whisper.cpp's standalone Silero VAD API via VoiceActivityDetector.
    /// The VAD model (~864KB) is bundled with the app.
    ///
    /// DEFAULT: false (disabled by default - users can enable in Settings → Sound)
    @AppStorage("enableVAD") var enableVAD: Bool = false

    // MARK: - Private Methods

    /// Update dock visibility
    /// Posts notification to main app to update NSApplication.activationPolicy
    ///
    /// NOTIFICATION FLOW:
    /// 1. Posts updateDockVisibility notification
    /// 2. hyperwhisperApp.swift receives notification
    /// 3. Updates NSApplication.shared.setActivationPolicy()
    /// 4. Dock icon appears/disappears immediately
    private func updateDockVisibility() {
        // Post notification to update dock visibility in the main app
        NotificationCenter.default.post(
            name: .updateDockVisibility,
            object: nil,
            userInfo: ["showInDock": showInDock]
        )
        logger.info("✅ Show in dock: \(self.showInDock, privacy: .public)")
    }
}
