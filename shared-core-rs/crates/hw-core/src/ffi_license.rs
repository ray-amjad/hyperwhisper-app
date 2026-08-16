//! UniFFI surface for the M3 license/usage core (`hw_license`).
//!
//! Mirrors the public records/enums and exposes the validate/cache/usage
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

/// Remote trial-limit override. Mirrors `hw_license::cache::TrialLimits`.
#[derive(uniffi::Record)]
pub struct TrialLimits {
    pub daily_seconds: i64,
    pub model_downloads: i64,
}

impl From<TrialLimits> for hw_license::cache::TrialLimits {
    fn from(t: TrialLimits) -> Self {
        hw_license::cache::TrialLimits {
            daily_seconds: t.daily_seconds,
            model_downloads: t.model_downloads,
        }
    }
}

impl From<hw_license::cache::TrialLimits> for TrialLimits {
    fn from(t: hw_license::cache::TrialLimits) -> Self {
        TrialLimits {
            daily_seconds: t.daily_seconds,
            model_downloads: t.model_downloads,
        }
    }
}

/// Active usage limits. Mirrors `hw_license::usage::Limits`.
#[derive(uniffi::Record)]
pub struct Limits {
    pub daily_seconds: i64,
    pub model_downloads: i64,
}

impl From<Limits> for hw_license::usage::Limits {
    fn from(l: Limits) -> Self {
        hw_license::usage::Limits {
            daily_seconds: l.daily_seconds,
            model_downloads: l.model_downloads,
        }
    }
}

/// A snapshot of current usage vs. the active limits. Mirrors
/// `hw_license::usage::UsageSnapshot`.
#[derive(uniffi::Record)]
pub struct UsageSnapshot {
    pub daily_seconds_used: i64,
    pub models_downloaded: i64,
    pub daily_limit_reached: bool,
    pub model_limit_reached: bool,
    pub remaining_daily_seconds: i64,
    pub remaining_model_downloads: i64,
}

impl From<hw_license::usage::UsageSnapshot> for UsageSnapshot {
    fn from(s: hw_license::usage::UsageSnapshot) -> Self {
        UsageSnapshot {
            daily_seconds_used: s.daily_seconds_used,
            models_downloaded: s.models_downloaded,
            daily_limit_reached: s.daily_limit_reached,
            model_limit_reached: s.model_limit_reached,
            remaining_daily_seconds: s.remaining_daily_seconds,
            remaining_model_downloads: s.remaining_model_downloads,
        }
    }
}

// ===========================================================================
// validate
// ===========================================================================

/// The `/api/license/validate` endpoint URL.
#[uniffi::export]
pub fn license_validate_url() -> String {
    hw_license::validate::VALIDATE_URL.to_string()
}

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

/// 24h validation cache TTL (seconds).
#[uniffi::export]
pub fn license_validation_cache_secs() -> i64 {
    hw_license::cache::VALIDATION_CACHE_SECS
}

/// 7-day offline grace period (seconds).
#[uniffi::export]
pub fn license_offline_grace_secs() -> i64 {
    hw_license::cache::OFFLINE_GRACE_SECS
}

/// 24h remote-override TTL (seconds).
#[uniffi::export]
pub fn license_remote_override_ttl_secs() -> i64 {
    hw_license::cache::REMOTE_OVERRIDE_TTL_SECS
}

#[uniffi::export]
pub fn license_store_license_key(store: Arc<dyn KeyValueStore>, key: String) {
    hw_license::cache::store_license_key(&KvAdapter(store), &key)
}

#[uniffi::export]
pub fn license_stored_license_key(store: Arc<dyn KeyValueStore>) -> Option<String> {
    hw_license::cache::stored_license_key(&KvAdapter(store))
}

#[uniffi::export]
pub fn license_update_validation_cache(
    store: Arc<dyn KeyValueStore>,
    status: HwLicenseStatus,
    now_unix_secs: i64,
) {
    hw_license::cache::update_validation_cache(&KvAdapter(store), status.into(), now_unix_secs)
}

/// Persist a server validation verdict for the attempted key: stores the key on
/// a valid (`Active`) verdict, and updates the validation cache only when the
/// verdict is valid or the attempted key matches the stored key. Prefer this
/// over the raw `license_update_validation_cache` after a validate response —
/// it prevents a rejected *replacement* key from clobbering the stored key's
/// cached status (a 24h lockout for a valid user).
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

#[uniffi::export]
pub fn license_store_remote_override(
    store: Arc<dyn KeyValueStore>,
    limits: TrialLimits,
    now_unix_secs: i64,
) {
    hw_license::cache::store_remote_override(&KvAdapter(store), limits.into(), now_unix_secs)
}

#[uniffi::export]
pub fn license_remote_override_if_fresh(
    store: Arc<dyn KeyValueStore>,
    now_unix_secs: i64,
) -> Option<TrialLimits> {
    hw_license::cache::remote_override_if_fresh(&KvAdapter(store), now_unix_secs).map(Into::into)
}

// ===========================================================================
// usage (constants + Limits::defaults + store-taking fns)
// ===========================================================================

/// Default daily seconds limit for release builds.
#[uniffi::export]
pub fn license_default_daily_limit_release() -> i64 {
    hw_license::usage::DEFAULT_DAILY_LIMIT_RELEASE
}

/// Default daily seconds limit for debug builds.
#[uniffi::export]
pub fn license_default_daily_limit_debug() -> i64 {
    hw_license::usage::DEFAULT_DAILY_LIMIT_DEBUG
}

/// Default model-download limit.
#[uniffi::export]
pub fn license_default_model_limit() -> i64 {
    hw_license::usage::DEFAULT_MODEL_LIMIT
}

/// The hardcoded default limits for the given build flavor.
#[uniffi::export]
pub fn license_limits_defaults(debug_build: bool) -> Limits {
    let l = hw_license::usage::Limits::defaults(debug_build);
    Limits {
        daily_seconds: l.daily_seconds,
        model_downloads: l.model_downloads,
    }
}

#[uniffi::export]
pub fn license_record_usage(store: Arc<dyn KeyValueStore>, seconds: i64, now_unix_secs: i64) {
    hw_license::usage::record_usage(&KvAdapter(store), seconds, now_unix_secs)
}

#[uniffi::export]
pub fn license_record_model_download(store: Arc<dyn KeyValueStore>) {
    hw_license::usage::record_model_download(&KvAdapter(store))
}

#[uniffi::export]
pub fn license_check_limits(
    store: Arc<dyn KeyValueStore>,
    status: HwLicenseStatus,
    limits: Limits,
    now_unix_secs: i64,
) -> UsageSnapshot {
    hw_license::usage::check_limits(&KvAdapter(store), status.into(), limits.into(), now_unix_secs)
        .into()
}

#[uniffi::export]
pub fn license_can_start_recording(
    store: Arc<dyn KeyValueStore>,
    status: HwLicenseStatus,
    limits: Limits,
    now_unix_secs: i64,
) -> bool {
    hw_license::usage::can_start_recording(
        &KvAdapter(store),
        status.into(),
        limits.into(),
        now_unix_secs,
    )
}

#[uniffi::export]
pub fn license_can_download_model(
    store: Arc<dyn KeyValueStore>,
    status: HwLicenseStatus,
    limits: Limits,
) -> bool {
    hw_license::usage::can_download_model(&KvAdapter(store), status.into(), limits.into())
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
    const K_OVERRIDE_MAX_AGE: &str = "com.hyperwhisper.config.maxAgeSecs";

    const DAY: i64 = 86_400;
    const CACHE_SECS: i64 = 86_400;
    const GRACE_SECS: i64 = 604_800;
    const OVERRIDE_DEFAULT_TTL: i64 = 21_600;

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

    fn limits(daily_seconds: i64, model_downloads: i64) -> Limits {
        Limits {
            daily_seconds,
            model_downloads,
        }
    }

    fn body(json: &str) -> Vec<u8> {
        json.as_bytes().to_vec()
    }

    // -----------------------------------------------------------------------
    // validate
    // -----------------------------------------------------------------------

    #[test]
    fn validate_url_is_the_production_endpoint() {
        assert_eq!(
            license_validate_url(),
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
        assert_eq!(req.url, license_validate_url());
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
        assert_eq!(license_validation_cache_secs(), CACHE_SECS);
        assert_eq!(license_offline_grace_secs(), GRACE_SECS);
        assert_eq!(license_remote_override_ttl_secs(), 86_400);
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
            license_update_validation_cache(kv(&s), status_from_tag(tag), 1_700_000_000);
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
        license_update_validation_cache(kv(&s), HwLicenseStatus::Active, t0);
        assert!(!license_should_revalidate(kv(&s), t0));
        assert!(!license_should_revalidate(kv(&s), t0 + CACHE_SECS));
        assert!(license_should_revalidate(kv(&s), t0 + CACHE_SECS + 1));
    }

    #[test]
    fn should_revalidate_with_no_cached_validation_or_a_backward_clock() {
        let s = store();
        assert!(license_should_revalidate(kv(&s), 1_700_000_000));

        let t0 = 1_700_000_000;
        license_update_validation_cache(kv(&s), HwLicenseStatus::Active, t0);
        // A clock correction that moves time backwards must not make the cache
        // look permanently fresh.
        assert!(license_should_revalidate(kv(&s), t0 - 1));
    }

    #[test]
    fn cached_status_expires_at_the_offline_grace_boundary() {
        let s = store();
        let t0 = 1_700_000_000;
        license_update_validation_cache(kv(&s), HwLicenseStatus::Active, t0);
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

        license_store_license_key(kv(&s), "   ".to_string());
        assert!(s.raw(K_LICENSE_KEY).is_none());

        license_store_license_key(kv(&s), "HW-KEY-1".to_string());
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
        license_store_remote_override(
            kv(&s),
            TrialLimits {
                daily_seconds: 900,
                model_downloads: 7,
            },
            1_000,
        );

        license_clear_stored_license(kv(&s));

        assert!(s.raw(K_LICENSE_KEY).is_none());
        assert!(s.raw(K_CUSTOMER_ID).is_none());
        assert!(s.raw(K_LAST_VALIDATION).is_none());
        assert!(s.raw(K_CACHED_STATUS).is_none());
        // The config override is not the user's licence — it survives.
        assert!(license_remote_override_if_fresh(kv(&s), 1_000).is_some());
    }

    // -----------------------------------------------------------------------
    // cache: offline fallback
    // -----------------------------------------------------------------------

    #[test]
    fn offline_fallback_uses_the_cached_status_and_customer_within_grace() {
        let s = store();
        let t0 = 1_700_000_000;
        license_update_validation_cache(kv(&s), HwLicenseStatus::Active, t0);
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
        license_update_validation_cache(kv(&s), HwLicenseStatus::Expired, t0);

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
        license_update_validation_cache(kv(&s), HwLicenseStatus::Active, t0);
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

    // -----------------------------------------------------------------------
    // cache: remote trial-limit override
    // -----------------------------------------------------------------------

    #[test]
    fn store_remote_override_writes_each_field_to_its_own_key() {
        // Asymmetric values: swapping the two fields cannot pass.
        let s = store();
        license_store_remote_override(
            kv(&s),
            TrialLimits {
                daily_seconds: 900,
                model_downloads: 7,
            },
            1_700_000_000,
        );
        assert_eq!(s.raw(K_OVERRIDE_DAILY).as_deref(), Some("900"));
        assert_eq!(s.raw(K_OVERRIDE_MODELS).as_deref(), Some("7"));
        assert_eq!(s.raw(K_OVERRIDE_FETCHED_AT).as_deref(), Some("1700000000"));
    }

    #[test]
    fn remote_override_reads_each_field_back_into_its_own_slot() {
        let s = store();
        s.seed(K_OVERRIDE_DAILY, "1200");
        s.seed(K_OVERRIDE_MODELS, "5");
        s.seed(K_OVERRIDE_FETCHED_AT, "1000");

        let got = license_remote_override_if_fresh(kv(&s), 2_000).expect("fresh override");
        assert_eq!(got.daily_seconds, 1200);
        assert_eq!(got.model_downloads, 5);
    }

    #[test]
    fn remote_override_expires_at_the_default_ttl_when_no_max_age_is_stored() {
        let s = store();
        let t0 = 1_700_000_000;
        license_store_remote_override(
            kv(&s),
            TrialLimits {
                daily_seconds: 900,
                model_downloads: 7,
            },
            t0,
        );
        assert!(license_remote_override_if_fresh(kv(&s), t0 + OVERRIDE_DEFAULT_TTL).is_some());
        assert!(license_remote_override_if_fresh(kv(&s), t0 + OVERRIDE_DEFAULT_TTL + 1).is_none());
    }

    #[test]
    fn a_stored_max_age_extends_the_override_but_is_clamped_to_the_24h_cap() {
        let s = store();
        let t0 = 1_700_000_000;
        license_store_remote_override(
            kv(&s),
            TrialLimits {
                daily_seconds: 900,
                model_downloads: 7,
            },
            t0,
        );
        // A server max-age longer than the cap cannot pin a stale override.
        s.seed(K_OVERRIDE_MAX_AGE, "999999");
        let cap = license_remote_override_ttl_secs();
        assert!(license_remote_override_if_fresh(kv(&s), t0 + cap).is_some());
        assert!(license_remote_override_if_fresh(kv(&s), t0 + cap + 1).is_none());
    }

    #[test]
    fn remote_override_rejects_a_backward_clock() {
        let s = store();
        let t0 = 1_700_000_000;
        license_store_remote_override(
            kv(&s),
            TrialLimits {
                daily_seconds: 900,
                model_downloads: 7,
            },
            t0,
        );
        assert!(license_remote_override_if_fresh(kv(&s), t0 - 1).is_none());
    }

    // -----------------------------------------------------------------------
    // usage
    // -----------------------------------------------------------------------

    #[test]
    fn default_limit_constants_are_exposed_unswapped() {
        assert_eq!(license_default_daily_limit_release(), 300);
        assert_eq!(license_default_daily_limit_debug(), 1800);
        assert_eq!(license_default_model_limit(), 3);
    }

    #[test]
    fn limits_defaults_follow_the_build_flavor_flag() {
        let release = license_limits_defaults(false);
        assert_eq!(release.daily_seconds, 300);
        assert_eq!(release.model_downloads, 3);

        let debug = license_limits_defaults(true);
        assert_eq!(debug.daily_seconds, 1800);
        assert_eq!(debug.model_downloads, 3);
    }

    #[test]
    fn the_snapshot_counts_seconds_and_downloads_in_separate_slots() {
        let s = store();
        let now = 10 * DAY + 500;
        license_record_usage(kv(&s), 42, now);
        license_record_model_download(kv(&s));
        license_record_model_download(kv(&s));

        let snap = license_check_limits(kv(&s), HwLicenseStatus::Trial, limits(300, 3), now);
        assert_eq!(snap.daily_seconds_used, 42);
        assert_eq!(snap.models_downloaded, 2);
    }

    #[test]
    fn record_usage_ignores_non_positive_seconds() {
        let s = store();
        let now = 10 * DAY + 500;
        license_record_usage(kv(&s), 0, now);
        license_record_usage(kv(&s), -30, now);
        let snap = license_check_limits(kv(&s), HwLicenseStatus::Trial, limits(300, 3), now);
        assert_eq!(snap.daily_seconds_used, 0);
    }

    #[test]
    fn local_usage_is_unlimited_for_every_status() {
        // Local transcription is free and unlimited (open source); the paid moat
        // is HyperWhisper Cloud, enforced server-side. A status or limits value
        // that starts gating locally is a regression, not a feature.
        let now = 10 * DAY + 500;
        for tag in ALL_STATUS_TAGS {
            let s = store();
            license_record_usage(kv(&s), 100_000, now);
            for _ in 0..20 {
                license_record_model_download(kv(&s));
            }
            let snap = license_check_limits(kv(&s), status_from_tag(tag), limits(1, 0), now);
            assert!(!snap.daily_limit_reached, "{tag}");
            assert!(!snap.model_limit_reached, "{tag}");
            assert_eq!(snap.remaining_daily_seconds, i64::MAX, "{tag}");
            assert_eq!(snap.remaining_model_downloads, i64::MAX, "{tag}");
            assert!(
                license_can_start_recording(kv(&s), status_from_tag(tag), limits(1, 0), now),
                "{tag}"
            );
            assert!(
                license_can_download_model(kv(&s), status_from_tag(tag), limits(1, 0)),
                "{tag}"
            );
        }
    }

    #[test]
    fn daily_seconds_reset_at_the_utc_day_boundary_but_downloads_do_not() {
        let s = store();
        license_record_usage(kv(&s), 250, 10 * DAY + 100);
        license_record_model_download(kv(&s));

        let snap = license_check_limits(
            kv(&s),
            HwLicenseStatus::Trial,
            limits(300, 3),
            11 * DAY + 100,
        );
        assert_eq!(snap.daily_seconds_used, 0);
        // The download count is a lifetime counter, not a daily one.
        assert_eq!(snap.models_downloaded, 1);
    }

    #[test]
    fn the_day_reset_is_durable_across_calls() {
        let s = store();
        license_record_usage(kv(&s), 250, 10 * DAY + 100);
        // Reading on the next day must persist the reset, so a later record on
        // that day starts from zero rather than from 250.
        let _ = license_check_limits(kv(&s), HwLicenseStatus::Trial, limits(300, 3), 11 * DAY);
        license_record_usage(kv(&s), 40, 11 * DAY + 10);

        let snap = license_check_limits(
            kv(&s),
            HwLicenseStatus::Trial,
            limits(300, 3),
            11 * DAY + 20,
        );
        assert_eq!(snap.daily_seconds_used, 40);
    }
}
