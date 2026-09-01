//
//  RustLicenseStore.swift
//  hyperwhisper
//
//  RUST SHARED-CORE KEY-VALUE STORE (M3-C)
//  Backs the `hw-license` Rust core's persistence. The core is pure (no clock,
//  no storage): it reads/writes license, remote-config, and usage state through
//  this `KeyValueStore`, and takes `now_unix_secs` at every time-dependent call.
//
//  BACKWARD-COMPATIBILITY (the #1 migration risk):
//  macOS persistence is split across secure license state, UserDefaults config
//  and usage state, and a one-time Core Data usage seed:
//
//  1. license.* keys → one versioned Keychain record. Legacy UserDefaults are
//     migrated once, but only the key is trusted; cached verdict fields are not.
//
//  2. config.*  (remote trial-limit override) → ConfigService stored these as
//     `config.trialDailyLimitSeconds` / `config.trialModelDownloadLimit` /
//     `config.lastFetchTimestamp` (NO `com.hyperwhisper.` prefix, and as Int /
//     Double, not String). The core asks for `com.hyperwhisper.config.*` as
//     Strings. We translate via `configKeyAliases` + numeric→String coercion in
//     `get`, so the legacy cache is read correctly. Once the core writes a fresh
//     override (`licenseStoreRemoteOverride`), the prefixed String keys self-heal.
//
//  3. usage.*  (dailySeconds / dayIndex / lifetime modelsDownloaded) → lived in
//     Core Data (UsageTracking entity), NOT UserDefaults. A ONE-SHOT seed in
//     `init` copies the Core Data values into UserDefaults under the core's
//     `com.hyperwhisper.usage.*` keys, guarded by `didSeedUsageToKeyValueStoreV1`
//     so it runs exactly once, BEFORE any `license_*` usage call. The lifetime
//     model count is irreversible if lost, so it is seeded unconditionally.
//
//  The Core Data UsageTracking entity + PersistenceController methods are kept
//  (dormant) — only read once for the seed; not deleted in this change.
//

import Foundation

/// Keychain/UserDefaults-backed `KeyValueStore` for the Rust license/usage core.
///
/// Class-only (`AnyObject`) to satisfy the binding's `KeyValueStore` protocol
/// (a UniFFI callback interface). A single shared instance is held by
/// `LicenseManager` and passed to every `license_*` call.
final class RustLicenseStore: KeyValueStore {

    enum StoredLicenseKeyRead: Equatable {
        case present(String)
        case missing
        case unavailable
    }

    // MARK: - Backing store

    private let defaults: UserDefaults
    private let licenseStore: LicenseKeychainStore
    private let transactionLock = NSRecursiveLock()
    private var transactionRecord: LicenseKeychainRecord?
    private var isLicenseTransactionActive = false
    private var cachedLicenseRecord: LicenseKeychainRecord?
    private var licenseRecordCacheState: LicenseRecordCacheState = .unloaded

    private enum LicenseRecordCacheState {
        case unloaded
        case available
        case unavailable
    }

    /// Guards the one-shot Core Data → UserDefaults usage seed.
    private static let seedFlagKey = "didSeedUsageToKeyValueStoreV1"

    // MARK: - Core license keys (must match hw-license exactly)

    static let kLicenseKey = "com.hyperwhisper.license.key"
    static let kLicenseCustomerId = "com.hyperwhisper.license.customerId"
    static let kLicenseLastValidation = "com.hyperwhisper.license.lastValidation"
    static let kLicenseCachedStatus = "com.hyperwhisper.license.cachedStatus"

    private static let legacyLicenseKeys = [
        kLicenseKey,
        kLicenseCustomerId,
        kLicenseLastValidation,
        kLicenseCachedStatus,
    ]

    // MARK: - Core usage keys (must match hw-license/src/usage.rs exactly)

    private static let kUsageDailySeconds = "com.hyperwhisper.usage.dailySeconds"
    private static let kUsageDayIndex = "com.hyperwhisper.usage.dayIndex"
    private static let kUsageModelsDownloaded = "com.hyperwhisper.usage.modelsDownloaded"

    /// Map of the core's prefixed remote-config keys → the legacy ConfigService
    /// UserDefaults keys (which lack the `com.hyperwhisper.` prefix). Only the
    /// READ path is aliased: the core writes the prefixed keys directly, which
    /// self-heals the cache on the next override fetch. The legacy `config.maxAge`
    /// key is intentionally not mapped — the core uses a fixed 24h TTL, so it
    /// never reads max-age.
    ///
    /// Source of truth for the legacy names: `Services/ConfigService.swift`.
    private static let configKeyAliases: [String: String] = [
        "com.hyperwhisper.config.trialDailyLimitSeconds": "config.trialDailyLimitSeconds",
        "com.hyperwhisper.config.trialModelDownloadLimit": "config.trialModelDownloadLimit",
        "com.hyperwhisper.config.lastFetchTimestamp": "config.lastFetchTimestamp",
    ]

    // MARK: - Init + one-shot seed

    init(
        defaults: UserDefaults = .standard,
        licenseStore: LicenseKeychainStore = LicenseKeychainStore(),
        seedUsage: Bool = true
    ) {
        self.defaults = defaults
        self.licenseStore = licenseStore
        migrateLegacyLicenseIfNeeded()
        if seedUsage {
            seedUsageFromCoreDataIfNeeded()
        }
    }

    /// Migrates only the legacy key. UserDefaults verdicts are forgeable and
    /// must never become authenticated cache state in the Keychain record.
    private func migrateLegacyLicenseIfNeeded() {
        transactionLock.lock()
        defer { transactionLock.unlock() }

        do {
            if try licenseStore.hasMigrationMarker() {
                removeLegacyLicenseDefaults()
                return
            }

            if try licenseStore.readRecord() == nil {
                let legacyKey = defaults.string(forKey: Self.kLicenseKey)?
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                if let legacyKey, !legacyKey.isEmpty {
                    try licenseStore.replaceRecord(with: LicenseKeychainRecord(key: legacyKey))
                    guard try licenseStore.readRecord()?.key == legacyKey else {
                        AppLogger.network.error("License Keychain migration read-back failed")
                        return
                    }
                }
            }

            // A record already present always wins over forgeable defaults.
            // Write the secure marker before removing plaintext preferences.
            try licenseStore.writeMigrationMarker()
            removeLegacyLicenseDefaults()
        } catch {
            // Keep legacy data so a later launch can retry, but get() never
            // falls back to it. Do not include any credential value in the log.
            AppLogger.network.error(
                "License Keychain migration failed: \(error.localizedDescription, privacy: .public)"
            )
        }
    }

    private func removeLegacyLicenseDefaults() {
        for key in Self.legacyLicenseKeys {
            defaults.removeObject(forKey: key)
        }
    }

    /// One-shot migration: copy the Core Data usage counters into UserDefaults
    /// under the core's keys. Idempotent — guarded by `seedFlagKey`. MUST run
    /// before any `license_*` usage call (it runs in `init`, and the shared
    /// store is created before usage is queried).
    private func seedUsageFromCoreDataIfNeeded() {
        guard !defaults.bool(forKey: Self.seedFlagKey) else { return }

        let persistence = PersistenceController.shared

        // Lifetime, irreversible count — seed UNCONDITIONALLY. If we lost this,
        // every existing user would be re-granted their free model downloads.
        let modelsDownloaded = Int(persistence.getModelDownloadCount())
        defaults.set(String(modelsDownloaded), forKey: Self.kUsageModelsDownloaded)

        // Today's daily seconds. `getDailyUsage()` self-resets in Core Data when
        // its own `lastResetDate` is not today (local calendar), so it returns
        // today's seconds (or 0). We seed both the seconds and the matching day
        // index so the core treats it as "already counted today" rather than
        // resetting on first read.
        let dailySeconds = Int(persistence.getDailyUsage())
        defaults.set(String(dailySeconds), forKey: Self.kUsageDailySeconds)
        defaults.set(String(RustLicenseTime.localDayIndex()), forKey: Self.kUsageDayIndex)

        defaults.set(true, forKey: Self.seedFlagKey)
        AppLogger.coreData.info(
            "Seeded usage to KeyValueStore: dailySeconds=\(dailySeconds, privacy: .public), models=\(modelsDownloaded, privacy: .public)"
        )
    }

    // MARK: - KeyValueStore conformance

    func get(key: String) -> String? {
        if let field = licenseField(for: key) {
            return withTransactionLock {
                if isLicenseTransactionActive {
                    return field.value(in: transactionRecord)
                }
                guard loadLicenseRecordIfNeeded() else { return nil }
                return field.value(in: cachedLicenseRecord)
            }
        }

        // Resolve a config-key alias to the legacy un-prefixed name only when the
        // prefixed key has not yet been written by the core. Prefer a freshly
        // written prefixed value so self-healing takes effect.
        if let legacyKey = Self.configKeyAliases[key] {
            if let prefixed = coerceToString(defaults.object(forKey: key)) {
                return prefixed
            }
            return coerceToString(defaults.object(forKey: legacyKey))
        }
        return coerceToString(defaults.object(forKey: key))
    }

    func set(key: String, value: String) {
        if let field = licenseField(for: key) {
            updateLicenseField(field, value: value)
            return
        }
        defaults.set(value, forKey: key)
    }

    func delete(key: String) {
        if let field = licenseField(for: key) {
            updateLicenseField(field, value: nil)
            return
        }
        defaults.removeObject(forKey: key)
    }

    // MARK: - Secure license transactions

    /// Stages all Rust license callbacks and commits one full Keychain record.
    /// A failed commit keeps the prior record and returns false.
    @discardableResult
    func performLicenseTransaction(_ operation: () -> Void) -> Bool {
        withTransactionLock {
            guard !isLicenseTransactionActive else {
                operation()
                return true
            }

            guard loadLicenseRecordIfNeeded() else { return false }
            transactionRecord = cachedLicenseRecord

            isLicenseTransactionActive = true
            operation()
            isLicenseTransactionActive = false
            if transactionRecord?.isEmpty == true {
                transactionRecord = nil
            }
            defer { transactionRecord = nil }

            do {
                try licenseStore.replaceRecord(with: transactionRecord)
                cachedLicenseRecord = transactionRecord
                licenseRecordCacheState = .available
                return true
            } catch {
                AppLogger.network.error(
                    "License Keychain transaction commit failed: \(error.localizedDescription, privacy: .public)"
                )
                return false
            }
        }
    }

    /// Replaces an imported key and clears all key-bound verdict data before
    /// the caller starts real server validation.
    @discardableResult
    func replaceLicenseKeyForImport(_ key: String) -> Bool {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return false }

        return withTransactionLock {
            do {
                let replacement = LicenseKeychainRecord(key: trimmed)
                try licenseStore.replaceRecord(with: replacement)
                cachedLicenseRecord = replacement
                licenseRecordCacheState = .available
                return true
            } catch {
                AppLogger.network.error(
                    "Imported license Keychain write failed: \(error.localizedDescription, privacy: .public)"
                )
                return false
            }
        }
    }

    /// Deletes the complete secure record without decoding it first. Explicit
    /// deactivation must remain possible when stored data is malformed or from
    /// a newer record format.
    @discardableResult
    func clearLicenseRecord() -> Bool {
        withTransactionLock {
            do {
                try licenseStore.replaceRecord(with: nil)
                cachedLicenseRecord = nil
                licenseRecordCacheState = .available
                return true
            } catch {
                AppLogger.network.error(
                    "License Keychain delete failed: \(error.localizedDescription, privacy: .public)"
                )
                return false
            }
        }
    }

    private func updateLicenseField(_ field: LicenseField, value: String?) {
        withTransactionLock {
            if isLicenseTransactionActive {
                field.set(value, in: &transactionRecord)
                return
            }

            guard loadLicenseRecordIfNeeded() else { return }
            var replacement = cachedLicenseRecord
            field.set(value, in: &replacement)
            if replacement?.isEmpty == true {
                replacement = nil
            }

            do {
                try licenseStore.replaceRecord(with: replacement)
                cachedLicenseRecord = replacement
                licenseRecordCacheState = .available
            } catch {
                AppLogger.network.error(
                    "License Keychain update failed: \(error.localizedDescription, privacy: .public)"
                )
            }
        }
    }

    /// Returns a secure key read without collapsing a Keychain error into
    /// ordinary missing state. A caller can request a fresh read after an
    /// earlier failure, while successful reads stay cached for the session.
    func readStoredLicenseKey(retryAfterFailure: Bool = false) -> StoredLicenseKeyRead {
        withTransactionLock {
            if retryAfterFailure, licenseRecordCacheState == .unavailable {
                licenseRecordCacheState = .unloaded
            }
            guard loadLicenseRecordIfNeeded() else { return .unavailable }
            guard let key = cachedLicenseRecord?.key?
                .trimmingCharacters(in: .whitespacesAndNewlines),
                  !key.isEmpty else {
                return .missing
            }
            return .present(key)
        }
    }

    private func loadLicenseRecordIfNeeded() -> Bool {
        switch licenseRecordCacheState {
        case .available:
            return true
        case .unavailable:
            return false
        case .unloaded:
            do {
                cachedLicenseRecord = try licenseStore.readRecord()
                licenseRecordCacheState = .available
                return true
            } catch {
                licenseRecordCacheState = .unavailable
                AppLogger.network.error(
                    "License Keychain read failed: \(error.localizedDescription, privacy: .public)"
                )
                return false
            }
        }
    }

    private enum LicenseField {
        case key
        case customerId
        case lastValidation
        case cachedStatus

        func value(in record: LicenseKeychainRecord?) -> String? {
            switch self {
            case .key: return record?.key
            case .customerId: return record?.customerId
            case .lastValidation: return record?.lastValidation
            case .cachedStatus: return record?.cachedStatus
            }
        }

        func set(_ value: String?, in record: inout LicenseKeychainRecord?) {
            if record == nil, value != nil {
                record = LicenseKeychainRecord()
            }
            switch self {
            case .key: record?.key = value
            case .customerId: record?.customerId = value
            case .lastValidation: record?.lastValidation = value
            case .cachedStatus: record?.cachedStatus = value
            }
        }
    }

    private func licenseField(for key: String) -> LicenseField? {
        switch key {
        case Self.kLicenseKey: return .key
        case Self.kLicenseCustomerId: return .customerId
        case Self.kLicenseLastValidation: return .lastValidation
        case Self.kLicenseCachedStatus: return .cachedStatus
        default: return nil
        }
    }

    private func withTransactionLock<T>(_ operation: () -> T) -> T {
        transactionLock.lock()
        defer { transactionLock.unlock() }
        return operation()
    }

    // MARK: - Numeric → String coercion

    /// The core parses every value as a String, but ConfigService persisted the
    /// config numbers as `Int` (limits) and `Double` (timestamp). Use
    /// `object(forKey:)` (not `string(forKey:)`) so missing keys are `nil`
    /// (distinct from empty), and coerce numeric NSNumbers to the integer string
    /// the core's `parse::<i64>()` expects.
    private func coerceToString(_ object: Any?) -> String? {
        switch object {
        case let s as String:
            return s
        case let n as NSNumber:
            // NSNumber from UserDefaults covers both the Int and Double cases.
            // The core only consumes whole seconds / counts, so truncate to Int.
            return String(n.int64Value)
        case .none:
            return nil
        default:
            return nil
        }
    }
}

/// Centralized `now` injection for the Rust license core.
///
/// WHY two flavors:
/// - The core's USAGE day-bucket is `now_unix_secs / 86400` (UTC days). Native
///   macOS reset daily usage at LOCAL calendar midnight (`isDateInToday`). To
///   preserve that, usage calls pass `now` shifted by the current GMT offset so
///   the UTC bucket boundary lands on local midnight. The offset is recomputed
///   each call so DST transitions are handled correctly.
/// - License CACHE TTL comparisons (`shouldRevalidate`, grace, override TTL) are
///   pure duration deltas (`now - stored`). The local offset would cancel out,
///   so those use plain UTC to avoid a one-time off-by-offset glitch on the very
///   first call after this migration (stored timestamps were written in UTC).
enum RustLicenseTime {
    /// Plain UTC unix seconds — for cache/grace/override TTL deltas.
    static func nowUTC() -> Int64 {
        Int64(Date().timeIntervalSince1970)
    }

    /// UTC seconds shifted into the local day — for usage day-bucket calls so the
    /// core's `now/86400` boundary matches local midnight. Recomputes the GMT
    /// offset every call (DST-safe).
    static func nowLocal() -> Int64 {
        Int64(Date().timeIntervalSince1970) + Int64(TimeZone.current.secondsFromGMT())
    }

    /// The local day index (`localNow / 86400`) consistent with `nowLocal()`,
    /// used to seed `com.hyperwhisper.usage.dayIndex`.
    static func localDayIndex() -> Int64 {
        nowLocal() / 86_400
    }
}
