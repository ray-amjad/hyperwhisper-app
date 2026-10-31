//! #2916: a 200 OK whose body is not the validate JSON (a captive-portal or
//! proxy login page, or an empty body) is not a server verdict about the key.
//! It must not overwrite the stored key's cached Active status with Invalid.
//!
//! The issue's repro persisted `outcome.status` unconditionally. With the fix the
//! core marks such an outcome `is_verdict == false`, and every head takes its
//! network-failure path instead of persisting. These tests run that head flow:
//! parse, then persist only a verdict, else build the offline fallback.

use hw_license::cache::{
    cached_status_within_grace, offline_fallback_outcome, persist_validation_verdict,
    should_revalidate, stored_license_key,
};
use hw_license::validate::{parse_validate_response, ValidationOutcome};
use hw_license::{LicenseStatus, MemoryStore};

const T0: i64 = 1_800_000_000;
const KEY: &str = "KEY-A";

/// What each head does with a 200 reply after the fix.
fn head_handles_200(s: &MemoryStore, body: &[u8], key: &str, now: i64) -> ValidationOutcome {
    let outcome = parse_validate_response(body);
    if outcome.is_verdict {
        persist_validation_verdict(s, outcome.status, key, now);
        outcome
    } else {
        offline_fallback_outcome(s, now)
    }
}

fn run(body: &[u8]) {
    let s = MemoryStore::new();
    // Paying user: key A validated Active.
    persist_validation_verdict(&s, LicenseStatus::Active, KEY, T0);
    // Next revalidation (cache expired) meets a hotel Wi-Fi login page.
    let t1 = T0 + 86_400 + 60;
    assert!(should_revalidate(&s, t1));

    let parsed = parse_validate_response(body);
    let shown = String::from_utf8_lossy(body);
    assert!(!parsed.is_verdict, "{shown:?} parsed as a verdict");

    let outcome = head_handles_200(&s, body, KEY, t1);
    assert_eq!(
        outcome.status,
        LicenseStatus::Active,
        "{shown:?}: offline fallback did not keep the cached Active"
    );
    assert!(outcome.is_valid, "{shown:?}");

    // The cache is untouched: still Active within grace, still due a re-check.
    assert_eq!(
        cached_status_within_grace(&s, t1 + 60),
        Some(LicenseStatus::Active),
        "non-JSON 200 body {shown:?} was cached as a verdict"
    );
    assert!(should_revalidate(&s, t1 + 60), "{shown:?}");
    assert_eq!(stored_license_key(&s).as_deref(), Some(KEY));
}

#[test]
fn captive_portal_html_200_does_not_cache_invalid() {
    run(b"<html><body>Please log in to Hotel WiFi</body></html>");
}

#[test]
fn empty_200_body_does_not_cache_invalid() {
    run(b"");
}

#[test]
fn json_200_without_valid_or_status_does_not_cache_invalid() {
    for body in [&b"{}"[..], b"[]", b"\"ok\"", b"null"] {
        run(body);
    }
}

/// Guard against over-fixing: a real rejection of the stored key is still a
/// verdict, cached as Invalid and trusted for the cache window.
#[test]
fn real_valid_false_for_the_stored_key_still_caches_invalid() {
    let s = MemoryStore::new();
    persist_validation_verdict(&s, LicenseStatus::Active, KEY, T0);
    let t1 = T0 + 86_400 + 60;

    let outcome = head_handles_200(&s, br#"{"valid":false}"#, KEY, t1);
    assert!(outcome.is_verdict);
    assert_eq!(outcome.status, LicenseStatus::Invalid);
    assert_eq!(
        cached_status_within_grace(&s, t1 + 60),
        Some(LicenseStatus::Invalid)
    );
    assert!(!should_revalidate(&s, t1 + 60));
}

#[test]
fn real_revoked_status_for_the_stored_key_still_caches_invalid() {
    let s = MemoryStore::new();
    persist_validation_verdict(&s, LicenseStatus::Active, KEY, T0);
    let t1 = T0 + 86_400 + 60;

    let outcome = head_handles_200(&s, br#"{"status":"revoked"}"#, KEY, t1);
    assert!(outcome.is_verdict);
    assert_eq!(
        cached_status_within_grace(&s, t1 + 60),
        Some(LicenseStatus::Invalid)
    );
}
