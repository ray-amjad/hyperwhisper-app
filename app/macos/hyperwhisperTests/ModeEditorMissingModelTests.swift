//
//  ModeEditorMissingModelTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1434: the Edit Mode sheet opened on the first
//  installed model when the mode's stored local model was not installed, so
//  Save with no change rewrote `base` to `base.en`, and the English-only
//  substitute also rewrote the language from Automatic to `en`.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Mode editor keeps a missing local model")
struct ModeEditorMissingModelTests {

    /// Opens the EDIT sheet on `storedModel` and presses Save with no change:
    /// the init seed, the onAppear clamp, then the values Save persists.
    private func saveUnchanged(
        storedModel: String?,
        language: String,
        availableModelIds: [String]
    ) -> (model: String, language: String, missing: String?) {
        let selection = ModeEditorDefaults.editModelSelection(
            storedModel: storedModel,
            availableModelIds: availableModelIds
        )
        var model = selection.model
        if selection.provider == .local {
            model = ModeEditorDefaults.onDeviceModel(
                current: model,
                availableModelIds: availableModelIds,
                missingLocalModelId: selection.missingLocalModelId
            )
        }
        let saved = ModeEditorDefaults.savedModel(
            provider: selection.provider,
            model: model,
            availableModelIds: availableModelIds
        )
        let savedLanguage = ModeEditorDefaults.savedLanguage(
            provider: selection.provider,
            savedModel: saved,
            language: language
        )
        return (saved, savedLanguage, selection.missingLocalModelId)
    }

    // MARK: - The issue's repro

    @Test func unchangedSaveKeepsMissingMultilingualModelAndAutomaticLanguage() {
        let result = saveUnchanged(
            storedModel: "base",
            language: LanguageData.automaticCode,
            availableModelIds: ["base.en"]
        )
        #expect(result.model == "base")
        #expect(result.language == LanguageData.automaticCode)
        #expect(result.missing == "base")
    }

    @Test func unchangedSaveKeepsMissingModelWhenNothingIsInstalled() {
        // Used to flip the mode to Cloud, so Save wrote "cloud".
        let selection = ModeEditorDefaults.editModelSelection(storedModel: "small", availableModelIds: [])
        #expect(selection.provider == .local)
        #expect(selection.model == "small")
        #expect(selection.missingLocalModelId == "small")
        #expect(ModeEditorDefaults.localPickerModelIds(
            availableModelIds: [],
            missingLocalModelId: selection.missingLocalModelId
        ) == ["small"])
        #expect(saveUnchanged(storedModel: "small", language: "de", availableModelIds: []).model == "small")
    }

    // MARK: - Picker rows

    @Test func pickerListsMissingModelFirstThenInstalledInPickerOrder() {
        let rows = ModeEditorDefaults.localPickerModelIds(
            availableModelIds: ["small.en", "base.en"],
            missingLocalModelId: "base"
        )
        #expect(rows == ["base", "base.en", "small.en"])
    }

    @Test func pickerIsUnchangedWhenNoModelIsMissing() {
        let ids = ["small.en", "base.en"]
        #expect(ModeEditorDefaults.localPickerModelIds(availableModelIds: ids, missingLocalModelId: nil)
            == ModeEditorDefaults.sortedLocalModelIds(ids))
    }

    // MARK: - Unaffected paths

    @Test func installedModelIsNotReportedMissing() {
        let selection = ModeEditorDefaults.editModelSelection(
            storedModel: "base",
            availableModelIds: ["base", "base.en"]
        )
        #expect(selection == ModeEditorDefaults.EditModelSelection(provider: .local, model: "base", missingLocalModelId: nil))
    }

    @Test func cloudModeIsNeverReportedMissing() {
        let selection = ModeEditorDefaults.editModelSelection(storedModel: "cloud", availableModelIds: [])
        #expect(selection == ModeEditorDefaults.EditModelSelection(provider: .cloud, model: "cloud", missingLocalModelId: nil))
        #expect(ModeEditorDefaults.savedModel(provider: .cloud, model: "cloud", availableModelIds: []) == "cloud")
    }

    @Test func aDeliberatePickSavesAsNormal() {
        // The user picks the installed English-only model: it saves, and the
        // language follows it to `en` as before.
        let saved = ModeEditorDefaults.savedModel(provider: .local, model: "base.en", availableModelIds: ["base.en"])
        #expect(saved == "base.en")
        #expect(ModeEditorDefaults.savedLanguage(
            provider: .local,
            savedModel: saved,
            language: LanguageData.automaticCode
        ) == "en")
    }

    // MARK: - Source toggle back to On-device

    @Test func returningToOnDeviceKeepsTheModesOwnMissingModel() {
        #expect(ModeEditorDefaults.onDeviceModel(
            current: "base",
            availableModelIds: ["base.en"],
            missingLocalModelId: "base"
        ) == "base")
    }

    @Test func cloudModeSwitchedToOnDeviceGetsAnInstalledModel() {
        // A Cloud mode has no missing local model; "cloud" is not a local row.
        #expect(ModeEditorDefaults.onDeviceModel(
            current: "cloud",
            availableModelIds: ["small.en", "base.en"],
            missingLocalModelId: nil
        ) == "base.en")
    }
}
