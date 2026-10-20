//
//  OnboardingSeams.swift
//  hyperwhisper
//
//  The narrow protocols `OnboardingFlowModel` reaches production state through,
//  so the flow is testable without Core Data, the Keychain, a microphone, or the
//  network. Moved out of OnboardingFlowModel.swift unchanged (#911).
//

import Combine
import Foundation

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
    /// What the download is currently doing, when the engine behind `model`
    /// publishes it. `nil` means "no stage information available" and callers
    /// must fall back to rendering the fraction on its own — that is what keeps
    /// Whisper, which only ever publishes a fraction, on its existing UI.
    func stage(for model: OnboardingModelSelection) -> ModelDownloadStage?
    func startDownload(_ model: OnboardingModelSelection)
    /// Emits whenever either engine's error message changes. Carrying both in one
    /// value is what lets a Parakeet failure reach the UI (bug 2).
    var downloadErrors: AnyPublisher<OnboardingDownloadErrors, Never> { get }
    /// Emits whenever download state or progress changes for either engine. The
    /// catalog reads are plain function calls, so without this tick nothing tells
    /// SwiftUI that the setup step's progress bar has moved (bug 2).
    var downloadActivity: AnyPublisher<Void, Never> { get }
}

@MainActor
extension OnboardingModelCatalog {
    /// Default: no stage. Declared as a requirement so a conformer that has one
    /// still overrides it dynamically, but defaulted here so a conformer that has
    /// nothing to say — including the test fakes — needs no change at all.
    func stage(for model: OnboardingModelSelection) -> ModelDownloadStage? { nil }
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
