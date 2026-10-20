//
//  BackupManager+Import.swift
//  hyperwhisper
//
//  Import: reading a backup file, the v1 import, the vocabulary-only universal
//  import, and the store-failure (#1613) and settings-restore (#1627) helpers.
//  Split out of BackupManager.swift (#732); same class, same behaviour.
//

import Foundation
import SwiftUI
import AppKit

extension BackupManager {

    // MARK: - Import Methods

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
                return await importUniversalV2(jsonData: jsonData, rawString: String(data: jsonData, encoding: .utf8), topLevel: topLevel, options: options)
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

        // Apply settings (only when selected AND present in the file). Snapshot the
        // live settings first: a failed store step below puts them back (#1627).
        var settingsApplied = false
        var settingsBefore: ImportSettingsSnapshot?
        if options.importSettings, let settings = backupData.settings {
            let liveSettings = await settingsStore.current()
            settingsBefore = ImportSettingsSnapshot(
                settings: liveSettings,
                writesForeignTopLevelExtensions: false,
                foreignTopLevelExtensions: nil
            )
            await settingsStore.apply(settings)
            settingsApplied = true
        }
        // Import modes and vocabulary, each section as ONE store transaction (#1613);
        // see importBackupStoreSections. Per-mode default model selections live in
        // settings.aiModel — only applied when the user actually chose to import
        // Settings (and the section is present). Otherwise a modes-only import would
        // silently mutate a settings value the user deselected.
        let store = importBackupStoreSections(
            modes: options.importModes ? backupData.modes : nil,
            vocabulary: options.importVocabulary ? backupData.vocabulary : nil,
            options: options,
            defaultModelByModeToApply: { backupData.settings?.aiModel.defaultModelByMode }
        )

        // Then: settings put back after a failed store step (#1627), API keys,
        // the licence key and the result — see finishBackupImport.
        return await finishBackupImport(
            .legacyV1,
            store: store,
            settingsBefore: settingsBefore,
            settingsApplied: settingsApplied,
            licenseKey: backupData.licenseKey,
            options: options,
            apiKeysStep: {
                guard options.importAPIKeys, let apiKeys = backupData.apiKeys else { return nil }
                return importAPIKeys(apiKeys)
            }
        )
    }

    /// Post-restore repair: turn off `.local` post-processing modes that can't run on
    /// this machine (never substituting a cloud LLM), and — on capable Apple-Silicon
    /// hardware — return the ids of cataloged-but-undownloaded local models so the
    /// restore flow can offer a batched re-download. Returns empty on Intel (modes are
    /// turned off) and Rosetta (the relaunch nudge is the relevant guidance, not a
    /// download), so the banner is never shown there.
    func repairRestoredLocalModes() -> Set<String> {
        let capability = SystemCapability.current
        let repair = importPersistence.repairBrokenLocalModes(
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
        let items = Self.backupVocabularyItems(fromUniversal: vocabArray)
        // One store transaction (#1613): all of the vocabulary or none of it.
        let vocabResult: PersistenceController.BackupStoreImportResult
        do {
            vocabResult = try importPersistence.importBackupStore(
                modes: nil,
                modeResolution: options.modeConflict,
                vocabulary: items,
                vocabularyResolution: options.vocabularyConflict
            )
        } catch {
            return storeImportFailureResult(
                Self.backupStoreImportFailure(from: error, modesSelected: false),
                modesSelected: false,
                vocabularySelected: true,
                settingsApplied: false,
                apiKeysImported: false,
                licenseKeyImported: false,
                repairImportedModes: false,
                apiKeysFailedProviders: []
            )
        }

        AppLogger.settings.info("Vocabulary imported from universal file: \(vocabResult.vocabularyImported) items, \(vocabResult.vocabularySkipped) skipped")

        return .success(
            modesImported: 0,
            modesSkipped: 0,
            vocabularyImported: vocabResult.vocabularyImported,
            vocabularySkipped: vocabResult.vocabularySkipped
        )
    }

    func licenseImportFailureResult(
        modesImported: Int,
        modesSkipped: Int,
        vocabularyImported: Int,
        vocabularySkipped: Int,
        apiKeysImported: Bool,
        earlierSectionsApplied: Bool,
        repairImportedModes: Bool,
        apiKeysFailedProviders: [KeychainManager.APIKeyType]
    ) -> ImportResult {
        let failureMessage = "Failed to securely import the license key"
        guard earlierSectionsApplied else {
            lastError = failureMessage
            var failure = ImportResult.failure(failureMessage)
            failure.apiKeysFailedProviders = apiKeysFailedProviders
            return failure
        }

        let message = "The other selected backup sections were applied, but the license key could not be securely imported."
        lastError = message
        var result = ImportResult.partialFailure(
            message,
            modesImported: modesImported,
            modesSkipped: modesSkipped,
            vocabularyImported: vocabularyImported,
            vocabularySkipped: vocabularySkipped,
            apiKeysImported: apiKeysImported
        )
        result.pendingLocalDownloadModelIds = repairImportedModes ? repairRestoredLocalModes() : []
        result.apiKeysFailedProviders = apiKeysFailedProviders
        return result
    }

    /// The `BackupStoreImportError` behind a failed `importBackupStore` call.
    /// It throws nothing else; any other error counts as a failure of the
    /// first selected section, with nothing saved.
    nonisolated static func backupStoreImportFailure(
        from error: Error,
        modesSelected: Bool
    ) -> PersistenceController.BackupStoreImportError {
        if let failure = error as? PersistenceController.BackupStoreImportError {
            return failure
        }
        return PersistenceController.BackupStoreImportError(
            failedSection: modesSelected ? .modes : .vocabulary,
            committed: PersistenceController.BackupStoreImportResult(),
            underlying: error
        )
    }

    /// The result when a store section failed and was rolled back (#1613).
    /// A failed modes save leaves the modes AND the vocabulary as they were
    /// (the vocabulary is not attempted). A failed vocabulary save leaves the
    /// vocabulary as it was; the modes, when selected, were saved before it,
    /// so their counts are reported and the post-restore mode repair runs for
    /// them as it does after a full import. Settings applied before the store
    /// step were put back by the caller (#1627): `settingsApplied` is then
    /// false and `settingsRestored` true. API keys and the licence ran after
    /// the store step, as they always did. When anything changed the result
    /// is a partial failure, otherwise a plain failure; the message names
    /// only what happened.
    func storeImportFailureResult(
        _ failure: PersistenceController.BackupStoreImportError,
        modesSelected: Bool,
        vocabularySelected: Bool,
        settingsApplied: Bool,
        settingsRestored: Bool = false,
        apiKeysImported: Bool,
        licenseKeyImported: Bool,
        licenseImportFailed: Bool = false,
        repairImportedModes: Bool,
        apiKeysFailedProviders: [KeychainManager.APIKeyType]
    ) -> ImportResult {
        let modesSaved = failure.failedSection == .vocabulary
        let committed = failure.committed
        let otherSectionsApplied = settingsApplied || apiKeysImported || licenseKeyImported
        let message = Self.storeImportFailureMessage(
            failedSection: failure.failedSection,
            modesSaved: modesSaved && modesSelected,
            vocabularySelected: vocabularySelected,
            settingsRestored: settingsRestored,
            otherSectionsApplied: otherSectionsApplied,
            licenseImportFailed: licenseImportFailed
        )
        lastError = message

        // Only when the modes save committed: imported modes may need it.
        let pendingLocalDownloads = (modesSaved && repairImportedModes) ? repairRestoredLocalModes() : []

        let anythingApplied = Self.earlierBackupSectionsWereApplied(
            settingsApplied: settingsApplied,
            modesImported: committed.modesImported,
            vocabularyImported: 0,
            apiKeysImported: apiKeysImported
        ) || licenseKeyImported
        guard anythingApplied else {
            var result = ImportResult.failure(message)
            result.pendingLocalDownloadModelIds = pendingLocalDownloads
            result.apiKeysFailedProviders = apiKeysFailedProviders
            return result
        }

        var result = ImportResult(
            success: false,
            partialSuccess: true,
            modesImported: committed.modesImported,
            modesSkipped: committed.modesSkipped,
            vocabularyImported: 0,
            vocabularySkipped: 0,
            apiKeysImported: apiKeysImported,
            licenseKeyImported: licenseKeyImported,
            errorMessage: message
        )
        result.pendingLocalDownloadModelIds = pendingLocalDownloads
        result.apiKeysFailedProviders = apiKeysFailedProviders
        result.settingsApplied = settingsApplied
        return result
    }

    /// The message for a failed store section (#1613): which section failed,
    /// what that left unchanged, whether the modes were saved before a
    /// failed vocabulary save, whether the settings were put back (#1627),
    /// then whether the licence import failed too and whether a section
    /// outside the store (settings, API keys, licence) changed something.
    /// Each part is its own sentence and its own key.
    nonisolated static func storeImportFailureMessage(
        failedSection: PersistenceController.BackupStoreImportError.Section,
        modesSaved: Bool,
        vocabularySelected: Bool,
        settingsRestored: Bool = false,
        otherSectionsApplied: Bool,
        licenseImportFailed: Bool
    ) -> String {
        var sentences: [String] = []
        switch failedSection {
        case .modes where vocabularySelected:
            sentences.append(NSLocalizedString(
                "settings.backup.import.error.storeModesAndVocabulary",
                value: "The modes could not be saved, so the modes and vocabulary were left as they were.",
                comment: "Backup import: the modes save failed and was rolled back; the vocabulary was not imported either"
            ))
        case .modes:
            sentences.append(NSLocalizedString(
                "settings.backup.import.error.storeModes",
                value: "The modes could not be saved, so they were left as they were.",
                comment: "Backup import: the modes save failed and was rolled back"
            ))
        case .vocabulary where modesSaved:
            sentences.append(NSLocalizedString(
                "settings.backup.import.error.storeVocabularyAfterModes",
                value: "The modes were imported, but the vocabulary could not be saved, so it was left as it was.",
                comment: "Backup import: the modes were saved, then the vocabulary save failed and was rolled back"
            ))
        case .vocabulary:
            sentences.append(NSLocalizedString(
                "settings.backup.import.error.storeVocabulary",
                value: "The vocabulary could not be saved, so it was left as it was.",
                comment: "Backup import: the vocabulary save failed and was rolled back"
            ))
        }
        if settingsRestored {
            sentences.append(NSLocalizedString(
                "settings.backup.import.error.storeSettingsRestored",
                value: "The settings were left as they were.",
                comment: "Backup import: added after a failed modes/vocabulary save when the settings applied before it were put back as they were"
            ))
        }
        if licenseImportFailed {
            sentences.append(NSLocalizedString(
                "settings.backup.import.error.storeLicense",
                value: "The license key could not be securely imported.",
                comment: "Backup import: added after a failed modes/vocabulary save when the licence key import failed too"
            ))
        }
        if otherSectionsApplied {
            sentences.append(NSLocalizedString(
                "settings.backup.import.error.storeOtherSections",
                value: "The other selected sections were applied.",
                comment: "Backup import: added after a failed modes/vocabulary save when settings, API keys or the licence key were applied"
            ))
        }
        return sentences.joined(separator: " ")
    }

    /// A partial-success message is truthful only when an earlier section made
    /// a durable change. Merely selecting an empty section does not count.
    nonisolated static func earlierBackupSectionsWereApplied(
        settingsApplied: Bool,
        modesImported: Int,
        vocabularyImported: Int,
        apiKeysImported: Bool
    ) -> Bool {
        settingsApplied || modesImported > 0 || vocabularyImported > 0 || apiKeysImported
    }

    /// The settings an import is about to change, taken before it changes them (#1627).
    struct ImportSettingsSnapshot {
        /// The live settings section (`BackupImportSettingsStore.current()`).
        let settings: BackupSettings
        /// Whether this import writes the foreign top-level `platformExtensions`
        /// (a v2 import with Settings selected); only then are they put back.
        let writesForeignTopLevelExtensions: Bool
        /// Their stored value before the import.
        let foreignTopLevelExtensions: String?
    }

    /// Puts back what an import changed before its modes/vocabulary store step
    /// failed (#1627): the foreign top-level extensions, when this import wrote
    /// them, and the settings section, when it was applied. Returns whether the
    /// settings section was put back.
    ///
    /// The whole snapshot is written back, so a setting the user changed in the
    /// app during the import is put back too. A hard kill between the settings
    /// step and this restore is out of scope (as on Windows and Linux).
    func restoreSettingsAfterFailedStoreImport(
        _ snapshot: ImportSettingsSnapshot,
        settingsApplied: Bool
    ) async -> Bool {
        if snapshot.writesForeignTopLevelExtensions {
            settingsStore.foreignTopLevelExtensions = snapshot.foreignTopLevelExtensions
        }
        guard settingsApplied else { return false }
        await settingsStore.restore(snapshot.settings)
        AppLogger.settings.info("Backup import: the modes/vocabulary save failed, so the settings were put back as they were")
        return true
    }

    // MARK: - Steps shared by the v1 and universal v2 imports

    /// Which import a summary log line is written for.
    enum BackupImportFormat {
        case legacyV1
        case universalV2
    }

    /// What an import's modes/vocabulary store step did (#1613).
    struct BackupStoreStepOutcome {
        /// Whether the import had modes to write (selected AND present).
        var modesSelected = false
        /// Whether the import had vocabulary to write (selected AND present).
        var vocabularySelected = false
        var modesImported = 0
        var modesSkipped = 0
        var vocabImported = 0
        var vocabSkipped = 0
        /// The store section that failed and was rolled back, if any.
        var storeFailure: PersistenceController.BackupStoreImportError?
    }

    /// Imports modes and vocabulary (each only when selected AND present; vocabulary is
    /// merge only, never a wipe), each section as ONE store transaction (#1613): modes
    /// first, then vocabulary only if the modes saved. A failed section is left as it
    /// was. The API-key and licence steps after it still run, as they always did; the
    /// result then says which section failed instead of a summary.
    ///
    /// Per-mode default model selections are applied only when the user actually chose
    /// to import Settings, modes were written and the store step saved (#1627: a failed
    /// store step applies no settings). `defaultModelByModeToApply` is read only then.
    func importBackupStoreSections(
        modes modesToImport: [BackupMode]?,
        vocabulary vocabToImport: [BackupVocabularyItem]?,
        options: ImportOptions,
        defaultModelByModeToApply: () -> [String: String]?
    ) -> BackupStoreStepOutcome {
        var outcome = BackupStoreStepOutcome(
            modesSelected: modesToImport != nil,
            vocabularySelected: vocabToImport != nil
        )
        if modesToImport != nil || vocabToImport != nil {
            let storeResult: PersistenceController.BackupStoreImportResult
            do {
                storeResult = try importPersistence.importBackupStore(
                    modes: modesToImport,
                    modeResolution: options.modeConflict,
                    vocabulary: vocabToImport,
                    vocabularyResolution: options.vocabularyConflict
                )
            } catch {
                let failure = Self.backupStoreImportFailure(from: error, modesSelected: modesToImport != nil)
                outcome.storeFailure = failure
                storeResult = failure.committed
            }
            outcome.modesImported = storeResult.modesImported
            outcome.modesSkipped = storeResult.modesSkipped
            outcome.vocabImported = storeResult.vocabularyImported
            outcome.vocabSkipped = storeResult.vocabularySkipped
            if outcome.storeFailure == nil, modesToImport != nil, options.importSettings, let map = defaultModelByModeToApply() {
                applyDefaultModelByMode(map, idRemap: storeResult.modeIdRemap)
            }
        }
        return outcome
    }

    /// Everything an import does after its store step, in the order the v1 and v2
    /// imports always ran it: a failed store step puts the settings back; then the
    /// API keys (`apiKeysStep` returns nil when there are none to import); then the
    /// licence key; then the summary log, the post-restore repair and the result.
    func finishBackupImport(
        _ format: BackupImportFormat,
        store: BackupStoreStepOutcome,
        settingsBefore: ImportSettingsSnapshot?,
        settingsApplied settingsAppliedBeforeStoreStep: Bool,
        licenseKey backupLicenseKey: String?,
        options: ImportOptions,
        apiKeysStep: () -> APIKeyRestoreOutcome?
    ) async -> ImportResult {
        let storeFailure = store.storeFailure
        let modesImported = store.modesImported
        let modesSkipped = store.modesSkipped
        let vocabImported = store.vocabImported
        let vocabSkipped = store.vocabSkipped
        var settingsApplied = settingsAppliedBeforeStoreStep

        // A failed store step puts the settings (and, for v2, the foreign extensions)
        // back as they were before the import (#1627, the same choice as Windows #1614
        // and Linux).
        var settingsRestored = false
        if storeFailure != nil, let settingsBefore {
            settingsRestored = await restoreSettingsAfterFailedStoreImport(settingsBefore, settingsApplied: settingsApplied)
            settingsApplied = false
        }

        // Import API keys if present and requested. Success is read off the
        // Keychain writes, not off the file; a failed key does not stop the import.
        var apiKeysImported = false
        var apiKeysFailedProviders: [KeychainManager.APIKeyType] = []
        if let outcome = apiKeysStep() {
            apiKeysImported = outcome.anySaved
            apiKeysFailedProviders = outcome.failed
        }

        // Import license key if present and requested. Trim first (an untrimmed
        // key would break the offline-guard key comparison), then revalidate so
        // the new key doesn't inherit the previous key's cached status.
        var licenseKeyImported = false
        if options.importLicenseKey,
           let licenseKey = backupLicenseKey?.trimmingCharacters(in: .whitespacesAndNewlines),
           !licenseKey.isEmpty {
            guard await importLicenseKeySecurely(licenseKey) else {
                return licenseKeyFailedImportResult(
                    store: store,
                    settingsApplied: settingsApplied,
                    settingsRestored: settingsRestored,
                    apiKeysImported: apiKeysImported,
                    apiKeysFailedProviders: apiKeysFailedProviders,
                    options: options
                )
            }
            licenseKeyImported = true
        }

        switch format {
        case .legacyV1:
            AppLogger.settings.info("Settings imported: \(modesImported) modes, \(vocabImported) vocabulary items")
        case .universalV2:
            AppLogger.settings.info("Universal v2 backup imported: settingsApplied=\(settingsApplied, privacy: .public), \(modesImported) modes, \(vocabImported) vocabulary items")
        }

        // Post-restore: silently turn off local modes that can't run here, and (on
        // capable hardware) collect cataloged-but-undownloaded models to offer a
        // batched re-download. Only when modes were actually imported.
        // A failed store section (#1613) is reported instead of a summary; the
        // repair runs there only when the modes saved (see storeImportFailureResult).
        if let storeFailure {
            return storeImportFailureResult(
                storeFailure,
                modesSelected: store.modesSelected,
                vocabularySelected: store.vocabularySelected,
                settingsApplied: settingsApplied,
                settingsRestored: settingsRestored,
                apiKeysImported: apiKeysImported,
                licenseKeyImported: licenseKeyImported,
                repairImportedModes: options.importModes,
                apiKeysFailedProviders: apiKeysFailedProviders
            )
        }
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
        result.apiKeysFailedProviders = apiKeysFailedProviders
        result.settingsApplied = settingsApplied
        return result
    }

    /// The result when the licence key could not be securely imported, after the
    /// store and API-key steps ran.
    func licenseKeyFailedImportResult(
        store: BackupStoreStepOutcome,
        settingsApplied: Bool,
        settingsRestored: Bool,
        apiKeysImported: Bool,
        apiKeysFailedProviders: [KeychainManager.APIKeyType],
        options: ImportOptions
    ) -> ImportResult {
        // The rolled-back store section (#1613) must be reported too.
        if let storeFailure = store.storeFailure {
            return storeImportFailureResult(
                storeFailure,
                modesSelected: store.modesSelected,
                vocabularySelected: store.vocabularySelected,
                settingsApplied: settingsApplied,
                settingsRestored: settingsRestored,
                apiKeysImported: apiKeysImported,
                licenseKeyImported: false,
                licenseImportFailed: true,
                repairImportedModes: options.importModes,
                apiKeysFailedProviders: apiKeysFailedProviders
            )
        }
        return licenseImportFailureResult(
            modesImported: store.modesImported,
            modesSkipped: store.modesSkipped,
            vocabularyImported: store.vocabImported,
            vocabularySkipped: store.vocabSkipped,
            apiKeysImported: apiKeysImported,
            earlierSectionsApplied: Self.earlierBackupSectionsWereApplied(
                settingsApplied: settingsApplied,
                modesImported: store.modesImported,
                vocabularyImported: store.vocabImported,
                apiKeysImported: apiKeysImported
            ),
            repairImportedModes: options.importModes,
            apiKeysFailedProviders: apiKeysFailedProviders
        )
    }
}
