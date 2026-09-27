//
//  ModeEditorDefaultsTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #873: Create New Mode opened on HyperWhisper
//  Cloud for an unlicensed user with a local model installed, and the new
//  mode was made active at once, so the next dictation needed a key the
//  profile did not have.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Mode editor create defaults")
struct ModeEditorDefaultsTests {

    @Test func unlicensedWithLocalModelOpensOnDevice() {
        #expect(ModeEditorDefaults.initialProvider(licenseActive: false, availableModelIds: ["base.en"]) == .local)
    }

    @Test func licensedKeepsCloudDefault() {
        #expect(ModeEditorDefaults.initialProvider(licenseActive: true, availableModelIds: ["base.en"]) == .cloud)
    }

    @Test func unlicensedWithNoLocalModelKeepsCloudDefault() {
        #expect(ModeEditorDefaults.initialProvider(licenseActive: false, availableModelIds: []) == .cloud)
    }

    @Test func licensedWithNoLocalModelKeepsCloudDefault() {
        #expect(ModeEditorDefaults.initialProvider(licenseActive: true, availableModelIds: []) == .cloud)
    }

    @Test func unlicensedWithOnlyAppleSpeechOpensOnDevice() {
        // macOS 26 lists Apple Speech on every Mac; it needs no key.
        #expect(ModeEditorDefaults.initialProvider(
            licenseActive: false,
            availableModelIds: [ModeEditorDefaults.appleSpeechAnalyzerId]
        ) == .local)
    }

    // MARK: - Licence signal

    @Test func activeStatusIsLicensed() {
        #expect(ModeEditorDefaults.treatsLicenseAsActive(status: .active, storedKey: .present("HW-KEY")))
        #expect(ModeEditorDefaults.treatsLicenseAsActive(status: .active, storedKey: .missing))
    }

    @Test func trialWithAStoredKeyIsLicensedWhileTheStatusResolves() {
        // licenseStatus is .trial until loadStoredLicense() publishes a
        // verdict; a key holder who opens the sheet then keeps Cloud.
        #expect(ModeEditorDefaults.treatsLicenseAsActive(status: .trial, storedKey: .present("HW-KEY")))
    }

    @Test func trialWithAnUnreadableKeychainKeepsTheCloudSeed() {
        #expect(ModeEditorDefaults.treatsLicenseAsActive(status: .trial, storedKey: .unavailable))
    }

    @Test func trialWithNoStoredKeyIsUnlicensed() {
        #expect(!ModeEditorDefaults.treatsLicenseAsActive(status: .trial, storedKey: .missing))
    }

    @Test func aKeyTheServerRefusedIsUnlicensed() {
        #expect(!ModeEditorDefaults.treatsLicenseAsActive(status: .expired, storedKey: .present("HW-KEY")))
        #expect(!ModeEditorDefaults.treatsLicenseAsActive(status: .invalid, storedKey: .present("HW-KEY")))
    }

    // MARK: - Post-processing

    @Test func unlicensedOnDeviceSeedTurnsPostProcessingOff() {
        // HyperWhisper Cloud post-processing needs the same key; round 1 of
        // #1070 still seeded it, so the first dictation hit /post-process.
        #expect(ModeEditorDefaults.initialPostProcessingMode(
            licenseActive: false,
            availableModelIds: ["base.en"]
        ) == .off)
    }

    @Test func everyCloudSeedKeepsCloudPostProcessing() {
        #expect(ModeEditorDefaults.initialPostProcessingMode(licenseActive: true, availableModelIds: ["base.en"]) == .cloud)
        #expect(ModeEditorDefaults.initialPostProcessingMode(licenseActive: true, availableModelIds: []) == .cloud)
        #expect(ModeEditorDefaults.initialPostProcessingMode(licenseActive: false, availableModelIds: []) == .cloud)
    }

    // MARK: - Model order

    @Test func sortsInOnDevicePickerOrder() {
        let sorted = ModeEditorDefaults.sortedLocalModelIds([
            "base", "unknown-model", "parakeet-tdt-0.6b-v3", "tiny.en",
            ModeEditorDefaults.appleSpeechAnalyzerId, "qwen3-asr-0.6b",
        ])
        #expect(sorted == [
            ModeEditorDefaults.appleSpeechAnalyzerId, "parakeet-tdt-0.6b-v3", "qwen3-asr-0.6b",
            "tiny.en", "base", "unknown-model",
        ])
    }

    @Test func seedsANonWhisperModelAheadOfWhisperLikeThePicker() {
        // The old seed ranked Whisper first, so it disagreed with the list.
        #expect(ModeEditorDefaults.initialLocalModel(availableModelIds: ["base", "parakeet-tdt-0.6b-v3"])
            == "parakeet-tdt-0.6b-v3")
    }

    @Test func seedPassesOverAppleSpeechWhenTheUserDownloadedAModel() {
        // Issue #873's own profile on macOS 26: Whisper Base from onboarding,
        // plus Apple Speech listed by the system.
        #expect(ModeEditorDefaults.initialLocalModel(
            availableModelIds: [ModeEditorDefaults.appleSpeechAnalyzerId, "base"]
        ) == "base")
    }

    @Test func seedsAppleSpeechWhenItIsTheOnlyModel() {
        #expect(ModeEditorDefaults.initialLocalModel(
            availableModelIds: [ModeEditorDefaults.appleSpeechAnalyzerId]
        ) == ModeEditorDefaults.appleSpeechAnalyzerId)
    }

    @Test func seedsBaseWhenNothingIsInstalled() {
        #expect(ModeEditorDefaults.initialLocalModel(availableModelIds: []) == "base")
    }

    // MARK: - Language

    @Test func englishOnlyOnDeviceSeedOpensOnEnglish() {
        // The save path persists "en" for a .en model; the picker must show it.
        #expect(ModeEditorDefaults.initialLanguage(provider: .local, model: "base.en") == "en")
        #expect(ModeEditorDefaults.initialLanguage(provider: .local, model: "parakeet-tdt-0.6b-v2") == "en")
    }

    @Test func multilingualOrCloudSeedOpensOnAutomatic() {
        #expect(ModeEditorDefaults.initialLanguage(provider: .local, model: "base") == LanguageData.automaticCode)
        #expect(ModeEditorDefaults.initialLanguage(provider: .cloud, model: "base.en") == LanguageData.automaticCode)
    }

    // MARK: - The CREATE branch reads the resolver

    private static let editorPath = "app/macos/hyperwhisper/Views/Modes/ModeEditorView.swift"

    @Test func theCreateBranchSeedsFromTheResolver() throws {
        let create = try ProductionSource.slice(
            of: Self.editorPath,
            from: "let seededProvider = ModeEditorDefaults.initialProvider(",
            to: "private func sortedModelIds() -> [String] {"
        )
        #expect(create.contains("let seededModel = ModeEditorDefaults.initialLocalModel(availableModelIds: availableModelIds)"))
        #expect(create.contains("_language = State(initialValue: ModeEditorDefaults.initialLanguage("))
        #expect(create.contains("_postProcessingMode = State(initialValue: ModeEditorDefaults.initialPostProcessingMode("))
        #expect(create.contains("_provider = State(initialValue: seededProvider)"))
        #expect(create.contains("_model = State(initialValue: seededModel)"))
        #expect(!create.contains("_postProcessingMode = State(initialValue: .cloud)"))
        #expect(!create.contains("_provider = State(initialValue: .cloud)"))
    }

    @Test func thePickerSortsWithTheSameTable() throws {
        let sort = try ProductionSource.slice(
            of: Self.editorPath,
            from: "private func sortedModelIds() -> [String] {",
            to: "private func displayName(for id: String) -> String {"
        )
        #expect(sort.contains("ModeEditorDefaults.sortedLocalModelIds(availableModelIds)"))
    }

    // MARK: - ModesView passes the licence to the CREATE sheet

    @Test func theCreateSheetGetsTheResolvedLicenceSignal() throws {
        let sheet = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Views/Modes/ModesView.swift",
            from: ".sheet(isPresented: $showingCreateMode) {",
            to: ") { (newModeData: ModeData) in"
        )
        #expect(sheet.contains("configuration: .create"))
        #expect(sheet.contains("licenseActive: ModeEditorDefaults.treatsLicenseAsActive("))
        #expect(sheet.contains("status: licenseManager.licenseStatus"))
        #expect(sheet.contains("storedKey: licenseManager.storedLicenseKeyReadForSeeding()"))
        // Without the argument the init default (false) seeds On-device for
        // every user, licensed or not.
        #expect(!sheet.contains("licenseActive: false"))
    }
}
