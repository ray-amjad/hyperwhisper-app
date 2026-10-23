//
//  PersistenceController+BackupImport.swift
//  hyperwhisper
//
//  Mode name repair, mode delete and the backup store import that applies
//  modes and vocabulary. Split out of PersistenceController.swift (#2383);
//  every body moved unchanged.
//

import CoreData
import Foundation

extension PersistenceController {

    /// Repairs names written by older builds before the storage invariant existed.
    /// The pass runs every launch, saves only when needed, and assigns deterministic
    /// suffixes when canonicalization exposes an existing case-insensitive conflict.
    @discardableResult
    func repairModeNames() -> Int {
        let modes = fetchAllModes().sorted {
            let lhsNeedsRepair = ModeNamePolicy.normalized($0.name ?? "") != $0.name
            let rhsNeedsRepair = ModeNamePolicy.normalized($1.name ?? "") != $1.name
            if lhsNeedsRepair != rhsNeedsRepair { return !lhsNeedsRepair }
            let lhs = $0.createdDate ?? .distantPast
            let rhs = $1.createdDate ?? .distantPast
            if lhs != rhs { return lhs < rhs }
            return ($0.id?.uuidString ?? "") < ($1.id?.uuidString ?? "")
        }
        var occupied = Set<String>()
        var repaired = 0

        for mode in modes {
            let base = ModeNamePolicy.storageName(mode.name ?? "", replacing: nil)
            let finalName = Self.uniqueModeName(base, occupiedKeys: occupied)
            if mode.name != finalName {
                mode.name = finalName
                mode.modifiedDate = Date()
                repaired += 1
            }
            if let key = ModeNamePolicy.comparisonKey(finalName) {
                occupied.insert(key)
            }
        }

        if repaired > 0 { save() }
        return repaired
    }

    private static func uniqueModeName(_ base: String, occupiedKeys: Set<String>) -> String {
        guard let baseKey = ModeNamePolicy.comparisonKey(base), occupiedKeys.contains(baseKey) else {
            return base
        }

        var suffix = 2
        while true {
            let candidate = "\(base) \(suffix)"
            if let key = ModeNamePolicy.comparisonKey(candidate), !occupiedKeys.contains(key) {
                return candidate
            }
            suffix += 1
        }
    }

    /// Deletes a mode
    /// - Parameter mode: The mode to delete
    /// - Note: The caller is responsible for ensuring at least one mode remains
    func deleteMode(_ mode: Mode) {
        deleteModeWithoutSaving(mode)
        save()
    }

    /// `deleteMode` without its save: the backup import (issue #1613) deletes
    /// inside one transaction and saves once at the end.
    private func deleteModeWithoutSaving(_ mode: Mode) {
        let context = container.viewContext
        let wasDefault = mode.isDefault
        context.delete(mode)
        // Deleting the default used to leave none, and the app then merely
        // BEHAVED as if the lowest-sortOrder mode were one — with an editable
        // name and no hint (issue #536). Move the flag for real.
        if wasDefault {
            DefaultModePolicy.apply(to: fetchAllModes())
        }
    }

    /// Imports a backup's modes and vocabulary, each section as ONE store
    /// transaction (issue #1613).
    ///
    /// Every row used to be saved on its own (`createOrUpdateMode`,
    /// `deleteMode`, `addVocabularyItem` and the default-flag pass each
    /// saved), so a crash, a kill or a failed save part-way left a silent
    /// partial import. Now each section's rows are written to `viewContext`
    /// with no save, and one `save()` commits that section. A failed save
    /// rolls the context back and throws, so nothing half-applied stays
    /// pending (the plain `save()` only logs, and its rejected row would
    /// poison every later save) and the caller can tell the user exactly
    /// which section failed.
    ///
    /// Why one save per section, not one for both: `Mode` lives in the
    /// `Local` store and `Vocabulary` in the `Cloud` one. A single save writes
    /// each store in its own SQLite transaction, with no two-phase commit, so
    /// it could commit the modes, fail the vocabulary, and a rollback could
    /// not undo the committed modes. Saving the sections one after the other
    /// makes each outcome known: modes first (with the default-mode repair in
    /// the same save), then the vocabulary only if the modes committed.
    ///
    /// Pass `nil` for a section the user did not select. An empty array still
    /// runs that section (for modes, the default-mode repair).
    ///
    /// - Throws: `BackupStoreImportError`, after the rollback. Changes the
    ///   context held BEFORE the import are saved first so the rollback cannot
    ///   discard them; if that save fails, this throws (as a failure of the
    ///   first selected section) before importing anything.
    @MainActor
    func importBackupStore(
        modes backupModes: [BackupMode]?,
        modeResolution: ModeConflictResolution,
        vocabulary backupItems: [BackupVocabularyItem]?,
        vocabularyResolution: VocabularyConflictResolution
    ) throws -> BackupStoreImportResult {
        let context = container.viewContext
        let saveSite = "backup_import"

        // The rollback below must undo the import and nothing else, so commit
        // whatever was pending before it starts.
        if context.hasChanges {
            do {
                try saveBackupImportChanges(context: context)
            } catch {
                AppLogger.logCoreData(
                    .save(site: saveSite, contextKey: CoreDataSaveDiagnostics.viewContextKey),
                    error: error as NSError,
                    metadata: CoreDataSaveDiagnostics.contextShape(context)
                )
                throw BackupStoreImportError(
                    failedSection: backupModes != nil ? .modes : .vocabulary,
                    committed: BackupStoreImportResult(),
                    underlying: error
                )
            }
        }

        var result = BackupStoreImportResult()

        if let backupModes {
            let modes = applyImportedModes(backupModes, resolution: modeResolution)
            do {
                try saveBackupImportSection(context: context, saveSite: saveSite)
            } catch {
                AppLogger.coreData.error("Backup import: the modes save failed and was rolled back · no mode or vocabulary row was changed")
                throw BackupStoreImportError(failedSection: .modes, committed: BackupStoreImportResult(), underlying: error)
            }
            result.modesImported = modes.imported
            result.modesSkipped = modes.skipped
            result.modeIdRemap = modes.idRemap
            AppLogger.coreData.info("Backup import: modes committed · \(modes.imported) imported, \(modes.skipped) skipped")
        }

        if let backupItems {
            let vocabulary = applyImportedVocabulary(backupItems, resolution: vocabularyResolution)
            do {
                try saveBackupImportSection(context: context, saveSite: saveSite)
            } catch {
                AppLogger.coreData.error("Backup import: the vocabulary save failed and was rolled back · no vocabulary row was changed")
                throw BackupStoreImportError(failedSection: .vocabulary, committed: result, underlying: error)
            }
            result.vocabularyImported = vocabulary.imported
            result.vocabularySkipped = vocabulary.skipped
            AppLogger.coreData.info("Backup import: vocabulary committed · \(vocabulary.imported) imported, \(vocabulary.skipped) skipped")
        }

        return result
    }

    /// Saves one staged backup-import section. A failed save rolls the whole
    /// context back (the section's staged rows and nothing else: the import
    /// saved everything else first) and rethrows.
    @MainActor
    private func saveBackupImportSection(context: NSManagedObjectContext, saveSite: String) throws {
        do {
            if context.hasChanges {
                try saveBackupImportChanges(context: context)
            }
            AppLogger.logCoreData(.save(site: saveSite, contextKey: CoreDataSaveDiagnostics.viewContextKey))
        } catch {
            // Read the shape BEFORE the rollback empties the pending sets.
            let shape = CoreDataSaveDiagnostics.contextShape(context)
            context.rollback()
            AppLogger.logCoreData(
                .save(site: saveSite, contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: error as NSError,
                metadata: shape
            )
            throw error
        }
    }

    /// Saves a backup import's changes in a bounded time, or throws (#1626).
    ///
    /// A plain `context.save()` has two ways to never come back when SQLite
    /// itself refuses the write:
    /// - Busy lock: Core Data waits for another process's write lock with no
    ///   limit. Neither `NSPersistentStoreTimeoutOption` nor a `busy_timeout`
    ///   pragma bounds that wait (measured on macOS 26). So before each
    ///   `context.save()` take the write lock on every store the save writes,
    ///   on our own connections within one 10 s deadline, hold them all until
    ///   the line before the save, and let them go together (Core Data's own
    ///   connection cannot take a lock ours hold, so they cannot be held
    ///   through it). A lock another process takes in the short gap between
    ///   that release and Core Data's own BEGIN still waits. The gap cannot be
    ///   handed over either: on macOS 26 a raw `fcntl` lock this process takes
    ///   on the WAL-index write byte blocks Core Data's own save too, because
    ///   SQLite's locks there belong to its open file, not to the process.
    /// - Constraint: a refused statement (`SQLITE_CONSTRAINT`, e.g. a trigger)
    ///   reads to Core Data as an optimistic-locking failure. With a merging
    ///   policy it retries, then ends the process ("Unable to recover from
    ///   optimistic locking failure"). So save under `NSErrorMergePolicy`,
    ///   which throws the conflicts instead. Real conflicts (a row another
    ///   context changed since the import read it) are resolved ONCE with the
    ///   context's own policy and the save is tried again; a failure with no
    ///   conflict to resolve, or a second failure, is thrown.
    @MainActor
    private func saveBackupImportChanges(context: NSManagedObjectContext) throws {
        try Self.saveWithoutRetryLoop(
            context: context,
            lockingStoresAt: Self.storeFileURLsWritten(by: context),
            lockTimeout: Self.backupImportWriteLockTimeout
        )
    }

    /// The on-disk SQLite files the context's pending changes are written to.
    /// An in-memory (`/dev/null`) store has no file and no other writer.
    @MainActor
    private static func storeFileURLsWritten(by context: NSManagedObjectContext) -> [URL] {
        guard let coordinator = context.persistentStoreCoordinator else { return [] }
        let pending = context.insertedObjects.union(context.updatedObjects).union(context.deletedObjects)
        let entityNames = Set(pending.compactMap { $0.entity.name })
        return coordinator.persistentStores.compactMap { store in
            guard store.type == NSSQLiteStoreType,
                  let url = store.url,
                  url.isFileURL,
                  url.path != "/dev/null" else { return nil }
            // A store with no named configuration holds every entity.
            guard let entities = coordinator.managedObjectModel.entities(forConfigurationName: store.configurationName) else {
                return url
            }
            return entities.contains { entityNames.contains($0.name ?? "") } ? url : nil
        }
    }

    /// Writes backup modes into `viewContext` with conflict resolution. Saves
    /// NOTHING: `importBackupStore` commits or rolls back the whole section.
    ///
    /// CONFLICT RESOLUTION:
    /// - .skip: Don't import if mode with same name exists (case-insensitive)
    /// - .replace: Update the local counterpart in place (same id, else the
    ///   first same-name row; issue #1479) and delete any other same-name row
    /// - .keepBoth: Import as "Mode Name (imported)"
    ///
    /// - Parameters:
    ///   - backupModes: Array of BackupMode structs to import
    ///   - resolution: How to handle conflicts
    /// - Returns: Tuple with counts of imported and skipped modes plus any old-to-new IDs created by `.keepBoth`
    ///
    /// `@MainActor`: the entire import runs against `container.viewContext`, which is bound to
    /// the main queue. Pinning this method to the main actor enforces that queue confinement at
    /// compile time so no caller can reach the fetch/delete work off-queue (Core Data
    /// undefined behavior). Every fetch below includes the context's unsaved inserts and
    /// excludes its unsaved deletes, so later rows see earlier ones exactly as they did when
    /// each row was saved on its own.
    @MainActor
    private func applyImportedModes(_ backupModes: [BackupMode], resolution: ModeConflictResolution) -> (imported: Int, skipped: Int, idRemap: [UUID: UUID]) {
        var imported = 0
        var skipped = 0
        var idRemap: [UUID: UUID] = [:]

        for backupMode in backupModes {
            let normalizedBackupName = ModeNamePolicy.storageName(backupMode.name, replacing: nil)
            let normalizedKey = ModeNamePolicy.comparisonKey(normalizedBackupName)!
            let conflicts = fetchAllModes().filter {
                guard let name = $0.name else { return false }
                return ModeNamePolicy.comparisonKey(name) == normalizedKey
            }
            let hasConflict = !conflicts.isEmpty

            // #1481: a backup without enableScreenOCR / useStreamingTranscription
            // (one written before they were exported) keeps the value of the
            // local mode this row replaces or updates: the same id first, then
            // the first same-name row. That row is updated in place (issue
            // #1479); `.replace` deletes any other same-name row below. With no
            // such row (a new mode, or `.keepBoth`'s copy) the value is
            // `false`, which is what every restore wrote before.
            let localCounterpart: Mode? = (hasConflict && resolution == .keepBoth)
                ? nil
                : (fetchAllModes().first { $0.id == backupMode.id } ?? conflicts.first)
            let restoredScreenOCR = backupMode.enableScreenOCR ?? localCounterpart?.enableScreenOCR ?? false
            let restoredStreaming = backupMode.useStreamingTranscription
                ?? localCounterpart?.useStreamingTranscription ?? false

            if hasConflict {
                switch resolution {
                case .skip:
                    // Skip this mode - it already exists
                    skipped += 1
                    continue

                case .replace:
                    // Issue #1479: the local counterpart is updated in place
                    // below. Every OTHER same-name row is still deleted.
                    // Re-fetch on the next iteration so duplicate backup rows
                    // use last-one-wins semantics without retaining deleted
                    // objects.
                    for existingMode in conflicts where !existingMode.isDeleted && existingMode !== localCounterpart {
                        deleteModeWithoutSaving(existingMode)
                    }

                case .keepBoth:
                    // Will create with modified name below
                    break
                }
            }

            // Issue #1479: ONE in-place path for every row that has a local
            // counterpart (same id first, else — `.replace` only — the first
            // same-name row; `.keepBoth` with a name conflict has none and
            // makes a copy). The counterpart object is handed to
            // `createOrUpdateMode` as `updating:`, so the update never depends
            // on an `id ==` predicate fetch matching an id changed earlier in
            // this same unsaved import. The row keeps its object (Z_PK),
            // `sortOrder` and `createdDate`, so importing your own backup again
            // changes nothing; every other column ends as a fresh create from
            // this backup row would (`createOrUpdateMode` writes them all,
            // including a nil `foreignPlatformExtensions`), bar the #1481
            // fallbacks above.
            let inPlaceTarget: Mode? = (localCounterpart?.isDeleted == false) ? localCounterpart : nil
            if let keeper = inPlaceTarget {
                if keeper.id != backupMode.id {
                    // A same-name row with ANOTHER id is a different mode that
                    // happens to share the name, so it takes what a create
                    // gives: the backup's id (as the re-created row had) and
                    // `isSystemProvided == false`. Only a row whose id IS the
                    // backup row's keeps the seeded flag; otherwise a user mode
                    // named like the seeded one would inherit it.
                    keeper.id = backupMode.id
                    keeper.isSystemProvided = false
                }
                if resolution == .replace {
                    // A create starts with `isDefault == false` and is promoted
                    // only by the `DefaultModePolicy.apply` below when the
                    // backup row is the default, or by the final repair. The
                    // flag is cleared the same way whether the counterpart was
                    // found by id or by name, so which local row ends as the
                    // default never depends on a row's name. `.skip` and
                    // `.keepBoth` keep the local flag, as before #1479: they
                    // only reach here for a same-id row with no name conflict,
                    // and they never promised to restore the backup's default.
                    keeper.isDefault = false
                }
            }

            // Determine the name to use
            let finalName: String
            let finalId: UUID
            if hasConflict && resolution == .keepBoth {
                let occupied = Set(fetchAllModes().compactMap { mode in
                    mode.name.flatMap(ModeNamePolicy.comparisonKey)
                })
                finalName = Self.uniqueModeName(
                    "\(normalizedBackupName) (imported)",
                    occupiedKeys: occupied
                )
                finalId = UUID()  // New ID for duplicate
                idRemap[backupMode.id] = finalId
            } else {
                finalName = normalizedBackupName
                finalId = backupMode.id
            }

            // Normalize legacy cloudProvider values so older backups land on the
            // current HyperWhisper Cloud accuracy tier setup.
            let normalized = CloudSTTCatalog.shared.normalizeCloudProvider(backupMode.cloudProvider)

            // Create the mode using existing method
            createOrUpdateMode(
                id: finalId,
                name: finalName,
                preset: backupMode.preset,
                language: backupMode.language,
                model: backupMode.model,
                punctuation: backupMode.punctuation,
                capitalization: backupMode.capitalization,
                profanityFilter: backupMode.profanityFilter,
                customInstructions: backupMode.customInstructions,
                languageModel: backupMode.languageModel,
                cloudProvider: normalized.provider,
                // Resolve legacy model IDs on restore, dispatched by provider, so older
                // backups map onto the current lineup regardless of which provider
                // retired the ID — e.g. AssemblyAI Universal-2 / Universal-3 Pro aliases,
                // removed Deepgram IDs collapsing onto Nova 3 General, or ElevenLabs
                // `scribe_v1` migrating to `scribe_v2`.
                cloudTranscriptionModel: backupMode.cloudTranscriptionModel.map {
                    CloudTranscriptionModels.resolveModelAlias(
                        $0,
                        provider: CloudProvider.parse(backupMode.cloudProvider)
                    )
                },
                postProcessingMode: backupMode.postProcessingMode,
                postProcessingProvider: backupMode.postProcessingProvider,
                // A backup with no spelling is restored with no spelling, NOT with
                // this machine's region default. Windows keeps the null too, so a
                // backup restores the same way on both platforms.
                englishSpelling: backupMode.englishSpelling ?? "",
                userSystemPrompt: backupMode.userSystemPrompt,
                useStreamingTranscription: restoredStreaming,
                cloudAccuracyTier: normalized.accuracyTier
                    ?? CloudAccuracyTier.fromStorageValue(backupMode.cloudAccuracyTier).rawValue,
                removeTrailingPeriod: backupMode.removeTrailingPeriod ?? false,
                enableScreenOCR: restoredScreenOCR,
                geminiCustomPrompt: backupMode.geminiCustomPrompt,
                cloudPostProcessingModel: backupMode.cloudPostProcessingModel,
                cloudTranscriptionDomain: backupMode.cloudTranscriptionDomain,
                foreignPlatformExtensions: backupMode.foreignPlatformExtensions,
                // One save for the whole import (issue #1613).
                persist: false,
                restoringFromBackup: true,
                updating: inPlaceTarget
            )

            // Update isDefault flag if this mode should be default
            // Only set as default if the original was default AND we're not in keepBoth mode
            if backupMode.isDefault && !(hasConflict && resolution == .keepBoth) {
                // Clear existing default(s) first. Re-fetch here rather than reusing
                // `existingModes`: under `.replace`, the delete above removes managed
                // objects on earlier iterations, so that captured array can hold
                // deleted objects (reading/writing them is a Core Data
                // use-after-delete). The fresh fetch never returns deleted objects;
                // `!mode.isDeleted` guards any in-flight, unsaved delete.
                // One decision, three heads (issue #536): the flag moves onto the
                // imported mode and off every other row in the same pass.
                DefaultModePolicy.apply(to: fetchAllModes(), preferred: finalId)
            }

            imported += 1
        }

        // A backup whose modes carry no `isDefault` at all — one written before
        // the field existed, or one whose default row was skipped as a conflict
        // — used to restore a store with NO default mode (issue #536). Nothing
        // repaired it: `initializeDefaultModes()` returns early whenever any
        // mode exists. The same repair as `enforceDefaultModeInvariant()`,
        // without its save.
        if DefaultModePolicy.apply(to: fetchAllModes()) {
            AppLogger.coreData.info(
                "Repaired the default-mode flag · exactly one mode is the default again"
            )
        }

        AppLogger.coreData.info("Mode import staged: \(imported) imported, \(skipped) skipped")
        return (imported, skipped, idRemap)
    }

    /// Writes backup vocabulary into `viewContext` with conflict resolution.
    /// Saves NOTHING: `importBackupStore` commits or rolls back the whole section.
    ///
    /// CONFLICT RESOLUTION:
    /// - .skip: Don't import if word already exists (case-insensitive)
    /// - .replace: Update existing item's replacement text
    ///
    /// - Parameters:
    ///   - backupItems: Array of BackupVocabularyItem structs to import
    ///   - resolution: How to handle conflicts
    /// - Returns: Tuple with counts of imported and skipped items
    ///
    /// `@MainActor`: like `applyImportedModes`, all work runs against `container.viewContext` (main
    /// queue). Pinning to the main actor enforces that queue confinement at compile time.
    @MainActor
    private func applyImportedVocabulary(_ backupItems: [BackupVocabularyItem], resolution: VocabularyConflictResolution) -> (imported: Int, skipped: Int) {
        let context = container.viewContext
        var imported = 0
        var skipped = 0
        // Highest sortOrder so far, read once and then advanced per insert —
        // the same "add to end" order `addVocabularyItem` gives each row.
        var maxSortOrder: Int16?

        // A backup made before #1475 can carry a word with line breaks inside it. It
        // imports as one word per non-empty line, the way the add field saves a paste
        // (#1777); each line carries the item's replacement and source. A blank word
        // stays one empty entry, so it is still counted as skipped below.
        let entries = backupItems.flatMap { item -> [(String, BackupVocabularyItem)] in
            let lines = Self.vocabularyWordLines(from: item.word)
            return lines.isEmpty ? [("", item)] : lines.map { ($0, item) }
        }

        for (normalizedWord, item) in entries {
            // Skip empty/whitespace-only words. An empty word would later compile
            // to the regex "\b\b" in VocabularyProcessor, matching every word
            // boundary and corrupting the entire transcript.
            guard !normalizedWord.isEmpty else {
                skipped += 1
                continue
            }

            // Check for an existing item. A FETCH, not `vocabularyItemExists`'s
            // count: nothing is saved until the end now, and a fetch evaluates
            // the context's unsaved inserts too, so a word repeated in the same
            // backup still finds the row an earlier entry staged.
            let exists = importedVocabularyItemExists(word: normalizedWord)

            if exists {
                switch resolution {
                case .skip:
                    // Skip this item - it already exists
                    skipped += 1
                    continue

                case .replace:
                    // Replace in place so a failed add cannot delete the user's existing
                    // vocabulary entry before recreating it.
                    let existingItems = fetchAllVocabularyItems()
                    if let existing = existingItems.first(where: { $0.word?.lowercased() == normalizedWord.lowercased() }) {
                        let normalizedReplacement = item.replacement?.trimmingCharacters(in: .whitespacesAndNewlines)
                        existing.word = normalizedWord
                        existing.replacement = normalizedReplacement?.isEmpty == true ? nil : normalizedReplacement
                        existing.setValue(item.source, forKey: "source")
                        imported += 1
                        continue
                    }
                    // A case-insensitive match the lowercase compare above missed.
                    // `addVocabularyItem` refused these as duplicates, so they
                    // stay skipped.
                    skipped += 1
                    continue
                }
            }

            // Add the vocabulary item: `addVocabularyItem`'s insert, without its save.
            let normalizedReplacement = item.replacement?.trimmingCharacters(in: .whitespacesAndNewlines)
            let vocabItem = Vocabulary(context: context)
            vocabItem.id = UUID()
            vocabItem.word = normalizedWord
            vocabItem.replacement = normalizedReplacement?.isEmpty == true ? nil : normalizedReplacement
            vocabItem.setValue(item.source, forKey: "source")
            vocabItem.createdDate = Date()

            let currentMax = maxSortOrder ?? (fetchAllVocabularyItems()
                .filter { $0 !== vocabItem }
                .map { $0.sortOrder }
                .max() ?? 0)
            // Int16: clamp at .max (as the mode path does) rather than trap on overflow.
            vocabItem.sortOrder = currentMax == .max ? .max : currentMax + 1
            maxSortOrder = vocabItem.sortOrder
            imported += 1
        }

        AppLogger.coreData.info("Vocabulary import staged: \(imported) imported, \(skipped) skipped")
        return (imported, skipped)
    }

    /// Whether `word` (trimmed, case-insensitive) is already in the vocabulary,
    /// counting rows this import staged but has not saved yet.
    private func importedVocabularyItemExists(word: String) -> Bool {
        let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
        request.predicate = NSPredicate(format: "word ==[c] %@", word)
        request.includesPendingChanges = true

        do {
            let matches = try container.viewContext.fetch(request)
            return !matches.isEmpty
        } catch {
            AppLogger.coreData.error("Failed to check vocabulary existence: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to check vocabulary existence", tags: ["component": "PersistenceController", "operation": "importedVocabularyItemExists"])
            return false
        }
    }
}
