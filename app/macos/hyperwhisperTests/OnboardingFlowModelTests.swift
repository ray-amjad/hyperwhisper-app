//
//  OnboardingFlowModelTests.swift
//  hyperwhisperTests
//
//  Behavioural coverage for the first-run flow: step gating, permission
//  recovery, all three source branches, provider validation, model download
//  failure surfacing for BOTH local engines, microphone lifecycle, Set Up Later
//  rollback, and the completion commit.
//
//  The XCUITest runner cannot bootstrap in this environment, so the real
//  coverage lives here, driven through the flow model's narrow protocol seams.
//

import Combine
import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Fakes

@MainActor
final class FakePermissions: OnboardingPermissionsChecking {
    var microphoneAuthorization: OnboardingMicrophoneAuthorization = .undetermined
    var hasAccessibilityPermission = false
    var requestResult = true
    var requestCount = 0
    var openedMicrophoneSettings = 0
    var openedAccessibilitySettings = 0
    var accessibilityCompletion: ((Bool) -> Void)?

    func requestMicrophonePermission() async -> Bool {
        requestCount += 1
        if requestResult { microphoneAuthorization = .authorized }
        return requestResult
    }

    func openMicrophoneSettings() { openedMicrophoneSettings += 1 }

    func openAccessibilitySettings() { openedAccessibilitySettings += 1 }

    func waitForAccessibilityPermission(_ completion: @escaping (Bool) -> Void) {
        accessibilityCompletion = completion
    }
}

@MainActor
final class FakeCatalog: OnboardingModelCatalog {
    static let parakeet = OnboardingModelSelection(
        id: "parakeet-tdt-0.6b-v2", kind: .parakeet, displayName: "Parakeet V2",
        subtitleKey: "k", size: "474 MB", speed: 5, accuracy: 3, isRecommended: true
    )
    static let whisper = OnboardingModelSelection(
        id: "base", kind: .whisper, displayName: "Whisper Base",
        subtitleKey: "k", size: "142 MB", speed: 5, accuracy: 1, isRecommended: false
    )

    var models: [OnboardingModelSelection] = [parakeet, whisper]
    var installed: Set<String> = []
    var downloading: Set<String> = []
    var progresses: [String: Double] = [:]
    /// Issue #312. Absent by default, which is the state every engine except
    /// Parakeet is in.
    var stages: [String: ModelDownloadStage] = [:]
    var startedDownloads: [String] = []
    let errors = CurrentValueSubject<OnboardingDownloadErrors, Never>(.none)
    /// Stands in for the nested download managers' change ticks. Unthrottled,
    /// because the live adapter is where the 200 ms throttle lives.
    let activity = PassthroughSubject<Void, Never>()

    func isInstalled(_ model: OnboardingModelSelection) -> Bool { installed.contains(model.id) }
    func isDownloading(_ model: OnboardingModelSelection) -> Bool { downloading.contains(model.id) }
    func progress(for model: OnboardingModelSelection) -> Double { progresses[model.id] ?? 0 }
    func stage(for model: OnboardingModelSelection) -> ModelDownloadStage? { stages[model.id] }
    func startDownload(_ model: OnboardingModelSelection) { startedDownloads.append(model.id) }
    var downloadErrors: AnyPublisher<OnboardingDownloadErrors, Never> { errors.eraseToAnyPublisher() }
    var downloadActivity: AnyPublisher<Void, Never> { activity.eraseToAnyPublisher() }
}

/// A catalog that implements only the requirements without a default, so
/// `OnboardingModelCatalog.stage(for:)`'s own default implementation is the code
/// under test. This stands in for every conformer that has no stage to give —
/// which is the whole reason the requirement is defaulted rather than added
/// bare (issue #312).
@MainActor
final class FakeCatalogWithoutStage: OnboardingModelCatalog {
    var models: [OnboardingModelSelection] = [FakeCatalog.parakeet]

    func isInstalled(_ model: OnboardingModelSelection) -> Bool { false }
    func isDownloading(_ model: OnboardingModelSelection) -> Bool { true }
    func progress(for model: OnboardingModelSelection) -> Double { 0.01 }
    func startDownload(_ model: OnboardingModelSelection) {}
    var downloadErrors: AnyPublisher<OnboardingDownloadErrors, Never> {
        Empty<OnboardingDownloadErrors, Never>(completeImmediately: false).eraseToAnyPublisher()
    }
    var downloadActivity: AnyPublisher<Void, Never> {
        Empty<Void, Never>(completeImmediately: false).eraseToAnyPublisher()
    }
}

@MainActor
final class FakeLicense: OnboardingLicenseGateway {
    var isActive = false
    var isValidating = false
    var lastError: String?
    var probeOutcome = OnboardingLicenseOutcome(isValid: true, errorMessage: nil)
    var activateOutcome = OnboardingLicenseOutcome(isValid: true, errorMessage: nil)
    var probedKeys: [String] = []
    var activatedKeys: [String] = []
    /// When true, `activate` suspends until `release()` is called, which is how the
    /// "late completion after dismissal" case is reproduced deterministically.
    var gateActivation = false
    private var gate: CheckedContinuation<Void, Never>?

    func probe(_ key: String) async -> OnboardingLicenseOutcome {
        probedKeys.append(key)
        return probeOutcome
    }

    func activate(_ key: String) async -> OnboardingLicenseOutcome {
        if gateActivation {
            await withCheckedContinuation { gate = $0 }
        }
        activatedKeys.append(key)
        if activateOutcome.isValid { isActive = true }
        return activateOutcome
    }

    func release() {
        gate?.resume()
        gate = nil
    }
}

@MainActor
final class FakeProviderKeys: OnboardingProviderKeyGateway {
    var validationError: String?
    var health: ProviderHealth = .healthy
    var persistSucceeds = true
    var stored: [CloudProvider: String] = [:]
    var probeCount = 0

    func probe(_ provider: CloudProvider, apiKey: String) async -> ProviderHealth {
        probeCount += 1
        return health
    }

    @discardableResult
    func persist(_ key: String, for provider: CloudProvider) -> Bool {
        guard persistSucceeds else {
            validationError = "keychain denied"
            return false
        }
        // Mirrors APIKeySettingsManager: writing an empty string deletes the entry.
        if key.isEmpty {
            stored[provider] = nil
        } else {
            stored[provider] = key
        }
        return true
    }

    func hasKey(for provider: CloudProvider) -> Bool { stored[provider] != nil }

    func currentKey(for provider: CloudProvider) -> String { stored[provider] ?? "" }
}

@MainActor
final class FakeAudio: OnboardingAudioGateway {
    static let connectedDevices = [
        OnboardingInputDevice(id: "builtin", name: "MacBook Pro Microphone"),
        OnboardingInputDevice(id: "usb", name: "External USB Microphone")
    ]

    var devices: [OnboardingInputDevice] = FakeAudio.connectedDevices
    /// Stands in for AudioRecordingManager's @Published device list. Seeded so
    /// subscribing mirrors the live adapter's immediate first value.
    let devicesSubject = CurrentValueSubject<[OnboardingInputDevice], Never>(FakeAudio.connectedDevices)
    var selectedDeviceID: String?
    /// The persisted preference. Kept separate from `selectedDeviceID` so tests
    /// can reproduce the case where the remembered microphone is unplugged.
    var storedDeviceID: String?
    var refreshDeviceCalls = 0
    var refreshPermissionCalls = 0
    var previewStarts = 0
    var previewStops = 0
    var toggleCalls = 0
    var stopForExitCalls = 0
    var clearTranscriptCalls = 0
    let recording = CurrentValueSubject<Bool, Never>(false)
    let transcriptSubject = CurrentValueSubject<String, Never>("")

    func refreshDevices() { refreshDeviceCalls += 1 }
    func refreshMicrophonePermission() { refreshPermissionCalls += 1 }
    /// A device is plugged in or pulled out while the step is open.
    func publish(devices newDevices: [OnboardingInputDevice]) {
        devices = newDevices
        devicesSubject.send(newDevices)
    }
    func selectDevice(id: String?) { selectedDeviceID = id; storedDeviceID = id }
    /// Mirrors the live adapter: the preference goes back even when the device
    /// it names is absent, while the open device only reopens if still present.
    func restoreDevice(storedID: String?, openID: String?) {
        storedDeviceID = storedID
        selectedDeviceID = devices.contains { $0.id == openID } ? openID : nil
    }
    func startInputLevelPreview() { previewStarts += 1 }
    func stopInputLevelPreview() { previewStops += 1 }
    func toggleTestRecording() { toggleCalls += 1 }
    func stopRecordingForExit() { stopForExitCalls += 1 }
    func clearTranscript() { clearTranscriptCalls += 1; transcriptSubject.send("") }
    var isRecordingPublisher: AnyPublisher<Bool, Never> { recording.eraseToAnyPublisher() }
    var transcriptPublisher: AnyPublisher<String, Never> { transcriptSubject.eraseToAnyPublisher() }
    var devicesPublisher: AnyPublisher<[OnboardingInputDevice], Never> { devicesSubject.eraseToAnyPublisher() }
}

struct FakeRestorePoint: OnboardingRestorePoint {
    let state: String
}

/// Stands in for Core Data, AppState, and UserDefaults. `productionState` is the
/// single observable fact the rollback tests assert on.
@MainActor
final class FakeCommitter: OnboardingSourceCommitting {
    static let seed = "seeded-default-mode"

    var productionState = FakeCommitter.seed
    var applied: [OnboardingStagedSource] = []
    var captureCount = 0
    var restoreCount = 0
    var markCompletedCount = 0
    var returnHomeCount = 0

    func captureRestorePoint() -> OnboardingRestorePoint {
        captureCount += 1
        return FakeRestorePoint(state: productionState)
    }

    func apply(_ staged: OnboardingStagedSource) {
        applied.append(staged)
        productionState = "\(staged.source.rawValue):\(staged.model):\(staged.cloudProvider ?? "-")"
    }

    func restore(_ point: OnboardingRestorePoint) {
        restoreCount += 1
        guard let point = point as? FakeRestorePoint else { return }
        productionState = point.state
    }

    func markOnboardingCompleted() { markCompletedCount += 1 }
    func returnToHome() { returnHomeCount += 1 }
}

// MARK: - Harness

@MainActor
private struct Harness {
    let permissions = FakePermissions()
    let catalog = FakeCatalog()
    let license = FakeLicense()
    let providerKeys = FakeProviderKeys()
    let audio = FakeAudio()
    let committer = FakeCommitter()
    let flow: OnboardingFlowModel

    init() {
        flow = OnboardingFlowModel(
            permissions: permissions,
            catalog: catalog,
            license: license,
            providerKeys: providerKeys,
            audio: audio,
            committer: committer,
            systemDefaultDeviceName: "System Default"
        )
    }

    /// Walk to a step, asserting each gate opens on the way.
    func advance(to target: OnboardingStep) {
        while flow.step < target {
            let moved = flow.advance()
            #expect(moved, "blocked at \(flow.step) on the way to \(target)")
            if !moved { return }
        }
    }

    /// Grant the microphone so the permissions gate opens.
    func grantMicrophone() {
        permissions.microphoneAuthorization = .authorized
        flow.refreshPermissions()
    }

    /// The shortest path to a usable on-device source.
    func stageInstalledOnDeviceModel() {
        grantMicrophone()
        flow.select(source: .onDevice)
        flow.select(model: FakeCatalog.parakeet)
        catalog.installed.insert(FakeCatalog.parakeet.id)
    }
}

// MARK: - Step gating

@MainActor
struct OnboardingStepGatingTests {
    @Test func welcomeAlwaysContinuesAndPermissionsBlockWithoutMicrophone() {
        let h = Harness()
        #expect(h.flow.step == .welcome)
        #expect(h.flow.canContinue)
        #expect(h.flow.advance())

        #expect(h.flow.step == .permissions)
        #expect(!h.flow.canContinue)
        #expect(!h.flow.advance())
        #expect(h.flow.step == .permissions)
    }

    @Test func sourceStepRequiresASelection() {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        #expect(!h.flow.canContinue)
        h.flow.select(source: .onDevice)
        #expect(h.flow.canContinue)
    }

    @Test func backStepsThroughTheFlowAndStopsAtWelcome() {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        #expect(h.flow.back())
        #expect(h.flow.step == .permissions)
        #expect(h.flow.back())
        #expect(h.flow.step == .welcome)
        #expect(!h.flow.back())
        #expect(h.flow.step == .welcome)
    }

    @Test func advanceStopsAtTheFinalStep() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        #expect(h.flow.step == .done)
        #expect(!h.flow.advance())
    }
}

// MARK: - Permission recovery

@MainActor
struct OnboardingPermissionTests {
    @Test func grantingMicrophoneAfterADenialReopensTheGate() async {
        let h = Harness()
        h.advance(to: .permissions)
        #expect(!h.flow.canContinue)

        h.permissions.requestResult = false
        h.flow.requestMicrophonePermission()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(!h.flow.hasMicrophonePermission)
        #expect(h.flow.permissionErrorMessage != nil)
        #expect(!h.flow.canContinue)

        // User grants it in System Settings; the flow re-reads on reactivation.
        h.permissions.microphoneAuthorization = .authorized
        h.flow.refreshPermissions()
        #expect(h.flow.hasMicrophonePermission)
        #expect(h.flow.canContinue)
    }

    @Test func aDeniedMicrophoneDeepLinksSettingsInsteadOfRePrompting() {
        let h = Harness()
        h.permissions.microphoneAuthorization = .denied
        h.flow.handleMicrophoneAction()
        #expect(h.permissions.openedMicrophoneSettings == 1)
        #expect(h.permissions.requestCount == 0)
    }

    @Test func accessibilityPollingResolvesThroughTheFlow() {
        let h = Harness()
        h.flow.handleAccessibilityAction()
        #expect(h.permissions.openedAccessibilitySettings == 1)
        #expect(h.flow.isPollingForAccessibility)
        // Accessibility is informational: it never blocks the gate.
        h.permissions.microphoneAuthorization = .authorized
        h.flow.refreshPermissions()
        h.advance(to: .permissions)
        #expect(h.flow.canContinue)
    }
}

// MARK: - Source branches

@MainActor
struct OnboardingSourceBranchTests {
    @Test func onDeviceBranchGatesOnAnInstalledModel() {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .onDevice)
        // Selecting the source pre-picks the recommended model.
        #expect(h.flow.selectedModel?.id == FakeCatalog.parakeet.id)

        #expect(h.flow.advance())
        #expect(h.flow.step == .configure)
        #expect(h.flow.canContinue)

        #expect(h.flow.advance())
        #expect(h.flow.step == .setup)
        // Nothing downloaded yet: the setup gate is closed.
        #expect(!h.flow.canContinue)

        h.flow.startSelectedModelDownload()
        #expect(h.catalog.startedDownloads == [FakeCatalog.parakeet.id])

        h.catalog.installed.insert(FakeCatalog.parakeet.id)
        #expect(h.flow.canContinue)
        #expect(h.flow.stagedSource?.model == FakeCatalog.parakeet.id)
        #expect(h.flow.stagedSource?.cloudProvider == nil)
    }

    @Test func cloudBranchNeedsAWorkingKeyNotJustTypedText() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .hyperwhisperCloud)
        h.advance(to: .configure)

        h.flow.licenseKeyInput = "some-key"
        #expect(!h.flow.canContinue, "typed text alone must not open the gate")

        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.license.probedKeys == ["some-key"])
        #expect(h.flow.licenseTestPassed == true)
        #expect(h.flow.canContinue)

        // Editing the key invalidates the pass.
        h.flow.licenseKeyInput = "another-key"
        #expect(!h.flow.keyValidated)
        #expect(!h.flow.canContinue)
    }

    @Test func cloudSetupGateOpensOnlyAfterActivation() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .setup)
        #expect(!h.flow.canContinue)

        h.flow.activateCloudLicense()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.license.activatedKeys == ["key"])
        #expect(h.flow.canContinue)
        #expect(h.flow.stagedSource?.cloudProvider == "hyperwhisper")
        #expect(h.flow.stagedSource?.postProcessingMode == 1)
    }

    @Test func failedActivationSurfacesItsErrorAndKeepsTheGateClosed() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "key"
        h.license.activateOutcome = .failure("license expired")

        h.flow.activateCloudLicense()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.flow.setupErrorMessage == "license expired")
        #expect(!h.flow.isSelectedSourceUsable)
    }

    @Test func providerBranchStagesTheChosenProvider() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .yourProvider)
        h.flow.select(provider: .groq)
        h.flow.apiKeyInput = "gsk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(h.flow.keyValidated)
        #expect(h.flow.stagedSource?.cloudProvider == CloudProvider.groq.rawValue)
        #expect(h.flow.stagedSource?.model == "cloud")
        #expect(h.flow.stagedSource?.postProcessingMode == 0)
    }
}

// MARK: - Provider validation

@MainActor
struct OnboardingProviderValidationTests {
    @Test func anUnauthorizedProbeNeverValidatesOrPersists() async {
        let h = Harness()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "bad-key"
        h.providerKeys.health = .unauthorized

        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(!h.flow.keyValidated)
        #expect(h.flow.providerTestHealth == .unauthorized)
        #expect(h.providerKeys.stored.isEmpty)
    }

    @Test func aHealthyProbeWithAFailedKeychainWriteIsNotAPass() async {
        let h = Harness()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "good-key"
        h.providerKeys.health = .healthy
        h.providerKeys.persistSucceeds = false

        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(!h.flow.keyValidated)
        #expect(h.flow.providerTestHealth == nil)
        #expect(h.flow.setupErrorMessage == "keychain denied")
    }

    /// `resetConfigureTestResults` runs on every appearance of the Configure step,
    /// so a Back navigation used to shut the gate on an already stored key.
    @Test func returningToConfigureKeepsTheGateOpenForAStoredProviderKey() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .yourProvider)
        h.advance(to: .configure)

        h.flow.apiKeyInput = "sk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.flow.canContinue)

        #expect(h.flow.advance())
        #expect(h.flow.back())
        // What the view does on every appearance of this step.
        h.flow.resetConfigureTestResults()

        #expect(!h.flow.keyValidated)
        #expect(h.flow.canContinue, "an already stored key must keep the gate open")
    }

    /// `license.lastError` and `providerKeys.validationError` are app-global and
    /// long lived. An error produced outside this flow must not render on the card.
    @Test func anErrorFromOutsideTheFlowIsNeverShown() {
        let h = Harness()
        h.license.lastError = "expired six months ago"
        h.providerKeys.validationError = "a keychain failure from another screen"

        h.flow.select(source: .hyperwhisperCloud)
        #expect(h.flow.setupErrorMessage == nil)

        h.flow.select(source: .yourProvider)
        #expect(h.flow.setupErrorMessage == nil)
    }

    @Test func changingProviderClearsTheKeyAndTheValidation() async {
        let h = Harness()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "sk-openai"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.flow.keyValidated)

        h.flow.select(provider: .deepgram)
        #expect(h.flow.apiKeyInput.isEmpty)
        #expect(!h.flow.keyValidated)
        #expect(h.flow.providerTestHealth == nil)
    }
}

// MARK: - Download failure surfacing (bug 2)

@MainActor
struct OnboardingDownloadErrorTests {
    @Test func parakeetDownloadFailureIsSurfaced() {
        let h = Harness()
        h.flow.select(source: .onDevice)
        h.flow.select(model: FakeCatalog.parakeet)

        h.catalog.errors.send(OnboardingDownloadErrors(whisper: nil, parakeet: "Parakeet download failed"))

        #expect(h.flow.setupErrorMessage == "Parakeet download failed")
    }

    @Test func whisperDownloadFailureIsSurfaced() {
        let h = Harness()
        h.flow.select(source: .onDevice)
        h.flow.select(model: FakeCatalog.whisper)

        h.catalog.errors.send(OnboardingDownloadErrors(whisper: "Whisper download failed", parakeet: nil))

        #expect(h.flow.setupErrorMessage == "Whisper download failed")
    }

    @Test func theOtherEnginesErrorIsNotAttributedToTheSelectedModel() {
        let h = Harness()
        h.flow.select(source: .onDevice)
        h.flow.select(model: FakeCatalog.parakeet)

        h.catalog.errors.send(OnboardingDownloadErrors(whisper: "stale whisper failure", parakeet: nil))

        #expect(h.flow.setupErrorMessage == nil)
    }

    @Test func switchingModelRepointsTheErrorAtItsOwnEngine() {
        let h = Harness()
        h.flow.select(source: .onDevice)
        h.catalog.errors.send(
            OnboardingDownloadErrors(whisper: "whisper failed", parakeet: "parakeet failed")
        )

        h.flow.select(model: FakeCatalog.whisper)
        #expect(h.flow.setupErrorMessage == "whisper failed")

        h.flow.select(model: FakeCatalog.parakeet)
        #expect(h.flow.setupErrorMessage == "parakeet failed")
    }
}

// MARK: - Download progress invalidation (bug 2)

@MainActor
struct OnboardingDownloadProgressTests {
    /// Progress is read through a plain function call, so the download tick is the
    /// only thing that tells SwiftUI to re-read it.
    @Test func catalogDownloadActivityInvalidatesTheFlow() {
        let h = Harness()
        h.flow.select(source: .onDevice)
        h.flow.select(model: FakeCatalog.parakeet)

        var invalidations = 0
        let observer = h.flow.objectWillChange.sink { _ in invalidations += 1 }
        defer { observer.cancel() }

        h.catalog.downloading.insert(FakeCatalog.parakeet.id)
        h.catalog.progresses[FakeCatalog.parakeet.id] = 0.42
        h.catalog.activity.send()

        #expect(invalidations == 1)
        #expect(h.flow.selectedModelProgress() == 0.42)
    }

    /// Issue #312. The setup step chooses between a percentage and an
    /// indeterminate bar from the stage, so the stage has to survive the trip
    /// across the catalog seam intact — associated values included.
    @Test func theCatalogStageReachesTheFlow() {
        let h = Harness()
        h.flow.select(source: .onDevice)
        h.flow.select(model: FakeCatalog.parakeet)

        #expect(h.flow.selectedModelStage() == nil)

        h.catalog.stages[FakeCatalog.parakeet.id] = .preparing
        #expect(h.flow.selectedModelStage() == ModelDownloadStage.preparing)

        h.catalog.stages[FakeCatalog.parakeet.id] = .downloading(completedFiles: 3, totalFiles: 22)
        #expect(h.flow.selectedModelStage()
                == ModelDownloadStage.downloading(completedFiles: 3, totalFiles: 22))

        h.catalog.stages[FakeCatalog.parakeet.id] = .processing
        #expect(h.flow.selectedModelStage() == ModelDownloadStage.processing)
    }

    /// The protocol's default implementation is the whole reason this change
    /// cannot regress an engine that publishes no stage: the view falls back to
    /// today's percentage rendering on `nil`.
    @Test func aCatalogWithNoStageOfItsOwnStaysNil() {
        let flow = OnboardingFlowModel(
            permissions: FakePermissions(),
            catalog: FakeCatalogWithoutStage(),
            license: FakeLicense(),
            providerKeys: FakeProviderKeys(),
            audio: FakeAudio(),
            committer: FakeCommitter(),
            systemDefaultDeviceName: "System Default"
        )
        flow.select(source: .onDevice)
        flow.select(model: FakeCatalog.parakeet)

        #expect(flow.selectedModel == FakeCatalog.parakeet)
        #expect(flow.selectedModelStage() == nil)
    }
}

// MARK: - Microphone lifecycle

@MainActor
struct OnboardingMicrophoneTests {
    @Test func systemDefaultIsTheFirstDeviceOption() {
        let h = Harness()
        h.flow.beginMicrophoneStep()

        #expect(h.flow.deviceOptions.first?.isSystemDefault == true)
        #expect(h.flow.deviceOptions.first?.name == "System Default")
        #expect(h.flow.deviceOptions.count == h.audio.devices.count + 1)
        #expect(h.flow.selectedDeviceID.isEmpty)
        #expect(h.flow.selectedDeviceName == "System Default")
    }

    @Test func enteringAndLeavingTheStepPairsThePreviewLifecycle() {
        let h = Harness()
        h.flow.beginMicrophoneStep()
        #expect(h.audio.refreshDeviceCalls == 1)
        #expect(h.audio.previewStarts == 1)
        #expect(h.audio.previewStops == 0)

        h.flow.endMicrophoneStep()
        #expect(h.audio.previewStops == 1)
    }

    @Test func choosingADeviceRepointsTheMeterAndPersistsThroughTheGateway() {
        let h = Harness()
        h.flow.beginMicrophoneStep()
        h.flow.selectDevice(id: "usb")

        #expect(h.audio.selectedDeviceID == "usb")
        #expect(h.audio.previewStarts == 2)
        #expect(h.flow.selectedDeviceName == "External USB Microphone")

        h.flow.selectDevice(id: "")
        #expect(h.audio.selectedDeviceID == nil)
    }

    /// Unplugging a microphone while the picker is open has to remove its row,
    /// otherwise the list keeps offering a device that no longer exists (bug 6).
    @Test func aDeviceChangeOnTheMicrophoneStepRefreshesTheOptions() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)
        h.flow.beginMicrophoneStep()
        #expect(h.flow.deviceOptions.contains(where: { $0.id == "usb" }))

        h.audio.publish(devices: [OnboardingInputDevice(id: "builtin", name: "MacBook Pro Microphone")])

        #expect(h.flow.deviceOptions.count == 2)
        #expect(!h.flow.deviceOptions.contains(where: { $0.id == "usb" }))
    }

    @Test func deviceChangesOffTheMicrophoneStepAreIgnored() {
        let h = Harness()
        h.flow.beginMicrophoneStep()
        let before = h.flow.deviceOptions.count

        // The flow is still on .welcome, so the step owns nothing to refresh.
        h.audio.publish(devices: [])

        #expect(h.flow.deviceOptions.count == before)
    }

    /// The picked device can disappear between the menu being drawn and the pick
    /// landing. Applying it would select a phantom row and, worse, mark a
    /// production write that never happened (bug 6).
    @Test func selectingADisconnectedDeviceIsIgnored() {
        let h = Harness()
        h.flow.beginMicrophoneStep()

        h.flow.selectDevice(id: "dock")

        #expect(h.flow.selectedDeviceID.isEmpty)
        #expect(h.audio.selectedDeviceID == nil)
        #expect(h.audio.storedDeviceID == nil)
        #expect(!h.flow.hasPendingProductionWrite)
    }

    @Test func everyExitPathReleasesTheMicrophone() {
        let h = Harness()
        h.flow.beginMicrophoneStep()
        h.flow.deferSetup()
        #expect(h.audio.previewStops >= 1)
        #expect(h.audio.stopForExitCalls >= 1)
    }
}

// MARK: - Try it step

@MainActor
struct OnboardingTryItTests {
    @Test func transcriptErrorsAreDetectedBySentinel() {
        let h = Harness()
        h.audio.transcriptSubject.send("Error: no speech detected")
        #expect(h.flow.transcriptIsError)
        #expect(h.flow.transcriptBody == "no speech detected")

        h.audio.transcriptSubject.send("Hello there")
        #expect(!h.flow.transcriptIsError)
        #expect(h.flow.transcriptBody == "Hello there")
    }

    @Test func leavingTheTryItStepStopsRecordingAndClearsTheTranscript() {
        let h = Harness()
        h.flow.beginTryItStep()
        h.audio.transcriptSubject.send("Hello there")
        h.flow.endTryItStep()
        #expect(h.audio.stopForExitCalls == 1)
        #expect(h.flow.transcript.isEmpty)
    }
}

// MARK: - Set Up Later rollback (bug 1)

@MainActor
struct OnboardingRollbackTests {
    @Test func setUpLaterAfterReachingTryItLeavesProductionStateUntouched() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .tryIt)

        // Reaching Try It is the one place the staged source is applied, because
        // the test recording has to run through it.
        #expect(h.committer.applied.count == 1)
        #expect(h.committer.productionState != FakeCommitter.seed)

        h.flow.deferSetup()

        #expect(h.committer.restoreCount == 1)
        #expect(h.committer.productionState == FakeCommitter.seed)
        #expect(h.committer.markCompletedCount == 1)
        #expect(!h.flow.hasPendingProductionWrite)
    }

    @Test func setUpLaterBeforeAnyWriteNeverTouchesProductionState() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)

        #expect(h.committer.applied.isEmpty)

        h.flow.deferSetup()

        #expect(h.committer.applied.isEmpty)
        #expect(h.committer.restoreCount == 0)
        #expect(h.committer.productionState == FakeCommitter.seed)
    }

    @Test func stagingASourceNeverWritesOnItsOwn() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .yourProvider)
        h.flow.select(provider: .openai)
        h.flow.apiKeyInput = "sk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(h.flow.stagedSource != nil)
        #expect(h.committer.applied.isEmpty)
        #expect(h.committer.productionState == FakeCommitter.seed)
    }

    /// "Test API key" has to write to the Keychain before the key can be trusted,
    /// so deferral must put the user's original key back.
    @Test func testingAKeyThenDeferringRestoresThePreviousProviderKey() async {
        let h = Harness()
        h.providerKeys.stored[.openai] = "sk-original"
        h.grantMicrophone()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "sk-temporary"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(h.providerKeys.stored[.openai] == "sk-temporary")
        #expect(h.flow.hasPendingProductionWrite)

        h.flow.deferSetup()

        #expect(h.providerKeys.stored[.openai] == "sk-original")
        #expect(!h.flow.hasPendingProductionWrite)
    }

    /// Repeated tests still roll back to what existed before onboarding, not to
    /// an intermediate key the user typed halfway through.
    @Test func onlyTheKeyPresentBeforeOnboardingIsRestored() async {
        let h = Harness()
        h.providerKeys.stored[.openai] = "sk-original"
        h.flow.select(source: .yourProvider)

        h.flow.apiKeyInput = "sk-first"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.flow.apiKeyInput = "sk-second"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.providerKeys.stored[.openai] == "sk-second")

        h.flow.deferSetup()
        #expect(h.providerKeys.stored[.openai] == "sk-original")
    }

    @Test func deferringRemovesAKeyThatDidNotExistBeforeTheFlow() async {
        let h = Harness()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "sk-new"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.providerKeys.hasKey(for: .openai))

        h.flow.deferSetup()
        #expect(!h.providerKeys.hasKey(for: .openai))
    }

    @Test func completingKeepsTheProviderKeyItWrote() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "sk-new"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value

        h.flow.complete()
        #expect(h.providerKeys.stored[.openai] == "sk-new")
    }

    @Test func deferringRestoresThePreviousInputDevice() {
        let h = Harness()
        h.audio.selectedDeviceID = "builtin"
        h.audio.storedDeviceID = "builtin"
        h.flow.beginMicrophoneStep()
        h.flow.selectDevice(id: "usb")
        #expect(h.audio.selectedDeviceID == "usb")
        #expect(h.flow.hasPendingProductionWrite)

        h.flow.deferSetup()
        #expect(h.audio.selectedDeviceID == "builtin")
        #expect(h.audio.storedDeviceID == "builtin")
    }

    /// The stored preference and the open device diverge whenever the remembered
    /// microphone is unplugged. `selectDevice` writes both, so deferral has to
    /// restore both: keeping the preference but leaving the onboarding pick open,
    /// or vice versa, are each a half-undo the user never asked for.
    @Test func deferringRestoresAStoredDeviceThatIsNotCurrentlyConnected() {
        let h = Harness()
        // Remembered: a dock mic that is not in `devices`, so nothing is open.
        h.audio.storedDeviceID = "dock"
        h.audio.selectedDeviceID = nil
        h.flow.beginMicrophoneStep()
        h.flow.selectDevice(id: "usb")
        #expect(h.audio.storedDeviceID == "usb")
        #expect(h.audio.selectedDeviceID == "usb")

        h.flow.deferSetup()
        // The preference survives even though the device it names is absent.
        #expect(h.audio.storedDeviceID == "dock")
        // And nothing is left open, which is where the flow found it.
        #expect(h.audio.selectedDeviceID == nil)
    }

    /// The system default is encoded as nil, which is a real value here rather
    /// than "nothing was captured".
    @Test func deferringRestoresTheSystemDefaultInputDevice() {
        let h = Harness()
        h.flow.beginMicrophoneStep()
        h.flow.selectDevice(id: "usb")

        h.flow.deferSetup()
        #expect(h.audio.selectedDeviceID == nil)
    }

    @Test func completingKeepsTheChosenInputDevice() {
        let h = Harness()
        h.flow.beginMicrophoneStep()
        h.flow.selectDevice(id: "usb")
        h.flow.complete()
        #expect(h.audio.selectedDeviceID == "usb")
    }

    @Test func deferringIsIdempotentAndCannotWriteAfterwards() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .tryIt)
        h.flow.deferSetup()
        h.flow.deferSetup()
        h.flow.complete()

        #expect(h.committer.restoreCount == 1)
        #expect(h.committer.markCompletedCount == 1)
        #expect(h.committer.productionState == FakeCommitter.seed)
    }
}

// MARK: - Completion commit

@MainActor
struct OnboardingCompletionTests {
    @Test func completingCommitsTheStagedSource() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.flow.complete()

        #expect(h.committer.applied.last?.source == .onDevice)
        #expect(h.committer.applied.last?.model == FakeCatalog.parakeet.id)
        #expect(h.committer.productionState.contains(FakeCatalog.parakeet.id))
        #expect(h.committer.restoreCount == 0)
        #expect(h.committer.markCompletedCount == 1)
        #expect(h.committer.returnHomeCount == 1)
        #expect(!h.flow.hasPendingProductionWrite)
        #expect(!h.flow.isLiveForTesting)
    }

    @Test func completingWithoutASourceStillClosesTheFlowCleanly() {
        let h = Harness()
        h.flow.complete()
        #expect(h.committer.applied.isEmpty)
        #expect(h.committer.productionState == FakeCommitter.seed)
        #expect(h.committer.markCompletedCount == 1)
    }
}

// MARK: - Re-checking the setup gate before the commit

/// The setup gate used to be positional: checked on the way out of `.setup` and
/// never again, so a model deleted or a license deactivated during the last two
/// steps still became the user's default. These pin the two places production
/// state can be written — completion, and entry to Try It — against a source that
/// stopped being usable after the user passed the gate honestly.
@MainActor
struct OnboardingCompletionGateTests {
    @Test func completeBouncesToSetupWhenTheModelDisappearedAfterTheGate() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        #expect(h.committer.applied.count == 1)

        h.catalog.installed.remove(FakeCatalog.parakeet.id)

        #expect(!h.flow.complete(), "a source that cannot transcribe must not be committed")
        #expect(h.flow.step == .setup)
        #expect(h.committer.applied.count == 1, "only the reversible Try It write, no second commit")
        #expect(h.committer.markCompletedCount == 0)
        #expect(h.committer.returnHomeCount == 0)
        #expect(h.flow.isLiveForTesting, "the flow stays open so the user can fix the source")
        #expect(h.flow.hasPendingProductionWrite)
        #expect(!h.flow.canContinue)
    }

    /// The reachable trigger: a license can be deactivated server side between the
    /// setup step and the finish, and the try it step makes that window minutes wide.
    @Test func completeBouncesToSetupWhenTheCloudLicenceWentInactiveAfterTheGate() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .setup)
        h.flow.activateCloudLicense()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .done)
        #expect(h.committer.applied.count == 1)

        h.license.isActive = false

        #expect(!h.flow.complete())
        #expect(h.flow.step == .setup)
        #expect(h.committer.applied.count == 1)
        #expect(h.committer.markCompletedCount == 0)
        #expect(h.committer.returnHomeCount == 0)
        #expect(h.flow.isLiveForTesting)
        #expect(!h.flow.canContinue)
    }

    /// The half of the gate no other test moves: `validatedProviders` still holds
    /// the provider while the Keychain entry behind it has gone.
    @Test func completeBouncesToSetupWhenTheProviderKeyVanishedAfterTheGate() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .yourProvider)
        h.advance(to: .configure)
        h.flow.apiKeyInput = "sk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .done)
        #expect(h.committer.applied.count == 1)

        h.providerKeys.stored[.openai] = nil

        #expect(!h.flow.complete())
        #expect(h.flow.step == .setup)
        #expect(h.committer.applied.count == 1)
        #expect(h.committer.markCompletedCount == 0)
        #expect(h.committer.returnHomeCount == 0)
        #expect(h.flow.isLiveForTesting)
        #expect(!h.flow.canContinue)
    }

    /// Called directly rather than through `Harness.advance(to:)`, which asserts
    /// that every call moved forward — the bounce deliberately does not.
    @Test func advanceBouncesToSetupWhenTheSourceDiedBeforeTryIt() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)

        h.catalog.installed.remove(FakeCatalog.parakeet.id)

        #expect(!h.flow.advance())
        #expect(h.flow.step == .setup,
                "Try It must record through the source the user set up, so a dead source is sent back rather than skipped past")
        #expect(h.committer.applied.isEmpty)
        #expect(h.committer.productionState == FakeCommitter.seed)
        #expect(!h.flow.canContinue)
        #expect(h.flow.isLiveForTesting)
    }

    @Test func fixingTheSourceAfterABounceLetsCompletionCommit() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())

        h.catalog.installed.insert(FakeCatalog.parakeet.id)

        #expect(h.flow.complete())
        #expect(h.committer.applied.last?.source == .onDevice)
        #expect(h.committer.applied.last?.model == FakeCatalog.parakeet.id)
        #expect(h.committer.productionState.contains(FakeCatalog.parakeet.id))
        #expect(h.committer.restoreCount == 0)
        #expect(h.committer.markCompletedCount == 1)
        #expect(!h.flow.hasPendingProductionWrite)
        #expect(!h.flow.isLiveForTesting)
    }

    /// The point of refusing the transition rather than silently suppressing the
    /// write: once the source is fixed, Try It records through the fixed source.
    @Test func fixingTheSourceAfterAnAdvanceBounceRecordsThroughTheNewSource() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.advance())

        h.catalog.installed.insert(FakeCatalog.parakeet.id)
        h.advance(to: .tryIt)

        #expect(h.committer.applied.count == 1)
        #expect(h.committer.applied.last?.model == FakeCatalog.parakeet.id)
        #expect(h.committer.productionState != FakeCommitter.seed)
    }

    /// The bounce deliberately leaves the Try It write applied, because the user is
    /// about to walk forward through Try It again and it has to record through the
    /// source they just fixed. Set Up Later must still put everything back.
    @Test func bouncingBackKeepsTheReversibleWriteRollbackable() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())

        h.flow.deferSetup()

        #expect(h.committer.restoreCount == 1)
        #expect(h.committer.productionState == FakeCommitter.seed)
        #expect(!h.flow.hasPendingProductionWrite)
    }
}

// MARK: - The bounce has to explain itself

/// A bounce rewinds the sheet by up to three steps and leaves Continue disabled.
/// If nothing on screen says why, that is a worse experience than the bug it
/// replaced, so these pin the explanation as hard as the refusal itself: without
/// them, a regression that armed no note at all would pass every test above.
@MainActor
struct OnboardingBounceExplanationTests {
    @Test func theOnDeviceBounceTellsTheUserWhyTheyWereSentBack() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)

        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking,
                "the setup card renders its note off this, and nothing else on the step explains the rewind")
        // Why the note has to be its own surface rather than another
        // `setupErrorMessage`: that property only republishes failures this flow
        // PRODUCED, and a model deleted from under us is not one of them.
        #expect(h.flow.setupErrorMessage == nil,
                "no download failed, so the download-error channel has nothing to say here")
    }

    @Test func theCloudBounceTellsTheUserWhyTheyWereSentBack() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .setup)
        h.flow.activateCloudLicense()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .done)

        h.license.isActive = false

        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)
        #expect(h.flow.setupErrorMessage == nil,
                "activation succeeded; the licence lapsed afterwards, so there is no activation error to show")
    }

    @Test func theProviderBounceTellsTheUserWhyTheyWereSentBack() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .yourProvider)
        h.advance(to: .configure)
        h.flow.apiKeyInput = "sk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .done)

        h.providerKeys.stored[.openai] = nil

        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)
        #expect(h.flow.setupErrorMessage == nil,
                "the key was written successfully; it vanished afterwards, so there is no Keychain error to show")
    }

    /// The other bounce site. Same note, because the user lands on the same step
    /// with the same disabled Continue.
    @Test func theAdvanceBounceTellsTheUserWhyTheyWereSentBack() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)

        #expect(!h.flow.advance())
        #expect(h.flow.selectedSourceStoppedWorking)
    }

    /// Reaching `.setup` the ordinary way is not a bounce. A first-run user who
    /// has simply not downloaded the model yet must not be told their source
    /// stopped working.
    @Test func arrivingAtSetupNormallyShowsNoBounceNote() {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .onDevice)
        h.advance(to: .setup)

        #expect(h.flow.step == .setup)
        #expect(!h.flow.isSelectedSourceUsable, "the gate is shut, but nothing has been taken away")
        #expect(!h.flow.selectedSourceStoppedWorking)
    }

    /// The note is derived from the gate, not stored, so fixing the source takes
    /// it away with no separate bookkeeping to forget.
    @Test func fixingTheSourceTakesTheBounceNoteAway() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)

        h.catalog.installed.insert(FakeCatalog.parakeet.id)

        #expect(!h.flow.selectedSourceStoppedWorking)
    }

    /// The note names one source. Picking a different one makes it a statement
    /// about something the user is no longer setting up.
    @Test func choosingADifferentSourceTakesTheBounceNoteAway() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)

        h.flow.select(source: .hyperwhisperCloud)

        #expect(!h.flow.selectedSourceStoppedWorking)
    }

    /// A bounce off a later step, then forward again, then a second bounce: the
    /// note has to come back rather than be spent by the first one.
    @Test func aSecondBounceArmsTheNoteAgain() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.advance())

        h.catalog.installed.insert(FakeCatalog.parakeet.id)
        h.advance(to: .done)
        #expect(!h.flow.selectedSourceStoppedWorking)

        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)
    }
}

// MARK: - The bounce note goes quiet while it is being obeyed

/// Review round 2, finding 1. The note prescribes exactly one action, and the
/// gate it is derived from cannot reopen until that action finishes — a 474 MB
/// Parakeet download takes minutes. Without these, the card renders a progress
/// bar at "37%" with a red note underneath telling the user to start the
/// download they are watching. Suppressed, never spent: every test here also
/// pins that the note comes back if the repair does not land.
@MainActor
struct OnboardingBounceNoteDuringRepairTests {
    /// The exact scenario in the finding: deleted model, bounce, press Download.
    @Test func theOnDeviceNoteIsSilentWhileTheReDownloadRuns() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)

        h.flow.startSelectedModelDownload()
        #expect(h.catalog.startedDownloads == [FakeCatalog.parakeet.id])
        // What the real managers publish once the transfer is under way. The
        // gate stays shut for the whole of it: nothing is installed yet.
        h.catalog.downloading.insert(FakeCatalog.parakeet.id)
        h.catalog.progresses[FakeCatalog.parakeet.id] = 0.37

        #expect(!h.flow.isSelectedSourceUsable, "still not installed, so the gate is still shut")
        #expect(!h.flow.selectedSourceStoppedWorking,
                "the note must not ask for the download that is on screen behind it")
    }

    /// Suppressed, not spent. A cancelled or failed transfer puts the user back
    /// in exactly the state the note describes, so the note has to return.
    @Test func theOnDeviceNoteReturnsIfTheReDownloadNeverLands() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())

        h.flow.startSelectedModelDownload()
        h.catalog.downloading.insert(FakeCatalog.parakeet.id)
        #expect(!h.flow.selectedSourceStoppedWorking)

        // The download drops out without installing anything.
        h.catalog.downloading.remove(FakeCatalog.parakeet.id)

        #expect(h.flow.selectedSourceStoppedWorking,
                "the source is unusable again, so the instruction is live again")
    }

    /// The download that does land closes the gate's own condition, so the note
    /// goes away for the original reason rather than for the in-flight one.
    @Test func theOnDeviceNoteStaysAwayOnceTheReDownloadFinishes() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())

        h.flow.startSelectedModelDownload()
        h.catalog.downloading.insert(FakeCatalog.parakeet.id)
        h.catalog.downloading.remove(FakeCatalog.parakeet.id)
        h.catalog.installed.insert(FakeCatalog.parakeet.id)

        #expect(h.flow.isSelectedSourceUsable)
        #expect(!h.flow.selectedSourceStoppedWorking)
        #expect(h.flow.complete(), "and the commit the bounce refused now goes through")
    }

    /// Same shape on the cloud card: the note names Activate, and the Activate
    /// button spends the whole round trip showing a spinner.
    @Test func theCloudNoteIsSilentWhileActivationIsInFlight() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .setup)
        h.flow.activateCloudLicense()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .done)

        h.license.isActive = false
        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)

        // Re-activate, and hold the call at its suspension point.
        h.license.gateActivation = true
        h.flow.activateCloudLicense()
        let task = h.flow.lastAsyncTaskForTesting
        await Task.yield()

        #expect(h.flow.isActivatingLicense)
        #expect(!h.flow.isSelectedSourceUsable)
        #expect(!h.flow.selectedSourceStoppedWorking,
                "the button is already spinning; the note must not repeat its own label back")

        h.license.release()
        await task?.value

        #expect(h.flow.isSelectedSourceUsable)
        #expect(!h.flow.selectedSourceStoppedWorking)
    }

    /// A refused re-activation is the cloud version of a failed download.
    @Test func theCloudNoteReturnsWhenActivationIsRefused() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .setup)
        h.flow.activateCloudLicense()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .done)

        h.license.isActive = false
        #expect(!h.flow.complete())

        h.license.activateOutcome = OnboardingLicenseOutcome(isValid: false, errorMessage: "declined")
        h.flow.activateCloudLicense()
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(!h.flow.isActivatingLicense)
        #expect(h.flow.selectedSourceStoppedWorking,
                "activation came back refused, so the instruction is live again")
    }

    /// The BYOK card's only action, `saveProviderKey()`, is synchronous, so it
    /// has no in-flight window to sit through — and the suppression must be per
    /// source, not a global "something is busy". A model download running for a
    /// source the user is not setting up cannot silence this note.
    @Test func theProviderNoteIsNotSilencedByUnrelatedDownloadActivity() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .yourProvider)
        h.advance(to: .configure)
        h.flow.apiKeyInput = "sk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.advance(to: .done)

        h.providerKeys.stored[.openai] = nil
        #expect(!h.flow.complete())
        #expect(h.flow.selectedSourceStoppedWorking)

        h.catalog.downloading.insert(FakeCatalog.parakeet.id)

        #expect(h.flow.selectedSourceStoppedWorking,
                "an on-device download says nothing about a missing Keychain entry")
    }

    /// Suppression is scoped to the source too on the way in: a cloud activation
    /// in flight cannot quiet an on-device note.
    @Test func theOnDeviceNoteIsNotSilencedByAnInFlightActivation() async {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .done)
        h.catalog.installed.remove(FakeCatalog.parakeet.id)
        #expect(!h.flow.complete())

        h.license.gateActivation = true
        h.flow.licenseKeyInput = "key"
        h.flow.activateCloudLicense()
        let task = h.flow.lastAsyncTaskForTesting
        await Task.yield()

        #expect(h.flow.isActivatingLicense)
        #expect(h.flow.selectedSourceStoppedWorking,
                "the selected source is on-device; a licence call is not its repair")

        h.license.release()
        await task?.value
    }
}

/// The bounce copy is authored in `Base.lproj` and synced into all 39 sibling
/// locales (review round 2, finding 2). These pin the Base entries, which are
/// what `String.localized` falls back to for any locale that ever drifts (see
/// `LocalizationFallbackTests`).
struct OnboardingBounceCopyTests {
    private let keys = [
        "onboarding.setup.stopped.onDevice",
        "onboarding.setup.stopped.cloud",
        "onboarding.setup.stopped.provider"
    ]

    @Test func everyBounceNoteKeyResolvesToRealCopy() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        for key in keys {
            let value = try #require(base.localizedValueIfPresent(forKey: key),
                                     "\(key) is missing from Base.lproj")
            #expect(value != key)
            #expect(key.localized != key, "\(key) renders as its own identifier")
        }
    }

    /// Both parameterised notes are rendered through `localized(arguments:)`.
    /// Drop the placeholder and `String(format:)` silently discards the model or
    /// provider name, leaving copy that names nothing.
    @Test func theParameterisedBounceNotesKeepTheirPlaceholder() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        for key in ["onboarding.setup.stopped.onDevice", "onboarding.setup.stopped.provider"] {
            let value = try #require(base.localizedValueIfPresent(forKey: key))
            #expect(value.contains("%@"), "\(key) no longer interpolates its name")
        }
    }
}

// MARK: - Late async completion (bug 3)

@MainActor
struct OnboardingActivationLifetimeTests {
    @Test func activationThatFinishesAfterDismissalCannotWriteFlowState() async {
        let h = Harness()
        h.grantMicrophone()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "key"
        h.license.gateActivation = true

        h.flow.activateCloudLicense()
        let task = h.flow.lastAsyncTaskForTesting
        // Let the activation reach its suspension point.
        await Task.yield()

        h.flow.deferSetup()
        #expect(!h.flow.isLiveForTesting)
        #expect(!h.flow.hasInFlightWorkForTesting)

        // The network call now lands, long after the sheet closed.
        h.license.release()
        await task?.value

        #expect(!h.flow.keyValidated)
        #expect(h.flow.setupErrorMessage == nil)
        #expect(h.committer.productionState == FakeCommitter.seed)
    }

    @Test func aStaleKeyProbeResultIsDiscarded() async {
        let h = Harness()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "first-key"
        h.flow.testAccessKey()
        // Edit the key before the probe result is consumed.
        h.flow.licenseKeyInput = "second-key"
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(!h.flow.keyValidated)
        #expect(h.flow.licenseTestPassed == nil)
    }

    /// The staleness check has to run BEFORE the persist: a probe the user has
    /// abandoned must never write the Keychain or set a restore point, which
    /// would otherwise flip `hasPendingProductionWrite` for a key nobody kept.
    @Test func aStaleProviderKeyProbeResultIsNeverPersisted() async {
        let h = Harness()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "sk-abandoned"
        h.flow.testProviderKey()
        // Edit the key before the probe result is consumed.
        h.flow.apiKeyInput = "sk-current"
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(h.providerKeys.stored.isEmpty)
        #expect(!h.flow.keyValidated)
        #expect(!h.flow.hasPendingProductionWrite)
        #expect(h.providerKeys.probeCount == 1)
    }

    @Test func switchingProviderMidProbeDiscardsThePersist() async {
        let h = Harness()
        h.flow.select(source: .yourProvider)
        h.flow.select(provider: .openai)
        h.flow.apiKeyInput = "sk-openai"
        h.flow.testProviderKey()
        // Switch provider before the probe result is consumed.
        h.flow.select(provider: .deepgram)
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(h.providerKeys.stored.isEmpty)
        #expect(!h.flow.keyValidated)
        #expect(!h.flow.hasPendingProductionWrite)
    }
}

// MARK: - Per-session validation records (BYOK + Cloud gates)

@MainActor
struct OnboardingSessionValidationTests {
    /// A key that merely sits in the Keychain was never verified by this
    /// session, so neither the configure gate nor the setup gate may trust it.
    @Test func aStoredButNeverProbedKeyKeepsBothGatesShut() {
        let h = Harness()
        h.providerKeys.stored[.openai] = "sk-preexisting"
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .yourProvider)
        h.advance(to: .configure)

        #expect(!h.flow.canContinue)
        #expect(!h.flow.isSelectedSourceUsable)
    }

    @Test func aValidatedKeySurvivesBackNavigationOnTheSetupGate() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .yourProvider)
        h.advance(to: .configure)

        h.flow.apiKeyInput = "sk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.flow.isSelectedSourceUsable)

        #expect(h.flow.advance())
        #expect(h.flow.back())
        // What the view does on every appearance of the configure step.
        h.flow.resetConfigureTestResults()

        #expect(!h.flow.keyValidated)
        #expect(h.flow.canContinue)
        #expect(h.flow.isSelectedSourceUsable)
    }

    @Test func validationIsRememberedPerProviderNotGlobally() async {
        let h = Harness()
        h.providerKeys.stored[.deepgram] = "dg-preexisting"
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .yourProvider)
        h.advance(to: .configure)

        h.flow.select(provider: .groq)
        h.flow.apiKeyInput = "gsk-test"
        h.flow.testProviderKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.flow.canContinue)

        // Deepgram's stored key was never probed this session.
        h.flow.select(provider: .deepgram)
        #expect(!h.flow.canContinue)
        #expect(!h.flow.isSelectedSourceUsable)

        // Groq's validation record survives the round trip.
        h.flow.select(provider: .groq)
        #expect(h.flow.canContinue)
    }

    @Test func returningToConfigureKeepsTheCloudGateOpenForTheTestedKey() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .hyperwhisperCloud)
        h.advance(to: .configure)

        h.flow.licenseKeyInput = "hw-key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.flow.canContinue)

        #expect(h.flow.advance())
        #expect(h.flow.back())
        h.flow.resetConfigureTestResults()

        #expect(!h.flow.keyValidated)
        #expect(h.flow.canContinue, "the field still holds the exact key that passed")
    }

    @Test func editingTheRememberedKeyClosesTheGateUntilItMatchesAgain() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .hyperwhisperCloud)
        h.advance(to: .configure)

        h.flow.licenseKeyInput = "hw-key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        h.flow.resetConfigureTestResults()
        #expect(h.flow.canContinue)

        h.flow.licenseKeyInput = "hw-key-edited"
        #expect(!h.flow.canContinue)

        // Retyping the validated key reopens the gate without another probe.
        h.flow.licenseKeyInput = "hw-key"
        #expect(h.flow.canContinue)
        #expect(h.license.probedKeys == ["hw-key"])
    }

    @Test func aFailedReProbeOfTheRememberedKeyClosesTheGate() async {
        let h = Harness()
        h.grantMicrophone()
        h.advance(to: .source)
        h.flow.select(source: .hyperwhisperCloud)
        h.advance(to: .configure)

        h.flow.licenseKeyInput = "hw-key"
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value
        #expect(h.flow.canContinue)

        // The key gets revoked server side; a re-probe of the SAME key fails.
        h.license.probeOutcome = .failure("revoked")
        h.flow.testAccessKey()
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(!h.flow.canContinue)
        h.flow.resetConfigureTestResults()
        #expect(!h.flow.canContinue, "a revoked key must not stay remembered")
    }
}

// MARK: - Mutations after the flow finished (#321)

/// `finish()` flips `isLive` false and both exits clear every restore point, so
/// anything written after that point is permanent and un-rollbackable. These pin
/// the five mutating entry points as no-ops once the sheet has gone, through BOTH
/// exits, plus the positive control that the guards are not inverted and the one
/// exit path that is deliberately NOT guarded.
@MainActor
struct OnboardingPostFinishMutationTests {
    @Test func saveProviderKeyAfterDeferralWritesNothingToTheKeychain() {
        let h = Harness()
        h.flow.select(source: .yourProvider)
        h.flow.apiKeyInput = "sk-late"

        h.flow.deferSetup()
        #expect(!h.flow.isLiveForTesting)

        h.flow.saveProviderKey()

        // Nothing reached the Keychain, and no restore point was captured for a
        // write that rollback can no longer undo.
        #expect(h.providerKeys.stored.isEmpty)
        #expect(!h.flow.hasPendingProductionWrite)
    }

    @Test func activatingAfterDeferralNeverReachesTheLicenceGateway() async {
        let h = Harness()
        h.flow.select(source: .hyperwhisperCloud)
        h.flow.licenseKeyInput = "hw-key"

        h.flow.deferSetup()
        #expect(!h.flow.isLiveForTesting)

        h.flow.activateCloudLicense()
        // Nil when the guard held; without it this awaits the activation the call
        // would have spawned, so the gateway assertion below cannot pass by timing.
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(h.license.activatedKeys.isEmpty)
        #expect(!h.flow.isActivatingLicense)
        #expect(!h.flow.hasInFlightWorkForTesting)
    }

    /// The load bearing one. `complete()` clears `restorePoint`, so stepping into
    /// `.tryIt` afterwards would re-enter `applyStagedSourceReversibly()`, find no
    /// restore point, and capture a fresh one nobody will ever restore.
    @Test func advancingAfterCompletionCannotReapplyTheStagedSource() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)

        #expect(h.flow.complete())
        #expect(!h.flow.isLiveForTesting)
        // The commit itself captures and applies once, so the snapshot has to be
        // taken AFTER it: the question is whether `advance()` moves them again.
        let capturesAfterCommit = h.committer.captureCount
        let appliedAfterCommit = h.committer.applied.count
        #expect(capturesAfterCommit == 1)
        #expect(appliedAfterCommit == 1)

        #expect(!h.flow.advance())

        #expect(h.flow.step == .microphone)
        #expect(h.committer.captureCount == capturesAfterCommit)
        #expect(h.committer.applied.count == appliedAfterCommit)
    }

    /// `beginMicrophoneStep()` runs FIRST, while the flow is still live, because
    /// `selectDevice` already rejects an id that is not in `deviceOptions` and the
    /// options are empty until the device list is applied. Without that setup this
    /// test would pass on the pre-existing guard alone (see
    /// `selectingADisconnectedDeviceIsIgnored`) and prove nothing.
    @Test func selectingADeviceAfterDeferralCannotRepointTheInput() {
        let h = Harness()
        h.flow.beginMicrophoneStep()
        #expect(h.flow.deviceOptions.contains(where: { $0.id == "usb" }),
                "the pick must be a real, connected device or the old guard rejects it")

        h.flow.deferSetup()
        #expect(!h.flow.isLiveForTesting)

        h.flow.selectDevice(id: "usb")

        #expect(h.audio.selectedDeviceID == nil)
        #expect(h.audio.storedDeviceID == nil)
        #expect(h.flow.selectedDeviceID.isEmpty)
        #expect(!h.flow.hasPendingProductionWrite)
    }

    @Test func togglingTheTestRecordingAfterDeferralStartsNothing() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .tryIt)
        let togglesBeforeExit = h.audio.toggleCalls

        h.flow.deferSetup()
        h.flow.toggleTestRecording()

        // A toggle here would START a recording with no sheet left to stop it.
        #expect(h.audio.toggleCalls == togglesBeforeExit)
        // `finish()` moves this counter itself, so it is never asserted at zero.
        #expect(h.audio.stopForExitCalls >= 1)
    }

    /// `finish()` is reached through both exits, so the refusal cannot be specific
    /// to Set Up Later.
    @Test func theSameRefusalHoldsAfterCompletionNotJustDeferral() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)

        #expect(h.flow.complete())
        #expect(!h.flow.isLiveForTesting)

        h.flow.apiKeyInput = "sk-after-the-commit"
        h.flow.saveProviderKey()

        #expect(h.providerKeys.stored.isEmpty)
        #expect(!h.flow.hasPendingProductionWrite)
    }

    /// The positive control. Every refusal above would still pass with the guard
    /// inverted, which would break the whole flow instead of protecting it.
    @Test func theGuardsDoNotBlockAnythingWhileTheSheetIsLive() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .microphone)
        h.flow.beginMicrophoneStep()
        #expect(h.flow.isLiveForTesting)

        h.flow.selectDevice(id: "usb")
        #expect(h.audio.selectedDeviceID == "usb")
        #expect(h.audio.storedDeviceID == "usb")

        h.flow.apiKeyInput = "sk-live"
        h.flow.saveProviderKey()
        #expect(h.providerKeys.stored[.openai] == "sk-live")

        let togglesBefore = h.audio.toggleCalls
        h.flow.toggleTestRecording()
        #expect(h.audio.toggleCalls == togglesBefore + 1)

        #expect(h.flow.advance())
        #expect(h.flow.step == .tryIt)
    }

    /// The deliberate non-guard. `.onDisappear` fires after the sheet is dismissed,
    /// so the microphone release backstop has to keep working post-`finish()`;
    /// gating it would strand an open recording.
    @Test func leavingTheTryItStepStillReleasesTheMicrophoneAfterCompletion() {
        let h = Harness()
        h.stageInstalledOnDeviceModel()
        h.advance(to: .tryIt)

        #expect(h.flow.complete())
        let stopsAfterCommit = h.audio.stopForExitCalls

        h.flow.endTryItStep()

        #expect(h.audio.stopForExitCalls == stopsAfterCommit + 1)
    }
}

// MARK: - The post-finish invariant, swept (#321)

/// Every side effect the six fakes record, in one comparable value. This is what
/// lets the sweep below say "no dependency was reached" as a SINGLE assertion
/// instead of one per method, so the guarded list can grow without the assertions
/// growing with it.
private struct OnboardingSideEffects: Equatable {
    var microphoneRequests = 0
    var openedMicrophoneSettings = 0
    var openedAccessibilitySettings = 0
    var startedDownloads: [String] = []
    var probedLicenseKeys: [String] = []
    var activatedLicenseKeys: [String] = []
    var providerProbes = 0
    var storedProviderKeys: [CloudProvider: String] = [:]
    var deviceRefreshes = 0
    var permissionRefreshes = 0
    var previewStarts = 0
    var previewStops = 0
    var recordingToggles = 0
    var stopsForExit = 0
    var transcriptClears = 0
    var openDeviceID: String?
    var storedDeviceID: String?
    var appliedSources = 0
    var restorePointCaptures = 0
    var restores = 0
    var completionMarks = 0
    var returnsHome = 0
    var productionState = ""
}

private extension Harness {
    var sideEffects: OnboardingSideEffects {
        OnboardingSideEffects(
            microphoneRequests: permissions.requestCount,
            openedMicrophoneSettings: permissions.openedMicrophoneSettings,
            openedAccessibilitySettings: permissions.openedAccessibilitySettings,
            startedDownloads: catalog.startedDownloads,
            probedLicenseKeys: license.probedKeys,
            activatedLicenseKeys: license.activatedKeys,
            providerProbes: providerKeys.probeCount,
            storedProviderKeys: providerKeys.stored,
            deviceRefreshes: audio.refreshDeviceCalls,
            permissionRefreshes: audio.refreshPermissionCalls,
            previewStarts: audio.previewStarts,
            previewStops: audio.previewStops,
            recordingToggles: audio.toggleCalls,
            stopsForExit: audio.stopForExitCalls,
            transcriptClears: audio.clearTranscriptCalls,
            openDeviceID: audio.selectedDeviceID,
            storedDeviceID: audio.storedDeviceID,
            appliedSources: committer.applied.count,
            restorePointCaptures: committer.captureCount,
            restores: committer.restoreCount,
            completionMarks: committer.markCompletedCount,
            returnsHome: committer.returnHomeCount,
            productionState: committer.productionState
        )
    }

    /// A flow parked on the microphone step with everything primed: a usable
    /// on-device source, a populated device list, and both key fields filled. Every
    /// entry point called after the exit therefore has real work it WOULD do if its
    /// guard were missing, which is what makes the frozen snapshot mean something.
    func primeForTheSweep() {
        stageInstalledOnDeviceModel()
        advance(to: .microphone)
        flow.beginMicrophoneStep()
        flow.licenseKeyInput = "hw-late"
        flow.apiKeyInput = "sk-late"
    }

    /// Every entry point the `isLive` doc block lists as guarded, in that order.
    ///
    /// THIS IS THE LIST. Adding an `isLive` guard to the model means adding its
    /// method here; adding a mutating entry point WITHOUT a guard means adding it
    /// here and watching this fail. That is the whole point — the alternative is a
    /// bespoke test per method, which grows with the allow list and can never catch
    /// the method nobody thought to write a test for.
    func callEveryGuardedEntryPoint() {
        flow.back()
        flow.advance()
        flow.complete()
        flow.deferSetup()
        flow.handleMicrophoneAction()
        flow.requestMicrophonePermission()
        flow.handleAccessibilityAction()
        flow.testAccessKey()
        flow.testProviderKey()
        flow.saveProviderKey()
        flow.activateCloudLicense()
        flow.startSelectedModelDownload()
        flow.beginMicrophoneStep()
        flow.selectDevice(id: "usb")
        flow.beginTryItStep()
        flow.toggleTestRecording()
    }
}

/// The invariant itself, rather than the five methods #321 happened to name:
/// once the flow has finished, no guarded entry point moves the step machine,
/// reaches a dependency, or starts new work — through EITHER exit. The companion
/// test pins the deliberate exclusions, so the two together cover the model's
/// whole mutating surface.
@MainActor
struct OnboardingPostFinishInvariantTests {
    @Test func nothingGuardedRunsAfterCompletion() async {
        let h = Harness()
        h.primeForTheSweep()
        #expect(h.flow.complete())
        await sweepEveryGuardedEntryPoint(h)
    }

    @Test func nothingGuardedRunsAfterSetUpLater() async {
        let h = Harness()
        h.primeForTheSweep()
        h.flow.deferSetup()
        await sweepEveryGuardedEntryPoint(h)
    }

    /// The other half of the invariant. The exclusions are excluded for reasons,
    /// and both halves of each reason are asserted here: the release hooks really
    /// do still fire after the exit (the microphone backstop, which is why gating
    /// them was rejected), and none of the excluded paths touches the step machine
    /// or anything a rollback would have had to undo.
    @Test func theExcludedEntryPointsStillReleaseButNeverWrite() {
        let h = Harness()
        h.primeForTheSweep()
        #expect(h.flow.complete())
        let before = h.sideEffects

        // Release hooks: `.onDisappear` fires after `finish()`.
        h.flow.endMicrophoneStep()
        h.flow.endTryItStep()
        // Mirrors of system state.
        h.flow.refreshPermissions()
        h.flow.refreshDeviceOptions()
        // Staged, in-memory selection.
        h.flow.resetConfigureTestResults()
        h.flow.select(source: .yourProvider)
        h.flow.select(provider: .deepgram)
        h.flow.select(model: FakeCatalog.whisper)

        // Gating these would strand an open device or a running test recording,
        // which is the exact failure the guards exist to prevent.
        #expect(h.audio.previewStops == before.previewStops + 1)
        #expect(h.audio.stopForExitCalls == before.stopsForExit + 1)
        #expect(h.audio.clearTranscriptCalls == before.transcriptClears + 1)

        // And that is all they may do. Staging after the exit is harmless only
        // because `advance()` and `complete()` — the two paths that turn a staged
        // selection into a production write — are themselves guarded.
        #expect(h.flow.step == .microphone)
        #expect(!h.flow.hasPendingProductionWrite)
        #expect(h.committer.applied.count == before.appliedSources)
        #expect(h.committer.captureCount == before.restorePointCaptures)
        #expect(h.committer.restoreCount == before.restores)
        #expect(h.committer.productionState == before.productionState)
        #expect(h.providerKeys.stored == before.storedProviderKeys)
        #expect(h.audio.selectedDeviceID == before.openDeviceID)
        #expect(h.audio.storedDeviceID == before.storedDeviceID)
        #expect(h.catalog.startedDownloads == before.startedDownloads)
    }

    private func sweepEveryGuardedEntryPoint(_ h: Harness) async {
        #expect(!h.flow.isLiveForTesting, "the exit under test did not close the flow")
        let stepAtExit = h.flow.step
        let before = h.sideEffects

        h.callEveryGuardedEntryPoint()

        // Synchronous detectors first. Each async entry point sets its in-progress
        // flag and stores its task BEFORE its first suspension point, so a leaked
        // guard shows up here with no awaiting at all.
        #expect(!h.flow.hasInFlightWorkForTesting, "an entry point spawned work after the exit")
        #expect(!h.flow.isTestingKey)
        #expect(!h.flow.isActivatingLicense)

        // Then drain anything that did get spawned, so the snapshot below cannot
        // pass merely because a leaked task had not run yet.
        await h.flow.lastAsyncTaskForTesting?.value

        #expect(h.flow.step == stepAtExit, "the step machine moved after the exit")
        #expect(!h.flow.hasPendingProductionWrite)
        // The single assertion that covers all six fakes at once.
        #expect(h.sideEffects == before, "an entry point reached a dependency after the exit")
    }
}
