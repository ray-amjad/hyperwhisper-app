//! Trial usage tracking + limit enforcement, persisted through the platform's
//! [`KeyValueStore`].
//!
//! No clock in Rust: the daily-usage bucket is keyed off a *day index* derived
//! from `now_unix_secs` passed in by the platform, so a fresh day automatically
//! resets daily seconds. This is the deterministic, golden-testable analogue of
//! the platform's "reset at midnight" timer.
//!
//! Parity target: macOS `LicenseUsageTracker` + `PersistenceController`
//! (`getDailyUsage`/`updateDailyUsage` self-reset when `lastResetDate` is not
//! today) and Windows `LicenseUsageTracker`.
//!
//! Divergence note: the platforms reset at *local* calendar midnight
//! (`Calendar.isDateInToday`). Rust has no calendar/timezone, so the shared core
//! buckets by **UTC day** (`now_unix_secs / 86400`). The platform is expected to
//! pass a `now` already offset to its local day if it needs local-midnight
//! semantics; absent that, UTC-day boundaries apply. Documented so call sites can
//! choose the offset.

use crate::{KeyValueStore, LicenseStatus};

/// Release-build default daily trial limit (5 minutes). Mirrors macOS/Windows
/// `trialDailyLimitSeconds = 300` in release.
pub const DEFAULT_DAILY_LIMIT_RELEASE: i64 = 300;
/// Debug-build default daily trial limit (30 minutes). Mirrors the `#if DEBUG`
/// value `1800`.
pub const DEFAULT_DAILY_LIMIT_DEBUG: i64 = 1800;
/// Default trial model-download limit. Mirrors `trialModelLimit = 3`.
pub const DEFAULT_MODEL_LIMIT: i64 = 3;

const SECS_PER_DAY: i64 = 86_400;

const K_DAILY_SECONDS: &str = "com.hyperwhisper.usage.dailySeconds";
const K_DAILY_DAY_INDEX: &str = "com.hyperwhisper.usage.dayIndex";
const K_MODELS_DOWNLOADED: &str = "com.hyperwhisper.usage.modelsDownloaded";

/// The effective trial limits in force (defaults, or a remote override applied by
/// the caller via [`crate::cache::remote_override_if_fresh`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Limits {
    pub daily_seconds: i64,
    pub model_downloads: i64,
}

impl Limits {
    /// The hardcoded defaults for the given build flavor.
    pub fn defaults(debug_build: bool) -> Self {
        Self {
            daily_seconds: if debug_build {
                DEFAULT_DAILY_LIMIT_DEBUG
            } else {
                DEFAULT_DAILY_LIMIT_RELEASE
            },
            model_downloads: DEFAULT_MODEL_LIMIT,
        }
    }
}

/// A snapshot of current usage vs. the active limits.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct UsageSnapshot {
    pub daily_seconds_used: i64,
    pub models_downloaded: i64,
    /// `true` once the daily seconds limit is reached (trial only).
    pub daily_limit_reached: bool,
    /// `true` once the model-download limit is reached (trial only).
    pub model_limit_reached: bool,
    /// Remaining daily seconds (`i64::MAX` for licensed users).
    pub remaining_daily_seconds: i64,
    /// Remaining model downloads (`i64::MAX` for licensed users).
    pub remaining_model_downloads: i64,
}

/// The day index (UTC days since epoch) for a given instant. See the module note
/// on the local-vs-UTC reset divergence.
fn day_index(now_unix_secs: i64) -> i64 {
    now_unix_secs.div_euclid(SECS_PER_DAY)
}

/// Read the stored daily seconds, auto-resetting to 0 if the stored day index is
/// not today's. Returns the (possibly reset) current daily seconds; when a reset
/// occurs the store is updated so the reset is durable (mirrors the platform's
/// self-resetting `getDailyUsage`).
fn current_daily_seconds(store: &dyn KeyValueStore, now_unix_secs: i64) -> i64 {
    let today = day_index(now_unix_secs);
    let stored_day = store
        .get(K_DAILY_DAY_INDEX.to_string())
        .and_then(|s| s.parse::<i64>().ok());
    match stored_day {
        Some(d) if d == today => store
            .get(K_DAILY_SECONDS.to_string())
            .and_then(|s| s.parse::<i64>().ok())
            .unwrap_or(0),
        _ => {
            // New day (or no record): reset durably.
            store.set(K_DAILY_SECONDS.to_string(), "0".to_string());
            store.set(K_DAILY_DAY_INDEX.to_string(), today.to_string());
            0
        }
    }
}

fn current_models_downloaded(store: &dyn KeyValueStore) -> i64 {
    store
        .get(K_MODELS_DOWNLOADED.to_string())
        .and_then(|s| s.parse::<i64>().ok())
        .unwrap_or(0)
}

/// Record `seconds` of transcription usage against today's bucket. No-op for
/// `seconds <= 0`. Performs a day-boundary reset first if needed. Mirrors macOS
/// `recordTranscriptionTime` / `updateDailyUsage`.
pub fn record_usage(store: &dyn KeyValueStore, seconds: i64, now_unix_secs: i64) {
    if seconds <= 0 {
        return;
    }
    let current = current_daily_seconds(store, now_unix_secs);
    let updated = current.saturating_add(seconds);
    store.set(K_DAILY_SECONDS.to_string(), updated.to_string());
    // current_daily_seconds already wrote today's day index.
}

/// Increment the lifetime model-download count by one. Mirrors macOS
/// `incrementModelDownloadCount`.
pub fn record_model_download(store: &dyn KeyValueStore) {
    let updated = current_models_downloaded(store).saturating_add(1);
    store.set(K_MODELS_DOWNLOADED.to_string(), updated.to_string());
}

/// Compute the current usage snapshot vs `limits` for `status` at `now`.
///
/// Licensed (`Active`) users have no limits: both `*_reached` flags are `false`
/// and both `remaining_*` are `i64::MAX` (the analogue of macOS' `Int.max`).
/// Trial / Expired / Invalid users are subject to `limits`.
///
/// Reading the snapshot performs the same day-boundary reset as `record_usage`,
/// so calling `check_limits` after midnight surfaces a fresh daily counter even
/// if no usage was recorded.
pub fn check_limits(
    store: &dyn KeyValueStore,
    status: LicenseStatus,
    limits: Limits,
    now_unix_secs: i64,
) -> UsageSnapshot {
    let daily = current_daily_seconds(store, now_unix_secs);
    let models = current_models_downloaded(store);

    // Only Active licenses bypass limits — matches the platforms, which check
    // `licenseStatus == .active` (Expired/Invalid fall back to trial limits).
    let unlimited = status == LicenseStatus::Active;

    if unlimited {
        UsageSnapshot {
            daily_seconds_used: daily,
            models_downloaded: models,
            daily_limit_reached: false,
            model_limit_reached: false,
            remaining_daily_seconds: i64::MAX,
            remaining_model_downloads: i64::MAX,
        }
    } else {
        UsageSnapshot {
            daily_seconds_used: daily,
            models_downloaded: models,
            daily_limit_reached: daily >= limits.daily_seconds,
            model_limit_reached: models >= limits.model_downloads,
            remaining_daily_seconds: (limits.daily_seconds - daily).max(0),
            remaining_model_downloads: (limits.model_downloads - models).max(0),
        }
    }
}

/// Whether a new recording may start. Licensed users: always `true`. Trial users:
/// `true` while under the daily limit. Mirrors macOS `canStartRecording`.
pub fn can_start_recording(
    store: &dyn KeyValueStore,
    status: LicenseStatus,
    limits: Limits,
    now_unix_secs: i64,
) -> bool {
    !check_limits(store, status, limits, now_unix_secs).daily_limit_reached
}

/// Whether another model may be downloaded. Licensed users: always `true`. Trial
/// users: `true` while under the model limit. Mirrors macOS `canDownloadModel`.
pub fn can_download_model(
    store: &dyn KeyValueStore,
    status: LicenseStatus,
    limits: Limits,
) -> bool {
    if status == LicenseStatus::Active {
        return true;
    }
    current_models_downloaded(store) < limits.model_downloads
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::MemoryStore;

    const DAY: i64 = 86_400;

    fn store() -> MemoryStore {
        MemoryStore::new()
    }

    fn release_limits() -> Limits {
        Limits::defaults(false)
    }

    #[test]
    fn defaults_match_build_flavor() {
        assert_eq!(Limits::defaults(false).daily_seconds, 300);
        assert_eq!(Limits::defaults(true).daily_seconds, 1800);
        assert_eq!(Limits::defaults(false).model_downloads, 3);
    }

    #[test]
    fn record_and_check_daily_usage() {
        let s = store();
        let now = 10 * DAY + 500; // some instant on day 10
        record_usage(&s, 120, now);
        record_usage(&s, 100, now + 60);
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), now + 120);
        assert_eq!(snap.daily_seconds_used, 220);
        assert_eq!(snap.remaining_daily_seconds, 80);
        assert!(!snap.daily_limit_reached);
    }

    #[test]
    fn daily_limit_enforced_at_boundary() {
        let s = store();
        let now = 10 * DAY;
        record_usage(&s, 300, now); // exactly the release limit
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), now);
        assert!(snap.daily_limit_reached);
        assert_eq!(snap.remaining_daily_seconds, 0);
        assert!(!can_start_recording(&s, LicenseStatus::Trial, release_limits(), now));
    }

    #[test]
    fn daily_usage_resets_at_day_boundary() {
        let s = store();
        let day10 = 10 * DAY + 100;
        record_usage(&s, 300, day10);
        assert!(!can_start_recording(
            &s,
            LicenseStatus::Trial,
            release_limits(),
            day10
        ));
        // Next UTC day → counter resets, recording allowed again.
        let day11 = 11 * DAY + 100;
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), day11);
        assert_eq!(snap.daily_seconds_used, 0);
        assert!(can_start_recording(
            &s,
            LicenseStatus::Trial,
            release_limits(),
            day11
        ));
    }

    #[test]
    fn day_reset_is_durable() {
        let s = store();
        let day10 = 10 * DAY + 100;
        record_usage(&s, 250, day10);
        // Crossing into day 11 via a read should persist the reset day index, so
        // a subsequent record on day 11 starts from 0.
        let _ = check_limits(&s, LicenseStatus::Trial, release_limits(), 11 * DAY);
        record_usage(&s, 40, 11 * DAY + 10);
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), 11 * DAY + 20);
        assert_eq!(snap.daily_seconds_used, 40);
    }

    #[test]
    fn model_downloads_enforced() {
        let s = store();
        let now = 0;
        assert!(can_download_model(&s, LicenseStatus::Trial, release_limits()));
        record_model_download(&s);
        record_model_download(&s);
        record_model_download(&s);
        assert!(!can_download_model(&s, LicenseStatus::Trial, release_limits()));
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), now);
        assert!(snap.model_limit_reached);
        assert_eq!(snap.remaining_model_downloads, 0);
    }

    #[test]
    fn active_license_is_unlimited() {
        let s = store();
        let now = 0;
        record_usage(&s, 10_000, now);
        for _ in 0..10 {
            record_model_download(&s);
        }
        let snap = check_limits(&s, LicenseStatus::Active, release_limits(), now);
        assert!(!snap.daily_limit_reached);
        assert!(!snap.model_limit_reached);
        assert_eq!(snap.remaining_daily_seconds, i64::MAX);
        assert_eq!(snap.remaining_model_downloads, i64::MAX);
        assert!(can_start_recording(&s, LicenseStatus::Active, release_limits(), now));
        assert!(can_download_model(&s, LicenseStatus::Active, release_limits()));
    }

    #[test]
    fn expired_license_is_limited_like_trial() {
        let s = store();
        let now = 0;
        record_usage(&s, 300, now);
        let snap = check_limits(&s, LicenseStatus::Expired, release_limits(), now);
        assert!(snap.daily_limit_reached);
    }

    #[test]
    fn remote_override_applied_via_limits() {
        let s = store();
        let now = 0;
        let override_limits = Limits {
            daily_seconds: 600,
            model_downloads: 5,
        };
        record_usage(&s, 400, now);
        let snap = check_limits(&s, LicenseStatus::Trial, override_limits, now);
        assert!(!snap.daily_limit_reached);
        assert_eq!(snap.remaining_daily_seconds, 200);
    }

    #[test]
    fn zero_or_negative_usage_is_noop() {
        let s = store();
        record_usage(&s, 0, 0);
        record_usage(&s, -5, 0);
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), 0);
        assert_eq!(snap.daily_seconds_used, 0);
    }
}
