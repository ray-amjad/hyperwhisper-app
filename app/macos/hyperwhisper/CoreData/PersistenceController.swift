//
//  PersistenceController.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  PERSISTENCE CONTROLLER
//  Manages the Core Data stack for the application.
//  Provides a shared instance for accessing the managed object context.
//
//  Features:
//  - Singleton pattern for app-wide access
//  - Preview context for SwiftUI previews
//  - Automatic saving on app termination
//  - Error handling for data operations
//

import CoreData
import Foundation
import SQLite3

// MARK: - Persistence Controller

/// Manages the Core Data stack and provides access to the managed object context
class PersistenceController: ObservableObject {
    
    // MARK: - Shared Instance
    
    /// Singleton instance for app-wide access
    static let shared = PersistenceController()

    /// The one `HyperWhisper` model every container in this process uses (#1653).
    ///
    /// `NSPersistentContainer(name:)` loads a NEW model instance each time it is
    /// called. With two or more loaded, `+[Transcript entity]` cannot tell them
    /// apart, so `Transcript(context:)` can take another model's entity: the
    /// insert then has no store, `obtainPermanentIDs` throws and the write
    /// saves nothing. The unit tests build many in-memory controllers at once
    /// and hit this; one shared model removes the ambiguity.
    ///
    /// Build every `HyperWhisper` container on this model, never by name. A
    /// missing model stops the app here, as a failed store load already does.
    static let managedObjectModel: NSManagedObjectModel = {
        guard let url = Bundle.main.url(forResource: "HyperWhisper", withExtension: "momd"),
              let model = NSManagedObjectModel(contentsOf: url) else {
            fatalError("Core Data model HyperWhisper.momd is missing from the app bundle")
        }
        return model
    }()

    /// True when THIS launch seeded the default modes — i.e. a genuine fresh
    /// install (no modes existed yet). Used to gate first-run onboarding so
    /// existing users (who already have modes) are never re-onboarded when the
    /// `hasCompletedOnboarding` default flips.
    private(set) var didSeedDefaultModesOnLaunch = false

    // MARK: - Preview Support
    
    /// In-memory store for SwiftUI previews
    static var preview: PersistenceController = {
        let controller = PersistenceController(inMemory: true)
        let viewContext = controller.container.viewContext
        
        // Create sample data for previews
        for i in 0..<10 {
            let transcript = Transcript(context: viewContext)
            transcript.id = UUID()
            transcript.text = "Sample transcript \(i + 1). This is a test transcript for preview purposes."
            transcript.date = Date().addingTimeInterval(TimeInterval(-i * 3600))
            transcript.duration = Double.random(in: 5...120)
            transcript.mode = ["Default", "Meeting", "Note", "Email"].randomElement()
        }
        
        do {
            try viewContext.save()
        } catch {
            // Handle error in production app
            AppLogger.coreData.error("Failed to save preview data: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to save preview data", tags: ["component": "PersistenceController", "operation": "previewSave"])
        }
        
        return controller
    }()
    
    // MARK: - Core Data Stack

    /// The persistent container that manages the Core Data stack.
    ///
    /// This is a `NSPersistentCloudKitContainer` so the Vocabulary store can mirror
    /// to iCloud, but it hosts TWO stores:
    ///   - `HyperWhisper.sqlite` (Local configuration): Transcript, Mode, RecordingSession,
    ///     UsageTracking. Device-specific, never synced.
    ///   - `HyperWhisper-Cloud.sqlite` (Cloud configuration): Vocabulary only. Mirrored to
    ///     iCloud via `NSPersistentCloudKitContainerOptions`.
    ///
    /// Typed as the base class so existing call sites keep working unchanged.
    let container: NSPersistentContainer

    // MARK: - Store configuration constants

    /// Name of the Core Data configuration containing device-local entities.
    /// Must match the configuration name in HyperWhisper.xcdatamodeld.
    private static let localConfigurationName = "Local"

    /// Name of the Core Data configuration containing CloudKit-synced entities (Vocabulary).
    /// Must match the configuration name in HyperWhisper.xcdatamodeld.
    private static let cloudConfigurationName = "Cloud"

    /// CloudKit container identifier. Must match the entry in the app entitlements and
    /// the container registered in the Apple Developer Portal.
    private static let cloudKitContainerIdentifier = "iCloud.com.hyperwhisper.hyperwhisper"

    /// `UserDefaults` flag that records whether the one-time vocabulary migration from
    /// the legacy unified store to the split cloud store has completed.
    private static let vocabularyMigrationDefaultsKey = "didMigrateVocabularyToCloudStore"

    /// UserDefaults flag for normalizing stored HyperWhisper Cloud route identifiers.
    private static let cloudRouteIdentifierMigrationDefaultsKey = "didNormalizeCloudRouteIdentifiersV1"

    /// UserDefaults flag for one-shot migration that rewrites removed Deepgram model IDs
    /// (Nova 1, Nova-2 domain splits, Enhanced/Base, hosted Whisper) onto `nova-3-general`.
    private static let removedDeepgramModelsMigrationDefaultsKey = "didMigrateRemovedDeepgramModelsV1"

    /// UserDefaults flag for retrying the corrected `defaultModelByMode` portion of
    /// the removed Deepgram model migration after V1 read the JSON blob as a dictionary.
    private static let removedDeepgramDefaultModelByModeMigrationDefaultsKey = "didMigrateRemovedDeepgramDefaultModelByModeV2"

    /// UserDefaults flag for one-shot migration that folds legacy standalone
    /// cloudProvider values (`microsoftazurespeech`, `googlespeech`) onto
    /// `hyperwhisper` + the matching accuracy tier. Catalog-driven.
    private static let cloudProviderMigrationDefaultsKey = "didNormalizeCloudProviderValuesV1"

    /// UserDefaults flag for the one-shot migration that rewrites the retired
    /// `googleChirp3` accuracy tier (and its aliases) onto `geminiTranscribe`.
    ///
    /// This needs its OWN key. `didNormalizeCloudProviderValuesV1` is already
    /// `true` for every existing user, so reusing it would never re-run and the
    /// retired value would only ever be rescued lazily at read time — by
    /// `CloudAccuracyTier.fromStorageValue`, whose fallback on any miss is
    /// `.deepgramNova3`. One unrescued read (the Local API PATCH path writes the
    /// raw string straight through, for instance) and the user is silently moved
    /// off Google.
    private static let googleChirp3TierMigrationDefaultsKey = "didMigrateGoogleChirp3TierToGeminiTranscribeV1"

    /// UserDefaults flag controlling whether the Cloud-configuration store mirrors
    /// to CloudKit. Default: false (off). User-facing toggle lives in VocabularyView.
    /// When false, the cloud store loads as a plain local SQLite file — vocabulary
    /// still works, it just doesn't leave the device.
    static let vocabularyCloudSyncEnabledDefaultsKey = "vocabularyCloudSyncEnabled"

    // MARK: - Initialization

    /// Initializes the persistence controller
    /// - Parameter inMemory: If true, uses an in-memory store (for testing/previews)
    init(inMemory: Bool = false) {
        // Use NSPersistentCloudKitContainer so the Cloud store can mirror to iCloud.
        // The Local store is configured without `cloudKitContainerOptions` and stays on disk only.
        container = NSPersistentCloudKitContainer(name: "HyperWhisper", managedObjectModel: Self.managedObjectModel)

        if inMemory {
            // Single in-memory store for tests/previews. CloudKit mirroring is disabled.
            // `/dev/null` is the standard Core Data trick to get an ephemeral SQLite store.
            container.persistentStoreDescriptions.first?.url = URL(fileURLWithPath: "/dev/null")
            container.persistentStoreDescriptions.first?.configuration = nil
        } else {
            // One-time migration BEFORE loading the main split container:
            // if the legacy unified HyperWhisper.sqlite still holds Vocabulary rows,
            // COPY them out to the new cloud store URL. We have to do this before
            // the main container opens the legacy file as the Local-only store,
            // because once that happens Core Data stops exposing Vocabulary rows
            // through it (they'd become orphan SQLite rows invisible to the API).
            //
            // NOTE: this migration is copy-only — the legacy rows are left in place
            // as a dormant backup. A separate cleanup task scheduled for a future
            // release (`tasks/macos/to-do/vocabulary-legacy-cleanup.md`) will reclaim
            // the disk space once iCloud sync has been stable in production for long
            // enough to trust it.
            Self.migrateLegacyVocabularyIfNeeded()

            let localStoreURL = NSPersistentContainer.defaultDirectoryURL()
                .appendingPathComponent("HyperWhisper.sqlite")
            let cloudStoreURL = NSPersistentContainer.defaultDirectoryURL()
                .appendingPathComponent("HyperWhisper-Cloud.sqlite")

            let localDescription = NSPersistentStoreDescription(url: localStoreURL)
            localDescription.configuration = Self.localConfigurationName
            localDescription.setOption(true as NSNumber, forKey: NSMigratePersistentStoresAutomaticallyOption)
            localDescription.setOption(true as NSNumber, forKey: NSInferMappingModelAutomaticallyOption)
            localDescription.setOption(true as NSNumber, forKey: NSPersistentHistoryTrackingKey)
            localDescription.setOption(true as NSNumber, forKey: NSPersistentStoreRemoteChangeNotificationPostOptionKey)
            // cloudKitContainerOptions stays nil — this store is NEVER mirrored.

            let cloudDescription = NSPersistentStoreDescription(url: cloudStoreURL)
            cloudDescription.configuration = Self.cloudConfigurationName
            cloudDescription.setOption(true as NSNumber, forKey: NSMigratePersistentStoresAutomaticallyOption)
            cloudDescription.setOption(true as NSNumber, forKey: NSInferMappingModelAutomaticallyOption)
            // NSPersistentCloudKitContainer REQUIRES history tracking and remote-change notifications
            // on any CloudKit-mirrored store. Without these two options, loadPersistentStores will fail.
            cloudDescription.setOption(true as NSNumber, forKey: NSPersistentHistoryTrackingKey)
            cloudDescription.setOption(true as NSNumber, forKey: NSPersistentStoreRemoteChangeNotificationPostOptionKey)

            // GATE: only mirror to CloudKit when the user has explicitly opted in
            // via the VocabularyView toggle. Default (false) means the cloud store
            // loads as a plain local SQLite file — vocabulary still works, it just
            // doesn't leave the device. History tracking + remote-change options stay
            // on regardless so flipping the toggle later requires no store reshuffle.
            if UserDefaults.standard.bool(forKey: Self.vocabularyCloudSyncEnabledDefaultsKey) {
                cloudDescription.cloudKitContainerOptions = NSPersistentCloudKitContainerOptions(
                    containerIdentifier: Self.cloudKitContainerIdentifier
                )
                AppLogger.coreData.info("iCloud vocabulary sync enabled — CloudKit mirror attached to cloud store")
            } else {
                AppLogger.coreData.info("iCloud vocabulary sync disabled — cloud store will run as local-only SQLite")
            }

            container.persistentStoreDescriptions = [localDescription, cloudDescription]
        }

        // Load the persistent stores
        container.loadPersistentStores { storeDescription, error in
            if let error = error as NSError? {
                // Log critical error before crashing
                AppLogger.logCoreData(.storeLoad, error: error)
                // PRIVACY: `error.userInfo` was stringified straight into a
                // Sentry extra, and `NSFilePathErrorKey` / the store URL inside
                // it is `/Users/<account name>/Library/…`. `beforeSend` matches
                // extra KEYS, not values, so "userInfo" sailed through it. The
                // codes and names below are the diagnostic part of that dump.
                var extras = CoreDataSaveDiagnostics.metadata(for: error)
                extras["store"] = storeDescription.configuration ?? "unknown"
                // Send critical error to Sentry before crashing
                SentryService.capture(error: error, message: "Critical: Core Data failed to load", extras: extras, tags: ["component": "PersistenceController", "operation": "loadPersistentStores", "severity": "fatal"], includeRecentLogs: false)
                // In production, this should be handled more gracefully
                // Consider showing an alert to the user or attempting recovery
                //
                // The crash message carried the same path. A `fatalError` string
                // is read back out of the crash report, so it is a log line too.
                fatalError("Core Data failed to load: \(CoreDataSaveDiagnostics.summary(for: error))")
            }
        }

        // Configure the view context
        container.viewContext.automaticallyMergesChangesFromParent = true

        // Set merge policy to handle conflicts.
        // For CloudKit-mirrored stores this gives us last-writer-wins on vocabulary.
        // Duplicates that arise from concurrent offline
        // edits (no unique constraints allowed with CloudKit) are handled by a dedup
        // pass in VocabularyView.
        container.viewContext.mergePolicy = NSMergeByPropertyObjectTrumpMergePolicy

        // Eagerly create the serial writer context here (not lazily) so exactly one
        // writer ever exists — a `lazy var` first-touch from two threads can build
        // two contexts and break the serial-write guarantee.
        let writer = container.newBackgroundContext()
        writer.name = "HyperWhisper.writer"
        // Store-trump (NOT object-trump): user actions on the viewContext win
        // over background flow writes. If a HistoryView delete lands between the
        // writer's fetch and its save, the writer's update is discarded instead
        // of resurrecting the deleted row. Inserts are unaffected, and all writer
        // updates already guard "row not found — skipping".
        writer.mergePolicy = NSMergeByPropertyStoreTrumpMergePolicy
        writer.automaticallyMergesChangesFromParent = true
        writer.undoManager = nil
        writerContext = writer

        // Initialize default modes on first launch
        if !inMemory {
            initializeDefaultModes()
            normalizeCloudRouteIdentifiersIfNeeded()
            migrateRemovedDeepgramModelsIfNeeded()
            normalizeCloudProviderIfNeeded()
            // AFTER normalizeCloudProviderIfNeeded: on a user who has never run
            // that migration it can itself write a tier from a legacy
            // `cloudProvider`, and the catalog now maps `googlespeech` to
            // `geminiTranscribe`, so running this first would leave nothing to do
            // and then let the older migration write the value again.
            migrateGoogleChirp3TierIfNeeded()
            // BEFORE repairModeNamesOnLaunch: that pass renames a duplicate
            // name with a " 2" suffix, and a leaked row is gone sooner if it
            // is never renamed in the first place (issue #1509).
            purgeLeakedLocalAPITransientModesOnLaunch()
            repairModeNamesOnLaunch()
            repairBrokenLocalModesOnLaunch()
            repairStaleProcessingTranscriptsOnLaunch()
        }
    }

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

    private func purgeLeakedLocalAPITransientModesOnLaunch() {
        let purged = purgeLeakedLocalAPITransientModes()
        if purged > 0 {
            AppLogger.coreData.info("Launch repair deleted \(purged, privacy: .public) leaked Local API transient mode(s)")
        }
    }

    private func repairModeNamesOnLaunch() {
        let repaired = repairModeNames()
        if repaired > 0 {
            AppLogger.coreData.info("Launch repair normalized \(repaired, privacy: .public) mode name(s)")
        }
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

    // MARK: - Legacy Vocabulary Migration

    /// Snapshot of a single vocabulary row — used to ferry rows between the legacy
    /// unified store and the new split cloud store without dragging managed objects
    /// across container boundaries.
    private struct VocabularySnapshot {
        let id: UUID
        let word: String
        let replacement: String?
        let createdDate: Date
        let sortOrder: Int16
    }

    /// One-time migration that COPIES Vocabulary rows out of the legacy unified
    /// `HyperWhisper.sqlite` store and into the new `HyperWhisper-Cloud.sqlite` store.
    ///
    /// This runs BEFORE the main split container loads, in two phases:
    ///
    /// **Phase A (read-only):** open a temporary plain `NSPersistentContainer`
    /// against the legacy file using the EXPLICIT v24 managed object model loaded from
    /// `HyperWhisper.momd/HyperWhisper_v24.mom`. v24 has no named configurations, so
    /// the default (nil) configuration contains every entity including `Vocabulary`.
    /// Using the v24 model explicitly means we never trigger lightweight migration on
    /// the legacy file — it stays byte-identical on disk — and we don't have to guess
    /// about configuration semantics in v25.
    ///
    /// **Phase B (write):** open a second temporary plain `NSPersistentContainer`
    /// against the new cloud store URL using the v25 model with `configuration = "Cloud"`,
    /// insert copies of the snapshots, save, remove stores.
    ///
    /// The legacy rows are DELIBERATELY NOT deleted. They remain in `HyperWhisper.sqlite`
    /// as a dormant backup so that if anything goes wrong with CloudKit sync for a user,
    /// their vocabulary is recoverable from the legacy file on disk. A separate cleanup
    /// task scheduled for a future release will reclaim that space once sync has been
    /// proven stable in production (see `tasks/macos/to-do/vocabulary-legacy-cleanup.md`).
    ///
    /// CloudKit mirroring is NOT enabled during migration — this is a local file-to-file
    /// transfer. CloudKit takes over on first real launch of the main container; it will
    /// push the copied rows up the first time the user is signed into iCloud.
    ///
    /// Idempotent via `UserDefaults`. If Phase B fails the flag is NOT set, so the next
    /// launch retries. Because there is no delete step, a retry can re-insert snapshots
    /// that were already written on a previous partially-successful run — the dedup pass
    /// in `VocabularyView` collapses any duplicates that result.
    private static func migrateLegacyVocabularyIfNeeded() {
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: vocabularyMigrationDefaultsKey) {
            return
        }

        let legacyURL = NSPersistentContainer.defaultDirectoryURL()
            .appendingPathComponent("HyperWhisper.sqlite")
        let cloudURL = NSPersistentContainer.defaultDirectoryURL()
            .appendingPathComponent("HyperWhisper-Cloud.sqlite")

        // If the legacy file doesn't exist, this is a fresh install — nothing to migrate.
        // Mark complete so we never try again.
        guard FileManager.default.fileExists(atPath: legacyURL.path) else {
            defaults.set(true, forKey: vocabularyMigrationDefaultsKey)
            AppLogger.coreData.info("Vocabulary migration skipped — fresh install, no legacy store at \(legacyURL.path, privacy: .public)")
            return
        }

        AppLogger.coreData.info("Starting one-time vocabulary migration (copy-only) from legacy store to cloud store")

        // --- Phase A: read vocabulary snapshots out of the legacy store ---
        let snapshots: [VocabularySnapshot]
        do {
            snapshots = try readLegacyVocabularySnapshots(at: legacyURL)
        } catch {
            AppLogger.coreData.error("Vocabulary migration failed to read legacy store: \(error, privacy: .public)")
            SentryService.capture(
                error: error,
                message: "Vocabulary migration: failed to read legacy store",
                tags: ["component": "PersistenceController", "operation": "migrateLegacyVocabulary", "phase": "read"]
            )
            return // do NOT set flag — retry on next launch
        }

        if snapshots.isEmpty {
            defaults.set(true, forKey: vocabularyMigrationDefaultsKey)
            AppLogger.coreData.info("Vocabulary migration complete — legacy store had no vocabulary rows (legacy file preserved)")
            return
        }

        // --- Phase B: write snapshots into the new cloud store ---
        do {
            try writeVocabularySnapshots(snapshots, to: cloudURL)
        } catch {
            AppLogger.coreData.error("Vocabulary migration failed to write cloud store: \(error, privacy: .public)")
            SentryService.capture(
                error: error,
                message: "Vocabulary migration: failed to write cloud store",
                extras: ["rowCount": "\(snapshots.count)"],
                tags: ["component": "PersistenceController", "operation": "migrateLegacyVocabulary", "phase": "write"]
            )
            return // do NOT set flag — retry on next launch. Legacy rows are untouched.
        }

        defaults.set(true, forKey: vocabularyMigrationDefaultsKey)
        AppLogger.coreData.info("Vocabulary migration complete — copied \(snapshots.count, privacy: .public) rows to cloud store (legacy preserved as dormant backup)")
    }

    /// Opens a temporary container against the legacy store using the EXPLICIT v24
    /// managed object model, reads all `Vocabulary` rows into value-type snapshots,
    /// and returns them. Does NOT delete anything, does NOT save, does NOT trigger
    /// lightweight migration — the legacy SQLite file is left byte-identical.
    ///
    /// Loads the v24 model from `HyperWhisper.momd/HyperWhisper_v24.mom` in the app
    /// bundle. v24 has no named configurations so the default (nil) configuration is
    /// guaranteed to contain the `Vocabulary` entity.
    ///
    /// The container is torn down before the function returns so the legacy file is not
    /// locked when the main container loads it afterwards.
    private static func readLegacyVocabularySnapshots(at url: URL) throws -> [VocabularySnapshot] {
        // Load the v24 model explicitly. When Xcode compiles the .xcdatamodeld bundle,
        // each .xcdatamodel version becomes a .mom file inside HyperWhisper.momd.
        guard let modelURL = Bundle.main.url(
            forResource: "HyperWhisper_v24",
            withExtension: "mom",
            subdirectory: "HyperWhisper.momd"
        ) else {
            throw NSError(
                domain: "PersistenceController.VocabularyMigration",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: "HyperWhisper_v24.mom not found in bundle"]
            )
        }
        guard let v24Model = NSManagedObjectModel(contentsOf: modelURL) else {
            throw NSError(
                domain: "PersistenceController.VocabularyMigration",
                code: 2,
                userInfo: [NSLocalizedDescriptionKey: "Failed to load NSManagedObjectModel from \(modelURL.path)"]
            )
        }

        // Plain NSPersistentContainer — NOT CloudKit. No automatic lightweight
        // migration: we want the legacy file to stay exactly as it is on disk.
        let tempContainer = NSPersistentContainer(name: "HyperWhisper", managedObjectModel: v24Model)
        let desc = NSPersistentStoreDescription(url: url)
        desc.configuration = nil // v24 has no named configs; default contains every entity
        desc.setOption(false as NSNumber, forKey: NSMigratePersistentStoresAutomaticallyOption)
        desc.setOption(false as NSNumber, forKey: NSInferMappingModelAutomaticallyOption)
        desc.isReadOnly = true
        tempContainer.persistentStoreDescriptions = [desc]

        var loadError: Error?
        tempContainer.loadPersistentStores { _, error in
            loadError = error
        }
        if let loadError {
            throw loadError
        }

        let context = tempContainer.viewContext
        let request = NSFetchRequest<NSManagedObject>(entityName: "Vocabulary")
        let results = try context.fetch(request)

        let snapshots: [VocabularySnapshot] = results.compactMap { obj in
            guard
                let word = obj.value(forKey: "word") as? String,
                !word.isEmpty
            else {
                return nil
            }
            let id = (obj.value(forKey: "id") as? UUID) ?? UUID()
            let replacement = obj.value(forKey: "replacement") as? String
            let createdDate = (obj.value(forKey: "createdDate") as? Date) ?? Date()
            let sortOrder = (obj.value(forKey: "sortOrder") as? Int16) ?? 0
            return VocabularySnapshot(
                id: id,
                word: word,
                replacement: replacement,
                createdDate: createdDate,
                sortOrder: sortOrder
            )
        }

        // Release stores so the file is unlocked before we return.
        // No save, no delete — the legacy file is left untouched.
        for store in tempContainer.persistentStoreCoordinator.persistentStores {
            try tempContainer.persistentStoreCoordinator.remove(store)
        }

        return snapshots
    }

    /// Writes vocabulary snapshots into the cloud store URL using a temporary container.
    /// CloudKit mirroring is NOT enabled during this write — it happens on first launch
    /// of the main container, which will push the rows up from the local cloud store file.
    private static func writeVocabularySnapshots(_ snapshots: [VocabularySnapshot], to url: URL) throws {
        let tempContainer = NSPersistentContainer(name: "HyperWhisper", managedObjectModel: managedObjectModel)
        let desc = NSPersistentStoreDescription(url: url)
        desc.configuration = cloudConfigurationName
        desc.setOption(true as NSNumber, forKey: NSMigratePersistentStoresAutomaticallyOption)
        desc.setOption(true as NSNumber, forKey: NSInferMappingModelAutomaticallyOption)
        // History tracking is required so the later CloudKit container can take over this store.
        desc.setOption(true as NSNumber, forKey: NSPersistentHistoryTrackingKey)
        desc.setOption(true as NSNumber, forKey: NSPersistentStoreRemoteChangeNotificationPostOptionKey)
        tempContainer.persistentStoreDescriptions = [desc]

        var loadError: Error?
        tempContainer.loadPersistentStores { _, error in
            loadError = error
        }
        if let loadError {
            throw loadError
        }

        let context = tempContainer.viewContext
        for snapshot in snapshots {
            let obj = NSEntityDescription.insertNewObject(forEntityName: "Vocabulary", into: context)
            obj.setValue(snapshot.id, forKey: "id")
            obj.setValue(snapshot.word, forKey: "word")
            obj.setValue(snapshot.replacement, forKey: "replacement")
            obj.setValue(snapshot.createdDate, forKey: "createdDate")
            obj.setValue(snapshot.sortOrder, forKey: "sortOrder")
        }
        if context.hasChanges {
            try context.save()
        }

        // Release stores so the file is unlocked before the main container loads.
        for store in tempContainer.persistentStoreCoordinator.persistentStores {
            try tempContainer.persistentStoreCoordinator.remove(store)
        }
    }

    /// One-time data migration for legacy HyperWhisper Cloud routing identifiers.
    /// Older releases stored UI-ish tier values (`high`, `Grok`) and one generic
    /// post-processing value (`default`). Newer code stores stable route IDs.
    private func normalizeCloudRouteIdentifiersIfNeeded() {
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: Self.cloudRouteIdentifierMigrationDefaultsKey) {
            return
        }

        let context = container.viewContext
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()

        do {
            let modes = try context.fetch(request)
            var changedCount = 0

            for mode in modes {
                let normalizedAccuracyTier = CloudAccuracyTier.fromStorageValue(mode.cloudAccuracyTier).rawValue
                if mode.cloudAccuracyTier != normalizedAccuracyTier {
                    mode.cloudAccuracyTier = normalizedAccuracyTier
                    changedCount += 1
                }

                let normalizedPostProcessingModel = CloudPostProcessingModel.fromStorageValue(mode.cloudPostProcessingModel).rawValue
                if mode.cloudPostProcessingModel != normalizedPostProcessingModel {
                    mode.cloudPostProcessingModel = normalizedPostProcessingModel
                    changedCount += 1
                }
            }

            if context.hasChanges {
                try context.save()
            }

            defaults.set(true, forKey: Self.cloudRouteIdentifierMigrationDefaultsKey)
            AppLogger.coreData.info("Normalized cloud route identifiers for \(changedCount, privacy: .public) mode fields")
        } catch {
            let nsError = error as NSError
            // One event, not two: the sibling SentryService.capture that used to
            // sit here was ungated, stack-trace fingerprinted, and carried the
            // recent_logs blob. `site` now says which caller failed.
            AppLogger.logCoreData(
                .save(site: "normalize_cloud_route_identifiers", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: CoreDataSaveDiagnostics.contextShape(context)
            )
        }
    }

    /// One-time data migration that folds legacy standalone cloudProvider values
    /// (`microsoftazurespeech`, `googlespeech`) onto `hyperwhisper` + the matching
    /// accuracy tier. Sourced from the catalog's `migrateFrom` aliases so the
    /// rename rules live in one place.
    private func normalizeCloudProviderIfNeeded() {
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: Self.cloudProviderMigrationDefaultsKey) {
            return
        }

        let context = container.viewContext
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()

        do {
            let modes = try context.fetch(request)
            var changedCount = 0

            for mode in modes {
                let result = CloudSTTCatalog.shared.normalizeCloudProvider(mode.cloudProvider)
                if let migratedProvider = result.provider, migratedProvider != mode.cloudProvider {
                    mode.cloudProvider = migratedProvider
                    changedCount += 1
                }
                // Only overwrite cloudAccuracyTier when the user hasn't
                // customised it (nil/empty or the default 'deepgramNova3').
                // Otherwise a user who paired e.g. microsoftazurespeech with
                // a non-default tier would silently lose their tier choice.
                if let migratedTier = result.accuracyTier, migratedTier != mode.cloudAccuracyTier {
                    let existing = mode.cloudAccuracyTier ?? ""
                    if existing.isEmpty || existing == CloudAccuracyTier.deepgramNova3.rawValue {
                        mode.cloudAccuracyTier = migratedTier
                        changedCount += 1
                    }
                }
            }

            if context.hasChanges {
                try context.save()
            }

            defaults.set(true, forKey: Self.cloudProviderMigrationDefaultsKey)
            AppLogger.coreData.info("Normalized cloud provider values for \(changedCount, privacy: .public) mode fields")
        } catch {
            let nsError = error as NSError
            // One event, not two: the sibling SentryService.capture that used to
            // sit here was ungated, stack-trace fingerprinted, and carried the
            // recent_logs blob. `site` now says which caller failed.
            AppLogger.logCoreData(
                .save(site: "normalize_cloud_provider", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: CoreDataSaveDiagnostics.contextShape(context)
            )
        }
    }

    /// One-time data migration for the retired `googleChirp3` accuracy tier.
    ///
    /// Catalog v8 replaced Chirp 3 with Gemini 3.5 Transcribe as Google's cloud
    /// tier. `CloudAccuracyTier.fromStorageValue` does rescue the old value
    /// through the catalog's `migrateFrom` aliases, but it is called at five
    /// separate read sites and its fallback is `.deepgramNova3` — and the Local
    /// API's mode PATCH path does not call it at all. Converge the stored data
    /// once so no reader has to be the last line of defence.
    ///
    /// Deliberately gated by a NEW key: `didNormalizeCloudProviderValuesV1` is
    /// already `true` for every existing user, which is exactly the population
    /// this migration exists for.
    ///
    /// Unlike `normalizeCloudProviderIfNeeded`, this does NOT preserve a
    /// "customised" tier: the value being rewritten IS the user's explicit
    /// choice, and the whole point is to carry it forward rather than drop it.
    private func migrateGoogleChirp3TierIfNeeded() {
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: Self.googleChirp3TierMigrationDefaultsKey) {
            return
        }

        let context = container.viewContext
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()

        do {
            let modes = try context.fetch(request)
            var changedCount = 0

            for mode in modes {
                guard let rewrite = Self.googleChirp3TierRewrite(
                    cloudProvider: mode.cloudProvider,
                    cloudAccuracyTier: mode.cloudAccuracyTier,
                    cloudTranscriptionModel: mode.cloudTranscriptionModel
                ) else { continue }

                if let tier = rewrite.accuracyTier {
                    mode.cloudAccuracyTier = tier
                }
                if let model = rewrite.transcriptionModel {
                    mode.cloudTranscriptionModel = model
                }
                changedCount += 1
            }

            if context.hasChanges {
                try context.save()
            }

            defaults.set(true, forKey: Self.googleChirp3TierMigrationDefaultsKey)
            AppLogger.coreData.info("Migrated googleChirp3 accuracy tier on \(changedCount, privacy: .public) modes")
        } catch {
            let nsError = error as NSError
            // One event, not two, and the same shape every sibling migration
            // reports: `site` names this caller, `contextKey` names the context
            // whose failure streak it belongs to.
            AppLogger.logCoreData(
                .save(site: "migrate_google_chirp3_tier", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: CoreDataSaveDiagnostics.contextShape(context)
            )
        }
    }

    /// Launch hook: repairs broken `.local` modes every launch (cheap; saves only on
    /// change). Runs unconditionally — a model deleted after a previous launch is
    /// still cleaned up. Not gated by a one-time UserDefaults flag for that reason.
    private func repairBrokenLocalModesOnLaunch() {
        let result = repairBrokenLocalModes(
            capability: SystemCapability.current,
            isCataloged: { LocalModelManager.catalogModelIds.contains($0) },
            isDownloaded: { Self.localModelFileExists($0) },
            keepPendingDownloads: false
        )
        if !result.disabledModeNames.isEmpty {
            AppLogger.coreData.info("Launch repair turned off local post-processing for \(result.disabledModeNames.count, privacy: .public) mode(s)")
        }
    }

    /// Launch hook: any transcript still marked "processing" at init is stale —
    /// nothing can be in flight this early in the process lifetime — left behind
    /// by a quit/crash between record-stop and the completion write. Mark it
    /// failed ("interrupted") so it doesn't sit in HistoryView as processing
    /// forever; rows that kept their audio file get the standard Retry
    /// affordance for free (canRetry = failed + audio path present).
    private func repairStaleProcessingTranscriptsOnLaunch() {
        let context = container.viewContext
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(format: "status == %@", "processing")
        do {
            let stale = try context.fetch(request)
            guard !stale.isEmpty else { return }
            for transcript in stale {
                transcript.setValue("failed", forKey: "status")
                transcript.setValue("interrupted", forKey: "failedReason")
                transcript.text = "history.status.interrupted".localized
            }
            try context.save()
            AppLogger.coreData.info("Launch repair marked \(stale.count, privacy: .public) stale processing transcript(s) as interrupted")
        } catch {
            let nsError = error as NSError
            // One event, not two: the sibling SentryService.capture that used to
            // sit here was ungated, stack-trace fingerprinted, and carried the
            // recent_logs blob. `site` now says which caller failed.
            AppLogger.logCoreData(
                .save(site: "repair_stale_processing_transcripts", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: CoreDataSaveDiagnostics.contextShape(context)
            )
        }
    }

    /// One-time data migration that rewrites Modes, the per-mode default-model map,
    /// and the streaming Deepgram setting away from the 25 Deepgram model IDs that
    /// were removed in the 2026-05 catalog cleanup. Everything in
    /// `CloudTranscriptionModels.removedDeepgramModelIds` collapses to `nova-3-general`.
    private func migrateRemovedDeepgramModelsIfNeeded() {
        let defaults = UserDefaults.standard
        let needsFullMigration = !defaults.bool(forKey: Self.removedDeepgramModelsMigrationDefaultsKey)
        let needsDefaultModelByModeRetry = !defaults.bool(forKey: Self.removedDeepgramDefaultModelByModeMigrationDefaultsKey)

        let target = "nova-3-general"
        let removed = CloudTranscriptionModels.removedDeepgramModelIds
        var totalChanges = 0

        if !needsFullMigration {
            guard needsDefaultModelByModeRetry else {
                return
            }

            totalChanges += Self.migrateRemovedDeepgramUserDefaults(
                defaults: defaults,
                removed: removed,
                target: target,
                migrateStreamingDeepgramModel: false
            )
            defaults.set(true, forKey: Self.removedDeepgramDefaultModelByModeMigrationDefaultsKey)
            AppLogger.coreData.info("Retried defaultModelByMode removed Deepgram model migration · changes=\(totalChanges, privacy: .public)")
            return
        }

        // 1. Core Data Modes
        let context = container.viewContext
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        do {
            let modes = try context.fetch(request)
            for mode in modes {
                if let current = mode.cloudTranscriptionModel, removed.contains(current) {
                    mode.cloudTranscriptionModel = target
                    totalChanges += 1
                }
            }
            if context.hasChanges {
                try context.save()
            }
        } catch {
            let nsError = error as NSError
            // One event, not two: the sibling SentryService.capture that used to
            // sit here was ungated, stack-trace fingerprinted, and carried the
            // recent_logs blob. `site` now says which caller failed.
            AppLogger.logCoreData(
                .save(site: "migrate_removed_deepgram_models", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: CoreDataSaveDiagnostics.contextShape(context)
            )
            // Don't set the flag — try again next launch.
            return
        }

        // 2 & 3. Avoid touching SettingsManager here: PersistenceController is
        // not MainActor-isolated, and tests can construct it off the main actor.
        // These UserDefaults-backed values can be normalized directly.
        let settingsChanges = Self.migrateRemovedDeepgramUserDefaults(
            defaults: defaults,
            removed: removed,
            target: target,
            migrateStreamingDeepgramModel: true
        )
        totalChanges += settingsChanges

        defaults.set(true, forKey: Self.removedDeepgramModelsMigrationDefaultsKey)
        defaults.set(true, forKey: Self.removedDeepgramDefaultModelByModeMigrationDefaultsKey)
        AppLogger.coreData.info("Migrated removed Deepgram model IDs to nova-3-general · changes=\(totalChanges, privacy: .public)")
    }

    private static func migrateRemovedDeepgramUserDefaults(
        defaults: UserDefaults,
        removed: Set<String>,
        target: String,
        migrateStreamingDeepgramModel: Bool
    ) -> Int {
        var changes = 0

        // `defaultModelByMode` is persisted by SettingsManager as JSON-encoded
        // Data (not a plist dictionary), so it must be decoded/re-encoded the
        // same way. `dictionary(forKey:)` returns nil for a Data blob, which
        // previously made this migration a permanent no-op.
        let defaultModelByModeKey = "defaultModelByMode"
        if let data = defaults.data(forKey: defaultModelByModeKey),
           var modelByMode = try? JSONDecoder().decode([String: String].self, from: data) {
            var changed = false
            for (modeId, modelId) in modelByMode where removed.contains(modelId) {
                modelByMode[modeId] = target
                changed = true
                changes += 1
            }
            if changed, let encoded = try? JSONEncoder().encode(modelByMode) {
                defaults.set(encoded, forKey: defaultModelByModeKey)
            }
        }

        guard migrateStreamingDeepgramModel else {
            return changes
        }

        let streamingDeepgramModelKey = "streamingDeepgramModel"
        let streamingDeepgramModel = defaults.string(forKey: streamingDeepgramModelKey)
        if let streamingDeepgramModel, removed.contains(streamingDeepgramModel) {
            defaults.set(target, forKey: streamingDeepgramModelKey)
            changes += 1
        }

        return changes
    }

    /// Save-site slug for the serial background writer.
    private static let writerSaveSite = "background_writer"

    /// Maximum rows deleted by one auto-delete writer transaction. The fetch
    /// reads one extra row only to decide whether the service must continue.
    private static let autoDeleteBatchLimit = 100
    
    // MARK: - Background Writer (serial)

    /// Long-lived, serial background context that all stop-flow writes funnel
    /// through. Because every write runs on this single private queue, writes are
    /// serialized (closing the duplicate-transcript race under rapid stop presses)
    /// AND kept entirely off the main thread (removing the ~4.7s main-thread Core
    /// Data stall that froze the stop→paste path).
    ///
    /// `automaticallyMergesChangesFromParent = true` means the `viewContext` picks
    /// up these writes via the standard `NSManagedObjectContextDidSave` merge, so
    /// HistoryView's existing save-notification observer refreshes with no change.
    ///
    /// Merge policy is store-trump: user actions committed on the `viewContext`
    /// (e.g. a HistoryView delete) win over this context's in-flight background
    /// writes, so a delete racing a slow transcription's completion write stays
    /// deleted rather than being resurrected.
    private let writerContext: NSManagedObjectContext

    /// Debounced view-context maintenance task (cancel-previous). Keeps the
    /// re-faulting benefit off the stop→paste hot path.
    @MainActor private var viewContextMaintenanceTask: Task<Void, Never>?

    /// Flush the serial writer before process exit. An empty `performAndWait`
    /// barrier queues behind any already-enqueued write blocks (each block
    /// saves itself), so returning means everything enqueued so far is
    /// persisted. Writer blocks are milliseconds each — safe to call from
    /// `applicationWillTerminate` without beachball risk.
    func drainWriterOnTerminate() {
        writerContext.performAndWait { }
    }

    /// Run a write block on the serial background writer context.
    ///
    /// The block receives the writer context and performs its mutations; if it
    /// leaves pending changes they are saved on the writer queue. On save failure
    /// the context is rolled back (never leaving the long-lived context poisoned)
    /// and the error is logged + captured. After every write the writer context is
    /// re-faulted to stay lean, and view-context maintenance is scheduled.
    ///
    /// Pass ONLY value types + `NSManagedObjectID` into `block` — never a managed
    /// object — so nothing crosses a context boundary.
    @discardableResult
    func performWrite<T: Sendable>(_ block: @escaping (NSManagedObjectContext) -> T) async -> T {
        return await performWriteReportingSave(block).value
    }

    /// Like `performWrite`, but returns `nil` unless the write actually persisted.
    ///
    /// Use for creates whose returned value (e.g. an `NSManagedObjectID`) is only
    /// meaningful if the row was saved — on save failure the writer rolls back, so
    /// handing the ID out anyway would point at a row that doesn't exist.
    func performWriteRequiringSave<T: Sendable>(_ block: @escaping (NSManagedObjectContext) -> T?) async -> T? {
        await performWriteRequiringSave(deleteWinsConflicts: false, block)
    }

    private func performWriteRequiringSave<T: Sendable>(
        deleteWinsConflicts: Bool,
        _ block: @escaping (NSManagedObjectContext) -> T?
    ) async -> T? {
        let result = await performWriterTransaction(deleteWinsConflicts: deleteWinsConflicts) { context in
            let value = block(context)
            return (value: value, shouldCommit: value != nil)
        }
        return result.saved ? result.value : nil
    }

    private func performWriteReportingSave<T: Sendable>(_ block: @escaping (NSManagedObjectContext) -> T) async -> (value: T, saved: Bool) {
        await performWriterTransaction { context in
            (value: block(context), shouldCommit: true)
        }
    }

    /// Shared serial-writer transaction envelope. `shouldCommit` lets the
    /// requiring-save API preserve its intentional nil-abort behavior without
    /// changing `performWrite` callers whose value can itself be optional.
    private func performWriterTransaction<T: Sendable>(
        deleteWinsConflicts: Bool = false,
        _ block: @escaping (NSManagedObjectContext) -> (value: T, shouldCommit: Bool)
    ) async -> (value: T, saved: Bool) {
        let context = writerContext
        let result: (value: T, saved: Bool, changed: Bool) = await context.perform {
            let previousMergePolicy = context.mergePolicy
            if deleteWinsConflicts {
                context.mergePolicy = NSMergeByPropertyObjectTrumpMergePolicy
            }
            defer { context.mergePolicy = previousMergePolicy }

            let operation = block(context)
            let changed = context.hasChanges
            guard operation.shouldCommit else {
                if changed {
                    context.rollback()
                    context.refreshAllObjects()
                }
                return (operation.value, false, changed)
            }

            let saved = !changed || self.savePendingWriterChanges(context)
            if changed {
                context.refreshAllObjects()
            }
            return (operation.value, saved, changed)
        }
        if result.changed {
            await MainActor.run { self.scheduleViewContextMaintenance() }
        }
        return (result.value, result.saved)
    }

    private func savePendingWriterChanges(_ context: NSManagedObjectContext) -> Bool {
        do {
            try saveWriterContext(context)
            CoreDataSaveDiagnostics.recordSuccess(contextKey: CoreDataSaveDiagnostics.writerContextKey)
            return true
        } catch {
            let nsError = error as NSError
            // Read before `rollback()` below, which empties these sets.
            let shape = CoreDataSaveDiagnostics.contextShape(context)
            AppLogger.logCoreData(
                .save(site: PersistenceController.writerSaveSite, contextKey: CoreDataSaveDiagnostics.writerContextKey),
                error: nsError,
                metadata: shape
            )
            context.rollback()
            return false
        }
    }

    /// One overrideable persistence boundary lets tests prove production callers'
    /// save-failure behavior without copying their fetch and mutation bodies.
    func saveWriterContext(_ context: NSManagedObjectContext) throws {
        try context.save()
    }

    /// Delete expired transcripts as one uninterrupted serial-writer transaction.
    ///
    /// The cutoff fetch, ordered path snapshot, row deletes, and save all run on
    /// `writerContext`. The returned snapshot contains values only and is handed
    /// out only after the save commits. A fetch or save failure returns `nil`, so
    /// callers must not unlink any files or record successful cleanup state.
    func deleteTranscriptsOlderThanInBackground(_ cutoffDate: Date) async -> AutoDeleteTransactionSnapshot? {
        let snapshot = await performWriteRequiringSave(deleteWinsConflicts: true) { context -> AutoDeleteTransactionSnapshot? in
            let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
            request.predicate = NSPredicate(format: "date < %@", cutoffDate as NSDate)
            request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: true)]
            // Keep stop-to-paste writes and the termination barrier responsive,
            // even when an old installation has a large cleanup backlog.
            request.fetchLimit = Self.autoDeleteBatchLimit + 1

            let transcripts: [Transcript]
            do {
                transcripts = try context.fetch(request)
            } catch {
                AppLogger.coreData.error("Failed to fetch transcripts for auto-delete: \(error, privacy: .public)")
                SentryService.capture(
                    error: error,
                    message: "Failed to fetch transcripts for auto-delete",
                    tags: ["component": "AutoDeleteCleanupService"],
                    includeRecentLogs: false
                )
                return nil
            }

            let batch = Array(transcripts.prefix(Self.autoDeleteBatchLimit))
            // Same file list as History's Delete, including the unrecorded
            // derived files (#1513). Collected before any row is deleted so
            // the ownership check sees only the rows that survive this batch.
            let paths = Self.audioFilePathsToDelete(for: batch, in: context)
            for transcript in batch {
                context.delete(transcript)
            }

            return AutoDeleteTransactionSnapshot(
                audioPaths: paths,
                transcriptsDeleted: batch.count,
                hasMore: transcripts.count > batch.count,
                transcriptIDs: batch.compactMap(\.id)
            )
        }
        // Only once the delete committed: stop the retries of the expired rows.
        if let snapshot {
            Self.stopRetries(ofDeleted: snapshot.transcriptIDs)
        }
        return snapshot
    }

    /// Debounced (cancel-previous) view-context re-faulting. After ~3s of write
    /// quiescence, if the view context has no pending edits, re-fault it to keep
    /// it lean without ever touching the stop→paste path.
    @MainActor
    private func scheduleViewContextMaintenance() {
        viewContextMaintenanceTask?.cancel()
        viewContextMaintenanceTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            guard !Task.isCancelled, let self else { return }
            let viewContext = self.container.viewContext
            guard !viewContext.hasChanges else { return }
            viewContext.refreshAllObjects()
        }
    }

    // MARK: - Background Domain Writes (stop flow)

    /// Create a processing transcript on the serial writer.
    ///
    /// Shares `createProcessingTranscript`'s idempotency guard, and additionally
    /// accepts the VAD trimmed path so the separate `setTrimmedAudioPath` write
    /// collapses into this single create. Returns the permanent object ID **only if
    /// the row was actually saved** — a dangling ID after a failed save would make
    /// later status/text updates silently write to a row that doesn't exist.
    func createProcessingTranscriptInBackground(
        duration: TimeInterval,
        mode: String?,
        audioFilePath: String?,
        trimmedAudioPath: String?
    ) async -> NSManagedObjectID? {
        return await performWriteRequiringSave { context -> NSManagedObjectID? in
            let reused = Self.reuseProcessingTranscript(
                in: context,
                audioFilePath: audioFilePath,
                duration: duration,
                mode: mode
            )

            let transcript: Transcript
            if let reused {
                transcript = reused
            } else {
                let created = Transcript(context: context)
                created.id = UUID()
                created.text = "Processing transcription..."
                created.date = Date()
                created.duration = duration
                created.mode = mode
                created.audioFilePath = audioFilePath
                created.setValue("processing", forKey: "status")
                transcript = created
            }

            if let trimmedAudioPath, !trimmedAudioPath.isEmpty {
                transcript.setValue(trimmedAudioPath, forKey: "trimmedAudioFilePath")
            }

            do {
                try context.obtainPermanentIDs(for: [transcript])
            } catch {
                AppLogger.coreData.error("Failed to obtain permanent ID for processing transcript: \(error.localizedDescription)")
                return nil
            }
            return transcript.objectID
        }
    }

    /// Create an already-failed transcript on the serial writer in ONE write
    /// (replaces the old create + mutate + second save on the error paths).
    /// Returns the permanent object ID **only if the row was actually saved**.
    func createFailedTranscriptInBackground(
        duration: TimeInterval,
        mode: String?,
        audioFilePath: String?,
        failedReason: String,
        errorText: String
    ) async -> NSManagedObjectID? {
        return await performWriteRequiringSave { context -> NSManagedObjectID? in
            let created = Transcript(context: context)
            created.id = UUID()
            created.date = Date()
            created.duration = duration
            created.mode = mode
            created.audioFilePath = audioFilePath
            created.setValue("failed", forKey: "status")
            created.setValue(failedReason, forKey: "failedReason")
            created.text = errorText
            do {
                try context.obtainPermanentIDs(for: [created])
            } catch {
                AppLogger.coreData.error("Failed to obtain permanent ID for failed transcript: \(error.localizedDescription)")
                return nil
            }
            return created.objectID
        }
    }

    /// Complete a processing transcript on the serial writer. Same field updates
    /// and near-now dedup (`collapseTransientTwins`) as the `@MainActor` variant, but no `processPendingChanges()`
    /// and no view-context `refreshAllObjects()` — the merge notification refreshes
    /// HistoryView, and maintenance is debounced off the hot path.
    ///
    /// - Parameter completesFailedRowByRetry: the row was saved as FAILED and a
    ///   retry is completing it (#1636). Clears `failedReason` (History reads
    ///   any non-empty one as a failure, so a completed row that kept it would
    ///   still show as failed; a processing row has none) and counts the retry
    ///   (`retryCount` + 1, `lastRetryDate`) as History's own Retry does
    ///   (`TranscriptionRetryController`).
    /// - Returns: whether the row was found and the write was saved.
    @discardableResult
    func updateTranscriptWithTranscriptionInBackground(
        transcriptID: NSManagedObjectID,
        transcribedText: String,
        postProcessedText: String? = nil,
        transcriptionProvider: String? = nil,
        postProcessingProvider: String? = nil,
        wordTimestampsJSON: String? = nil,
        completesFailedRowByRetry: Bool = false
    ) async -> Bool {
        let result = await performWriteReportingSave { context -> Bool in
            guard let transcript = try? context.existingObject(with: transcriptID) as? Transcript else {
                AppLogger.coreData.warning("updateTranscriptWithTranscriptionInBackground: transcript row not found (cancel race?) — skipping")
                return false
            }

            transcript.text = postProcessedText ?? transcribedText
            transcript.setValue("completed", forKey: "status")
            if completesFailedRowByRetry {
                transcript.setValue(nil, forKey: "failedReason")
                let currentRetryCount = transcript.value(forKey: "retryCount") as? Int16 ?? 0
                transcript.setValue(currentRetryCount + 1, forKey: "retryCount")
                transcript.setValue(Date(), forKey: "lastRetryDate")
            }
            transcript.setValue(transcribedText, forKey: "transcribedText")
            // Always overwrite (including clearing to nil) — see the @MainActor variant.
            transcript.setValue(postProcessedText, forKey: "postProcessedText")
            if let transcriptionProvider {
                transcript.setValue(transcriptionProvider, forKey: "transcriptionProvider")
            }
            // Always overwrite, in step with `postProcessedText` — see the @MainActor variant.
            transcript.setValue(postProcessingProvider, forKey: "postProcessingProvider")
            // Always overwrite (including clearing to nil) — see the @MainActor variant.
            transcript.setValue(wordTimestampsJSON, forKey: "wordTimestampsJSON")

            Self.collapseTransientTwins(of: transcript, in: context, transcribedText: transcribedText)
            return true
        }
        return result.value && result.saved
    }

    /// Mark a transcript failed on the serial writer.
    ///
    /// - Parameters:
    ///   - countsRetry: a retry failed this row (#1815). Counts it
    ///     (`retryCount` + 1, `lastRetryDate`) as History's own Retry does.
    ///   - transcriptionProvider: the engine that failed, the same kind of
    ///     value a completed row stores ("Tiny (Multilingual)", "Parakeet TDT"),
    ///     so History can show which engine failed (#1518). `nil` leaves the
    ///     column as it is, as the completed-row writes do.
    /// - Returns: whether the row was found and the write was saved.
    @discardableResult
    func markTranscriptFailedInBackground(
        transcriptID: NSManagedObjectID,
        failedReason: String,
        errorText: String,
        countsRetry: Bool = false,
        transcriptionProvider: String? = nil
    ) async -> Bool {
        let result = await performWriteReportingSave { context -> Bool in
            guard let transcript = try? context.existingObject(with: transcriptID) as? Transcript else {
                AppLogger.coreData.warning("markTranscriptFailedInBackground: transcript row not found — skipping")
                return false
            }
            transcript.setValue("failed", forKey: "status")
            transcript.setValue(failedReason, forKey: "failedReason")
            transcript.text = errorText
            if countsRetry {
                let currentRetryCount = transcript.value(forKey: "retryCount") as? Int16 ?? 0
                transcript.setValue(currentRetryCount + 1, forKey: "retryCount")
                transcript.setValue(Date(), forKey: "lastRetryDate")
            }
            if let transcriptionProvider {
                transcript.setValue(transcriptionProvider, forKey: "transcriptionProvider")
            }
            return true
        }
        return result.value && result.saved
    }

    // MARK: - Transcript De-duplication Rules

    // Both rules delete transcript rows. The serial writer and the
    // `viewContext` paths each pass their own context, so every rule lives
    // here once (#909).

    /// The only status the idempotency guard reuses.
    static let reusableTranscriptStatus = "processing"
    /// A twin row's date is at most this far from the anchor's, in seconds.
    static let transientTwinDateWindow: TimeInterval = 20
    /// A twin row's duration is at most this far from the anchor's, in seconds.
    static let transientTwinDurationWindow: TimeInterval = 0.5

    /// Idempotency guard: if a processing transcript already exists for the
    /// same audio file, reset it for a new run and return it instead of
    /// creating a duplicate. Any extra processing rows for that path are
    /// collapsed, keeping the newest. Returns `nil` when the path is empty
    /// or no processing row exists. Call on `context`'s queue.
    static func reuseProcessingTranscript(
        in context: NSManagedObjectContext,
        audioFilePath: String?,
        duration: TimeInterval,
        mode: String?
    ) -> Transcript? {
        let normalizedPath = audioFilePath?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !normalizedPath.isEmpty else { return nil }

        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(
            format: "status == %@ AND audioFilePath == %@",
            reusableTranscriptStatus,
            normalizedPath
        )
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]

        guard let existing = try? context.fetch(request), let primary = existing.first else { return nil }

        if existing.count > 1 {
            for duplicate in existing.dropFirst() {
                context.delete(duplicate)
            }
            stopRetries(ofDeleted: existing.dropFirst().compactMap(\.id))
            AppLogger.coreData.warning(
                "Collapsed duplicate processing transcripts for audio path: \(normalizedPath, privacy: .public) (\(existing.count, privacy: .public) -> 1)"
            )
        }

        primary.text = "Processing transcription..."
        primary.date = Date()
        primary.duration = max(primary.duration, duration)
        primary.mode = mode
        primary.audioFilePath = normalizedPath
        primary.setValue("processing", forKey: "status")
        return primary
    }

    /// Collapse transient duplicates: in rare stop/transcribe race
    /// conditions, near-identical entries can be created for the same audio
    /// path. Delete only "near-now" twins of the just-completed `transcript`,
    /// so legitimate historical retranscriptions of the same file stay.
    /// Call on `context`'s queue, after `transcript`'s text fields are set.
    static func collapseTransientTwins(
        of transcript: Transcript,
        in context: NSManagedObjectContext,
        transcribedText: String
    ) {
        guard let rawPath = transcript.audioFilePath else { return }
        let audioPath = rawPath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !audioPath.isEmpty else { return }

        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(format: "SELF != %@ AND audioFilePath == %@", transcript.objectID, audioPath)

        guard let candidates = try? context.fetch(request) else { return }
        let anchorDate = transcript.date ?? Date()
        let anchorDuration = transcript.duration
        let anchorText = transcript.text ?? ""

        for candidate in candidates {
            let status = candidate.value(forKey: "status") as? String ?? ""
            guard status == "processing" || status == "completed" else { continue }

            let candidateDate = candidate.date ?? .distantPast
            let isNearInTime = abs(candidateDate.timeIntervalSince(anchorDate)) <= transientTwinDateWindow
            let isNearInDuration = abs(candidate.duration - anchorDuration) <= transientTwinDurationWindow
            let candidateRawText = candidate.value(forKey: "transcribedText") as? String ?? ""
            let sameText = (candidate.text ?? "") == anchorText || candidateRawText == transcribedText

            if isNearInTime && (isNearInDuration || sameText) {
                context.delete(candidate)
                stopRetries(ofDeleted: [candidate.id].compactMap { $0 })
                AppLogger.coreData.warning("Removed transient duplicate transcript for path: \(audioPath, privacy: .public)")
            }
        }
    }

    // MARK: - Transcript Operations

    /// Creates a new transcript in processing state
    ///
    /// LEGACY (main-context, synchronous): blocks on `viewContext`, so a large
    /// store can stall the main thread. New callers on any hot path should use
    /// `createProcessingTranscriptInBackground` (serial writer, ID-based)
    /// instead. Remaining callers are UI-initiated flows (file transcription,
    /// retry, transcript actions) where the returned object is bound to UI.
    ///
    /// - Parameters:
    ///   - duration: Recording duration in seconds
    ///   - mode: The transcription mode used
    ///   - audioFilePath: Path to the audio file
    /// - Returns: The created transcript
    @discardableResult
    func createProcessingTranscript(
        duration: TimeInterval,
        mode: String? = nil,
        audioFilePath: String? = nil
    ) -> Transcript {
        let context = container.viewContext

        var transcript: Transcript!
        context.performAndWait {
            // Idempotency guard: reuse a processing row for the same audio
            // file instead of creating a duplicate.
            if let reused = Self.reuseProcessingTranscript(
                in: context,
                audioFilePath: audioFilePath,
                duration: duration,
                mode: mode
            ) {
                transcript = reused
                return
            }

            let created = Transcript(context: context)
            created.id = UUID()
            created.text = "Processing transcription..."
            created.date = Date()
            created.duration = duration
            created.mode = mode
            created.audioFilePath = audioFilePath
            created.setValue("processing", forKey: "status")
            transcript = created
        }

        save()

        return transcript
    }
    
    /// Updates a transcript with the transcription result
    /// This method is called when transcription completes (either successfully or with error)
    ///
    /// LEGACY (main-context, synchronous): new callers on any hot path should
    /// use `updateTranscriptWithTranscriptionInBackground` (serial writer,
    /// ID-based) instead. Remaining callers mutate viewContext objects that are
    /// directly bound to UI (e.g. TranscriptionRetryController on a
    /// HistoryView-bound row).
    ///
    /// The @MainActor attribute ensures this runs on the main thread, which is critical because:
    /// 1. Core Data UI updates must happen on the main thread
    /// 2. This guarantees immediate UI refresh without threading delays
    ///
    /// - Parameters:
    ///   - transcript: The transcript to update (currently in "processing" state)
    ///   - transcribedText: The transcribed text (or error message if transcription failed)
    ///   - postProcessedText: The delivered text when it differs from the raw text (AI
    ///     post-processing, or with it off, filler removal, voice commands and
    ///     Vocabulary swaps); nil clears the field. See `TranscriptionResult.historyPostProcessedText`.
    ///
    /// Auto-refresh mechanism:
    /// 1. Updates the transcript's text and status from "processing" to "completed"
    /// 2. Calls save() which triggers the NSManagedObjectContextDidSave notification
    /// 3. processPendingChanges() forces immediate processing without waiting for the run loop
    /// 4. HistoryView receives the notification and refreshes its @FetchRequest results
    /// 5. The UI updates instantly to show the completed transcript
    @MainActor
    func updateTranscriptWithTranscription(_ transcript: Transcript, transcribedText: String, postProcessedText: String? = nil, transcriptionProvider: String? = nil, postProcessingProvider: String? = nil, wordTimestampsJSON: String? = nil) {
        // Update the transcript object with the final transcription
        // Use post-processed text if available, otherwise use raw transcribed text
        transcript.text = postProcessedText ?? transcribedText

        // Change status from "processing" to "completed"
        // This status change is what triggers the visual update in HistoryView
        transcript.setValue("completed", forKey: "status")

        // Store the raw transcribed text
        transcript.setValue(transcribedText, forKey: "transcribedText")

        // Store the delivered text. Always overwrite, including clearing to
        // nil: History, the Recording dialog and `GET /recordings/{id}` all
        // read `postProcessedText` before `text`, so a Retry whose new text
        // equals its raw text would otherwise keep showing the old run's
        // text (issue #1638).
        transcript.setValue(postProcessedText, forKey: "postProcessedText")

        // Store the transcription provider (e.g., "LibWhisper", "HyperWhisper Cloud", "OpenAI Whisper")
        if let provider = transcriptionProvider {
            transcript.setValue(provider, forKey: "transcriptionProvider")
        }

        // Store the post-processing provider. Always overwrite, including
        // clearing to nil, in step with `postProcessedText`: a Retry with AI
        // off (or an AI step that left the text unchanged) has no provider,
        // and keeping the old run's one would badge non-AI text as AI in
        // History and in `GET /recordings/{id}`.
        transcript.setValue(postProcessingProvider, forKey: "postProcessingProvider")

        // Store segment/word timestamps JSON blob (basis: raw_text). Always
        // overwrite — including clearing to nil when the caller supplies none.
        // Every call site here is a "transcription (re)completed" event that
        // replaces `transcribedText`, so any previously stored blob aligns to
        // the OLD text and is now stale. Only the in-app recording path passes a
        // fresh blob; all other paths (streaming, file, retry, re-process) leave
        // it nil, which must clear the old one rather than preserve a mismatch.
        transcript.setValue(wordTimestampsJSON, forKey: "wordTimestampsJSON")

        Self.collapseTransientTwins(of: transcript, in: container.viewContext, transcribedText: transcribedText)

        // Save changes and post notification for UI refresh
        save()

        // CRITICAL: Force immediate processing of pending changes
        // Without this, there could be a delay before the UI updates
        // This ensures the HistoryView refreshes instantly when transcription completes
        container.viewContext.processPendingChanges()

        // Re-fault all unchanged objects to prevent viewContext bloat
        // After hundreds of recordings, thousands of managed objects stay materialized
        // which degrades save performance. @FetchRequest transparently re-faults on next access.
        container.viewContext.refreshAllObjects()
    }

    private struct TrimmedPathUpdate: Sendable {
        /// The replaced trimmed file and its derived upload copy (#1513), when
        /// no transcript owns them any more. Empty when nothing is unowned.
        let unownedPreviousPaths: [String]
    }

    /// Sets the trimmed audio file path for a transcript on the serial writer.
    ///
    /// Auto-delete uses the same writer. If cleanup runs first, the row no
    /// longer exists and this method removes the new, unowned file. If this
    /// write runs first, cleanup snapshots the committed path before deleting
    /// the row. Both queue orders therefore leave no orphaned trimmed file.
    ///
    /// - Parameters:
    ///   - transcript: The transcript to update
    ///   - trimmedPath: The file path to the VAD-trimmed audio file
    @MainActor
    @discardableResult
    func setTrimmedAudioPath(_ transcript: Transcript, trimmedPath: String) async -> Bool {
        let transcriptID = transcript.objectID
        let update = await performWriteRequiringSave { context -> TrimmedPathUpdate? in
            guard let writerTranscript = try? context.existingObject(with: transcriptID) as? Transcript,
                  !writerTranscript.isDeleted else {
                return nil
            }

            let previousPath = writerTranscript.value(forKey: "trimmedAudioFilePath") as? String
            let originalPath = writerTranscript.audioFilePath
            writerTranscript.setValue(trimmedPath, forKey: "trimmedAudioFilePath")

            // A pre-upgrade retry can replace `<name>_trimmed.<source-ext>`
            // with the corrected collision-safe WAV path. Delete the prior
            // artifact only after no transcript owns it and the path is not the
            // original recording. The delete runs after this transaction saves.
            var unownedPreviousPaths: [String] = []
            if let previousPath,
               previousPath != trimmedPath,
               previousPath != originalPath {
                let ownersRequest: NSFetchRequest<Transcript> = Transcript.fetchRequest()
                ownersRequest.predicate = NSPredicate(
                    format: "trimmedAudioFilePath == %@ AND SELF != %@",
                    previousPath,
                    transcriptID
                )
                ownersRequest.fetchLimit = 1

                if let owners = try? context.count(for: ownersRequest), owners == 0 {
                    // The old trimmed WAV may also have a `.m4a` upload copy
                    // that no column records (#1513). The shared helper adds it
                    // unless a transcript (this one included, with its new
                    // path) still records or derives it.
                    unownedPreviousPaths = Self.audioFilePathsToDelete(
                        for: [TranscriptRecordedAudioPaths(audioFilePath: nil, trimmedAudioFilePath: previousPath)],
                        excluding: [],
                        in: context
                    )
                }
            }

            return TrimmedPathUpdate(unownedPreviousPaths: unownedPreviousPaths)
        }

        guard let update else {
            // The VAD output has no Core Data owner if the row was deleted or
            // the path save failed. Remove it instead of leaking it on disk.
            _ = await FileDeletion.deleteFiles(at: [trimmedPath])
            AppLogger.coreData.warning("Could not attach trimmed audio path; removed the unowned file")
            return false
        }

        if !update.unownedPreviousPaths.isEmpty {
            _ = await FileDeletion.deleteFiles(at: update.unownedPreviousPaths)
            AppLogger.coreData.info("Removed replaced trimmed audio artifact")
        }

        let trimmedFormat = AudioConstants.diagnosticAudioFormat(URL(fileURLWithPath: trimmedPath).pathExtension)
        AppLogger.coreData.debug("Set trimmed audio path · format=\(trimmedFormat, privacy: .public)")
        return true
    }

    /// Deletes a transcript
    /// - Parameter transcript: The transcript to delete
    func deleteTranscript(_ transcript: Transcript) {
        // DEFENSIVE CHECK: Ensure transcript has an ID before deletion
        // This prevents crashes in other parts of the code that track deletion state
        guard transcript.id != nil else {
            AppLogger.coreData.warning("Attempted to delete transcript with nil ID")
            return
        }
        
        let context = container.viewContext

        // DELETE AUDIO FILES FROM DISK:
        // The original, the VAD-trimmed file, and the unrecorded files derived
        // from them (#1513) — one list shared with bulk delete and auto-delete.
        for path in Self.audioFilePathsToDelete(for: [transcript], in: context) {
            removeTranscriptAudioFile(atPath: path)
        }

        Self.stopRetries(ofDeleted: [transcript.id].compactMap { $0 })
        context.delete(transcript)
        save()
    }
    
    /// Deletes multiple transcripts
    /// - Parameter transcripts: Set of transcripts to delete
    func deleteTranscripts(_ transcripts: Set<Transcript>) {
        let context = container.viewContext
        
        // DEFENSIVE CHECK: Only delete transcripts with valid IDs
        // This prevents crashes in other parts of the code that track deletion state
        let validTranscripts = transcripts.filter { transcript in
            if transcript.id == nil {
                AppLogger.coreData.warning("Skipping deletion of transcript with nil ID")
                return false
            }
            return true
        }
        
        // DELETE AUDIO FILES FROM DISK:
        // Every file of every selected transcript: original, trimmed, and the
        // unrecorded files derived from them (#1513). Collected for the whole
        // selection before any row is deleted, so the ownership check sees
        // exactly the transcripts that survive.
        let transcriptsToDelete = Array(validTranscripts)
        for path in Self.audioFilePathsToDelete(for: transcriptsToDelete, in: context) {
            removeTranscriptAudioFile(atPath: path)
        }
        Self.stopRetries(ofDeleted: transcriptsToDelete.compactMap(\.id))
        transcriptsToDelete.forEach { context.delete($0) }
        save()
    }

    /// Removes one transcript audio file if it exists, logging the outcome.
    private func removeTranscriptAudioFile(atPath path: String) {
        guard FileManager.default.fileExists(atPath: path) else { return }
        do {
            try FileManager.default.removeItem(atPath: path)
            AppLogger.coreData.info("Deleted audio file: \(path, privacy: .public)")
        } catch {
            AppLogger.coreData.error("Failed to delete audio file: \(error.localizedDescription, privacy: .public)")
        }
    }
    
    // MARK: - Mode Operations
    
    /// Initializes the default mode in Core Data if none exist
    /// Called on app startup to ensure a default mode is available
    ///
    /// WHAT the mode is lives in the shared Rust core (`hw-catalog::mode_seed`,
    /// reached through `SeededModeValues`), so macOS, Windows and the
    /// Linux/portable head all create the same single mode on a fresh install
    /// and there is no longer a definition to keep in sync. This method only
    /// owns WHEN to write it.
    ///
    /// Internal rather than private so `DefaultModeSeedTests` can drive it
    /// against an in-memory store — `init` (the only caller) skips it when
    /// `inMemory` is true, so a test has to call it explicitly.
    func initializeDefaultModes() {
        let context = container.viewContext
        
        // Check if any modes exist
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.fetchLimit = 1
        
        do {
            let count = try context.count(for: request)
            if count > 0 {
                // Modes already exist, no need to initialize
                // This preserves existing users' modes unchanged.
                //
                // Seeding is not the same question as the invariant, though: a
                // store can arrive with two default modes or none — a backup
                // restored from another machine is the realistic route — and
                // this early return is exactly why nothing used to repair it
                // (issue #536).
                enforceDefaultModeInvariant()
                return
            }
        } catch {
            AppLogger.coreData.error("Failed to check for existing modes: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to check for existing modes", tags: ["component": "PersistenceController", "operation": "checkModes"])
            return
        }
        
        // NEW INSTALLS ONLY: exactly ONE mode, defined by the shared core.
        //
        // The well-known UUID, the name, the language, the HyperWhisper Cloud
        // post-processing provider and the two catalog-resolved model ids all
        // come from `hw-catalog::mode_seed` — not from literals here — so the
        // three heads cannot drift again. The spelling variant inside it is
        // derived from the system region, so a first install in e.g. the UK
        // opens on "British" instead of making the user change every mode by
        // hand; an unknown region keeps the historical American.
        //
        // WHAT to write lives in `seedDefaultMode()`; this method owns only
        // WHEN. The save below is kept here rather than in the helper because
        // its diagnostics and the `didSeedDefaultModesOnLaunch` flag are about
        // first launch specifically, and onboarding's use of the helper is not
        // a first launch.
        let mode = seedDefaultMode(persist: false)
        let seededName = mode.name ?? ""

        // Save the default mode
        do {
            try context.save()
            didSeedDefaultModesOnLaunch = true
            AppLogger.coreData.info("Initialized the default mode for new install: \(seededName, privacy: .public)")
        } catch {
            AppLogger.logCoreData(
                .save(site: "initialize_default_modes", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: error,
                metadata: CoreDataSaveDiagnostics.contextShape(context)
            )
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
