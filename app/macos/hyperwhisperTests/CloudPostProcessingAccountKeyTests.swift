//
//  CloudPostProcessingAccountKeyTests.swift
//  hyperwhisperTests
//
//  Issue #1487: Local API `/post-process` on a HyperWhisper Cloud mode with no
//  account key answered `ok: true` with the caller's text unchanged and no
//  reason. Ray (inbox #298): it refuses with `MISSING_API_KEY`, as
//  `/transcribe` does. The endpoint keys that refusal on
//  `MutationSignal.cloudAccountMissing`, so these tests pin that the processor
//  sets it, on the single-segment AND the multi-segment Local API path.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct CloudPostProcessingAccountKeyTests {

    private static let text = "um so like the meeting is at three pm tomorrow okay"

    /// `AIPostProcessor.licenseManager` is `weak`, so the caller must hold the
    /// returned `LicenseManager` for the whole test. Dropped, it is nil at once
    /// and the processor takes its "license manager not available" exit, which
    /// never reaches the account-key check.
    private func makeProcessor() -> (AIPostProcessor, LicenseManager, ErrorBox) {
        let processor = AIPostProcessor(settingsManager: SettingsManager.shared)
        let licenseManager = LicenseManager(
            networkService: NoAccountKeyLicenseNetwork(),
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )
        processor.licenseManager = licenseManager
        let box = ErrorBox()
        processor.onPostProcessingError = { box.errors.append($0) }
        return (processor, licenseManager, box)
    }

    private func makeCloudMode(_ persistence: PersistenceController) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.preset = "hyper"
        mode.postProcessingMode = 1
        mode.postProcessingProvider = PostProcessingProvider.hyperwhisper.rawValue
        mode.cloudPostProcessingModel = "anthropic:claude-haiku-4-5"
        return mode
    }

    @Test func noAccountKeyFlagsTheSignalAndReturnsTheTextUnchanged() async throws {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeCloudMode(persistence)
        let (processor, licenseManager, box) = makeProcessor()
        let signal = MutationSignal()

        let result = try await processor.performAIPostProcessingPreservingBreaks(
            text: Self.text,
            mode: mode,
            applicationContext: ApplicationContext.none,
            mutationSignal: signal,
            onSegmentTextUpdate: { _ in }
        )

        #expect(result == Self.text)
        #expect(signal.cloudAccountMissing)
        #expect(!signal.didMutate)
        #expect(signal.resolvedModel == nil)
        #expect(processor.licenseManager === licenseManager)
        // The in-app pipeline still gets the actionable error, now naming the
        // real cause rather than a 401 from a doomed request.
        guard case .cloudAccountRequired? = box.errors.first else {
            Issue.record("expected .cloudAccountRequired, got \(box.errors)")
            return
        }
    }

    @Test func noAccountKeyIsFlaggedOnTheMultiSegmentPath() async throws {
        let persistence = PersistenceController(inMemory: true)
        let mode = makeCloudMode(persistence)
        let (processor, licenseManager, _) = makeProcessor()
        let signal = MutationSignal()

        _ = try await processor.performAIPostProcessingPreservingBreaks(
            text: "first part\n\nsecond part",
            mode: mode,
            applicationContext: ApplicationContext.none,
            mutationSignal: signal,
            onSegmentTextUpdate: { _ in }
        )

        #expect(signal.cloudAccountMissing)
        #expect(!signal.didMutate)
        #expect(!signal.anyPartialFailure)
        #expect(processor.licenseManager === licenseManager)
    }

}

private final class ErrorBox {
    var errors: [TranscriptionError] = []
}

/// No stored key, so `getTranscriptionIdentifier()` answers unlicensed.
private final class NoAccountKeyLicenseNetwork: LicenseNetworkServing {
    private let invalid = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: nil
    )

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult { invalid }
    func deactivateLicense() async -> (success: Bool, error: String?) { (true, nil) }
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult { invalid }
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult { invalid }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { nil }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
