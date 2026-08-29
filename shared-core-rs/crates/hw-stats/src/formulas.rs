//! The four arithmetic decisions, isolated so the conformance vectors can point
//! at one function each.

/// The ceiling on the displayed "saved this week" figure: one week of minutes.
///
/// **Unify decision.** Both .NET copies clamp here and the Windows comment at
/// `HomeStatsBarViewModel.cs:28-29` says why — "a row with Words>0 and
/// Duration≈0 can otherwise produce an absurd savings figure". macOS had no
/// ceiling, so one bad row put a five-figure number in the home strip.
pub const SAVED_MINUTES_CEILING: i32 = 7 * 24 * 60;

/// A duration the arithmetic can use.
///
/// **Unify decision.** Guard every duration with is-finite-and-positive.
/// `HomeStatistics.cs:128-129` did; the other two did not, and macOS then fed
/// the result to `Int(saved.rounded())`, which *traps* on NaN — one corrupt
/// `duration` row crashed the home view at `HomeStatsBar.swift:168`. Rust would
/// not trap, but `f64 as i32` saturates NaN to 0, so an unguarded NaN would
/// silently zero a real figure instead. Both are wrong; normalising at the row
/// is right.
pub fn normalize_duration(duration_seconds: f64) -> f64 {
    if duration_seconds.is_finite() && duration_seconds > 0.0 {
        duration_seconds
    } else {
        0.0
    }
}

/// Round half away from zero, then take the integer.
///
/// **Unify decision.** Half-away-from-zero everywhere. Swift's `.rounded()`
/// already is; C#'s `Math.Round(double)` is banker's rounding, so 2.5 saved
/// minutes showed as 3 on macOS and 2 on Windows and Linux. Rust's
/// `f64::round` is half-away-from-zero, which makes the shared answer the macOS
/// one. The `as i32` cast saturates rather than wrapping, and maps a
/// non-finite value to 0 — reachable only if a caller skips
/// [`normalize_duration`].
pub fn round_half_away_from_zero(value: f64) -> i32 {
    value.round() as i32
}

/// Words per minute over a period, or 0 when nothing was spoken.
pub fn average_words_per_minute(words: u32, duration_seconds: f64) -> i32 {
    let minutes = duration_seconds / 60.0;
    if minutes > 0.0 {
        round_half_away_from_zero(f64::from(words) / minutes)
    } else {
        0
    }
}

/// How long the same words would have taken to type, in minutes. 0 when the
/// typing speed is unset or nonsensical.
pub fn estimated_typing_minutes(words: u32, typing_speed_words_per_minute: i32) -> f64 {
    if typing_speed_words_per_minute > 0 {
        f64::from(words) / f64::from(typing_speed_words_per_minute)
    } else {
        0.0
    }
}

/// The unclamped saving, in minutes, floored at 0. This is the figure the
/// *statistics page* reports per period; the home strip shows the clamped
/// integer from [`displayed_saved_minutes`].
pub fn estimated_time_saved_minutes(
    words: u32,
    duration_seconds: f64,
    typing_speed_words_per_minute: i32,
) -> f64 {
    if typing_speed_words_per_minute <= 0 {
        return 0.0;
    }
    let typing = estimated_typing_minutes(words, typing_speed_words_per_minute);
    let spoken = duration_seconds / 60.0;
    (typing - spoken).max(0.0)
}

/// The integer the home strip renders: rounded, floored at 0, clamped to
/// [`SAVED_MINUTES_CEILING`].
///
/// The order matters and is the .NET order: round first, then floor, then
/// clamp. Rounding after the clamp would let 10079.5 display as 10080.
pub fn displayed_saved_minutes(
    words: u32,
    duration_seconds: f64,
    typing_speed_words_per_minute: i32,
) -> i32 {
    if typing_speed_words_per_minute <= 0 {
        return 0;
    }
    let typing = estimated_typing_minutes(words, typing_speed_words_per_minute);
    let spoken = duration_seconds / 60.0;
    let rounded = round_half_away_from_zero(typing - spoken);
    rounded.clamp(0, SAVED_MINUTES_CEILING)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalize_duration_rejects_every_unusable_value() {
        assert_eq!(normalize_duration(12.5), 12.5);
        assert_eq!(normalize_duration(0.0), 0.0);
        assert_eq!(normalize_duration(-1.0), 0.0);
        assert_eq!(normalize_duration(f64::NAN), 0.0);
        assert_eq!(normalize_duration(f64::INFINITY), 0.0);
        assert_eq!(normalize_duration(f64::NEG_INFINITY), 0.0);
    }

    #[test]
    fn rounding_is_half_away_from_zero_not_bankers() {
        // The exact case issue #285 names: banker's rounding gives 2.
        assert_eq!(round_half_away_from_zero(2.5), 3);
        assert_eq!(round_half_away_from_zero(3.5), 4);
        assert_eq!(round_half_away_from_zero(-2.5), -3);
        assert_eq!(round_half_away_from_zero(2.4), 2);
    }

    #[test]
    fn rounding_saturates_instead_of_wrapping() {
        assert_eq!(round_half_away_from_zero(1e30), i32::MAX);
        assert_eq!(round_half_away_from_zero(-1e30), i32::MIN);
        assert_eq!(round_half_away_from_zero(f64::NAN), 0);
    }

    #[test]
    fn average_wpm_is_zero_without_spoken_time() {
        assert_eq!(average_words_per_minute(500, 0.0), 0);
        assert_eq!(average_words_per_minute(0, 60.0), 0);
        assert_eq!(average_words_per_minute(150, 60.0), 150);
        // 145 words in 1.5 minutes = 96.66 -> 97.
        assert_eq!(average_words_per_minute(145, 90.0), 97);
    }

    #[test]
    fn typing_minutes_need_a_usable_speed() {
        assert_eq!(estimated_typing_minutes(200, 40), 5.0);
        assert_eq!(estimated_typing_minutes(200, 0), 0.0);
        assert_eq!(estimated_typing_minutes(200, -40), 0.0);
    }

    #[test]
    fn saved_minutes_never_go_negative() {
        // 40 words at 40 WPM is 1 typed minute against 2 spoken minutes.
        assert_eq!(estimated_time_saved_minutes(40, 120.0, 40), 0.0);
        assert_eq!(displayed_saved_minutes(40, 120.0, 40), 0);
    }

    #[test]
    fn saved_minutes_are_clamped_to_one_week() {
        // The pathological row the Windows comment describes: many words, no
        // duration. 4_000_000 words at 40 WPM is 100_000 minutes.
        assert_eq!(
            displayed_saved_minutes(4_000_000, 0.0, 40),
            SAVED_MINUTES_CEILING
        );
        // The unclamped per-period figure is deliberately NOT capped.
        assert_eq!(estimated_time_saved_minutes(4_000_000, 0.0, 40), 100_000.0);
    }

    #[test]
    fn saved_minutes_round_before_they_clamp() {
        // 200 words at 40 WPM is 5 typed minutes against 2.5 spoken ones, so
        // 2.5 minutes saved. It shows as 3, not the banker's-rounding 2.
        assert_eq!(displayed_saved_minutes(200, 150.0, 40), 3);
    }

    #[test]
    fn saved_minutes_are_zero_without_a_typing_speed() {
        assert_eq!(displayed_saved_minutes(1_000, 0.0, 0), 0);
        assert_eq!(estimated_time_saved_minutes(1_000, 0.0, 0), 0.0);
    }
}
