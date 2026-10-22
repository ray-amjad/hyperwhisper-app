//
//  LicenseKeyFormatTests.swift
//  hyperwhisperTests
//
//  Issue #1489: every account-key field runs one loose shape check before it
//  sends anything. It refuses only text that cannot be a key: empty after
//  trimming, longer than 64 characters, any whitespace inside, or anything
//  outside printable ASCII. There is no HW- pattern, so Polar-era keys still
//  activate. The keys below are made up and are not real account keys.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LicenseKeyFormatTests {

    @Test(arguments: [
        "HW-ABCD-EFGH-JKLM-NPQR",
        "HW-ABCD",
        "  HW-ABCD-EFGH-JKLM-NPQR\r\n",
        "HW-ABCD\n",
        "123e4567-e89b-12d3-a456-426614174000",
        "abcdef-lowercase-key",
        String(repeating: "A", count: 64),
    ])
    func acceptsTextThatCouldBeAKey(_ text: String) {
        #expect(LicenseKeyFormat.isPlausible(text))
        #expect(!LicenseKeyFormat.showsHint(text))
    }

    @Test(arguments: [
        String(repeating: "A", count: 65),
        "HW-ABCD EFGH",
        "HW-ABCD\tEFGH",
        "HW-ABCD\nEFGH",
        "مفتاح",
        "HW-ABCD-🔑",
        "HW-ÄBCD",
        "please send me a key",
    ])
    func refusesTextThatCannotBeAKeyAndShowsTheHint(_ text: String) {
        #expect(!LicenseKeyFormat.isPlausible(text))
        #expect(LicenseKeyFormat.showsHint(text))
    }

    @Test(arguments: ["", "   ", "\n", " \r\n\t "])
    func emptyOrWhitespaceOnlyIsRefusedWithoutAHint(_ text: String) {
        #expect(!LicenseKeyFormat.isPlausible(text))
        #expect(!LicenseKeyFormat.showsHint(text))
    }

    @Test func normalizedTrimsLeadingAndTrailingWhitespaceAndNewlines() {
        #expect(LicenseKeyFormat.normalized("  HW-ABCD-EFGH\r\n") == "HW-ABCD-EFGH")
    }
}

/// `LicenseManager` is the defensive guard behind every key field: text that
/// cannot be a key never reaches the network, and a plausible key is sent trimmed.
@MainActor
struct LicenseManagerKeyFormatTests {

    private func makeManager(_ network: RecordingLicenseNetwork) -> LicenseManager {
        LicenseManager(
            networkService: network,
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )
    }

    @Test func activationRefusesTextThatCannotBeAKeyBeforeTheNetwork() async {
        let network = RecordingLicenseNetwork()
        let manager = makeManager(network)

        let result = await manager.activateLicense("this is not a key")

        #expect(result.isValid == false)
        #expect(result.errorMessage == "license.error.keyFormat".localized)
        #expect(manager.lastError == "license.error.keyFormat".localized)
        #expect(network.activatedKeys.isEmpty)
        #expect(manager.isValidating == false)
    }

    @Test func activationSendsTheTrimmedKey() async {
        let network = RecordingLicenseNetwork()
        let manager = makeManager(network)

        _ = await manager.activateLicense("  HW-ABCD-EFGH-JKLM-NPQR\r\n")

        #expect(network.activatedKeys == ["HW-ABCD-EFGH-JKLM-NPQR"])
    }

    @Test func probeRefusesTextThatCannotBeAKeyAndSendsTheTrimmedKeyOtherwise() async {
        let network = RecordingLicenseNetwork()
        let manager = makeManager(network)

        let refused = await manager.probeLicense(String(repeating: "A", count: 65))
        #expect(refused.isValid == false)
        #expect(network.probedKeys.isEmpty)
        #expect(manager.lastError == nil)

        _ = await manager.probeLicense("legacy-polar-style-key\n")
        #expect(network.probedKeys == ["legacy-polar-style-key"])
    }
}

private final class RecordingLicenseNetwork: LicenseNetworkServing {
    var activatedKeys: [String] = []
    var probedKeys: [String] = []

    private let notFound = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: "License key not found"
    )

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        activatedKeys.append(licenseKey)
        return notFound
    }
    func deactivateLicense() async -> (success: Bool, error: String?) { (true, nil) }
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult { notFound }
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult {
        probedKeys.append(licenseKey)
        return notFound
    }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { nil }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
