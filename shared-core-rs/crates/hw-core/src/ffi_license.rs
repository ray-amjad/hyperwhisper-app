//! UniFFI surface for the M3 license core (`hw_license`).
//!
//! Mirrors the public records/enums and exposes the validate/cache
//! functions. Persistence-taking functions accept the foreign
//! `Arc<dyn KeyValueStore>` (defined in lib.rs), wrap it in [`KvAdapter`] — which
//! implements the leaf crate's plain `hw_license::KeyValueStore` trait — and pass
//! `&adapter` into the leaf. Time is always an explicit `now_unix_secs: i64`.

use crate::KeyValueStore;
use std::sync::Arc;

/// Bridges the UniFFI foreign trait (`crate::KeyValueStore`, an
/// `Arc<dyn ...>`) to the leaf crate's plain `hw_license::KeyValueStore` trait by
/// delegating each call.
struct KvAdapter(Arc<dyn KeyValueStore>);

impl hw_license::KeyValueStore for KvAdapter {
    fn get(&self, key: String) -> Option<String> {
        self.0.get(key)
    }
    fn set(&self, key: String, value: String) {
        self.0.set(key, value)
    }
    fn delete(&self, key: String) {
        self.0.delete(key)
    }
}

// ===========================================================================
// Mirrored types
// ===========================================================================

/// The current license state. Mirrors `hw_license::LicenseStatus`.
#[derive(uniffi::Enum)]
pub enum HwLicenseStatus {
    Trial,
    Active,
    Expired,
    Invalid,
}

impl From<HwLicenseStatus> for hw_license::LicenseStatus {
    fn from(s: HwLicenseStatus) -> Self {
        match s {
            HwLicenseStatus::Trial => hw_license::LicenseStatus::Trial,
            HwLicenseStatus::Active => hw_license::LicenseStatus::Active,
            HwLicenseStatus::Expired => hw_license::LicenseStatus::Expired,
            HwLicenseStatus::Invalid => hw_license::LicenseStatus::Invalid,
        }
    }
}

impl From<hw_license::LicenseStatus> for HwLicenseStatus {
    fn from(s: hw_license::LicenseStatus) -> Self {
        match s {
            hw_license::LicenseStatus::Trial => HwLicenseStatus::Trial,
            hw_license::LicenseStatus::Active => HwLicenseStatus::Active,
            hw_license::LicenseStatus::Expired => HwLicenseStatus::Expired,
            hw_license::LicenseStatus::Invalid => HwLicenseStatus::Invalid,
        }
    }
}

/// The POST `/api/license/validate` request. Mirrors
/// `hw_license::validate::ValidateRequest`.
#[derive(uniffi::Record)]
pub struct ValidateRequest {
    pub url: String,
    pub content_type: String,
    pub body: Vec<u8>,
}

impl From<hw_license::validate::ValidateRequest> for ValidateRequest {
    fn from(r: hw_license::validate::ValidateRequest) -> Self {
        ValidateRequest {
            url: r.url,
            content_type: r.content_type,
            body: r.body,
        }
    }
}

/// Outcome of a validation attempt. Mirrors
/// `hw_license::validate::ValidationOutcome`.
#[derive(uniffi::Record)]
pub struct ValidationOutcome {
    pub is_valid: bool,
    pub status: HwLicenseStatus,
    pub customer_id: Option<String>,
    pub customer_email: Option<String>,
    pub expires_at: Option<String>,
    pub error_message: Option<String>,
}

impl From<hw_license::validate::ValidationOutcome> for ValidationOutcome {
    fn from(o: hw_license::validate::ValidationOutcome) -> Self {
        ValidationOutcome {
            is_valid: o.is_valid,
            status: o.status.into(),
            customer_id: o.customer_id,
            customer_email: o.customer_email,
            expires_at: o.expires_at,
            error_message: o.error_message,
        }
    }
}

// ===========================================================================
// validate
// ===========================================================================

/// Build the POST `/api/license/validate` request.
#[uniffi::export]
pub fn license_build_validate_request(
    license_key: String,
    device_id: String,
    device_name: String,
) -> ValidateRequest {
    hw_license::validate::build_validate_request(&license_key, &device_id, &device_name).into()
}

/// The empty/whitespace-only license-key outcome (rejected before any call).
#[uniffi::export]
pub fn license_empty_key_outcome() -> ValidationOutcome {
    hw_license::validate::empty_key_outcome().into()
}

/// Outcome for a terminal non-200 HTTP validate response.
#[uniffi::export]
pub fn license_http_error_outcome(status_code: u16, body: Vec<u8>) -> ValidationOutcome {
    hw_license::validate::http_error_outcome(status_code, &body).into()
}

/// Parse a 200-OK validate response body to a `ValidationOutcome`.
#[uniffi::export]
pub fn license_parse_validate_response(body: Vec<u8>) -> ValidationOutcome {
    hw_license::validate::parse_validate_response(&body).into()
}

// ===========================================================================
// cache (constants + store-taking fns)
// ===========================================================================

#[uniffi::export]
pub fn license_stored_license_key(store: Arc<dyn KeyValueStore>) -> Option<String> {
    hw_license::cache::stored_license_key(&KvAdapter(store))
}

/// Persist a server validation verdict for the attempted key: stores the key on
/// a valid (`Active`) verdict, and updates the validation cache only when the
/// verdict is valid or the attempted key matches the stored key. This is the
/// only FFI write path for a validate response: it prevents a rejected
/// *replacement* key from clobbering the stored key's cached status (a 24h
/// lockout for a valid user).
#[uniffi::export]
pub fn license_persist_validation_verdict(
    store: Arc<dyn KeyValueStore>,
    status: HwLicenseStatus,
    attempted_key: String,
    now_unix_secs: i64,
) {
    hw_license::cache::persist_validation_verdict(
        &KvAdapter(store),
        status.into(),
        &attempted_key,
        now_unix_secs,
    )
}

#[uniffi::export]
pub fn license_should_revalidate(store: Arc<dyn KeyValueStore>, now_unix_secs: i64) -> bool {
    hw_license::cache::should_revalidate(&KvAdapter(store), now_unix_secs)
}

#[uniffi::export]
pub fn license_cached_status_within_grace(
    store: Arc<dyn KeyValueStore>,
    now_unix_secs: i64,
) -> Option<HwLicenseStatus> {
    hw_license::cache::cached_status_within_grace(&KvAdapter(store), now_unix_secs).map(Into::into)
}

#[uniffi::export]
pub fn license_offline_fallback_outcome(
    store: Arc<dyn KeyValueStore>,
    now_unix_secs: i64,
) -> ValidationOutcome {
    hw_license::cache::offline_fallback_outcome(&KvAdapter(store), now_unix_secs).into()
}

#[uniffi::export]
pub fn license_clear_stored_license(store: Arc<dyn KeyValueStore>) {
    hw_license::cache::clear_stored_license(&KvAdapter(store))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use std::sync::Mutex;

    // -----------------------------------------------------------------------
    // helpers
    //
    // The exported functions here are a bridge: they map the mirrored UniFFI
    // records/enums onto `hw_license` and forward the foreign store. A
    // round-trip through the bridge cannot catch a pair of arms (or a pair of
    // fields) swapped in BOTH directions, so every mapping below is pinned to an
    // observable that does not go back through the same conversion:
    //
    //  - the `Hw* -> hw_license` direction is read off the raw key/value pairs
    //    the call leaves in the store (the strings the platforms persist),
    //  - the `hw_license -> Hw*` direction is read out of a store seeded with
    //    those raw strings by hand, or out of a parsed server response.
    //
    // The key strings are written out literally for the same reason, and because
    // they are themselves a platform contract: macOS and Windows read the same
    // Keychain / Credential Manager entries.
    // -----------------------------------------------------------------------

    const K_LICENSE_KEY: &str = "com.hyperwhisper.license.key";
    const K_CUSTOMER_ID: &str = "com.hyperwhisper.license.customerId";
    const K_LAST_VALIDATION: &str = "com.hyperwhisper.license.lastValidation";
    const K_CACHED_STATUS: &str = "com.hyperwhisper.license.cachedStatus";
    const K_OVERRIDE_DAILY: &str = "com.hyperwhisper.config.trialDailyLimitSeconds";
    const K_OVERRIDE_MODELS: &str = "com.hyperwhisper.config.trialModelDownloadLimit";
    const K_OVERRIDE_FETCHED_AT: &str = "com.hyperwhisper.config.lastFetchTimestamp";

    const DAY: i64 = 86_400;
    const CACHE_SECS: i64 = 86_400;
    const GRACE_SECS: i64 = 604_800;

    /// The foreign `KeyValueStore` a platform would supply, plus raw read/write
    /// access so a test can inspect exactly what the bridge persisted.
    #[derive(Default)]
    struct TestStore {
        inner: Mutex<HashMap<String, String>>,
    }

    impl TestStore {
        fn raw(&self, key: &str) -> Option<String> {
            self.inner.lock().expect("poisoned").get(key).cloned()
        }

        fn seed(&self, key: &str, value: &str) {
            self.inner
                .lock()
                .expect("poisoned")
                .insert(key.to_string(), value.to_string());
        }
    }

    impl KeyValueStore for TestStore {
        fn get(&self, key: String) -> Option<String> {
            self.inner.lock().expect("poisoned").get(&key).cloned()
        }
        fn set(&self, key: String, value: String) {
            self.inner.lock().expect("poisoned").insert(key, value);
        }
        fn delete(&self, key: String) {
            self.inner.lock().expect("poisoned").remove(&key);
        }
    }

    fn store() -> Arc<TestStore> {
        Arc::new(TestStore::default())
    }

    /// The same store as the `Arc<dyn KeyValueStore>` the exports take.
    fn kv(s: &Arc<TestStore>) -> Arc<dyn KeyValueStore> {
        s.clone()
    }

    /// Write the validation cache the way the bridge's own adapter would: the
    /// foreign store through `KvAdapter`, the mirrored status through its
    /// `From` impl. The cache writer has no FFI export of its own (heads write
    /// a verdict through `license_persist_validation_verdict`), so the tests
    /// that need a cached validation seed it here.
    fn seed_validation_cache(s: &Arc<TestStore>, status: HwLicenseStatus, now_unix_secs: i64) {
        hw_license::cache::update_validation_cache(&KvAdapter(kv(s)), status.into(), now_unix_secs)
    }

    /// A distinct label per FFI status arm, written independently of the `From`
    /// impls under test. The exhaustive `match` also fails to compile if a new
    /// arm is added without a test.
    fn status_tag(s: &HwLicenseStatus) -> &'static str {
        match s {
            HwLicenseStatus::Trial => "trial",
            HwLicenseStatus::Active => "active",
            HwLicenseStatus::Expired => "expired",
            HwLicenseStatus::Invalid => "invalid",
        }
    }

    /// Build a fresh status from a tag — the mirrored enum is neither `Copy` nor
    /// `Clone`, so a loop over the four arms has to construct each one.
    fn status_from_tag(tag: &str) -> HwLicenseStatus {
        match tag {
            "trial" => HwLicenseStatus::Trial,
            "active" => HwLicenseStatus::Active,
            "expired" => HwLicenseStatus::Expired,
            "invalid" => HwLicenseStatus::Invalid,
            other => panic!("unknown status tag {other}"),
        }
    }

    const ALL_STATUS_TAGS: [&str; 4] = ["trial", "active", "expired", "invalid"];

    fn body(json: &str) -> Vec<u8> {
        json.as_bytes().to_vec()
    }

    // -----------------------------------------------------------------------
    // validate
    // -----------------------------------------------------------------------

    #[test]
    fn validate_url_is_the_production_endpoint() {
        assert_eq!(
            hw_license::validate::VALIDATE_URL,
            "https://www.hyperwhisper.com/api/license/validate"
        );
    }

    #[test]
    fn build_validate_request_puts_each_argument_in_its_own_json_field() {
        // Three distinct values, so any two arguments swapped on the way through
        // the bridge lands in the wrong field and fails here.
        let req = license_build_validate_request(
            "KEY-AAA".to_string(),
            "device-BBB".to_string(),
            "host-CCC".to_string(),
        );
        let text = String::from_utf8(req.body).expect("body is utf-8");
        assert_eq!(
            text,
            r#"{"license_key":"KEY-AAA","device_id":"device-BBB","device_name":"host-CCC"}"#
        );
        assert_eq!(req.url, hw_license::validate::VALIDATE_URL);
        assert_eq!(req.content_type, "application/json");
    }

    #[test]
    fn build_validate_request_trims_only_the_license_key() {
        let req = license_build_validate_request(
            "  KEY-AAA \n".to_string(),
            " device-BBB ".to_string(),
            " host CCC ".to_string(),
        );
        let text = String::from_utf8(req.body).expect("body is utf-8");
        assert!(text.contains(r#""license_key":"KEY-AAA""#), "{text}");
        // The device fields are platform-supplied and pass through verbatim.
        assert!(text.contains(r#""device_id":" device-BBB ""#), "{text}");
        assert!(text.contains(r#""device_name":" host CCC ""#), "{text}");
    }

    #[test]
    fn build_validate_request_escapes_a_quote_in_the_device_name() {
        let req = license_build_validate_request(
            "KEY".to_string(),
            "dev".to_string(),
            "Ray\"s Mac".to_string(),
        );
        let text = String::from_utf8(req.body).expect("body is utf-8");
        assert!(text.contains(r#""device_name":"Ray\"s Mac""#), "{text}");
        // Still one well-formed JSON object.
        let parsed: serde_json::Value = serde_json::from_str(&text).expect("valid json");
        assert_eq!(parsed["device_name"], "Ray\"s Mac");
    }

    #[test]
    fn empty_key_outcome_is_invalid_and_names_the_empty_key() {
        let outcome = license_empty_key_outcome();
        assert!(!outcome.is_valid);
        assert_eq!(status_tag(&outcome.status), "invalid");
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("License key cannot be empty")
        );
    }

    #[test]
    fn http_error_outcome_names_the_status_code_when_the_body_has_none() {
        let outcome = license_http_error_outcome(503, body("not json at all"));
        assert!(!outcome.is_valid);
        assert_eq!(status_tag(&outcome.status), "invalid");
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("Server error (HTTP 503)")
        );
    }

    #[test]
    fn http_error_outcome_prefers_the_servers_own_error_field() {
        let outcome =
            license_http_error_outcome(401, body(r#"{"error":"License key not recognised"}"#));
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("License key not recognised")
        );
    }

    #[test]
    fn parse_validate_response_maps_each_status_string_to_its_own_arm() {
        let cases = [
            (r#"{"status":"active"}"#, "active", true),
            (r#"{"status":"expired"}"#, "expired", false),
            (r#"{"status":"revoked"}"#, "invalid", false),
            (r#"{"status":"invalid"}"#, "invalid", false),
        ];
        for (json, expected, expected_valid) in cases {
            let outcome = license_parse_validate_response(body(json));
            assert_eq!(status_tag(&outcome.status), expected, "{json}");
            assert_eq!(outcome.is_valid, expected_valid, "{json}");
        }
    }

    #[test]
    fn parse_validate_response_falls_back_to_the_boolean_signals() {
        let valid = license_parse_validate_response(body(r#"{"valid":true}"#));
        assert_eq!(status_tag(&valid.status), "active");
        assert!(valid.is_valid);

        let expired = license_parse_validate_response(body(r#"{"valid":false,"expired":true}"#));
        assert_eq!(status_tag(&expired.status), "expired");
        assert!(!expired.is_valid);

        let neither = license_parse_validate_response(body(r#"{"valid":false}"#));
        assert_eq!(status_tag(&neither.status), "invalid");
    }

    #[test]
    fn parse_validate_response_carries_each_customer_field_to_its_own_slot() {
        // Distinct values per field: a swapped pair cannot pass.
        let outcome = license_parse_validate_response(body(
            r#"{"status":"expired","customer_id":"cus_111","customer_email":"a@example.com","expires_at":"2026-01-31T00:00:00Z","error":"Subscription lapsed"}"#,
        ));
        assert_eq!(outcome.customer_id.as_deref(), Some("cus_111"));
        assert_eq!(outcome.customer_email.as_deref(), Some("a@example.com"));
        assert_eq!(outcome.expires_at.as_deref(), Some("2026-01-31T00:00:00Z"));
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("Subscription lapsed")
        );
    }

    #[test]
    fn parse_validate_response_hides_the_error_field_on_an_active_licence() {
        let outcome = license_parse_validate_response(body(
            r#"{"status":"active","customer_id":"cus_222","error":"stale warning"}"#,
        ));
        assert!(outcome.is_valid);
        assert_eq!(outcome.error_message, None);
        assert_eq!(outcome.customer_id.as_deref(), Some("cus_222"));
    }

    #[test]
    fn parse_validate_response_on_a_non_json_body_is_invalid_not_a_panic() {
        let outcome = license_parse_validate_response(body("<html>502 Bad Gateway</html>"));
        assert!(!outcome.is_valid);
        assert_eq!(status_tag(&outcome.status), "invalid");
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("Invalid server response")
        );
    }

    // -----------------------------------------------------------------------
    // cache: constants + the two mapping directions
    // -----------------------------------------------------------------------

    #[test]
    fn cache_window_constants_match_their_documented_durations() {
        assert_eq!(hw_license::cache::VALIDATION_CACHE_SECS, CACHE_SECS);
        assert_eq!(hw_license::cache::OFFLINE_GRACE_SECS, GRACE_SECS);
    }

    #[test]
    fn update_validation_cache_persists_each_status_under_its_own_name() {
        // Pins the `HwLicenseStatus -> hw_license::LicenseStatus` direction
        // against the raw strings the platforms persist.
        let expected = [
            ("trial", "Trial"),
            ("active", "Active"),
            ("expired", "Expired"),
            ("invalid", "Invalid"),
        ];
        for (tag, persisted) in expected {
            let s = store();
            seed_validation_cache(&s, status_from_tag(tag), 1_700_000_000);
            assert_eq!(s.raw(K_CACHED_STATUS).as_deref(), Some(persisted), "{tag}");
            assert_eq!(
                s.raw(K_LAST_VALIDATION).as_deref(),
                Some("1700000000"),
                "{tag}"
            );
        }
    }

    #[test]
    fn cached_status_reads_each_persisted_name_back_to_its_own_arm() {
        // Pins the `hw_license::LicenseStatus -> HwLicenseStatus` direction from
        // a hand-seeded store, so a pair of arms swapped both ways still fails.
        let expected = [
            ("Trial", "trial"),
            ("Active", "active"),
            ("Expired", "expired"),
            ("Invalid", "invalid"),
        ];
        for (persisted, tag) in expected {
            let s = store();
            s.seed(K_CACHED_STATUS, persisted);
            s.seed(K_LAST_VALIDATION, "1000");
            let got = license_cached_status_within_grace(kv(&s), 1_500);
            assert_eq!(got.as_ref().map(status_tag), Some(tag), "{persisted}");
        }
    }

    #[test]
    fn an_unrecognised_persisted_status_is_treated_as_no_cache() {
        let s = store();
        s.seed(K_CACHED_STATUS, "Suspended");
        s.seed(K_LAST_VALIDATION, "1000");
        assert!(license_cached_status_within_grace(kv(&s), 1_500).is_none());
    }

    #[test]
    fn should_revalidate_flips_at_the_validation_cache_boundary() {
        let s = store();
        let t0 = 1_700_000_000;
        seed_validation_cache(&s, HwLicenseStatus::Active, t0);
        assert!(!license_should_revalidate(kv(&s), t0));
        assert!(!license_should_revalidate(kv(&s), t0 + CACHE_SECS));
        assert!(license_should_revalidate(kv(&s), t0 + CACHE_SECS + 1));
    }

    #[test]
    fn should_revalidate_with_no_cached_validation_or_a_backward_clock() {
        let s = store();
        assert!(license_should_revalidate(kv(&s), 1_700_000_000));

        let t0 = 1_700_000_000;
        seed_validation_cache(&s, HwLicenseStatus::Active, t0);
        // A clock correction that moves time backwards must not make the cache
        // look permanently fresh.
        assert!(license_should_revalidate(kv(&s), t0 - 1));
    }

    #[test]
    fn cached_status_expires_at_the_offline_grace_boundary() {
        let s = store();
        let t0 = 1_700_000_000;
        seed_validation_cache(&s, HwLicenseStatus::Active, t0);
        assert!(license_cached_status_within_grace(kv(&s), t0 + GRACE_SECS).is_some());
        assert!(license_cached_status_within_grace(kv(&s), t0 + GRACE_SECS + 1).is_none());
    }

    // -----------------------------------------------------------------------
    // cache: stored key + verdict persistence
    // -----------------------------------------------------------------------

    #[test]
    fn stored_licence_key_ignores_blank_values() {
        let s = store();
        assert!(license_stored_license_key(kv(&s)).is_none());

        hw_license::cache::store_license_key(&KvAdapter(kv(&s)), "   ");
        assert!(s.raw(K_LICENSE_KEY).is_none());

        hw_license::cache::store_license_key(&KvAdapter(kv(&s)), "HW-KEY-1");
        assert_eq!(
            license_stored_license_key(kv(&s)).as_deref(),
            Some("HW-KEY-1")
        );
    }

    #[test]
    fn persist_verdict_stores_the_key_only_for_an_active_licence() {
        for tag in ALL_STATUS_TAGS {
            let s = store();
            license_persist_validation_verdict(
                kv(&s),
                status_from_tag(tag),
                "HW-KEY-1".to_string(),
                1_000,
            );
            if tag == "active" {
                assert_eq!(s.raw(K_LICENSE_KEY).as_deref(), Some("HW-KEY-1"));
                assert_eq!(s.raw(K_CACHED_STATUS).as_deref(), Some("Active"));
            } else {
                assert!(s.raw(K_LICENSE_KEY).is_none(), "{tag}");
                // No stored key to re-validate, so nothing is cached either.
                assert!(s.raw(K_CACHED_STATUS).is_none(), "{tag}");
            }
        }
    }

    #[test]
    fn a_rejected_replacement_key_never_clobbers_the_stored_keys_verdict() {
        // The entitlement guard this wrapper exists for: a valid user who types a
        // wrong second key must not be locked out for the 24h cache window.
        let s = store();
        license_persist_validation_verdict(
            kv(&s),
            HwLicenseStatus::Active,
            "HW-GOOD".to_string(),
            1_000,
        );
        license_persist_validation_verdict(
            kv(&s),
            HwLicenseStatus::Invalid,
            "HW-TYPO".to_string(),
            2_000,
        );
        assert_eq!(s.raw(K_LICENSE_KEY).as_deref(), Some("HW-GOOD"));
        assert_eq!(s.raw(K_CACHED_STATUS).as_deref(), Some("Active"));
        assert_eq!(s.raw(K_LAST_VALIDATION).as_deref(), Some("1000"));
    }

    #[test]
    fn a_revoked_verdict_for_the_stored_key_does_update_the_cache() {
        let s = store();
        license_persist_validation_verdict(
            kv(&s),
            HwLicenseStatus::Active,
            "HW-GOOD".to_string(),
            1_000,
        );
        // Same key, trimmed the way the platforms trim it before sending.
        license_persist_validation_verdict(
            kv(&s),
            HwLicenseStatus::Expired,
            "  HW-GOOD  ".to_string(),
            2_000,
        );
        assert_eq!(s.raw(K_CACHED_STATUS).as_deref(), Some("Expired"));
        assert_eq!(s.raw(K_LAST_VALIDATION).as_deref(), Some("2000"));
    }

    #[test]
    fn clear_stored_license_drops_the_licence_state_but_keeps_the_remote_override() {
        let s = store();
        license_persist_validation_verdict(
            kv(&s),
            HwLicenseStatus::Active,
            "HW-GOOD".to_string(),
            1_000,
        );
        s.seed(K_CUSTOMER_ID, "cus_333");
        s.seed(K_OVERRIDE_DAILY, "900");
        s.seed(K_OVERRIDE_MODELS, "7");
        s.seed(K_OVERRIDE_FETCHED_AT, "1000");

        license_clear_stored_license(kv(&s));

        assert!(s.raw(K_LICENSE_KEY).is_none());
        assert!(s.raw(K_CUSTOMER_ID).is_none());
        assert!(s.raw(K_LAST_VALIDATION).is_none());
        assert!(s.raw(K_CACHED_STATUS).is_none());
        // The config override is not the user's licence — it survives.
        assert_eq!(s.raw(K_OVERRIDE_DAILY).as_deref(), Some("900"));
        assert_eq!(s.raw(K_OVERRIDE_MODELS).as_deref(), Some("7"));
        assert_eq!(s.raw(K_OVERRIDE_FETCHED_AT).as_deref(), Some("1000"));
    }

    // -----------------------------------------------------------------------
    // cache: offline fallback
    // -----------------------------------------------------------------------

    #[test]
    fn offline_fallback_uses_the_cached_status_and_customer_within_grace() {
        let s = store();
        let t0 = 1_700_000_000;
        seed_validation_cache(&s, HwLicenseStatus::Active, t0);
        s.seed(K_CUSTOMER_ID, "cus_444");

        let outcome = license_offline_fallback_outcome(kv(&s), t0 + DAY);
        assert!(outcome.is_valid);
        assert_eq!(status_tag(&outcome.status), "active");
        assert_eq!(outcome.customer_id.as_deref(), Some("cus_444"));
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("Using cached license (offline)")
        );
    }

    #[test]
    fn offline_fallback_for_a_cached_non_active_status_is_not_valid() {
        let s = store();
        let t0 = 1_700_000_000;
        seed_validation_cache(&s, HwLicenseStatus::Expired, t0);

        let outcome = license_offline_fallback_outcome(kv(&s), t0 + DAY);
        assert!(!outcome.is_valid);
        assert_eq!(status_tag(&outcome.status), "expired");
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("Using cached license (offline)")
        );
    }

    #[test]
    fn offline_fallback_past_the_grace_period_is_invalid_with_no_customer() {
        let s = store();
        let t0 = 1_700_000_000;
        seed_validation_cache(&s, HwLicenseStatus::Active, t0);
        s.seed(K_CUSTOMER_ID, "cus_555");

        let outcome = license_offline_fallback_outcome(kv(&s), t0 + GRACE_SECS + 1);
        assert!(!outcome.is_valid);
        assert_eq!(status_tag(&outcome.status), "invalid");
        assert_eq!(outcome.customer_id, None);
        assert_eq!(
            outcome.error_message.as_deref(),
            Some("Offline and no cached license")
        );
    }
}
