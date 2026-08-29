//! UniFFI surface for the home statistics formulas (`hw_stats`, #285).
//!
//! Follows the `ffi_phonetic` / `ffi_releasenotes` shape: the leaf crate's types
//! are **mirrored** here as owned `uniffi::Record`s with `From` impls rather
//! than re-exported, so `hw-stats` stays a plain crate that can be unit-tested
//! with no UniFFI in the way.
//!
//! # Why the whole history crosses the boundary at once
//!
//! The home strip recomputes on every transcript add, update and delete, and
//! all three heads already materialise the full row set to do it (macOS through
//! a `@FetchRequest`, the .NET heads through `IStatisticsTranscriptProvider`).
//! One call per recompute with the rows attached costs a single crossing;
//! per-row calls would cost thousands, and a "give me the next row" callback
//! would put a foreign trait on the hot path for no gain.
//!
//! # The `Hw` prefix is not cosmetic
//!
//! An unprefixed `Transcript` would generate `Transcript` in
//! `hyperwhisper_core.cs`, which is the name of the Windows EF Core entity
//! (`HyperWhisper.Data.Entities.Transcript`) and of the macOS Core Data class.
//! `Hw` keeps the FFI record and the host's persistence type visibly distinct
//! at every call site.

/// The persisted status of a transcript row. Mirrors
/// `hw_stats::TranscriptStatus`.
///
/// The host maps its own status column onto this and hands over everything;
/// the completed-only filter is applied on this side, once.
#[derive(uniffi::Enum)]
pub enum HwTranscriptStatus {
    Processing,
    Completed,
    Failed,
}

impl From<&HwTranscriptStatus> for hw_stats::TranscriptStatus {
    fn from(status: &HwTranscriptStatus) -> Self {
        match status {
            HwTranscriptStatus::Processing => hw_stats::TranscriptStatus::Processing,
            HwTranscriptStatus::Completed => hw_stats::TranscriptStatus::Completed,
            HwTranscriptStatus::Failed => hw_stats::TranscriptStatus::Failed,
        }
    }
}

/// One persisted transcript, projected down to what the statistics need.
#[derive(uniffi::Record)]
pub struct HwStatsTranscript {
    /// The row's instant **already shifted into the calendar time zone**,
    /// as seconds since the Unix epoch.
    ///
    /// The host owns this conversion because the host owns the time-zone
    /// database: `TimeZoneInfo.ConvertTime(row.CreatedAt, tz)` on .NET,
    /// `date.timeIntervalSince1970 + TimeZone.current.secondsFromGMT(for: date)`
    /// on Swift. Doing it per row is what keeps DST correct. Every calendar
    /// boundary above it — Monday, the 1st, January 1st — is computed in Rust.
    pub created_at_local_epoch_seconds: i64,
    /// Counted by the host from the full text. Word counting stays native:
    /// there is no persisted count on any of the three stores, and the two
    /// native implementations already agree (#285).
    pub word_count: u32,
    /// Spoken seconds, as stored. Not trusted — a non-finite or negative value
    /// is normalised to 0 rather than rejected.
    pub duration_seconds: f64,
    pub status: HwTranscriptStatus,
}

impl From<&HwStatsTranscript> for hw_stats::Transcript {
    fn from(transcript: &HwStatsTranscript) -> Self {
        hw_stats::Transcript {
            created_at_local_epoch_seconds: transcript.created_at_local_epoch_seconds,
            word_count: transcript.word_count,
            duration_seconds: transcript.duration_seconds,
            status: (&transcript.status).into(),
        }
    }
}

/// One period's totals and derived figures. Mirrors `hw_stats::PeriodStats`.
#[derive(uniffi::Record)]
pub struct HwPeriodStats {
    /// Saturating sum of the rows' word counts.
    pub word_count: u32,
    pub duration_seconds: f64,
    pub average_words_per_minute: i32,
    pub estimated_typing_minutes: f64,
    /// Floored at 0 but NOT clamped — the one-week ceiling applies to
    /// [`HwHomeStatsSnapshot::saved_this_week_minutes`] only.
    pub estimated_time_saved_minutes: f64,
}

impl From<hw_stats::PeriodStats> for HwPeriodStats {
    fn from(stats: hw_stats::PeriodStats) -> Self {
        HwPeriodStats {
            word_count: stats.word_count,
            duration_seconds: stats.duration_seconds,
            average_words_per_minute: stats.average_words_per_minute,
            estimated_typing_minutes: stats.estimated_typing_minutes,
            estimated_time_saved_minutes: stats.estimated_time_saved_minutes,
        }
    }
}

/// Everything the three home strips render, plus the periods the statistics
/// pages use. Mirrors `hw_stats::HomeStatsSnapshot`.
#[derive(uniffi::Record)]
pub struct HwHomeStatsSnapshot {
    pub this_week: HwPeriodStats,
    pub this_month: HwPeriodStats,
    pub this_year: HwPeriodStats,
    pub all_time: HwPeriodStats,
    /// Echoed back so a head can render the gear menu without reading settings
    /// twice.
    pub typing_speed_words_per_minute: i32,
    /// The "avg WPM" column: the all-time figure, on all three heads.
    pub average_words_per_minute: i32,
    /// The "saved this week" column: rounded half away from zero, floored at 0,
    /// clamped to [`stats_saved_minutes_ceiling`].
    pub saved_this_week_minutes: i32,
}

impl From<hw_stats::HomeStatsSnapshot> for HwHomeStatsSnapshot {
    fn from(snapshot: hw_stats::HomeStatsSnapshot) -> Self {
        HwHomeStatsSnapshot {
            this_week: snapshot.this_week.into(),
            this_month: snapshot.this_month.into(),
            this_year: snapshot.this_year.into(),
            all_time: snapshot.all_time.into(),
            typing_speed_words_per_minute: snapshot.typing_speed_words_per_minute,
            average_words_per_minute: snapshot.average_words_per_minute(),
            saved_this_week_minutes: snapshot.saved_this_week_minutes,
        }
    }
}

/// Calculate every home statistic from the host's transcript rows.
///
/// `now_local_epoch_seconds` is "now" in the same shifted coordinate as the
/// rows. A `typing_speed_words_per_minute` of 0 or less zeroes the saving
/// figures instead of failing — an unset setting is not an error.
///
/// This call is total: there is no error case, and no input can panic.
#[uniffi::export]
pub fn stats_calculate_home(
    transcripts: Vec<HwStatsTranscript>,
    typing_speed_words_per_minute: i32,
    now_local_epoch_seconds: i64,
) -> HwHomeStatsSnapshot {
    let transcripts: Vec<hw_stats::Transcript> =
        transcripts.iter().map(hw_stats::Transcript::from).collect();
    hw_stats::calculate(
        &transcripts,
        typing_speed_words_per_minute,
        now_local_epoch_seconds,
    )
    .into()
}

/// The ceiling the displayed "saved this week" figure is clamped to: one week
/// of minutes.
///
/// Exported so a head can assert against it rather than restate `7 * 24 * 60`,
/// which is exactly how the constant drifted onto two platforms and off the
/// third.
#[uniffi::export]
pub fn stats_saved_minutes_ceiling() -> i32 {
    hw_stats::SAVED_MINUTES_CEILING
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_ceiling_matches_the_native_constants_it_replaces() {
        assert_eq!(stats_saved_minutes_ceiling(), 7 * 24 * 60);
    }

    #[test]
    fn the_snapshot_carries_the_all_time_average() {
        // 2026-08-29 00:00, a Saturday. 300 words in one spoken minute.
        let snapshot = stats_calculate_home(
            vec![HwStatsTranscript {
                created_at_local_epoch_seconds: 1_787_961_600,
                word_count: 300,
                duration_seconds: 60.0,
                status: HwTranscriptStatus::Completed,
            }],
            40,
            1_787_961_600,
        );
        assert_eq!(snapshot.average_words_per_minute, 300);
        assert_eq!(snapshot.all_time.average_words_per_minute, 300);
        assert_eq!(snapshot.this_week.word_count, 300);
        // 300 words at 40 WPM is 7.5 typed minutes, less 1 spoken = 6.5 -> 7.
        assert_eq!(snapshot.saved_this_week_minutes, 7);
    }

    #[test]
    fn a_non_completed_row_is_dropped_on_this_side_of_the_boundary() {
        let snapshot = stats_calculate_home(
            vec![HwStatsTranscript {
                created_at_local_epoch_seconds: 1_787_961_600,
                word_count: 300,
                duration_seconds: 60.0,
                status: HwTranscriptStatus::Failed,
            }],
            40,
            1_787_961_600,
        );
        assert_eq!(snapshot.all_time.word_count, 0);
    }
}
