//
//  BackupImportSuccessMessageTests.swift
//  hyperwhisperTests
//
//  Issue #1406: a settings-only backup import said "Import complete: 0 modes, 0 vocabulary
//  items imported", which reads as if nothing was imported. The success message now lists
//  only settings when they were applied and modes and vocabulary when they were chosen. It
//  makes no claim about API keys or the license key, and with nothing to list it is a plain
//  "Import complete".
//

import Foundation
import Testing
@testable import HyperWhisper

struct BackupImportSuccessMessageTests {

    private func options(settings: Bool, modes: Bool, vocabulary: Bool,
                         apiKeys: Bool = false, license: Bool = false) -> ImportOptions {
        ImportOptions(
            importSettings: settings,
            importModes: modes,
            importVocabulary: vocabulary,
            modeConflict: .replace,
            vocabularyConflict: .skip,
            importAPIKeys: apiKeys,
            importLicenseKey: license
        )
    }

    private func result(modes: Int = 0, vocabulary: Int = 0, settingsApplied: Bool,
                        apiKeys: Bool = false, license: Bool = false) -> ImportResult {
        var result = ImportResult.success(
            modesImported: modes,
            modesSkipped: 0,
            vocabularyImported: vocabulary,
            vocabularySkipped: 0,
            apiKeysImported: apiKeys,
            licenseKeyImported: license
        )
        result.settingsApplied = settingsApplied
        return result
    }

    // MARK: - Summary items

    @Test func settingsOnlyImportReportsOnlySettings() {
        let items = result(settingsApplied: true)
            .summaryItems(options: options(settings: true, modes: false, vocabulary: false))

        #expect(items == [.settings])
    }

    @Test func mixedImportReportsEveryChosenSectionInOrder() {
        let items = result(modes: 3, vocabulary: 12, settingsApplied: true)
            .summaryItems(options: options(settings: true, modes: true, vocabulary: true))

        #expect(items == [.settings, .modes(3), .vocabulary(12)])
    }

    @Test func modesAndVocabularyWithoutSettingsDoNotClaimSettings() {
        let items = result(modes: 2, vocabulary: 5, settingsApplied: false)
            .summaryItems(options: options(settings: false, modes: true, vocabulary: true))

        #expect(items == [.modes(2), .vocabulary(5)])
    }

    /// A chosen section whose items were all skipped as duplicates still reports its 0.
    @Test func aChosenSectionWithNothingNewStillReportsZero() {
        let items = result(settingsApplied: false)
            .summaryItems(options: options(settings: false, modes: false, vocabulary: true))

        #expect(items == [.vocabulary(0)])
    }

    /// The universal-v2 path continues when its settings step fails; selecting Settings
    /// alone must not make the message claim they were restored.
    @Test func selectedButNotAppliedSettingsAreNotReported() {
        let items = result(modes: 1, settingsApplied: false)
            .summaryItems(options: options(settings: true, modes: true, vocabulary: false))

        #expect(items == [.modes(1)])
    }

    /// Keys and the license are reported through the result's own failure text, not the
    /// summary, so the summary never lists them, imported or not.
    @Test func keysAndLicenseAreNeverListed() {
        let items = result(settingsApplied: true, apiKeys: true, license: true)
            .summaryItems(options: options(settings: true, modes: false, vocabulary: false, apiKeys: true, license: true))
        #expect(items == [.settings])

        let keysOnly = result(settingsApplied: false, apiKeys: true, license: true)
            .summaryItems(options: options(settings: false, modes: false, vocabulary: false, apiKeys: true, license: true))
        #expect(keysOnly.isEmpty)
    }

    @Test func nothingAppliedGivesAnEmptySummary() {
        let items = result(settingsApplied: false)
            .summaryItems(options: options(settings: true, modes: false, vocabulary: false))

        #expect(items.isEmpty)
    }

    // MARK: - Localized message (locale-independent checks)

    /// The settings-only message carries no count at all, in any language: the bug was a
    /// "0 modes, 0 vocabulary items" result for an import that restored the settings.
    @Test func settingsOnlyMessageHasNoCounts() {
        let message = result(settingsApplied: true)
            .successMessage(options: options(settings: true, modes: false, vocabulary: false))

        #expect(!message.isEmpty)
        #expect(!message.contains("0"))
        #expect(!message.contains("%"))
    }

    @Test func mixedMessageCarriesBothCounts() {
        let message = result(modes: 3, vocabulary: 12, settingsApplied: true)
            .successMessage(options: options(settings: true, modes: true, vocabulary: true))

        #expect(message.contains("3"))
        #expect(message.contains("12"))
        #expect(!message.contains("%"))
    }

    private var plainImportComplete: String {
        NSLocalizedString("settings.backup.import.complete", value: "Import complete", comment: "")
    }

    @Test func theEmptyCaseIsAPlainImportComplete() {
        let message = result(settingsApplied: false)
            .successMessage(options: options(settings: true, modes: false, vocabulary: false))

        #expect(!message.isEmpty)
        #expect(message == plainImportComplete)
        #expect(!message.contains("0"))
        #expect(!message.contains("%"))
    }

    /// A keys-only import: no count, and no "restored" claim about keys that may not have
    /// been written, just the plain "Import complete".
    @Test func keysOnlyImportSaysPlainImportComplete() {
        let message = result(settingsApplied: false, apiKeys: true, license: true)
            .successMessage(options: options(settings: false, modes: false, vocabulary: false, apiKeys: true, license: true))

        #expect(message == plainImportComplete)
        #expect(!message.contains("0"))
        #expect(!message.contains("%"))
        #expect(!message.contains("restored"))
        #expect(!message.contains("API"))
    }
}
