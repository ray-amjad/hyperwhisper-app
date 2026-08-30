//! Civil-date arithmetic over "local epoch seconds".
//!
//! The host has already shifted every instant into the calendar time zone (see
//! the crate docs), so the whole file works in one flat coordinate: days since
//! 1970-01-01, floor-divided so pre-epoch rows stay ordered.
//!
//! The two conversions are Howard Hinnant's `days_from_civil` /
//! `civil_from_days`, which are exact for the whole proleptic Gregorian
//! calendar and need no tables. They are the same algorithm `chrono` and
//! `time` use; sixty lines here buys a zero-dependency crate.

/// Seconds in one day. Leap seconds do not exist in Unix time.
const SECONDS_PER_DAY: i64 = 86_400;

/// Days since 1970-01-01, rounding towards negative infinity so that a
/// pre-epoch instant lands on the day it actually falls in.
pub fn day_number(local_epoch_seconds: i64) -> i64 {
    local_epoch_seconds.div_euclid(SECONDS_PER_DAY)
}

/// Days from 1970-01-01 to the given civil date. `month` is 1-12, `day` 1-31.
///
/// Hinnant's algorithm: shift the year so March is month 1, which puts the leap
/// day at the end of the year and makes the day-of-year formula a straight
/// line.
fn days_from_civil(year: i64, month: u32, day: u32) -> i64 {
    let month = i64::from(month);
    let day = i64::from(day);
    let year = if month <= 2 { year - 1 } else { year };
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let year_of_era = year - era * 400; // [0, 399]
    let shifted_month = if month > 2 { month - 3 } else { month + 9 }; // [0, 11]
    let day_of_year = (153 * shifted_month + 2) / 5 + day - 1; // [0, 365]
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

/// The civil `(year, month, day)` a day number falls on. Inverse of
/// [`days_from_civil`].
fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let days = days + 719_468;
    let era = if days >= 0 { days } else { days - 146_096 } / 146_097;
    let day_of_era = days - era * 146_097; // [0, 146096]
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let shifted_month = (5 * day_of_year + 2) / 153; // [0, 11]
    let day = day_of_year - (153 * shifted_month + 2) / 5 + 1; // [1, 31]
    let month = if shifted_month < 10 {
        shifted_month + 3
    } else {
        shifted_month - 9
    }; // [1, 12]
    let year = if month <= 2 { year + 1 } else { year };
    // Both casts are in range by construction: `month` is [1, 12] and `day` is
    // [1, 31] on every branch above.
    (year, month as u32, day as u32)
}

/// The Monday on or before `day`.
///
/// **Unify decision.** The week starts on the local-time-zone Monday on every
/// platform. Two of the three copies already did (`HomeStatistics.cs:122-126`
/// local, `StatisticsService.cs:109-113` forced to UTC); macOS asked
/// `Calendar.current` for `.weekOfYear`, which starts on Sunday in en-US, so a
/// Sunday transcript counted towards next week there and last week everywhere
/// else.
pub fn start_of_week(day: i64) -> i64 {
    // 1970-01-01 was a Thursday, so `day + 4` makes 0 = Sunday.
    let weekday = (day + 4).rem_euclid(7);
    // ...and `+ 6` rotates that to 0 = Monday.
    let days_since_monday = (weekday + 6).rem_euclid(7);
    day - days_since_monday
}

/// The first day of the month `day` falls in, and the first day of the next
/// month.
pub fn month_bounds(day: i64) -> (i64, i64) {
    let (year, month, _) = civil_from_days(day);
    let start = days_from_civil(year, month, 1);
    let next = if month == 12 {
        days_from_civil(year + 1, 1, 1)
    } else {
        days_from_civil(year, month + 1, 1)
    };
    (start, next)
}

/// The first day of the year `day` falls in, and the first day of the next
/// year. macOS is the only head that shows a "words this year" column, but the
/// snapshot carries every period so the three heads differ in layout only.
pub fn year_bounds(day: i64) -> (i64, i64) {
    let (year, _, _) = civil_from_days(day);
    (days_from_civil(year, 1, 1), days_from_civil(year + 1, 1, 1))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn day_of(year: i64, month: u32, day: u32) -> i64 {
        days_from_civil(year, month, day)
    }

    #[test]
    fn epoch_day_is_zero() {
        assert_eq!(day_of(1970, 1, 1), 0);
        assert_eq!(civil_from_days(0), (1970, 1, 1));
    }

    #[test]
    fn civil_round_trips_across_leap_years_and_centuries() {
        for &(y, m, d) in &[
            (1969, 12, 31),
            (1972, 2, 29),
            (1900, 3, 1),
            (2000, 2, 29),
            (2026, 8, 29),
            (2100, 3, 1),
            (1601, 1, 1),
        ] {
            let days = day_of(y, m, d);
            assert_eq!(civil_from_days(days), (y, m, d), "round trip {y}-{m}-{d}");
        }
    }

    #[test]
    fn day_number_floors_before_the_epoch() {
        assert_eq!(day_number(0), 0);
        assert_eq!(day_number(SECONDS_PER_DAY - 1), 0);
        assert_eq!(day_number(-1), -1);
        assert_eq!(day_number(-SECONDS_PER_DAY), -1);
        assert_eq!(day_number(-SECONDS_PER_DAY - 1), -2);
    }

    #[test]
    fn week_starts_on_monday() {
        // 2026-08-29 is a Saturday; its week starts Monday 2026-08-24.
        let saturday = day_of(2026, 8, 29);
        assert_eq!(start_of_week(saturday), day_of(2026, 8, 24));
        // The Sunday after it belongs to the SAME week under a Monday rule,
        // and to the next one under macOS's old Sunday rule. This is the drift.
        let sunday = day_of(2026, 8, 30);
        assert_eq!(start_of_week(sunday), day_of(2026, 8, 24));
        // A Monday is its own week start.
        let monday = day_of(2026, 8, 24);
        assert_eq!(start_of_week(monday), monday);
    }

    #[test]
    fn week_start_holds_before_the_epoch() {
        // 1969-12-31 was a Wednesday.
        let wednesday = day_of(1969, 12, 31);
        assert_eq!(start_of_week(wednesday), day_of(1969, 12, 29));
    }

    #[test]
    fn month_bounds_wrap_december() {
        assert_eq!(
            month_bounds(day_of(2026, 8, 29)),
            (day_of(2026, 8, 1), day_of(2026, 9, 1))
        );
        assert_eq!(
            month_bounds(day_of(2026, 12, 31)),
            (day_of(2026, 12, 1), day_of(2027, 1, 1))
        );
        assert_eq!(
            month_bounds(day_of(2024, 2, 29)),
            (day_of(2024, 2, 1), day_of(2024, 3, 1))
        );
    }

    #[test]
    fn year_bounds_cover_the_whole_year() {
        assert_eq!(
            year_bounds(day_of(2026, 8, 29)),
            (day_of(2026, 1, 1), day_of(2027, 1, 1))
        );
        assert_eq!(
            year_bounds(day_of(2026, 1, 1)),
            (day_of(2026, 1, 1), day_of(2027, 1, 1))
        );
    }
}
