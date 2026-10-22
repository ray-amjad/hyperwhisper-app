//
//  BackupUniversalV2RoundTripTests.swift
//  hyperwhisperTests
//
//  Round-trip cover for the universal v2 backup path (#732).
//
//  `encodeBackupV2` and `importUniversalV2` had no direct test: both were
//  private and read or wrote the live app. #732 split them into steps a test
//  can call. Here a fixture is built with the export steps
//  (`universalV2SettingsSections` → `universalBackupDTO` → `encodedBackupV2`,
//  which include the core's schema self-check), then decoded the way the import
//  decodes it and fed to the import's decode arms (`macosSettings(fromV2:)`,
//  `backupModes(fromV2:)`, `backupVocabularyItems(fromUniversal:)`,
//  `universalAPIKeyAssignments`, `foreignTopLevelExtensions`). Nothing here
//  touches the running test host's settings, store or Keychain: the
//  `importUniversalV2` tests (#2207) write to an in-memory store and a recording
//  settings store, injected through `importPersistence` and `settingsStore`.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
@Suite("Universal v2 backup round-trips through the export and import steps (#732)")
struct BackupUniversalV2RoundTripTests {

    private static let richModeId = UUID()
    private static let plainModeId = UUID()
    private static let wordId = UUID()
    private static let secondWordId = UUID()
    private static let storedForeign = #"{"windows":{"theme":"dark"}}"#
    private static let apiKeys: [KeychainManager.APIKeyType: String] = [
        .openAI: "percy732-openai-value",
        .geminiTranscribe: "percy732-transcribe-value",
    ]

    // MARK: - Fixtures

    /// A complete settings section. Every value differs between the two
    /// variants, so a field taken from the wrong side shows.
    private static func settings(_ v: Bool) -> BackupSettings {
        BackupSettings(
            general: BackupGeneralSettings(
                launchAtLogin: v,
                showInDock: !v,
                launchMinimized: v,
                showRecordingWindow: !v,
                checkForUpdatesAutomatically: v,
                enableErrorLogging: !v,
                shareAnonymousSpeedData: v
            ),
            audio: BackupAudioSettings(
                autoIncreaseMicVolume: v,
                mediaControlMode: v ? "pauseAndResume" : "off",
                enableSoundEffects: !v,
                soundTheme: v ? "default" : "classic",
                soundEffectsVolume: v ? 0.8 : 0.25
            ),
            storage: BackupStorageSettings(filesyncEnabled: v, storeAsM4A: !v),
            textOutput: BackupTextOutputSettings(
                pasteResultText: v,
                removeFillerWords: !v,
                restoreClipboardAfterPaste: v,
                hideFromClipboardHistory: !v,
                clipboardRestoreDelaySeconds: v ? 1.5 : 0.5,
                autocapitalizeInsert: v,
                storeWordTimestamps: !v
            ),
            shortcuts: BackupShortcutSettings(
                pushToTalkMode: v ? "hold" : "disabled",
                pushToTalkDoublePressEnabled: v,
                quickCaptureEnabled: v,
                quickCaptureModeId: v ? richModeId.uuidString : "",
                keyboardShortcuts: [
                    "toggleRecordingWithTranscription": v ? .combo(carbonKeyCode: 32, carbonModifiers: 4608) : .unassigned,
                ]
            ),
            aiModel: BackupAIModelSettings(
                showExperimentalModels: v,
                defaultTranscriptionModel: v ? "large-v3-turbo" : "base",
                defaultLanguage: v ? "fr" : "en",
                defaultModelByMode: v ? [richModeId.uuidString: "large-v3"] : [:]
            ),
            advanced: BackupAdvancedSettings(
                maxRecordingDuration: v ? 1200 : 600,
                audioSampleRate: v ? 48000 : 16000,
                keepAudioFiles: v,
                historyRetentionDays: v ? 90 : 30
            )
        )
    }

    /// A mode with every field set to a non-default value.
    private static func richMode() -> BackupMode {
        BackupMode(
            id: richModeId,
            name: "Percy 732 Rich",
            preset: "hyper",
            language: "fr",
            model: "large-v3",
            punctuation: false,
            capitalization: false,
            profanityFilter: true,
            customInstructions: "Keep it short.",
            languageModel: "local-llm",
            cloudProvider: "hyperwhisperCloud",
            cloudTranscriptionModel: "nova-3",
            postProcessingMode: 2,
            postProcessingProvider: "anthropic",
            englishSpelling: "british",
            userSystemPrompt: "You are terse.",
            isDefault: true,
            sortOrder: 7,
            cloudAccuracyTier: "deepgramNova3",
            removeTrailingPeriod: true,
            geminiCustomPrompt: "Transcribe verbatim.",
            cloudPostProcessingModel: "anthropic:claude-haiku-4-5",
            cloudTranscriptionDomain: "medical",
            enableScreenOCR: true,
            useStreamingTranscription: false,
            foreignPlatformExtensions: #"{"windows":{"localEngine":"whisper"}}"#
        )
    }

    /// A mode with every optional field absent.
    private static func plainMode() -> BackupMode {
        BackupMode(
            id: plainModeId,
            name: "Percy 732 Plain",
            preset: "custom",
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
            sortOrder: 1,
            cloudAccuracyTier: nil,
            removeTrailingPeriod: nil,
            geminiCustomPrompt: nil,
            cloudPostProcessingModel: nil,
            cloudTranscriptionDomain: nil
        )
    }

    private static func vocabulary() -> [UniversalVocabularyItem] {
        [
            UniversalVocabularyItem(id: wordId, word: "HyperWhisper", replacement: "Hyper Whisper", sortOrder: 3, source: "manual"),
            UniversalVocabularyItem(id: secondWordId, word: "teh", replacement: nil, sortOrder: 0, source: nil),
        ]
    }

    nonisolated private static func json<T: Encodable>(_ value: T) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return String(decoding: try encoder.encode(value), as: UTF8.self)
    }

    /// The fixture file, built by the export steps, plus the two views of it
    /// `importUniversalV2` reads: the decoded envelope and the raw top level.
    private struct Fixture {
        let data: Data
        let filename: String
        let dto: UniversalBackupDTO
        let topLevel: [String: Any]
    }

    private func exportFixture() throws -> Fixture {
        // Own instance, never `.shared` — see `BackupManager.init`.
        let manager = BackupManager()
        let sections = try #require(manager.universalV2SettingsSections(
            from: Self.settings(true),
            storedForeignTopLevelExtensions: Self.storedForeign
        ))
        let dto = BackupManager.universalBackupDTO(
            settings: sections.settings,
            platformExtensions: sections.platformExtensions,
            modes: [Self.richMode(), Self.plainMode()],
            vocabulary: Self.vocabulary(),
            apiKeys: BackupManager.universalAPIKeyMap { Self.apiKeys[$0] },
            licenseKey: nil,
            appVersion: "9.9.9"
        )
        let file = try #require(manager.encodedBackupV2(dto, dateString: "2026-10-20"))
        #expect(manager.lastError == nil)

        let decoded = try JSONDecoder().decode(UniversalBackupDTO.self, from: file.data)
        let topLevel = try #require(try JSONSerialization.jsonObject(with: file.data) as? [String: Any])
        return Fixture(data: file.data, filename: file.filename, dto: decoded, topLevel: topLevel)
    }

    // MARK: - Envelope

    @Test func theExportIsAValidV2File() throws {
        let fixture = try exportFixture()
        #expect(fixture.filename == "HyperWhisper-Backup-2026-10-20.hwbackup.json")
        #expect(validateBackupJson(json: String(decoding: fixture.data, as: UTF8.self)).isEmpty)
        #expect(fixture.dto.schemaVersion == UniversalBackupDTO.universalSchemaVersion)
        #expect(fixture.dto.platform == "macos")
        #expect(fixture.dto.appVersion == "9.9.9")
        #expect(fixture.dto.licenseKey == nil)
        // The importer takes the full v2 path for this file, not the vocab-only one.
        #expect((fixture.topLevel["schemaVersion"] as? Int) == UniversalBackupDTO.universalSchemaVersion)
        #expect(fixture.topLevel["settings"] is [String: Any])
        #expect(fixture.topLevel["modes"] is [Any])
    }

    // MARK: - Settings

    @Test func everySettingRoundTripsOverADifferentBaseline() throws {
        let fixture = try exportFixture()
        let settingsValue = try #require(fixture.dto.settings)
        let imported = try BackupManager.macosSettings(
            fromV2: settingsValue,
            platformExtensions: fixture.dto.platformExtensions,
            baseline: Self.settings(false)
        )

        #expect(try Self.json(imported) == Self.json(Self.settings(true)))
        // A few by name, so a failure says which side won.
        #expect(imported.general.launchAtLogin == true)          // macOS-only, parked
        #expect(imported.general.launchMinimized == true)        // universal general
        #expect(imported.audio.enableSoundEffects == false)      // universal general
        #expect(imported.audio.soundEffectsVolume == 0.8)        // macOS-only, parked
        #expect(imported.textOutput.clipboardRestoreDelaySeconds == 1.5)
        #expect(imported.storage.storeAsM4A == false)
        #expect(imported.advanced.keepAudioFiles == true)        // universal storage
        #expect(imported.advanced.maxRecordingDuration == 1200)  // universal advanced
        #expect(imported.shortcuts.keyboardShortcuts == Self.settings(true).shortcuts.keyboardShortcuts)
        #expect(imported.aiModel.defaultModelByMode == [Self.richModeId.uuidString: "large-v3"])
    }

    /// A file without the macOS slice (a Windows backup) restores the universal
    /// fields and keeps the baseline's macOS-only ones. Fails if the merge runs
    /// the wrong way round or the universal categories are dropped.
    @Test func aFileWithoutTheMacosSliceKeepsTheBaselineMacOnlyValues() throws {
        let fixture = try exportFixture()
        let settingsValue = try #require(fixture.dto.settings)
        let baseline = Self.settings(false)
        let exported = Self.settings(true)
        let imported = try BackupManager.macosSettings(
            fromV2: settingsValue,
            platformExtensions: nil,
            baseline: baseline
        )

        // Universal fields: the file's values.
        #expect(imported.general.launchMinimized == exported.general.launchMinimized)
        #expect(imported.general.shareAnonymousSpeedData == exported.general.shareAnonymousSpeedData)
        #expect(imported.audio.enableSoundEffects == exported.audio.enableSoundEffects)
        #expect(imported.textOutput.removeFillerWords == exported.textOutput.removeFillerWords)
        #expect(imported.storage.storeAsM4A == exported.storage.storeAsM4A)
        #expect(imported.advanced.keepAudioFiles == exported.advanced.keepAudioFiles)
        #expect(imported.advanced.maxRecordingDuration == exported.advanced.maxRecordingDuration)
        // macOS-only fields: the baseline's values.
        #expect(imported.general.launchAtLogin == baseline.general.launchAtLogin)
        #expect(imported.general.showInDock == baseline.general.showInDock)
        #expect(imported.audio.soundEffectsVolume == baseline.audio.soundEffectsVolume)
        #expect(imported.storage.filesyncEnabled == baseline.storage.filesyncEnabled)
        #expect(imported.advanced.historyRetentionDays == baseline.advanced.historyRetentionDays)
        #expect(imported.aiModel.defaultLanguage == baseline.aiModel.defaultLanguage)
        #expect(imported.aiModel.defaultModelByMode == baseline.aiModel.defaultModelByMode)
    }

    @Test func theForeignTopLevelSlicesRoundTripAndOurOwnSliceIsWritten() throws {
        let fixture = try exportFixture()
        #expect(BackupManager.foreignTopLevelExtensions(from: fixture.dto.platformExtensions) == Self.storedForeign)
        #expect(fixture.dto.platformExtensions?.objectValue?["macos"]?.objectValue?["settings"] != nil)
    }

    // MARK: - Modes

    @Test func everyModeFieldRoundTrips() throws {
        let fixture = try exportFixture()
        let modes = BackupManager.backupModes(fromV2: try #require(fixture.dto.modes))
        #expect(modes.map(\.id) == [Self.richModeId, Self.plainModeId])

        let rich = try #require(modes.first { $0.id == Self.richModeId })
        #expect(rich.name == "Percy 732 Rich")
        #expect(rich.preset == "hyper")
        #expect(rich.language == "fr")
        #expect(rich.model == "large-v3")
        #expect(rich.punctuation == false)
        #expect(rich.capitalization == false)
        #expect(rich.profanityFilter == true)
        #expect(rich.customInstructions == "Keep it short.")
        #expect(rich.languageModel == "local-llm")
        #expect(rich.cloudProvider == "hyperwhisperCloud")
        #expect(rich.cloudTranscriptionModel == "nova-3")
        #expect(rich.postProcessingMode == 2)
        #expect(rich.postProcessingProvider == "anthropic")
        #expect(rich.englishSpelling == "british")
        #expect(rich.userSystemPrompt == "You are terse.")
        #expect(rich.isDefault == true)
        #expect(rich.sortOrder == 7)
        #expect(rich.cloudAccuracyTier == "deepgramNova3")
        #expect(rich.removeTrailingPeriod == true)
        #expect(rich.geminiCustomPrompt == "Transcribe verbatim.")
        #expect(rich.cloudPostProcessingModel == "anthropic:claude-haiku-4-5")
        #expect(rich.cloudTranscriptionDomain == "medical")
        #expect(rich.enableScreenOCR == true)
        #expect(rich.useStreamingTranscription == false)
        let foreign = try #require(rich.foreignPlatformExtensions)
        let foreignObject = try JSONSerialization.jsonObject(with: Data(foreign.utf8)) as? NSDictionary
        #expect(foreignObject == ["windows": ["localEngine": "whisper"]])

        // The whole mode, field for field, as the v1 encoder writes it.
        #expect(try Self.json(rich) == Self.json(Self.richMode()))
        let plain = try #require(modes.first { $0.id == Self.plainModeId })
        #expect(try Self.json(plain) == Self.json(Self.plainMode()))
    }

    @Test func aModeWhoseIdIsNotAUUIDIsSkipped() throws {
        let json = """
            [{"id":"not-a-uuid","name":"Broken"},
             {"id":"AA0E8400-E29B-41D4-A716-446655440010","name":"Fine"}]
            """
        let dtos = try JSONDecoder().decode([UniversalModeDTO].self, from: Data(json.utf8))
        let modes = BackupManager.backupModes(fromV2: dtos)
        #expect(modes.map(\.name) == ["Fine"])
    }

    // MARK: - Vocabulary

    @Test func everyVocabularyFieldRoundTrips() throws {
        let fixture = try exportFixture()
        let raw = try #require(fixture.topLevel["vocabulary"] as? [[String: Any]])
        let items = BackupManager.backupVocabularyItems(fromUniversal: raw)
        let exported = Self.vocabulary()
        #expect(items.count == exported.count)
        for (item, source) in zip(items, exported) {
            #expect(item.id == source.id)
            #expect(item.word == source.word)
            #expect(item.replacement == source.replacement)
            #expect(Int(item.sortOrder) == source.sortOrder)
            #expect(item.source == source.source)
        }
        #expect(items.first?.id == Self.wordId)
        #expect(items.first?.replacement == "Hyper Whisper")
    }

    @Test func vocabularyIsReadLeniently() {
        let items = BackupManager.backupVocabularyItems(fromUniversal: [
            ["word": "   "],
            ["id": "not-a-uuid", "word": "kept", "sortOrder": 40_000],
        ])
        #expect(items.map(\.word) == ["kept"])
        #expect(items.first?.sortOrder == Int16.max)
    }

    // MARK: - API keys and per-mode default models

    @Test func apiKeysRoundTripToTheirKeychainSlots() throws {
        let fixture = try exportFixture()
        let keys = try #require(fixture.dto.apiKeys)
        #expect(keys == ["openai": "percy732-openai-value", "geminitranscribe": "percy732-transcribe-value"])

        let assignments = BackupManager.universalAPIKeyAssignments(from: keys)
        #expect(assignments.map { $0.provider } == [.openAI, .geminiTranscribe])
        #expect(assignments.map { $0.key } == ["percy732-openai-value", "percy732-transcribe-value"])
    }

    /// `defaultModelByModeFromExtensions` reads the map where the export parks
    /// it, `platformExtensions.macos.settings.aiModel.defaultModelByMode`
    /// (#2207), and falls back to a flat `settings.defaultModelByMode`.
    @Test func theParkedDefaultModelMapIsRead() throws {
        let id = Self.richModeId.uuidString
        let fixture = try exportFixture()
        #expect(BackupManager.defaultModelByModeFromExtensions(fixture.dto.platformExtensions) == [id: "large-v3"])

        let flat = JSONValue.object([
            "macos": .object(["settings": .object([
                "defaultModelByMode": .object([id: .string("large-v3"), "skipped": .number(1)]),
            ])]),
        ])
        #expect(BackupManager.defaultModelByModeFromExtensions(flat) == [id: "large-v3"])
        #expect(BackupManager.defaultModelByModeFromExtensions(nil) == nil)
        #expect(BackupManager.defaultModelByModeFromExtensions(.object(["windows": .object([:])])) == nil)
    }

    // MARK: - importUniversalV2 restores the per-mode default models (#2207)

    /// The settings as an import sees them. Like the live store, `apply` does
    /// not write `defaultModelByMode`: only the import's remap step does.
    @MainActor
    private final class RecordingSettingsStore: BackupImportSettingsStore {
        private(set) var live: BackupSettings
        var defaultModelByMode: [String: String]
        var foreignTopLevelExtensions: String?

        init(live: BackupSettings) {
            self.live = live
            self.defaultModelByMode = live.aiModel.defaultModelByMode
        }

        func current() async -> BackupSettings { live }
        func apply(_ settings: BackupSettings) async { live = settings }
        func restore(_ settings: BackupSettings) async {
            live = settings
            defaultModelByMode = settings.aiModel.defaultModelByMode
        }
    }

    private static let localModeId = UUID()

    /// Imports the export fixture through `importUniversalV2` into an
    /// in-memory store that already holds a mode named like the rich mode,
    /// under another id.
    private func importFixture(
        modeConflict: ModeConflictResolution
    ) async throws -> (result: ImportResult, store: RecordingSettingsStore, persistence: PersistenceController) {
        let persistence = PersistenceController(inMemory: true)
        _ = persistence.createOrUpdateMode(
            id: Self.localModeId, name: "Percy 732 Rich", preset: "custom", language: "en", model: "base",
            punctuation: true, capitalization: true, profanityFilter: false
        )
        try persistence.container.viewContext.save()

        let store = RecordingSettingsStore(live: Self.settings(false))
        // Own instance, never `.shared` — see `BackupManager.init`.
        let backup = BackupManager()
        backup.importPersistence = persistence
        backup.settingsStore = store

        var options = ImportOptions()
        options.modeConflict = modeConflict
        options.importAPIKeys = false
        options.importLicenseKey = false

        let fixture = try exportFixture()
        let result = await backup.importUniversalV2(
            jsonData: fixture.data,
            rawString: String(decoding: fixture.data, as: UTF8.self),
            topLevel: fixture.topLevel,
            options: options
        )
        return (result, store, persistence)
    }

    /// The issue's "Done when": a v2 file built by the export steps, imported
    /// with a mode-id remap (Keep Both gives the rich mode's copy a new id),
    /// leaves the per-mode default model on the NEW id.
    @Test func keepBothRemapsTheDefaultModelMapToTheNewModeId() async throws {
        let (result, store, persistence) = try await importFixture(modeConflict: .keepBoth)
        #expect(result.success)
        #expect(result.settingsApplied)

        let copy = try #require(persistence.fetchAllModes().first { $0.name == "Percy 732 Rich (imported)" })
        let newId = try #require(copy.id)
        #expect(newId != Self.richModeId)
        #expect(newId != Self.localModeId)
        #expect(store.defaultModelByMode == [newId.uuidString: "large-v3"])
    }

    /// Replace keeps the backup's ids, so the map is restored as written.
    @Test func replaceRestoresTheDefaultModelMap() async throws {
        let (result, store, persistence) = try await importFixture(modeConflict: .replace)
        #expect(result.success)
        #expect(persistence.fetchAllModes().contains { $0.id == Self.richModeId })
        #expect(store.defaultModelByMode == [Self.richModeId.uuidString: "large-v3"])
    }
}
