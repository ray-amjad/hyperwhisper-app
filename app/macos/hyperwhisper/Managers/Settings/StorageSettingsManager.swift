//
//  StorageSettingsManager.swift
//  hyperwhisper
//
//  STORAGE SETTINGS MANAGER
//  Manages file storage locations, folder permissions, and fallback strategies
//  for recordings and app data.
//
//  RESPONSIBILITIES:
//  - App folder location management
//  - Recordings folder configuration
//  - TCC (Transparency, Consent, and Control) permission handling
//  - Automatic fallback to safe locations
//  - Manual folder selection
//
//  ARCHITECTURE:
//  - @AppStorage for folder path persistence
//  - TCC-aware permission prompts
//  - Multi-tier fallback strategy (Documents → App Support → Downloads → Temp)
//  - User education for macOS permission dialogs
//
//  PERMISSION FLOW:
//  1. First launch: Default to Documents/hyperwhisper/recordings
//  2. Before creating: Show explanation of why macOS will prompt
//  3. User proceeds: Attempt creation (triggers TCC)
//  4. TCC granted: Use Documents folder
//  5. TCC denied: Fallback to Application Support (no TCC required)
//

import Foundation
import SwiftUI
import AppKit
import os

/// Manages storage locations and folder permissions
@MainActor
class StorageSettingsManager: ObservableObject {

    // MARK: - Logger

    /// Logger for storage settings operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "StorageSettings")

    // MARK: - Folder Paths

    /// App folder location for storing data (models, logs, etc.)
    /// Default: ~/Library/Application Support/HyperWhisper (no TCC required)
    @AppStorage("appFolderPath") var appFolderPath: String = {
        // Default to Application Support/HyperWhisper (does not require TCC)
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return appSupport.appendingPathComponent("HyperWhisper").path
    }()

    /// Recordings folder location for storing audio files
    /// Default: ~/Documents/hyperwhisper/recordings (requires TCC on first access)
    @AppStorage("recordingsFolder") var recordingsFolder: String = {
        // Default to Documents/hyperwhisper/recordings
        let documentsPath = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first!
        return documentsPath.appendingPathComponent("hyperwhisper/recordings").path
    }()

    // MARK: - Permission Tracking

    /// Whether we've shown our custom explanation for Documents folder access
    /// Prevents showing the explanation multiple times
    @AppStorage("documentsPermissionExplained") var documentsPermissionExplained: Bool = false

    /// Whether the user explicitly denied Documents access (TCC)
    /// When true, we skip Documents folder and use Application Support
    @AppStorage("documentsAccessDenied") var documentsAccessDenied: Bool = false

    /// Whether the user explicitly chose an alternate storage location
    /// When true, we respect their choice and don't prompt again
    @AppStorage("userChoseAlternateStorage") var userChoseAlternateStorage: Bool = false

    /// Controls the in-app alert that explains why macOS may ask for Documents access
    /// Shows before triggering the system TCC prompt
    @Published var showDocumentsPermissionAlert: Bool = false

    // MARK: - Feature Flags

    /// Whether Filesync is enabled
    /// When enabled, recordings sync across devices
    @AppStorage("filesyncEnabled") var filesyncEnabled: Bool = false

    /// Whether to compress WAV recordings to M4A after transcription
    /// When ON: Background converts WAV→M4A after successful transcription, deletes WAV
    /// When OFF: Keeps WAV files as-is (larger files, but no conversion overhead)
    /// Default: ON (reduces storage usage by ~10x)
    @AppStorage("storeAsM4A") var storeAsM4A: Bool = true

    // MARK: - Published Error State

    /// Validation error for folder operations
    /// Displayed in UI when folder creation or permission checks fail
    @Published var validationError: String?

    // MARK: - Initialization

    init() {
        // Create app folder if it doesn't exist (safe location, no TCC required)
        createAppFolderIfNeeded()

        // Defer creating the recordings folder so we can show an
        // explanation before macOS prompts for Documents access
    }

    // MARK: - Public Methods

    /// Change recordings folder location
    /// - Parameter url: The new folder URL
    /// - Note: This does NOT migrate existing recordings, only affects new recordings
    func changeRecordingsFolder(to url: URL) {
        let newPath = url.path

        // Create folder if it doesn't exist
        if !FileManager.default.fileExists(atPath: newPath) {
            do {
                try FileManager.default.createDirectory(
                    at: url,
                    withIntermediateDirectories: true
                )
                logger.info("✅ Created recordings folder at: \(newPath, privacy: .public)")
            } catch {
                logger.error("❌ Failed to create recordings folder: \(error.localizedDescription, privacy: .public)")
                validationError = "Failed to create folder: \(error.localizedDescription)"
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Recordings folder creation failed",
                        category: "settings.storage",
                        level: .error,
                        data: [
                            "path": newPath,
                            "errorDescription": error.localizedDescription
                        ]
                    )
                }
                return
            }
        }

        // Verify write permissions
        if !FileManager.default.isWritableFile(atPath: newPath) {
            validationError = "Cannot write to selected folder. Please check permissions."
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Recordings folder not writable",
                    category: "settings.storage",
                    level: .warning,
                    data: ["path": newPath]
                )
            }
            return
        }

        recordingsFolder = newPath
        logger.info("✅ Recordings folder changed to: \(newPath, privacy: .public)")
        if AppLogger.isErrorLoggingEnabled {
            SentryService.addBreadcrumb(
                message: "Recordings folder changed",
                category: "settings.storage",
                data: ["path": newPath]
            )
        }
    }

    /// Ensure recordings folder exists
    /// Creates folder if it doesn't exist, handling permissions appropriately
    func ensureRecordingsFolderExists() {
        let url = URL(fileURLWithPath: recordingsFolder)

        if !FileManager.default.fileExists(atPath: recordingsFolder) {
            do {
                try FileManager.default.createDirectory(
                    at: url,
                    withIntermediateDirectories: true
                )
                logger.info("✅ Created recordings folder at: \(self.recordingsFolder, privacy: .public)")
            } catch {
                logger.error("❌ Failed to create recordings folder: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// Prepare the recordings folder with an explanatory flow if needed.
    /// If the configured folder is under Documents, show an explanation before
    /// triggering the system prompt. If access is denied, fall back to
    /// Application Support.
    ///
    /// PERMISSION FLOW:
    /// 1. Check if folder already exists and is writable → done
    /// 2. If user chose alternate storage → use best fallback
    /// 3. If folder is in Documents and not explained → show alert first
    /// 4. Otherwise attempt creation (may trigger TCC) → fallback on error
    func prepareRecordingsFolderIfNeeded() {
        let recordingsURL = URL(fileURLWithPath: recordingsFolder)
        let documentsURL = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first!

        // If the folder already exists and is writable, nothing to do.
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: recordingsURL.path, isDirectory: &isDir), isDir.boolValue,
           FileManager.default.isWritableFile(atPath: recordingsURL.path) {
            return
        }

        // If the user already chose an alternate location, don't re-prompt.
        if userChoseAlternateStorage {
            _ = fallbackToBestAvailableLocation()
            return
        }

        // If it lives in Documents and we haven't explained yet, show alert first.
        if recordingsURL.path.hasPrefix(documentsURL.path) && !documentsPermissionExplained && !documentsAccessDenied {
            showDocumentsPermissionAlert = true
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Documents access explanation shown",
                    category: "settings.storage",
                    data: ["path": recordingsURL.path]
                )
            }
            return
        }

        // Otherwise attempt to create (may trigger TCC). On failure, fall back.
        do {
            try FileManager.default.createDirectory(at: recordingsURL, withIntermediateDirectories: true)
        } catch {
            // Permission denied or other error → try fallbacks in order
            _ = fallbackToBestAvailableLocation()
        }
    }

    /// Async helper that prepares the recordings folder and waits for resolution.
    /// Returns true if a writable folder is ready, false otherwise.
    ///
    /// TIMEOUT HANDLING:
    /// Waits up to `timeoutSeconds` (default 120s) for user to respond to alerts
    /// Polls every 200-300ms to check if folder is ready
    ///
    /// - Parameter timeoutSeconds: Maximum wait time (default 120s)
    /// - Returns: True if folder is ready and writable
    func prepareRecordingsFolderIfNeededAsync(timeoutSeconds: Double = 120) async -> Bool {
        prepareRecordingsFolderIfNeeded()
        let start = Date()
        while Date().timeIntervalSince(start) < timeoutSeconds {
            let url = URL(fileURLWithPath: recordingsFolder)
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue,
               FileManager.default.isWritableFile(atPath: url.path) {
                return true
            }
            // If alert is showing, wait for user to respond
            if showDocumentsPermissionAlert {
                try? await Task.sleep(nanoseconds: 200_000_000) // 0.2s
                continue
            }
            // If we aren't showing an alert and not writable, try fallbacks
            if fallbackToBestAvailableLocation() {
                return true
            }
            try? await Task.sleep(nanoseconds: 300_000_000)
        }
        return false
    }

    /// User chose to proceed with saving in Documents after explanation.
    /// Attempts to create folder in Documents, falls back on TCC denial
    func proceedWithDocumentsAccess() {
        documentsPermissionExplained = true
        showDocumentsPermissionAlert = false
        let target = URL(fileURLWithPath: recordingsFolder)
        do {
            try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        } catch {
            // Likely denied by the system — switch to a safe location
            documentsAccessDenied = true
            _ = fallbackToBestAvailableLocation()
        }
    }

    /// User declined Documents access; switch to a safe storage location.
    /// Sets flags and immediately falls back to Application Support
    func useAlternateStorageInstead() {
        documentsPermissionExplained = true
        userChoseAlternateStorage = true
        showDocumentsPermissionAlert = false
        _ = fallbackToBestAvailableLocation()
    }

    /// Try fallbacks in order: Application Support → Downloads → Temp
    /// Returns true if a fallback succeeded, false if all failed
    ///
    /// FALLBACK STRATEGY:
    /// 1. Application Support (best, no TCC required)
    /// 2. Downloads (acceptable, may require TCC on some systems)
    /// 3. Temp (last resort, cleared on reboot)
    @discardableResult
    func fallbackToBestAvailableLocation() -> Bool {
        if fallbackRecordingsLocationToAppSupport() { return true }
        if fallbackRecordingsLocationToDownloads() { return true }
        if fallbackRecordingsLocationToTemp() { return true }
        return false
    }

    /// Offer manual folder selection when all automatic options fail
    /// Presents NSOpenPanel for user to choose a writable location
    ///
    /// - Returns: True if user selected a folder, false if cancelled
    @MainActor
    func offerManualFolderSelection() -> Bool {
        let panel = NSOpenPanel()
        panel.title = "storage.folder.choose.title".localized
        panel.message = "storage.folder.choose.message".localized
        panel.prompt = "storage.folder.choose.prompt".localized
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        let response = panel.runModal()
        if response == .OK, let url = panel.url {
            changeRecordingsFolder(to: url)
            return true
        }
        return false
    }

    /// Present a recovery alert and offer manual folder selection
    /// Called when all automatic fallbacks have failed
    ///
    /// - Returns: True if user selected a folder, false otherwise
    @MainActor
    func presentStorageRecoveryPrompt() -> Bool {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "storage.recovery.title".localized
        alert.informativeText = "storage.recovery.message".localized
        alert.addButton(withTitle: "storage.recovery.choose".localized)
        alert.addButton(withTitle: "common.cancel".localized)
        let result = alert.runModal()
        if result == .alertFirstButtonReturn {
            return offerManualFolderSelection()
        }
        return false
    }

    // MARK: - Private Methods

    /// Create app folder if it doesn't exist
    /// This is safe to call anytime - Application Support doesn't require TCC
    private func createAppFolderIfNeeded() {
        let url = URL(fileURLWithPath: appFolderPath)

        if !FileManager.default.fileExists(atPath: url.path) {
            do {
                try FileManager.default.createDirectory(
                    at: url,
                    withIntermediateDirectories: true
                )
                logger.info("✅ Created app folder at: \(self.appFolderPath, privacy: .public)")
            } catch {
                logger.error("❌ Failed to create app folder: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// Point recordings to Application Support, which does not require TCC approval.
    /// BEST FALLBACK: No permission prompt, persistent storage
    ///
    /// - Returns: True if successful, false otherwise
    private func fallbackRecordingsLocationToAppSupport() -> Bool {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let base = appSupport.appendingPathComponent("HyperWhisper", isDirectory: true)
        let recs = base.appendingPathComponent("recordings", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: recs, withIntermediateDirectories: true)
            appFolderPath = base.path
            recordingsFolder = recs.path
            logger.info("↩️ Using Application Support for recordings: \(self.recordingsFolder, privacy: .public)")
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Storage fallback selected",
                    category: "settings.storage",
                    data: [
                        "fallback": "application_support",
                        "path": self.recordingsFolder
                    ]
                )
            }
            return true
        } catch {
            return false
        }
    }

    /// Fallback to Downloads folder
    /// ACCEPTABLE FALLBACK: User-visible location, may require TCC
    ///
    /// - Returns: True if successful, false otherwise
    private func fallbackRecordingsLocationToDownloads() -> Bool {
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first!
        let recs = downloads.appendingPathComponent("HyperWhisper Recordings", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: recs, withIntermediateDirectories: true)
            recordingsFolder = recs.path
            logger.info("↩️ Using Downloads for recordings: \(self.recordingsFolder, privacy: .public)")
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Storage fallback selected",
                    category: "settings.storage",
                    data: [
                        "fallback": "downloads",
                        "path": self.recordingsFolder
                    ]
                )
            }
            return true
        } catch {
            return false
        }
    }

    /// Last resort fallback to temporary directory
    /// LAST RESORT: Cleared on reboot, not persistent
    ///
    /// - Returns: True if successful, false otherwise
    private func fallbackRecordingsLocationToTemp() -> Bool {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("HyperWhisper/recordings", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
            recordingsFolder = tmp.path
            logger.info("↩️ Using temporary directory for recordings: \(self.recordingsFolder, privacy: .public)")
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Storage fallback selected",
                    category: "settings.storage",
                    data: [
                        "fallback": "temporary",
                        "path": self.recordingsFolder
                    ]
                )
            }
            return true
        } catch {
            return false
        }
    }
}
