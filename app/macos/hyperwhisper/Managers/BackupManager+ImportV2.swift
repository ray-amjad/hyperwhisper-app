//
//  BackupManager+ImportV2.swift
//  hyperwhisper
//
//  Universal v2 import (M3-D): validate, settings, modes, vocabulary, keys
//  and licence for a full `.hwbackup.json`.
//  Split out of BackupManager.swift (#732); same class, same behaviour.
//

import Foundation
import SwiftUI
import AppKit

extension BackupManager {

    /// Imports a FULL universal-v2 `.hwbackup.json` (M3-D) — settings AND/OR modes present
    /// (Windows backups, or a macOS v2 export). Additive: this branch is only reached when the
    /// file carries settings/modes; vocab-only v2 files still go through `importUniversalVocab`.
    ///
    /// Order: validate-first (reject before mutating) -> settings (REJOIN platformExtensions ->
    /// core inverse mapping -> applySettings UNCHANGED) -> modes (DTO -> present-only migrations ->
    /// existing importModes -> defaultModelByMode from parked extensions) -> vocab/keys/license.
    func importUniversalV2(jsonData: Data, rawString: String?, topLevel: [String: Any], options: ImportOptions) async -> ImportResult {
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

        var settingsApplied = false

        // 2. SETTINGS (only when selected AND present): REJOIN -> core inverse map -> MERGE over the
        //    current live settings -> applySettings.
        //
        // DEFENSE IN DEPTH: the entire settings step is wrapped in do/catch. A Windows backup (or any
        // file) that can't produce a complete macOS-settings object must NOT abort the import — we log
        // and continue to modes/vocab/keys. `settingsApplied` records whether settings actually applied.
        // The snapshot (and the foreign top-level extensions it replaces, #288) comes first: see
        // snapshotSettingsForV2Import.
        let settingsBefore = await snapshotSettingsForV2Import(dto, options: options)

        if options.importSettings, let settingsBefore, let settingsValue = dto.settings {
            do {
                // The snapshot is also the baseline the merge fills missing fields from.
                let backupSettings = try Self.macosSettings(
                    fromV2: settingsValue,
                    platformExtensions: dto.platformExtensions,
                    baseline: settingsBefore.settings
                )
                await settingsStore.apply(backupSettings)
                settingsApplied = true
            } catch {
                // Never abort the import for a settings problem — log and continue.
                AppLogger.settings.warning("v2 import: settings step failed, continuing with modes/vocab/keys: \(error.localizedDescription, privacy: .public)")
            }
        }

        // 3. MODES (only when selected AND present): DTO -> present-only migrations -> BackupMode.
        //    Built here, written in step 4 together with the vocabulary.
        var modesToImport: [BackupMode]?
        if options.importModes, let modeDTOs = dto.modes {
            modesToImport = Self.backupModes(fromV2: modeDTOs)
        }

        // 4. VOCABULARY (merge only — never a wipe), reusing the existing universal-vocab logic.
        var vocabToImport: [BackupVocabularyItem]?
        if options.importVocabulary, let vocabArray = topLevel["vocabulary"] as? [[String: Any]] {
            vocabToImport = Self.backupVocabularyItems(fromUniversal: vocabArray)
        }

        // Steps 3 + 4 are written one section per store transaction (#1613): modes first,
        // then vocabulary only if the modes saved; a failed section is left as it was.
        // Step 5 still runs, as it always did; the result then says which section failed.
        // Per-mode default model selections are parked under
        // platformExtensions.macos.settings.defaultModelByMode in v2 (NOT top-level settings).
        // Only applied when Settings import was chosen (mirrors the v1 guard).
        let store = importBackupStoreSections(
            modes: modesToImport,
            vocabulary: vocabToImport,
            options: options,
            defaultModelByModeToApply: { Self.defaultModelByModeFromExtensions(dto.platformExtensions) }
        )

        // A failed store step puts the settings (and the foreign extensions) back, then
        // 5. API keys + license (reuse the existing flat-lowercase logic + license write).
        //    Success is read off the Keychain writes, not off the file. See finishBackupImport.
        return await finishBackupImport(
            .universalV2,
            store: store,
            settingsBefore: settingsBefore,
            settingsApplied: settingsApplied,
            licenseKey: dto.licenseKey,
            options: options,
            apiKeysStep: {
                guard options.importAPIKeys, let apiKeys = dto.apiKeys, !apiKeys.isEmpty else { return nil }
                return importUniversalAPIKeys(apiKeys)
            }
        )
    }

    /// Step 2 setup of a v2 import, only when Settings import was chosen: snapshots the
    /// live settings and the stored foreign top-level `platformExtensions`, then stores
    /// this file's foreign slices. Returns nil (and changes nothing) otherwise.
    ///
    /// Preserve the foreign TOP-LEVEL platformExtensions slices before anything
    /// else touches them, so the next export re-emits them (#288). Gated on
    /// importSettings for symmetry with the export, which only writes the
    /// top-level map when settings are included — and so a vocabulary-only
    /// interchange file cannot wipe the store.
    ///
    /// Snapshot both first: a failed store step puts them back (#1627).
    /// Launch-at-login is a cached value, so `current()` re-reads it (off the
    /// main thread, #853) — the snapshot is also the baseline the settings
    /// merge fills missing fields from.
    func snapshotSettingsForV2Import(_ dto: UniversalBackupDTO, options: ImportOptions) async -> ImportSettingsSnapshot? {
        var settingsBefore: ImportSettingsSnapshot?
        if options.importSettings {
            let liveSettings = await settingsStore.current()
            settingsBefore = ImportSettingsSnapshot(
                settings: liveSettings,
                writesForeignTopLevelExtensions: true,
                foreignTopLevelExtensions: settingsStore.foreignTopLevelExtensions
            )
            let foreign = Self.foreignTopLevelExtensions(from: dto.platformExtensions)
            // REPLACE, not merge: the store describes the LAST IMPORTED file.
            settingsStore.foreignTopLevelExtensions = foreign
        }
        return settingsBefore
    }

    /// The macOS settings a v2 file's `settings` section restores: the 5 universal
    /// categories plus the top-level `platformExtensions` (the macOS-only fields),
    /// mapped back by the core and deep-merged over `baseline`, so any field the
    /// file lacks (a Windows backup has no macOS-only fields) keeps its `baseline`
    /// value. Pure; throws when the result is not a complete macOS settings object.
    nonisolated static func macosSettings(
        fromV2 settingsValue: JSONValue,
        platformExtensions: JSONValue?,
        baseline: BackupSettings
    ) throws -> BackupSettings {
        // Reconstruct the SettingsRecord JSON: the 5 universal categories from the top-level
        // `settings`, plus the backup TOP-LEVEL `platformExtensions` RE-INJECTED as the
        // record-level `platformExtensions` (exact inverse of the export hoist). Without this,
        // `universal_to_macos_settings` finds no `platformExtensions.macos.settings` and returns
        // ALL macOS-only fields blank.
        var recordObj: [String: JSONValue] = settingsValue.objectValue ?? [:]
// MUTATION         if let ext = platformExtensions {
// MUTATION             recordObj["platformExtensions"] = ext
// MUTATION         }
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
        // lacks decodes successfully with the user's current value. The import's snapshot
        // re-read launch-at-login, so a backup without the field never applies a stale one.
        let baselineValue = try Self.settingsBaseline(baseline)

        // DEEP-MERGE imported OVER baseline: imported values win where present; baseline fills
        // every field the backup didn't carry.
        let mergedValue = importedValue.deepMerged(over: baselineValue)

        guard let mergedData = try? encoder.encode(mergedValue) else {
            throw BackupV2Error.settingsEncodeFailed
        }
        // Decode the MERGED 7-category macOS settings into BackupSettings (applied UNCHANGED).
        return try JSONDecoder().decode(BackupSettings.self, from: mergedData)
    }

    /// The modes a v2 file's `modes` section restores, in file order, each through
    /// `backupMode(fromV2:)`. A mode whose id is not a UUID is skipped and logged. Pure
    /// apart from that log line.
    nonisolated static func backupModes(fromV2 modeDTOs: [UniversalModeDTO]) -> [BackupMode] {
        let modes: [BackupMode] = modeDTOs.compactMap { modeDTO in
            guard let mode = Self.backupMode(fromV2: modeDTO) else {
                // Malformed/unparseable id — skip but make it VISIBLE (real producers emit
                // valid UUIDs/GUIDs; we do not invent a replacement id).
                // PRIVACY (issue #795): neither the Mode's `name` NOR its `id` is
                // reported. `backupMode(fromV2:)` returns nil ONLY when
                // `UUID(uuidString: dto.id)` fails, so on this branch the id is
                // provably NOT a UUID — it is arbitrary text out of the backup
                // file, which a hand-edited or third-party file can fill with
                // anything, including a name. This line is `.warning`, and
                // `AppLogger.getRecentLogs` DOES capture `.warning` records into
                // the `recent_logs` extra that `SentryService.capture` attaches.
                // The LENGTH is reported instead: it is not user content, and it
                // separates every failure a real producer can hit — 0 is an absent
                // or empty id, 36 is the right shape with bad characters (a
                // truncated or altered UUID), 38 is a Windows brace-wrapped
                // `{GUID}`, and anything else is arbitrary text. One line is
                // emitted per skipped row, so the count stays visible too.
                let invalidIdLength = modeDTO.id.count
                AppLogger.settings.warning("v2 import: skipping mode whose id is not a UUID · idLength=\(invalidIdLength, privacy: .public)")
                return nil
            }
            return mode
        }
        return modes
    }

    /// The vocabulary a universal file's raw `vocabulary` array restores, read
    /// leniently: an entry without a usable word is skipped, a missing or bad id
    /// gets a fresh one, a missing sortOrder is 0. Pure.
    nonisolated static func backupVocabularyItems(fromUniversal vocabArray: [[String: Any]]) -> [BackupVocabularyItem] {
        let items: [BackupVocabularyItem] = vocabArray.compactMap { entry in
            guard let word = entry["word"] as? String,
                  !word.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            let id = (entry["id"] as? String).flatMap(UUID.init) ?? UUID()
            let sortOrder = entry["sortOrder"] as? Int ?? 0
            return BackupVocabularyItem(
                id: id,
                word: word,
                replacement: nil, // MUTATION
                sortOrder: Int16(clamping: sortOrder),
                source: entry["source"] as? String
            )
        }
        return items
    }

    /// Internal errors thrown by the v2 import settings step (caught locally so the step can fail
    /// soft without aborting the whole import).
    private enum BackupV2Error: Error {
        case settingsEncodeFailed
        case settingsDecodeFailed
    }

    /// The CURRENT live macOS settings as a 7-category `JSONValue`, to serve as the baseline
    /// the v2 settings import merges OVER. Built from the SAME `BackupSettings` the v1 export path
    /// builds (`liveBackupSettings`, via the import's snapshot), so the baseline is always a
    /// complete, decodable object — guaranteeing a Windows backup (missing macOS-only fields) still
    /// decodes after the merge.
    nonisolated private static func settingsBaseline(_ settings: BackupSettings) throws -> JSONValue {
        let data = try JSONEncoder().encode(settings)
        return try JSONDecoder().decode(JSONValue.self, from: data)
    }

    /// Projects a `UniversalModeDTO` into the internal `BackupMode`, running the present-only cloud
    /// migrations BEFORE constructing the mode. Migrations run only when the source value is
    /// non-nil/non-empty so we never write a default where the source intended `nil`.
    ///
    /// Internal (not private) so the tests can reach it.
    nonisolated static func backupMode(fromV2 dto: UniversalModeDTO) -> BackupMode? {
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
            // #1481: macOS's own per-mode slice first. A Windows or Linux backup
            // carries enableScreenOCR in its own slice, so that is the fallback;
            // neither has a per-mode streaming flag. Absent stays nil, and
            // importModes keeps the local value.
            enableScreenOCR: dto.platformExtensionBool(
                UniversalModeDTO.enableScreenOCRKey,
                platforms: ["macos", "windows", "linux"]
            ),
            useStreamingTranscription: dto.platformExtensionBool(
                UniversalModeDTO.useStreamingTranscriptionKey,
                platforms: ["macos"]
            ),
            foreignPlatformExtensions: foreignExt
        )
    }

    /// Extracts `defaultModelByMode` from the parked `platformExtensions.macos.settings` blob
    /// (where v2 stores it). Returns nil when absent/empty.
    nonisolated static func defaultModelByModeFromExtensions(_ ext: JSONValue?) -> [String: String]? {
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
}
