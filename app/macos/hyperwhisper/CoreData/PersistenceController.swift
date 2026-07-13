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

// MARK: - Mode Snapshot

/// Thread-safe, value-type copy of the Mode properties needed for background
/// validation during recording start. Core Data managed objects are tied to
/// their context's queue, so this snapshot allows safe access from any thread.
struct ModeSnapshot: Sendable {
    let id: UUID
    let name: String
    let model: String
    let cloudProvider: String
    let rawCloudProvider: String?
    let postProcessingMode: Int16
    let postProcessingProvider: String
    let rawPostProcessingProvider: String?
    let languageModel: String?
    let enableScreenOCR: Bool
    let sortOrder: Int16

    init(_ mode: Mode) {
        self.id = mode.id ?? UUID()
        self.name = mode.name ?? "Default"
        self.model = mode.model ?? "base"
        self.cloudProvider = mode.cloudProvider ?? "hyperwhisper"
        self.rawCloudProvider = mode.cloudProvider
        self.postProcessingMode = mode.postProcessingMode
        self.postProcessingProvider = mode.postProcessingProvider ?? "hyperwhisper"
        self.rawPostProcessingProvider = mode.postProcessingProvider
        self.languageModel = mode.languageModel
        self.enableScreenOCR = mode.enableScreenOCR
        self.sortOrder = mode.sortOrder
    }
}

// MARK: - Vocabulary Entry Snapshot

/// Thread-safe, value-type copy of a Vocabulary entry for use on the recording
/// hot path. Core Data managed objects are tied to their context's queue, so
/// this snapshot allows safe access from streaming callbacks on any thread.
struct VocabularyEntrySnapshot: Sendable {
    let word: String
    let replacement: String?
}

// MARK: - Persistence Controller

/// Manages the Core Data stack and provides access to the managed object context
class PersistenceController: ObservableObject {
    
    // MARK: - Shared Instance
    
    /// Singleton instance for app-wide access
    static let shared = PersistenceController()
    
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
        container = NSPersistentCloudKitContainer(name: "HyperWhisper")

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
                // Send critical error to Sentry before crashing
                SentryService.capture(error: error, message: "Critical: Core Data failed to load", extras: ["userInfo": "\(error.userInfo)", "store": storeDescription.configuration ?? "unknown"], tags: ["component": "PersistenceController", "operation": "loadPersistentStores", "severity": "fatal"])
                // In production, this should be handled more gracefully
                // Consider showing an alert to the user or attempting recovery
                fatalError("Core Data failed to load: \(error), \(error.userInfo)")
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
            repairBrokenLocalModesOnLaunch()
            repairStaleProcessingTranscriptsOnLaunch()
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
        let tempContainer = NSPersistentContainer(name: "HyperWhisper")
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
            AppLogger.logCoreData(.save, error: nsError)
            SentryService.capture(
                error: nsError,
                message: "Failed to normalize cloud route identifiers",
                tags: ["component": "PersistenceController", "operation": "normalizeCloudRouteIdentifiers"]
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
            AppLogger.logCoreData(.save, error: nsError)
            SentryService.capture(
                error: nsError,
                message: "Failed to normalize cloud provider values",
                tags: ["component": "PersistenceController", "operation": "normalizeCloudProvider"]
            )
        }
    }

    // MARK: - Broken Local Post-Processing Mode Repair

    /// Outcome of a `repairBrokenLocalModes` pass.
    struct LocalModeRepairResult {
        /// Names of modes whose post-processing was silently turned off.
        var disabledModeNames: [String] = []
        /// Model ids referenced by modes that were LEFT as `.local` but whose
        /// weights aren't downloaded yet (capable hardware, restore flow only) —
        /// candidates for a batched "Download all" prompt.
        var pendingDownloadModelIds: Set<String> = []
    }

    /// Silently repairs broken on-device (`.local`) post-processing modes by turning
    /// post-processing OFF (`postProcessingMode = 0`). We NEVER substitute a cloud
    /// LLM — on failure the user's text is delivered raw or post-processing is off,
    /// never silently routed to the cloud.
    ///
    /// A `.local` mode is broken when the machine can't run the local runtime
    /// (Intel), it has no model selected, or its selected model isn't usable. On
    /// capable hardware running under Rosetta the mode is LEFT intact (a native
    /// relaunch fixes it — see `SystemCapability.needsNativeRelaunch`).
    ///
    /// - Parameters:
    ///   - capability: hardware/runtime capability — drives the Intel gate.
    ///   - isCataloged: true if a model id is a known downloadable local model.
    ///   - isDownloaded: true if a model id's weights are present and usable on disk.
    ///   - keepPendingDownloads: when true (restore on capable hardware), a `.local`
    ///     mode whose model is cataloged-but-not-downloaded is LEFT as `.local` and
    ///     reported in `pendingDownloadModelIds` instead of being turned off. When
    ///     false (launch), such a mode is turned off like any other missing model.
    @discardableResult
    func repairBrokenLocalModes(
        capability: SystemCapability,
        isCataloged: (String) -> Bool,
        isDownloaded: (String) -> Bool,
        keepPendingDownloads: Bool
    ) -> LocalModeRepairResult {
        var result = LocalModeRepairResult()
        let context = container.viewContext
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()

        let localRawValue = PostProcessingMode.local.rawValue
        let offRawValue = PostProcessingMode.off.rawValue

        func disable(_ mode: Mode, reason: String) {
            let name = mode.name ?? "Untitled"
            mode.postProcessingMode = offRawValue
            result.disabledModeNames.append(name)
            // Greppable support-visibility line (there is no UI signal for a silent repair).
            AppLogger.coreData.info("PP disabled for Mode \"\(name, privacy: .public)\": \(reason, privacy: .public)")
        }

        do {
            let modes = try context.fetch(request)
            for mode in modes where mode.postProcessingMode == localRawValue {
                // Hardware gate first — on Intel never keep/offer a local mode.
                guard capability.isAppleSiliconHardware else {
                    disable(mode, reason: "unsupported hardware (Intel)")
                    continue
                }
                let modelId = (mode.languageModel ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                guard !modelId.isEmpty else {
                    disable(mode, reason: "no local model selected")
                    continue
                }
                if isDownloaded(modelId) {
                    continue // healthy — model present and usable
                }
                // Not downloaded. On restore (capable HW) keep the intent for a
                // cataloged model and offer a re-download; otherwise turn it off.
                if keepPendingDownloads, isCataloged(modelId) {
                    result.pendingDownloadModelIds.insert(modelId)
                    continue
                }
                disable(mode, reason: isCataloged(modelId) ? "model not downloaded" : "model not available")
            }

            if context.hasChanges {
                try context.save()
            }
        } catch {
            let nsError = error as NSError
            AppLogger.logCoreData(.save, error: nsError)
            SentryService.capture(
                error: nsError,
                message: "Failed to repair broken local post-processing modes",
                tags: ["component": "PersistenceController", "operation": "repairBrokenLocalModes"]
            )
        }

        return result
    }

    /// Filesystem-backed `isDownloaded` check for the launch/restore repair: a model
    /// is "present" when its GGUF file exists on disk. (Model id == GGUF filename.)
    /// Checksum validity isn't known synchronously at launch — an invalid file that
    /// exists is caught later by the mode editor and never causes data loss because
    /// the streaming fallback always returns the raw transcript.
    static func localModelFileExists(_ modelId: String) -> Bool {
        let url = LocalModelManager.modelsDirectory.appendingPathComponent(modelId)
        return FileManager.default.fileExists(atPath: url.path)
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
            AppLogger.logCoreData(.save, error: nsError)
            SentryService.capture(
                error: nsError,
                message: "Failed to repair stale processing transcripts on launch",
                tags: ["component": "PersistenceController", "operation": "repairStaleProcessingTranscripts"]
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
            AppLogger.logCoreData(.save, error: nsError)
            SentryService.capture(
                error: nsError,
                message: "Failed to migrate removed Deepgram models on Modes",
                tags: ["component": "PersistenceController", "operation": "migrateRemovedDeepgramModels"]
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

    // MARK: - Save Operations
    
    /// Saves the view context if there are changes
    /// Core Data automatically posts NSManagedObjectContextDidSave notification when saving
    func save() {
        let context = container.viewContext
        
        // Early return if no changes to save (performance optimization)
        guard context.hasChanges else { return }
        
        do {
            // Persist changes to the Core Data store
            // Core Data automatically posts notification
            try context.save()
        } catch {
            // In production, handle this error appropriately
            let nsError = error as NSError
            AppLogger.logCoreData(.save, error: nsError)
            SentryService.capture(error: nsError, message: "Core Data save failed", tags: ["component": "PersistenceController", "operation": "save"])
        }
    }
    
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
        let outcome = await performWriteReportingSave(block)
        return outcome.saved ? outcome.value : nil
    }

    private func performWriteReportingSave<T: Sendable>(_ block: @escaping (NSManagedObjectContext) -> T) async -> (value: T, saved: Bool) {
        let context = writerContext
        let result: (value: T, saved: Bool) = await context.perform {
            let value = block(context)
            var saved = true
            if context.hasChanges {
                do {
                    try context.save()
                } catch {
                    saved = false
                    let nsError = error as NSError
                    AppLogger.logCoreData(.save, error: nsError)
                    SentryService.capture(
                        error: nsError,
                        message: "Core Data background write failed",
                        tags: ["component": "PersistenceController", "operation": "performWrite"]
                    )
                    // Never leave the long-lived writer context poisoned.
                    context.rollback()
                }
                // Re-fault so the long-lived writer doesn't accumulate objects.
                context.refreshAllObjects()
            }
            return (value, saved)
        }
        await MainActor.run { self.scheduleViewContextMaintenance() }
        return result
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
    /// Mirrors `createProcessingTranscript`'s idempotency guard, and additionally
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
            let normalizedPath = audioFilePath?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""

            var reused: Transcript?
            if !normalizedPath.isEmpty {
                let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
                request.predicate = NSPredicate(
                    format: "status == %@ AND audioFilePath == %@",
                    "processing",
                    normalizedPath
                )
                request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]

                if let existing = try? context.fetch(request), let primary = existing.first {
                    if existing.count > 1 {
                        for duplicate in existing.dropFirst() {
                            context.delete(duplicate)
                        }
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
                    reused = primary
                }
            }

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
    /// and near-now dedup as the `@MainActor` variant, but no `processPendingChanges()`
    /// and no view-context `refreshAllObjects()` — the merge notification refreshes
    /// HistoryView, and maintenance is debounced off the hot path.
    func updateTranscriptWithTranscriptionInBackground(
        transcriptID: NSManagedObjectID,
        transcribedText: String,
        postProcessedText: String? = nil,
        transcriptionProvider: String? = nil,
        postProcessingProvider: String? = nil,
        wordTimestampsJSON: String? = nil
    ) async {
        await performWrite { context in
            guard let transcript = try? context.existingObject(with: transcriptID) as? Transcript else {
                AppLogger.coreData.warning("updateTranscriptWithTranscriptionInBackground: transcript row not found (cancel race?) — skipping")
                return
            }

            transcript.text = postProcessedText ?? transcribedText
            transcript.setValue("completed", forKey: "status")
            transcript.setValue(transcribedText, forKey: "transcribedText")
            if let postProcessedText {
                transcript.setValue(postProcessedText, forKey: "postProcessedText")
            }
            if let transcriptionProvider {
                transcript.setValue(transcriptionProvider, forKey: "transcriptionProvider")
            }
            if let postProcessingProvider {
                transcript.setValue(postProcessingProvider, forKey: "postProcessingProvider")
            }
            // Always overwrite (including clearing to nil) — see the @MainActor variant.
            transcript.setValue(wordTimestampsJSON, forKey: "wordTimestampsJSON")

            // Collapse transient near-now duplicates for the same audio path.
            if let rawPath = transcript.audioFilePath {
                let audioPath = rawPath.trimmingCharacters(in: .whitespacesAndNewlines)
                if !audioPath.isEmpty {
                    let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
                    request.predicate = NSPredicate(format: "SELF != %@ AND audioFilePath == %@", transcript.objectID, audioPath)

                    if let candidates = try? context.fetch(request) {
                        let anchorDate = transcript.date ?? Date()
                        let anchorDuration = transcript.duration
                        let anchorText = transcript.text ?? ""

                        for candidate in candidates {
                            let status = candidate.value(forKey: "status") as? String ?? ""
                            guard status == "processing" || status == "completed" else { continue }

                            let candidateDate = candidate.date ?? .distantPast
                            let isNearInTime = abs(candidateDate.timeIntervalSince(anchorDate)) <= 20
                            let isNearInDuration = abs(candidate.duration - anchorDuration) <= 0.5
                            let candidateRawText = candidate.value(forKey: "transcribedText") as? String ?? ""
                            let sameText = (candidate.text ?? "") == anchorText || candidateRawText == transcribedText

                            if isNearInTime && (isNearInDuration || sameText) {
                                context.delete(candidate)
                                AppLogger.coreData.warning("Removed transient duplicate transcript for path: \(audioPath, privacy: .public)")
                            }
                        }
                    }
                }
            }
        }
    }

    /// Mark a transcript failed on the serial writer.
    func markTranscriptFailedInBackground(
        transcriptID: NSManagedObjectID,
        failedReason: String,
        errorText: String
    ) async {
        await performWrite { context in
            guard let transcript = try? context.existingObject(with: transcriptID) as? Transcript else {
                AppLogger.coreData.warning("markTranscriptFailedInBackground: transcript row not found — skipping")
                return
            }
            transcript.setValue("failed", forKey: "status")
            transcript.setValue(failedReason, forKey: "failedReason")
            transcript.text = errorText
        }
    }

    /// Finalize a recording session on stop in ONE serial write — collapses the
    /// old `updateRecordingSessionOnStop` + audioFormat mutation + save.
    func updateRecordingSessionOnStopInBackground(
        sessionID: NSManagedObjectID,
        audioFilePath: String,
        duration: TimeInterval,
        audioFormat: String
    ) async {
        await performWrite { context in
            guard let session = try? context.existingObject(with: sessionID) as? RecordingSession else {
                AppLogger.coreData.warning("updateRecordingSessionOnStopInBackground: session row not found — skipping")
                return
            }
            session.audioFilePath = audioFilePath
            session.durationInSeconds = duration
            session.endTime = Date()
            session.audioFormat = audioFormat
        }
    }

    /// Update a transcript's audio file path (and its recording session's path)
    /// on the serial writer — used by the background M4A conversion path.
    func updateTranscriptAudioFilePathInBackground(
        transcriptID: NSManagedObjectID,
        newPath: String
    ) async {
        await performWrite { context in
            guard let transcript = try? context.existingObject(with: transcriptID) as? Transcript else {
                AppLogger.coreData.warning("updateTranscriptAudioFilePathInBackground: transcript row not found — skipping")
                return
            }
            transcript.audioFilePath = newPath
            if let session = transcript.recordingSession {
                session.audioFilePath = newPath
            }
        }
    }

    /// Delete a recording session on the serial writer, then remove its audio file
    /// off the main thread. Reads the path inside the writer so nothing crosses a
    /// context boundary.
    func deleteRecordingSessionInBackground(
        sessionID: NSManagedObjectID,
        deleteAudioFile: Bool = true
    ) async {
        let filePathToRemove: String? = await performWrite { context -> String? in
            guard let session = try? context.existingObject(with: sessionID) as? RecordingSession else {
                AppLogger.coreData.debug("deleteRecordingSessionInBackground: session row not found — skipping")
                return nil
            }
            let path = deleteAudioFile ? session.audioFilePath : nil
            context.delete(session)
            return path
        }

        if let filePathToRemove {
            do {
                try FileManager.default.removeItem(atPath: filePathToRemove)
                AppLogger.audio.debug("Deleted incomplete audio file: \(filePathToRemove, privacy: .public)")
            } catch {
                AppLogger.audio.debug("Could not delete incomplete audio file (may not exist): \(error.localizedDescription)")
            }
        }
    }

    // MARK: - Transcript Operations

    /// Creates a new transcript
    /// - Parameters:
    ///   - text: The transcribed text
    ///   - duration: Recording duration in seconds
    ///   - mode: The transcription mode used
    ///   - audioFilePath: Optional path to the audio file
    /// - Returns: The created transcript
    @discardableResult
    func createTranscript(
        text: String,
        duration: TimeInterval,
        mode: String? = nil,
        audioFilePath: String? = nil
    ) -> Transcript {
        let context = container.viewContext
        
        let transcript = Transcript(context: context)
        transcript.id = UUID()
        transcript.text = text
        transcript.date = Date()
        transcript.duration = duration  // Duration in seconds
        transcript.mode = mode
        transcript.audioFilePath = audioFilePath
        transcript.setValue("completed", forKey: "status")
        transcript.setValue(text, forKey: "transcribedText")
        
        save()
        
        return transcript
    }
    
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
            let normalizedPath = audioFilePath?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""

            // Idempotency guard:
            // If a processing transcript already exists for the same audio file, reuse it
            // instead of creating duplicates. Also collapse any accidental duplicates.
            if !normalizedPath.isEmpty {
                let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
                request.predicate = NSPredicate(
                    format: "status == %@ AND audioFilePath == %@",
                    "processing",
                    normalizedPath
                )
                request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]

                if let existing = try? context.fetch(request), let primary = existing.first {
                    if existing.count > 1 {
                        for duplicate in existing.dropFirst() {
                            context.delete(duplicate)
                        }
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
                    transcript = primary
                    return
                }
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
    ///   - postProcessedText: Optional AI-enhanced version of the text
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

        // Store the post-processed text if available
        if let postProcessedText = postProcessedText {
            transcript.setValue(postProcessedText, forKey: "postProcessedText")
        }

        // Store the transcription provider (e.g., "LibWhisper", "HyperWhisper Cloud", "OpenAI Whisper")
        if let provider = transcriptionProvider {
            transcript.setValue(provider, forKey: "transcriptionProvider")
        }

        // Store the post-processing provider if available
        if let provider = postProcessingProvider {
            transcript.setValue(provider, forKey: "postProcessingProvider")
        }

        // Store segment/word timestamps JSON blob (basis: raw_text). Always
        // overwrite — including clearing to nil when the caller supplies none.
        // Every call site here is a "transcription (re)completed" event that
        // replaces `transcribedText`, so any previously stored blob aligns to
        // the OLD text and is now stale. Only the in-app recording path passes a
        // fresh blob; all other paths (streaming, file, retry, re-process) leave
        // it nil, which must clear the old one rather than preserve a mismatch.
        transcript.setValue(wordTimestampsJSON, forKey: "wordTimestampsJSON")

        // Collapse transient duplicates:
        // In rare stop/transcribe race conditions, near-identical entries can be created
        // for the same audio path. Remove only "near-now" twins to avoid touching
        // legitimate historical retranscriptions of the same file.
        if let rawPath = transcript.audioFilePath {
            let audioPath = rawPath.trimmingCharacters(in: .whitespacesAndNewlines)
            if !audioPath.isEmpty {
                let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
                request.predicate = NSPredicate(format: "SELF != %@ AND audioFilePath == %@", transcript.objectID, audioPath)

                if let candidates = try? container.viewContext.fetch(request) {
                    let anchorDate = transcript.date ?? Date()
                    let anchorDuration = transcript.duration
                    let anchorText = transcript.text ?? ""

                    for candidate in candidates {
                        let status = candidate.value(forKey: "status") as? String ?? ""
                        guard status == "processing" || status == "completed" else { continue }

                        let candidateDate = candidate.date ?? .distantPast
                        let isNearInTime = abs(candidateDate.timeIntervalSince(anchorDate)) <= 20
                        let isNearInDuration = abs(candidate.duration - anchorDuration) <= 0.5
                        let candidateRawText = candidate.value(forKey: "transcribedText") as? String ?? ""
                        let sameText = (candidate.text ?? "") == anchorText || candidateRawText == transcribedText

                        if isNearInTime && (isNearInDuration || sameText) {
                            container.viewContext.delete(candidate)
                            AppLogger.coreData.warning("Removed transient duplicate transcript for path: \(audioPath, privacy: .public)")
                        }
                    }
                }
            }
        }

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

    /// Sets the trimmed audio file path for a transcript
    /// Called after VAD (Voice Activity Detection) processing creates a trimmed version
    ///
    /// - Parameters:
    ///   - transcript: The transcript to update
    ///   - trimmedPath: The file path to the VAD-trimmed audio file
    @MainActor
    func setTrimmedAudioPath(_ transcript: Transcript, trimmedPath: String) {
        transcript.setValue(trimmedPath, forKey: "trimmedAudioFilePath")
        save()
        AppLogger.coreData.debug("Set trimmed audio path for transcript: \(trimmedPath, privacy: .public)")
    }

    /// Finds the most recent processing transcript
    /// - Returns: The most recent transcript with processing status, if any
    func findMostRecentProcessingTranscript() -> Transcript? {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(format: "status == %@", "processing")
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]
        request.fetchLimit = 1

        do {
            let results = try container.viewContext.fetch(request)
            return results.first
        } catch {
            AppLogger.coreData.error("Failed to fetch processing transcript: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch processing transcript", tags: ["component": "PersistenceController", "operation": "fetchProcessingTranscript"])
            return nil
        }
    }

    /// Finds the most recent failed transcript
    /// - Returns: The most recent transcript with failed status, if any
    func findMostRecentFailedTranscript() -> Transcript? {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(format: "status == %@", "failed")
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]
        request.fetchLimit = 1

        do {
            let results = try container.viewContext.fetch(request)
            return results.first
        } catch {
            AppLogger.coreData.error("Failed to fetch failed transcript: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch failed transcript", tags: ["component": "PersistenceController", "operation": "fetchFailedTranscript"])
            return nil
        }
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
        
        // DELETE AUDIO FILES FROM DISK:
        // Remove the original audio file if it exists
        if let audioFilePath = transcript.audioFilePath,
           FileManager.default.fileExists(atPath: audioFilePath) {
            do {
                try FileManager.default.removeItem(atPath: audioFilePath)
                AppLogger.coreData.info("Deleted audio file: \(audioFilePath, privacy: .public)")
            } catch {
                AppLogger.coreData.error("Failed to delete audio file: \(error.localizedDescription, privacy: .public)")
            }
        }

        // DELETE TRIMMED AUDIO FILE FROM DISK:
        // Remove the VAD-trimmed audio file if it exists
        // This prevents orphaned trimmed files from accumulating
        if let trimmedPath = transcript.value(forKey: "trimmedAudioFilePath") as? String,
           FileManager.default.fileExists(atPath: trimmedPath) {
            do {
                try FileManager.default.removeItem(atPath: trimmedPath)
                AppLogger.coreData.info("Deleted trimmed audio file: \(trimmedPath, privacy: .public)")
            } catch {
                AppLogger.coreData.error("Failed to delete trimmed audio file: \(error.localizedDescription, privacy: .public)")
            }
        }

        let context = container.viewContext
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
        // Remove associated audio files for each transcript (both original and trimmed)
        validTranscripts.forEach { transcript in
            // Delete original audio file
            if let audioFilePath = transcript.audioFilePath,
               FileManager.default.fileExists(atPath: audioFilePath) {
                do {
                    try FileManager.default.removeItem(atPath: audioFilePath)
                    AppLogger.coreData.info("Deleted audio file: \(audioFilePath, privacy: .public)")
                } catch {
                    AppLogger.coreData.error("Failed to delete audio file: \(error.localizedDescription, privacy: .public)")
                }
            }
            // Delete trimmed audio file (VAD-processed version)
            if let trimmedPath = transcript.value(forKey: "trimmedAudioFilePath") as? String,
               FileManager.default.fileExists(atPath: trimmedPath) {
                do {
                    try FileManager.default.removeItem(atPath: trimmedPath)
                    AppLogger.coreData.info("Deleted trimmed audio file: \(trimmedPath, privacy: .public)")
                } catch {
                    AppLogger.coreData.error("Failed to delete trimmed audio file: \(error.localizedDescription, privacy: .public)")
                }
            }
            context.delete(transcript)
        }
        save()
    }
    
    /// Updates a transcript's text
    /// - Parameters:
    ///   - transcript: The transcript to update
    ///   - newText: The new text content
    func updateTranscriptText(_ transcript: Transcript, newText: String) {
        // DEFENSIVE CHECK: Ensure transcript has an ID
        // This helps maintain data integrity
        guard transcript.id != nil else {
            AppLogger.coreData.warning("Attempted to update transcript with nil ID")
            return
        }

        transcript.text = newText
        save()
    }

    /// Updates a transcript's audio file path
    ///
    /// **Purpose:**
    /// Used after background WAV→M4A conversion to update the transcript's
    /// audioFilePath to point to the new M4A file.
    ///
    /// **When Called:**
    /// After successful background M4A conversion in RecordingLifecycle,
    /// which happens post-transcription when storeAsM4A setting is enabled.
    ///
    /// - Parameters:
    ///   - transcript: The transcript to update
    ///   - newPath: The new audio file path (typically the M4A file path)
    @MainActor
    func updateTranscriptAudioFilePath(_ transcript: Transcript, newPath: String) {
        // DEFENSIVE CHECK: Ensure transcript has an ID
        guard transcript.id != nil else {
            AppLogger.coreData.warning("Attempted to update transcript audioFilePath with nil ID")
            return
        }

        transcript.audioFilePath = newPath

        // Also update the associated RecordingSession if it exists
        if let session = transcript.recordingSession {
            session.audioFilePath = newPath
        }

        save()
        AppLogger.coreData.debug("Updated transcript audio file path to: \(newPath, privacy: .public)")
    }

    // MARK: - Mode Operations
    
    /// Initializes default modes in Core Data if none exist
    /// Called on app startup to ensure default modes are available
    private func initializeDefaultModes() {
        let context = container.viewContext
        
        // Check if any modes exist
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.fetchLimit = 1
        
        do {
            let count = try context.count(for: request)
            if count > 0 {
                // Modes already exist, no need to initialize
                // This preserves existing users' modes unchanged
                return
            }
        } catch {
            AppLogger.coreData.error("Failed to check for existing modes: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to check for existing modes", tags: ["component": "PersistenceController", "operation": "checkModes"])
            return
        }
        
        // NEW INSTALLS ONLY: Create default mode with HyperWhisperCloud
        // Using well-known UUID for stable identification
        let defaultModes = [
            (
                name: "Default",
                preset: "hyper",
                id: UUID(uuidString: "00000000-0000-0000-0000-000000000001")!,
                model: "cloud",
                isDefault: true,
                sortOrder: 0,
                postProcessingMode: Int16(1),  // Cloud post-processing
                cloudProvider: "hyperwhisper",  // Use HyperWhisperCloud by default
                postProcessingProvider: "hyperwhisper"  // HyperWhisperCloud handles post-processing
            )
        ]
        
        for modeData in defaultModes {
            let mode = Mode(context: context)
            mode.id = modeData.id
            mode.name = modeData.name
            mode.preset = modeData.preset
            mode.language = "en"
            mode.model = modeData.model
            mode.punctuation = true
            mode.capitalization = true
            mode.profanityFilter = false
            mode.isDefault = modeData.isDefault
            mode.isSystemProvided = true
            mode.createdDate = Date()
            mode.modifiedDate = Date()
            mode.sortOrder = Int16(modeData.sortOrder)
            mode.customInstructions = ""
            mode.postProcessingMode = modeData.postProcessingMode
            mode.postProcessingProvider = modeData.postProcessingProvider
            mode.cloudProvider = modeData.cloudProvider
            mode.cloudAccuracyTier = CloudAccuracyTier.elevenLabsScribeV2.rawValue
            // Seed the tier's own default model (Scribe v2) explicitly. Without this the
            // attribute inherits the Core Data default `whisper-1`, a stale BYOK id that
            // isn't valid for the ElevenLabs tier — the provider would silently fall back,
            // but the stored value would be misleading.
            mode.cloudTranscriptionModel = CloudAccuracyTier.elevenLabsScribeV2.defaultModelId
            mode.cloudPostProcessingModel = CloudPostProcessingModel.claudeHaiku.rawValue
        }
        
        // Save the default modes
        do {
            try context.save()
            AppLogger.coreData.info("Initialized \(defaultModes.count, privacy: .public) default modes for new install")
        } catch {
            AppLogger.logCoreData(.save, error: error)
            SentryService.capture(error: error, message: "Failed to initialize default modes", tags: ["component": "PersistenceController", "operation": "initializeModes"])
        }
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
    func resolveTranscriptionModeInBackground(
        id: String,
        fallbackName: String,
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
        foreignPlatformExtensions: String? = nil
    ) -> Mode {
        let context = container.viewContext
        
        // Check if mode exists (for update)
        var mode: Mode?
        if let id = id {
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
            let maxSortOrder = fetchAllModes().map { $0.sortOrder }.max() ?? 0
            mode?.sortOrder = maxSortOrder + 1
        }
        
        // Update mode properties
        mode?.name = name
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
            mode?.userSystemPrompt = String(trimmedUserPrompt.prefix(2000))
        }

        let modeEnum = PostProcessingMode(rawValue: postProcessingMode) ?? .cloud
        let normalizedLanguageModel: String
        if modeEnum == .local {
            normalizedLanguageModel = languageModel ?? PostProcessingProvider.localLLM.defaultModel
        } else {
            normalizedLanguageModel = languageModel ?? "gpt-4.1-nano"
        }

        mode?.languageModel = normalizedLanguageModel
        mode?.cloudProvider = cloudProvider ?? "hyperwhisper"
        // cloudTranscriptionModel is assigned below, after the accuracy tier is
        // resolved, so an omitted model derives from the resolved tier's catalog
        // default (e.g. scribe_v2) instead of persisting a stale "whisper-1".
        mode?.postProcessingMode = postProcessingMode

        if modeEnum == .local {
            mode?.postProcessingProvider = PostProcessingProvider.localLLM.rawValue
        } else if let provider = postProcessingProvider {
            mode?.postProcessingProvider = provider
        } else {
            mode?.postProcessingProvider = modeEnum.defaultProvider?.rawValue ?? "hyperwhisper"
        }
        mode?.englishSpelling = englishSpelling ?? "american"
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
        let resolvedCloudProvider = CloudProvider(rawValue: mode?.cloudProvider ?? "hyperwhisper") ?? .hyperwhisper
        mode?.cloudTranscriptionModel = cloudTranscriptionModel
            ?? (resolvedCloudProvider == .hyperwhisper
                ? (CloudAccuracyTier(rawValue: resolvedAccuracyTier)?.defaultModelId ?? "")
                : CloudTranscriptionModels.defaultModel(for: resolvedCloudProvider))
        mode?.removeTrailingPeriod = removeTrailingPeriod
        mode?.enableScreenOCR = enableScreenOCR
        // Preserve a foreign (non-macOS) per-mode platformExtensions blob captured
        // on a v2 import (H4). Only assign when explicitly provided so an unrelated
        // GUI/API mode edit (which passes nil) never wipes a stored foreign slice.
        if let foreignPlatformExtensions {
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

        save()

        return mode!
    }
    
    /// Deletes a mode
    /// - Parameter mode: The mode to delete
    /// - Note: The caller is responsible for ensuring at least one mode remains
    func deleteMode(_ mode: Mode) {
        let context = container.viewContext
        context.delete(mode)
        save()
    }
    
    // MARK: - RecordingSession Operations
    
    /// Creates a new recording session
    /// - Parameters:
    ///   - deviceId: The audio device ID
    ///   - deviceName: The audio device name
    ///   - sampleRate: Audio sample rate
    ///   - channelCount: Number of audio channels
    ///   - audioFormat: Audio format description
    /// - Returns: The created recording session
    @discardableResult
    func createRecordingSession(
        deviceId: String,
        deviceName: String,
        sampleRate: Double,
        channelCount: Int16,
        audioFormat: String
    ) -> RecordingSession {
        let context = container.viewContext
        
        let session = RecordingSession(context: context)
        session.id = UUID()
        session.startTime = Date()
        session.deviceId = deviceId
        session.deviceName = deviceName
        session.sampleRate = sampleRate
        session.channelCount = channelCount
        session.audioFormat = audioFormat
        session.status = "recording"
        session.retryCount = 0
        
        save()
        
        return session
    }
    
    /// Updates a recording session with transcription result
    /// - Parameters:
    ///   - session: The recording session to update
    ///   - transcript: The resulting transcript (if successful)
    ///   - error: Error message (if failed)
    ///   - retryCount: Number of retry attempts made
    func updateRecordingSessionWithResult(
        _ session: RecordingSession,
        transcript: Transcript? = nil,
        error: String? = nil,
        retryCount: Int16 = 0
    ) {
        session.retryCount = retryCount
        
        if let transcript = transcript {
            session.transcript = transcript
            session.status = "completed"
        } else if let error = error {
            session.errorMessage = error
            session.status = "failed"
        }
        
        save()
    }
    
    // MARK: - Vocabulary Operations
    
    /// Adds a new vocabulary item to Core Data
    /// - Parameters:
    ///   - word: The word/phrase to recognize
    ///   - replacement: Optional replacement text
    /// - Returns: True if added successfully, false if duplicate
    func addVocabularyItem(
        word: String,
        replacement: String?,
        excludingId: UUID? = nil,
        source: String? = "manual"
    ) -> Bool {
        let context = container.viewContext

        // Normalize input (trim whitespace)
        let normalizedWord = word.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalizedReplacement = replacement?.trimmingCharacters(in: .whitespacesAndNewlines)

        // Reject empty/whitespace-only words. An empty word would later compile
        // to the regex "\b\b" in VocabularyProcessor, matching every word
        // boundary and corrupting the entire transcript.
        guard !normalizedWord.isEmpty else {
            return false
        }

        // Check for duplicate (exclude the item being edited)
        if vocabularyItemExists(word: normalizedWord, excludingId: excludingId) {
            return false
        }
        
        // Create new vocabulary item
        let vocabItem = Vocabulary(context: context)
        vocabItem.id = UUID()
        vocabItem.word = normalizedWord
        vocabItem.replacement = normalizedReplacement?.isEmpty == true ? nil : normalizedReplacement
        vocabItem.setValue(source, forKey: "source")
        vocabItem.createdDate = Date()
        
        // Set sort order (add to end)
        let maxSortOrder = fetchAllVocabularyItems().map { $0.sortOrder }.max() ?? 0
        vocabItem.sortOrder = maxSortOrder + 1
        
        save()
        return true
    }
    
    /// Checks if a vocabulary item with the given word already exists
    /// - Parameter word: The word to check
    /// - Returns: True if exists, false otherwise
    func vocabularyItemExists(word: String, excludingId: UUID? = nil) -> Bool {
        let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
        let trimmedWord = word.trimmingCharacters(in: .whitespacesAndNewlines)

        if let excludingId = excludingId {
            request.predicate = NSCompoundPredicate(andPredicateWithSubpredicates: [
                NSPredicate(format: "word ==[c] %@", trimmedWord),
                NSPredicate(format: "id != %@", excludingId as CVarArg)
            ])
        } else {
            request.predicate = NSPredicate(format: "word ==[c] %@", trimmedWord)
        }
        request.fetchLimit = 1

        do {
            let count = try container.viewContext.count(for: request)
            return count > 0
        } catch {
            AppLogger.coreData.error("Failed to check vocabulary existence: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to check vocabulary existence", tags: ["component": "PersistenceController", "operation": "vocabularyItemExists"])
            return false
        }
    }
    
    /// Deletes a vocabulary item
    /// - Parameter item: The vocabulary item to delete
    func deleteVocabularyItem(_ item: Vocabulary) {
        let context = container.viewContext
        context.delete(item)
        save()
    }
    
    /// Deletes a vocabulary item by ID
    /// - Parameter id: The ID of the vocabulary item to delete
    func deleteVocabularyItem(byId id: UUID) {
        let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
        request.predicate = NSPredicate(format: "id == %@", id as CVarArg)
        request.fetchLimit = 1
        
        do {
            if let item = try container.viewContext.fetch(request).first {
                deleteVocabularyItem(item)
            }
        } catch {
            AppLogger.logCoreData(.delete(entity: "Vocabulary"), error: error)
            SentryService.capture(error: error, message: "Failed to delete vocabulary item", tags: ["component": "PersistenceController", "operation": "deleteVocabularyItem"])
        }
    }
    
    /// Fetch all vocabulary entries on a background context to avoid blocking the main thread.
    /// Returns thread-safe value-type snapshots instead of managed objects, so callers can
    /// hold them in callbacks that fire on arbitrary threads.
    /// Same rationale as `fetchModeSnapshotInBackground` (DB on Main Thread during Recording Start).
    func fetchVocabularyEntriesInBackground() async -> [VocabularyEntrySnapshot] {
        let context = container.newBackgroundContext()
        return await context.perform {
            let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
            request.sortDescriptors = [NSSortDescriptor(keyPath: \Vocabulary.word, ascending: true)]
            do {
                return try context.fetch(request).compactMap { item in
                    guard let word = item.word else { return nil }
                    return VocabularyEntrySnapshot(word: word, replacement: item.replacement)
                }
            } catch {
                AppLogger.coreData.error("Failed to fetch vocabulary entries: \(error, privacy: .public)")
                SentryService.capture(error: error, message: "Failed to fetch vocabulary entries", tags: ["component": "PersistenceController", "operation": "fetchVocabularyEntriesInBackground"])
                return []
            }
        }
    }

    /// Fetches all vocabulary items sorted by word
    /// - Returns: Array of vocabulary items
    func fetchAllVocabularyItems() -> [Vocabulary] {
        let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Vocabulary.word, ascending: true)]
        
        do {
            return try container.viewContext.fetch(request)
        } catch {
            AppLogger.coreData.error("Failed to fetch vocabulary: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch vocabulary", tags: ["component": "PersistenceController", "operation": "fetchAllVocabularyItems"])
            return []
        }
    }
    
    // MARK: - Fetch Requests
    
    /// Fetches all transcripts sorted by date
    /// - Returns: Array of transcripts
    func fetchAllTranscripts() -> [Transcript] {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]
        
        do {
            return try container.viewContext.fetch(request)
        } catch {
            AppLogger.coreData.error("Failed to fetch transcripts: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch transcripts", tags: ["component": "PersistenceController", "operation": "fetchAllTranscripts"])
            return []
        }
    }
    
    /// Searches transcripts by text
    /// - Parameter searchText: Text to search for
    /// - Returns: Array of matching transcripts
    func searchTranscripts(containing searchText: String) -> [Transcript] {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(format: "text CONTAINS[cd] %@", searchText)
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]
        
        do {
            return try container.viewContext.fetch(request)
        } catch {
            AppLogger.coreData.error("Failed to search transcripts: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to search transcripts", tags: ["component": "PersistenceController", "operation": "searchTranscripts"])
            return []
        }
    }
    
    // MARK: - Usage Tracking Operations
    
    /// Gets or creates the usage tracking record
    /// There should only be one UsageTracking record in the database
    /// - Returns: The usage tracking record
    func getOrCreateUsageTracking() -> UsageTracking {
        let request: NSFetchRequest<UsageTracking> = UsageTracking.fetchRequest()
        request.fetchLimit = 1
        
        do {
            let results = try container.viewContext.fetch(request)
            if let existing = results.first {
                return existing
            }
        } catch {
            AppLogger.coreData.error("Failed to fetch usage tracking: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch usage tracking", tags: ["component": "PersistenceController", "operation": "getOrCreateUsageTracking"])
        }
        
        // Create new usage tracking record
        let usage = UsageTracking(context: container.viewContext)
        usage.id = UUID()
        usage.dailyTranscriptionSeconds = 0
        usage.totalModelsDownloaded = 0
        usage.lastResetDate = Date()
        usage.firstUsageDate = Date()
        usage.licenseStatus = "trial"
        
        save()
        return usage
    }
    
    // NOTE: usage WRITES (updateDailyUsage / updateModelDownloadCount /
    // resetDailyUsage) were removed — the Rust hw-license core owns usage
    // tracking now. The getters below remain solely for the one-shot
    // Core Data → UserDefaults seed in RustLicenseStore.

    /// Gets current daily usage in seconds
    /// - Returns: Number of seconds used today
    func getDailyUsage() -> Int64 {
        let usage = getOrCreateUsageTracking()
        
        // Check if we need to reset (new day)
        if let lastReset = usage.lastResetDate {
            let calendar = Calendar.current
            if !calendar.isDateInToday(lastReset) {
                // Reset for new day
                usage.dailyTranscriptionSeconds = 0
                usage.lastResetDate = Date()
                save()
                return 0
            }
        }
        
        return usage.dailyTranscriptionSeconds
    }
    
    /// Gets the count of downloaded models
    /// - Returns: Number of models downloaded
    func getModelDownloadCount() -> Int16 {
        let usage = getOrCreateUsageTracking()
        return usage.totalModelsDownloaded
    }
    
    /// Updates license status and related information
    /// - Parameters:
    ///   - status: New license status
    ///   - email: Customer email (optional)
    ///   - activatedDate: License activation date (optional)
    func updateLicenseStatus(_ status: String, email: String? = nil, activatedDate: Date? = nil) {
        let usage = getOrCreateUsageTracking()
        usage.licenseStatus = status
        usage.lastValidationDate = Date()
        
        if let email = email {
            usage.customerEmail = email
        }
        
        if let activatedDate = activatedDate {
            usage.licenseActivatedDate = activatedDate
        }
        
        save()
    }
    
    // MARK: - Bulk Import Operations (Backup/Restore)

    /// Imports modes from backup data with conflict resolution
    ///
    /// CONFLICT RESOLUTION:
    /// - .skip: Don't import if mode with same name exists (case-insensitive)
    /// - .replace: Delete existing mode, import new one
    /// - .keepBoth: Import as "Mode Name (imported)"
    ///
    /// - Parameters:
    ///   - backupModes: Array of BackupMode structs to import
    ///   - resolution: How to handle conflicts
    /// - Returns: Tuple with counts of imported and skipped modes plus any old-to-new IDs created by `.keepBoth`
    ///
    /// `@MainActor`: the entire import runs against `container.viewContext`, which is bound to
    /// the main queue. Pinning this method to the main actor enforces that queue confinement at
    /// compile time so no caller can reach the fetch/save/delete work off-queue (Core Data
    /// undefined behavior). The only caller today (`BackupManager`) is already main-actor isolated.
    @MainActor
    func importModes(_ backupModes: [BackupMode], resolution: ModeConflictResolution) -> (imported: Int, skipped: Int, idRemap: [UUID: UUID]) {
        var imported = 0
        var skipped = 0
        var idRemap: [UUID: UUID] = [:]

        // Get existing modes for conflict detection
        let existingModes = fetchAllModes()
        let existingNames = Set(existingModes.compactMap { $0.name?.lowercased() })
        var existingModeObjectIDsByName: [String: NSManagedObjectID] = [:]
        for mode in existingModes {
            guard let normalizedName = mode.name?.lowercased(),
                  existingModeObjectIDsByName[normalizedName] == nil else {
                continue
            }
            existingModeObjectIDsByName[normalizedName] = mode.objectID
        }
        var replacedExistingModeNames = Set<String>()

        for backupMode in backupModes {
            let normalizedName = backupMode.name.lowercased()
            let hasConflict = existingNames.contains(normalizedName)

            if hasConflict {
                switch resolution {
                case .skip:
                    // Skip this mode - it already exists
                    skipped += 1
                    continue

                case .replace:
                    // Delete the pre-import conflict once. Re-fetch by object ID rather than
                    // holding a managed object from `existingModes`: `deleteMode` saves during
                    // the loop, so captured objects can be invalidated. Tracking the original
                    // object ID also prevents duplicate backup names from deleting a mode that
                    // was imported earlier in this restore.
                    if !replacedExistingModeNames.contains(normalizedName),
                       let objectID = existingModeObjectIDsByName[normalizedName] {
                        if let existingObject = try? container.viewContext.existingObject(with: objectID),
                           let existingMode = existingObject as? Mode,
                           !existingMode.isDeleted {
                            deleteMode(existingMode)
                        }
                        replacedExistingModeNames.insert(normalizedName)
                    }
                    // Fall through to create new mode

                case .keepBoth:
                    // Will create with modified name below
                    break
                }
            }

            // Determine the name to use
            let finalName: String
            let finalId: UUID
            if hasConflict && resolution == .keepBoth {
                finalName = "\(backupMode.name) (imported)"
                finalId = UUID()  // New ID for duplicate
                idRemap[backupMode.id] = finalId
            } else {
                finalName = backupMode.name
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
                // Resolve legacy AssemblyAI model IDs on restore so older backups map
                // onto the current Universal-2 / Universal-3 Pro lineup, then collapse
                // any removed Deepgram IDs onto Nova 3 General.
                cloudTranscriptionModel: CloudTranscriptionModels.resolveDeepgramModelAlias(
                    backupMode.cloudTranscriptionModel.map { CloudTranscriptionModels.resolveAssemblyAIModelAlias($0) }
                ),
                postProcessingMode: backupMode.postProcessingMode,
                postProcessingProvider: backupMode.postProcessingProvider,
                englishSpelling: backupMode.englishSpelling,
                userSystemPrompt: backupMode.userSystemPrompt,
                cloudAccuracyTier: normalized.accuracyTier
                    ?? CloudAccuracyTier.fromStorageValue(backupMode.cloudAccuracyTier).rawValue,
                removeTrailingPeriod: backupMode.removeTrailingPeriod ?? false,
                geminiCustomPrompt: backupMode.geminiCustomPrompt,
                cloudPostProcessingModel: backupMode.cloudPostProcessingModel,
                cloudTranscriptionDomain: backupMode.cloudTranscriptionDomain,
                foreignPlatformExtensions: backupMode.foreignPlatformExtensions
            )

            // Update isDefault flag if this mode should be default
            // Only set as default if the original was default AND we're not in keepBoth mode
            if backupMode.isDefault && !(hasConflict && resolution == .keepBoth) {
                // Clear existing default(s) first. Re-fetch here rather than reusing
                // `existingModes`: under `.replace`, `deleteMode` above deletes and
                // saves managed objects on earlier iterations, so that captured array
                // can hold invalidated objects (reading/writing them is a Core Data
                // use-after-delete). The fresh fetch never returns deleted objects;
                // `!mode.isDeleted` guards any in-flight, unsaved delete.
                let defaultsRequest: NSFetchRequest<Mode> = Mode.fetchRequest()
                defaultsRequest.predicate = NSPredicate(format: "isDefault == YES")
                if let currentDefaults = try? container.viewContext.fetch(defaultsRequest) {
                    for mode in currentDefaults where !mode.isDeleted {
                        mode.isDefault = false
                    }
                }
                // Set new default
                if let newMode = fetchMode(withId: finalId.uuidString) {
                    newMode.isDefault = true
                }
                save()
            }

            imported += 1
        }

        AppLogger.coreData.info("Mode import complete: \(imported) imported, \(skipped) skipped")
        return (imported, skipped, idRemap)
    }

    /// Imports vocabulary items from backup data with conflict resolution
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
    /// `@MainActor`: like `importModes`, all work runs against `container.viewContext` (main
    /// queue). Pinning to the main actor enforces that queue confinement at compile time.
    @MainActor
    func importVocabulary(_ backupItems: [BackupVocabularyItem], resolution: VocabularyConflictResolution) -> (imported: Int, skipped: Int) {
        var imported = 0
        var skipped = 0

        for item in backupItems {
            let normalizedWord = item.word.trimmingCharacters(in: .whitespacesAndNewlines)

            // Skip empty/whitespace-only words. An empty word would later compile
            // to the regex "\b\b" in VocabularyProcessor, matching every word
            // boundary and corrupting the entire transcript.
            guard !normalizedWord.isEmpty else {
                skipped += 1
                continue
            }

            // Check for existing item
            let exists = vocabularyItemExists(word: normalizedWord)

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
                        save()
                        imported += 1
                        continue
                    }
                    // Fall through to add new item
                }
            }

            // Add the vocabulary item. `addVocabularyItem` can return false (e.g. a
            // duplicate that wasn't removed), in which case nothing was persisted — count
            // it as skipped so the success summary doesn't overstate what was saved.
            if addVocabularyItem(word: normalizedWord, replacement: item.replacement, source: item.source) {
                imported += 1
            } else {
                skipped += 1
            }
        }

        AppLogger.coreData.info("Vocabulary import complete: \(imported) imported, \(skipped) skipped")
        return (imported, skipped)
    }
}
