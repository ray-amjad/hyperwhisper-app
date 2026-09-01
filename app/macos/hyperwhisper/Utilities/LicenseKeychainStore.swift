//
//  LicenseKeychainStore.swift
//  hyperwhisper
//
//  Secure, serialized storage for the license state. This component remains
//  inactive until every license and backup path switches together.
//

import Foundation
import Security

struct LicenseCredentialDescriptor: Hashable, Sendable {
    enum ItemClass: Hashable, Sendable {
        case genericPassword
    }

    enum Accessibility: Hashable, Sendable {
        case whenUnlockedThisDeviceOnly
    }

    let itemClass: ItemClass
    let service: String
    let account: String
    let accessibility: Accessibility
    let synchronizable: Bool
    let label: String
}

protocol LicenseCredentialStore: AnyObject {
    func read(item: LicenseCredentialDescriptor) throws -> Data?
    func write(_ data: Data, item: LicenseCredentialDescriptor) throws
    func delete(item: LicenseCredentialDescriptor) throws
}

enum LicenseCredentialStoreError: LocalizedError {
    case unhandledStatus(OSStatus)
    case unexpectedData

    var errorDescription: String? {
        switch self {
        case .unhandledStatus(let status):
            let message = SecCopyErrorMessageString(status, nil) as String? ?? "Unknown error"
            return "Keychain error: \(status). \(message)"
        case .unexpectedData:
            return "Keychain returned data in an unexpected format"
        }
    }
}

/// The production credential adapter. It uses update-then-add so an existing
/// item keeps its identity and access controls.
final class SecurityLicenseCredentialStore: LicenseCredentialStore {
    func read(item: LicenseCredentialDescriptor) throws -> Data? {
        var query = baseQuery(for: item)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound {
            return nil
        }
        guard status == errSecSuccess else {
            throw LicenseCredentialStoreError.unhandledStatus(status)
        }
        guard let data = result as? Data else {
            throw LicenseCredentialStoreError.unexpectedData
        }
        return data
    }

    func write(_ data: Data, item: LicenseCredentialDescriptor) throws {
        let query = baseQuery(for: item)
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: securityAccessibility(item.accessibility),
            kSecAttrSynchronizable as String: item.synchronizable,
        ]

        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var addQuery = query
            addQuery[kSecValueData as String] = data
            addQuery[kSecAttrAccessible as String] = securityAccessibility(item.accessibility)
            addQuery[kSecAttrSynchronizable as String] = item.synchronizable
            addQuery[kSecAttrLabel as String] = item.label
            status = SecItemAdd(addQuery as CFDictionary, nil)
        }

        guard status == errSecSuccess else {
            throw LicenseCredentialStoreError.unhandledStatus(status)
        }
    }

    func delete(item: LicenseCredentialDescriptor) throws {
        let status = SecItemDelete(baseQuery(for: item) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw LicenseCredentialStoreError.unhandledStatus(status)
        }
    }

    private func baseQuery(for item: LicenseCredentialDescriptor) -> [String: Any] {
        [
            kSecClass as String: securityClass(item.itemClass),
            kSecAttrService as String: item.service,
            kSecAttrAccount as String: item.account,
            kSecAttrSynchronizable as String: item.synchronizable,
        ]
    }

    private func securityClass(_ itemClass: LicenseCredentialDescriptor.ItemClass) -> CFString {
        switch itemClass {
        case .genericPassword:
            return kSecClassGenericPassword
        }
    }

    private func securityAccessibility(
        _ accessibility: LicenseCredentialDescriptor.Accessibility
    ) -> CFString {
        switch accessibility {
        case .whenUnlockedThisDeviceOnly:
            return kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        }
    }
}

struct LicenseKeychainRecord: Codable, Equatable, Sendable {
    var key: String?
    var customerId: String?
    var lastValidation: String?
    var cachedStatus: String?

    init(
        key: String? = nil,
        customerId: String? = nil,
        lastValidation: String? = nil,
        cachedStatus: String? = nil
    ) {
        self.key = key
        self.customerId = customerId
        self.lastValidation = lastValidation
        self.cachedStatus = cachedStatus
    }

    var isEmpty: Bool {
        key == nil && customerId == nil && lastValidation == nil && cachedStatus == nil
    }
}

/// Owns the versioned license-state item and the secure migration marker.
/// The lock makes compound read/modify/write operations safe for concurrent
/// calls from the Rust `Send + Sync` callback interface.
final class LicenseKeychainStore: @unchecked Sendable {
    static let licenseStateItem = LicenseCredentialDescriptor(
        itemClass: .genericPassword,
        service: "com.hyperwhisper.app.license",
        account: "license-state-v1",
        accessibility: .whenUnlockedThisDeviceOnly,
        synchronizable: false,
        label: "HyperWhisper license state"
    )

    static let migrationMarkerItem = LicenseCredentialDescriptor(
        itemClass: .genericPassword,
        service: "com.hyperwhisper.app.license",
        account: "userdefaults-migration-v1",
        accessibility: .whenUnlockedThisDeviceOnly,
        synchronizable: false,
        label: "HyperWhisper license migration marker"
    )

    private static let migrationMarkerData = Data("complete".utf8)

    private let credentialStore: LicenseCredentialStore
    private let lock = NSLock()
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    init(credentialStore: LicenseCredentialStore = SecurityLicenseCredentialStore()) {
        self.credentialStore = credentialStore
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        self.encoder = encoder
        self.decoder = JSONDecoder()
    }

    func readRecord() throws -> LicenseKeychainRecord? {
        try withLock {
            try readRecordLocked()
        }
    }

    /// Replaces or deletes the full record in one serialized operation.
    func replaceRecord(with record: LicenseKeychainRecord?) throws {
        try mutateRecord { current in
            current = record
        }
    }

    /// Changes the full record while holding the store lock. The previous
    /// Keychain value remains when encoding, update, add, or delete fails.
    func mutateRecord(
        _ mutation: (inout LicenseKeychainRecord?) throws -> Void
    ) throws {
        try withLock {
            let previous = try readRecordLocked()
            var replacement = previous
            try mutation(&replacement)

            guard replacement != previous else { return }
            if let replacement {
                let data = try encoder.encode(replacement)
                try credentialStore.write(data, item: Self.licenseStateItem)
            } else {
                try credentialStore.delete(item: Self.licenseStateItem)
            }
        }
    }

    func hasMigrationMarker() throws -> Bool {
        try withLock {
            try credentialStore.read(item: Self.migrationMarkerItem) != nil
        }
    }

    func writeMigrationMarker() throws {
        try withLock {
            try credentialStore.write(Self.migrationMarkerData, item: Self.migrationMarkerItem)
        }
    }

    func deleteMigrationMarker() throws {
        try withLock {
            try credentialStore.delete(item: Self.migrationMarkerItem)
        }
    }

    private func readRecordLocked() throws -> LicenseKeychainRecord? {
        guard let data = try credentialStore.read(item: Self.licenseStateItem) else {
            return nil
        }
        return try decoder.decode(LicenseKeychainRecord.self, from: data)
    }

    private func withLock<T>(_ operation: () throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try operation()
    }
}
