//
//  StubLicenseNetwork.swift
//  hyperwhisperTests
//
//  Issue #2198: one `LicenseNetworkServing` stub for the tests that only need
//  a fixed stored key and a fixed validation answer. Before this, six test
//  files each carried a private copy of the same twelve methods, so every new
//  protocol requirement had to be added six times.
//
//  The spies that record what they were called with (`LicenseProbeTests`,
//  `LicenseKeyFormatTests`, `RustLicenseStoreSecurityTests`) keep their own
//  conformances.
//

import Foundation
@testable import HyperWhisper

/// Answers `getStoredLicenseKey()` with `storedKey` and every activation,
/// validation and probe with `result`; nothing leaves the process.
final class StubLicenseNetwork: LicenseNetworkServing {
    /// The answer the stubs gave before they were shared: invalid, no customer,
    /// no message.
    static let invalid = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: nil
    )

    private let storedKey: String?
    private let result: LicenseValidationResult

    init(storedKey: String? = nil, result: LicenseValidationResult = StubLicenseNetwork.invalid) {
        self.storedKey = storedKey
        self.result = result
    }

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult { result }
    func deactivateLicense() async -> (success: Bool, error: String?) { (true, nil) }
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult { result }
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult { result }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { storedKey }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
