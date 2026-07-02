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

    /// Whether an import operation is in progress
    @Published var isImporting = false

    /// Last error message (for display in UI)
    @Published var lastError: String?

    // MARK: - Dependencies

    /// License manager, injected from the app root (it is a `@StateObject`
    /// there, not a singleton). Used to force a real validation after an
    /// import writes a license key.
    weak var licenseManager: LicenseManager?

    // MARK: - Private Init

    private init() {}

    // MARK: - License Revalidation

    /// After a backup import writes a license key, force a real validation so
    /// the imported key isn't paired with the PREVIOUS key's cached status and
    /// expiry (the stored cache is keyed to whatever key was active before the
    /// import). Uses the same `validateLicense` call normal Settings activation
    /// goes through; if offline at import time, validation fails and the status
    /// falls back honestly instead of inheriting the old key's Active.
    private func revalidateImportedLicenseKey(_ licenseKey: String) {
        guard let licenseManager else {
            AppLogger.settings.warning("Imported a license key but no LicenseManager is wired — revalidation deferred to next launch")
            return
        }
        Task {
            _ = await licenseManager.validateLicense(licenseKey)
        }
    }

    // MARK: - Export Methods

    /// Exports settings to a file, presenting NSSavePanel for file selection
    /// - Parameter options: Export options (API keys, license key inclusion)
    /// - Returns: True if export was successful, false otherwise
    @discardableResult
    func exportWithDialog(options: ExportOptions) async -> Bool {
        isExporting = true
        lastError = nil

        defer { isExporting = false }

        // Nothing selected — caller should prevent this, but guard anyway.
        guard options.hasAnySelection else {
            lastError = NSLocalizedString("settings.backup.export.error.create", value: "Failed to create backup data", comment: "")
            return false
        }

        // Build the JSON payload and the default filename for the chosen format.
        guard let encoded = encodeBackup(options: options) else {
            // lastError already set by encodeBackup
            return false
        }
        let jsonData = encoded.data

        // Present save dialog
        let panel = NSSavePanel()
        panel.title = NSLocalizedString("settings.backup.export.panel.title", value: "Export Settings", comment: "")
        panel.nameFieldLabel = NSLocalizedString("settings.backup.export.panel.label", value: "Backup File:", comment: "")
        panel.allowedContentTypes = [.json]
        panel.canCreateDirectories = true
        panel.nameFieldStringValue = encoded.filename

        let response = await panel.beginSheetModal(for: NSApp.keyWindow ?? NSApp.mainWindow ?? NSWindow())

        guard response == .OK, let url = panel.url else {
            // User cancelled - not an error
            return false
        }

        // Write to file
        do {
            try jsonData.write(to: url, options: .atomic)
            AppLogger.settings.info("Settings exported successfully to: \(url.path, privacy: .public)")
            return true
        } catch {
            lastError = NSLocalizedString("settings.backup.export.error.write", value: "Failed to write backup file", comment: "")
            AppLogger.settings.error("Failed to export settings: \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    /// Creates the BackupData structure from current app state
    /// - Parameter options: Export options
    /// - Returns: BackupData or nil on failure
    private func createBackupData(options: ExportOptions) -> BackupData? {
        let settingsManager = SettingsManager.shared
        let persistence = PersistenceController.shared
        let keychainManager = KeychainManager.shared

        // Collect settings from all managers (only when the section is selected).
        // Deselected sections are left nil so the JSON key is omitted entirely
        // (key-presence is the source of truth for what a file contains).
        let settings: BackupSettings? = options.includeSettings ? BackupSettings(
            general: BackupGeneralSettings(
                launchAtLogin: settingsManager.launchAtLogin,
                showInDock: settingsManager.showInDock,
                launchMinimized: settingsManager.launchMinimized,
                showRecordingWindow: settingsManager.showRecordingWindow,
                checkForUpdatesAutomatically: settingsManager.checkForUpdatesAutomatically,
                enableErrorLogging: settingsManager.enableErrorLogging
            ),
            audio: BackupAudioSettings(
                autoIncreaseMicVolume: settingsManager.autoIncreaseMicVolume,
                mediaControlMode: settingsManager.audio.mediaControlMode.rawValue,
                enableSoundEffects: settingsManager.enableSoundEffects,
                soundTheme: settingsManager.soundTheme.rawValue,
                soundEffectsVolume: settingsManager.soundEffectsVolume
            ),
            storage: BackupStorageSettings(
                filesyncEnabled: settingsManager.filesyncEnabled,
                storeAsM4A: settingsManager.storeAsM4A
            ),
            textOutput: BackupTextOutputSettings(
                pasteResultText: settingsManager.pasteResultText,
                removeFillerWords: settingsManager.removeFillerWords,
                restoreClipboardAfterPaste: settingsManager.restoreClipboardAfterPaste,
                hideFromClipboardHistory: settingsManager.hideFromClipboardHistory,
                clipboardRestoreDelaySeconds: settingsManager.clipboardRestoreDelaySeconds,
                autocapitalizeInsert: settingsManager.autocapitalizeInsert,
                storeWordTimestamps: settingsManager.storeWordTimestamps
            ),
            shortcuts: BackupShortcutSettings(
                pushToTalkMode: settingsManager.pushToTalkMode.rawValue,
                pushToTalkDoublePressEnabled: settingsManager.pushToTalkDoublePressEnabled,
                quickCaptureEnabled: settingsManager.quickCaptureEnabled,
                quickCaptureModeId: settingsManager.quickCaptureModeId
            ),
            aiModel: BackupAIModelSettings(
                showExperimentalModels: settingsManager.showExperimentalModels,
                defaultTranscriptionModel: settingsManager.defaultTranscriptionModel,
                defaultLanguage: settingsManager.defaultLanguage,
                defaultModelByMode: settingsManager.defaultModelByMode
            ),
            advanced: BackupAdvancedSettings(
                // The backup field keeps its cross-platform name; macOS now
                // stores the value under maxRecordingDurationSeconds.
                maxRecordingDuration: settingsManager.maxRecordingDurationSeconds,
                audioSampleRate: settingsManager.audioSampleRate,
                keepAudioFiles: settingsManager.keepAudioFiles,
                historyRetentionDays: settingsManager.historyRetentionDays
            )
        ) : nil

        // Collect API keys if requested
        var apiKeys: BackupAPIKeys?
        if options.includeAPIKeys {
            apiKeys = BackupAPIKeys(
                openai: emptyToNil(keychainManager.getAPIKey(for: .openAI)),
                groq: emptyToNil(keychainManager.getAPIKey(for: .groq)),
                fireworks: nil,  // Fireworks removed — field kept for backward-compatible decoding only
                anthropic: emptyToNil(keychainManager.getAPIKey(for: .anthropic)),
                gemini: emptyToNil(keychainManager.getAPIKey(for: .gemini)),
                deepgram: emptyToNil(keychainManager.getAPIKey(for: .deepgram)),
                assemblyai: emptyToNil(keychainManager.getAPIKey(for: .assemblyAI)),
                elevenlabs: emptyToNil(keychainManager.getAPIKey(for: .elevenLabs)),
                mistral: emptyToNil(keychainManager.getAPIKey(for: .mistral)),
                grok: emptyToNil(keychainManager.getAPIKey(for: .grok))
            )
        }

        // Collect license key if requested
        var licenseKey: String?
        if options.includeLicenseKey {
            licenseKey = emptyToNil(UserDefaults.standard.string(forKey: LicenseNetworkService.DefaultsKey.licenseKey))
        }

        // Fetch modes from Core Data (only when selected)
        let modes: [BackupMode]? = options.includeModes
            ? persistence.fetchAllModes().map { BackupMode(from: $0) }
            : nil

        // Fetch vocabulary from Core Data (only when selected)
        let vocabulary: [BackupVocabularyItem]? = options.includeVocabulary
            ? persistence.fetchAllVocabularyItems().map { BackupVocabularyItem(from: $0) }
            : nil

        // Get app version
        let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Unknown"

        return BackupData(
            version: BackupData.currentVersion,
            exportDate: Date(),
            appVersion: appVersion,
            settings: settings,
            apiKeys: apiKeys,
            licenseKey: licenseKey,
            modes: modes,
            vocabulary: vocabulary
        )
    }

    /// Encodes the backup payload for the chosen sections and returns the JSON bytes plus a
    /// default filename. A *vocabulary-only* selection is written in the universal v2
    /// `.hwbackup.json` format (cross-platform mac↔Windows); any other selection is written in
    /// the legacy v1 `BackupData` format. Sets `lastError` and returns nil on failure.
    private func encodeBackup(options: ExportOptions) -> (data: Data, filename: String)? {
        let dateFormatter = DateFormatter()
        dateFormatter.locale = Locale(identifier: "en_US_POSIX")
        dateFormatter.dateFormat = "yyyy-MM-dd"
        let dateString = dateFormatter.string(from: Date())

        if options.isVocabularyOnly {
            // Universal v2 vocabulary-only file — cross-platform interchange unit.
            let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Unknown"
            let vocabulary = PersistenceController.shared.fetchAllVocabularyItems().map { UniversalVocabularyItem(from: $0) }
            let payload = UniversalVocabBackup(
                schemaVersion: UniversalVocabBackup.universalSchemaVersion,
                exportDate: ISO8601DateFormatter().string(from: Date()),
                appVersion: appVersion,
                platform: "macos",
                vocabulary: vocabulary
            )

            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            guard let data = try? encoder.encode(payload) else {
                lastError = NSLocalizedString("settings.backup.export.error.encode", value: "Failed to encode backup data", comment: "")
                return nil
            }
            return (data, "HyperWhisper Vocabulary \(dateString).hwbackup.json")
        }

        // NEW (M3-D): full universal-v2 export, gated behind a default-OFF flag. When the flag
        // is OFF (the default), macOS keeps writing the legacy v1 format below — default behavior
        // is unchanged and the change is fully reversible. Vocab-only exports are handled above by
        // the existing universal path and never reach here.
        if Self.useUniversalV2Export {
            if let v2 = encodeBackupV2(options: options, dateString: dateString) {
                return v2
            }
            // encodeBackupV2 sets lastError on failure (incl. the validate self-check). Do NOT
            // silently fall back to v1 — surface the failure so a corrupt file is never written.
            return nil
        }

        // Legacy v1 backup with the selected sections.
        guard let backupData = createBackupData(options: options) else {
            lastError = NSLocalizedString("settings.backup.export.error.create", value: "Failed to create backup data", comment: "")
            return nil
        }

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(backupData) else {
            lastError = NSLocalizedString("settings.backup.export.error.encode", value: "Failed to encode backup data", comment: "")
            return nil
        }
        return (data, "HyperWhisper-Backup-\(dateString).json")
    }

    // MARK: - Universal v2 export (M3-D, additive — gated behind a default-OFF flag)

    /// UserDefaults key gating the NEW full universal-v2 export. Default OFF: when absent or false,
    /// macOS writes the legacy v1 format for any non-vocab-only export (unchanged behavior).
    static let useUniversalV2ExportDefaultsKey = "backup.useUniversalV2Export"

    /// Whether full universal-v2 export is enabled. Reads the default-OFF flag.
    static var useUniversalV2Export: Bool {
        UserDefaults.standard.bool(forKey: useUniversalV2ExportDefaultsKey)
    }

    /// Builds a full universal-v2 `.hwbackup.json` payload for the selected sections, in parallel
    /// to the v1 `encodeBackup`. Settings flow through the Rust core's macOS↔universal mapping;
    /// modes/vocab/keys/license are mapped in Swift. Self-validates with the core before returning;
    /// sets `lastError` and returns nil on any failure (never returns a corrupt payload).
    private func encodeBackupV2(options: ExportOptions, dateString: String) -> (data: Data, filename: String)? {
        let settingsManager = SettingsManager.shared
        let persistence = PersistenceController.shared
        let keychainManager = KeychainManager.shared
        let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Unknown"

        // --- settings (only if selected) -> core mapping -> SPLIT into top-level settings +
        //     hoisted platformExtensions ---
        var settingsValue: JSONValue?
        var topLevelPlatformExtensions: JSONValue?
        if options.includeSettings {
            // 1. Build BackupSettings EXACTLY as the v1 settings branch does (reuse createBackupData
            //    so the field set can never drift), then JSON-encode the 7-category macOS settings.
            guard let backupData = createBackupData(options: options), let macSettings = backupData.settings else {
                lastError = NSLocalizedString("settings.backup.export.error.create", value: "Failed to create backup data", comment: "")
                return nil
            }
            let macEncoder = JSONEncoder()
            guard let macSettingsData = try? macEncoder.encode(macSettings),
                  let macosJson = String(data: macSettingsData, encoding: .utf8) else {
                lastError = NSLocalizedString("settings.backup.export.error.encode", value: "Failed to encode backup data", comment: "")
                return nil
            }

            // 2. macOS 7-category -> universal SettingsRecord (5 universal categories + record-level
            //    platformExtensions:{macos:{settings:{...}}}). existingMacosExt = nil (fresh export).
            let recordJson: String
            do {
                recordJson = try macosSettingsToUniversalSettingsJson(macosJson: macosJson, existingMacosExtJson: nil)
            } catch {
                lastError = NSLocalizedString("settings.backup.export.error.encode", value: "Failed to encode backup data", comment: "")
                AppLogger.settings.error("v2 export: macos->universal settings mapping failed: \(error.localizedDescription, privacy: .public)")
                return nil
            }

            // 3. CRITICAL SPLIT: decode the record; the 5 universal categories become the backup
            //    top-level `settings`; the record's record-level `platformExtensions` map is HOISTED
            //    to the backup TOP-LEVEL `platformExtensions` (matches macos-export.hwbackup.json).
            guard let recordData = recordJson.data(using: .utf8),
                  let record = try? JSONDecoder().decode(JSONValue.self, from: recordData),
                  let recordObj = record.objectValue else {
                lastError = NSLocalizedString("settings.backup.export.error.encode", value: "Failed to encode backup data", comment: "")
                return nil
            }
            var settingsObj: [String: JSONValue] = [:]
            for key in ["general", "textOutput", "storage", "streaming", "advanced"] {
                if let v = recordObj[key] { settingsObj[key] = v }
            }
            settingsValue = .object(settingsObj)
            // Hoist the record's `platformExtensions` map (e.g. {"macos":{"settings":{...}}}) to top level.
            if let ext = recordObj["platformExtensions"] {
                topLevelPlatformExtensions = ext
            }
        }

        // --- modes (only if selected): BackupMode (as v1) -> UniversalModeDTO; NO FFI for modes ---
        var modeDTOs: [UniversalModeDTO]?
        if options.includeModes {
            modeDTOs = persistence.fetchAllModes().map { UniversalModeDTO(from: BackupMode(from: $0)) }
        }

        // --- vocabulary (reuse the already-universal-shaped item, projected to opaque JSONValue) ---
        var vocab: [JSONValue]?
        if options.includeVocabulary {
            let items = persistence.fetchAllVocabularyItems().map { UniversalVocabularyItem(from: $0) }
            // Encode each universal item, then re-decode as JSONValue so the DTO field stays opaque.
            let itemEncoder = JSONEncoder()
            vocab = items.compactMap { item in
                guard let data = try? itemEncoder.encode(item),
                      let value = try? JSONDecoder().decode(JSONValue.self, from: data) else { return nil }
                return value
            }
        }

        // --- API keys: flat lowercase provider keys (same shape as the universal/Windows path) ---
        var apiKeys: [String: String]?
        if options.includeAPIKeys {
            var map: [String: String] = [:]
            func put(_ name: String, _ provider: KeychainManager.APIKeyType) {
                if let k = emptyToNil(keychainManager.getAPIKey(for: provider)) { map[name] = k }
            }
            put("openai", .openAI)
            put("groq", .groq)
            put("anthropic", .anthropic)
            put("gemini", .gemini)
            put("deepgram", .deepgram)
            put("assemblyai", .assemblyAI)
            put("elevenlabs", .elevenLabs)
            put("mistral", .mistral)
            put("grok", .grok)
            apiKeys = map.isEmpty ? nil : map
        }

        // --- license key (as today) ---
        var licenseKey: String?
        if options.includeLicenseKey {
            licenseKey = emptyToNil(UserDefaults.standard.string(forKey: LicenseNetworkService.DefaultsKey.licenseKey))
        }

        // --- assemble the envelope (exportDate is an ISO-8601 STRING, not a Date) ---
        let dto = UniversalBackupDTO(
            schemaVersion: UniversalBackupDTO.universalSchemaVersion,
            exportDate: ISO8601DateFormatter().string(from: Date()),
            appVersion: appVersion,
            platform: "macos",
            settings: settingsValue,
            modes: modeDTOs,
            vocabulary: vocab,
            apiKeys: apiKeys,
            licenseKey: licenseKey,
            platformExtensions: topLevelPlatformExtensions
        )

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(dto), let jsonString = String(data: data, encoding: .utf8) else {
            lastError = NSLocalizedString("settings.backup.export.error.encode", value: "Failed to encode backup data", comment: "")
            return nil
        }

        // --- self-check with the core: abort (do NOT write) if structurally invalid ---
        let errs = validateBackupJson(json: jsonString)
        if let first = errs.first {
            lastError = "\(first.path): \(first.message)"
            AppLogger.settings.error("v2 export self-validation failed: \(first.path, privacy: .public): \(first.message, privacy: .public)")
            return nil
        }

        return (data, "HyperWhisper-Backup-\(dateString).hwbackup.json")
    }

    // MARK: - Import Methods

    /// Imports settings from a file, presenting NSOpenPanel for file selection
    /// - Parameter options: Import options (conflict resolution)
    /// - Returns: ImportResult with statistics, or nil if user cancelled
    func importWithDialog(options: ImportOptions) async -> ImportResult? {
        isImporting = true
        lastError = nil

        defer { isImporting = false }

        // Present open dialog
        let panel = NSOpenPanel()
        panel.title = NSLocalizedString("settings.backup.import.panel.title", value: "Import Settings", comment: "")
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false

        let response = await panel.beginSheetModal(for: NSApp.keyWindow ?? NSApp.mainWindow ?? NSWindow())

        guard response == .OK, let url = panel.url else {
            // User cancelled
            return nil
        }

        return await importSettings(from: url, options: options)
    }

    /// Imports settings from a specific URL
    /// - Parameters:
    ///   - url: URL of the backup file
    ///   - options: Import options
    /// - Returns: ImportResult with statistics
    func importSettings(from url: URL, options: ImportOptions) async -> ImportResult {
        // Read file
        guard let jsonData = try? Data(contentsOf: url) else {
            let message = NSLocalizedString("settings.backup.import.error.read", value: "Failed to read backup file", comment: "")
            lastError = message
            return .failure(message)
        }

        // Detect format by key-presence: a universal v2 file carries `schemaVersion`,
        // a legacy macOS file carries `version`.
        guard let topLevel = try? JSONSerialization.jsonObject(with: jsonData) as? [String: Any] else {
            let message = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
            lastError = message
            return .failure(message)
        }

        if topLevel["schemaVersion"] is Int {
            // Universal v2 file. If it carries settings/modes, do a FULL v2 import (M3-D);
            // otherwise (vocab-only or empty) fall back to the unchanged vocab-only path.
            let hasSettings = topLevel["settings"] is [String: Any]
            let hasModes = topLevel["modes"] is [Any]
            if hasSettings || hasModes {
                return importUniversalV2(jsonData: jsonData, rawString: String(data: jsonData, encoding: .utf8), topLevel: topLevel, options: options)
            }
            return importUniversalVocab(jsonData: jsonData, options: options)
        }

        // Legacy v1 backup
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        let backupData: BackupData
        do {
            backupData = try decoder.decode(BackupData.self, from: jsonData)
        } catch {
            let message = Self.decodeErrorMessage(for: error)
            lastError = message
            AppLogger.settings.error("Failed to decode backup file: \(error.localizedDescription, privacy: .public)")
            return .failure(message)
        }

        // Validate version
        if backupData.version > BackupData.currentVersion {
            let message = NSLocalizedString("settings.backup.import.error.version", value: "Backup file is from a newer version", comment: "")
            lastError = message
            return .failure(message)
        }

        var modesImported = 0
        var modesSkipped = 0
        var vocabImported = 0
        var vocabSkipped = 0

        // Apply settings (only when selected AND present in the file)
        if options.importSettings, let settings = backupData.settings {
            applySettings(settings)
        }

        // Import modes (only when selected AND present)
        if options.importModes, let modes = backupData.modes {
            let modeResult = PersistenceController.shared.importModes(modes, resolution: options.modeConflict)
            modesImported = modeResult.imported
            modesSkipped = modeResult.skipped
            // Per-mode default model selections live in settings.aiModel — only apply them when
            // the user actually chose to import Settings (and the section is present). Otherwise a
            // modes-only import would silently mutate a settings value the user deselected.
            if options.importSettings, let aiMap = backupData.settings?.aiModel.defaultModelByMode {
                applyDefaultModelByMode(aiMap, idRemap: modeResult.idRemap)
            }
        }

        // Import vocabulary (only when selected AND present) — merge only, never a wipe
        if options.importVocabulary, let vocab = backupData.vocabulary {
            let vocabResult = PersistenceController.shared.importVocabulary(vocab, resolution: options.vocabularyConflict)
            vocabImported = vocabResult.imported
            vocabSkipped = vocabResult.skipped
        }

        // Import API keys if present and requested
        var apiKeysImported = false
        if options.importAPIKeys, let apiKeys = backupData.apiKeys {
            importAPIKeys(apiKeys)
            apiKeysImported = apiKeys.hasAnyKey
        }

        // Import license key if present and requested. Trim first (an untrimmed
        // key would break the offline-guard key comparison), then revalidate so
        // the new key doesn't inherit the previous key's cached status.
        var licenseKeyImported = false
        if options.importLicenseKey,
           let licenseKey = backupData.licenseKey?.trimmingCharacters(in: .whitespacesAndNewlines),
           !licenseKey.isEmpty {
            UserDefaults.standard.set(licenseKey, forKey: LicenseNetworkService.DefaultsKey.licenseKey)
            revalidateImportedLicenseKey(licenseKey)
            licenseKeyImported = true
        }

        AppLogger.settings.info("Settings imported: \(modesImported) modes, \(vocabImported) vocabulary items")

        // Post-restore: silently turn off local modes that can't run here, and (on
        // capable hardware) collect cataloged-but-undownloaded models to offer a
        // batched re-download. Only when modes were actually imported.
        let pendingLocalDownloads = options.importModes ? repairRestoredLocalModes() : []

        var result = ImportResult.success(
            modesImported: modesImported,
            modesSkipped: modesSkipped,
            vocabularyImported: vocabImported,
            vocabularySkipped: vocabSkipped,
            apiKeysImported: apiKeysImported,
            licenseKeyImported: licenseKeyImported
        )
        result.pendingLocalDownloadModelIds = pendingLocalDownloads
        return result
    }

    /// Post-restore repair: turn off `.local` post-processing modes that can't run on
    /// this machine (never substituting a cloud LLM), and — on capable Apple-Silicon
    /// hardware — return the ids of cataloged-but-undownloaded local models so the
    /// restore flow can offer a batched re-download. Returns empty on Intel (modes are
    /// turned off) and Rosetta (the relaunch nudge is the relevant guidance, not a
    /// download), so the banner is never shown there.
    private func repairRestoredLocalModes() -> Set<String> {
        let capability = SystemCapability.current
        let repair = PersistenceController.shared.repairBrokenLocalModes(
            capability: capability,
            isCataloged: { LocalModelManager.catalogModelIds.contains($0) },
            isDownloaded: { PersistenceController.localModelFileExists($0) },
            keepPendingDownloads: capability.isAppleSiliconHardware
        )
        if !repair.disabledModeNames.isEmpty {
            AppLogger.settings.info("Restore repair turned off local post-processing for \(repair.disabledModeNames.count, privacy: .public) mode(s)")
        }
        return capability == .supported ? repair.pendingDownloadModelIds : []
    }

    /// Imports a universal v2 `.hwbackup.json` file. macOS understands only the vocabulary
    /// section of this format; settings/modes (if any) are ignored. Vocabulary is merged using
    /// the existing word-matched skip/replace path — never a wipe.
    private func importUniversalVocab(jsonData: Data, options: ImportOptions) -> ImportResult {
        // Parse leniently (same as inspect/preview) so a single malformed item doesn't fail the
        // whole import after the UI already reported "Vocabulary (N words)".
        guard let obj = try? JSONSerialization.jsonObject(with: jsonData) as? [String: Any] else {
            let message = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
            lastError = message
            return .failure(message)
        }

        if let schemaVersion = obj["schemaVersion"] as? Int, schemaVersion > UniversalVocabBackup.universalSchemaVersion {
            let message = NSLocalizedString("settings.backup.import.error.version", value: "Backup file is from a newer version", comment: "")
            lastError = message
            return .failure(message)
        }

        guard options.importVocabulary, let vocabArray = obj["vocabulary"] as? [[String: Any]] else {
            // Nothing selected/present to import — a successful no-op.
            return .success(modesImported: 0, modesSkipped: 0, vocabularyImported: 0, vocabularySkipped: 0)
        }

        // Build import items from the raw entries, skipping any without a usable word.
        let items: [BackupVocabularyItem] = vocabArray.compactMap { entry in
            guard let word = entry["word"] as? String,
                  !word.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            let id = (entry["id"] as? String).flatMap(UUID.init) ?? UUID()
            let sortOrder = entry["sortOrder"] as? Int ?? 0
            return BackupVocabularyItem(
                id: id,
                word: word,
                replacement: entry["replacement"] as? String,
                sortOrder: Int16(clamping: sortOrder),
                source: entry["source"] as? String
            )
        }
        let vocabResult = PersistenceController.shared.importVocabulary(items, resolution: options.vocabularyConflict)

        AppLogger.settings.info("Vocabulary imported from universal file: \(vocabResult.imported) items, \(vocabResult.skipped) skipped")

        return .success(
            modesImported: 0,
            modesSkipped: 0,
            vocabularyImported: vocabResult.imported,
            vocabularySkipped: vocabResult.skipped
        )
    }

    /// Imports a FULL universal-v2 `.hwbackup.json` (M3-D) — settings AND/OR modes present
    /// (Windows backups, or a macOS v2 export). Additive: this branch is only reached when the
    /// file carries settings/modes; vocab-only v2 files still go through `importUniversalVocab`.
    ///
    /// Order: validate-first (reject before mutating) -> settings (REJOIN platformExtensions ->
    /// core inverse mapping -> applySettings UNCHANGED) -> modes (DTO -> present-only migrations ->
    /// existing importModes -> defaultModelByMode from parked extensions) -> vocab/keys/license.
    private func importUniversalV2(jsonData: Data, rawString: String?, topLevel: [String: Any], options: ImportOptions) -> ImportResult {
        // 1. VALIDATE FIRST with the core — reject before mutating any state.
        guard let raw = rawString else {
            let message = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
            lastError = message
            return .failure(message)
        }
        let errs = validateBackupJson(json: raw)
        if let first = errs.first {
            let message = "\(first.path): \(first.message)"
            lastError = message
            AppLogger.settings.error("v2 import validation failed: \(first.path, privacy: .public): \(first.message, privacy: .public)")
            return .failure(message)
        }

        // Decode the envelope leniently (unknown universal keys ignored by the DTOs).
        let dto: UniversalBackupDTO
        do {
            dto = try JSONDecoder().decode(UniversalBackupDTO.self, from: jsonData)
        } catch {
            let message = Self.decodeErrorMessage(for: error)
            lastError = message
            AppLogger.settings.error("Failed to decode v2 backup: \(error.localizedDescription, privacy: .public)")
            return .failure(message)
        }

        var modesImported = 0
        var modesSkipped = 0
        var vocabImported = 0
        var vocabSkipped = 0
        var settingsApplied = false

        // 2. SETTINGS (only when selected AND present): REJOIN -> core inverse map -> MERGE over the
        //    current live settings -> applySettings.
        //
        // DEFENSE IN DEPTH: the entire settings step is wrapped in do/catch. A Windows backup (or any
        // file) that can't produce a complete macOS-settings object must NOT abort the import — we log
        // and continue to modes/vocab/keys. `settingsApplied` records whether settings actually applied.
        if options.importSettings, let settingsValue = dto.settings {
            do {
                // Reconstruct the SettingsRecord JSON: the 5 universal categories from the top-level
                // `settings`, plus the backup TOP-LEVEL `platformExtensions` RE-INJECTED as the
                // record-level `platformExtensions` (exact inverse of the export hoist). Without this,
                // `universal_to_macos_settings` finds no `platformExtensions.macos.settings` and returns
                // ALL macOS-only fields blank.
                var recordObj: [String: JSONValue] = settingsValue.objectValue ?? [:]
                if let ext = dto.platformExtensions {
                    recordObj["platformExtensions"] = ext
                }
                let recordValue = JSONValue.object(recordObj)
                let encoder = JSONEncoder()
                guard let recordData = try? encoder.encode(recordValue),
                      let recordJson = String(data: recordData, encoding: .utf8) else {
                    throw BackupV2Error.settingsEncodeFailed
                }

                // Core inverse mapping: universal SettingsRecord -> macOS 7-category settings JSON.
                // For a Windows backup this is missing macOS-only required fields (shortcuts/aiModel/…).
                let importedMacosJson = try universalSettingsToMacosSettingsJson(recordJson: recordJson)
                guard let importedData = importedMacosJson.data(using: .utf8),
                      let importedValue = try? JSONDecoder().decode(JSONValue.self, from: importedData) else {
                    throw BackupV2Error.settingsDecodeFailed
                }

                // Build the CURRENT macOS settings JSON as a baseline (the same BackupSettings the v1
                // path would produce from the live settings), so any macOS-only field the backup
                // lacks decodes successfully with the user's current value.
                let baselineValue = try currentSettingsBaseline()

                // DEEP-MERGE imported OVER baseline: imported values win where present; baseline fills
                // every field the backup didn't carry.
                let mergedValue = importedValue.deepMerged(over: baselineValue)

                guard let mergedData = try? encoder.encode(mergedValue) else {
                    throw BackupV2Error.settingsEncodeFailed
                }
                // Decode the MERGED 7-category macOS settings into BackupSettings and apply UNCHANGED.
                let backupSettings = try JSONDecoder().decode(BackupSettings.self, from: mergedData)
                applySettings(backupSettings)
                settingsApplied = true
            } catch {
                // Never abort the import for a settings problem — log and continue.
                AppLogger.settings.warning("v2 import: settings step failed, continuing with modes/vocab/keys: \(error.localizedDescription, privacy: .public)")
            }
        }

        // 3. MODES (only when selected AND present): DTO -> present-only migrations -> BackupMode.
        if options.importModes, let modeDTOs = dto.modes {
            let backupModes: [BackupMode] = modeDTOs.compactMap { modeDTO in
                guard let mode = Self.backupMode(fromV2: modeDTO) else {
                    // Malformed/unparseable id — skip but make it VISIBLE (real producers emit
                    // valid UUIDs/GUIDs; we do not invent a replacement id).
                    AppLogger.settings.warning("v2 import: skipping mode with invalid id \(modeDTO.id, privacy: .public) (name: \(modeDTO.name, privacy: .public))")
                    return nil
                }
                return mode
            }
            let modeResult = PersistenceController.shared.importModes(backupModes, resolution: options.modeConflict)
            modesImported = modeResult.imported
            modesSkipped = modeResult.skipped

            // Per-mode default model selections are parked under
            // platformExtensions.macos.settings.defaultModelByMode in v2 (NOT top-level settings).
            // Only apply when Settings import was chosen (mirrors the v1 guard).
            if options.importSettings, let map = Self.defaultModelByModeFromExtensions(dto.platformExtensions) {
                applyDefaultModelByMode(map, idRemap: modeResult.idRemap)
            }
        }

        // 4. VOCABULARY (merge only — never a wipe), reusing the existing universal-vocab logic.
        if options.importVocabulary, let vocabArray = topLevel["vocabulary"] as? [[String: Any]] {
            let items: [BackupVocabularyItem] = vocabArray.compactMap { entry in
                guard let word = entry["word"] as? String,
                      !word.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
                let id = (entry["id"] as? String).flatMap(UUID.init) ?? UUID()
                let sortOrder = entry["sortOrder"] as? Int ?? 0
                return BackupVocabularyItem(
                    id: id,
                    word: word,
                    replacement: entry["replacement"] as? String,
                    sortOrder: Int16(clamping: sortOrder),
                    source: entry["source"] as? String
                )
            }
            let vocabResult = PersistenceController.shared.importVocabulary(items, resolution: options.vocabularyConflict)
            vocabImported = vocabResult.imported
            vocabSkipped = vocabResult.skipped
        }

        // 5. API keys + license (reuse the existing flat-lowercase logic + license write).
        var apiKeysImported = false
        if options.importAPIKeys, let apiKeys = dto.apiKeys, !apiKeys.isEmpty {
            importUniversalAPIKeys(apiKeys)
            apiKeysImported = true
        }

        // Trim + revalidate — see the v1 license-key import above.
        var licenseKeyImported = false
        if options.importLicenseKey,
           let licenseKey = dto.licenseKey?.trimmingCharacters(in: .whitespacesAndNewlines),
           !licenseKey.isEmpty {
            UserDefaults.standard.set(licenseKey, forKey: LicenseNetworkService.DefaultsKey.licenseKey)
            revalidateImportedLicenseKey(licenseKey)
            licenseKeyImported = true
        }

        AppLogger.settings.info("Universal v2 backup imported: settingsApplied=\(settingsApplied, privacy: .public), \(modesImported) modes, \(vocabImported) vocabulary items")

        // Post-restore local-mode repair + re-download collection (see v1 path).
        let pendingLocalDownloads = options.importModes ? repairRestoredLocalModes() : []

        var result = ImportResult.success(
            modesImported: modesImported,
            modesSkipped: modesSkipped,
            vocabularyImported: vocabImported,
            vocabularySkipped: vocabSkipped,
            apiKeysImported: apiKeysImported,
            licenseKeyImported: licenseKeyImported
        )
        result.pendingLocalDownloadModelIds = pendingLocalDownloads
        return result
    }

    /// Internal errors thrown by the v2 import settings step (caught locally so the step can fail
    /// soft without aborting the whole import).
    private enum BackupV2Error: Error {
        case settingsEncodeFailed
        case settingsDecodeFailed
    }

    /// Builds the CURRENT live macOS settings as a 7-category `JSONValue`, to serve as the baseline
    /// the v2 settings import merges OVER. Produced from the SAME `BackupSettings` the v1 export path
    /// builds (via `createBackupData`), so the baseline is always a complete, decodable object —
    /// guaranteeing a Windows backup (missing macOS-only fields) still decodes after the merge.
    private func currentSettingsBaseline() throws -> JSONValue {
        var opts = ExportOptions()
        opts.includeSettings = true
        guard let backupData = createBackupData(options: opts), let settings = backupData.settings else {
            throw BackupV2Error.settingsEncodeFailed
        }
        let data = try JSONEncoder().encode(settings)
        return try JSONDecoder().decode(JSONValue.self, from: data)
    }

    /// Projects a `UniversalModeDTO` into the internal `BackupMode`, running the present-only cloud
    /// migrations BEFORE constructing the mode. Migrations run only when the source value is
    /// non-nil/non-empty so we never write a default where the source intended `nil`.
    nonisolated private static func backupMode(fromV2 dto: UniversalModeDTO) -> BackupMode? {
        guard let id = UUID(uuidString: dto.id) else { return nil }

        // Present-only migrations (both idempotent with macOS's own fromStorageValue at read time).
        let migratedTier: String?
        if let t = dto.cloudAccuracyTier, !t.isEmpty {
            migratedTier = migrateCloudAccuracyTier(value: t)
        } else {
            migratedTier = dto.cloudAccuracyTier
        }
        let migratedPP: String?
        if let p = dto.cloudPostProcessingModel, !p.isEmpty {
            migratedPP = migrateCloudPpModel(value: p)
        } else {
            migratedPP = dto.cloudPostProcessingModel
        }

        // Preserve every NON-macOS per-mode platformExtensions slice (e.g. the
        // `windows` blob) verbatim so it survives a macOS round-trip (H4). macOS
        // owns the "macos" slice; foreign slices are stored as raw JSON on the
        // Mode entity and re-emitted on the next v2 export.
        var foreignExt: String?
        if case .object(let obj)? = dto.platformExtensions {
            let foreign = obj.filter { $0.key != "macos" }
            if !foreign.isEmpty,
               let data = try? JSONEncoder().encode(JSONValue.object(foreign)) {
                foreignExt = String(data: data, encoding: .utf8)
            }
        }

        return BackupMode(
            id: id,
            name: dto.name,
            preset: dto.preset ?? "custom",
            language: dto.language ?? "en",
            model: dto.model ?? "base",
            punctuation: dto.punctuation ?? true,
            capitalization: dto.capitalization ?? true,
            profanityFilter: dto.profanityFilter ?? false,
            customInstructions: dto.customInstructions,
            languageModel: dto.languageModel,
            cloudProvider: dto.cloudProvider,
            cloudTranscriptionModel: dto.cloudTranscriptionModel,
            postProcessingMode: Int16(clamping: dto.postProcessingMode ?? 0),
            postProcessingProvider: dto.postProcessingProvider,
            englishSpelling: dto.englishSpelling,
            userSystemPrompt: dto.userSystemPrompt,
            isDefault: dto.isDefault ?? false,
            sortOrder: Int16(clamping: dto.sortOrder ?? 0),
            cloudAccuracyTier: migratedTier,
            removeTrailingPeriod: dto.removeTrailingPeriod,
            geminiCustomPrompt: dto.geminiCustomPrompt,
            cloudPostProcessingModel: migratedPP,
            cloudTranscriptionDomain: dto.cloudTranscriptionDomain,
            foreignPlatformExtensions: foreignExt
        )
    }

    /// Extracts `defaultModelByMode` from the parked `platformExtensions.macos.settings` blob
    /// (where v2 stores it). Returns nil when absent/empty.
    nonisolated private static func defaultModelByModeFromExtensions(_ ext: JSONValue?) -> [String: String]? {
        guard let macos = ext?.objectValue?["macos"]?.objectValue,
              let settings = macos["settings"]?.objectValue,
              case .object(let map)? = settings["defaultModelByMode"] else {
            return nil
        }
        var result: [String: String] = [:]
        for (k, v) in map {
            if let s = v.stringValue { result[k] = s }
        }
        return result.isEmpty ? nil : result
    }

    /// Imports flat lowercase-provider API keys (the universal/Windows shape) into the Keychain.
    /// Reuses the same per-provider save calls as the v1 `importAPIKeys`; unknown keys are ignored.
    private func importUniversalAPIKeys(_ keys: [String: String]) {
        let keychainManager = KeychainManager.shared
        func save(_ name: String, _ provider: KeychainManager.APIKeyType) {
            if let k = keys[name], !k.isEmpty { try? keychainManager.saveAPIKey(k, for: provider) }
        }
        save("openai", .openAI)
        save("groq", .groq)
        // "fireworks" intentionally ignored (provider removed).
        save("anthropic", .anthropic)
        save("gemini", .gemini)
        save("deepgram", .deepgram)
        save("assemblyai", .assemblyAI)
        save("elevenlabs", .elevenLabs)
        save("mistral", .mistral)
        save("grok", .grok)
    }

    /// Pre-parses a chosen backup file to report which sections it contains, so the import UI can
    /// offer granular, auto-detected options (absent sections are disabled). Sets `lastError` and
    /// returns nil if the file can't be read or isn't a recognized backup.
    func inspectBackupFile(at url: URL) -> BackupContents? {
        guard let data = try? Data(contentsOf: url) else {
            lastError = NSLocalizedString("settings.backup.import.error.read", value: "Failed to read backup file", comment: "")
            return nil
        }
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            lastError = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
            return nil
        }

        // Universal v2 file (cross-platform) — macOS supports only its vocabulary section.
        if let schemaVersion = obj["schemaVersion"] as? Int {
            if schemaVersion > UniversalVocabBackup.universalSchemaVersion {
                lastError = NSLocalizedString("settings.backup.import.error.version", value: "Backup file is from a newer version", comment: "")
                return nil
            }
            let vocabArray = obj["vocabulary"] as? [[String: Any]]
            return BackupContents(
                format: .universalVocab,
                hasSettings: false,
                hasModes: false,
                hasVocabulary: vocabArray != nil,
                vocabularyCount: vocabArray?.count ?? 0,
                hasAPIKeys: false,
                hasLicense: false,
                appVersion: obj["appVersion"] as? String
            )
        }

        // Legacy macOS v1 file.
        if let version = obj["version"] as? Int {
            if version > BackupData.currentVersion {
                lastError = NSLocalizedString("settings.backup.import.error.version", value: "Backup file is from a newer version", comment: "")
                return nil
            }
            let vocabArray = obj["vocabulary"] as? [[String: Any]]
            let apiKeys = obj["apiKeys"] as? [String: Any]
            let license = obj["licenseKey"] as? String
            return BackupContents(
                format: .legacyV1,
                hasSettings: obj["settings"] is [String: Any],
                hasModes: obj["modes"] is [Any],
                hasVocabulary: vocabArray != nil,
                vocabularyCount: vocabArray?.count ?? 0,
                hasAPIKeys: !(apiKeys?.isEmpty ?? true),
                hasLicense: !(license?.isEmpty ?? true),
                appVersion: obj["appVersion"] as? String
            )
        }

        lastError = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
        return nil
    }

    /// Computes how many vocabulary words in a backup file are new vs. already present (matched by
    /// word, case-insensitive), to show a pre-merge summary before applying a vocabulary import.
    /// Returns nil if the file can't be parsed.
    func vocabularyMergePreview(at url: URL) -> (newCount: Int, conflictCount: Int)? {
        guard let data = try? Data(contentsOf: url),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let vocabArray = obj["vocabulary"] as? [[String: Any]] else {
            return nil
        }

        // Snapshot existing words once (lowercased, trimmed) for an O(n) comparison.
        let existing = Set(
            PersistenceController.shared.fetchAllVocabularyItems().compactMap {
                $0.word?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            }
        )

        var newCount = 0
        var conflictCount = 0
        var seenInFile = Set<String>()
        for entry in vocabArray {
            guard let word = entry["word"] as? String else { continue }
            let key = word.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            guard !key.isEmpty, seenInFile.insert(key).inserted else { continue }
            if existing.contains(key) {
                conflictCount += 1
            } else {
                newCount += 1
            }
        }
        return (newCount, conflictCount)
    }

    /// Validates a backup file without importing
    /// - Parameter url: URL of the backup file
    /// - Returns: BackupValidationResult with preview information
    func validateBackupFile(at url: URL) -> BackupValidationResult {
        guard let jsonData = try? Data(contentsOf: url) else {
            return .failure(NSLocalizedString("settings.backup.import.error.read", value: "Failed to read backup file", comment: ""))
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        let backupData: BackupData
        do {
            backupData = try decoder.decode(BackupData.self, from: jsonData)
        } catch {
            AppLogger.settings.error("Failed to decode backup file: \(error.localizedDescription, privacy: .public)")
            return .failure(Self.decodeErrorMessage(for: error))
        }

        if backupData.version > BackupData.currentVersion {
            return .failure(NSLocalizedString("settings.backup.import.error.version", value: "Backup file is from a newer version", comment: ""))
        }

        return .success(
            version: backupData.version,
            exportDate: backupData.exportDate,
            appVersion: backupData.appVersion,
            modeCount: backupData.modes?.count ?? 0,
            vocabularyCount: backupData.vocabulary?.count ?? 0,
            hasAPIKeys: backupData.apiKeys?.hasAnyKey ?? false,
            hasLicenseKey: backupData.licenseKey != nil && !backupData.licenseKey!.isEmpty
        )
    }

    // MARK: - Private Helpers

    /// Applies settings from backup to the app
    /// - Parameter settings: BackupSettings to apply
    private func applySettings(_ settings: BackupSettings) {
        let settingsManager = SettingsManager.shared

        // General settings
        settingsManager.launchAtLogin = settings.general.launchAtLogin
        settingsManager.showInDock = settings.general.showInDock
        settingsManager.launchMinimized = settings.general.launchMinimized
        settingsManager.showRecordingWindow = settings.general.showRecordingWindow
        settingsManager.checkForUpdatesAutomatically = settings.general.checkForUpdatesAutomatically
        settingsManager.enableErrorLogging = settings.general.enableErrorLogging

        // Audio settings
        settingsManager.autoIncreaseMicVolume = settings.audio.autoIncreaseMicVolume
        if settings.audio.mediaControlMode == "pauseMedia" {
            // Removed mode — fall back to .off (matches startup migration)
            settingsManager.audio.mediaControlMode = .off
        } else if let mode = MediaControlMode(rawValue: settings.audio.mediaControlMode) {
            settingsManager.audio.mediaControlMode = mode
        }
        settingsManager.enableSoundEffects = settings.audio.enableSoundEffects
        if let theme = SoundTheme(rawValue: settings.audio.soundTheme) {
            settingsManager.soundTheme = theme
        }
        settingsManager.soundEffectsVolume = settings.audio.soundEffectsVolume

        // Storage settings
        settingsManager.filesyncEnabled = settings.storage.filesyncEnabled
        settingsManager.storeAsM4A = settings.storage.storeAsM4A

        // Text output settings
        settingsManager.pasteResultText = settings.textOutput.pasteResultText
        settingsManager.removeFillerWords = settings.textOutput.removeFillerWords
        settingsManager.restoreClipboardAfterPaste = settings.textOutput.restoreClipboardAfterPaste
        settingsManager.hideFromClipboardHistory = settings.textOutput.hideFromClipboardHistory
        settingsManager.clipboardRestoreDelaySeconds = settings.textOutput.clipboardRestoreDelaySeconds
        // Optional in backup payload — legacy (pre-#643) backups omit this field.
        // Only apply when present so restoring an old backup doesn't silently flip
        // a user's disabled Autocapitalize Insert back on.
        if let autocapitalizeInsert = settings.textOutput.autocapitalizeInsert {
            settingsManager.autocapitalizeInsert = autocapitalizeInsert
        }
        settingsManager.storeWordTimestamps = settings.textOutput.storeWordTimestamps

        // Shortcut settings
        if let mode = PushToTalkMode(rawValue: settings.shortcuts.pushToTalkMode) {
            settingsManager.pushToTalkMode = mode
        }
        settingsManager.pushToTalkDoublePressEnabled = settings.shortcuts.pushToTalkDoublePressEnabled
        // Quick Capture (optional in backup payload — pre-feature backups omit these).
        if let qcEnabled = settings.shortcuts.quickCaptureEnabled {
            settingsManager.quickCaptureEnabled = qcEnabled
        }
        if let qcModeId = settings.shortcuts.quickCaptureModeId {
            settingsManager.quickCaptureModeId = qcModeId
        }
        // Programmatic writes above bypass the settings UI's .onChange posters,
        // so re-sync shortcut consumers (PTT observer, feature-gated hotkey
        // registration) explicitly.
        NotificationCenter.default.post(name: .shortcutDidChange, object: nil)

        // AI model settings
        settingsManager.showExperimentalModels = settings.aiModel.showExperimentalModels
        settingsManager.defaultTranscriptionModel = settings.aiModel.defaultTranscriptionModel
        settingsManager.defaultLanguage = settings.aiModel.defaultLanguage

        // Advanced settings
        // Legacy backups carry 300 in advanced.maxRecordingDuration — the old
        // never-exposed default, not a user choice. Treat it as unset so the
        // import doesn't silently cap recordings at 5 minutes.
        if settings.advanced.maxRecordingDuration != 300 {
            settingsManager.maxRecordingDurationSeconds = settings.advanced.maxRecordingDuration
        }
        settingsManager.audioSampleRate = settings.advanced.audioSampleRate
        settingsManager.keepAudioFiles = settings.advanced.keepAudioFiles
        settingsManager.historyRetentionDays = settings.advanced.historyRetentionDays

    }

    private func applyDefaultModelByMode(_ importedMap: [String: String], idRemap: [UUID: UUID]) {
        let settingsManager = SettingsManager.shared
        let resolvedImportedMap = Self.remapDefaultModelByMode(
            importedMap.mapValues { CloudTranscriptionModels.resolveDeepgramModelAlias($0) ?? $0 },
            using: idRemap
        )

        settingsManager.defaultModelByMode = Self.mergeDefaultModelByMode(
            current: settingsManager.defaultModelByMode,
            imported: resolvedImportedMap
        )
    }

    nonisolated static func mergeDefaultModelByMode(current: [String: String], imported: [String: String]) -> [String: String] {
        current.merging(imported) { _, imported in imported }
    }

    nonisolated static func remapDefaultModelByMode(_ map: [String: String], using idRemap: [UUID: UUID]) -> [String: String] {
        var remapped = map

        for (oldId, newId) in idRemap {
            if let model = remapped.removeValue(forKey: oldId.uuidString) {
                remapped[newId.uuidString] = model
            }
        }

        return remapped
    }

    /// Builds a user-facing decode failure message.
    ///
    /// Keeps the existing localized "Invalid backup file format" prefix (so all
    /// translations still apply) and appends a concise diagnostic — including the
    /// failing field path for `DecodingError`s — so the user has a recovery path
    /// instead of an opaque error.
    nonisolated static func decodeErrorMessage(for error: Error) -> String {
        let prefix = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
        guard let detail = decodeErrorDetail(for: error) else { return prefix }
        return "\(prefix)\n\(detail)"
    }

    /// Renders a `DecodingError` into a short, human-readable detail string with
    /// the failing coding path. Returns nil for non-decoding errors.
    nonisolated static func decodeErrorDetail(for error: Error) -> String? {
        guard let decodingError = error as? DecodingError else { return nil }

        func pathString(_ context: DecodingError.Context) -> String {
            context.codingPath.map(\.stringValue).joined(separator: ".")
        }

        switch decodingError {
        case let .keyNotFound(key, context):
            let path = pathString(context)
            let field = path.isEmpty ? key.stringValue : "\(path).\(key.stringValue)"
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.keyNotFound", value: "Missing required field: %@", comment: "Backup import decode error; %@ is the field path"),
                field
            )
        case let .typeMismatch(_, context):
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.typeMismatch", value: "Wrong type for field: %@", comment: "Backup import decode error; %@ is the field path"),
                pathString(context)
            )
        case let .valueNotFound(_, context):
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.valueNotFound", value: "Missing value for field: %@", comment: "Backup import decode error; %@ is the field path"),
                pathString(context)
            )
        case let .dataCorrupted(context):
            let path = pathString(context)
            if path.isEmpty {
                return String(
                    format: NSLocalizedString("settings.backup.import.error.decode.corrupted", value: "Corrupted data: %@", comment: "Backup import decode error; %@ is the underlying reason"),
                    context.debugDescription
                )
            }
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.corruptedField", value: "Corrupted value for field: %@", comment: "Backup import decode error; %@ is the field path"),
                path
            )
        @unknown default:
            return nil
        }
    }

    /// Imports API keys from backup to Keychain
    /// - Parameter apiKeys: BackupAPIKeys to import
    private func importAPIKeys(_ apiKeys: BackupAPIKeys) {
        let keychainManager = KeychainManager.shared

        if let key = apiKeys.openai, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .openAI)
        }
        if let key = apiKeys.groq, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .groq)
        }
        // Fireworks removed: read-and-ignore apiKeys.fireworks so old backups still decode.
        if let key = apiKeys.anthropic, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .anthropic)
        }
        if let key = apiKeys.gemini, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .gemini)
        }
        if let key = apiKeys.deepgram, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .deepgram)
        }
        if let key = apiKeys.assemblyai, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .assemblyAI)
        }
        if let key = apiKeys.elevenlabs, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .elevenLabs)
        }
        if let key = apiKeys.mistral, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .mistral)
        }
        if let key = apiKeys.grok, !key.isEmpty {
            try? keychainManager.saveAPIKey(key, for: .grok)
        }
    }

    /// Converts empty strings to nil
    private func emptyToNil(_ string: String?) -> String? {
        guard let string = string, !string.isEmpty else { return nil }
        return string
    }
}
