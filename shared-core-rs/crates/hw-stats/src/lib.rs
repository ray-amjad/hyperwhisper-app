//! `hw-stats` — the home statistics formulas, once.
//!
//! Three copies existed and they disagreed five separate ways (#285):
//! `HomeStatsBar.swift:163-169` (macOS), `HomeStatsBarViewModel.cs:160-174`
//! (Windows) and `HomeStatistics.cs:117-171` (the shared .NET calculator that
//! Linux uses and Windows already compiles but never called). The drifts are
//! listed on [`calculate`], and each one has a `decision` row in
//! `shared-conformance/stats-vectors.json`.
//!
//! # What stays native
//!
//! * **Word counting.** There is no persisted word count on any of the three
//!   stores, and the two native implementations already agree
//!   (`.whitespacesAndNewlines` / `Split((char[])null)`), so the host counts and
//!   passes [`Transcript::word_count`]. Issue #285 records why: sharing it would
//!   need a schema migration and a backfill on three stores.
//! * **The time-zone database.** The host converts each row's instant into the
//!   calendar time zone and passes the result as
//!   [`Transcript::created_at_local_epoch_seconds`] — seconds since the epoch
//!   read as local wall-clock time. That is the one line each platform already
//!   writes (`TimeZoneInfo.ConvertTime` / `TimeZone.secondsFromGMT(for:)`), and
//!   it keeps DST correct per row without a tz-database dependency here. Every
//!   boundary *above* that conversion — Monday, the 1st, January 1st — is
//!   computed here, in [`calendar`].
//!
//! # Panic-free by construction
//!
//! `indexing_slicing`, `unwrap_used` and `expect_used` are denied for this
//! package (see `Cargo.toml`), and the crate root re-allows them under
//! `cfg(test)`. Every arithmetic path is either float (guarded finite) or a
//! saturating integer operation: the workspace release profile sets
//! `panic = "abort"`, so a bad row must produce a wrong number, never a crash.

#![cfg_attr(
    test,
    allow(clippy::indexing_slicing, clippy::unwrap_used, clippy::expect_used)
)]

mod calendar;
mod formulas;
mod snapshot;

pub use snapshot::{
    calculate, HomeStatsSnapshot, PeriodStats, Transcript, TranscriptStatus, SAVED_MINUTES_CEILING,
};
