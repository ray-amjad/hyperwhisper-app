//
//  RustLicenseStoreSecurityTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

private enum FakeCredentialStoreError: Error {
    case readFailed
    case writeFailed
    case deleteFailed
}

private final class FakeLicenseCredentialStore: LicenseCredentialStore {
    var storage: [LicenseCredentialDescriptor: Data] = [:]
    var reads: [LicenseCredentialDescriptor] = []
    var writes: [(LicenseCredentialDescriptor, Data)] = []
    var deletions: [LicenseCredentialDescriptor] = []
    var failWrites = false
    var failDeletes = false
    var failReads = false

    func read(item: LicenseCredentialDescriptor) throws -> Data? {
        reads.append(item)
        if failReads {
            throw FakeCredentialStoreError.readFailed
        }
        return storage[item]
    }

    func write(_ data: Data, item: LicenseCredentialDescriptor) throws {
        if failWrites {
            throw FakeCredentialStoreError.writeFailed
        }
        writes.append((item, data))
        storage[item] = data
    }

    func delete(item: LicenseCredentialDescriptor) throws {
        if failDeletes {
            throw FakeCredentialStoreError.deleteFailed
        }
        deletions.append(item)
        storage.removeValue(forKey: item)
    }
}

struct RustLicenseStoreSecurityTests {
    @Test func recordEncodingReadUpdateAndDeletion() throws {
        let credentials = FakeLicenseCredentialStore()
        let store = LicenseKeychainStore(credentialStore: credentials)
        let initial = LicenseKeychainRecord(
            key: "license-key",
            customerId: "customer",
            lastValidation: "12345",
            cachedStatus: "Active"
        )

        try store.replaceRecord(with: initial)

        let data = try #require(credentials.storage[LicenseKeychainStore.licenseStateItem])
        #expect(
            String(decoding: data, as: UTF8.self)
                == #"{"cachedStatus":"Active","customerId":"customer","key":"license-key","lastValidation":"12345"}"#
        )
        #expect(try store.readRecord() == initial)

        try store.mutateRecord { record in
            record?.cachedStatus = "Expired"
        }
        #expect(try store.readRecord()?.cachedStatus == "Expired")
        #expect(credentials.writes.count == 2)

        try store.replaceRecord(with: nil)
        #expect(try store.readRecord() == nil)
        #expect(credentials.deletions == [LicenseKeychainStore.licenseStateItem])
    }

    @Test func recordAndMarkerUseRequiredKeychainAttributes() {
        for item in [LicenseKeychainStore.licenseStateItem, LicenseKeychainStore.migrationMarkerItem] {
            #expect(item.itemClass == .genericPassword)
            #expect(item.service == "com.hyperwhisper.app.license")
            #expect(item.accessibility == .whenUnlockedThisDeviceOnly)
            #expect(item.synchronizable == false)
            #expect(item.usesDataProtectionKeychain)
        }
        #expect(LicenseKeychainStore.licenseStateItem.account == "license-state-v1")
        #expect(LicenseKeychainStore.migrationMarkerItem.account == "userdefaults-migration-v1")
    }

    @Test func migrationMarkerStorageIsIndependent() throws {
        let credentials = FakeLicenseCredentialStore()
        let store = LicenseKeychainStore(credentialStore: credentials)

        #expect(try !store.hasMigrationMarker())
        try store.writeMigrationMarker()
        #expect(try store.hasMigrationMarker())
        #expect(credentials.storage[LicenseKeychainStore.licenseStateItem] == nil)

        try store.deleteMigrationMarker()
        #expect(try !store.hasMigrationMarker())
        #expect(credentials.deletions == [LicenseKeychainStore.migrationMarkerItem])
    }

    @Test func concurrentMutationsAreSerialized() throws {
        let credentials = FakeLicenseCredentialStore()
        let store = LicenseKeychainStore(credentialStore: credentials)
        try store.replaceRecord(with: LicenseKeychainRecord(customerId: "0"))

        DispatchQueue.concurrentPerform(iterations: 100) { _ in
            try! store.mutateRecord { record in
                let current = Int(record?.customerId ?? "0") ?? 0
                record?.customerId = String(current + 1)
            }
        }

        #expect(try store.readRecord()?.customerId == "100")
    }

    @Test func failedAtomicReplacementKeepsPriorRecordAndThrows() throws {
        let credentials = FakeLicenseCredentialStore()
        let store = LicenseKeychainStore(credentialStore: credentials)
        let prior = LicenseKeychainRecord(
            key: "prior-key",
            customerId: "prior-customer",
            lastValidation: "100",
            cachedStatus: "Active"
        )
        try store.replaceRecord(with: prior)
        credentials.failWrites = true

        var visibleError: Error?
        do {
            try store.replaceRecord(with: LicenseKeychainRecord(key: "replacement-key"))
        } catch {
            visibleError = error
        }

        #expect(visibleError is FakeCredentialStoreError)
        #expect(try store.readRecord() == prior)
    }

    @Test func explicitReplacementAndDeletionRecoverMalformedRecord() throws {
        let credentials = FakeLicenseCredentialStore()
        let store = LicenseKeychainStore(credentialStore: credentials)
        credentials.storage[LicenseKeychainStore.licenseStateItem] = Data("future-format".utf8)

        #expect(throws: DecodingError.self) {
            _ = try store.readRecord()
        }

        try store.replaceRecord(with: LicenseKeychainRecord(key: "replacement"))
        #expect(try store.readRecord()?.key == "replacement")

        credentials.storage[LicenseKeychainStore.licenseStateItem] = Data("malformed".utf8)
        try store.replaceRecord(with: nil)
        #expect(try store.readRecord() == nil)
    }

    @Test func explicitServiceClearRecoversMalformedRecord() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.writeMigrationMarker()
        credentials.storage[LicenseKeychainStore.licenseStateItem] = Data("future-format".utf8)
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)

        #expect(LicenseNetworkService(store: store).clearStoredLicense())
        #expect(try keychain.readRecord() == nil)
    }

    @Test func successfulRecordReadIsCachedAcrossCoreFieldAccesses() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.writeMigrationMarker()
        try keychain.replaceRecord(with: LicenseKeychainRecord(
            key: "key",
            customerId: "customer",
            lastValidation: "100",
            cachedStatus: "Active"
        ))
        credentials.reads.removeAll()
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        credentials.reads.removeAll()

        #expect(store.get(key: RustLicenseStore.kLicenseKey) == "key")
        #expect(store.get(key: RustLicenseStore.kLicenseCustomerId) == "customer")
        #expect(store.get(key: RustLicenseStore.kLicenseLastValidation) == "100")
        #expect(store.get(key: RustLicenseStore.kLicenseCachedStatus) == "Active")
        #expect(credentials.reads == [LicenseKeychainStore.licenseStateItem])
    }

    @Test func failedReadIsDistinctAndCanRecoverInSession() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.writeMigrationMarker()
        try keychain.replaceRecord(with: LicenseKeychainRecord(key: "secure-key"))
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)

        credentials.failReads = true
        #expect(store.readStoredLicenseKey() == .unavailable)
        credentials.failReads = false
        #expect(store.readStoredLicenseKey(retryAfterFailure: true) == .present("secure-key"))
    }

    @Test func migratesOnlyTrimmedLegacyKeyAndDeletesAllPlaintextFields() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        defaults.set("  legacy-key  ", forKey: RustLicenseStore.kLicenseKey)
        defaults.set("forged-customer", forKey: RustLicenseStore.kLicenseCustomerId)
        defaults.set("999999", forKey: RustLicenseStore.kLicenseLastValidation)
        defaults.set("Active", forKey: RustLicenseStore.kLicenseCachedStatus)

        let store = RustLicenseStore(
            defaults: defaults,
            licenseStore: keychain,
            seedUsage: false
        )

        #expect(store.get(key: RustLicenseStore.kLicenseKey) == "legacy-key")
        #expect(store.get(key: RustLicenseStore.kLicenseCustomerId) == nil)
        #expect(store.get(key: RustLicenseStore.kLicenseLastValidation) == nil)
        #expect(store.get(key: RustLicenseStore.kLicenseCachedStatus) == nil)
        #expect(try keychain.readRecord() == LicenseKeychainRecord(key: "legacy-key"))
        #expect(try keychain.hasMigrationMarker())
        #expect(defaults.object(forKey: RustLicenseStore.kLicenseKey) == nil)
        #expect(defaults.object(forKey: RustLicenseStore.kLicenseCustomerId) == nil)
        #expect(defaults.object(forKey: RustLicenseStore.kLicenseLastValidation) == nil)
        #expect(defaults.object(forKey: RustLicenseStore.kLicenseCachedStatus) == nil)
    }

    @Test func forgedLegacyVerdictCannotCreateCachedEntitlement() throws {
        let defaults = makeDefaults()
        let keychain = LicenseKeychainStore(credentialStore: FakeLicenseCredentialStore())
        defaults.set("Active", forKey: RustLicenseStore.kLicenseCachedStatus)
        defaults.set("9999999999", forKey: RustLicenseStore.kLicenseLastValidation)
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)

        #expect(licenseCachedStatusWithinGrace(store: store, nowUnixSecs: 1) == nil)
        #expect(licenseShouldRevalidate(store: store, nowUnixSecs: 1))
    }

    @Test func secureRecordTakesPrecedenceAndMarkerBlocksLaterDefaults() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.replaceRecord(with: LicenseKeychainRecord(key: "secure-key"))
        defaults.set("forged-key", forKey: RustLicenseStore.kLicenseKey)

        let first = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        #expect(first.get(key: RustLicenseStore.kLicenseKey) == "secure-key")
        #expect(try keychain.hasMigrationMarker())

        try keychain.replaceRecord(with: nil)
        defaults.set("later-forged-key", forKey: RustLicenseStore.kLicenseKey)
        let second = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        #expect(second.get(key: RustLicenseStore.kLicenseKey) == nil)
        #expect(defaults.object(forKey: RustLicenseStore.kLicenseKey) == nil)
    }

    @Test func failedMigrationKeepsLegacyKeyAndRetriesWithoutPlaintextFallback() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        defaults.set("legacy-key", forKey: RustLicenseStore.kLicenseKey)
        credentials.failWrites = true

        let failed = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        #expect(failed.get(key: RustLicenseStore.kLicenseKey) == nil)
        #expect(defaults.string(forKey: RustLicenseStore.kLicenseKey) == "legacy-key")

        credentials.failWrites = false
        let retried = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        #expect(retried.get(key: RustLicenseStore.kLicenseKey) == "legacy-key")
        #expect(defaults.object(forKey: RustLicenseStore.kLicenseKey) == nil)
    }

    @MainActor
    @Test func rustVerdictCommitsAtomicallyAndClearPreservesNonLicenseState() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.writeMigrationMarker()
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        defaults.set("42", forKey: "com.hyperwhisper.usage.dailySeconds")
        defaults.set("3600", forKey: "com.hyperwhisper.config.trialDailyLimitSeconds")

        let persisted = LicenseNetworkService.persistValidationVerdictIfCurrent(
            store: store,
            status: .active,
            attemptedKey: "secure-key",
            expectedStoredLicenseKey: nil,
            nowUnixSecs: 123,
            isCancelled: false
        )

        #expect(persisted == .persisted)
        #expect(
            try keychain.readRecord()
                == LicenseKeychainRecord(
                    key: "secure-key",
                    customerId: nil,
                    lastValidation: "123",
                    cachedStatus: "Active"
                )
        )

        let service = LicenseNetworkService(store: store)
        #expect(service.clearStoredLicense())
        #expect(try keychain.readRecord() == nil)
        #expect(defaults.string(forKey: "com.hyperwhisper.usage.dailySeconds") == "42")
        #expect(defaults.string(forKey: "com.hyperwhisper.config.trialDailyLimitSeconds") == "3600")
    }

    @MainActor
    @Test func failedVerdictCommitAndFailedClearKeepPriorRecord() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.writeMigrationMarker()
        try keychain.replaceRecord(with: LicenseKeychainRecord(
            key: "prior-key",
            lastValidation: "100",
            cachedStatus: "Active"
        ))
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        credentials.failWrites = true

        let result = LicenseNetworkService.persistValidationVerdictIfCurrent(
            store: store,
            status: .active,
            attemptedKey: "replacement-key",
            expectedStoredLicenseKey: nil,
            nowUnixSecs: 200,
            isCancelled: false
        )
        #expect(result == .storageFailed)
        #expect(try keychain.readRecord()?.key == "prior-key")

        credentials.failWrites = false
        credentials.failDeletes = true
        let service = LicenseNetworkService(store: store)
        #expect(!service.clearStoredLicense())
        #expect(try keychain.readRecord()?.key == "prior-key")
    }

    @Test func importedReplacementClearsOldKeyBoundVerdictBeforeValidation() throws {
        let defaults = makeDefaults()
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.writeMigrationMarker()
        try keychain.replaceRecord(with: LicenseKeychainRecord(
            key: "old-key",
            customerId: "old-customer",
            lastValidation: "100",
            cachedStatus: "Active"
        ))
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)

        #expect(store.replaceLicenseKeyForImport(" new-key "))
        #expect(try keychain.readRecord() == LicenseKeychainRecord(key: "new-key"))

        credentials.failWrites = true
        #expect(!store.replaceLicenseKeyForImport("another-key"))
        #expect(try keychain.readRecord() == LicenseKeychainRecord(key: "new-key"))
    }

    private func makeDefaults() -> UserDefaults {
        let suiteName = "RustLicenseStoreSecurityTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        return defaults
    }
}

@MainActor
@Suite(.serialized)
struct BackupLicenseStorageTests {
    @Test func partialImportFailurePreservesEarlierSectionResults() {
        let result = ImportResult.partialFailure(
            "license failed",
            modesImported: 2,
            modesSkipped: 1,
            vocabularyImported: 3,
            vocabularySkipped: 4,
            apiKeysImported: true
        )

        #expect(!result.success)
        #expect(result.partialSuccess)
        #expect(result.modesImported == 2)
        #expect(result.modesSkipped == 1)
        #expect(result.vocabularyImported == 3)
        #expect(result.vocabularySkipped == 4)
        #expect(result.apiKeysImported)
        #expect(!result.licenseKeyImported)
    }

    @Test func exportReadsSharedSecureStoreInsteadOfForgedDefaults() throws {
        let suiteName = "BackupLicenseStorageTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.set("forged-key", forKey: RustLicenseStore.kLicenseKey)
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.replaceRecord(with: LicenseKeychainRecord(key: "secure-key"))
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        let manager = LicenseManager(
            networkService: LicenseNetworkService(store: store),
            loadStoredLicenseOnInit: false
        )
        BackupManager.shared.licenseManager = manager

        #expect(BackupManager.shared.licenseKeyForExport() == "secure-key")
    }

    @Test func exportReportsSecureReadFailureInsteadOfOmittingSelectedLicense() throws {
        let suiteName = "BackupLicenseStorageTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        let credentials = FakeLicenseCredentialStore()
        let keychain = LicenseKeychainStore(credentialStore: credentials)
        try keychain.writeMigrationMarker()
        let store = RustLicenseStore(defaults: defaults, licenseStore: keychain, seedUsage: false)
        credentials.failReads = true
        let manager = LicenseManager(
            networkService: LicenseNetworkService(store: store),
            loadStoredLicenseOnInit: false
        )
        BackupManager.shared.licenseManager = manager
        BackupManager.shared.lastError = nil

        #expect(BackupManager.shared.licenseKeyForExport() == nil)
        #expect(BackupManager.shared.lastError == "Failed to securely read the license key")
    }

    @Test func importRequestsRevalidationOnlyAfterSecureReplacement() async {
        let service = BackupLicenseNetworkSpy()
        let manager = LicenseManager(networkService: service, loadStoredLicenseOnInit: false)
        BackupManager.shared.licenseManager = manager

        #expect(BackupManager.shared.importLicenseKeySecurely("replacement-key"))
        for _ in 0..<10 where service.actions.count < 2 {
            await Task.yield()
        }

        #expect(service.actions == ["replace", "validate"])
        #expect(service.expectedKeys == ["replacement-key"])
        #expect(manager.licenseStatus == .active)
    }

    @Test func importPublishesNonActiveStateBeforeValidationStarts() {
        let service = BackupLicenseNetworkSpy()
        let manager = LicenseManager(networkService: service, loadStoredLicenseOnInit: false)
        manager.licenseStatus = .active
        BackupManager.shared.licenseManager = manager

        #expect(BackupManager.shared.importLicenseKeySecurely("replacement-key"))
        #expect(manager.licenseStatus == .trial)
    }

    @Test func lateImportedValidationCannotOverrideNewerImportState() async {
        let service = ControlledBackupLicenseNetworkSpy()
        let manager = LicenseManager(networkService: service, loadStoredLicenseOnInit: false)
        BackupManager.shared.licenseManager = manager

        #expect(BackupManager.shared.importLicenseKeySecurely("older-key"))
        await service.waitForValidationCount(1)
        #expect(BackupManager.shared.importLicenseKeySecurely("newer-key"))
        await service.waitForValidationCount(2)

        service.completeValidation(for: "newer-key", status: .active)
        await Task.yield()
        #expect(manager.licenseStatus == .active)

        service.completeValidation(for: "older-key", status: .invalid)
        await Task.yield()
        #expect(manager.licenseStatus == .active)
        #expect(service.expectedKeys == ["older-key", "newer-key"])
    }

    @Test func lateImportedValidationCannotOverrideDeactivation() async {
        let service = ControlledBackupLicenseNetworkSpy()
        let manager = LicenseManager(networkService: service, loadStoredLicenseOnInit: false)
        BackupManager.shared.licenseManager = manager

        #expect(BackupManager.shared.importLicenseKeySecurely("imported-key"))
        await service.waitForValidationCount(1)
        #expect(await manager.deactivateLicense())
        service.completeValidation(for: "imported-key", status: .active)
        await Task.yield()

        #expect(manager.licenseStatus == .trial)
        #expect(service.getStoredLicenseKey() == nil)
    }

    @Test func failedSecureReplacementSkipsValidation() {
        let service = BackupLicenseNetworkSpy()
        service.replaceSucceeds = false
        let manager = LicenseManager(networkService: service, loadStoredLicenseOnInit: false)
        BackupManager.shared.licenseManager = manager

        #expect(!BackupManager.shared.importLicenseKeySecurely("replacement-key"))
        #expect(service.actions == ["replace"])
    }

    @Test func failedClearDoesNotPublishTrialState() {
        let service = BackupLicenseNetworkSpy()
        service.clearSucceeds = false
        let manager = LicenseManager(networkService: service, loadStoredLicenseOnInit: false)
        manager.licenseStatus = .active

        #expect(!manager.clearLicense())
        #expect(manager.licenseStatus == .active)
        #expect(manager.lastError != nil)
    }
}

private final class BackupLicenseNetworkSpy: LicenseNetworkServing {
    var actions: [String] = []
    var replaceSucceeds = true
    var clearSucceeds = true
    var expectedKeys: [String?] = []
    private var storedKey: String?

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        validationResult
    }

    func deactivateLicense() async -> (success: Bool, error: String?) { (true, nil) }

    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult {
        actions.append("validate")
        expectedKeys.append(expectedStoredLicenseKey)
        return validationResult
    }

    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult { validationResult }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { storedKey }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead {
        storedKey.map(RustLicenseStore.StoredLicenseKeyRead.present) ?? .missing
    }
    func clearStoredLicense() -> Bool { clearSucceeds }

    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool {
        actions.append("replace")
        guard replaceSucceeds else { return false }
        storedKey = licenseKey
        return true
    }

    private var validationResult: LicenseValidationResult {
        LicenseValidationResult(
            isValid: true,
            status: .active,
            customerId: nil,
            customerEmail: nil,
            customerName: nil,
            errorMessage: nil
        )
    }
}

@MainActor
private final class ControlledBackupLicenseNetworkSpy: LicenseNetworkServing {
    private struct PendingValidation {
        let key: String
        let continuation: CheckedContinuation<LicenseValidationResult, Never>
    }

    private var storedKey: String?
    private var pending: [PendingValidation] = []
    var expectedKeys: [String?] = []

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        result(status: .active)
    }

    func deactivateLicense() async -> (success: Bool, error: String?) {
        storedKey = nil
        return (true, nil)
    }

    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult {
        expectedKeys.append(expectedStoredLicenseKey)
        return await withCheckedContinuation { continuation in
            pending.append(PendingValidation(key: licenseKey, continuation: continuation))
        }
    }

    func waitForValidationCount(_ count: Int) async {
        for _ in 0..<20 where pending.count < count {
            await Task.yield()
        }
    }

    func completeValidation(for key: String, status: LicenseStatus) {
        guard let index = pending.firstIndex(where: { $0.key == key }) else { return }
        let validation = pending.remove(at: index)
        validation.continuation.resume(returning: result(status: status))
    }

    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult {
        result(status: .active)
    }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { storedKey }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead {
        storedKey.map(RustLicenseStore.StoredLicenseKeyRead.present) ?? .missing
    }
    func clearStoredLicense() -> Bool {
        storedKey = nil
        return true
    }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool {
        storedKey = licenseKey
        return true
    }

    private func result(status: LicenseStatus) -> LicenseValidationResult {
        LicenseValidationResult(
            isValid: status == .active,
            status: status,
            customerId: nil,
            customerEmail: nil,
            customerName: nil,
            errorMessage: status == .active ? nil : "invalid"
        )
    }
}
