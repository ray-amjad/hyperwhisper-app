//! The appcast item-selection step: raw `<item>` fields in, renderable
//! releases out (issue #353).
//!
//! The XML reader stays native on each head — `XMLParser` on macOS,
//! `XDocument` on Windows — and only the *rules* live here, over a plain
//! record. That is the whole point: the two heads read the same two feeds and
//! had drifted apart on every rule that turns an `<item>` into a row of Recent
//! Updates, and the drift was invisible because each head only ever looked at
//! its own feed.
//!
//! # The two heads' version rules were BOTH wrong on the other head's feed
//!
//! | | `appcast.xml` (macOS) | `appcast-windows.xml` (Windows) |
//! |---|---|---|
//! | `<title>` | `2.46.0` | `1.11.0 (ARM64)` / `1.11.0 (x64)` |
//! | `sparkle:version` | `116` (a build number) | `1.11.0` (a semver) |
//! | `sparkle:shortVersionString` | `2.46.0` | `1.11.0` |
//!
//! macOS read `<title>`; on the Windows feed that shows `1.11.0 (ARM64)`, the
//! arch suffix leaking into the version and each arch becoming its own release.
//! Windows read `sparkle:version`; on the macOS feed that shows `116`, `115`,
//! `114` — build numbers where the UI wants versions. Neither rule could be
//! adopted as-is, which is why [`select_releases`] picks a third
//! (**decision D1**).
//!
//! # The decisions this module owns
//!
//! **D1 — `version`.** `sparkle:shortVersionString`, else `sparkle:version`,
//! else `<title>`; each candidate trimmed of ASCII whitespace and skipped when
//! it is empty after trimming. This reproduces today's displayed string on both
//! live feeds exactly, so the unification ships with no visible change, while
//! being the order that is semantically right: `shortVersionString` is the
//! human-readable version by Sparkle's own definition, `sparkle:version` is the
//! machine build identifier, and `<title>` is free-form prose.
//!
//! **D2 — a missing or unparseable `<pubDate>` is epoch 0**, not "now" and not
//! a platform sentinel. Deterministic (macOS's `Date()` meant the same feed
//! sorted differently on every fetch, and a malformed entry always won the top
//! of the list); sorts last, so a malformed entry can never displace a real
//! release or steal Windows' `IsLatest` flag; and it needs no clock, which
//! matters because the house FFI convention (`ffi_license.rs`) is that Rust
//! never reads the clock — "now" would force a `now_unix_secs` parameter onto
//! the exported function purely to service a malformed input.
//!
//! The field is deliberately **not** `Option<i64>`. That reads as more honest,
//! but it turns `pubDate` into `Date?` / `DateTime?` on both heads and ripples
//! into their date formatters and `Equatable` conformances, for an input that
//! has never occurred in 129 committed feed items.
//!
//! **D3 — dedupe** on the resolved D1 version, compared byte-exact: no case
//! folding and no semver normalisation, because normalising is version
//! *comparison*, which #284 settled stays native. Inside a duplicate group the
//! **first entry in document order** wins — that is what Windows does today
//! (`GroupBy(...).Select(g => g.First())` runs before `OrderByDescending`, so
//! `First()` is document order, not date order), and the arch pairs it exists
//! to collapse are identical in date and notes anyway. The Windows feed ships
//! one item per architecture, so its 30 items carry 15 versions, each twice.
//! On the macOS feed all 99 versions are unique, so dedupe is a no-op there
//! today — but not inert: `RecentUpdatesView` renders with `ForEach(id:
//! \.element.id)` and `AppcastItem.id` is the version, so a feed that ever
//! repeated one would hand SwiftUI duplicate `Identifiable` ids.
//!
//! **D4 — ordering** is a **stable** sort by `pub_date_epoch_secs`, descending;
//! ties keep post-dedupe document order. Stability is load-bearing and is
//! asserted by a test below: the Windows feed has same-second `pubDate` values
//! and LINQ's `OrderByDescending` is stable, so an unstable sort here would be
//! a silent behaviour change on Windows.
//!
//! **D5 — drop rules.** An entry is dropped when its resolved version is empty,
//! or when it has no inline release notes — `sparkle:releaseNotesLink` present,
//! or `<description>` absent or whitespace-only. A missing or unparseable
//! `<pubDate>` is **not** a drop rule; the entry survives with epoch 0, which
//! is Windows' behaviour. An entry with real notes and a broken date is still a
//! release the user wants to read, and D2 already guarantees it cannot pollute
//! the ordering.
//!
//! **The pipeline order — filter, then dedupe, then sort — is part of the
//! spec.** Dedupe-before-filter gives different answers: a version whose first
//! document-order entry has no notes would collapse the group to a dropped
//! entry and lose the good one. Windows' current order is filter-then-dedupe;
//! it is kept.
//!
//! **D6 — what stayed native.** `IsLatest` means "index 0 of the returned
//! list", not a property of a feed entry, so it stays Windows-only and outside
//! this record (a field carrying it would go stale across a copy or a take).
//! The `prefix(5)` / `Take(maxCount)` cap stays native on both heads because
//! they cap at different points relative to their caches. `build_number` is a
//! raw `sparkle:version` passthrough for macOS's `AppcastItem.buildNumber` —
//! **a passthrough, not a rule**, because putting a rule there would be
//! inventing shared behaviour with a single consumer.
//!
//! # Panic-free by construction
//!
//! This input is a remote feed and the workspace release profile sets
//! `panic = "abort"`, so the crate denies `clippy::indexing_slicing`,
//! `clippy::unwrap_used` and `clippy::expect_used` (see `Cargo.toml`). Every
//! read below goes through `slice::get`, `split_ascii_whitespace` or
//! `str::parse(...).ok()?`, and every arithmetic step uses a `checked_*` form —
//! including the ones the validated ranges already make unreachable, so the
//! range checks are not the only thing standing between a hostile feed and a
//! crash.

use std::collections::HashSet;

/// One raw `<item>`, exactly as a native XML reader found it. **No rules
/// applied**: every field is the feed's own text, untrimmed, and `None` means
/// the element was absent rather than empty.
///
/// The reader's only job is to fill this in and hand it over in document order.
/// Anything it decides for itself is drift waiting to happen — that is the
/// lesson of the version-rule table above.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct FeedEntry {
    /// `<title>`. The macOS feed puts the version here; the Windows feed puts
    /// `1.11.0 (ARM64)`, and its `<channel><title>` is the app name, which the
    /// reader must not let leak into the first item.
    pub title: Option<String>,
    /// `sparkle:version` — the machine build identifier (`116` on macOS), which
    /// the Windows feed happens to fill with a semver.
    pub sparkle_version: Option<String>,
    /// `sparkle:shortVersionString` — the human-readable version. Present on
    /// every item of both feeds (99/99 and 30/30).
    pub sparkle_short_version_string: Option<String>,
    /// `<pubDate>`, as RFC 2822 text. See [`parse_pub_date`].
    pub pub_date: Option<String>,
    /// `<description>` — the inline release notes, HTML.
    pub description: Option<String>,
    /// Whether `sparkle:releaseNotesLink` was present. Sparkle's own precedence
    /// is link-over-`description`, and neither head's Recent Updates card can
    /// fetch a link, so such an entry has nothing to render (D5).
    pub has_release_notes_link: bool,
}

/// One selected, normalised release. Ready to render.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Release {
    /// D1's resolved version, trimmed and guaranteed non-empty.
    pub version: String,
    /// The raw `sparkle:version`, trimmed, or `None` when it was absent or
    /// blank. A passthrough for macOS's `AppcastItem.buildNumber`; Windows has
    /// no such field. Not a rule — see D6.
    pub build_number: Option<String>,
    /// Seconds since the Unix epoch. **0 when `<pubDate>` was absent, blank or
    /// unparseable** (D2), which sorts the entry last rather than first.
    pub pub_date_epoch_secs: i64,
    /// The inline release notes, trimmed and guaranteed non-empty.
    pub release_notes: String,
}

/// Turn a feed's `<item>`s, in document order, into the releases the update UI
/// renders: **filter (D1, D5), then dedupe (D3), then stable-sort (D4)**.
///
/// The three steps are in that order on purpose; see the module header. The
/// result carries no cap — `prefix(5)` on macOS and `Take(maxCount)` on Windows
/// stay native, because the two heads cap at different points relative to their
/// caches (D6).
#[must_use]
pub fn select_releases(entries: Vec<FeedEntry>) -> Vec<Release> {
    // Step 1 — filter. Every surviving entry has a version and inline notes.
    let mut releases: Vec<Release> = entries.into_iter().filter_map(select_one).collect();

    // Step 2 — dedupe on the resolved version, keeping the first entry in
    // document order. `retain` visits in order and the set makes the keep/drop
    // decision, so document order survives untouched.
    let mut seen: HashSet<String> = HashSet::new();
    releases.retain(|release| seen.insert(release.version.clone()));

    // Step 3 — order. `sort_by` is stable (`sort_unstable_by` is not), so the
    // Windows feed's same-second arch pairs keep document order among
    // themselves. Do not "optimise" this into `sort_unstable_by`.
    releases.sort_by(|a, b| b.pub_date_epoch_secs.cmp(&a.pub_date_epoch_secs));

    releases
}

/// Apply D1, D2 and D5 to one entry. `None` means the entry is dropped.
fn select_one(entry: FeedEntry) -> Option<Release> {
    // D5: a link to the notes means there are no inline notes to render, and it
    // is checked before anything else because it makes the entry unrenderable
    // whatever else it carries.
    if entry.has_release_notes_link {
        return None;
    }

    let release_notes = trimmed_non_empty(entry.description.as_deref())?;

    // D1, in order. The first candidate that is non-blank after trimming wins.
    let version = trimmed_non_empty(entry.sparkle_short_version_string.as_deref())
        .or_else(|| trimmed_non_empty(entry.sparkle_version.as_deref()))
        .or_else(|| trimmed_non_empty(entry.title.as_deref()))?;

    // D2: absent, blank and unparseable all collapse to epoch 0, and none of
    // the three drops the entry.
    let pub_date_epoch_secs = entry
        .pub_date
        .as_deref()
        .and_then(parse_pub_date)
        .unwrap_or(0);

    Some(Release {
        version,
        build_number: trimmed_non_empty(entry.sparkle_version.as_deref()),
        pub_date_epoch_secs,
        release_notes,
    })
}

/// The trimmed value, or `None` when it was absent or blank. Trimming is ASCII
/// whitespace only, matching every other whitespace predicate in this crate.
fn trimmed_non_empty(value: Option<&str>) -> Option<String> {
    let trimmed = value?.trim_matches(|c: char| c.is_ascii_whitespace());
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

/// Parse an RFC 2822 `<pubDate>` into seconds since the Unix epoch, or `None`
/// when it is not a date this accepts.
///
/// Exported rather than private so each head's own suite can assert the
/// malformed-date case without going through [`select_releases`], and so a
/// future caller does not re-implement it.
///
/// Accepted, after trimming the whole string:
///
/// ```text
/// [ <token> "," ] DD SP+ Mon SP+ YYYY SP+ HH ":" MM [ ":" SS ] SP+ ZONE
/// ```
///
/// * **Day-of-week** is optional, comma-terminated, and its content is
///   **ignored**. macOS's `EEE` specifier validated it; validating a token that
///   is fully redundant with the date only creates a way to reject a date that
///   is otherwise valid. It must still be a single token.
/// * **Day** is 1–2 digits, **month** the three-letter English abbreviation
///   matched ASCII-case-insensitively, **year** exactly 4 digits.
/// * **Time** is `HH:MM` or `HH:MM:SS`, two digits each.
/// * **Zone** is `+HHMM` / `-HHMM`, or `Z` / `UT` / `GMT`. The obsolete named
///   US zones (`EST`, `PDT`, …) are **rejected**: the generator never emits
///   them and macOS's `Z` specifier did not accept them either, so accepting
///   them would be new behaviour on both heads.
/// * Everything is range-checked — month 1–12, day valid for the month
///   including leap years, hour 0–23, minute and second 0–59 (no leap seconds).
///
/// **The year is bounded to `1..=9999`, and that is not cosmetic.** The Windows
/// facade converts the result with `DateTimeOffset.FromUnixTimeSeconds`, which
/// **throws** outside .NET's `DateTime` range. This bound, in Rust, is what
/// stops a remote feed throwing inside the facade.
#[must_use]
pub fn parse_pub_date(value: &str) -> Option<i64> {
    let trimmed = value.trim_matches(|c: char| c.is_ascii_whitespace());

    // Drop an optional day-of-week. A comma is legal only there, so anything
    // before it must be a single token — "foo bar, 02 Sep …" is not a
    // day-of-week and is refused rather than silently skipped.
    let rest = match trimmed.split_once(',') {
        Some((day_of_week, rest)) => {
            let day_of_week = day_of_week.trim_matches(|c: char| c.is_ascii_whitespace());
            if day_of_week.is_empty() || day_of_week.contains(char::is_whitespace) {
                return None;
            }
            rest
        }
        None => trimmed,
    };

    // `split_ascii_whitespace` accepts a tab or a newline where the grammar
    // writes `SP+`; the fields themselves are still validated exactly, so this
    // only avoids refusing a date a lenient generator wrote.
    let mut fields = rest.split_ascii_whitespace();
    let day = fields.next()?;
    let month = fields.next()?;
    let year = fields.next()?;
    let time = fields.next()?;
    let zone = fields.next()?;
    if fields.next().is_some() {
        return None;
    }

    let day = parse_digits(day, 1, 2)?;
    let month = parse_month(month)?;
    let year = parse_digits(year, 4, 4)?;
    if !(1..=9999).contains(&year) || !(1..=days_in_month(month, year)).contains(&day) {
        return None;
    }

    let (hour, minute, second) = parse_time(time)?;
    let offset_secs = parse_zone(zone)?;

    let days = days_from_civil(i64::from(year), i64::from(month), i64::from(day))?;
    days.checked_mul(86_400)?
        .checked_add(i64::from(hour).checked_mul(3_600)?)?
        .checked_add(i64::from(minute).checked_mul(60)?)?
        .checked_add(i64::from(second))?
        .checked_sub(offset_secs)
}

/// A run of exactly `min..=max` ASCII digits, as a `u32`. Rejects a sign, a
/// space and any non-ASCII digit, so `"+2"`, `"٢"` and `"2 "` are all refused.
fn parse_digits(value: &str, min: usize, max: usize) -> Option<u32> {
    if value.len() < min || value.len() > max || !value.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    value.parse::<u32>().ok()
}

/// The three-letter English month abbreviation, 1-based. ASCII-case-insensitive
/// — the feeds emit `Sep`, but a generator writing `SEP` is still unambiguous.
fn parse_month(value: &str) -> Option<u32> {
    const MONTHS: [&str; 12] = [
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
    ];
    MONTHS
        .iter()
        .position(|name| name.eq_ignore_ascii_case(value))
        // The index is 0..12, so the +1 cannot overflow a u32; it is written
        // checked anyway, in keeping with the rest of the file.
        .and_then(|index| u32::try_from(index).ok()?.checked_add(1))
}

/// `HH:MM` or `HH:MM:SS`, two digits each, range-checked. Second 60 is rejected
/// — no leap-second support, which matches both heads.
fn parse_time(value: &str) -> Option<(u32, u32, u32)> {
    let mut parts = value.split(':');
    let hour = parse_digits(parts.next()?, 2, 2)?;
    let minute = parse_digits(parts.next()?, 2, 2)?;
    let second = match parts.next() {
        Some(text) => parse_digits(text, 2, 2)?,
        None => 0,
    };
    if parts.next().is_some() || hour > 23 || minute > 59 || second > 59 {
        return None;
    }
    Some((hour, minute, second))
}

/// The zone's offset from UTC, in seconds, to be **subtracted** from the local
/// wall-clock reading.
///
/// `+HHMM` / `-HHMM`, or the three zero-offset names. The offset is itself
/// bounded (`HH <= 23`, `MM <= 59`) so a feed cannot send `+9999` and shift a
/// date by four days.
fn parse_zone(value: &str) -> Option<i64> {
    for name in ["z", "ut", "gmt"] {
        if value.eq_ignore_ascii_case(name) {
            return Some(0);
        }
    }

    let (sign, digits) = match value.split_at_checked(1)? {
        ("+", digits) => (1_i64, digits),
        ("-", digits) => (-1_i64, digits),
        _ => return None,
    };

    let hhmm = parse_digits(digits, 4, 4)?;
    let hours = hhmm.checked_div(100)?;
    let minutes = hhmm.checked_rem(100)?;
    if hours > 23 || minutes > 59 {
        return None;
    }

    i64::from(hours)
        .checked_mul(3_600)?
        .checked_add(i64::from(minutes).checked_mul(60)?)?
        .checked_mul(sign)
}

/// Days in `month` of `year`, with the full Gregorian leap rule.
fn days_in_month(month: u32, year: u32) -> u32 {
    match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) => 29,
        2 => 28,
        _ => 0,
    }
}

/// Howard Hinnant's `days_from_civil`: a proleptic-Gregorian date to a day
/// count relative to 1970-01-01, with no table and no branch on leap years.
///
/// Every step is `checked_*`. With the caller's `1..=9999` year bound none of
/// them can fail, which is the point — the bound is not the only guard.
fn days_from_civil(year: i64, month: i64, day: i64) -> Option<i64> {
    // The algorithm shifts the year so that March is month 1 and the leap day
    // lands at the end of it.
    let shifted = if month <= 2 {
        year.checked_sub(1)?
    } else {
        year
    };
    let era = if shifted >= 0 {
        shifted
    } else {
        shifted.checked_sub(399)?
    }
    .checked_div(400)?;
    let year_of_era = shifted.checked_sub(era.checked_mul(400)?)?; // 0..=399
    let shifted_month = if month > 2 {
        month.checked_sub(3)?
    } else {
        month.checked_add(9)?
    };
    let day_of_year = 153_i64
        .checked_mul(shifted_month)?
        .checked_add(2)?
        .checked_div(5)?
        .checked_add(day)?
        .checked_sub(1)?; // 0..=365
    let day_of_era = year_of_era
        .checked_mul(365)?
        .checked_add(year_of_era.checked_div(4)?)?
        .checked_sub(year_of_era.checked_div(100)?)?
        .checked_add(day_of_year)?; // 0..=146096
    era.checked_mul(146_097)?
        .checked_add(day_of_era)?
        .checked_sub(719_468)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A feed entry with inline notes and nothing else set — the base every
    /// case below varies one field of.
    fn entry() -> FeedEntry {
        FeedEntry {
            description: Some("<ul><li>a</li></ul>".to_string()),
            ..FeedEntry::default()
        }
    }

    fn versions(releases: &[Release]) -> Vec<&str> {
        releases.iter().map(|r| r.version.as_str()).collect()
    }

    // -----------------------------------------------------------------------
    // D1 — version precedence
    // -----------------------------------------------------------------------

    /// All three sources present: `shortVersionString` wins.
    #[test]
    fn short_version_string_wins_over_sparkle_version_and_title() {
        let releases = select_releases(vec![FeedEntry {
            title: Some("t".to_string()),
            sparkle_version: Some("v".to_string()),
            sparkle_short_version_string: Some("s".to_string()),
            ..entry()
        }]);
        assert_eq!(versions(&releases), ["s"]);
    }

    /// Each source missing in turn walks the chain down.
    #[test]
    fn the_version_chain_falls_through_each_missing_source() {
        let releases = select_releases(vec![
            FeedEntry {
                title: Some("t".to_string()),
                sparkle_version: Some("v".to_string()),
                ..entry()
            },
            FeedEntry {
                title: Some("t2".to_string()),
                ..entry()
            },
        ]);
        assert_eq!(versions(&releases), ["v", "t2"]);
    }

    /// A whitespace-only candidate is skipped, not accepted as a blank version.
    #[test]
    fn whitespace_only_candidates_are_skipped() {
        let releases = select_releases(vec![FeedEntry {
            title: Some("  2.46.0  ".to_string()),
            sparkle_version: Some("   ".to_string()),
            sparkle_short_version_string: Some("\n\t".to_string()),
            ..entry()
        }]);
        assert_eq!(versions(&releases), ["2.46.0"]);
        // …and the trimmed value is what is returned.
        assert_eq!(releases[0].build_number, None);
    }

    /// All three blank: the entry is dropped (D5). Both heads drop it today.
    #[test]
    fn an_entry_with_no_usable_version_is_dropped() {
        let releases = select_releases(vec![FeedEntry {
            title: Some(" ".to_string()),
            sparkle_version: Some("".to_string()),
            sparkle_short_version_string: None,
            ..entry()
        }]);
        assert!(releases.is_empty());
    }

    /// The macOS feed's real shape: `shortVersionString` 2.46.0 alongside a
    /// `sparkle:version` of 116. The version must be the former and the build
    /// number the latter.
    #[test]
    fn the_macos_feed_shape_yields_the_displayed_version_not_the_build_number() {
        let releases = select_releases(vec![FeedEntry {
            title: Some("2.46.0".to_string()),
            sparkle_version: Some("116".to_string()),
            sparkle_short_version_string: Some("2.46.0".to_string()),
            pub_date: Some("Wed, 02 Sep 2026 12:06:28 +0000".to_string()),
            ..entry()
        }]);
        assert_eq!(versions(&releases), ["2.46.0"]);
        assert_eq!(releases[0].build_number.as_deref(), Some("116"));
    }

    /// The Windows feed's real shape: the arch suffix lives in `<title>` and
    /// must not leak into the version.
    #[test]
    fn the_windows_feed_shape_keeps_the_arch_suffix_out_of_the_version() {
        let releases = select_releases(vec![FeedEntry {
            title: Some("1.11.0 (ARM64)".to_string()),
            sparkle_version: Some("1.11.0".to_string()),
            sparkle_short_version_string: Some("1.11.0".to_string()),
            pub_date: Some("Sun, 16 Aug 2026 04:17:53 +0000".to_string()),
            ..entry()
        }]);
        assert_eq!(versions(&releases), ["1.11.0"]);
    }

    // -----------------------------------------------------------------------
    // D2 — pubDate fallback
    // -----------------------------------------------------------------------

    /// An absent `<pubDate>` is epoch 0 and the entry SURVIVES. macOS dropped
    /// it; Windows kept it, and Windows wins.
    #[test]
    fn an_absent_pub_date_is_epoch_zero_and_survives() {
        let releases = select_releases(vec![FeedEntry {
            sparkle_short_version_string: Some("1.0.0".to_string()),
            pub_date: None,
            ..entry()
        }]);
        assert_eq!(versions(&releases), ["1.0.0"]);
        assert_eq!(releases[0].pub_date_epoch_secs, 0);
    }

    /// Garbage in `<pubDate>` is epoch 0, and still survives.
    #[test]
    fn an_unparseable_pub_date_is_epoch_zero_and_survives() {
        for text in ["not a date", "", "   ", "Wed, 32 Sep 2026 12:06:28 +0000"] {
            let releases = select_releases(vec![FeedEntry {
                sparkle_short_version_string: Some("1.0.0".to_string()),
                pub_date: Some(text.to_string()),
                ..entry()
            }]);
            assert_eq!(versions(&releases), ["1.0.0"], "input {text:?}");
            assert_eq!(releases[0].pub_date_epoch_secs, 0, "input {text:?}");
        }
    }

    /// The whole reason epoch 0 was chosen over "now": a broken entry sorts
    /// LAST, so it can never displace a real release from the top of the list
    /// or steal Windows' `IsLatest` flag.
    #[test]
    fn a_zero_dated_entry_sorts_last() {
        let releases = select_releases(vec![
            FeedEntry {
                sparkle_short_version_string: Some("broken".to_string()),
                pub_date: Some("nonsense".to_string()),
                ..entry()
            },
            FeedEntry {
                sparkle_short_version_string: Some("older".to_string()),
                pub_date: Some("Fri, 28 Aug 2026 03:54:10 +0000".to_string()),
                ..entry()
            },
            FeedEntry {
                sparkle_short_version_string: Some("newer".to_string()),
                pub_date: Some("Wed, 02 Sep 2026 12:06:28 +0000".to_string()),
                ..entry()
            },
        ]);
        assert_eq!(versions(&releases), ["newer", "older", "broken"]);
    }

    // -----------------------------------------------------------------------
    // D3 — dedupe
    // -----------------------------------------------------------------------

    /// Document order decides a duplicate group, NOT the newest date. The
    /// second entry here is newer and must still lose.
    #[test]
    fn dedupe_keeps_the_first_in_document_order_not_the_newest() {
        let releases = select_releases(vec![
            FeedEntry {
                sparkle_short_version_string: Some("1.11.0".to_string()),
                pub_date: Some("Sun, 16 Aug 2026 04:17:53 +0000".to_string()),
                description: Some("first".to_string()),
                ..entry()
            },
            FeedEntry {
                sparkle_short_version_string: Some("1.11.0".to_string()),
                pub_date: Some("Wed, 02 Sep 2026 12:06:28 +0000".to_string()),
                description: Some("second, newer".to_string()),
                ..entry()
            },
        ]);
        assert_eq!(releases.len(), 1);
        assert_eq!(releases[0].release_notes, "first");
    }

    /// The Windows arch pair: two items, one version, identical dates. The
    /// dedupe that collapses them is why Windows shows 15 releases for 30
    /// items.
    #[test]
    fn the_windows_arch_pair_collapses_to_one_release() {
        let arch = |suffix: &str| FeedEntry {
            title: Some(format!("1.11.0 ({suffix})")),
            sparkle_version: Some("1.11.0".to_string()),
            sparkle_short_version_string: Some("1.11.0".to_string()),
            pub_date: Some("Sun, 16 Aug 2026 04:17:53 +0000".to_string()),
            ..entry()
        };
        assert_eq!(
            versions(&select_releases(vec![arch("ARM64"), arch("x64")])),
            ["1.11.0"]
        );
    }

    // -----------------------------------------------------------------------
    // D4 — ordering
    // -----------------------------------------------------------------------

    /// STABILITY. Equal timestamps keep document order, which is what LINQ's
    /// `OrderByDescending` does on the Windows feed's same-second entries. A
    /// `sort_unstable_by` here would be a silent behaviour change.
    #[test]
    fn equal_timestamps_keep_document_order() {
        let at = |version: &str| FeedEntry {
            sparkle_short_version_string: Some(version.to_string()),
            pub_date: Some("Sun, 16 Aug 2026 04:17:53 +0000".to_string()),
            ..entry()
        };
        let releases = select_releases(vec![at("a"), at("b"), at("c"), at("d"), at("e")]);
        assert_eq!(versions(&releases), ["a", "b", "c", "d", "e"]);
    }

    /// A feed in ascending order comes back descending.
    #[test]
    fn releases_come_back_newest_first() {
        let releases = select_releases(vec![
            FeedEntry {
                sparkle_short_version_string: Some("2.44.0".to_string()),
                pub_date: Some("Sun, 16 Aug 2026 14:08:26 +0000".to_string()),
                ..entry()
            },
            FeedEntry {
                sparkle_short_version_string: Some("2.46.0".to_string()),
                pub_date: Some("Wed, 02 Sep 2026 12:06:28 +0000".to_string()),
                ..entry()
            },
            FeedEntry {
                sparkle_short_version_string: Some("2.45.0".to_string()),
                pub_date: Some("Fri, 28 Aug 2026 03:54:10 +0000".to_string()),
                ..entry()
            },
        ]);
        assert_eq!(versions(&releases), ["2.46.0", "2.45.0", "2.44.0"]);
    }

    // -----------------------------------------------------------------------
    // D5 — drop rules and pipeline order
    // -----------------------------------------------------------------------

    /// A `sparkle:releaseNotesLink` drops the entry even when `<description>`
    /// is non-empty: Sparkle's precedence is link-over-description and no card
    /// here can fetch a link.
    #[test]
    fn a_release_notes_link_drops_the_entry_even_with_a_description() {
        let releases = select_releases(vec![FeedEntry {
            sparkle_short_version_string: Some("1.0.0".to_string()),
            description: Some("<ul><li>real notes</li></ul>".to_string()),
            has_release_notes_link: true,
            ..entry()
        }]);
        assert!(releases.is_empty());
    }

    /// An absent or whitespace-only `<description>` drops the entry.
    #[test]
    fn an_entry_without_inline_notes_is_dropped() {
        for description in [None, Some("".to_string()), Some(" \n\t ".to_string())] {
            let releases = select_releases(vec![FeedEntry {
                sparkle_short_version_string: Some("1.0.0".to_string()),
                description,
                ..FeedEntry::default()
            }]);
            assert!(releases.is_empty());
        }
    }

    /// The notes are trimmed on the way out, matching what macOS already hands
    /// `AppcastItem` and fixing what Windows handed it raw.
    #[test]
    fn the_release_notes_are_trimmed() {
        let releases = select_releases(vec![FeedEntry {
            sparkle_short_version_string: Some("1.0.0".to_string()),
            description: Some("\n   <ul><li>a</li></ul>\n  ".to_string()),
            ..FeedEntry::default()
        }]);
        assert_eq!(releases[0].release_notes, "<ul><li>a</li></ul>");
    }

    /// FILTER BEFORE DEDUPE. The first entry of this duplicate group has no
    /// notes. Dedupe-first would collapse the group onto it and then drop it,
    /// losing the good second entry entirely.
    #[test]
    fn filtering_happens_before_dedupe() {
        let releases = select_releases(vec![
            FeedEntry {
                sparkle_short_version_string: Some("1.11.0".to_string()),
                description: None,
                ..FeedEntry::default()
            },
            FeedEntry {
                sparkle_short_version_string: Some("1.11.0".to_string()),
                description: Some("the good one".to_string()),
                ..FeedEntry::default()
            },
        ]);
        assert_eq!(releases.len(), 1);
        assert_eq!(releases[0].release_notes, "the good one");
    }

    /// An empty feed is an empty list, not a panic.
    #[test]
    fn an_empty_feed_yields_no_releases() {
        assert!(select_releases(Vec::new()).is_empty());
    }

    // -----------------------------------------------------------------------
    // parse_pub_date
    // -----------------------------------------------------------------------

    /// Both live feeds' real date strings, against values computed
    /// independently of this implementation.
    #[test]
    fn the_real_feed_dates_parse() {
        assert_eq!(
            parse_pub_date("Wed, 02 Sep 2026 12:06:28 +0000"),
            Some(1_788_350_788)
        );
        assert_eq!(
            parse_pub_date("Sun, 16 Aug 2026 04:17:53 +0000"),
            Some(1_786_853_873)
        );
    }

    /// A non-zero offset is SUBTRACTED from the wall-clock reading: the same
    /// instant, written in two zones, is one number.
    #[test]
    fn offsets_are_applied() {
        let utc = parse_pub_date("Wed, 02 Sep 2026 12:06:28 +0000");
        assert_eq!(parse_pub_date("Wed, 02 Sep 2026 21:06:28 +0900"), utc);
        assert_eq!(parse_pub_date("Wed, 02 Sep 2026 07:06:28 -0500"), utc);
        assert_ne!(parse_pub_date("Wed, 02 Sep 2026 12:06:28 +0900"), utc);
    }

    /// `Z`, `UT` and `GMT` are all zero offsets, in any ASCII case.
    #[test]
    fn the_zero_offset_zone_names_are_accepted() {
        let utc = parse_pub_date("Wed, 02 Sep 2026 12:06:28 +0000");
        for zone in ["Z", "z", "UT", "ut", "GMT", "gmt"] {
            assert_eq!(
                parse_pub_date(&format!("Wed, 02 Sep 2026 12:06:28 {zone}")),
                utc,
                "zone {zone:?}"
            );
        }
    }

    /// The obsolete named US zones are REJECTED. macOS's `Z` specifier did not
    /// accept them either, so accepting them would be new behaviour.
    #[test]
    fn named_us_zones_are_rejected() {
        for zone in ["EST", "EDT", "CST", "CDT", "MST", "MDT", "PST", "PDT"] {
            assert_eq!(
                parse_pub_date(&format!("Wed, 02 Sep 2026 12:06:28 {zone}")),
                None,
                "zone {zone:?}"
            );
        }
    }

    /// Seconds are optional.
    #[test]
    fn the_seconds_field_may_be_omitted() {
        assert_eq!(
            parse_pub_date("Wed, 02 Sep 2026 12:06 +0000"),
            Some(1_788_350_760)
        );
    }

    /// The day-of-week is ignored, so a feed whose day name is simply wrong
    /// still parses — 02 Sep 2026 is a Wednesday, not a Monday.
    #[test]
    fn a_bogus_day_name_is_accepted_and_ignored() {
        let expected = parse_pub_date("Wed, 02 Sep 2026 12:06:28 +0000");
        assert_eq!(parse_pub_date("Mon, 02 Sep 2026 12:06:28 +0000"), expected);
        assert_eq!(parse_pub_date("Xyz, 02 Sep 2026 12:06:28 +0000"), expected);
        // …and it is optional entirely.
        assert_eq!(parse_pub_date("02 Sep 2026 12:06:28 +0000"), expected);
    }

    /// A day-of-week that is not one token is not a day-of-week.
    #[test]
    fn a_multi_token_day_of_week_is_rejected() {
        assert_eq!(parse_pub_date("Wed x, 02 Sep 2026 12:06:28 +0000"), None);
        assert_eq!(parse_pub_date(", 02 Sep 2026 12:06:28 +0000"), None);
    }

    /// Every out-of-range field is refused rather than wrapped.
    #[test]
    fn out_of_range_fields_are_rejected() {
        let cases = [
            "Wed, 30 Feb 2026 12:06:28 +0000", // day past the month's end
            "Wed, 29 Feb 2026 12:06:28 +0000", // 2026 is not a leap year
            "Wed, 32 Jan 2026 12:06:28 +0000",
            "Wed, 00 Jan 2026 12:06:28 +0000",
            "Wed, 02 Foo 2026 12:06:28 +0000",  // month 13, spelled
            "Wed, 02 Sep 2026 24:06:28 +0000",  // hour 24
            "Wed, 02 Sep 2026 12:60:28 +0000",  // minute 60
            "Wed, 02 Sep 2026 12:06:60 +0000",  // second 60, no leap seconds
            "Wed, 02 Sep 0000 12:06:28 +0000",  // year 0
            "Wed, 02 Sep 10000 12:06:28 +0000", // year 10000
            "Wed, 02 Sep 999 12:06:28 +0000",   // year is exactly 4 digits
            "Wed, 02 Sep 2026 12:06:28 +2500",  // zone hour out of range
            "Wed, 02 Sep 2026 12:06:28 +0060",  // zone minute out of range
            "Wed, 02 Sep 2026 12:06:28 +000",   // zone is exactly 4 digits
            "Wed, 02 Sep 2026 12:06:28",        // no zone at all
            "Wed, 02 Sep 2026 12:06:28 +0000 extra",
            "Wed, 02 Sep 2026 1:06:28 +0000", // hour must be two digits
            "Wed, 02 Sep 2026 12:06:28:99 +0000",
            "Wed, 002 Sep 2026 12:06:28 +0000", // day is one or two digits
            "",
            "   ",
            "not a date at all",
        ];
        for case in cases {
            assert_eq!(parse_pub_date(case), None, "input {case:?}");
        }
    }

    /// A leap day in a leap year is accepted, and the leap rule is the full
    /// Gregorian one at both century boundaries.
    #[test]
    fn leap_days_are_accepted_in_leap_years() {
        assert!(parse_pub_date("Thu, 29 Feb 2024 00:00:00 +0000").is_some());
        assert!(parse_pub_date("Tue, 29 Feb 2000 00:00:00 +0000").is_some()); // /400
        assert_eq!(parse_pub_date("Wed, 29 Feb 1900 00:00:00 +0000"), None); // /100
        assert_eq!(
            parse_pub_date("Thu, 29 Feb 2024 00:00:00 +0000"),
            Some(1_709_164_800)
        );
    }

    /// The year bound's two ends, which is what keeps Windows'
    /// `DateTimeOffset.FromUnixTimeSeconds` from throwing.
    #[test]
    fn the_year_bound_holds_at_both_ends() {
        assert_eq!(
            parse_pub_date("Mon, 01 Jan 0001 00:00:00 +0000"),
            Some(-62_135_596_800)
        );
        assert_eq!(
            parse_pub_date("Fri, 31 Dec 9999 23:59:59 +0000"),
            Some(253_402_300_799)
        );
    }

    /// Pre-epoch dates go negative rather than wrapping.
    #[test]
    fn a_pre_epoch_date_is_negative() {
        assert_eq!(parse_pub_date("Thu, 01 Jan 1970 00:00:00 +0000"), Some(0));
        assert_eq!(parse_pub_date("Wed, 31 Dec 1969 23:59:59 +0000"), Some(-1));
    }

    /// Surrounding whitespace and a lowercase month are both tolerated.
    #[test]
    fn the_whole_string_is_trimmed_and_the_month_is_case_insensitive() {
        let expected = parse_pub_date("Wed, 02 Sep 2026 12:06:28 +0000");
        assert_eq!(
            parse_pub_date("  \n Wed,  02  SEP  2026  12:06:28  +0000 \t "),
            expected
        );
        assert_eq!(parse_pub_date("Wed, 02 sep 2026 12:06:28 +0000"), expected);
    }

    /// Non-ASCII digits are not digits. `٢٠٢٦` is 2026 to a human and must not
    /// be to the parser.
    #[test]
    fn non_ascii_digits_are_rejected() {
        assert_eq!(parse_pub_date("Wed, 02 Sep ٢٠٢٦ 12:06:28 +0000"), None);
        assert_eq!(parse_pub_date("Wed, +2 Sep 2026 12:06:28 +0000"), None);
    }
}
