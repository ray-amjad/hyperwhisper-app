//
//  BackupManager.swift
//  hyperwhisper
//
//  BACKUP MANAGER
//  Handles export and import of app settings to/from JSON files.
//  Coordinates between SettingsManager, KeychainManager, and PersistenceController.
//
//  EXPORT FLOW:
//  1. Collect settings from SettingsManager and sub-managers
//  2. Fetch modes and vocabulary from Core Data via PersistenceController
//  3. Optionally collect API keys from Keychain (user opt-in)
//  4. Encode to JSON and present NSSavePanel
//
//  IMPORT FLOW:
//  1. Present NSOpenPanel for file selection
//  2. Validate backup file structure
//  3. Show preview dialog with contents summary
//  4. Apply settings with conflict resolution
//  5. Return ImportResult with statistics
//

import Foundation
import SwiftUI
import AppKit

// MARK: - API Key Writer

/// The one secure-storage write a backup restore makes per BYOK key. A
/// protocol so a test can inject a writer that fails; production writes go to
/// `KeychainManager.shared`.
protocol BackupAPIKeyWriting {
    func saveAPIKey(_ key: String, for type: KeychainManager.APIKeyType) throws
}

extension KeychainManager: BackupAPIKeyWriting {}

// MARK: - Import Settings Store

/// The live settings a backup import writes BEFORE its modes/vocabulary store
/// step (#1627). A protocol so a test can watch the writes without touching
/// the running app's settings; production uses `LiveBackupImportSettingsStore`.
///
/// Secure storage (BYOK keys, the licence) is NOT here: those are written
/// after the store step, so a failed store step never leaves them changed.
@MainActor
protocol BackupImportSettingsStore: AnyObject {
    /// The live settings, in the shape `apply` writes (the same values a
    /// settings export carries). Re-reads launch-at-login first.
    func current() async -> BackupSettings
    /// Writes a backup's settings section, as an import does.
    func apply(_ settings: BackupSettings) async
    /// Puts back a `current()` snapshot exactly, including the values `apply`
    /// deliberately leaves alone for an old backup, and the per-mode default
    /// models.
    func restore(_ settings: BackupSettings) async
    /// `SettingsManager.defaultModelByMode`.
    var defaultModelByMode: [String: String] { get set }
    /// The stored foreign top-level `platformExtensions` (#288).
    var foreignTopLevelExtensions: String? { get set }
}

/// The running app's settings: `SettingsManager.shared` and `UserDefaults`.
@MainActor
final class LiveBackupImportSettingsStore: BackupImportSettingsStore {

    /// Holds no state, so it can be built from a property default value.
    nonisolated init() {}

    func current() async -> BackupSettings {
        // Launch-at-login is a cached value, so re-read it (off the main
        // thread, #853) — otherwise a restore could write a stale one.
        await SettingsManager.shared.refreshLaunchAtLogin()
        return BackupManager.liveBackupSettings()
    }

    func apply(_ settings: BackupSettings) async {
        await BackupManager.applySettings(settings)
    }

    func restore(_ settings: BackupSettings) async {
        await BackupManager.applySettings(settings)
        let settingsManager = SettingsManager.shared
        // `applySettings` skips 300 as a legacy placeholder; a snapshot's
        // 300 is the user's real value.
        settingsManager.maxRecordingDurationSeconds = settings.advanced.maxRecordingDuration
        // An import writes these only after a saved modes step, but put them
        // back too so a restore never depends on that ordering.
        settingsManager.defaultModelByMode = settings.aiModel.defaultModelByMode
    }

    var defaultModelByMode: [String: String] {
        get { SettingsManager.shared.defaultModelByMode }
        set { SettingsManager.shared.defaultModelByMode = newValue }
    }

    var foreignTopLevelExtensions: String? {
        get { UserDefaults.standard.string(forKey: BackupManager.foreignTopLevelExtensionsDefaultsKey) }
        set { UserDefaults.standard.set(newValue, forKey: BackupManager.foreignTopLevelExtensionsDefaultsKey) }
    }
}

// MARK: - Backup Manager

/// Manages export and import of app settings
/// Thread-safe, @MainActor for UI operations
@MainActor
class BackupManager: ObservableObject {

    // MARK: - Singleton

    static let shared = BackupManager()

    // MARK: - Published State

    /// Whether an export operation is in progress
    @Published var isExporting = false

    /// Last error message (for display in UI)
    @Published var lastError: String?

    // MARK: - Dependencies

    /// License manager, injected from the app root (it is a `@StateObject`
    /// there, not a singleton). All backup license reads and writes route
    /// through its one shared secure store.
    weak var licenseManager: LicenseManager?

    /// Where a restore writes BYOK API keys. Only a test replaces this.
    var apiKeyWriter: any BackupAPIKeyWriting = KeychainManager.shared

    /// The settings an import writes, snapshots and restores (#1627). Only a
    /// test replaces this.
    var settingsStore: any BackupImportSettingsStore = LiveBackupImportSettingsStore()

    /// The store an import writes its modes and vocabulary to, and repairs
    /// afterwards. Only a test replaces this (with an in-memory controller).
    var importPersistence: PersistenceController = PersistenceController.shared

    /// What a BYOK key restore actually did, read off the Keychain writes
    /// rather than off the backup file.
    struct APIKeyRestoreOutcome {
        /// Whether at least one write succeeded.
        var anySaved = false
        /// Providers whose key is in the file but is NOT in the Keychain,
        /// in restore order, each listed once.
        var failed: [KeychainManager.APIKeyType] = []
    }

    // MARK: - Init

    /// Production code must use `BackupManager.shared`. This initializer is
    /// internal only so a unit test can build its OWN instance.
    ///
    /// Writing `BackupManager.shared` from a test is not safe: the test host IS
    /// the running app, so every `@Published` write here republishes into the
    /// live view tree. That forces an AppKit constraint pass, which re-evaluates
    /// `HomeStatsBar.body`, whose `@FetchRequest` throws "A fetch request must
    /// have an entity" under XCTest bundle injection — the whole test host dies
    /// with SIGABRT and the test that happened to be running is reported as a
    /// bare failure in 0.000 seconds. Take an instance instead.
    init() {}

    // MARK: - License Revalidation

    /// After a backup import writes a license key, force a real validation so
    /// the imported key isn't paired with the PREVIOUS key's cached status and
    /// expiry (the stored cache is keyed to whatever key was active before the
    /// import). Uses the same `validateLicense` call normal Settings activation
    /// goes through; if offline at import time, validation fails and the status
    /// falls back honestly instead of inheriting the old key's Active.
    private func revalidateImportedLicenseKey(_ licenseKey: String) async {
        guard let licenseManager else {
            AppLogger.settings.warning("Imported a license key but no LicenseManager is wired — revalidation deferred to next launch")
            return
        }
        await licenseManager.validateImportedLicenseKey(licenseKey)
    }

    /// Replaces the key and clears its old key-bound verdict in one secure
    /// write. Revalidation starts only after that write succeeds.
    func importLicenseKeySecurely(_ licenseKey: String) async -> Bool {
        guard let licenseManager else {
            AppLogger.settings.error("License backup import failed: LicenseManager is not wired")
            return false
        }
        guard licenseManager.replaceStoredLicenseKeyFromBackup(licenseKey) else {
            return false
        }
        await revalidateImportedLicenseKey(licenseKey)
        return true
    }

    /// Testable shared-store read used by both backup formats.
    func licenseKeyForExport() -> String? {
        guard let licenseManager else {
            lastError = "Failed to securely read the license key"
            return nil
        }
        switch licenseManager.storedLicenseKeyForBackup() {
        case .present(let key):
            return emptyToNil(key)
        case .missing:
            return nil
        case .unavailable:
            lastError = "Failed to securely read the license key"
            return nil
        }
    }
}
