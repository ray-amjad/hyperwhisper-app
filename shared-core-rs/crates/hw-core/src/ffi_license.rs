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
