//! Fuzz every `hw-releasenotes` entry point over arbitrary input.
//!
//! What this proves: the parser never panics. That matters more here than in
//! most crates, because the shared-core release profile sets `panic = "abort"`
//! and the input is a remote appcast feed — a panic is a crash on a hostile or
//! merely malformed feed, with no unwinding and no Sentry breadcrumb.
//!
//! The corpus in `corpus/parse/` is seeded from BOTH oracle suites
//! (`ReleaseNotesHTMLTests.swift` and the `InlineHtml` block of the Windows
//! `Program.cs`), plus the two entity drift cases and fragments of the two live
//! feeds. One file per case, committed — cargo-fuzz's generated `.gitignore`
//! excludes `corpus/` by default and this crate's re-allows it on purpose.
//!
//! Run:
//!   cargo +nightly fuzz run parse -- -max_total_time=300
//!
//! CI never runs this. See fuzz/Cargo.toml for why this package sits outside the
//! shared-core workspace.

#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    // Non-UTF-8 bytes are not reachable through the FFI boundary (UniFFI hands
    // Rust a `String`), so lossy-decode rather than discard the input: it keeps
    // every mutation the engine produces useful instead of throwing most of them
    // away, and U+FFFD is itself a scalar the parser must survive.
    let html = String::from_utf8_lossy(data);

    // Both modes, off one corpus. `collapse_whitespace` is a plain parameter, so
    // exercising both here costs one extra pass and covers the C# `false` branch
    // that no head calls today.
    for collapse in [true, false] {
        let runs = hw_releasenotes::parse_inline(&html, collapse);
        let text = hw_releasenotes::plain_text(&html, collapse);

        // An invariant worth asserting while we are here: `plain_text` is
        // exactly the concatenation of the runs' text. If that ever stops
        // holding, the two entry points have drifted apart.
        let joined: String = runs.iter().map(|r| r.text.as_str()).collect();
        assert_eq!(joined, text);

        // No run is ever empty — `flush` drops an empty buffer — and every link
        // that survived carries an allow-listed scheme.
        for run in &runs {
            assert!(!run.text.is_empty());
            if let Some(link) = &run.link {
                let scheme = link
                    .split_once(':')
                    .map(|(head, _)| head.to_ascii_lowercase())
                    .unwrap_or_default();
                assert!(
                    matches!(scheme.as_str(), "http" | "https" | "mailto"),
                    "link escaped the scheme allowlist: {link:?}"
                );
            }
        }
    }

    // The block layer, over the same input. It scans tags itself rather than
    // going through `parse_inline`, so it has its own index arithmetic and its
    // own way to fail to terminate — a `<li>` whose close tag is never found
    // must still advance the cursor.
    let blocks = hw_releasenotes::split_blocks(&html);
    let note = hw_releasenotes::parse_release_note(&html);

    // A block is never empty. Both entry points drop a block that carries no
    // text, which is what keeps "<li>  </li>" out of a bullet list — and both
    // heads render a block without checking, so an empty one is a blank row.
    let every_block = blocks
        .iter()
        .chain(note.title.iter())
        .chain(note.bullets.iter());

    for block in every_block {
        assert!(!block.runs.is_empty(), "empty block from {html:?}");
        for run in &block.runs {
            assert!(!run.text.is_empty());
            if let Some(link) = &run.link {
                let scheme = link
                    .split_once(':')
                    .map(|(head, _)| head.to_ascii_lowercase())
                    .unwrap_or_default();
                assert!(
                    matches!(scheme.as_str(), "http" | "https" | "mailto"),
                    "link escaped the scheme allowlist: {link:?}"
                );
            }
        }
    }

    // Only `<li>` becomes a bullet, and `parse_release_note` never falls back to
    // the line splitter — a card with no list renders its title and nothing
    // else, rather than repeating the whole note underneath itself.
    assert!(note
        .bullets
        .iter()
        .all(|block| block.kind == hw_releasenotes::BlockKind::Bullet));
    assert!(note
        .title
        .iter()
        .all(|block| block.kind == hw_releasenotes::BlockKind::Heading));

    // The selection layer (#353), over the same corpus. Its input is remote too
    // — a `<pubDate>` is attacker-influenced text and the date arithmetic is
    // hand-written, so this is where an unchecked `*` or a bad slice index
    // would abort the app.
    if let Some(secs) = hw_releasenotes::parse_pub_date(&html) {
        // The bound is what stops Windows' `DateTimeOffset.FromUnixTimeSeconds`
        // throwing, so assert the range rather than merely that we did not
        // panic. It is asserted on the RETURNED instant, exactly as the parser
        // applies it — an earlier version of this assertion widened the range
        // by a day at each end to allow for the zone offset, which is precisely
        // the defect: a `-0100` on 9999-12-31 produced a value .NET cannot
        // hold, and this assertion passed it.
        assert!(
            (hw_releasenotes::MIN_REPRESENTABLE_EPOCH_SECS
                ..=hw_releasenotes::MAX_REPRESENTABLE_EPOCH_SECS)
                .contains(&secs),
            "date out of the DateTimeOffset-representable range: {html:?} -> {secs}"
        );
    }

    // One entry per input, with the same string in every field: the cheapest
    // way to reach every branch of the version chain and the drop rules from a
    // flat byte string. Two copies, so dedupe and the stable sort run too.
    let entry = || hw_releasenotes::FeedEntry {
        title: Some(html.to_string()),
        sparkle_version: Some(html.to_string()),
        sparkle_short_version_string: Some(html.to_string()),
        pub_date: Some(html.to_string()),
        description: Some(html.to_string()),
        has_release_notes_link: false,
    };
    let releases = hw_releasenotes::select_releases(vec![entry(), entry()]);

    // Post-conditions the heads rely on: at most one release per version, and
    // never an empty version or empty notes — both heads render these without
    // checking.
    assert!(
        releases.len() <= 1,
        "dedupe let a duplicate version through"
    );
    for release in &releases {
        assert!(!release.version.is_empty());
        assert!(!release.release_notes.is_empty());
        // ASCII whitespace, matching the crate's own trim predicate — U+00A0 is
        // whitespace to `str::trim` but deliberately not to this crate.
        let ascii = |c: char| c.is_ascii_whitespace();
        assert_eq!(release.version.trim_matches(ascii), release.version);
        assert_eq!(
            release.release_notes.trim_matches(ascii),
            release.release_notes
        );
    }
});
