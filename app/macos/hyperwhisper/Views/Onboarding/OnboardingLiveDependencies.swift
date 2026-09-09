//
//  OnboardingLiveDependencies.swift
//  hyperwhisper
//
//  Live adapters that bind `OnboardingFlowModel`'s narrow seams to the app's
//  real managers. Everything here is thin plumbing on purpose: policy lives in
//  the flow model so it can be exercised without Core Data, the Keychain, a
//  microphone, or the network.
//

import AVFoundation
import AppKit
import Combine
import Foundation

// MARK: - Permissions

@MainActor
final class LiveOnboardingPermissions: OnboardingPermissionsChecking {
    private let audioManager: AudioRecordingManager

    init(audioManager: AudioRecordingManager) {
        self.audioManager = audioManager
    }

    var microphoneAuthorization: OnboardingMicrophoneAuthorization {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return .authorized
        case .notDetermined: return .undetermined
        default: return .denied
        }
    }

    var hasAccessibilityPermission: Bool {
        AccessibilityHelper.shared.hasAccessibilityPermission()
    }

    func requestMicrophonePermission() async -> Bool {
        await audioManager.requestMicrophonePermission()
    }

    func openMicrophoneSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") else { return }
        NSWorkspace.shared.open(url)
    }

    func openAccessibilitySettings() {
        AccessibilityHelper.shared.openAccessibilitySettings()
    }

    func waitForAccessibilityPermission(_ completion: @escaping (Bool) -> Void) {
        AccessibilityHelper.shared.waitForAccessibilityPermission(completion: completion)
    }
}

// MARK: - Model catalog

@MainActor
final class LiveOnboardingModelCatalog: OnboardingModelCatalog {
    private let whisper: WhisperModelManager
    private let parakeet: ParakeetModelManager

    init(whisper: WhisperModelManager, parakeet: ParakeetModelManager) {
        self.whisper = whisper
        self.parakeet = parakeet
    }

    var models: [OnboardingModelSelection] {
        OnboardingModelSelection.curated(whisper: whisper, parakeet: parakeet)
    }

    func isInstalled(_ model: OnboardingModelSelection) -> Bool {
        switch model.kind {
        case .whisper:
            return whisper.getModelPath(for: model.id) != nil
        case .parakeet:
            return parakeet.availableModels.first { $0.id == model.id }?.isDownloaded == true
        }
    }

    func isDownloading(_ model: OnboardingModelSelection) -> Bool {
        switch model.kind {
        case .whisper:
            return whisper.downloadingModels.contains(model.id)
        case .parakeet:
            return parakeet.downloads.isDownloading(model.id)
        }
    }

    func progress(for model: OnboardingModelSelection) -> Double {
        switch model.kind {
        case .whisper:
            return whisper.downloadProgress[model.id] ?? 0
        case .parakeet:
            return parakeet.downloads.progress[model.id] ?? 0
        }
    }

    /// Issue #312. Only the Parakeet path has a stage to give: `ParakeetModelManager`
    /// translates FluidAudio's `DownloadPhase` into `ModelDownloadStage` and puts it
    /// on the shared `DownloadController`. `WhisperModelManager` publishes a fraction
    /// and nothing else, so `nil` here is deliberate — it keeps Whisper's card on
    /// exactly the rendering it has today.
    func stage(for model: OnboardingModelSelection) -> ModelDownloadStage? {
        switch model.kind {
        case .whisper:
            return nil
        case .parakeet:
            return parakeet.downloads.stage[model.id]
        }
    }

    func startDownload(_ model: OnboardingModelSelection) {
        switch model.kind {
        case .whisper:
            guard let catalogModel = whisper.availableModels.first(where: { $0.name == model.id }) else { return }
            Task { await whisper.downloadModel(catalogModel) }
        case .parakeet:
            // Constants-derived ids only; `startDownload` silently ignores a typo.
            parakeet.startDownload(model.id)
        }
    }

    /// Bug 2: both engines are combined into one stream so the flow can pick the
    /// message that belongs to the selected model instead of only ever showing
    /// Whisper's.
    var downloadErrors: AnyPublisher<OnboardingDownloadErrors, Never> {
        whisper.$errorMessage
            .combineLatest(parakeet.$errorMessage)
            .map { OnboardingDownloadErrors(whisper: $0, parakeet: $1) }
            .removeDuplicates()
            .eraseToAnyPublisher()
    }

    /// Bug 2: `ParakeetModelManager.downloads` is a nested ObservableObject, so
    /// its ticks never reach the flow's observers on their own. Progress streams
    /// are throttled here rather than in the flow so the unit tests stay
    /// synchronous, mirroring `ModelLibraryManager.configure`.
    var downloadActivity: AnyPublisher<Void, Never> {
        let immediate: [AnyPublisher<Void, Never>] = [
            whisper.$downloadingModels.map { _ in () }.eraseToAnyPublisher(),
            parakeet.downloads.$downloading.map { _ in () }.eraseToAnyPublisher(),
        ]
        let throttled: [AnyPublisher<Void, Never>] = [
            whisper.$downloadProgress.map { _ in () }.eraseToAnyPublisher(),
            parakeet.downloads.$progress.map { _ in () }.eraseToAnyPublisher(),
            // Issue #312: the stage changes without the fraction moving (the whole
            // download sits inside one stage), so it needs its own tick or the
            // phase line never refreshes.
            parakeet.downloads.$stage.map { _ in () }.eraseToAnyPublisher(),
        ].map {
            $0.throttle(for: .milliseconds(200), scheduler: DispatchQueue.main, latest: true)
                .eraseToAnyPublisher()
        }
        return Publishers.MergeMany(immediate + throttled).eraseToAnyPublisher()
    }
}

// MARK: - License

@MainActor
final class LiveOnboardingLicenseGateway: OnboardingLicenseGateway {
    private let manager: LicenseManager

    init(manager: LicenseManager) {
        self.manager = manager
    }

    var isActive: Bool { manager.licenseStatus == .active }
    var isValidating: Bool { manager.isValidating }
    var lastError: String? { manager.lastError }

    /// Read only. Never mutates account state.
    func probe(_ key: String) async -> OnboardingLicenseOutcome {
        let result = await manager.probeLicense(key)
        return OnboardingLicenseOutcome(
            isValid: result.isValid,
            errorMessage: result.isValid ? nil : (result.errorMessage ?? "app.unknown.error".localized)
        )
    }

    /// The single explicit activation. Entitlement is verified server side; there
    /// is no local shortcut here and there must never be one.
    func activate(_ key: String) async -> OnboardingLicenseOutcome {
        let result = await manager.activateLicense(key)
        return OnboardingLicenseOutcome(
            isValid: result.isValid,
            errorMessage: result.isValid ? nil : (result.errorMessage ?? "app.unknown.error".localized)
        )
    }
}

// MARK: - Provider keys

@MainActor
final class LiveOnboardingProviderKeyGateway: OnboardingProviderKeyGateway {
    private let settingsManager: SettingsManager
    private let cloudHealth: CloudProviderHealthManager

    init(settingsManager: SettingsManager, cloudHealth: CloudProviderHealthManager) {
        self.settingsManager = settingsManager
        self.cloudHealth = cloudHealth
    }

    var validationError: String? { settingsManager.apiKeys.validationError }

    func probe(_ provider: CloudProvider, apiKey: String) async -> ProviderHealth {
        await cloudHealth.probe(provider, apiKey: apiKey)
    }

    @discardableResult
    func persist(_ key: String, for provider: CloudProvider) -> Bool {
        let saved = settingsManager.apiKeys.setAPIKey(key, for: provider)
        if saved {
            cloudHealth.registerAPIKeyChange(for: provider, newValue: key)
        }
        return saved
    }

    func hasKey(for provider: CloudProvider) -> Bool {
        settingsManager.apiKeys.hasAPIKey(for: provider)
    }

    /// Reads back the stored key so the flow can restore it on deferral.
    /// `setAPIKey("")` deletes the Keychain entry, so "" round trips correctly
    /// for a provider that had no key.
    func currentKey(for provider: CloudProvider) -> String {
        settingsManager.apiKeys.apiKey(for: provider)
    }
}

// MARK: - Audio

@MainActor
final class LiveOnboardingAudioGateway: OnboardingAudioGateway {
    private let audioManager: AudioRecordingManager
    private let settingsManager: SettingsManager
    private let appState: AppState

    init(audioManager: AudioRecordingManager, settingsManager: SettingsManager, appState: AppState) {
        self.audioManager = audioManager
        self.settingsManager = settingsManager
        self.appState = appState
    }

    var devices: [OnboardingInputDevice] {
        audioManager.availableDevices.map { OnboardingInputDevice(id: $0.id, name: $0.name) }
    }

    var devicesPublisher: AnyPublisher<[OnboardingInputDevice], Never> {
        audioManager.$availableDevices
            .map { $0.map { OnboardingInputDevice(id: $0.id, name: $0.name) } }
            .eraseToAnyPublisher()
    }

    var selectedDeviceID: String? { audioManager.selectedDevice?.id }

    var storedDeviceID: String? {
        let stored = settingsManager.selectedMicrophoneId
        return stored.isEmpty ? nil : stored
    }

    func refreshDevices() { audioManager.updateAvailableDevices() }

    func refreshMicrophonePermission() { audioManager.checkMicrophonePermission() }

    func selectDevice(id: String?) {
        guard let id, !id.isEmpty else {
            audioManager.selectDevice(nil)
            settingsManager.selectedMicrophoneId = ""
            return
        }
        guard let device = audioManager.availableDevices.first(where: { $0.id == id }) else { return }
        audioManager.selectDevice(device)
        settingsManager.selectedMicrophoneId = id
    }

    func restoreDevice(storedID: String?, openID: String?) {
        // Put the preference back unconditionally. Routing this through
        // `selectDevice` would drop it when the remembered microphone is not
        // currently connected, turning a deferral into a silent reset to the
        // system default.
        settingsManager.selectedMicrophoneId = storedID ?? ""
        if let openID, let device = audioManager.availableDevices.first(where: { $0.id == openID }) {
            audioManager.selectDevice(device)
        } else {
            audioManager.selectDevice(nil)
        }
    }

    func startInputLevelPreview() { audioManager.startInputLevelPreview() }

    func stopInputLevelPreview() { audioManager.stopInputLevelPreview() }

    func toggleTestRecording() {
        // The `.onboarding` trigger routes the transcript inline. It is never
        // delivered into another app.
        audioManager.toggleRecordingWithTranscription(trigger: .onboarding)
    }

    func stopRecordingForExit() {
        // Not gated on isRecording: streaming capture carries separate state, and
        // stopOnly is a no-op when both are idle.
        audioManager.toggleRecordingWithTranscription(stopOnly: true, trigger: .onboarding)
    }

    func clearTranscript() { appState.lastTranscription = "" }

    var isRecordingPublisher: AnyPublisher<Bool, Never> {
        audioManager.$isRecording.eraseToAnyPublisher()
    }

    var transcriptPublisher: AnyPublisher<String, Never> {
        appState.$lastTranscription.eraseToAnyPublisher()
    }
}

// MARK: - Commit

/// Everything `apply` overwrites, captured before the first write so Set Up
/// Later can put production state back byte for byte.
struct LiveOnboardingRestorePoint: OnboardingRestorePoint {
    let modeExisted: Bool
    let modeID: UUID
    let name: String
    let preset: String
    let language: String
    let model: String
    let punctuation: Bool
    let capitalization: Bool
    let profanityFilter: Bool
    let customInstructions: String?
    let languageModel: String?
    let cloudProvider: String?
    let cloudTranscriptionModel: String?
    let postProcessingMode: Int16
    let postProcessingProvider: String?
    let englishSpelling: String?
    let userSystemPrompt: String?
    let useStreamingTranscription: Bool
    let cloudAccuracyTier: String?
    let removeTrailingPeriod: Bool
    let enableScreenOCR: Bool
    let geminiCustomPrompt: String?
    let cloudPostProcessingModel: String?
    let cloudTranscriptionDomain: String?
    let foreignPlatformExtensions: String?
    let wasDefault: Bool
    let previousSelection: ModeSnapshot?
}

@MainActor
final class LiveOnboardingSourceCommitter: OnboardingSourceCommitting {
    /// Well-known UUID of the seeded default Mode (see
    /// `PersistenceController.initializeDefaultModes()`).
    static let defaultModeID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

    /// The shared first-run seed, in macOS' own column names.
    ///
    /// Onboarding no longer uses this to CREATE the default mode — that is
    /// `PersistenceController.seedDefaultMode()`, the same `applySeededValues`
    /// the first-launch seeder uses, so there is no second list of seeded values
    /// to keep in step. What is left here is the nil-coalescing for Core Data
    /// columns that are optional in Swift, plus `captureRestorePoint`'s inert
    /// arms. `hw-catalog::mode_seed` remains the one definition all three heads
    /// read.
    ///
    /// Resolved per access rather than stored, because `forCurrentRegion` reads
    /// `Locale.current` and onboarding is exactly when a fresh install first
    /// observes it.
    ///
    /// Internal rather than private so `OnboardingSeededDefaultsTests` can pin
    /// it against a Core Data row that `initializeDefaultModes()` really wrote.
    static var seed: SeededModeValues { SeededModeValues.forCurrentRegion }

    private let persistence: PersistenceController
    private let appState: AppState

    init(persistence: PersistenceController = .shared, appState: AppState) {
        self.persistence = persistence
        self.appState = appState
    }

    func captureRestorePoint() -> OnboardingRestorePoint {
        let existing = persistence.findDefaultMode()
        return LiveOnboardingRestorePoint(
            modeExisted: existing != nil,
            modeID: existing?.id ?? Self.defaultModeID,
            // These four `??` arms are INERT — and kept identical to `apply`'s
            // anyway. They yield a value only when `existing` is nil, which is
            // exactly `modeExisted == false`, and on that path `restore` DELETES
            // the row `apply` created rather than writing a single field back
            // (see the `if !point.modeExisted` branch). So this changes no
            // rollback behaviour; an earlier comment here claimed `restore`
            // writes these back, and that is not what the code does.
            //
            // Sourced from the shared seed regardless, because the pre-seed
            // literals macOS used to carry (`"en"`, `"base"`) are values the
            // seeder can no longer produce, and a later `restore` that did write
            // on that path should not be able to resurrect them.
            name: existing?.name ?? SeededModeValues.seededName,
            preset: existing?.preset ?? Self.seed.preset,
            language: existing?.language ?? Self.seed.language,
            model: existing?.model ?? Self.seed.model,
            punctuation: existing?.punctuation ?? true,
            capitalization: existing?.capitalization ?? true,
            profanityFilter: existing?.profanityFilter ?? false,
            customInstructions: existing?.customInstructions,
            languageModel: existing?.languageModel,
            cloudProvider: existing?.cloudProvider,
            cloudTranscriptionModel: existing?.cloudTranscriptionModel,
            postProcessingMode: existing?.postProcessingMode ?? 1,
            postProcessingProvider: existing?.postProcessingProvider,
            englishSpelling: existing?.englishSpelling,
            userSystemPrompt: existing?.userSystemPrompt,
            useStreamingTranscription: existing?.useStreamingTranscription ?? false,
            cloudAccuracyTier: existing?.cloudAccuracyTier,
            removeTrailingPeriod: existing?.removeTrailingPeriod ?? false,
            enableScreenOCR: existing?.enableScreenOCR ?? false,
            geminiCustomPrompt: existing?.geminiCustomPrompt,
            cloudPostProcessingModel: existing?.cloudPostProcessingModel,
            cloudTranscriptionDomain: existing?.cloudTranscriptionDomain,
            foreignPlatformExtensions: existing?.foreignPlatformExtensions,
            wasDefault: existing?.isDefault ?? false,
            previousSelection: appState.selectedModeSnapshot
        )
    }

    /// Write the chosen transcription source onto the default Mode, then select
    /// it. The Core Data half is `commitStagedSource` below; this adds only the
    /// `AppState` half.
    func apply(_ staged: OnboardingStagedSource) {
        let updated = Self.commitStagedSource(staged, to: persistence)

        // Writing the source onto Default is not enough on its own: a returning
        // user's selectedModeId still points at their own Mode, so the next
        // recording would keep using that Mode's source.
        appState.selectMode(updated, persist: true)
    }

    /// Everything `apply` does to Core Data, with no `AppState` in the way.
    ///
    /// Split out so it can actually be tested. Constructing a
    /// `LiveOnboardingSourceCommitter` requires an `AppState`, and building one
    /// instantiates `PersistenceController.shared` — the real on-disk store —
    /// from inside `setupSubscriptions()`, so a unit test can never reach
    /// `apply` itself. That difficulty is why the previous round's test compared
    /// the seed against a row written from that same seed and could not fail;
    /// `OnboardingSeededDefaultsTests` drives this instead.
    ///
    /// The row is RESOLVED before it is reconfigured, and never created by
    /// `createOrUpdateMode`:
    ///
    /// 1. the flagged default mode, if there is one;
    /// 2. otherwise a row already carrying the well-known seed UUID — this is
    ///    the row `createOrUpdateMode(id:)` used to adopt implicitly, so keeping
    ///    the step keeps that behaviour;
    /// 3. otherwise the shared first-run seed, written by the same
    ///    `applySeededValues` `initializeDefaultModes()` uses.
    ///
    /// Step 3 is the fix. `apply` does not only reconfigure the default mode, it
    /// CREATES it — when the flagged default is deleted while other modes remain
    /// (both delete guards only protect the LAST mode), or if the seeder's save
    /// failed. Routing that through `createOrUpdateMode` gave the row
    /// `isSystemProvided = false` and `sortOrder = maxSortOrder + 1` against the
    /// seed's `true` and `0`, permanently — `initializeDefaultModes()` returns
    /// early once any mode exists — so `GET /modes` reported that install's
    /// default as `isSystemProvided: false` at sortOrder 1.
    ///
    /// It also removes the shape that caused it: this method no longer restates
    /// any seeded value. Adding a field to `ModeSeed` now means editing
    /// `applySeededValues` alone, not a parallel arm list here that nothing
    /// would fail to compile without.
    @discardableResult
    static func commitStagedSource(
        _ staged: OnboardingStagedSource,
        to persistence: PersistenceController
    ) -> Mode {
        let flaggedDefault = persistence.findDefaultMode()
        let existing = flaggedDefault
            ?? persistence.fetchMode(withId: Self.defaultModeID.uuidString)
            ?? persistence.seedDefaultMode()

        // Every `existing.x ?? …` below is now only about Core Data's columns
        // being optional in Swift, NOT about the row being absent — the row is
        // guaranteed above, and if it was just seeded these are the seed's own
        // values. `model` is absent from the list on purpose: it is not a seeded
        // value here but the transcription source the user just chose, which is
        // the whole point of `apply`.
        let updated = persistence.createOrUpdateMode(
            id: existing.id ?? Self.defaultModeID,
            name: existing.name ?? SeededModeValues.seededName,
            preset: existing.preset ?? Self.seed.preset,
            language: existing.language ?? Self.seed.language,
            model: staged.model,
            punctuation: existing.punctuation,
            capitalization: existing.capitalization,
            profanityFilter: existing.profanityFilter,
            customInstructions: existing.customInstructions,
            languageModel: existing.languageModel,
            cloudProvider: staged.cloudProvider,
            postProcessingMode: staged.postProcessingMode,
            postProcessingProvider: existing.postProcessingProvider,
            englishSpelling: existing.englishSpelling,
            userSystemPrompt: existing.userSystemPrompt,
            useStreamingTranscription: existing.useStreamingTranscription,
            cloudAccuracyTier: staged.cloudAccuracyTier,
            removeTrailingPeriod: existing.removeTrailingPeriod,
            enableScreenOCR: existing.enableScreenOCR,
            geminiCustomPrompt: existing.geminiCustomPrompt,
            cloudPostProcessingModel: existing.cloudPostProcessingModel,
            cloudTranscriptionDomain: existing.cloudTranscriptionDomain,
            foreignPlatformExtensions: existing.foreignPlatformExtensions
        )

        // Only when nothing was flagged default: otherwise this could flag a
        // second one. `createOrUpdateMode` does not set the flag on a row it
        // adopts by id, which would leave the chosen source on a stray
        // non-default Mode.
        if flaggedDefault == nil && !updated.isDefault {
            updated.isDefault = true
            persistence.save()
        }

        return updated
    }

    func restore(_ point: OnboardingRestorePoint) {
        guard let point = point as? LiveOnboardingRestorePoint else { return }

        if !point.modeExisted {
            // Nothing was there before, so remove what `apply` created rather than
            // leaving a synthetic default behind.
            if let created = persistence.fetchMode(withId: point.modeID.uuidString) {
                persistence.deleteMode(created)
            }
        } else {
            let restored = persistence.createOrUpdateMode(
                id: point.modeID,
                name: point.name,
                preset: point.preset,
                language: point.language,
                model: point.model,
                punctuation: point.punctuation,
                capitalization: point.capitalization,
                profanityFilter: point.profanityFilter,
                customInstructions: point.customInstructions,
                languageModel: point.languageModel,
                cloudProvider: point.cloudProvider,
                cloudTranscriptionModel: point.cloudTranscriptionModel,
                postProcessingMode: point.postProcessingMode,
                postProcessingProvider: point.postProcessingProvider,
                englishSpelling: point.englishSpelling,
                userSystemPrompt: point.userSystemPrompt,
                useStreamingTranscription: point.useStreamingTranscription,
                cloudAccuracyTier: point.cloudAccuracyTier,
                removeTrailingPeriod: point.removeTrailingPeriod,
                enableScreenOCR: point.enableScreenOCR,
                geminiCustomPrompt: point.geminiCustomPrompt,
                cloudPostProcessingModel: point.cloudPostProcessingModel,
                cloudTranscriptionDomain: point.cloudTranscriptionDomain,
                foreignPlatformExtensions: point.foreignPlatformExtensions
            )
            if restored.isDefault != point.wasDefault {
                restored.isDefault = point.wasDefault
                persistence.save()
            }
        }

        // Put the active mode selection back where it was.
        if let previous = point.previousSelection {
            appState.selectMode(previous, persist: true)
        } else if !point.modeExisted, appState.selectedModeId == point.modeID.uuidString {
            // Nothing was selected before and the Mode this flow created has just
            // been deleted, so keeping the id would select a row that is gone.
            appState.clearModeSelection()
        }
    }

    func markOnboardingCompleted() {
        UserDefaults.standard.set(true, forKey: "hasCompletedOnboarding")
        UserDefaults.standard.set(false, forKey: "onboardingPending")
    }

    func returnToHome() {
        appState.selectedNavigationItem = .home
    }
}

// MARK: - Assembly

extension OnboardingFlowModel {
    /// Build a flow model wired to the app's real managers.
    static func live(
        appState: AppState,
        audioManager: AudioRecordingManager,
        settingsManager: SettingsManager,
        whisperModelManager: WhisperModelManager,
        parakeetModelManager: ParakeetModelManager,
        licenseManager: LicenseManager,
        cloudHealth: CloudProviderHealthManager
    ) -> OnboardingFlowModel {
        OnboardingFlowModel(
            permissions: LiveOnboardingPermissions(audioManager: audioManager),
            catalog: LiveOnboardingModelCatalog(whisper: whisperModelManager, parakeet: parakeetModelManager),
            license: LiveOnboardingLicenseGateway(manager: licenseManager),
            providerKeys: LiveOnboardingProviderKeyGateway(
                settingsManager: settingsManager,
                cloudHealth: cloudHealth
            ),
            audio: LiveOnboardingAudioGateway(
                audioManager: audioManager,
                settingsManager: settingsManager,
                appState: appState
            ),
            committer: LiveOnboardingSourceCommitter(appState: appState)
        )
    }
}
