//
//  LicenseProbeTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LicenseProbeTests {
    @Test func managerProbeDoesNotApplyValidResultToAccountState() async {
        let service = LicenseNetworkSpy()
        service.probeResult = LicenseValidationResult(
            isValid: true,
            status: .active,
            customerId: "customer",
            customerEmail: "person@example.com",
            customerName: "Person",
            errorMessage: nil
        )
        let manager = LicenseManager(
            networkService: service,
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )

        let result = await manager.probeLicense("test-key")

        #expect(result.isValid)
        #expect(service.probedKeys == ["test-key"])
        #expect(manager.licenseStatus == .trial)
        #expect(manager.customerEmail == nil)
        #expect(manager.customerName == nil)
        #expect(manager.lastError == nil)
    }

    @Test func probeRequestOmitsDeviceTrackingFields() throws {
        let data = try #require(
            LicenseNetworkService.makeProbeRequestBody(licenseKey: "  test-key  ")
        )
        let object = try #require(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )

        #expect(object["license_key"] as? String == "  test-key  ")
        #expect(object["probe_only"] as? Bool == true)
        #expect(object["device_id"] == nil)
        #expect(object["device_name"] == nil)
    }

    @Test func probeModeDisablesEveryPersistentBehavior() {
        #expect(!LicenseNetworkService.ValidationMode.probe.includesDeviceTracking)
        #expect(!LicenseNetworkService.ValidationMode.probe.persistsResult)
        #expect(!LicenseNetworkService.ValidationMode.probe.allowsOfflineFallback)
    }
}

private final class LicenseNetworkSpy: LicenseNetworkServing {
    var probeResult = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: nil
    )
    var probedKeys: [String] = []

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        probeResult
    }

    func deactivateLicense() async -> (success: Bool, error: String?) {
        (true, nil)
    }

    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult {
        probeResult
    }

    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult {
        probedKeys.append(licenseKey)
        return probeResult
    }

    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { nil }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
