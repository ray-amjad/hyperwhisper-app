//! One pass over the transcript rows, four period buckets out.

use crate::calendar::{day_number, month_bounds, start_of_week, year_bounds};
use crate::formulas::{
    average_words_per_minute, displayed_saved_minutes, estimated_time_saved_minutes,
    estimated_typing_minutes, normalize_duration,
};

pub use crate::formulas::SAVED_MINUTES_CEILING;

/// The persisted status of a transcript row.
///
/// **Unify decision.** Only `Completed` rows count, on every platform. Both
/// .NET copies already filtered; macOS filtered in the `@FetchRequest`
/// predicate, which means the rule lived in a SwiftUI property wrapper on one
/// head and in the calculator on the other two. It lives here now, so a head
/// that hands over everything it has still gets the same answer.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum TranscriptStatus {
    Processing,
    Completed,
    Failed,
}

/// The projection of a persisted transcript the home statistics need.
#[derive(Clone, Debug)]
pub struct Transcript {
    /// The row's instant, already shifted into the calendar time zone by the
    /// host and expressed as seconds since the epoch. See the crate docs for
    /// why the conversion stays native.
    pub created_at_local_epoch_seconds: i64,
    /// Counted by the host from the full text. There is no persisted count on
    /// any of the three stores (#285).
    pub word_count: u32,
    /// Spoken seconds, as stored. Not trusted: [`normalize_duration`] runs on
    /// every row.
    pub duration_seconds: f64,
    pub status: TranscriptStatus,
}

/// One period's totals and derived figures.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct PeriodStats {
    /// Saturating sum of the rows' word counts. A saturated total is a wrong
    /// number; the `checked` sum the .NET calculator used was an exception on
    /// the home view's render path.
    pub word_count: u32,
    pub duration_seconds: f64,
    pub average_words_per_minute: i32,
    pub estimated_typing_minutes: f64,
    /// Floored at 0 but NOT clamped — the ceiling applies to the displayed
    /// home-strip figure only. See [`HomeStatsSnapshot::saved_this_week_minutes`].
    pub estimated_time_saved_minutes: f64,
}

/// Everything the three home strips render, plus the periods the statistics
/// pages use.
///
/// Every head gets every period. They differ in which columns they show —
/// macOS shows month and year, Windows and Linux show week and month — and
/// that stays a layout decision, not an arithmetic one.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct HomeStatsSnapshot {
    pub this_week: PeriodStats,
    pub this_month: PeriodStats,
    pub this_year: PeriodStats,
    pub all_time: PeriodStats,
    /// Echoed back so a head can render the gear menu's current value without
    /// reading settings twice.
    pub typing_speed_words_per_minute: i32,
    /// The home strip's "saved this week" integer: rounded half away from zero,
    /// floored at 0, clamped to [`SAVED_MINUTES_CEILING`].
    pub saved_this_week_minutes: i32,
}

impl HomeStatsSnapshot {
    /// The "avg WPM" column is the all-time figure on all three heads.
    pub fn average_words_per_minute(&self) -> i32 {
        self.all_time.average_words_per_minute
    }
}

/// Calculate every home statistic from the rows the host holds.
///
/// `now_local_epoch_seconds` is "now" in the same shifted coordinate as the
/// rows. `typing_speed_words_per_minute` is the user's gear-menu setting; 0 or
/// negative means the saving figures are 0 rather than an error.
///
/// The five drifts this resolves, each with a `decision` row in
/// `shared-conformance/stats-vectors.json`:
///
/// 1. `saved-minutes-ceiling` — macOS had none.
/// 2. `half-away-from-zero` — C# used banker's rounding.
/// 3. `finite-positive-duration` — only the shared .NET copy guarded, and the
///    unguarded macOS path *trapped*.
/// 4. `monday-week-start` — macOS started the week on the locale's first
///    weekday; one .NET copy forced UTC.
/// 5. `completed-only` — the filter lived in three different places.
pub fn calculate(
    transcripts: &[Transcript],
    typing_speed_words_per_minute: i32,
    now_local_epoch_seconds: i64,
) -> HomeStatsSnapshot {
    let today = day_number(now_local_epoch_seconds);
    let week_start = start_of_week(today);
    let next_week_start = week_start + 7;
    let (month_start, next_month_start) = month_bounds(today);
    let (year_start, next_year_start) = year_bounds(today);

    let mut week = Accumulator::default();
    let mut month = Accumulator::default();
    let mut year = Accumulator::default();
    let mut all_time = Accumulator::default();

    for transcript in transcripts {
        if transcript.status != TranscriptStatus::Completed {
            continue;
        }

        let words = transcript.word_count;
        let duration = normalize_duration(transcript.duration_seconds);
        all_time.add(words, duration);

        let day = day_number(transcript.created_at_local_epoch_seconds);
        if day >= week_start && day < next_week_start {
            week.add(words, duration);
        }
        if day >= month_start && day < next_month_start {
            month.add(words, duration);
        }
        if day >= year_start && day < next_year_start {
            year.add(words, duration);
        }
    }

    let this_week = week.finish(typing_speed_words_per_minute);
    let saved_this_week_minutes = displayed_saved_minutes(
        this_week.word_count,
        this_week.duration_seconds,
        typing_speed_words_per_minute,
    );

    HomeStatsSnapshot {
        this_week,
        this_month: month.finish(typing_speed_words_per_minute),
        this_year: year.finish(typing_speed_words_per_minute),
        all_time: all_time.finish(typing_speed_words_per_minute),
        typing_speed_words_per_minute,
        saved_this_week_minutes,
    }
}

#[derive(Default)]
struct Accumulator {
    words: u32,
    duration_seconds: f64,
}

impl Accumulator {
    fn add(&mut self, words: u32, duration_seconds: f64) {
        self.words = self.words.saturating_add(words);
        self.duration_seconds += duration_seconds;
    }

    fn finish(&self, typing_speed_words_per_minute: i32) -> PeriodStats {
        PeriodStats {
            word_count: self.words,
            duration_seconds: self.duration_seconds,
            average_words_per_minute: average_words_per_minute(self.words, self.duration_seconds),
            estimated_typing_minutes: estimated_typing_minutes(
                self.words,
                typing_speed_words_per_minute,
            ),
            estimated_time_saved_minutes: estimated_time_saved_minutes(
                self.words,
                self.duration_seconds,
                typing_speed_words_per_minute,
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 2026-08-29 00:00 local, a Saturday. Its week runs Mon 24th - Sun 30th.
    const SATURDAY: i64 = 1_787_961_600;
    const DAY: i64 = 86_400;

    fn row(offset_days: i64, words: u32, duration_seconds: f64) -> Transcript {
        Transcript {
            created_at_local_epoch_seconds: SATURDAY + offset_days * DAY,
            word_count: words,
            duration_seconds,
            status: TranscriptStatus::Completed,
        }
    }

    #[test]
    fn the_reference_saturday_is_what_the_comment_says() {
        // Guards the fixture itself: 1_787_961_600 must be 2026-08-29.
        assert_eq!(
            start_of_week(day_number(SATURDAY)),
            day_number(SATURDAY) - 5
        );
    }

    #[test]
    fn empty_input_is_all_zeroes() {
        let snapshot = calculate(&[], 40, SATURDAY);
        assert_eq!(snapshot.all_time.word_count, 0);
        assert_eq!(snapshot.average_words_per_minute(), 0);
        assert_eq!(snapshot.saved_this_week_minutes, 0);
        assert_eq!(snapshot.typing_speed_words_per_minute, 40);
    }

    #[test]
    fn only_completed_rows_count() {
        let rows = vec![
            row(0, 100, 60.0),
            Transcript {
                status: TranscriptStatus::Processing,
                ..row(0, 500, 60.0)
            },
            Transcript {
                status: TranscriptStatus::Failed,
                ..row(0, 900, 60.0)
            },
        ];
        let snapshot = calculate(&rows, 40, SATURDAY);
        assert_eq!(snapshot.all_time.word_count, 100);
        assert_eq!(snapshot.this_week.word_count, 100);
    }

    #[test]
    fn the_sunday_after_a_saturday_is_the_same_week() {
        // This is the macOS drift: under a Sunday-start week it fell out.
        let snapshot = calculate(&[row(1, 60, 60.0)], 40, SATURDAY);
        assert_eq!(snapshot.this_week.word_count, 60);
        // The Monday before it is in; the Sunday before THAT is not.
        let snapshot = calculate(&[row(-5, 60, 60.0), row(-6, 70, 60.0)], 40, SATURDAY);
        assert_eq!(snapshot.this_week.word_count, 60);
        assert_eq!(snapshot.this_month.word_count, 130);
    }

    #[test]
    fn periods_nest_but_do_not_leak() {
        let rows = vec![
            row(0, 10, 60.0),    // this week, month, year
            row(-10, 20, 60.0),  // August 19th: this month and year
            row(-60, 40, 60.0),  // June 30th: this year only
            row(-300, 80, 60.0), // 2025: all time only
        ];
        let snapshot = calculate(&rows, 40, SATURDAY);
        assert_eq!(snapshot.this_week.word_count, 10);
        assert_eq!(snapshot.this_month.word_count, 30);
        assert_eq!(snapshot.this_year.word_count, 70);
        assert_eq!(snapshot.all_time.word_count, 150);
    }

    #[test]
    fn a_corrupt_duration_does_not_poison_the_totals() {
        // The row that crashed HomeStatsBar.swift:168.
        let rows = vec![row(0, 100, f64::NAN), row(0, 50, 60.0), row(0, 10, -5.0)];
        let snapshot = calculate(&rows, 40, SATURDAY);
        assert_eq!(snapshot.this_week.duration_seconds, 60.0);
        assert_eq!(snapshot.this_week.word_count, 160);
        assert!(snapshot.this_week.estimated_time_saved_minutes.is_finite());
        assert_eq!(snapshot.average_words_per_minute(), 160);
    }

    #[test]
    fn a_words_without_duration_row_is_clamped_not_absurd() {
        let snapshot = calculate(&[row(0, 4_000_000, 0.0)], 40, SATURDAY);
        assert_eq!(snapshot.saved_this_week_minutes, SAVED_MINUTES_CEILING);
        // The per-period figure keeps the raw estimate.
        assert_eq!(snapshot.this_week.estimated_time_saved_minutes, 100_000.0);
    }

    #[test]
    fn word_counts_saturate_rather_than_overflow() {
        let rows = vec![row(0, u32::MAX, 60.0), row(0, 10, 60.0)];
        let snapshot = calculate(&rows, 40, SATURDAY);
        assert_eq!(snapshot.all_time.word_count, u32::MAX);
    }

    #[test]
    fn a_zero_typing_speed_zeroes_the_savings_only() {
        let snapshot = calculate(&[row(0, 300, 60.0)], 0, SATURDAY);
        assert_eq!(snapshot.saved_this_week_minutes, 0);
        assert_eq!(snapshot.this_week.estimated_typing_minutes, 0.0);
        assert_eq!(snapshot.this_week.estimated_time_saved_minutes, 0.0);
        // The speed figures still work.
        assert_eq!(snapshot.average_words_per_minute(), 300);
    }
}
