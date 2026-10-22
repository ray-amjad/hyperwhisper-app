//! Golden parity tests for hw-license M3 — deterministic end-to-end scenarios
//! driven by an injected `now_unix_secs` and an in-memory `KeyValueStore`.
//!
//! These exercise the full lifecycle across the modules together (the
//! per-module `#[cfg(test)]` blocks cover the units): validate → cache,
//! status transitions, and cache/grace expiry.

use hw_license::cache::{
    self, offline_fallback_outcome, remote_override_if_fresh, should_revalidate,
    store_license_key, store_remote_override, stored_license_key, update_validation_cache,
    TrialLimits,
};
use hw_license::validate::{
    build_validate_request, parse_validate_response, VALIDATE_URL,
};
use hw_license::{KeyValueStore, LicenseStatus, MemoryStore};

const DAY: i64 = 86_400;

/// Representative validate-endpoint request shape.
#[test]
fn golden_validate_request_shape() {
    let req = build_validate_request("ABCD-1234", "macbook-uuid", "Rays-MacBook-Pro");
    assert_eq!(req.url, VALIDATE_URL);
    assert_eq!(req.content_type, "application/json");
    assert_eq!(
        req.body_text(),
        r#"{"license_key":"ABCD-1234","device_id":"macbook-uuid","device_name":"Rays-MacBook-Pro"}"#
    );
}

/// Full activation flow: trial → activate with a valid key → Active, cache
/// updated, key stored.
#[test]
fn golden_trial_to_active_transition() {
    let store = MemoryStore::new();
    let t0 = 1_700_000_000i64;

    // Starts with no stored license → caller would treat as Trial.
    assert_eq!(stored_license_key(&store), None);

    // Server says valid (macOS bool shape).
    let outcome = parse_validate_response(br#"{"valid":true,"customer_id":"cust_42"}"#);
    assert_eq!(outcome.status, LicenseStatus::Active);
    assert!(outcome.is_valid);

    // Caller persists on success.
    store_license_key(&store, "ABCD-1234");
    update_validation_cache(&store, outcome.status, t0);

    assert_eq!(stored_license_key(&store).as_deref(), Some("ABCD-1234"));
    assert_eq!(
        cache::cached_status_within_grace(&store, t0 + 60),
        Some(LicenseStatus::Active)
    );
    // Within 24h → no revalidation needed.
    assert!(!should_revalidate(&store, t0 + 60));
}

/// Active → Expired transition via a later revalidation (Windows status string).
#[test]
fn golden_active_to_expired_transition() {
    let store = MemoryStore::new();
    let t0 = 1_700_000_000i64;
    store_license_key(&store, "ABCD-1234");
    update_validation_cache(&store, LicenseStatus::Active, t0);

    // 25h later → revalidation due.
    let t1 = t0 + DAY + 3600;
    assert!(should_revalidate(&store, t1));

    // Server now reports expired (status string wins).
    let outcome = parse_validate_response(br#"{"status":"expired","error":"subscription lapsed"}"#);
    assert_eq!(outcome.status, LicenseStatus::Expired);
    assert!(!outcome.is_valid);
    update_validation_cache(&store, outcome.status, t1);

    assert_eq!(
        cache::cached_status_within_grace(&store, t1 + 60),
        Some(LicenseStatus::Expired)
    );
}

/// Offline behavior: within grace returns cached Active; past grace returns
/// Invalid.
#[test]
fn golden_offline_grace_window() {
    let store = MemoryStore::new();
    let t0 = 1_700_000_000i64;
    store.set(
        "com.hyperwhisper.license.customerId".to_string(),
        "cust_42".to_string(),
    );
    update_validation_cache(&store, LicenseStatus::Active, t0);

    // Day 3 offline → cached Active.
    let within = offline_fallback_outcome(&store, t0 + 3 * DAY);
    assert_eq!(within.status, LicenseStatus::Active);
    assert!(within.is_valid);
    assert_eq!(within.customer_id.as_deref(), Some("cust_42"));

    // Day 8 offline (past 7-day grace) → Invalid.
    let past = offline_fallback_outcome(&store, t0 + 8 * DAY);
    assert_eq!(past.status, LicenseStatus::Invalid);
    assert!(!past.is_valid);
}

/// The remote-override store/TTL machinery in `cache.rs` round-trips with a
/// 24h TTL. Nothing reads the override any more: local usage is unlimited.
#[test]
fn golden_remote_override_round_trips_with_24h_ttl() {
    let store = MemoryStore::new();
    let t0 = 1_700_000_000i64;
    let day = t0.div_euclid(DAY);
    let morning = day * DAY + 3600;

    store_remote_override(
        &store,
        TrialLimits {
            daily_seconds: 1,
            model_downloads: 0,
        },
        morning,
    );
    let ov = remote_override_if_fresh(&store, morning + 3600).expect("override fresh");
    assert_eq!(ov.daily_seconds, 1);
    assert_eq!(ov.model_downloads, 0);

    // TTL still expires as before.
    assert!(remote_override_if_fresh(&store, morning + DAY + 3600).is_none());
}

/// An invalid key never activates.
#[test]
fn golden_invalid_key_never_activates() {
    let invalid = parse_validate_response(br#"{"valid":false,"error":"unknown key"}"#);
    assert_eq!(invalid.status, LicenseStatus::Invalid);
    assert!(!invalid.is_valid);
    assert_eq!(invalid.error_message.as_deref(), Some("unknown key"));
}
