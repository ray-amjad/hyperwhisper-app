//
//  ModeEditorSeedTests.swift
//  hyperwhisperTests
//
//  First direct cover for the EDIT branch of the mode editor's seeding
//  (issue #730). `ModeEditorView.init` used to work these values out in place,
//  where no test could reach them; `ModeEditorSeed.make` is the same logic as
//  a pure function, so each migration it performs is asserted here by calling
//  it on a real Core Data `Mode`.
//
//  `Mode`'s Core Data attribute defaults are hostile (`cloudProvider` is
//  "openai", `cloudTranscriptionModel` is "whisper-1", `postProcessingProvider`
//  is "openai"), so every test sets the fields its case depends on explicitly,
//  including the ones it needs to be nil.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
@Suite("Mode editor EDIT seed")
struct ModeEditorSeedTests {

    // The controller is a PARAMETER and every caller holds it for the length of
    // the test: a store dropped inside this helper deallocates on return, the
    // Mode faults, and every attribute reads back as its zero value.
    private func makeMode(in persistence: PersistenceController) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = "Work"
        mode.preset = "hyper"
        mode.model = "cloud"
        mode.postProcessingMode = PostProcessingMode.cloud.rawValue
        mode.postProcessingProvider = nil
        mode.cloudProvider = CloudProvider.openai.rawValue
        mode.cloudTranscriptionModel = "whisper-1"
        mode.cloudAccuracyTier = CloudAccuracyTier.deepgramNova3.rawValue
        return mode
    }

    // MARK: - Legacy voiceToText preset

    @Test func theLegacyVoiceToTextPresetOpensAsHyperWithPostProcessingOff() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.preset = "voiceToText"
        mode.postProcessingMode = PostProcessingMode.cloud.rawValue

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.preset == "hyper")
        #expect(seed.postProcessingMode == .off)
    }

    @Test func anyOtherPresetAndItsPostProcessingModeAreKept() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.preset = "email"
        mode.postProcessingMode = PostProcessingMode.local.rawValue

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.preset == "email")
        #expect(seed.postProcessingMode == .local)
    }

    // MARK: - No saved transcription model

    /// A backup restore or a Local API PATCH can leave a mode with a provider
    /// and no model. It opens on THAT provider's default, not "whisper-1".
    @Test func noSavedModelFallsBackToTheSavedProvidersDefault() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.cloudProvider = CloudProvider.deepgram.rawValue

        for stored: String? in [nil, "", "   "] {
            mode.cloudTranscriptionModel = stored
            let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])
            #expect(seed.cloudTranscriptionModel == CloudTranscriptionModels.defaultModel(for: .deepgram))
            #expect(seed.cloudTranscriptionModel != "whisper-1")
            #expect(seed.cloudProvider == CloudProvider.deepgram.rawValue)
        }
    }

    @Test func noSavedModelOnAnUnknownProviderFallsBackToWhisper1() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.cloudProvider = "not-a-provider"
        mode.cloudTranscriptionModel = nil

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.cloudTranscriptionModel == "whisper-1")
    }

    @Test func aSavedModelIsKept() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.cloudProvider = CloudProvider.openai.rawValue
        mode.cloudTranscriptionModel = "gpt-4o-transcribe"

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.cloudTranscriptionModel == "gpt-4o-transcribe")
    }

    // MARK: - Cloud provider spelling

    /// Windows and Linux back up `cloudProvider` in camelCase. The provider
    /// picker tags its rows with the lowercase raw value, so the seed must
    /// canonicalise it or the menu renders blank.
    @Test func aWindowsCamelCaseProviderIsCanonicalised() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.cloudProvider = "geminiTranscribe"
        mode.cloudTranscriptionModel = "gemini-3.5-transcribe"

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.cloudProvider == CloudProvider.geminiTranscribe.rawValue)
        #expect(seed.cloudProvider == "geminitranscribe")
        // Not a legacy standalone provider, so the stored tier is untouched.
        #expect(seed.cloudAccuracyTier == CloudAccuracyTier.deepgramNova3.rawValue)
    }

    // MARK: - Legacy Azure / Google providers

    /// Azure MAI and Google Chirp were once standalone providers. They now
    /// live under HyperWhisper Cloud as accuracy tiers, and a Cloud
    /// post-processing mode with no stored provider gets HyperWhisper Cloud's.
    @Test func aLegacyAzureProviderFoldsIntoTheHyperWhisperCloudTier() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.cloudProvider = "microsoftAzureSpeech"
        mode.postProcessingProvider = nil

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.cloudProvider == CloudProvider.hyperwhisper.rawValue)
        #expect(seed.cloudAccuracyTier == CloudAccuracyTier.azureMaiTranscribe.rawValue)
        #expect(seed.postProcessingProvider == PostProcessingProvider.hyperwhisper.storageValue)
    }

    @Test func aLegacyGoogleProviderFoldsIntoTheHyperWhisperCloudTier() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.cloudProvider = "googlespeech"

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.cloudProvider == CloudProvider.hyperwhisper.rawValue)
        #expect(seed.cloudAccuracyTier == CloudAccuracyTier.geminiTranscribe.rawValue)
    }

    @Test func aStoredPostProcessingProviderIsKeptOnALegacyProvider() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.cloudProvider = "microsoftAzureSpeech"
        mode.postProcessingProvider = PostProcessingProvider.anthropic.rawValue

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: [])

        #expect(seed.postProcessingProvider == PostProcessingProvider.anthropic.rawValue)
    }

    // MARK: - Stored local model (issue #1434)

    @Test func aStoredLocalModelThatIsNotInstalledIsKeptAndReportedMissing() {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeMode(in: persistence)
        mode.model = "large-v3"

        let seed = ModeEditorSeed.make(mode: mode, availableModelIds: ["base"])

        #expect(seed.provider == .local)
        #expect(seed.model == "large-v3")
        #expect(seed.missingLocalModelId == "large-v3")
    }
}
