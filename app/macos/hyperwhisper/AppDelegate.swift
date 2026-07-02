//
//  AppDelegate.swift
//  hyperwhisper
//
//  Created by AI Assistant on 18/08/2025.
//

import Cocoa
import Sparkle

/// APP DELEGATE WITH ENHANCED UPDATE LOGGING
/// This AppDelegate implements SPUUpdaterDelegate to capture all Sparkle update events
/// and log them comprehensively for debugging production update failures.
///
/// Key responsibilities:
/// - Initialize and configure Sparkle updater
/// - Implement SPUUpdaterDelegate to monitor all update lifecycle events
/// - Log detailed information about each update step
/// - Capture and log any errors that occur during updates
final class AppDelegate: NSObject, NSApplicationDelegate {
    
    // MARK: - Properties
    
    /// Sparkle updater controller with delegate support
    /// We now pass 'self' as the updaterDelegate to receive all update events
    /// startingUpdater is set to false - we initialize based on user setting instead
    lazy var updaterController = SPUStandardUpdaterController(
        startingUpdater: false,  // Don't auto-start - controlled by user setting
        updaterDelegate: self,  // Now receiving delegate callbacks for logging
        userDriverDelegate: self  // Surface update alerts even if app is hidden
    )
    
    /// Logger instance for update events
    private let logger = UpdateLogger.shared
    
    /// Tracks whether we've started Sparkle's updater
    private var didStartUpdater = false

    /// Watches macOS memory pressure and reclaims idle local models on
    /// .warning/.critical. Retained for the process lifetime; behavior-neutral
    /// when there is no pressure.
    private var memoryPressureMonitor: MemoryPressureMonitor?
    
    // MARK: - Initialization
    
    override init() {
        super.init()
        
        // LOG INITIALIZATION
        // Record that the app delegate and updater are being initialized
        logger.info("AppDelegate initialized", context: [
            "sparkleVersion": "3.0",  // SPUSparkleVersionString not available in all versions
            "appVersion": Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown",
            "buildNumber": Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "unknown"
        ])
    }

    // MARK: - NSApplicationDelegate
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Initialize Sentry if DSN is configured and error logging is enabled
        let loggingEnabled = UserDefaults.standard.bool(forKey: "enableErrorLogging")
        if loggingEnabled {
            SentryService.initialize()
            let env = Bundle.main.object(forInfoDictionaryKey: "SentryEnvironment") as? String ?? "production"
            SentryService.setTag("environment", env)
        }
        
        // FIX: Monitor window closing to ensure app returns to accessory mode (hidden from Dock)
        // when "Show in Dock" is disabled and the last window is closed.
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(windowWillClose(_:)),
            name: NSWindow.willCloseNotification,
            object: nil
        )

        // LOCAL API SERVER: sleep/wake lifecycle. The server is opt-in (Settings →
        // API Server) and only starts when the toggle is on. Sleep releases the
        // bound port; we re-bind after wake if the toggle is still on. The
        // initial start happens after the main window appears and dependencies
        // are injected (see `LocalAPIServer.configure(...)`).
        NSWorkspace.shared.notificationCenter.addObserver(
            self,
            selector: #selector(systemWillSleep(_:)),
            name: NSWorkspace.willSleepNotification,
            object: nil
        )
        NSWorkspace.shared.notificationCenter.addObserver(
            self,
            selector: #selector(systemDidWake(_:)),
            name: NSWorkspace.didWakeNotification,
            object: nil
        )

        // MEMORY PRESSURE: reclaim idle local models (STT runtimes, local LLM)
        // when macOS signals .warning/.critical pressure. Behavior-neutral when
        // there is no pressure; never evicts a model mid-transcription.
        memoryPressureMonitor = MemoryPressureMonitor()
    }

    /// Cleanly shut down the Local API server so the port file is removed
    /// and FlyingFox releases the bound port, and flush any Core Data writes
    /// still queued on the serial background writer (e.g. a completion write
    /// racing a quick Cmd-Q right after paste).
    func applicationWillTerminate(_ notification: Notification) {
        LocalAPIServer.shared.stop()
        PersistenceController.shared.drainWriterOnTerminate()
    }

    /// Stops the Local API server while the system is going to sleep.
    @objc func systemWillSleep(_ notification: Notification) {
        Task { @MainActor in
            LocalAPIServer.shared.handleSystemWillSleep()
        }
    }

    /// Re-starts the Local API server on wake if the user toggle is still on.
    @objc func systemDidWake(_ notification: Notification) {
        Task { @MainActor in
            LocalAPIServer.shared.handleSystemDidWake()
        }
    }

    /// Handles window closing to manage Dock visibility
    @objc func windowWillClose(_ notification: Notification) {
        // Check user preference - if "Show in Dock" is enabled, we don't need to do anything
        let showInDock = UserDefaults.standard.bool(forKey: "showInDock")
        guard !showInDock else { return }
        
        guard let closingWindow = notification.object as? NSWindow else { return }
        
        // Check if any other significant windows are visible
        // We filter out:
        // 1. The window being closed
        // 2. Windows that can't become key (like overlays, tooltips)
        // 3. Invisible windows
        let openWindows = NSApplication.shared.windows.filter { window in
            return window.isVisible && window != closingWindow && window.canBecomeKey
        }
        
        if openWindows.isEmpty {
            // If no other windows are open, return to accessory mode
            // This ensures the app disappears from the Dock and App Switcher
            DispatchQueue.main.async {
                AppActivationPolicyController.apply(.accessory)
                
                // Also resign active to ensure focus goes back to previous app
                // This prevents the menu bar from getting stuck showing our app name
                AppActivationPolicyController.deactivateIfActive()
            }
        }
    }
    
    // MARK: - Public Methods

    /// Ensure Sparkle's updater is started before we interact with it.
    /// Sparkle requires startUpdater() to be called at least once when
    /// startingUpdater is set to false on the controller.
    private func ensureUpdaterStarted() {
        guard !didStartUpdater else { return }
        updaterController.startUpdater()
        didStartUpdater = true
        logger.info("Sparkle updater started")
    }

    /// Initialize the Sparkle updater based on user settings
    /// This should be called once during app launch to ensure Sparkle's state
    /// matches the user's auto-update preference from UserDefaults.
    ///
    /// INITIALIZATION FLOW:
    /// 1. Read checkForUpdatesAutomatically setting from UserDefaults
    /// 2. Configure Sparkle's automaticallyChecksForUpdates to match
    /// 3. If enabled, let Sparkle's scheduler check and download updates in
    ///    the background without presenting the scheduled "update found" alert
    ///
    /// This ensures the toggle setting and Sparkle state are always in sync
    /// from the very first app launch.
    func initializeUpdater() {
        ensureUpdaterStarted()

        let autoUpdateEnabled = UserDefaults.standard.bool(forKey: "checkForUpdatesAutomatically")

        logger.info("Initializing updater from user settings", context: [
            "autoUpdateEnabled": String(autoUpdateEnabled)
        ])

        // Sync Sparkle's setting with UserDefaults
        configureAutomaticChecks(enabled: autoUpdateEnabled)

        // Sparkle schedules background checks after startUpdater(). Avoid
        // forcing an extra launch check; scheduled UI alerts can synchronously
        // load Sparkle nibs on the main thread and have shown up as 10s app
        // hangs in Sentry (HYPERWHISPER-F7).
    }

    /// Configure whether Sparkle automatically checks for updates
    /// This method is called when:
    /// 1. App launches (via initializeUpdater)
    /// 2. User toggles the "Auto update" setting in preferences
    ///
    /// SPARKLE CONNECTION:
    /// Sets Sparkle's automatic check/download behavior. Keeping automatic
    /// downloads in sync with the app's "Auto update" toggle keeps scheduled
    /// checks off Sparkle's modal "update found" UI path; manual Check for
    /// Updates still uses the standard UI.
    func configureAutomaticChecks(enabled: Bool) {
        ensureUpdaterStarted()
        logger.info("Configuring automatic update checks", context: [
            "enabled": String(enabled)
        ])
        updaterController.updater.automaticallyChecksForUpdates = enabled
        updaterController.updater.automaticallyDownloadsUpdates = enabled
    }
    
    func setAutomaticallyDownloadsUpdates(_ enabled: Bool) {
        ensureUpdaterStarted()
        logger.info("Configuring automatic downloads", context: [
            "enabled": String(enabled)
        ])
        updaterController.updater.automaticallyDownloadsUpdates = enabled
    }
    
    func checkForUpdates() {
        ensureUpdaterStarted()
        logger.logUpdateCheckStarted(automatic: false)
        updaterController.checkForUpdates(nil)
    }
    
    func checkForUpdatesInBackground() {
        ensureUpdaterStarted()
        logger.logUpdateCheckStarted(automatic: true)
        updaterController.updater.checkForUpdatesInBackground()
    }
}

// MARK: - SPUUpdaterDelegate

/// SPARKLE UPDATER DELEGATE IMPLEMENTATION
/// This extension implements all available SPUUpdaterDelegate methods to capture
/// comprehensive information about the update process. Each method logs relevant
/// details to help diagnose update failures in production.
extension AppDelegate: SPUUpdaterDelegate {
    
    // MARK: - Update Check Events
    
    /// Called when updater starts checking for updates
    func updater(_ updater: SPUUpdater, didFinishLoading appcast: SUAppcast) {
        logger.info("Appcast loaded successfully", context: [
            "itemCount": String(appcast.items.count),
            "appcastURL": updater.feedURL?.absoluteString ?? "unknown"
        ])
        
        // Log details about available updates
        for item in appcast.items {
            logger.debug("Appcast item found", context: [
                "version": item.versionString,
                "build": item.displayVersionString,
                "minimumSystemVersion": item.minimumSystemVersion ?? "none",
                "maximumSystemVersion": item.maximumSystemVersion ?? "none"
            ])
        }
    }
    
    /// Called when an update is found
    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        logger.logUpdateFound(
            version: item.displayVersionString,
            releaseNotes: item.releaseNotesURL?.absoluteString
        )
        
        // Log additional update details
        logger.info("Update details", context: [
            "fileURL": item.fileURL?.absoluteString ?? "unknown",
            "fileSize": String(item.contentLength),
            "criticalUpdate": String(item.isCriticalUpdate)
        ])
    }
    
    /// Called when no update is available
    func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
        logger.info("No update available", context: [
            "currentVersion": Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown",
            "lastCheckTime": ISO8601DateFormatter().string(from: Date())
        ])
    }
    
    /// Called when the user cancels the update
    func userDidCancelDownload(_ updater: SPUUpdater) {
        logger.info("User cancelled update download")
    }
    
    // MARK: - Download Events
    
    /// Called periodically during download with progress
    func updater(_ updater: SPUUpdater, didDownloadUpdate item: SUAppcastItem) {
        logger.logDownloadCompleted()
        logger.info("Update ready for installation", context: [
            "version": item.displayVersionString,
            "downloadSize": String(item.contentLength)
        ])
    }
    
    /// Called when download fails
    func updater(_ updater: SPUUpdater, failedToDownloadUpdate item: SUAppcastItem, error: Error) {
        logger.logDownloadFailed(error: error as NSError)
        
        // Log additional context about the failure
        logger.error("Download failure details", error: error as NSError, context: [
            "updateVersion": item.displayVersionString,
            "fileURL": item.fileURL?.absoluteString ?? "unknown",
            "expectedSize": String(item.contentLength)
        ])
    }
    
    // MARK: - Installation Events
    
    /// Called when update will be installed
    func updater(_ updater: SPUUpdater, willInstallUpdate item: SUAppcastItem) {
        logger.logInstallationStarted(version: item.displayVersionString)
    }
    
    /// Called when installation succeeds and app will relaunch
    func updater(_ updater: SPUUpdater, willInstallUpdateOnQuit item: SUAppcastItem, immediateInstallationInvocation: @escaping () -> Void) {
        logger.info("Update will install on quit", context: [
            "version": item.displayVersionString
        ])
        
        // Store the immediate installation block if needed
        // This can be called later to force immediate installation
    }
    
    /// Called after successful installation
    func updater(_ updater: SPUUpdater, didInstallUpdate item: SUAppcastItem) {
        logger.logInstallationCompleted()
        logger.info("Update installed successfully", context: [
            "version": item.displayVersionString,
            "willRelaunch": "true"
        ])
    }
    
    // MARK: - Error Handling
    
    /// Called when any error occurs during the update process
    func updater(_ updater: SPUUpdater, didAbortWithError error: Error) {
        let nsError = error as NSError
        
        // Determine the phase where error occurred based on error details
        let phase = determineUpdatePhase(from: nsError)
        
        logger.critical("Update aborted with error", error: nsError, context: [
            "phase": phase,
            "feedURL": updater.feedURL?.absoluteString ?? "unknown",
            "automaticChecks": String(updater.automaticallyChecksForUpdates),
            "sessionInProgress": String(updater.sessionInProgress)
        ])
        
        // Log specific error scenarios
        switch nsError.code {
        case 1001: // SUNoUpdateError
            logger.info("No update available (not an error)")
        case 1002: // SUAppcastError
            logger.error("Appcast error - check feed URL and server", error: nsError)
        case 1003: // SURunningFromDiskImageError
            logger.warning("App running from disk image - cannot update", context: [
                "appPath": Bundle.main.bundlePath
            ])
        case 2000: // SURelaunchError
            logger.critical("Failed to relaunch after update", error: nsError)
        case 2001: // SUInstallationError
            logger.critical("Installation failed", error: nsError, context: [
                "permissions": checkAppPermissions()
            ])
        case 3000: // SUDownloadError
            logger.error("Download failed", error: nsError, context: [
                "networkStatus": checkNetworkStatus()
            ])
        case 4000: // SUSignatureError
            logger.critical("Signature verification failed", error: nsError, context: [
                "publicKey": checkPublicKeyPresence()
            ])
        default:
            logger.error("Unknown error during update", error: nsError)
        }
    }
    
    /// Called to check if update should be automatically downloaded
    func updater(_ updater: SPUUpdater, shouldDownloadUpdate item: SUAppcastItem) -> Bool {
        let shouldDownload = true // Default behavior
        
        logger.info("Checking if update should be downloaded", context: [
            "version": item.displayVersionString,
            "decision": String(shouldDownload),
            "automaticDownloads": String(updater.automaticallyDownloadsUpdates)
        ])
        
        return shouldDownload
    }
    
    // MARK: - Helper Methods
    
    /// Determines which phase of the update process an error occurred in
    private func determineUpdatePhase(from error: NSError) -> String {
        let code = error.code
        let domain = error.domain
        
        if domain == "SUSparkleErrorDomain" {
            switch code {
            case 1000...1999: return "initialization"
            case 2000...2999: return "installation"
            case 3000...3999: return "download"
            case 4000...4999: return "verification"
            case 5000...5999: return "extraction"
            default: return "unknown"
            }
        } else if domain == NSURLErrorDomain {
            return "network"
        } else if domain == NSCocoaErrorDomain {
            return "filesystem"
        }
        
        return "unknown"
    }
    
    /// Checks app bundle permissions
    private func checkAppPermissions() -> String {
        let bundleURL = Bundle.main.bundleURL
        let isWritable = FileManager.default.isWritableFile(atPath: bundleURL.path)
        let isReadable = FileManager.default.isReadableFile(atPath: bundleURL.path)
        let isDeletable = FileManager.default.isDeletableFile(atPath: bundleURL.path)
        
        return "r=\(isReadable),w=\(isWritable),d=\(isDeletable)"
    }
    
    /// Checks network status (basic check)
    private func checkNetworkStatus() -> String {
        // This is a simple check - in production you might use SCNetworkReachability
        if let feedURL = updaterController.updater.feedURL,
           let _ = try? Data(contentsOf: feedURL) {
            return "reachable"
        }
        return "unreachable"
    }
    
    /// Checks if public key is present for signature verification
    private func checkPublicKeyPresence() -> String {
        if let _ = Bundle.main.path(forResource: "dsa_pub", ofType: "pem") {
            return "DSA key present"
        } else if let _ = Bundle.main.path(forResource: "ed25519_pub", ofType: "pem") {
            return "EdDSA key present"
        }
        return "No public key found"
    }
}

// MARK: - SPUStandardUserDriverDelegate

extension AppDelegate: SPUStandardUserDriverDelegate {
    func standardUserDriverWillShowModalAlert() {
        DispatchQueue.main.async {
            // Ensure any Sparkle alert appears in front of other apps
            NSApp.activate(ignoringOtherApps: true)

            if let primaryWindow = NSApp.mainWindow ?? NSApp.windows.first(where: { $0.canBecomeKey }) {
                primaryWindow.makeKeyAndOrderFront(nil)
            } else {
                NSApp.windows.first?.orderFrontRegardless()
            }
        }
    }
}
