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

    var inputLevel: Float { audioManager.idleInputLevel }
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
            name: existing?.name ?? "Default",
            preset: existing?.preset ?? "hyper",
            language: existing?.language ?? "en",
            model: existing?.model ?? "base",
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

    /// Reconfigure the EXISTING default Mode in place. `createOrUpdateMode` resets
    /// every omitted parameter to its default, so all non-source fields are
    /// forwarded from the current row. `cloudTranscriptionModel` is deliberately
    /// omitted so it re-derives for the new provider and tier.
    func apply(_ staged: OnboardingStagedSource) {
        let existing = persistence.findDefaultMode()
        let updated = persistence.createOrUpdateMode(
            id: existing?.id ?? Self.defaultModeID,
            name: existing?.name ?? "Default",
            preset: existing?.preset ?? "hyper",
            language: existing?.language ?? "en",
            model: staged.model,
            punctuation: existing?.punctuation ?? true,
            capitalization: existing?.capitalization ?? true,
            profanityFilter: existing?.profanityFilter ?? false,
            customInstructions: existing?.customInstructions,
            languageModel: existing?.languageModel,
            cloudProvider: staged.cloudProvider,
            postProcessingMode: staged.postProcessingMode,
            postProcessingProvider: existing?.postProcessingProvider,
            englishSpelling: existing?.englishSpelling,
            userSystemPrompt: existing?.userSystemPrompt,
            useStreamingTranscription: existing?.useStreamingTranscription ?? false,
            cloudAccuracyTier: staged.cloudAccuracyTier,
            removeTrailingPeriod: existing?.removeTrailingPeriod ?? false,
            enableScreenOCR: existing?.enableScreenOCR ?? false,
            geminiCustomPrompt: existing?.geminiCustomPrompt,
            cloudPostProcessingModel: existing?.cloudPostProcessingModel,
            cloudTranscriptionDomain: existing?.cloudTranscriptionDomain,
            foreignPlatformExtensions: existing?.foreignPlatformExtensions
        )

        // If no default existed, createOrUpdateMode does not flag the row it just
        // created, which would leave the chosen source on a stray non-default Mode.
        if existing == nil && !updated.isDefault {
            updated.isDefault = true
            persistence.save()
        }

        // Writing the source onto Default is not enough on its own: a returning
        // user's selectedModeId still points at their own Mode, so the next
        // recording would keep using that Mode's source.
        appState.selectMode(updated, persist: true)
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
