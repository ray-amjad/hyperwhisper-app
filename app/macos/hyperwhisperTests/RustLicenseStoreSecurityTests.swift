//
//  RustLicenseStoreSecurityTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

private enum FakeCredentialStoreError: Error {
    case writeFailed
}

private final class FakeLicenseCredentialStore: LicenseCredentialStore {
    var storage: [LicenseCredentialDescriptor: Data] = [:]
    var reads: [LicenseCredentialDescriptor] = []
    var writes: [(LicenseCredentialDescriptor, Data)] = []
    var deletions: [LicenseCredentialDescriptor] = []
    var failWrites = false

    func read(item: LicenseCredentialDescriptor) throws -> Data? {
        reads.append(item)
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
}
