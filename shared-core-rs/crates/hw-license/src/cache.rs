//! Validation cache, offline grace period, and remote trial-limit override —
//! all persisted through the platform's [`KeyValueStore`].
//!
//! No clock lives in Rust: every time-dependent function takes `now_unix_secs:
//! i64` supplied by the platform, which makes the whole module deterministically
//! golden-testable.
//!
//! Parity target: macOS `LicenseNetworkService` (24h revalidation, 7-day grace)
//! and `ConfigService` (remote trial-limit fetch). The config TTL is **server-
//! driven**: the platform persists the response's `Cache-Control: max-age` into
//! [`K_OVERRIDE_MAX_AGE`], and [`remote_override_if_fresh`] honors it — defaulting
//! to the prior macOS default ([`REMOTE_OVERRIDE_DEFAULT_TTL_SECS`], 6h) when no
//! max-age is stored, and clamping any stored value to
//! [`REMOTE_OVERRIDE_TTL_SECS`] (24h) as an absolute upper bound.

use crate::validate::ValidationOutcome;
use crate::{KeyValueStore, LicenseStatus};

/// Seconds in 24 hours — the validation cache duration. Mirrors macOS
/// `NetworkConfig.validationCacheDuration = 86400`.
pub const VALIDATION_CACHE_SECS: i64 = 86_400;

/// Seconds in 7 days — the offline grace period. Mirrors macOS
/// `NetworkConfig.offlineGracePeriod = 604800`.
pub const OFFLINE_GRACE_SECS: i64 = 604_800;

/// Absolute upper clamp on the remote trial-limit override TTL: 24 hours. A stored
/// server `max-age` is honored up to this cap (a hostile/misconfigured server
/// cannot pin a stale override indefinitely). See [`remote_override_if_fresh`].
pub const REMOTE_OVERRIDE_TTL_SECS: i64 = 86_400;

/// Default remote-override TTL used when no server `max-age` has been stored: 6
/// hours. Mirrors the pre-unification macOS `ConfigService` default
/// (`Cache-Control` absent ⇒ 6h).
pub const REMOTE_OVERRIDE_DEFAULT_TTL_SECS: i64 = 21_600;

// ---- KeyValueStore keys (namespaced to match the platform conventions) ----

const K_LICENSE_KEY: &str = "com.hyperwhisper.license.key";
const K_CUSTOMER_ID: &str = "com.hyperwhisper.license.customerId";
const K_LAST_VALIDATION: &str = "com.hyperwhisper.license.lastValidation";
const K_CACHED_STATUS: &str = "com.hyperwhisper.license.cachedStatus";

const K_OVERRIDE_DAILY: &str = "com.hyperwhisper.config.trialDailyLimitSeconds";
const K_OVERRIDE_MODELS: &str = "com.hyperwhisper.config.trialModelDownloadLimit";
const K_OVERRIDE_FETCHED_AT: &str = "com.hyperwhisper.config.lastFetchTimestamp";
/// Server-driven TTL: the platform writes the config response's `Cache-Control:
/// max-age` (in seconds) here when it fetches config; [`remote_override_if_fresh`]
/// reads it. Absent ⇒ [`REMOTE_OVERRIDE_DEFAULT_TTL_SECS`]. Exposed as a `pub
/// const` so platform call sites reference the exact key string.
pub const K_OVERRIDE_MAX_AGE: &str = "com.hyperwhisper.config.maxAgeSecs";

/// Serialize a [`LicenseStatus`] to its stable persisted string. Matches the
/// macOS `LicenseStatus.rawValue` ("Trial"/"Active"/"Expired"/"Invalid").
fn status_to_str(s: LicenseStatus) -> &'static str {
    match s {
        LicenseStatus::Trial => "Trial",
        LicenseStatus::Active => "Active",
        LicenseStatus::Expired => "Expired",
        LicenseStatus::Invalid => "Invalid",
    }
}

/// Parse a persisted status string back to a [`LicenseStatus`]. Returns `None`
/// for an unrecognized value (caller treats as no-cache).
fn status_from_str(s: &str) -> Option<LicenseStatus> {
    match s {
        "Trial" => Some(LicenseStatus::Trial),
        "Active" => Some(LicenseStatus::Active),
        "Expired" => Some(LicenseStatus::Expired),
        "Invalid" => Some(LicenseStatus::Invalid),
        _ => None,
    }
}

/// Persist the stored license key (skips empty/whitespace-only values, matching
/// the macOS `setDefaultsValue` guard).
pub fn store_license_key(store: &dyn KeyValueStore, key: &str) {
    if key.trim().is_empty() {
        return;
    }
    store.set(K_LICENSE_KEY.to_string(), key.to_string());
}

/// Read the stored license key, returning `None` for missing or whitespace-only
/// values (matches macOS `getStoredLicenseKey`).
pub fn stored_license_key(store: &dyn KeyValueStore) -> Option<String> {
    let key = store.get(K_LICENSE_KEY.to_string())?;
    if key.trim().is_empty() {
        None
    } else {
        Some(key)
    }
}

/// Update the validation cache after a validation attempt: records `now` as the
/// last-validation time and the resolved status. Mirrors macOS
/// `updateValidationCache`.
pub fn update_validation_cache(
    store: &dyn KeyValueStore,
    status: LicenseStatus,
    now_unix_secs: i64,
) {
    store.set(K_LAST_VALIDATION.to_string(), now_unix_secs.to_string());
    store.set(K_CACHED_STATUS.to_string(), status_to_str(status).to_string());
}

/// Whether the license should be revalidated against the server.
///
/// Returns `true` when there is no cached validation timestamp, the timestamp is
/// unparseable, or more than [`VALIDATION_CACHE_SECS`] (24h) have elapsed.
/// Mirrors macOS `shouldRevalidateLicense`.
pub fn should_revalidate(store: &dyn KeyValueStore, now_unix_secs: i64) -> bool {
    let Some(last) = last_validation(store) else {
        return true;
    };
    // A negative delta means the clock ran backwards since the last validation
    // (NTP correction, manual change). Treat that as stale and revalidate — never
    // let a backward clock make the cache look perpetually fresh.
    let delta = now_unix_secs - last;
    !(0..=VALIDATION_CACHE_SECS).contains(&delta)
}

/// The cached license status if still within the 7-day offline grace period,
/// else `None`. Mirrors macOS `getCachedLicenseStatus`.
///
/// Returns `None` if there is no cached status, no/invalid timestamp, or the
/// grace period has elapsed.
pub fn cached_status_within_grace(
    store: &dyn KeyValueStore,
    now_unix_secs: i64,
) -> Option<LicenseStatus> {
    let status = status_from_str(&store.get(K_CACHED_STATUS.to_string())?)?;
    let last = last_validation(store)?;
    // Within grace only for a non-negative, sub-grace delta. A backward clock
    // (negative delta) is treated as expired so it cannot extend the grace window.
    let delta = now_unix_secs - last;
    if (0..=OFFLINE_GRACE_SECS).contains(&delta) {
        Some(status)
    } else {
        None
    }
}

/// Build the offline-fallback outcome when a validation network call fails: uses
/// the cached status if still within grace, else an `Invalid` outcome.
///
/// Mirrors the macOS `catch` branch of `validateLicense`.
pub fn offline_fallback_outcome(
    store: &dyn KeyValueStore,
    now_unix_secs: i64,
) -> ValidationOutcome {
    if let Some(status) = cached_status_within_grace(store, now_unix_secs) {
        ValidationOutcome {
            is_valid: status == LicenseStatus::Active,
            status,
            customer_id: store.get(K_CUSTOMER_ID.to_string()),
            customer_email: None,
            expires_at: None,
            error_message: Some("Using cached license (offline)".to_string()),
        }
    } else {
        ValidationOutcome {
            is_valid: false,
            status: LicenseStatus::Invalid,
            customer_id: None,
            customer_email: None,
            expires_at: None,
            error_message: Some("Offline and no cached license".to_string()),
        }
    }
}

/// Clear all stored license data (deactivation / reset to trial). Mirrors macOS
/// `clearStoredLicense`. Does NOT clear the remote-override config (which is
/// independent of the user's license).
pub fn clear_stored_license(store: &dyn KeyValueStore) {
    store.delete(K_LICENSE_KEY.to_string());
    store.delete(K_CUSTOMER_ID.to_string());
    store.delete(K_LAST_VALIDATION.to_string());
    store.delete(K_CACHED_STATUS.to_string());
}

fn last_validation(store: &dyn KeyValueStore) -> Option<i64> {
    // Stored as a unix-seconds integer string. macOS stores a fractional
    // TimeInterval; we truncate to whole seconds (the comparisons use 24h/7d
    // granularity, so sub-second precision is irrelevant) and tolerate either by
    // parsing the integer part.
    let raw = store.get(K_LAST_VALIDATION.to_string())?;
    raw.split('.').next()?.trim().parse::<i64>().ok()
}

// ---- Remote trial-limit override (24h TTL) ----

/// Trial limits, either the hardcoded defaults or a remote override.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TrialLimits {
    pub daily_seconds: i64,
    pub model_downloads: i64,
}

/// Persist a fetched remote override with `now` as its fetch timestamp.
pub fn store_remote_override(
    store: &dyn KeyValueStore,
    limits: TrialLimits,
    now_unix_secs: i64,
) {
    store.set(K_OVERRIDE_DAILY.to_string(), limits.daily_seconds.to_string());
    store.set(
        K_OVERRIDE_MODELS.to_string(),
        limits.model_downloads.to_string(),
    );
    store.set(
        K_OVERRIDE_FETCHED_AT.to_string(),
        now_unix_secs.to_string(),
    );
}

/// The effective remote-override TTL: the stored server `max-age` if present
/// (clamped to `[0, REMOTE_OVERRIDE_TTL_SECS]`), else
/// [`REMOTE_OVERRIDE_DEFAULT_TTL_SECS`]. An unparseable / missing value falls back
/// to the default.
fn effective_override_ttl(store: &dyn KeyValueStore) -> i64 {
    match store
        .get(K_OVERRIDE_MAX_AGE.to_string())
        .and_then(|v| v.trim().parse::<i64>().ok())
    {
        Some(max_age) => max_age.clamp(0, REMOTE_OVERRIDE_TTL_SECS),
        None => REMOTE_OVERRIDE_DEFAULT_TTL_SECS,
    }
}

/// The remote trial-limit override if present and still fresh, else `None`.
/// Mirrors macOS `ConfigService.getCachedConfig`: freshness uses the server-driven
/// TTL (stored `Cache-Control: max-age`, default 6h, clamped to 24h) — see
/// [`effective_override_ttl`] and the module docs.
pub fn remote_override_if_fresh(
    store: &dyn KeyValueStore,
    now_unix_secs: i64,
) -> Option<TrialLimits> {
    let daily = store.get(K_OVERRIDE_DAILY.to_string())?.parse::<i64>().ok()?;
    let models = store.get(K_OVERRIDE_MODELS.to_string())?.parse::<i64>().ok()?;
    let fetched = store
        .get(K_OVERRIDE_FETCHED_AT.to_string())?
        .parse::<i64>()
        .ok()?;
    // Reject a backward clock (negative delta) as stale, same as an expired TTL —
    // otherwise a clock set into the past would pin a stale override as "fresh".
    let delta = now_unix_secs - fetched;
    if delta < 0 || delta > effective_override_ttl(store) {
        return None;
    }
    Some(TrialLimits {
        daily_seconds: daily,
        model_downloads: models,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::MemoryStore;

    fn store() -> MemoryStore {
        MemoryStore::new()
    }

    #[test]
    fn store_and_read_license_key_skips_empty() {
        let s = store();
        store_license_key(&s, "   ");
        assert_eq!(stored_license_key(&s), None);
        store_license_key(&s, "KEY-1");
        assert_eq!(stored_license_key(&s).as_deref(), Some("KEY-1"));
    }

    #[test]
    fn should_revalidate_true_when_no_cache() {
        let s = store();
        assert!(should_revalidate(&s, 1_000_000));
    }

    #[test]
    fn cache_ttl_boundary() {
        let s = store();
        let t0 = 1_000_000i64;
        update_validation_cache(&s, LicenseStatus::Active, t0);
        // Exactly 24h later → still cached (uses `>` not `>=`).
        assert!(!should_revalidate(&s, t0 + VALIDATION_CACHE_SECS));
        // One second past 24h → revalidate.
        assert!(should_revalidate(&s, t0 + VALIDATION_CACHE_SECS + 1));
    }

    #[test]
    fn grace_period_boundary() {
        let s = store();
        let t0 = 1_000_000i64;
        update_validation_cache(&s, LicenseStatus::Active, t0);
        // Exactly 7 days later → still within grace.
        assert_eq!(
            cached_status_within_grace(&s, t0 + OFFLINE_GRACE_SECS),
            Some(LicenseStatus::Active)
        );
        // One second past 7 days → grace expired.
        assert_eq!(
            cached_status_within_grace(&s, t0 + OFFLINE_GRACE_SECS + 1),
            None
        );
    }

    #[test]
    fn offline_fallback_uses_cache_within_grace() {
        let s = store();
        let t0 = 1_000_000i64;
        s.set(K_CUSTOMER_ID.to_string(), "cust_9".to_string());
        update_validation_cache(&s, LicenseStatus::Active, t0);
        let out = offline_fallback_outcome(&s, t0 + 60);
        assert_eq!(out.status, LicenseStatus::Active);
        assert!(out.is_valid);
        assert_eq!(out.customer_id.as_deref(), Some("cust_9"));
    }

    #[test]
    fn offline_fallback_invalid_after_grace() {
        let s = store();
        let t0 = 1_000_000i64;
        update_validation_cache(&s, LicenseStatus::Active, t0);
        let out = offline_fallback_outcome(&s, t0 + OFFLINE_GRACE_SECS + 10);
        assert_eq!(out.status, LicenseStatus::Invalid);
        assert!(!out.is_valid);
    }

    #[test]
    fn clear_removes_license_but_keeps_override() {
        let s = store();
        store_license_key(&s, "KEY-1");
        update_validation_cache(&s, LicenseStatus::Active, 100);
        store_remote_override(
            &s,
            TrialLimits {
                daily_seconds: 600,
                model_downloads: 5,
            },
            100,
        );
        clear_stored_license(&s);
        assert_eq!(stored_license_key(&s), None);
        assert_eq!(cached_status_within_grace(&s, 200), None);
        assert_eq!(
            remote_override_if_fresh(&s, 200),
            Some(TrialLimits {
                daily_seconds: 600,
                model_downloads: 5
            })
        );
    }

    #[test]
    fn remote_override_default_ttl_when_no_max_age() {
        // GOLDEN (B4): with no stored server max-age, freshness uses the 6h default.
        let s = store();
        let t0 = 5_000_000i64;
        store_remote_override(
            &s,
            TrialLimits {
                daily_seconds: 600,
                model_downloads: 5,
            },
            t0,
        );
        // Within the default 6h TTL.
        assert!(remote_override_if_fresh(&s, t0 + REMOTE_OVERRIDE_DEFAULT_TTL_SECS).is_some());
        // One second past 6h → stale.
        assert_eq!(
            remote_override_if_fresh(&s, t0 + REMOTE_OVERRIDE_DEFAULT_TTL_SECS + 1),
            None
        );
        // The old 24h default no longer applies absent a server max-age.
        assert_eq!(remote_override_if_fresh(&s, t0 + REMOTE_OVERRIDE_TTL_SECS), None);
    }

    #[test]
    fn remote_override_honors_server_max_age() {
        // GOLDEN (B4): a stored Cache-Control max-age drives the freshness window.
        let s = store();
        let t0 = 5_000_000i64;
        store_remote_override(
            &s,
            TrialLimits {
                daily_seconds: 600,
                model_downloads: 5,
            },
            t0,
        );
        // Server says cache for 2h (7200s) — shorter than the default.
        s.set(K_OVERRIDE_MAX_AGE.to_string(), "7200".to_string());
        assert!(remote_override_if_fresh(&s, t0 + 7200).is_some());
        assert_eq!(remote_override_if_fresh(&s, t0 + 7200 + 1), None);

        // A server max-age above the 24h clamp is capped at 24h.
        s.set(K_OVERRIDE_MAX_AGE.to_string(), "999999999".to_string());
        assert!(remote_override_if_fresh(&s, t0 + REMOTE_OVERRIDE_TTL_SECS).is_some());
        assert_eq!(
            remote_override_if_fresh(&s, t0 + REMOTE_OVERRIDE_TTL_SECS + 1),
            None
        );

        // An unparseable max-age falls back to the 6h default.
        s.set(K_OVERRIDE_MAX_AGE.to_string(), "not-a-number".to_string());
        assert!(remote_override_if_fresh(&s, t0 + REMOTE_OVERRIDE_DEFAULT_TTL_SECS).is_some());
        assert_eq!(
            remote_override_if_fresh(&s, t0 + REMOTE_OVERRIDE_DEFAULT_TTL_SECS + 1),
            None
        );
    }

    #[test]
    fn backward_clock_forces_revalidation() {
        // GOLDEN (F4): clock ran backwards — `now` is earlier than the stored
        // last-validation timestamp. Must revalidate, not treat the cache as fresh.
        let s = store();
        let t0 = 5_000_000i64;
        update_validation_cache(&s, LicenseStatus::Active, t0);
        assert!(should_revalidate(&s, t0 - 60));
        assert!(should_revalidate(&s, t0 - VALIDATION_CACHE_SECS * 2));
        // Sanity: a forward delta inside the window still does NOT revalidate.
        assert!(!should_revalidate(&s, t0 + 60));
    }

    #[test]
    fn backward_clock_expires_grace() {
        // GOLDEN (F4): a backward clock must not extend the offline grace window.
        let s = store();
        let t0 = 5_000_000i64;
        update_validation_cache(&s, LicenseStatus::Active, t0);
        assert_eq!(cached_status_within_grace(&s, t0 - 1), None);
        // The offline fallback then degrades to Invalid rather than a stale Active.
        let out = offline_fallback_outcome(&s, t0 - 1);
        assert_eq!(out.status, LicenseStatus::Invalid);
        assert!(!out.is_valid);
    }

    #[test]
    fn backward_clock_expires_remote_override() {
        // GOLDEN (F4): a backward clock must not keep a stale remote override fresh.
        let s = store();
        let t0 = 5_000_000i64;
        store_remote_override(
            &s,
            TrialLimits {
                daily_seconds: 600,
                model_downloads: 5,
            },
            t0,
        );
        assert_eq!(remote_override_if_fresh(&s, t0 - 1), None);
        // Sanity: a forward delta inside the default TTL is still fresh.
        assert!(remote_override_if_fresh(&s, t0 + 60).is_some());
    }

    #[test]
    fn last_validation_tolerates_fractional_timestamp() {
        let s = store();
        // macOS persists a fractional TimeInterval; ensure we parse the seconds.
        s.set(K_LAST_VALIDATION.to_string(), "1000000.523".to_string());
        s.set(K_CACHED_STATUS.to_string(), "Active".to_string());
        assert_eq!(
            cached_status_within_grace(&s, 1_000_060),
            Some(LicenseStatus::Active)
        );
    }
}
