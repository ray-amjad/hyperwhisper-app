//
//  HealthEndpointCloudKeyPresentTests.swift
//  hyperwhisperTests
//
//  Issues #1492 / #650: Local API `GET /health` hard-coded `key_present: true`
//  for the HyperWhisper Cloud-routed providers (`hyperwhisper`,
//  `microsoftazurespeech`, `googlespeech`, and post-processing `hyperwhisper`),
//  so a profile with no account key read "key present" while `/transcribe`
//  refused every Cloud job with `MISSING_API_KEY`.
//
//  These drive the real `/health` rows (`HealthEndpoint.providerEntries`, the
//  FlyingFox-free part of `handle`) with a real `LicenseManager`, so the rows
//  read the same `getTranscriptionIdentifier().isLicensed` that
//  `HyperWhisperCloudEntitlement.requireLicense` refuses on.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct HealthEndpointCloudKeyPresentTests {

    private static let cloudTranscriptionIds: Set<String> = [
        CloudProvider.hyperwhisper.rawValue,
        CloudProvider.microsoftAzureSpeech.rawValue,
        CloudProvider.googleSpeech.rawValue,
    ]

    private func makeLicenseManager(storedKey: String?, status: LicenseStatus) -> LicenseManager {
        let manager = LicenseManager(
            networkService: StubLicenseNetwork(storedKey: storedKey),
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )
        manager.licenseStatus = status
        return manager
    }

    private func cloudRows(
        _ licenseManager: LicenseManager?
    ) -> (transcription: [HealthProviderStatus], postProcessing: HealthProviderStatus?, localLLM: HealthProviderStatus?) {
        let entries = HealthEndpoint.providerEntries(
            snapshot: nil,
            settingsManager: nil,
            licenseManager: licenseManager
        )
        let transcription = entries.providers.filter { Self.cloudTranscriptionIds.contains($0.id) }
        let post = entries.postProcessing.first { $0.id == PostProcessingProvider.hyperwhisper.rawValue }
        let localLLM = entries.postProcessing.first { $0.id == PostProcessingProvider.localLLM.rawValue }
        return (transcription, post, localLLM)
    }

    @Test func noAccountKeyReportsKeyAbsentForEveryCloudRow() throws {
        let licenseManager = makeLicenseManager(storedKey: nil, status: .trial)
        // The predicate /transcribe refuses on.
        #expect(licenseManager.getTranscriptionIdentifier().isLicensed == false)

        let rows = cloudRows(licenseManager)
        #expect(rows.transcription.count == 3)
        for row in rows.transcription {
            #expect(row.key_present == false, "\(row.id) must not claim a key with no account key stored")
        }
        let post = try #require(rows.postProcessing)
        #expect(post.key_present == false)
        // On-device post-processing holds no credential and stays true.
        #expect(try #require(rows.localLLM).key_present == true)
    }

    @Test func storedAccountKeyReportsKeyPresentForEveryCloudRow() throws {
        let licenseManager = makeLicenseManager(storedKey: "stored-account-key", status: .active)
        #expect(licenseManager.getTranscriptionIdentifier().isLicensed == true)

        let rows = cloudRows(licenseManager)
        #expect(rows.transcription.count == 3)
        for row in rows.transcription {
            #expect(row.key_present == true, "\(row.id) must report the stored account key")
        }
        #expect(try #require(rows.postProcessing).key_present == true)
    }

    /// A stored key whose licence is not active is refused by `/transcribe`
    /// too (`getTranscriptionIdentifier` falls back to the device id).
    @Test func inactiveLicenceWithStoredKeyReportsKeyAbsent() throws {
        let licenseManager = makeLicenseManager(storedKey: "stored-account-key", status: .expired)
        #expect(licenseManager.getTranscriptionIdentifier().isLicensed == false)

        let rows = cloudRows(licenseManager)
        for row in rows.transcription {
            #expect(row.key_present == false)
        }
        #expect(try #require(rows.postProcessing).key_present == false)
    }

    @Test func noLicenseManagerReportsKeyAbsent() throws {
        let rows = cloudRows(nil)
        #expect(rows.transcription.count == 3)
        for row in rows.transcription {
            #expect(row.key_present == false)
        }
        #expect(try #require(rows.postProcessing).key_present == false)
    }
}

/// Answers `getStoredLicenseKey()` with a fixed value; every network call is a
/// no-op refusal, so nothing leaves the process.
private final class StubLicenseNetwork: LicenseNetworkServing {
    private let storedKey: String?
    private let invalid = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: nil
    )

    init(storedKey: String?) {
        self.storedKey = storedKey
    }

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
    func getStoredLicenseKey() -> String? { storedKey }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
