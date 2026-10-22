//
//  LicenseErrorDismissTests.swift
//  hyperwhisperTests
//
//  Issue #642: after a failed activation, Settings → HyperWhisper Cloud kept the
//  red "License key not found" line under an emptied, edited or re-opened key
//  field, because nothing cleared `LicenseManager.lastError`. The section now
//  calls `dismissLastError()` when the field changes and when the page closes.
//
//  These drive a real `LicenseManager` through a failed `activateLicense`, the
//  same call the Activate button makes, so `lastError` is set by production code.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LicenseErrorDismissTests {

    private func makeLicenseManager() -> LicenseManager {
        LicenseManager(
            networkService: NotFoundLicenseNetwork(),
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )
    }

    @Test func failedActivationErrorIsDroppedOnDismiss() async {
        let manager = makeLicenseManager()

        let result = await manager.activateLicense("HW-AAAA-BBBB-CCCC-DDDD")
        #expect(result.isValid == false)
        #expect(manager.lastError == NotFoundLicenseNetwork.message)

        manager.dismissLastError()
        #expect(manager.lastError == nil)
    }

    @Test func dismissKeepsTheErrorWhileACheckIsInFlight() {
        let manager = makeLicenseManager()
        manager.lastError = NotFoundLicenseNetwork.message
        manager.isValidating = true

        manager.dismissLastError()
        #expect(manager.lastError == NotFoundLicenseNetwork.message)
    }
}

private final class NotFoundLicenseNetwork: LicenseNetworkServing {
    static let message = "License key not found"

    private let notFound = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: NotFoundLicenseNetwork.message
    )

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult { notFound }
    func deactivateLicense() async -> (success: Bool, error: String?) { (true, nil) }
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult { notFound }
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult { notFound }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { nil }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
