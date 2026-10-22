//
//  PersistenceController+Modes.swift
//  hyperwhisper
//
//  Mode reads, writes and the default-mode invariant.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

extension PersistenceController {

    /// Inserts the one mode a fresh install creates, unconditionally.
    ///
    /// `initializeDefaultModes()` owns WHEN the store may be seeded — its
    /// `count > 0 → return` guard is the "existing users are never re-seeded"
    /// contract and does not move. This owns WHAT gets written, and it is
    /// separate because onboarding needs the same row on a path that guard
    /// deliberately refuses to act on: the flagged default mode has been deleted
    /// while other modes remain, so the store is not empty but the default is
    /// missing.
    ///
    /// The alternative — letting `createOrUpdateMode` create it — is NOT
    /// equivalent and was the bug this fixes. Its CREATE branch hardcodes
    /// `isSystemProvided = false` and `sortOrder = maxSortOrder + 1`, against
    /// the seed's `true` and `0`, and the row is permanent because
    /// `initializeDefaultModes()` returns early once any mode exists.
    ///
    /// ⚠️ `Mode`'s Core Data attribute defaults are hostile — an unwritten field
    /// inherits a stale legacy value (`whisper-1`, `claudeHaiku`, `openai`,
    /// `en`, `base`) rather than a null. `applySeededValues` is where every
    /// column is written explicitly and where that list is documented; do not
    /// thin it out, and do not hand-roll a second copy of it here.
    ///
    /// - Parameter persist: `false` when the caller owns the save — the
    ///   first-launch path saves with its own diagnostics.
    @discardableResult
    func seedDefaultMode(persist: Bool = true) -> Mode {
        let mode = Mode(context: container.viewContext)
        mode.applySeededValues(SeededModeValues.forCurrentRegion)
        if persist { save() }
        return mode
    }

    /// Fetches all modes sorted by sortOrder
    /// - Returns: Array of modes
    func fetchAllModes() -> [Mode] {
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Mode.sortOrder, ascending: true)]
        
        do {
            return try container.viewContext.fetch(request)
        } catch {
            AppLogger.coreData.error("Failed to fetch modes: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch all modes", tags: ["component": "PersistenceController", "operation": "fetchAllModes"])
            return []
        }
    }

    /// Fetch all mode properties on a background context. Recording UI uses
    /// these snapshots so SwiftUI menu/dialog re-evaluation never fault-fills
    /// Mode managed objects on the main context during recording start.
    func fetchAllModeSnapshotsInBackground() async -> [ModeSnapshot] {
        let context = container.newBackgroundContext()
        return await context.perform {
            let request: NSFetchRequest<Mode> = Mode.fetchRequest()
            request.sortDescriptors = [NSSortDescriptor(keyPath: \Mode.sortOrder, ascending: true)]

            do {
                return try context.fetch(request).map(ModeSnapshot.init)
            } catch {
                AppLogger.coreData.error("Failed to fetch mode snapshots in background: \(error, privacy: .public)")
                return []
            }
        }
    }
    
    /// Finds the default mode
    /// - Returns: The default mode, if it exists
    func findDefaultMode() -> Mode? {
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "isDefault == YES")
        request.fetchLimit = 1
        
        do {
            return try container.viewContext.fetch(request).first
        } catch {
            AppLogger.coreData.error("Failed to fetch default mode: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch default mode", tags: ["component": "PersistenceController", "operation": "findDefaultMode"])
            return nil
        }
    }

    /// Fetch a specific mode by its identifier. RETRIEVAL FLOW:
    /// 1. Convert the stored string UUID into an actual UUID instance.
    /// 2. Execute a lightweight Core Data fetch limited to a single result.
    /// 3. Return the mode (or nil when the identifier is malformed/not found).
    func fetchMode(withId id: String) -> Mode? {
        guard let uuid = UUID(uuidString: id) else { return nil }
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
        request.fetchLimit = 1

        do {
            return try container.viewContext.fetch(request).first
        } catch {
            AppLogger.coreData.error("Failed to fetch mode by id: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch mode by id", tags: ["component": "PersistenceController", "operation": "modeWithId"])
            return nil
        }
    }

    /// Fetch a specific mode by identifier on a background context, then return
    /// the corresponding viewContext object for UI/main-actor consumers.
    func fetchModeInBackground(withId id: String) async -> Mode? {
        guard let uuid = UUID(uuidString: id) else { return nil }
        let context = container.newBackgroundContext()
        let objectID = await context.perform { () -> NSManagedObjectID? in
            let request: NSFetchRequest<Mode> = Mode.fetchRequest()
            request.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
            request.fetchLimit = 1

            do {
                return try context.fetch(request).first?.objectID
            } catch {
                AppLogger.coreData.error("Failed to fetch mode ID in background: \(error, privacy: .public)")
                return nil
            }
        }

        guard let objectID else { return nil }
        return await MainActor.run {
            guard let object = try? container.viewContext.existingObject(with: objectID) else {
                return nil
            }
            return object as? Mode
        }
    }

    /// Fetch a mode by name with a targeted predicate.
    /// This avoids loading/sorting the full mode table when only a fallback match is needed.
    func fetchMode(byName name: String) -> Mode? {
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "name == %@", name)
        request.fetchLimit = 1

        do {
            return try container.viewContext.fetch(request).first
        } catch {
            AppLogger.coreData.error("Failed to fetch mode by name: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch mode by name", tags: ["component": "PersistenceController", "operation": "modeWithName"])
            return nil
        }
    }

    /// Fetch a mode's properties on a background context to avoid blocking the main thread.
    /// Returns a thread-safe value-type snapshot instead of a managed object.
    /// Fixes Sentry HYPERWHISPER-KP (DB on Main Thread during Recording Start).
    func fetchModeSnapshotInBackground(withId id: String) async -> ModeSnapshot? {
        guard let uuid = UUID(uuidString: id) else { return nil }
        let context = container.newBackgroundContext()
        return await context.perform {
            let request: NSFetchRequest<Mode> = Mode.fetchRequest()
            request.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
            request.fetchLimit = 1
            do {
                guard let mode = try context.fetch(request).first else { return nil }
                return ModeSnapshot(mode)
            } catch {
                AppLogger.coreData.error("Failed to fetch mode snapshot: \(error, privacy: .public)")
                return nil
            }
        }
    }

    /// Resolve a transcription mode without running fetch requests on the main context.
    ///
    /// The stop/retry paths are called while SwiftUI is transitioning recording UI state.
    /// Keep the fallback chain equivalent to the recording hot-path logic:
    /// mode id -> mode name -> default mode.
    ///
    /// `allowNameFallback: false` resolves by id only: a mode the user picked
    /// must never resolve to a different mode that happens to share its name.
    func resolveTranscriptionModeInBackground(
        id: String,
        fallbackName: String,
        allowNameFallback: Bool = true,
        allowDefaultFallback: Bool = true
    ) async -> Mode? {
        let context = container.newBackgroundContext()
        let objectID = await context.perform { () -> NSManagedObjectID? in
            if let uuid = UUID(uuidString: id) {
                let byId: NSFetchRequest<Mode> = Mode.fetchRequest()
                byId.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
                byId.fetchLimit = 1
                if let mode = try? context.fetch(byId).first {
                    return mode.objectID
                }
            }

            guard allowNameFallback else { return nil }

            let byName: NSFetchRequest<Mode> = Mode.fetchRequest()
            byName.predicate = NSPredicate(format: "name == %@", fallbackName)
            byName.fetchLimit = 1
            if let mode = try? context.fetch(byName).first {
                return mode.objectID
            }

            guard allowDefaultFallback else { return nil }

            let byDefault: NSFetchRequest<Mode> = Mode.fetchRequest()
            byDefault.predicate = NSPredicate(format: "isDefault == YES")
            byDefault.fetchLimit = 1
            return try? context.fetch(byDefault).first?.objectID
        }

        guard let objectID else { return nil }
        return await MainActor.run {
            guard let object = try? container.viewContext.existingObject(with: objectID) else {
                return nil
            }
            return object as? Mode
        }
    }
    
    /// Creates or updates a mode
    /// - Parameters:
    ///   - name: Mode name
    ///   - preset: Preset type
    ///   - language: Language code
    ///   - model: Model ID
    ///   - punctuation: Enable punctuation
    ///   - capitalization: Enable capitalization
    ///   - profanityFilter: Enable profanity filter
    ///   - customInstructions: Custom instructions for "custom" preset
    /// - Returns: The created or updated mode
    @discardableResult
    func createOrUpdateMode(
        id: UUID? = nil,
        name: String,
        preset: String,
        language: String,
        model: String,
        punctuation: Bool,
        capitalization: Bool,
        profanityFilter: Bool,
        customInstructions: String? = nil,
        languageModel: String? = nil,
        cloudProvider: String? = nil,
        cloudTranscriptionModel: String? = nil,
        postProcessingMode: Int16 = 1,  // Default to cloud (1)
        postProcessingProvider: String? = nil,
        englishSpelling: String? = nil,
        userSystemPrompt: String? = nil,
        useStreamingTranscription: Bool = false,
        cloudAccuracyTier: String? = nil,
        removeTrailingPeriod: Bool = false,
        enableScreenOCR: Bool = false,
        geminiCustomPrompt: String? = nil,
        cloudPostProcessingModel: String? = nil,
        cloudTranscriptionDomain: String? = nil,
        foreignPlatformExtensions: String? = nil,
        persist: Bool = true,
        restoringFromBackup: Bool = false,
        updating existingMode: Mode? = nil,
        in suppliedContext: NSManagedObjectContext? = nil
    ) -> Mode {
        let context = suppliedContext ?? container.viewContext

        // Check if mode exists (for update). A caller that already holds the
        // row (the backup restore's in-place `.replace`, issue #1479) passes it
        // in, so the update never depends on a predicate fetch matching an
        // unsaved `id` change.
        var mode: Mode? = existingMode
        if mode == nil, let id = id {
            let request: NSFetchRequest<Mode> = Mode.fetchRequest()
            request.predicate = NSPredicate(format: "id == %@", id as CVarArg)
            request.fetchLimit = 1
            mode = try? context.fetch(request).first
        }
        
        // Create new mode if not found
        if mode == nil {
            mode = Mode(context: context)
            mode?.id = id ?? UUID()
            mode?.createdDate = Date()
            mode?.isSystemProvided = false
            
            // Set sort order for new mode
            let request: NSFetchRequest<Mode> = Mode.fetchRequest()
            let maxSortOrder = (try? context.fetch(request).map(\.sortOrder).max()) ?? 0
            mode?.sortOrder = maxSortOrder == .max ? .max : maxSortOrder + 1
        }
        
        // Update mode properties.
        //
        // The default mode's name is fixed (issue #536). The editor disables the
        // field and says so; here the stored name is kept rather than an error
        // raised, because this is also the path a backup restore and an
        // onboarding rollback take, and neither should be abandoned over a field
        // its caller did not mean to change. `ModesEndpoint` checks first so the
        // API can say no out loud.
        //
        // A restore is exempt. It is not a rename: it replays a whole store, and
        // which row carries the flag afterwards is decided by the backup, not by
        // whichever row happens to hold it right now. Holding the lock here
        // would keep the LOCAL name on a row the backup renamed — and Windows
        // and Linux (`ApplicationBackupSelection.ApplyModes`, which does a plain
        // `SetValues`) restore the backup's name — so the same file would land
        // with different mode names per platform, which is the divergence this
        // issue exists to remove. `importModes` settles the flag straight after,
        // and `enforceDefaultModeInvariant()` backstops the whole set.
        if let existing = mode, !restoringFromBackup, !DefaultModePolicy.canRename(existing, to: name) {
            AppLogger.coreData.warning(
                "createOrUpdateMode: refused to rename the default mode · its name is fixed"
            )
        } else {
            mode?.name = ModeNamePolicy.storageName(name, replacing: mode?.name)
        }
        mode?.preset = preset
        mode?.language = LanguageData.canonicalLanguageCode(language)
        mode?.model = model
        mode?.punctuation = punctuation
        mode?.capitalization = capitalization
        mode?.profanityFilter = profanityFilter
        mode?.customInstructions = customInstructions ?? ""
        let trimmedUserPrompt = userSystemPrompt?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if trimmedUserPrompt.isEmpty {
            mode?.userSystemPrompt = nil
        } else {
            mode?.userSystemPrompt = String(trimmedUserPrompt.prefix(userSystemPromptCharacterLimit))
        }

        let modeEnum = PostProcessingMode(rawValue: postProcessingMode) ?? .cloud
        let normalizedLanguageModel: String
        if modeEnum == .local {
            normalizedLanguageModel = languageModel ?? PostProcessingProvider.localLLM.defaultModel
        } else {
            normalizedLanguageModel = languageModel ?? "gpt-5.6-luna"
        }

        mode?.languageModel = normalizedLanguageModel
        // Canonicalise on the WRITE path: this is the single funnel every mode
        // write goes through (GUI edit, Local API PATCH, backup restore), so a
        // camelCase id from a Windows/Linux backup — `geminiTranscribe` — is
        // stored in the lowercase form every reader and every picker tag below
        // expects. An id this build does not model passes through verbatim.
        mode?.cloudProvider = CloudProvider.canonicalStorageValue(cloudProvider) ?? CloudProvider.effectiveModeStorageValue(nil)
        // cloudTranscriptionModel is assigned below, after the accuracy tier is
        // resolved, so an omitted model derives from the resolved tier's catalog
        // default (e.g. scribe_v2) instead of persisting a stale "whisper-1".
        mode?.postProcessingMode = postProcessingMode

        // A provider this app DERIVES is written in its canonical cross-platform
        // spelling — HyperWhisper Cloud is "hyperwhispercloud", the token Windows
        // and the Linux/portable head already store. A provider the CALLER
        // supplied is stored verbatim: the Local API contract on both heads is
        // "tolerant on read, verbatim on write" (`ModesEndpoints.cs:106,397`
        // normalise neither create nor patch), and a restore must round-trip a
        // foreign head's string unchanged. Every reader folds the spellings back
        // together through `PostProcessingProvider(rawValue:)`.
        if modeEnum == .local {
            mode?.postProcessingProvider = PostProcessingProvider.localLLM.storageValue
        } else if let provider = postProcessingProvider {
            mode?.postProcessingProvider = provider
        } else {
            mode?.postProcessingProvider = modeEnum.defaultProvider?.storageValue
                ?? PostProcessingProvider.hyperwhisper.storageValue
        }
        // An omitted spelling lands on the system region's variant, the same value
        // the GUI seeds into a new mode. Callers that forward an existing mode's
        // value (onboarding, backup restore) still pass it through unchanged.
        mode?.englishSpelling = englishSpelling ?? EnglishSpelling.defaultForCurrentRegion.rawValue
        mode?.useStreamingTranscription = useStreamingTranscription
        // API/MCP-created cloud modes that omit the accuracy tier should land on
        // the SAME recommended engine the GUI seeds for new modes
        // (ElevenLabs Scribe v2), not the legacy `fromStorageValue(nil)` fallback
        // (deepgramNova3). Non-empty values still flow through `fromStorageValue`
        // so legacy aliases keep migrating correctly.
        let resolvedAccuracyTier = (cloudAccuracyTier?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
            ? CloudAccuracyTier.elevenLabsScribeV2.rawValue
            : CloudAccuracyTier.fromStorageValue(cloudAccuracyTier).rawValue
        mode?.cloudAccuracyTier = resolvedAccuracyTier
        // Derive an omitted transcription model so the API/MCP path never
        // persists a misleading "whisper-1". For HyperWhisper Cloud modes the
        // engine is chosen by the accuracy tier, so use the tier's catalog
        // default (mirrors the seedDefaultModes fix). For direct BYOK providers
        // (openai / soniox / …) there is no accuracy tier — fall back to THAT
        // provider's own default model, otherwise a tier-derived id like
        // "scribe_v2" would be persisted and sent to e.g. OpenAI and rejected.
        let resolvedCloudProvider = CloudProvider.forMode(mode?.cloudProvider)
        mode?.cloudTranscriptionModel = cloudTranscriptionModel
            ?? (resolvedCloudProvider == .hyperwhisper
                ? (CloudAccuracyTier(rawValue: resolvedAccuracyTier)?.defaultModelId ?? "")
                : CloudTranscriptionModels.defaultModel(for: resolvedCloudProvider))
        mode?.removeTrailingPeriod = removeTrailingPeriod
        mode?.enableScreenOCR = enableScreenOCR
        // Preserve a foreign (non-macOS) per-mode platformExtensions blob captured
        // on a v2 import (H4). Only assign when explicitly provided so an unrelated
        // GUI/API mode edit (which passes nil) never wipes a stored foreign slice.
        //
        // A restore is the exception: it writes the backup row's slice even
        // when that is nil. `.replace` updates the local row in place (issue
        // #1479), and the row must end up exactly as a fresh create from the
        // backup row would leave it — so a stale slice from an earlier foreign
        // import must not survive a restore of a backup that has none, or
        // `BackupMode(from:)` would re-emit it into every later export.
        if restoringFromBackup || foreignPlatformExtensions != nil {
            mode?.foreignPlatformExtensions = foreignPlatformExtensions
        }
        let trimmedGeminiPrompt = geminiCustomPrompt?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if trimmedGeminiPrompt.isEmpty {
            mode?.geminiCustomPrompt = nil
        } else {
            mode?.geminiCustomPrompt = String(trimmedGeminiPrompt.prefix(2000))
        }
        // Same alignment for post-processing: an absent model should land on the
        // GUI's recommended engine (Claude Haiku), not the legacy
        // `fromStorageValue(nil)` fallback (grokFast). Non-empty values still go
        // through `fromStorageValue` to preserve legacy migration.
        let resolvedPostProcessingModel = (cloudPostProcessingModel?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
            ? CloudPostProcessingModel.claudeHaiku.rawValue
            : CloudPostProcessingModel.fromStorageValue(cloudPostProcessingModel).rawValue
        mode?.cloudPostProcessingModel = resolvedPostProcessingModel
        let trimmedDomain = cloudTranscriptionDomain?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        mode?.cloudTranscriptionDomain = trimmedDomain.isEmpty ? nil : trimmedDomain
        mode?.modifiedDate = Date()

        if persist {
            save()
        }

        return mode!
    }

    /// Deletes a mode the user asked to delete, then repairs the app state that
    /// pointed at it. The Modes page and Local API `DELETE /modes/:id` both use
    /// this, so the two cannot drift apart again.
    ///
    /// Issue #1439: the Modes page read `mode.id` AFTER `deleteMode(_:)` had
    /// deleted and saved the object. A deleted, saved Core Data object no longer
    /// gives attribute values, so the id read back nil, the "was it selected?"
    /// test was always false, and the app stayed on the deleted mode until a
    /// relaunch. The id is read here before anything is deleted.
    ///
    /// - Parameters:
    ///   - mode: The mode to delete. The caller still ensures one mode remains.
    ///   - appState: Moves its selection to the first remaining mode when the
    ///     deleted mode was selected. `nil` deletes without touching selection.
    ///   - settingsManager: Drops the deleted mode's `defaultModelByMode` entry.
    @MainActor
    func deleteModeAndReconcileSelection(
        _ mode: Mode,
        appState: AppState?,
        settingsManager: SettingsManager?
    ) {
        // Read BEFORE the delete — see the note above.
        let deletedModeId = mode.id?.uuidString

        deleteMode(mode)

        guard let deletedModeId else { return }

        // Guarded: `defaultModelByMode`'s didSet rewrites UserDefaults on every
        // mutation, even a removal of a key that is not there.
        if let settingsManager, settingsManager.defaultModelByMode[deletedModeId] != nil {
            settingsManager.defaultModelByMode.removeValue(forKey: deletedModeId)
        }

        // Fetch only when the deleted mode was the selected one; any other
        // delete would sort-fetch every mode just to throw the result away.
        // `reconcileSelectionAfterDeletingMode` keeps its own guard.
        if let appState, appState.selectedModeId == deletedModeId {
            appState.reconcileSelectionAfterDeletingMode(
                id: deletedModeId,
                remainingModes: fetchAllModes()
            )
        }
    }

    /// Make exactly one mode the default again, if something left the store with
    /// two or with none.
    ///
    /// Every write above keeps the invariant, so this is for the rows nothing
    /// here wrote: a backup restored from another machine, and a store that was
    /// already broken before this shipped. Idempotent, and it saves nothing when
    /// there is nothing to repair.
    @discardableResult
    func enforceDefaultModeInvariant() -> Bool {
        guard DefaultModePolicy.apply(to: fetchAllModes()) else { return false }
        save()
        AppLogger.coreData.info(
            "Repaired the default-mode flag · exactly one mode is the default again"
        )
        return true
    }
}
