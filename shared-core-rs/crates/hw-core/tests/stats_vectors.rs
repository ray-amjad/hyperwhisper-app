//! Conformance-vector tests for the shared home statistics.
//!
//! `shared-conformance/stats-vectors.json` is the cross-platform source of
//! truth for the formulas issue #285 moved into `hw-stats`. It is a **decision
//! table**, not just a golden file: every row carries a `decision` field naming
//! which of the three native copies the unified answer came from —
//!
//! * `agreed` — `HomeStatsBar.swift`, `HomeStatsBarViewModel.cs` and
//!   `HomeStatistics.cs` already did this, and so do we.
//! * `macos` — the copies disagreed and the macOS behaviour is the
//!   documented-correct one.
//! * `dotnet` — the copies disagreed and the .NET behaviour is the
//!   documented-correct one.
//! * `neither` — every copy was wrong in the same way, and this row is the fix.
//! * `new` — no copy had a rule here; the unified core picks one and pins it.
//!
//! Read the `decision` column before the inputs. Every row that is not `agreed`
//! is a behaviour change that belongs in the pull-request notes, and the
//! coverage test below fails if any of those four buckets loses its last row.
//!
//! # Two conventions the JSON needs
//!
//! * `durationSeconds: null` means the store held a value that is not a finite
//!   positive number — NaN or an infinity. Which one does not matter: both
//!   normalise to 0, and JSON cannot carry either. A negative duration IS
//!   representable, so it is written as a plain negative number.
//! * Every instant is **local** epoch seconds. The host shifts each row into the
//!   calendar time zone before it calls; see `hw_stats`'s crate docs for why.
//!
//! Regenerate after an intended policy change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-core --test stats_vectors -- --ignored regenerate
//! ```
//!
//! Then read the diff. An expectation that moves without a matching `hw-stats`
//! edit is a regression, not a refresh.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use hyperwhisper_core::ffi_stats::{stats_calculate_home, HwStatsTranscript, HwTranscriptStatus};

const VECTORS_PATH: &str = "../../../shared-conformance/stats-vectors.json";

fn vectors_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH)
}

// ---------------------------------------------------------------------------
// Vector shapes. Flat and explicit so the JSON reads as data a human can review
// in a pull request.
// ---------------------------------------------------------------------------

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct Document {
    description: String,
    cases: Vec<CaseVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct CaseVector {
    /// What this row proves.
    name: String,
    /// Which native copy the unified answer came from. See the module docs.
    decision: String,
    /// One sentence on what each copy used to do. Empty for `agreed` rows.
    was: String,
    /// The user's gear-menu typing speed.
    typing_speed_words_per_minute: i32,
    /// "Now", in local epoch seconds.
    now_local_epoch_seconds: i64,
    transcripts: Vec<TranscriptVector>,
    expected: SnapshotVector,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct TranscriptVector {
    created_at_local_epoch_seconds: i64,
    word_count: u32,
    /// `null` means a non-finite stored value. See the module docs.
    duration_seconds: Option<f64>,
    /// `processing`, `completed` or `failed`.
    status: String,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct SnapshotVector {
    this_week: PeriodVector,
    this_month: PeriodVector,
    this_year: PeriodVector,
    all_time: PeriodVector,
    typing_speed_words_per_minute: i32,
    average_words_per_minute: i32,
    saved_this_week_minutes: i32,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct PeriodVector {
    word_count: u32,
    duration_seconds: f64,
    average_words_per_minute: i32,
    estimated_typing_minutes: f64,
    estimated_time_saved_minutes: f64,
}

// ---------------------------------------------------------------------------
// Decision labels. One constant per bucket so a typo cannot silently create a
// new, uncounted one.
// ---------------------------------------------------------------------------

const AGREED: &str = "agreed";
const MACOS: &str = "macos";
const DOTNET: &str = "dotnet";
const NEITHER: &str = "neither";
const NEW: &str = "new";

const DECISIONS: [&str; 5] = [AGREED, MACOS, DOTNET, NEITHER, NEW];

/// The buckets that MUST keep at least one row: each is a behaviour change the
/// pull-request notes list, and a vector set that loses its last one stops
/// proving the change happened.
const CHANGED: [&str; 4] = [MACOS, DOTNET, NEITHER, NEW];

// ---------------------------------------------------------------------------
// The reference clock. 2026-08-29 00:00 local is a SATURDAY, so the four
// boundaries are all visible at once and none of them coincide:
//   week  -> Mon 2026-08-24 .. Mon 2026-08-31
//   month -> 2026-08-01 .. 2026-09-01
//   year  -> 2026-01-01 .. 2027-01-01
// ---------------------------------------------------------------------------

const SATURDAY: i64 = 1_787_961_600;
const DAY: i64 = 86_400;

fn completed(offset_days: i64, words: u32, duration_seconds: f64) -> TranscriptVector {
    TranscriptVector {
        created_at_local_epoch_seconds: SATURDAY + offset_days * DAY,
        word_count: words,
        duration_seconds: Some(duration_seconds),
        status: "completed".to_string(),
    }
}

fn with_status(status: &str, row: TranscriptVector) -> TranscriptVector {
    TranscriptVector {
        status: status.to_string(),
        ..row
    }
}

fn without_finite_duration(offset_days: i64, words: u32) -> TranscriptVector {
    TranscriptVector {
        duration_seconds: None,
        ..completed(offset_days, words, 0.0)
    }
}

// ---------------------------------------------------------------------------
// The inputs. Expectations are NOT written here — `build_document` fills them
// from the shared core, and the committed file is what review reads.
// ---------------------------------------------------------------------------

struct Case {
    name: &'static str,
    decision: &'static str,
    was: &'static str,
    typing_speed: i32,
    transcripts: Vec<TranscriptVector>,
}

fn cases() -> Vec<Case> {
    vec![
        // --- Rows every copy already agreed on ---------------------------
        Case {
            name: "no transcripts is every figure at zero",
            decision: AGREED,
            was: "",
            typing_speed: 40,
            transcripts: vec![],
        },
        Case {
            name: "average WPM is the all-time words over the all-time minutes",
            decision: AGREED,
            was: "",
            typing_speed: 40,
            transcripts: vec![completed(0, 150, 60.0), completed(-200, 150, 60.0)],
        },
        Case {
            name: "a period with words but no spoken time reports 0 WPM, not a division",
            decision: AGREED,
            was: "",
            typing_speed: 40,
            transcripts: vec![completed(0, 120, 0.0)],
        },
        Case {
            name: "saved minutes are floored at zero when speaking was slower than typing",
            decision: AGREED,
            was: "",
            typing_speed: 40,
            transcripts: vec![completed(0, 40, 120.0)],
        },
        Case {
            name: "a typing speed of zero zeroes the savings but not the speed figures",
            decision: AGREED,
            was: "",
            typing_speed: 0,
            transcripts: vec![completed(0, 300, 60.0)],
        },
        Case {
            name: "only completed rows count",
            decision: AGREED,
            was: "macOS applied this in the @FetchRequest predicate rather than in the \
                  calculation, so the rule lived in a SwiftUI property wrapper on one head \
                  and in the calculator on the other two. It is one rule now.",
            typing_speed: 40,
            transcripts: vec![
                completed(0, 100, 60.0),
                with_status("processing", completed(0, 500, 60.0)),
                with_status("failed", completed(0, 900, 60.0)),
            ],
        },
        // --- Rows where the copies disagreed -----------------------------
        Case {
            name: "2.5 saved minutes rounds up to 3, not down to 2",
            decision: MACOS,
            was: "Swift .rounded() is half away from zero; C# Math.Round(double) is \
                  banker's rounding, so the same week showed 3 minutes saved on macOS \
                  and 2 on Windows and Linux. 200 words at 40 WPM is 5 typed minutes \
                  against 2.5 spoken.",
            typing_speed: 40,
            transcripts: vec![completed(0, 200, 150.0)],
        },
        Case {
            name: "a words-without-duration row is clamped to one week of minutes",
            decision: DOTNET,
            was: "HomeStatsBarViewModel.cs:30 and HomeStatistics.cs:67 clamp to 7*24*60 \
                  and the Windows comment says why; macOS had no ceiling, so one such row \
                  put a five-figure number in the home strip.",
            typing_speed: 40,
            transcripts: vec![completed(0, 4_000_000, 0.0)],
        },
        Case {
            name: "a non-finite duration contributes zero seconds instead of poisoning the sum",
            decision: DOTNET,
            was: "Only HomeStatistics.cs:128-129 guarded IsFinite && > 0. macOS summed the \
                  raw value and then called Int(saved.rounded()), which TRAPS on NaN — one \
                  corrupt row crashed the home view at HomeStatsBar.swift:168.",
            typing_speed: 40,
            transcripts: vec![without_finite_duration(0, 100), completed(0, 50, 60.0)],
        },
        Case {
            name: "a negative duration contributes zero seconds",
            decision: DOTNET,
            was: "Same guard, the representable half of it.",
            typing_speed: 40,
            transcripts: vec![completed(0, 10, -5.0), completed(0, 50, 60.0)],
        },
        Case {
            name: "the week starts on Monday, so the Sunday that closes it is still inside",
            decision: DOTNET,
            was: "HomeStatistics.cs:122-126 used the local-time-zone Monday and \
                  StatisticsService.cs:109-113 used a UTC-forced Monday; macOS asked \
                  Calendar.current for .weekOfYear, which starts on Sunday in en-US. So \
                  this row counted towards NEXT week on macOS and this week elsewhere.",
            typing_speed: 40,
            transcripts: vec![completed(1, 60, 60.0)],
        },
        Case {
            name: "the Sunday before the Monday is last week, and still this month",
            decision: DOTNET,
            was: "The other side of the same boundary: on macOS's Sunday-start week this \
                  row was inside the current week.",
            typing_speed: 40,
            transcripts: vec![completed(-5, 60, 60.0), completed(-6, 70, 60.0)],
        },
        // --- Rows every copy got wrong -----------------------------------
        Case {
            name: "an absurd word total saturates instead of trapping",
            decision: NEITHER,
            was: "HomeStatistics.cs:148 summed with `checked`, which THROWS on overflow, \
                  and Swift's += traps. Both turn a corrupt store into a crash on the \
                  home view's render path. A saturated total is a wrong number the user \
                  can see and report. Three rows rather than one huge one: every row \
                  count here has to stay inside a 32-bit SIGNED integer so the .NET \
                  head can replay this case, and only their sum overflows.",
            typing_speed: 40,
            transcripts: vec![
                completed(0, 2_000_000_000, 60.0),
                completed(0, 2_000_000_000, 60.0),
                completed(0, 2_000_000_000, 60.0),
            ],
        },
        // --- Rows no copy had --------------------------------------------
        Case {
            name: "the four periods nest without leaking into one another",
            decision: NEW,
            was: "No copy produced all four periods: macOS computed week, month, year and \
                  all-time in the view; the .NET calculator computed week, month and \
                  all-time and had no year. Every head gets every period now, and which \
                  columns it shows stays a layout decision.",
            typing_speed: 40,
            transcripts: vec![
                completed(0, 10, 60.0),
                completed(-10, 20, 60.0),
                completed(-60, 40, 60.0),
                completed(-300, 80, 60.0),
            ],
        },
        Case {
            name: "the per-period saving is not clamped, only the displayed week figure is",
            decision: NEW,
            was: "Neither .NET copy exposed both numbers, so nothing pinned the difference. \
                  The ceiling is a display rule for the home strip; the statistics page's \
                  per-period estimate keeps the raw figure.",
            typing_speed: 40,
            transcripts: vec![completed(0, 500_000, 0.0)],
        },
        Case {
            name: "a row dated in the future is bucketed by its own date, not dropped",
            decision: NEW,
            was: "No copy had a rule. A clock change or an imported backup can date a row \
                  ahead of now; it is a real transcript, so it counts, but it lands in \
                  September, outside this week and this month.",
            typing_speed: 40,
            transcripts: vec![completed(9, 90, 60.0), completed(0, 10, 60.0)],
        },
        Case {
            name: "a row from before the epoch is bucketed by its own calendar day",
            decision: NEW,
            was: "No copy had a rule, and a naive integer division would have floored a \
                  negative timestamp towards zero and moved the row a day.",
            typing_speed: 40,
            transcripts: vec![TranscriptVector {
                created_at_local_epoch_seconds: -1,
                word_count: 25,
                duration_seconds: Some(60.0),
                status: "completed".to_string(),
            }],
        },
    ]
}

fn to_ffi_status(status: &str) -> HwTranscriptStatus {
    match status {
        "processing" => HwTranscriptStatus::Processing,
        "failed" => HwTranscriptStatus::Failed,
        _ => HwTranscriptStatus::Completed,
    }
}

fn to_ffi(transcripts: &[TranscriptVector]) -> Vec<HwStatsTranscript> {
    transcripts
        .iter()
        .map(|row| HwStatsTranscript {
            created_at_local_epoch_seconds: row.created_at_local_epoch_seconds,
            word_count: row.word_count,
            // `null` stands for any non-finite stored value; NaN is the one that
            // trapped on macOS, so it is the one the generator replays.
            duration_seconds: row.duration_seconds.unwrap_or(f64::NAN),
            status: to_ffi_status(&row.status),
        })
        .collect()
}

fn run(case: &Case) -> SnapshotVector {
    let snapshot = stats_calculate_home(to_ffi(&case.transcripts), case.typing_speed, SATURDAY);
    SnapshotVector {
        this_week: period(&snapshot.this_week),
        this_month: period(&snapshot.this_month),
        this_year: period(&snapshot.this_year),
        all_time: period(&snapshot.all_time),
        typing_speed_words_per_minute: snapshot.typing_speed_words_per_minute,
        average_words_per_minute: snapshot.average_words_per_minute,
        saved_this_week_minutes: snapshot.saved_this_week_minutes,
    }
}

fn period(stats: &hyperwhisper_core::ffi_stats::HwPeriodStats) -> PeriodVector {
    PeriodVector {
        word_count: stats.word_count,
        duration_seconds: stats.duration_seconds,
        average_words_per_minute: stats.average_words_per_minute,
        estimated_typing_minutes: stats.estimated_typing_minutes,
        estimated_time_saved_minutes: stats.estimated_time_saved_minutes,
    }
}

fn build_document() -> Document {
    Document {
        description: "Golden home-statistics vectors (issue #285). Generated from hw-stats \
            by `cargo test -p hw-core --test stats_vectors -- --ignored regenerate`. Every \
            case carries a `decision` field naming which native copy the unified answer \
            came from: agreed / macos / dotnet / neither / new. Every instant is LOCAL \
            epoch seconds — the host shifts each row into the calendar time zone before it \
            calls. A `durationSeconds` of null means the store held a non-finite value."
            .to_string(),
        cases: cases()
            .iter()
            .map(|case| CaseVector {
                name: case.name.to_string(),
                decision: case.decision.to_string(),
                was: case.was.to_string(),
                typing_speed_words_per_minute: case.typing_speed,
                now_local_epoch_seconds: SATURDAY,
                expected: run(case),
                transcripts: case.transcripts.clone(),
            })
            .collect(),
    }
}

fn load_document() -> Document {
    let raw = std::fs::read_to_string(vectors_path())
        .expect("shared-conformance/stats-vectors.json must exist");
    serde_json::from_str(&raw).expect("stats-vectors.json must parse")
}

/// Writes the vectors from the current shared-core answer. Ignored by default;
/// run it deliberately after an intended policy change, then read the diff.
#[test]
#[ignore = "regenerates shared-conformance/stats-vectors.json"]
fn regenerate() {
    let doc = build_document();
    let mut json = serde_json::to_string_pretty(&doc).expect("vectors must serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("vectors must be writable");
    eprintln!("wrote {}", vectors_path().display());
}

/// The committed vectors are exactly what the shared core answers today. This
/// is the whole point of the file: it fails on a behaviour change that was not
/// deliberately regenerated and reviewed.
#[test]
fn vectors_match_the_shared_core() {
    let expected = load_document();
    let actual = build_document();

    assert_eq!(
        expected.cases.len(),
        actual.cases.len(),
        "the committed vectors and the generator disagree on the case list — regenerate"
    );
    for (want, got) in expected.cases.iter().zip(actual.cases.iter()) {
        assert_eq!(want.name, got.name, "case order changed — regenerate");
        assert_eq!(
            want.transcripts, got.transcripts,
            "input rows changed for {:?}",
            want.name
        );
        assert_eq!(
            want.typing_speed_words_per_minute, got.typing_speed_words_per_minute,
            "input typing speed changed for {:?}",
            want.name
        );
        assert_eq!(
            want.now_local_epoch_seconds, got.now_local_epoch_seconds,
            "the reference clock moved for {:?}",
            want.name
        );
        assert_eq!(
            want.expected, got.expected,
            "answer changed for {:?}",
            want.name
        );
    }
}

/// A decision table is only proof while it still has a row in every bucket that
/// records a behaviour change. This fails if a future edit deletes the last one.
#[test]
fn every_decision_bucket_keeps_a_row() {
    let doc = load_document();

    let unknown: Vec<&str> = doc
        .cases
        .iter()
        .map(|case| case.decision.as_str())
        .filter(|decision| !DECISIONS.contains(decision))
        .collect();
    assert!(unknown.is_empty(), "unknown decision labels: {unknown:?}");

    for decision in CHANGED {
        let count = doc
            .cases
            .iter()
            .filter(|case| case.decision == decision)
            .count();
        assert!(
            count > 0,
            "no {decision:?} row left — that behaviour change is no longer proven"
        );
    }

    for case in &doc.cases {
        if case.decision != AGREED {
            assert!(
                !case.was.trim().is_empty(),
                "{:?} is a {:?} row with no `was` note",
                case.name,
                case.decision
            );
        }
    }
}

/// The reference clock has to be the Saturday the module docs claim, or every
/// boundary row proves nothing.
#[test]
fn the_reference_clock_is_a_saturday() {
    // Ask the core: a row dated the next day (Sunday) must be inside the same
    // week, and a row six days earlier (the previous Sunday) must not be.
    let snapshot = stats_calculate_home(
        vec![
            HwStatsTranscript {
                created_at_local_epoch_seconds: SATURDAY + DAY,
                word_count: 1,
                duration_seconds: 60.0,
                status: HwTranscriptStatus::Completed,
            },
            HwStatsTranscript {
                created_at_local_epoch_seconds: SATURDAY - 6 * DAY,
                word_count: 100,
                duration_seconds: 60.0,
                status: HwTranscriptStatus::Completed,
            },
        ],
        40,
        SATURDAY,
    );
    assert_eq!(snapshot.this_week.word_count, 1);
    assert_eq!(snapshot.all_time.word_count, 101);
}
