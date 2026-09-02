//
//  OnboardingFlowModel.swift
//  hyperwhisper
//
//  PRESENTATION LAYER FOR FIRST-RUN ONBOARDING
//  A unit-testable @MainActor view model that owns the eight step machine, the
//  per source configuration, the validation gates, and every side effecting
//  action the flow can take. The SwiftUI views bind to it and hold no policy of
//  their own.
//
//  Four production defects are fixed here rather than in the views:
//
//  1. Set Up Later used to leave the default Mode rewritten. Every source
//     configuration is now STAGED on this model. The only writes to production
//     state go through `OnboardingSourceCommitting`, and the flow always holds a
//     restore point so `deferSetup()` puts the app back exactly as it was.
//  2. Parakeet download failures were invisible because only WhisperModelManager
//     exposed its error to the setup screen. Both engines now feed the single
//     published `setupErrorMessage`.
//  3. Cloud activation ran in an untracked Task that could land after the sheet
//     closed. Every asynchronous action is now owned by `taskBox`, cancelled on
//     teardown, and its result is dropped unless the flow is still live.
//  4. There was no meaningful coverage. Everything below is reachable from
//     hyperwhisperTests through the narrow protocols in this file.
//

import Combine
import Foundation

// MARK: - Step machine

/// The eight onboarding steps, in order. Raw values are stable so a step can be
/// compared, persisted, or reported for analytics without a lookup table.
enum OnboardingStep: Int, CaseIterable, Identifiable, Comparable {
    case welcome
    case permissions
    case source
    case configure
    case setup
    case microphone
    case tryIt
    case done

    var id: Int { rawValue }

    static func < (lhs: OnboardingStep, rhs: OnboardingStep) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

// MARK: - Value types crossing the seams

/// Microphone authorization, reduced to the three cases the flow reacts to.
/// `.denied` covers restricted as well: in both cases the OS will not re-prompt,
/// so the flow deep links System Settings instead of asking again.
enum OnboardingMicrophoneAuthorization: Equatable {
    case undetermined
    case authorized
    case denied
}

/// One selectable input device. `id` is empty for the synthetic "System Default"
/// row, which is always offered first (see `deviceOptions`).
struct OnboardingInputDevice: Identifiable, Equatable {
    let id: String
    let name: String

    /// The synthetic first option. An empty id means "follow the system default",
    /// which is exactly how `settingsManager.selectedMicrophoneId` encodes it.
    static func systemDefault(name: String) -> OnboardingInputDevice {
        OnboardingInputDevice(id: "", name: name)
    }

    var isSystemDefault: Bool { id.isEmpty }
}

/// The last known download error for each local engine. Both are carried so the
/// flow can pick the one that matches the selected model rather than defaulting
/// to Whisper, which is how Parakeet failures used to disappear.
struct OnboardingDownloadErrors: Equatable {
    var whisper: String?
    var parakeet: String?

    static let none = OnboardingDownloadErrors(whisper: nil, parakeet: nil)

    func message(for kind: OnboardingModelSelection.Kind) -> String? {
        switch kind {
        case .whisper: return whisper
        case .parakeet: return parakeet
        }
    }
}

/// Result of a license probe or activation, reduced to what the flow needs.
struct OnboardingLicenseOutcome: Equatable {
    let isValid: Bool
    let errorMessage: String?

    static func failure(_ message: String?) -> OnboardingLicenseOutcome {
        OnboardingLicenseOutcome(isValid: false, errorMessage: message)
    }
}

/// The fully staged source configuration. Nothing in here has touched Core Data,
/// SettingsManager, the Keychain, or LicenseManager. It is handed to the
/// committer once, at an explicit commit boundary.
struct OnboardingStagedSource: Equatable {
    let source: TranscriptionSource
    /// Written verbatim to `Mode.model`. A Whisper catalog name ("base") or a
    /// Parakeet id ("parakeet-tdt-0.6b-v2"), never a prettified display name.
    let model: String
    let cloudProvider: String?
    let postProcessingMode: Int16
    let cloudAccuracyTier: String?
}

/// Opaque marker for whatever the committer needs in order to put production
/// state back. The flow only ever stores and returns it, so the Core Data detail
/// stays out of the presentation layer and out of the tests.
protocol OnboardingRestorePoint {}

// MARK: - Narrow seams

/// Permission reads and the two System Settings deep links.
@MainActor
protocol OnboardingPermissionsChecking: AnyObject {
    var microphoneAuthorization: OnboardingMicrophoneAuthorization { get }
    var hasAccessibilityPermission: Bool { get }
    func requestMicrophonePermission() async -> Bool
    func openMicrophoneSettings()
    func openAccessibilitySettings()
    func waitForAccessibilityPermission(_ completion: @escaping (Bool) -> Void)
}

/// The curated on device model shortlist plus download state for BOTH engines.
@MainActor
protocol OnboardingModelCatalog: AnyObject {
    var models: [OnboardingModelSelection] { get }
    func isInstalled(_ model: OnboardingModelSelection) -> Bool
    func isDownloading(_ model: OnboardingModelSelection) -> Bool
    func progress(for model: OnboardingModelSelection) -> Double
    func startDownload(_ model: OnboardingModelSelection)
    /// Emits whenever either engine's error message changes. Carrying both in one
    /// value is what lets a Parakeet failure reach the UI (bug 2).
    var downloadErrors: AnyPublisher<OnboardingDownloadErrors, Never> { get }
    /// Emits whenever download state or progress changes for either engine. The
    /// catalog reads are plain function calls, so without this tick nothing tells
    /// SwiftUI that the setup step's progress bar has moved (bug 2).
    var downloadActivity: AnyPublisher<Void, Never> { get }
}

/// HyperWhisper Cloud. `probe` is read only; `activate` is the single explicit
/// action that writes account state. Entitlement itself stays server side.
@MainActor
protocol OnboardingLicenseGateway: AnyObject {
    var isActive: Bool { get }
    var isValidating: Bool { get }
    var lastError: String? { get }
    func probe(_ key: String) async -> OnboardingLicenseOutcome
    func activate(_ key: String) async -> OnboardingLicenseOutcome
}

/// Bring your own key providers.
@MainActor
protocol OnboardingProviderKeyGateway: AnyObject {
    var validationError: String? { get }
    func probe(_ provider: CloudProvider, apiKey: String) async -> ProviderHealth
    /// Returns false when the Keychain write failed. A healthy probe alone must
    /// never be treated as a pass.
    @discardableResult
    func persist(_ key: String, for provider: CloudProvider) -> Bool
    func hasKey(for provider: CloudProvider) -> Bool
    /// Whatever is stored for this provider right now, or "" when nothing is.
    /// Snapshotted before the flow's first write so deferral can put it back,
    /// and an empty string round trips as "delete the entry" through `persist`.
    func currentKey(for provider: CloudProvider) -> String
}

/// Input devices, the idle level preview, and the "give it a try" recording.
@MainActor
protocol OnboardingAudioGateway: AnyObject {
    var devices: [OnboardingInputDevice] { get }
    /// Emits the connected devices whenever they change, so a microphone that is
    /// unplugged while the step is open leaves the list (bug 6).
    var devicesPublisher: AnyPublisher<[OnboardingInputDevice], Never> { get }
    /// nil means the system default is in use.
    var selectedDeviceID: String? { get }
    /// The persisted preference, which is what deferral has to put back. It can
    /// name a device that is not connected right now, in which case it differs
    /// from `selectedDeviceID`, which reflects what is actually open.
    var storedDeviceID: String? { get }
    func refreshDevices()
    func refreshMicrophonePermission()
    func selectDevice(id: String?)
    /// Undo a selection. `selectDevice` performs two writes, so deferral has to
    /// put both back: the stored preference (which survives even when the device
    /// it names is absent) and whichever device was actually open.
    func restoreDevice(storedID: String?, openID: String?)
    func startInputLevelPreview()
    func stopInputLevelPreview()
    func toggleTestRecording()
    /// Privacy backstop on every exit path. Deliberately not gated on isRecording.
    func stopRecordingForExit()
    func clearTranscript()
    var isRecordingPublisher: AnyPublisher<Bool, Never> { get }
    var transcriptPublisher: AnyPublisher<String, Never> { get }
}

/// The one and only path from staged configuration to production state.
@MainActor
protocol OnboardingSourceCommitting: AnyObject {
    /// Snapshot everything `apply` is about to overwrite.
    func captureRestorePoint() -> OnboardingRestorePoint
    func apply(_ staged: OnboardingStagedSource)
    func restore(_ point: OnboardingRestorePoint)
    func markOnboardingCompleted()
    func returnToHome()
}

// MARK: - Task ownership

/// Holds the flow's in flight tasks so they can be cancelled from a nonisolated
/// deinit. Keyed so a second press of the same button replaces the first.
final class OnboardingTaskBox: @unchecked Sendable {
    private let lock = NSLock()
    private var tasks: [String: Task<Void, Never>] = [:]

    func store(_ task: Task<Void, Never>, for key: String) {
        lock.lock()
        let previous = tasks[key]
        tasks[key] = task
        lock.unlock()
        previous?.cancel()
    }

    func cancel(_ key: String) {
        lock.lock()
        let task = tasks.removeValue(forKey: key)
        lock.unlock()
        task?.cancel()
    }

    func clear(_ key: String) {
        lock.lock()
        tasks.removeValue(forKey: key)
        lock.unlock()
    }

    func cancelAll() {
        lock.lock()
        let all = tasks.values
        tasks.removeAll()
        lock.unlock()
        all.forEach { $0.cancel() }
    }

    var isEmpty: Bool {
        lock.lock()
        defer { lock.unlock() }
        return tasks.isEmpty
    }
}

// MARK: - Flow model

@MainActor
final class OnboardingFlowModel: ObservableObject {

    // MARK: Step machine

    @Published private(set) var step: OnboardingStep = .welcome

    // MARK: Permissions

    @Published private(set) var hasMicrophonePermission = false
    @Published private(set) var hasAccessibilityPermission = false
    @Published private(set) var isPollingForAccessibility = false
    @Published private(set) var microphoneAuthorization: OnboardingMicrophoneAuthorization = .undetermined
    /// Non-nil when the last permission request was refused. Drives the alert.
    @Published var permissionErrorMessage: String?

    // MARK: Staged source configuration

    @Published private(set) var selectedSource: TranscriptionSource?
    @Published private(set) var selectedModel: OnboardingModelSelection?
    @Published var licenseKeyInput: String = "" {
        didSet {
            guard licenseKeyInput != oldValue else { return }
            invalidateLicenseValidation()
        }
    }
    @Published private(set) var selectedProvider: CloudProvider = .openai
    @Published var apiKeyInput: String = "" {
        didSet {
            guard apiKeyInput != oldValue else { return }
            invalidateProviderValidation()
        }
    }

    // MARK: Validation

    /// True only while the inline test has a passing result for the CURRENT key
    /// and provider. Cleared by every edit so a stale pass cannot open the gate.
    @Published private(set) var keyValidated = false
    @Published private(set) var isTestingKey = false
    @Published private(set) var licenseTestPassed: Bool?
    @Published private(set) var providerTestHealth: ProviderHealth?

    // MARK: Setup

    @Published private(set) var isActivatingLicense = false
    /// Bug 2: the single error surface for the setup step. Fed by Whisper AND
    /// Parakeet download failures, license activation failures, and Keychain
    /// write failures, whichever matches the selected source.
    @Published private(set) var setupErrorMessage: String?

    // MARK: Microphone

    @Published private(set) var deviceOptions: [OnboardingInputDevice] = []
    @Published private(set) var selectedDeviceID: String = ""

    // MARK: Try it

    @Published private(set) var isRecording = false
    @Published private(set) var transcript = ""

    // MARK: Dependencies

    private let permissions: any OnboardingPermissionsChecking
    private let catalog: any OnboardingModelCatalog
    private let license: any OnboardingLicenseGateway
    private let providerKeys: any OnboardingProviderKeyGateway
    private let audio: any OnboardingAudioGateway
    private let committer: any OnboardingSourceCommitting
    private let systemDefaultDeviceName: String

    // MARK: Private state

    private let taskBox = OnboardingTaskBox()
    private var cancellables = Set<AnyCancellable>()
    private var downloadErrors: OnboardingDownloadErrors = .none
    private var activationErrorMessage: String?
    private var providerErrorMessage: String?
    /// Captured before the first write to production state so deferral can undo
    /// it exactly. nil means production state has not been touched at all.
    private var restorePoint: (any OnboardingRestorePoint)?
    /// Bug 1, BYOK branch. "Test API key" has to write the candidate key to the
    /// Keychain before it can be trusted, so the value it overwrites is captured
    /// here first. "" means the provider had no key, which `persist` encodes as a
    /// delete, so a rollback is exact either way.
    private var providerKeyRestorePoints: [CloudProvider: String] = [:]
    /// Providers whose key passed a probe AND a Keychain write THIS session.
    /// Survives `resetConfigureTestResults()` so Back navigation does not shut
    /// the gate on a key that was just verified, but a pre-existing stored key
    /// that was never probed here stays untrusted.
    private var validatedProviders: Set<CloudProvider> = []
    /// The exact trimmed key whose license probe last passed this session.
    /// Editing the field closes the gate through the string mismatch; retyping
    /// the validated key reopens it, mirroring the BYOK stored-key semantics.
    private var lastValidatedLicenseKey: String?
    /// Bug 1, microphone step. `selectDevice` writes the app's input device
    /// setting immediately, so the value it replaces is captured on the first
    /// change. nil is a real value here ("follow the system default"), hence the
    /// separate captured flag.
    private var didCaptureDevice = false
    private var previousDeviceID: String?
    private var previousOpenDeviceID: String?
    /// The guarded commit boundary (bug 3). Flipped false the moment the flow is
    /// finished, so a late async completion can never write onboarding state.
    private var isLive = true

    private enum TaskKey {
        static let licenseTest = "license.test"
        static let providerTest = "provider.test"
        static let activation = "license.activate"
        static let microphonePermission = "permission.microphone"
    }

    // MARK: Init

    init(
        permissions: any OnboardingPermissionsChecking,
        catalog: any OnboardingModelCatalog,
        license: any OnboardingLicenseGateway,
        providerKeys: any OnboardingProviderKeyGateway,
        audio: any OnboardingAudioGateway,
        committer: any OnboardingSourceCommitting,
        systemDefaultDeviceName: String = "onboarding.mic.device.systemDefault".localized
    ) {
        self.permissions = permissions
        self.catalog = catalog
        self.license = license
        self.providerKeys = providerKeys
        self.audio = audio
        self.committer = committer
        self.systemDefaultDeviceName = systemDefaultDeviceName

        catalog.downloadErrors
            .sink { [weak self] errors in
                guard let self else { return }
                self.downloadErrors = errors
                self.refreshSetupError()
            }
            .store(in: &cancellables)

        // The catalog's download state lives on nested ObservableObjects, whose
        // changes do not propagate to this flow's own observers, so the setup
        // step's progress bar sat frozen at whatever it first rendered (bug 2).
        catalog.downloadActivity
            .sink { [weak self] in self?.objectWillChange.send() }
            .store(in: &cancellables)

        // Use the emitted list, never a re-read of `audio.devices`: @Published
        // fires on willSet, so pulling here would see the pre-change value.
        audio.devicesPublisher
            .sink { [weak self] devices in
                guard let self, self.step == .microphone else { return }
                self.applyDeviceList(devices)
            }
            .store(in: &cancellables)

        audio.isRecordingPublisher
            .sink { [weak self] recording in self?.isRecording = recording }
            .store(in: &cancellables)

        audio.transcriptPublisher
            .sink { [weak self] text in self?.transcript = text }
            .store(in: &cancellables)

        refreshPermissions()
    }

    deinit {
        // Nonisolated on purpose: the box is Sendable so cancellation is safe
        // even when the sheet is torn down off the flow's own actor hop.
        taskBox.cancelAll()
    }

    // MARK: - Permissions

    func refreshPermissions() {
        microphoneAuthorization = permissions.microphoneAuthorization
        hasMicrophonePermission = microphoneAuthorization == .authorized
        hasAccessibilityPermission = permissions.hasAccessibilityPermission
        // Keep the audio manager's own preview guard from holding stale state
        // after the user returns from System Settings.
        audio.refreshMicrophonePermission()
    }

    /// The microphone row's action. The OS refuses to re-prompt after a denial,
    /// so anything other than "undetermined" deep links System Settings.
    func handleMicrophoneAction() {
        switch permissions.microphoneAuthorization {
        case .undetermined:
            requestMicrophonePermission()
        case .authorized, .denied:
            permissions.openMicrophoneSettings()
        }
    }

    func requestMicrophonePermission() {
        let task = Task { [weak self] in
            guard let self else { return }
            let granted = await self.permissions.requestMicrophonePermission()
            guard !Task.isCancelled, self.isLive else { return }
            self.hasMicrophonePermission = granted
            self.microphoneAuthorization = granted ? .authorized : .denied
            if !granted {
                self.permissionErrorMessage = "onboarding.error.microphone.denied".localized
            }
            self.taskBox.clear(TaskKey.microphonePermission)
        }
        track(task, key: TaskKey.microphonePermission)
    }

    func handleAccessibilityAction() {
        permissions.openAccessibilitySettings()
        isPollingForAccessibility = true
        permissions.waitForAccessibilityPermission { [weak self] granted in
            Task { @MainActor [weak self] in
                guard let self, self.isLive else { return }
                self.hasAccessibilityPermission = granted
                self.isPollingForAccessibility = false
            }
        }
    }

    // MARK: - Source selection (staged only)

    func select(source: TranscriptionSource) {
        guard selectedSource != source else { return }
        selectedSource = source
        keyValidated = false
        licenseTestPassed = nil
        providerTestHealth = nil
        activationErrorMessage = nil
        providerErrorMessage = nil
        if source == .onDevice, selectedModel == nil {
            selectedModel = catalog.models.first { $0.isRecommended } ?? catalog.models.first
        }
        refreshSetupError()
    }

    func select(model: OnboardingModelSelection) {
        guard selectedModel != model else { return }
        selectedModel = model
        refreshSetupError()
    }

    func select(provider: CloudProvider) {
        guard selectedProvider != provider else { return }
        selectedProvider = provider
        // A masked key typed for one provider must never be saved under another.
        apiKeyInput = ""
        invalidateProviderValidation()
    }

    private func invalidateLicenseValidation() {
        licenseTestPassed = nil
        activationErrorMessage = nil
        if selectedSource == .hyperwhisperCloud { keyValidated = false }
        refreshSetupError()
    }

    private func invalidateProviderValidation() {
        providerTestHealth = nil
        providerErrorMessage = nil
        if selectedSource == .yourProvider { keyValidated = false }
        refreshSetupError()
    }

    /// Clears any inline test result so a pass from a previous visit cannot be
    /// read as a pass for whatever is in the field now.
    func resetConfigureTestResults() {
        isTestingKey = false
        licenseTestPassed = nil
        providerTestHealth = nil
        activationErrorMessage = nil
        providerErrorMessage = nil
        keyValidated = false
        refreshSetupError()
    }

    // MARK: - Configure step actions

    /// Read only license check. Account state is untouched until activation.
    func testAccessKey() {
        let key = licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        isTestingKey = true
        licenseTestPassed = nil
        activationErrorMessage = nil
        let task = Task { [weak self] in
            guard let self else { return }
            let outcome = await self.license.probe(key)
            guard !Task.isCancelled, self.isLive else { return }
            // Drop a result that arrived for a key the user has since edited.
            guard self.licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines) == key else {
                self.isTestingKey = false
                self.taskBox.clear(TaskKey.licenseTest)
                return
            }
            self.licenseTestPassed = outcome.isValid
            self.activationErrorMessage = outcome.isValid ? nil : outcome.errorMessage
            self.keyValidated = outcome.isValid
            if outcome.isValid {
                self.lastValidatedLicenseKey = key
            } else if self.lastValidatedLicenseKey == key {
                // A revoked key that fails a re-probe must not stay remembered.
                self.lastValidatedLicenseKey = nil
            }
            self.isTestingKey = false
            self.refreshSetupError()
            self.taskBox.clear(TaskKey.licenseTest)
        }
        track(task, key: TaskKey.licenseTest)
    }

    /// Probe the candidate key, then accept it only once the Keychain confirms
    /// the write. A passing network round trip on its own is not a pass.
    func testProviderKey() {
        let key = apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        let provider = selectedProvider
        isTestingKey = true
        providerTestHealth = nil
        providerErrorMessage = nil
        let task = Task { [weak self] in
            guard let self else { return }
            let health = await self.providerKeys.probe(provider, apiKey: key)
            guard !Task.isCancelled, self.isLive else { return }
            // Drop a result the user has since superseded BEFORE the persist:
            // a stale probe must never write the Keychain or set a restore
            // point (which would also wrongly flag a pending production write).
            guard self.selectedProvider == provider,
                  self.apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines) == key else {
                self.isTestingKey = false
                self.taskBox.clear(TaskKey.providerTest)
                return
            }
            var persisted = false
            if health.isHealthy {
                // Snapshot whatever this provider had BEFORE overwriting it, so
                // Set Up Later can put the user's original key back (bug 1).
                self.captureProviderKeyRestorePoint(for: provider)
                persisted = self.providerKeys.persist(key, for: provider)
            }
            if health.isHealthy && !persisted {
                self.providerTestHealth = nil
                self.providerErrorMessage = self.providerKeys.validationError
                    ?? "onboarding.setup.provider.saveFailed".localized
                self.keyValidated = false
            } else {
                self.providerTestHealth = health
                self.providerErrorMessage = nil
                self.keyValidated = health.isHealthy && persisted
                if self.keyValidated {
                    self.validatedProviders.insert(provider)
                }
            }
            self.isTestingKey = false
            self.refreshSetupError()
            self.taskBox.clear(TaskKey.providerTest)
        }
        track(task, key: TaskKey.providerTest)
    }

    // MARK: - Setup step actions

    /// The curated on device shortlist, resolved live from the catalog.
    var availableModels: [OnboardingModelSelection] { catalog.models }

    func isInstalled(_ model: OnboardingModelSelection) -> Bool { catalog.isInstalled(model) }

    func startSelectedModelDownload() {
        guard let model = selectedModel else { return }
        catalog.startDownload(model)
        refreshSetupError()
    }

    func isSelectedModelInstalled() -> Bool {
        guard let model = selectedModel else { return false }
        return catalog.isInstalled(model)
    }

    func isSelectedModelDownloading() -> Bool {
        guard let model = selectedModel else { return false }
        return catalog.isDownloading(model)
    }

    func selectedModelProgress() -> Double {
        guard let model = selectedModel else { return 0 }
        return catalog.progress(for: model)
    }

    /// Bug 3. The activation task is owned, replaces any earlier one, and its
    /// result is discarded unless the flow is still live. Activation itself is
    /// the user's single explicit action, so entitlement stays server enforced;
    /// nothing here shortcuts or fakes it.
    func activateCloudLicense() {
        let key = licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty, !isActivatingLicense else { return }
        isActivatingLicense = true
        activationErrorMessage = nil
        let task = Task { [weak self] in
            guard let self else { return }
            let outcome = await self.license.activate(key)
            guard !Task.isCancelled, self.isLive else { return }
            self.isActivatingLicense = false
            self.activationErrorMessage = outcome.isValid ? nil : outcome.errorMessage
            if outcome.isValid { self.keyValidated = true }
            self.refreshSetupError()
            self.taskBox.clear(TaskKey.activation)
        }
        track(task, key: TaskKey.activation)
    }

    func saveProviderKey() {
        let key = apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        captureProviderKeyRestorePoint(for: selectedProvider)
        let persisted = providerKeys.persist(key, for: selectedProvider)
        providerErrorMessage = persisted
            ? nil
            : (providerKeys.validationError ?? "onboarding.setup.provider.saveFailed".localized)
        refreshSetupError()
    }

    /// Records the Keychain value a subsequent write is about to replace. Only the
    /// FIRST capture per provider counts, so repeated tests still roll back to
    /// what the user had before onboarding rather than to an intermediate key.
    private func captureProviderKeyRestorePoint(for provider: CloudProvider) {
        guard providerKeyRestorePoints[provider] == nil else { return }
        providerKeyRestorePoints[provider] = providerKeys.currentKey(for: provider)
    }

    /// One published error property for the setup step, per selected source.
    /// The on device branch reads whichever engine the selected model belongs to,
    /// which is what makes Parakeet failures visible.
    ///
    /// Everything here is produced INSIDE this flow. `license.lastError` and
    /// `providerKeys.validationError` are app-global, long lived, and unobserved,
    /// so falling back to them used to render an unrelated failure from an earlier
    /// session before the user had done anything on the step.
    private func refreshSetupError() {
        switch selectedSource {
        case .onDevice:
            setupErrorMessage = selectedModel.flatMap { downloadErrors.message(for: $0.kind) }
        case .hyperwhisperCloud:
            setupErrorMessage = activationErrorMessage
        case .yourProvider:
            setupErrorMessage = providerErrorMessage
        case nil:
            setupErrorMessage = nil
        }
    }

    // MARK: - Microphone step

    func beginMicrophoneStep() {
        audio.refreshDevices()
        audio.refreshMicrophonePermission()
        refreshDeviceOptions()
        audio.startInputLevelPreview()
    }

    func endMicrophoneStep() {
        audio.stopInputLevelPreview()
    }

    func refreshDeviceOptions() {
        applyDeviceList(audio.devices)
    }

    private func applyDeviceList(_ devices: [OnboardingInputDevice]) {
        // "System Default" is always the first option, and an empty id is how the
        // rest of the app already encodes it.
        deviceOptions = [.systemDefault(name: systemDefaultDeviceName)] + devices
        selectedDeviceID = audio.selectedDeviceID ?? ""
    }

    func selectDevice(id: String) {
        // A device can vanish between the menu being built and the pick landing.
        // Rejecting it here keeps a disconnected microphone out of the UI
        // selection and, more importantly, stops it from flipping the pending
        // write flag for a change that was never applied (bug 6).
        guard id.isEmpty || deviceOptions.contains(where: { $0.id == id }) else { return }
        // The device change reaches SettingsManager immediately, because the
        // level meter and the try it recording both have to follow it. Capture
        // what it replaces so Set Up Later restores it (bug 1).
        if !didCaptureDevice {
            // Snapshot BOTH writes. The persisted preference and the open device
            // diverge when the remembered microphone is unplugged, so restoring
            // either one alone leaves the other pointing at the onboarding pick.
            previousDeviceID = audio.storedDeviceID
            previousOpenDeviceID = audio.selectedDeviceID
            didCaptureDevice = true
        }
        selectedDeviceID = id
        audio.selectDevice(id: id.isEmpty ? nil : id)
        // Re-point the metering session at the newly selected device.
        audio.startInputLevelPreview()
    }

    var selectedDeviceName: String {
        deviceOptions.first { $0.id == selectedDeviceID }?.name ?? systemDefaultDeviceName
    }

    // MARK: - Try it step

    func beginTryItStep() {
        audio.clearTranscript()
    }

    func endTryItStep() {
        audio.stopRecordingForExit()
        audio.clearTranscript()
    }

    func toggleTestRecording() {
        audio.toggleTestRecording()
    }

    /// Recording failures arrive through the same channel as transcripts with an
    /// "Error:" sentinel, so the view can render them differently.
    var transcriptIsError: Bool { transcript.hasPrefix("Error:") }

    var transcriptBody: String {
        guard transcriptIsError else { return transcript }
        return String(transcript.dropFirst("Error:".count))
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: - Gating

    var canContinue: Bool {
        switch step {
        case .welcome:
            return true
        case .permissions:
            return hasMicrophonePermission
        case .source:
            return selectedSource != nil
        case .configure:
            guard let source = selectedSource else { return false }
            switch source {
            case .onDevice:
                return selectedModel != nil
            case .hyperwhisperCloud:
                // A working key, not merely a typed one. Either the license is
                // already active on this Mac, the inline test passed, or the
                // field still holds the exact key that passed earlier this
                // session (Back navigation clears `keyValidated`, not the fact
                // that the key was verified).
                let key = licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
                return license.isActive || keyValidated
                    || (!key.isEmpty && key == lastValidatedLicenseKey)
            case .yourProvider:
                // `keyValidated` is cleared every time this step appears, so the
                // per-session validation record keeps the gate open across Back
                // navigation. A key that merely sits in the Keychain but was
                // never probed this session does not count.
                return keyValidated || validatedProviders.contains(selectedProvider)
            }
        case .setup:
            return isSelectedSourceUsable
        case .microphone, .tryIt, .done:
            return true
        }
    }

    /// The mandatory gate on the setup step: is the chosen source genuinely
    /// usable right now.
    var isSelectedSourceUsable: Bool {
        guard let source = selectedSource else { return false }
        switch source {
        case .onDevice:
            guard let model = selectedModel else { return false }
            return catalog.isInstalled(model)
        case .hyperwhisperCloud:
            return license.isActive
        case .yourProvider:
            // Stored AND verified this session: an unprobed pre-existing key
            // must not read as "validated" on the setup checklist.
            return providerKeys.hasKey(for: selectedProvider)
                && validatedProviders.contains(selectedProvider)
        }
    }

    @discardableResult
    func advance() -> Bool {
        guard canContinue,
              let next = OnboardingStep(rawValue: step.rawValue + 1) else { return false }
        // #315: the setup gate is positional, so a source that died after the
        // user passed it still reached Try It. Suppressing the write there is
        // not enough — the test recording would then run through their PREVIOUS
        // production configuration and read as a pass. Refuse the transition and
        // send them to the one step that can explain and fix it. Checked before
        // `step = next` so `stepDidChange()` is never entered re-entrantly; the
        // return value stays honest ("the user did not move forward") even though
        // `step` changed.
        if next == .tryIt, stagedSource != nil, !isSelectedSourceUsable {
            step = .setup
            refreshSetupError()
            return false
        }
        step = next
        stepDidChange()
        return true
    }

    @discardableResult
    func back() -> Bool {
        guard let previous = OnboardingStep(rawValue: step.rawValue - 1) else { return false }
        step = previous
        return true
    }

    private func stepDidChange() {
        // The try it step has to record through the source the user just set up,
        // so this is the one place production state is written before completion.
        // It is fully reversible: `deferSetup()` restores the captured point.
        if step == .tryIt { applyStagedSourceReversibly() }
    }

    // MARK: - Staging and commit

    /// The staged configuration, or nil while the user has not chosen a source.
    var stagedSource: OnboardingStagedSource? {
        guard let source = selectedSource else { return nil }
        switch source {
        case .onDevice:
            // Fully offline: local model, post-processing off.
            return OnboardingStagedSource(
                source: .onDevice,
                model: selectedModel?.id ?? "base",
                cloudProvider: nil,
                postProcessingMode: 0,
                cloudAccuracyTier: nil
            )
        case .hyperwhisperCloud:
            return OnboardingStagedSource(
                source: .hyperwhisperCloud,
                model: "cloud",
                cloudProvider: "hyperwhisper",
                postProcessingMode: 1,
                cloudAccuracyTier: CloudAccuracyTier.elevenLabsScribeV2.rawValue
            )
        case .yourProvider:
            // Post-processing off by default so first run never fails on a
            // missing post-processing key.
            return OnboardingStagedSource(
                source: .yourProvider,
                model: "cloud",
                cloudProvider: selectedProvider.rawValue,
                postProcessingMode: 0,
                cloudAccuracyTier: nil
            )
        }
    }

    /// True once production state has been written and not yet restored. Covers
    /// all three reversible writes: the default Mode, the Keychain, and the
    /// selected input device.
    var hasPendingProductionWrite: Bool {
        restorePoint != nil || !providerKeyRestorePoints.isEmpty || didCaptureDevice
    }

    private func applyStagedSourceReversibly() {
        // #315: usability is a precondition of the WRITE, not of a step
        // transition. Both callers now re-check it and bounce the user to
        // `.setup` first, so nothing reachable returns here — this is the
        // backstop that stops a future third caller reintroducing the bug.
        guard isSelectedSourceUsable, let staged = stagedSource else { return }
        if restorePoint == nil {
            restorePoint = committer.captureRestorePoint()
        }
        committer.apply(staged)
    }

    /// Explicit completion. The staged configuration becomes production state and
    /// there is nothing left to roll back.
    ///
    /// Returns false when completion was refused: either the flow has already
    /// closed, or the setup gate has shut since the user passed it. In the second
    /// case nothing is committed and the flow is sent back to `.setup` to fix it,
    /// so the caller must keep the sheet open.
    @discardableResult
    func complete() -> Bool {
        guard isLive else { return false }
        // #315: the gate was only ever checked on the way out of `.setup`, so a
        // model deleted or a license deactivated during the last two steps still
        // became the user's default and failed on their first real dictation.
        // Gate on the thing being committed rather than on the gate alone: with
        // no staged source there is nothing to protect and closing cleanly is
        // existing behaviour.
        if stagedSource != nil, !isSelectedSourceUsable {
            step = .setup
            refreshSetupError()
            return false
        }
        applyStagedSourceReversibly()
        restorePoint = nil
        providerKeyRestorePoints.removeAll()
        didCaptureDevice = false
        previousDeviceID = nil
        previousOpenDeviceID = nil
        finish()
        return true
    }

    /// Set Up Later. Bug 1: every reversible write this flow made is put back, so
    /// the default Mode, the active mode selection, the provider API keys, and the
    /// selected input device are exactly what they were before the sheet opened.
    /// Downloaded models are deliberately kept (harmless, and the user paid the
    /// bytes), as is an activated HyperWhisper Cloud license: activation is a
    /// server side account action, not local state this flow can un-write.
    func deferSetup() {
        guard isLive else { return }
        rollback()
        finish()
    }

    private func rollback() {
        if let point = restorePoint {
            committer.restore(point)
            restorePoint = nil
        }

        // "Test API key" writes to the Keychain before any commit boundary, so
        // deferral has to put the previous value back. "" is how the key store
        // encodes "no key", so a provider that had nothing ends up with nothing.
        for (provider, previous) in providerKeyRestorePoints {
            providerKeys.persist(previous, for: provider)
        }
        providerKeyRestorePoints.removeAll()

        if didCaptureDevice {
            audio.restoreDevice(storedID: previousDeviceID, openID: previousOpenDeviceID)
            didCaptureDevice = false
            previousDeviceID = nil
            previousOpenDeviceID = nil
        }
    }

    private func finish() {
        // Close the commit boundary FIRST so any in flight continuation that is
        // already past its cancellation check still cannot write onboarding state.
        isLive = false
        taskBox.cancelAll()
        audio.stopRecordingForExit()
        audio.stopInputLevelPreview()
        committer.markOnboardingCompleted()
        committer.returnToHome()
    }

    // MARK: - Test seams

    #if DEBUG
    /// Test only seams. Never referenced from shipping code paths, and they grant
    /// no capability: there is no way to bypass validation or entitlement here.
    var hasInFlightWorkForTesting: Bool { !taskBox.isEmpty }
    var isLiveForTesting: Bool { isLive }
    /// The most recently spawned asynchronous action, so a test can await the
    /// exact task instead of yielding an arbitrary number of times.
    private(set) var lastAsyncTaskForTesting: Task<Void, Never>?
    #endif

    private func track(_ task: Task<Void, Never>, key: String) {
        #if DEBUG
        lastAsyncTaskForTesting = task
        #endif
        taskBox.store(task, for: key)
    }
}
