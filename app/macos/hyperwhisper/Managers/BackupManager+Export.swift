//
//  BackupManager+Export.swift
//  hyperwhisper
//
//  Export: the save dialog, the v1 payload and the universal v2 payload,
//  plus the top-level platformExtensions foreign-slice passthrough (#288).
//  Split out of BackupManager.swift (#732); same class, same behaviour.
//

import Foundation
import SwiftUI
import AppKit

extension BackupManager {

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

        // The export writes the cached launch-at-login value, so re-read the login
        // item first — off the main thread (#853). The user may have changed it in
        // System Settings since the last read.
        if options.includeSettings {
            await SettingsManager.shared.refreshLaunchAtLogin()
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

    /// The live settings as a backup's settings section: what an export writes,
    /// what `applySettings` writes back, and the snapshot a failed import
    /// restores (#1627). `launchAtLogin` is the cached value; call
    /// `refreshLaunchAtLogin()` first when it must be current.
    static func liveBackupSettings() -> BackupSettings {
        let settingsManager = SettingsManager.shared
        return BackupSettings(
            general: BackupGeneralSettings(
                launchAtLogin: settingsManager.launchAtLogin,
                showInDock: settingsManager.showInDock,
                launchMinimized: settingsManager.launchMinimized,
                showRecordingWindow: settingsManager.showRecordingWindow,
                checkForUpdatesAutomatically: settingsManager.checkForUpdatesAutomatically,
                enableErrorLogging: settingsManager.enableErrorLogging,
                shareAnonymousSpeedData: settingsManager.shareAnonymousSpeedData
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
                quickCaptureModeId: settingsManager.quickCaptureModeId,
                // The key combos themselves (#1481) — without them a restore
                // silently lost every custom shortcut.
                keyboardShortcuts: BackupKeyboardShortcuts.liveSnapshot()
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
        )
    }

    /// Creates the BackupData structure from current app state
    /// - Parameter options: Export options
    /// - Returns: BackupData or nil on failure
    private func createBackupData(options: ExportOptions) -> BackupData? {
        let persistence = PersistenceController.shared
        let keychainManager = KeychainManager.shared

        // Collect settings from all managers (only when the section is selected).
        // Deselected sections are left nil so the JSON key is omitted entirely
        // (key-presence is the source of truth for what a file contains).
        let settings: BackupSettings? = options.includeSettings ? Self.liveBackupSettings() : nil

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
                grok: emptyToNil(keychainManager.getAPIKey(for: .grok)),
                geminitranscribe: emptyToNil(keychainManager.getAPIKey(for: .geminiTranscribe)),
                meta: emptyToNil(keychainManager.getAPIKey(for: .meta))
            )
        }

        // Collect license key if requested
        var licenseKey: String?
        if options.includeLicenseKey {
            licenseKey = licenseKeyForExport()
            guard lastError == nil else { return nil }
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
            if lastError == nil {
                lastError = NSLocalizedString("settings.backup.export.error.create", value: "Failed to create backup data", comment: "")
            }
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

    // MARK: - Top-level platformExtensions foreign-slice passthrough (#288)

    /// UserDefaults key holding the NON-`macos` TOP-LEVEL `platformExtensions`
    /// slices of the last imported v2 backup, as raw JSON — `{"windows":{…},"linux":{…}}`.
    ///
    /// The per-MODE equivalent has always worked (`Mode.foreignPlatformExtensions`,
    /// Core Data). The TOP-LEVEL map did not: `encodeBackupV2` built it purely
    /// from the core record, which only ever holds `macos`, so a Windows or Linux
    /// top-level slice died on a macOS round-trip (#288).
    ///
    /// This is separate state from the settings blob and must NOT go through
    /// `settingsBaseline(_:)` / `deepMerged(over:)` — that is a whole-blob
    /// apply of the 7 macOS settings categories and knows nothing about this.
    static let foreignTopLevelExtensionsDefaultsKey = "backup.foreignPlatformExtensions"

    /// The non-`macos` slices of an imported backup's TOP-LEVEL
    /// `platformExtensions`, as raw JSON, or `nil` when there are none.
    ///
    /// Pure and `nonisolated` so a test can call it without a live app:
    /// `encodeBackupV2` and the v2 import read Core Data, the Keychain and
    /// UserDefaults. Same seam shape as `mergeDefaultModelByMode` /
    /// `remapDefaultModelByMode`.
    nonisolated static func foreignTopLevelExtensions(from platformExtensions: JSONValue?) -> String? {
        guard let object = platformExtensions?.objectValue else { return nil }
        let foreign = object.filter { $0.key != "macos" }
        guard !foreign.isEmpty else { return nil }
        guard let data = try? JSONEncoder().encode(JSONValue.object(foreign)),
              let json = String(data: data, encoding: .utf8) else { return nil }
        return json
    }

    /// Merges preserved foreign slices under this platform's own `macos` slice.
    ///
    /// OUR OWN SLICE ALWAYS WINS over a stale preserved copy — the same rule the
    /// per-mode passthrough already follows.
    nonisolated static func mergingForeignTopLevelExtensions(
        into own: JSONValue?,
        stored: String?
    ) -> JSONValue? {
        guard let stored,
              let data = stored.data(using: .utf8),
              let decoded = try? JSONDecoder().decode(JSONValue.self, from: data),
              let foreign = decoded.objectValue,
              !foreign.isEmpty
        else { return own }

        var merged = foreign
        merged.removeValue(forKey: "macos")
        for (key, value) in own?.objectValue ?? [:] {
            merged[key] = value
        }
        return merged.isEmpty ? own : .object(merged)
    }

    /// Builds a full universal-v2 `.hwbackup.json` payload for the selected sections, in parallel
    /// to the v1 `encodeBackup`. Settings flow through the Rust core's macOS↔universal mapping;
    /// modes/vocab/keys/license are mapped in Swift. Self-validates with the core before returning;
    /// sets `lastError` and returns nil on any failure (never returns a corrupt payload).
    ///
    /// Reads the live app; the work after the reads is in `universalV2SettingsSections`,
    /// `universalBackupDTO` and `encodedBackupV2`, which a test can call with its own data.
    func encodeBackupV2(options: ExportOptions, dateString: String) -> (data: Data, filename: String)? {
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
                if lastError == nil {
                    lastError = NSLocalizedString("settings.backup.export.error.create", value: "Failed to create backup data", comment: "")
                }
                return nil
            }
            // 2. + 3. Map, split and hoist (see universalV2SettingsSections).
            guard let sections = universalV2SettingsSections(
                from: macSettings,
                storedForeignTopLevelExtensions: UserDefaults.standard.string(forKey: Self.foreignTopLevelExtensionsDefaultsKey)
            ) else {
                return nil
            }
            settingsValue = sections.settings
            topLevelPlatformExtensions = sections.platformExtensions
        }

        // --- modes (only if selected): BackupMode (as v1) -> UniversalModeDTO; NO FFI for modes ---
        var modes: [BackupMode]?
        if options.includeModes {
            modes = persistence.fetchAllModes().map { BackupMode(from: $0) }
        }

        // --- vocabulary (reuse the already-universal-shaped item, projected to opaque JSONValue) ---
        var vocabulary: [UniversalVocabularyItem]?
        if options.includeVocabulary {
            vocabulary = persistence.fetchAllVocabularyItems().map { UniversalVocabularyItem(from: $0) }
        }

        // --- API keys: flat lowercase provider keys (same shape as the universal/Windows path) ---
        var apiKeys: [String: String]?
        if options.includeAPIKeys {
            let map = BackupManager.universalAPIKeyMap { keychainManager.getAPIKey(for: $0) }
            apiKeys = map.isEmpty ? nil : map
        }

        // --- license key (explicit opt-in, read from the shared secure store) ---
        var licenseKey: String?
        if options.includeLicenseKey {
            licenseKey = licenseKeyForExport()
            guard lastError == nil else { return nil }
        }

        let dto = Self.universalBackupDTO(
            settings: settingsValue,
            platformExtensions: topLevelPlatformExtensions,
            modes: modes,
            vocabulary: vocabulary,
            apiKeys: apiKeys,
            licenseKey: licenseKey,
            appVersion: appVersion
        )
        return encodedBackupV2(dto, dateString: dateString)
    }

    /// Steps 2 and 3 of a v2 export's settings section: the macOS 7-category settings, mapped
    /// by the core to the universal `SettingsRecord`, SPLIT into the backup's top-level
    /// `settings` (the 5 universal categories) and its top-level `platformExtensions` (the
    /// record's map, HOISTED), with the preserved foreign top-level slices re-attached (#288).
    /// `storedForeignTopLevelExtensions` is read only after the mapping succeeds.
    /// Sets `lastError` and returns nil on failure.
    func universalV2SettingsSections(
        from macSettings: BackupSettings,
        storedForeignTopLevelExtensions: @autoclosure () -> String?
    ) -> (settings: JSONValue, platformExtensions: JSONValue?)? {
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
        let settingsValue = JSONValue.object(settingsObj)
        var topLevelPlatformExtensions: JSONValue?
        // Hoist the record's `platformExtensions` map (e.g. {"macos":{"settings":{...}}}) to top level.
        if let ext = recordObj["platformExtensions"] {
            topLevelPlatformExtensions = ext
        }
        // Re-attach the windows / linux top-level slices preserved from the last
        // import, so they survive a macOS round-trip the way the per-mode slices
        // already do. Our own `macos` slice wins.
        topLevelPlatformExtensions = Self.mergingForeignTopLevelExtensions(
            into: topLevelPlatformExtensions,
            stored: storedForeignTopLevelExtensions()
        )
        return (settingsValue, topLevelPlatformExtensions)
    }

    /// The universal v2 envelope for the given sections (nil = section not exported):
    /// modes are projected to `UniversalModeDTO`, vocabulary to opaque `JSONValue` items,
    /// and `exportDate` is now as an ISO-8601 STRING, not a Date. Pure apart from the clock.
    nonisolated static func universalBackupDTO(
        settings: JSONValue?,
        platformExtensions: JSONValue?,
        modes: [BackupMode]?,
        vocabulary: [UniversalVocabularyItem]?,
        apiKeys: [String: String]?,
        licenseKey: String?,
        appVersion: String
    ) -> UniversalBackupDTO {
        let modeDTOs: [UniversalModeDTO]? = modes?.map { UniversalModeDTO(from: $0) }

        var vocab: [JSONValue]?
        if let items = vocabulary {
            // Encode each universal item, then re-decode as JSONValue so the DTO field stays opaque.
            let itemEncoder = JSONEncoder()
            vocab = items.compactMap { item in
                guard let data = try? itemEncoder.encode(item),
                      let value = try? JSONDecoder().decode(JSONValue.self, from: data) else { return nil }
                return value
            }
        }

        // --- assemble the envelope (exportDate is an ISO-8601 STRING, not a Date) ---
        return UniversalBackupDTO(
            schemaVersion: UniversalBackupDTO.universalSchemaVersion,
            exportDate: ISO8601DateFormatter().string(from: Date()),
            appVersion: appVersion,
            platform: "macos",
            settings: settings,
            modes: modeDTOs,
            vocabulary: vocab,
            apiKeys: apiKeys,
            licenseKey: licenseKey,
            platformExtensions: platformExtensions
        )
    }

    /// Encodes a v2 envelope and self-checks it with the core: aborts (sets `lastError`,
    /// returns nil, writes nothing) when it is structurally invalid.
    func encodedBackupV2(_ dto: UniversalBackupDTO, dateString: String) -> (data: Data, filename: String)? {
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
}
