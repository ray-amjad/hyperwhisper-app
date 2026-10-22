//
//  BackupImportSettingsRestoreTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1627 (macOS sibling of #1614).
//
//  A backup import applies the backup's settings BEFORE its one-transaction
//  modes/vocabulary store write (#1613). When that write failed and rolled
//  back, the settings stayed applied: a Settings + Modes import could leave
//  the app on the backup's settings with none of its modes. Both import paths
//  now snapshot the settings first and put the snapshot back when the store
//  step fails.
//
//  The store failure is a real Core Data validation failure on an in-memory
//  store (the #1621 seam, see `BackupImportAtomicityTests`), injected through
//  `BackupManager.importPersistence`. The settings go to a recording store
//  injected through `BackupManager.settingsStore`, so no test here writes the
//  running test host's settings, shortcuts or login item.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
@Suite("A failed backup store step puts the settings back (#1627)", .serialized)
struct BackupImportSettingsRestoreTests {

    private static let poisonName = "percy1627-poison"
    private static let poisonWord = "percy1627-poison-word"
    private static let importedModeId = UUID()
    private static let poisonModeId = UUID()

    // MARK: - Fixtures

    /// The settings as an import sees them. `apply` and `restore` record what
    /// they were given and replace `live`; `defaultModelByMode` and the foreign
    /// extensions are their own state, as in the live store.
    @MainActor
    private final class RecordingSettingsStore: BackupImportSettingsStore {
        private(set) var live: BackupSettings
        var defaultModelByMode: [String: String]
        var foreignTopLevelExtensions: String?
        private(set) var currentCalls = 0
        private(set) var applied: [BackupSettings] = []
        private(set) var restored: [BackupSettings] = []

        init(live: BackupSettings, foreignTopLevelExtensions: String? = nil) {
            self.live = live
            self.defaultModelByMode = live.aiModel.defaultModelByMode
            self.foreignTopLevelExtensions = foreignTopLevelExtensions
        }

        func current() async -> BackupSettings {
            currentCalls += 1
            return live
        }

        func apply(_ settings: BackupSettings) async {
            applied.append(settings)
            live = settings
        }

        func restore(_ settings: BackupSettings) async {
            restored.append(settings)
            live = settings
            defaultModelByMode = settings.aiModel.defaultModelByMode
        }
    }

    /// Blanks the required `name` of the poison mode when a save is about to
    /// write it, so the modes save fails validation. Removed by the caller.
    @MainActor
    private final class ModeSavePoison {
        private(set) var fired = false
        private var token: NSObjectProtocol?

        init(context: NSManagedObjectContext, poisonName: String) {
            token = NotificationCenter.default.addObserver(
                forName: NSManagedObjectContext.willSaveObjectsNotification,
                object: context,
                queue: nil
            ) { [weak self] _ in
                MainActor.assumeIsolated {
                    for case let mode as Mode in context.insertedObjects where mode.name == poisonName {
                        mode.name = nil
                        self?.fired = true
                    }
                }
            }
        }

        func remove() {
            if let token {
                NotificationCenter.default.removeObserver(token)
            }
            token = nil
        }
    }

    /// Makes the save that writes `poisonWord` fail validation by adding a
    /// Mode with no name to it (see `BackupImportAtomicityTests`).
    @MainActor
    private final class VocabularySavePoison {
        private(set) var fired = false
        private var token: NSObjectProtocol?

        init(context: NSManagedObjectContext, poisonWord: String) {
            token = NotificationCenter.default.addObserver(
                forName: NSManagedObjectContext.willSaveObjectsNotification,
                object: context,
                queue: nil
            ) { [weak self] _ in
                MainActor.assumeIsolated {
                    let poisoned = context.insertedObjects.contains { ($0 as? Vocabulary)?.word == poisonWord }
                    if poisoned {
                        _ = Mode(context: context)
                        self?.fired = true
                    }
                }
            }
        }

        func remove() {
            if let token {
                NotificationCenter.default.removeObserver(token)
            }
            token = nil
        }
    }

    /// A complete settings section. Every argument differs between the
    /// "before" and the "backup" values the tests use.
    private static func settings(
        launchMinimized: Bool,
        removeFillerWords: Bool,
        soundEffectsVolume: Double,
        quickCaptureModeId: String,
        maxRecordingDuration: Int,
        defaultModelByMode: [String: String]
    ) -> BackupSettings {
        BackupSettings(
            general: BackupGeneralSettings(
                launchAtLogin: false,
                showInDock: true,
                launchMinimized: launchMinimized,
                showRecordingWindow: true,
                checkForUpdatesAutomatically: true,
                enableErrorLogging: true,
                shareAnonymousSpeedData: true
            ),
            audio: BackupAudioSettings(
                autoIncreaseMicVolume: false,
                mediaControlMode: "off",
                enableSoundEffects: true,
                soundTheme: "default",
                soundEffectsVolume: soundEffectsVolume
            ),
            storage: BackupStorageSettings(filesyncEnabled: false, storeAsM4A: true),
            textOutput: BackupTextOutputSettings(
                pasteResultText: true,
                removeFillerWords: removeFillerWords,
                restoreClipboardAfterPaste: true,
                hideFromClipboardHistory: true,
                clipboardRestoreDelaySeconds: 1,
                autocapitalizeInsert: true,
                storeWordTimestamps: true
            ),
            shortcuts: BackupShortcutSettings(
                pushToTalkMode: "disabled",
                pushToTalkDoublePressEnabled: false,
                quickCaptureEnabled: true,
                quickCaptureModeId: quickCaptureModeId,
                keyboardShortcuts: ["toggleRecordingWithTranscription": .combo(carbonKeyCode: 32, carbonModifiers: 4608)]
            ),
            aiModel: BackupAIModelSettings(
                showExperimentalModels: false,
                defaultTranscriptionModel: "base",
                defaultLanguage: "en",
                defaultModelByMode: defaultModelByMode
            ),
            advanced: BackupAdvancedSettings(
                maxRecordingDuration: maxRecordingDuration,
                audioSampleRate: 16000,
                keepAudioFiles: true,
                historyRetentionDays: 30
            )
        )
    }

    /// The app's settings before the import. The Quick Capture mode is a
    /// local one; 300 s is a real user value here, not the legacy placeholder.
    private static let localModeId = UUID()
    private static func before() -> BackupSettings {
        settings(
            launchMinimized: false,
            removeFillerWords: false,
            soundEffectsVolume: 0.25,
            quickCaptureModeId: localModeId.uuidString,
            maxRecordingDuration: 300,
            defaultModelByMode: [localModeId.uuidString: "base"]
        )
    }

    /// The backup's settings: they select a mode that exists only in the
    /// backup, and give it a per-mode default model.
    private static func backupSettings() -> BackupSettings {
        settings(
            launchMinimized: true,
            removeFillerWords: true,
            soundEffectsVolume: 0.9,
            quickCaptureModeId: importedModeId.uuidString,
            maxRecordingDuration: 1200,
            defaultModelByMode: [importedModeId.uuidString: "percy1627-model"]
        )
    }

    private static func backupMode(id: UUID, name: String) -> BackupMode {
        BackupMode(
            id: id,
            name: name,
            preset: "hyper",
            language: "en",
            model: "base",
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            customInstructions: nil,
            languageModel: nil,
            cloudProvider: nil,
            cloudTranscriptionModel: nil,
            postProcessingMode: 0,
            postProcessingProvider: nil,
            englishSpelling: nil,
            userSystemPrompt: nil,
            isDefault: false,
            sortOrder: 0,
            cloudAccuracyTier: nil,
            removeTrailingPeriod: nil,
            geminiCustomPrompt: nil,
            cloudPostProcessingModel: nil,
            cloudTranscriptionDomain: nil
        )
    }

    /// A legacy v1 file with Settings, Modes and Vocabulary. `poisonModes`
    /// adds the mode whose save fails; `poisonVocabulary` the word whose save
    /// fails.
    private static func v1File(poisonModes: Bool, poisonVocabulary: Bool) throws -> URL {
        var modes = [backupMode(id: importedModeId, name: "percy1627-imported")]
        if poisonModes {
            modes.append(backupMode(id: poisonModeId, name: poisonName))
        }
        var vocabulary = [BackupVocabularyItem(id: UUID(), word: "percy1627-word", replacement: nil, sortOrder: 0, source: "manual")]
        if poisonVocabulary {
            vocabulary.append(BackupVocabularyItem(id: UUID(), word: poisonWord, replacement: nil, sortOrder: 1, source: "manual"))
        }
        let data = BackupData(
            version: BackupData.currentVersion,
            exportDate: Date(),
            appVersion: "1.0",
            settings: backupSettings(),
            apiKeys: nil,
            licenseKey: nil,
            modes: modes,
            vocabulary: vocabulary
        )
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return try write(encoder.encode(data))
    }

    private static func write(_ data: Data) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("hw-1627-\(UUID().uuidString).json")
        try data.write(to: url)
        return url
    }

    /// The shared macOS v2 example, read from the source tree (the idiom
    /// `BackupConformanceVectorTests` uses).
    private static func v2Example() throws -> [String: Any] {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-backup/examples/macos-export.hwbackup.json")
        let object = try JSONSerialization.jsonObject(with: Data(contentsOf: url))
        return try #require(object as? [String: Any])
    }

    /// The name of the example's first mode, which the v2 tests poison.
    private static func firstModeName(_ root: [String: Any]) throws -> String {
        let modes = try #require(root["modes"] as? [[String: Any]])
        return try #require(modes.first?["name"] as? String)
    }

    private static func options(settings: Bool = true, modes: Bool = true, vocabulary: Bool = true) -> ImportOptions {
        var options = ImportOptions()
        options.importSettings = settings
        options.importModes = modes
        options.importVocabulary = vocabulary
        options.importAPIKeys = false
        options.importLicenseKey = false
        return options
    }

    /// Rows in the STORE, read on a fresh background context.
    private func storedCount(_ entityName: String, in persistence: PersistenceController) throws -> Int {
        let context = persistence.container.newBackgroundContext()
        var count = 0
        var failure: Error?
        context.performAndWait {
            do {
                count = try context.count(for: NSFetchRequest<NSFetchRequestResult>(entityName: entityName))
            } catch {
                failure = error
            }
        }
        if let failure { throw failure }
        return count
    }

    /// One local mode and one word, saved.
    private func seed(_ persistence: PersistenceController) throws {
        let local = persistence.createOrUpdateMode(
            id: Self.localModeId, name: "Local", preset: "hyper", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        local.isDefault = true
        #expect(persistence.addVocabularyItem(word: "teh", replacement: "the"))
        try persistence.container.viewContext.save()
    }

    nonisolated private static func json(_ settings: BackupSettings) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return String(decoding: try encoder.encode(settings), as: UTF8.self)
    }

    private func manager(_ persistence: PersistenceController, _ store: RecordingSettingsStore) -> BackupManager {
        // Own instance, never `.shared` — see `BackupManager.init`.
        let backup = BackupManager()
        backup.importPersistence = persistence
        backup.settingsStore = store
        return backup
    }

    // MARK: - v1 (legacy) import

    /// The issue's "Done when": a failed modes save during a Settings + Modes
    /// import leaves the settings as they were before the import.
    @Test func v1AFailedModesSavePutsTheSettingsBack() async throws {
        let persistence = PersistenceController(inMemory: true)
        try seed(persistence)
        let modesBefore = try storedCount("Mode", in: persistence)
        let store = RecordingSettingsStore(live: Self.before())
        let backup = manager(persistence, store)

        let poison = ModeSavePoison(context: persistence.container.viewContext, poisonName: Self.poisonName)
        defer { poison.remove() }
        let url = try Self.v1File(poisonModes: true, poisonVocabulary: false)
        defer { try? FileManager.default.removeItem(at: url) }

        let result = await backup.importSettings(from: url, options: Self.options())

        #expect(poison.fired, "the poisoned modes save never ran")
        // The backup's settings WERE applied first, so the restore is what
        // put them back — not an import that never touched them.
        #expect(try store.applied.map(Self.json) == [Self.json(Self.backupSettings())])
        #expect(try store.restored.map(Self.json) == [Self.json(Self.before())])

        // Done when: the settings are as they were before the import.
        #expect(try Self.json(store.live) == Self.json(Self.before()))
        #expect(store.live.shortcuts.quickCaptureModeId == Self.localModeId.uuidString)
        #expect(store.live.advanced.maxRecordingDuration == 300)
        #expect(store.defaultModelByMode == [Self.localModeId.uuidString: "base"])

        // The store is as it was too.
        #expect(try storedCount("Mode", in: persistence) == modesBefore)
        #expect(persistence.fetchAllModes().contains { $0.id == Self.importedModeId } == false)

        // Nothing changed, so a plain failure that says so.
        #expect(!result.success)
        #expect(!result.partialSuccess)
        #expect(!result.settingsApplied)
        let message = try #require(result.errorMessage)
        #expect(message.contains("modes and vocabulary were left as they were"))
        #expect(message.contains("settings were left as they were"))
        #expect(!message.contains("were applied"))
    }

    /// A failed vocabulary save after the modes saved: the modes stay
    /// imported, the vocabulary and the settings are as they were, and the
    /// backup's per-mode default models are not merged in.
    @Test func v1AFailedVocabularySavePutsTheSettingsBack() async throws {
        let persistence = PersistenceController(inMemory: true)
        try seed(persistence)
        let vocabularyBefore = try storedCount("Vocabulary", in: persistence)
        let store = RecordingSettingsStore(live: Self.before())
        let backup = manager(persistence, store)

        let poison = VocabularySavePoison(context: persistence.container.viewContext, poisonWord: Self.poisonWord)
        defer { poison.remove() }
        let url = try Self.v1File(poisonModes: false, poisonVocabulary: true)
        defer { try? FileManager.default.removeItem(at: url) }

        let result = await backup.importSettings(from: url, options: Self.options())

        #expect(poison.fired, "the poisoned vocabulary save never ran")
        #expect(store.applied.count == 1)
        #expect(try Self.json(store.live) == Self.json(Self.before()))
        #expect(store.defaultModelByMode == [Self.localModeId.uuidString: "base"])

        #expect(persistence.fetchAllModes().contains { $0.id == Self.importedModeId })
        #expect(try storedCount("Vocabulary", in: persistence) == vocabularyBefore)

        #expect(!result.success)
        #expect(result.partialSuccess)
        #expect(result.modesImported == 1)
        #expect(!result.settingsApplied)
        let message = try #require(result.errorMessage)
        #expect(message.contains("modes were imported"))
        #expect(message.contains("settings were left as they were"))
        #expect(!message.contains("were applied"))
    }

    /// Control: when the store step saves, the backup's settings stay applied
    /// and nothing is restored.
    @Test func v1ASuccessfulImportKeepsTheBackupSettings() async throws {
        let persistence = PersistenceController(inMemory: true)
        try seed(persistence)
        let store = RecordingSettingsStore(live: Self.before())
        let backup = manager(persistence, store)

        let url = try Self.v1File(poisonModes: false, poisonVocabulary: false)
        defer { try? FileManager.default.removeItem(at: url) }

        let result = await backup.importSettings(from: url, options: Self.options())

        #expect(result.success)
        #expect(result.settingsApplied)
        #expect(store.restored.isEmpty)
        #expect(try Self.json(store.live) == Self.json(Self.backupSettings()))
        #expect(store.defaultModelByMode[Self.importedModeId.uuidString] == "percy1627-model")
        #expect(persistence.fetchAllModes().contains { $0.id == Self.importedModeId })
    }

    /// Settings not selected: a failed modes save has nothing to put back,
    /// and the settings are never read or written.
    @Test func v1AFailedModesSaveWithoutSettingsTouchesNoSettings() async throws {
        let persistence = PersistenceController(inMemory: true)
        try seed(persistence)
        let store = RecordingSettingsStore(live: Self.before())
        let backup = manager(persistence, store)

        let poison = ModeSavePoison(context: persistence.container.viewContext, poisonName: Self.poisonName)
        defer { poison.remove() }
        let url = try Self.v1File(poisonModes: true, poisonVocabulary: false)
        defer { try? FileManager.default.removeItem(at: url) }

        let result = await backup.importSettings(from: url, options: Self.options(settings: false))

        #expect(poison.fired)
        #expect(store.currentCalls == 0)
        #expect(store.applied.isEmpty)
        #expect(store.restored.isEmpty)
        #expect(!result.success)
        let message = try #require(result.errorMessage)
        #expect(!message.contains("settings were left as they were"))
    }

    // MARK: - v2 (universal) import

    /// The same "Done when" on the universal v2 path, which also replaces the
    /// stored foreign top-level `platformExtensions`: both are put back.
    @Test func v2AFailedModesSavePutsTheSettingsAndForeignExtensionsBack() async throws {
        let persistence = PersistenceController(inMemory: true)
        try seed(persistence)
        let modesBefore = try storedCount("Mode", in: persistence)
        let foreignBefore = #"{"windows":{"settings":{"percy1627":true}}}"#
        let store = RecordingSettingsStore(live: Self.before(), foreignTopLevelExtensions: foreignBefore)
        let backup = manager(persistence, store)

        let example = try Self.v2Example()
        let poison = ModeSavePoison(context: persistence.container.viewContext, poisonName: try Self.firstModeName(example))
        defer { poison.remove() }
        let url = try Self.write(JSONSerialization.data(withJSONObject: example))
        defer { try? FileManager.default.removeItem(at: url) }

        let result = await backup.importSettings(from: url, options: Self.options())

        #expect(poison.fired, "the poisoned modes save never ran")
        // The v2 settings step really applied something different first.
        let applied = try #require(store.applied.first, "the v2 settings step did not apply the example's settings")
        #expect(store.applied.count == 1)
        #expect(try Self.json(applied) != Self.json(Self.before()))
        #expect(try store.restored.map(Self.json) == [Self.json(Self.before())])

        #expect(try Self.json(store.live) == Self.json(Self.before()))
        #expect(store.foreignTopLevelExtensions == foreignBefore)
        #expect(store.defaultModelByMode == [Self.localModeId.uuidString: "base"])
        #expect(try storedCount("Mode", in: persistence) == modesBefore)

        #expect(!result.success)
        #expect(!result.settingsApplied)
        let message = try #require(result.errorMessage)
        #expect(message.contains("settings were left as they were"))
        #expect(!message.contains("were applied"))
    }

    /// A v2 file with no settings section still replaces the stored foreign
    /// extensions when Settings is selected; a failed modes save puts them
    /// back, and the message does not claim a settings restore that never
    /// happened.
    @Test func v2WithoutSettingsAFailedModesSavePutsTheForeignExtensionsBack() async throws {
        let persistence = PersistenceController(inMemory: true)
        try seed(persistence)
        let foreignBefore = #"{"linux":{"settings":{"percy1627":true}}}"#
        let store = RecordingSettingsStore(live: Self.before(), foreignTopLevelExtensions: foreignBefore)
        let backup = manager(persistence, store)

        var example = try Self.v2Example()
        example.removeValue(forKey: "settings")
        let poison = ModeSavePoison(context: persistence.container.viewContext, poisonName: try Self.firstModeName(example))
        defer { poison.remove() }
        let url = try Self.write(JSONSerialization.data(withJSONObject: example))
        defer { try? FileManager.default.removeItem(at: url) }

        let result = await backup.importSettings(from: url, options: Self.options())

        #expect(poison.fired, "the poisoned modes save never ran")
        #expect(store.applied.isEmpty)
        #expect(store.restored.isEmpty)
        #expect(store.foreignTopLevelExtensions == foreignBefore)
        #expect(try Self.json(store.live) == Self.json(Self.before()))

        #expect(!result.success)
        let message = try #require(result.errorMessage)
        #expect(!message.contains("settings were left as they were"))
    }

    // MARK: - Message

    @Test func theSettingsSentenceAppearsOnlyWhenTheSettingsWerePutBack() {
        let restored = BackupManager.storeImportFailureMessage(
            failedSection: .modes,
            modesSaved: false,
            vocabularySelected: false,
            settingsRestored: true,
            otherSectionsApplied: false,
            licenseImportFailed: false
        )
        let notRestored = BackupManager.storeImportFailureMessage(
            failedSection: .modes,
            modesSaved: false,
            vocabularySelected: false,
            otherSectionsApplied: false,
            licenseImportFailed: false
        )
        #expect(restored == "The modes could not be saved, so they were left as they were. The settings were left as they were.")
        #expect(notRestored == "The modes could not be saved, so they were left as they were.")
    }
}
