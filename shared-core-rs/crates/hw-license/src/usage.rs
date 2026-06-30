//! Local usage tracking, persisted through the platform's [`KeyValueStore`].
//!
//! **Local limits are removed (HyperWhisper is open source).** All local,
//! on-device transcription and model downloads are unconditionally free and
//! unlimited: `check_limits` reports unlimited for every status and
//! `can_start_recording` / `can_download_model` always return `true`. The
//! `Limits` / `DEFAULT_*` constants, `record_usage` / `record_model_download`,
//! and the `cache.rs` remote-override functions are kept inert to preserve a
//! stable FFI surface (deleting them is an optional later cleanup). The daily
//! day-index reset logic is retained only so the surfaced `daily_seconds_used`
//! counter still behaves sensibly.
//!
//! This is orthogonal to HyperWhisper **Cloud** (server-side paid transcription),
//! which remains the paid moat and is enforced server-side.
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

/// Compute the current usage snapshot for `status` at `now`.
///
/// **Local transcription is unconditionally free and unlimited** (HyperWhisper is
/// open source). Regardless of `status` or `limits`, both `*_reached` flags are
/// `false` and both `remaining_*` are `i64::MAX` (the analogue of macOS'
/// `Int.max`). The `limits` parameter and the `Limits`/`DEFAULT_*` machinery are
/// retained inert to keep the FFI surface stable; they no longer gate anything.
///
/// HyperWhisper **Cloud** (server-side paid transcription) is the paid moat and is
/// enforced server-side — it is orthogonal to this local usage tracking.
///
/// Reading the snapshot still performs the day-boundary reset as `record_usage`,
/// so the surfaced `daily_seconds_used` counter resets after midnight.
pub fn check_limits(
    store: &dyn KeyValueStore,
    status: LicenseStatus,
    limits: Limits,
    now_unix_secs: i64,
) -> UsageSnapshot {
    let daily = current_daily_seconds(store, now_unix_secs);
    let models = current_models_downloaded(store);

    // Local limits are removed (open source): every status is unlimited. `status`
    // and `limits` are ignored for gating and kept only for FFI stability.
    let _ = (status, limits);

    UsageSnapshot {
        daily_seconds_used: daily,
        models_downloaded: models,
        daily_limit_reached: false,
        model_limit_reached: false,
        remaining_daily_seconds: i64::MAX,
        remaining_model_downloads: i64::MAX,
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

/// Whether another model may be downloaded. **Always `true`** — local model
/// downloads are unlimited (open source). `store`/`status`/`limits` are ignored
/// and kept only for FFI stability.
pub fn can_download_model(
    store: &dyn KeyValueStore,
    status: LicenseStatus,
    limits: Limits,
) -> bool {
    let _ = (store, status, limits);
    true
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

    /// Every status — including the inert `Limits` — yields an unlimited
    /// snapshot now that local limits are removed (open source).
    const ALL_STATUSES: [LicenseStatus; 4] = [
        LicenseStatus::Active,
        LicenseStatus::Trial,
        LicenseStatus::Expired,
        LicenseStatus::Invalid,
    ];

    fn assert_unlimited(snap: &UsageSnapshot) {
        assert!(!snap.daily_limit_reached);
        assert!(!snap.model_limit_reached);
        assert_eq!(snap.remaining_daily_seconds, i64::MAX);
        assert_eq!(snap.remaining_model_downloads, i64::MAX);
    }

    #[test]
    fn defaults_match_build_flavor() {
        // The `Limits` machinery is inert but retained for FFI stability.
        assert_eq!(Limits::defaults(false).daily_seconds, 300);
        assert_eq!(Limits::defaults(true).daily_seconds, 1800);
        assert_eq!(Limits::defaults(false).model_downloads, 3);
    }

    #[test]
    fn local_usage_is_unlimited_for_every_status() {
        let now = 10 * DAY + 500;
        for status in ALL_STATUSES {
            let s = store();
            // Pile on far more usage than any historical trial limit.
            record_usage(&s, 100_000, now);
            for _ in 0..20 {
                record_model_download(&s);
            }
            let snap = check_limits(&s, status, release_limits(), now);
            assert_unlimited(&snap);
            assert!(can_start_recording(&s, status, release_limits(), now));
            assert!(can_download_model(&s, status, release_limits()));
        }
    }

    #[test]
    fn limits_parameter_is_ignored() {
        // Even an absurdly tight override never gates anything.
        let tiny = Limits {
            daily_seconds: 1,
            model_downloads: 0,
        };
        let now = 0;
        for status in ALL_STATUSES {
            let s = store();
            record_usage(&s, 10_000, now);
            record_model_download(&s);
            let snap = check_limits(&s, status, tiny, now);
            assert_unlimited(&snap);
            assert!(can_start_recording(&s, status, tiny, now));
            assert!(can_download_model(&s, status, tiny));
        }
    }

    #[test]
    fn record_still_tracks_daily_seconds() {
        // Usage is still recorded (and surfaced) even though it never gates.
        let s = store();
        let now = 10 * DAY + 500;
        record_usage(&s, 120, now);
        record_usage(&s, 100, now + 60);
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), now + 120);
        assert_eq!(snap.daily_seconds_used, 220);
        assert_unlimited(&snap);
    }

    #[test]
    fn daily_usage_resets_at_day_boundary() {
        let s = store();
        let day10 = 10 * DAY + 100;
        record_usage(&s, 300, day10);
        // Next UTC day → surfaced counter resets.
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
    fn zero_or_negative_usage_is_noop() {
        let s = store();
        record_usage(&s, 0, 0);
        record_usage(&s, -5, 0);
        let snap = check_limits(&s, LicenseStatus::Trial, release_limits(), 0);
        assert_eq!(snap.daily_seconds_used, 0);
    }
}
