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
//  Today's macOS persistence is split across two stores with two key naming
//  conventions, and this class reconciles both so existing users do NOT lose
//  their trial seconds, lifetime model-download count, or active license:
//
//  1. license.* keys  → already in UserDefaults under
//     `com.hyperwhisper.license.*` (see LicenseNetworkService.DefaultsKey).
//     These match the core's keys EXACTLY — no migration, no alias.
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

/// UserDefaults-backed `KeyValueStore` for the Rust license/usage core.
///
/// Class-only (`AnyObject`) to satisfy the binding's `KeyValueStore` protocol
/// (a UniFFI callback interface). A single shared instance is held by
/// `LicenseManager` and passed to every `license_*` call.
final class RustLicenseStore: KeyValueStore {

    // MARK: - Backing store

    private let defaults: UserDefaults

    /// Guards the one-shot Core Data → UserDefaults usage seed.
    private static let seedFlagKey = "didSeedUsageToKeyValueStoreV1"

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

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        seedUsageFromCoreDataIfNeeded()
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
        defaults.set(value, forKey: key)
    }

    func delete(key: String) {
        defaults.removeObject(forKey: key)
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
