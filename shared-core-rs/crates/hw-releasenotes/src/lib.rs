//! `hw-releasenotes` — the release-notes HTML parser shared by every head.
//!
//! The appcast feeds carry a small slice of HTML for release notes. This crate
//! turns it into styled runs the UI can render, and is the single source of
//! truth for that behaviour: it replaces
//! `app/macos/hyperwhisper/Utilities/ReleaseNotesHTML.swift` and
//! `app/windows/HyperWhisper/Utilities/InlineHtml.cs`, which were the same
//! program written twice (issue #284).
//!
//! Supported: `<b>`/`<strong>`, `<i>`/`<em>`, `<a href>`, `<br>`, and character
//! entities. Everything else is dropped, keeping its text content — so a feed
//! that grows a `<span>` degrades to plain text instead of leaking markup into
//! the UI.
//!
//! Only `http`, `https` and `mailto` links are carried through. A `javascript:`
//! or `data:` href keeps its label and loses the link, so a compromised feed
//! cannot turn a release note into something the user can click into running
//! code. That allowlist lives here, in Rust, on purpose: [`Run::link`] is the
//! href verbatim, and each platform builds its own `URL`/`Uri` from it.
//!
//! # THE STRING UNIT IS THE UNICODE SCALAR VALUE (`char`)
//!
//! This is the crate's single source of truth for that choice, and it is a
//! deliberate behaviour change on both heads. Every index, every scan limit and
//! every whitespace predicate below counts **Unicode scalar values**:
//!
//! * The entity scan limit is **12 scalars, counted from the `&` inclusive**.
//! * `parse_inline` walks a `&[char]`, so a tag body, an attribute value and an
//!   entity body are all scalar slices.
//! * Collapsible whitespace is exactly `{' ', '\n', '\r', '\t'}` — four scalars,
//!   U+00A0 deliberately excluded. The tag scanner's separate predicate is
//!   `char::is_whitespace` (the Unicode White_Space property).
//!
//! Neither head counted scalars before, and they did not agree with each other:
//!
//! | | unit before | consequence |
//! |---|---|---|
//! | macOS | grapheme cluster (`Character`) | `"\r\n"` is ONE Character, equal to neither `"\r"` nor `"\n"`, so it needed its own arm in the collapsible set; `prefix(12)` counted 12 graphemes |
//! | Windows | UTF-16 code unit (`char`) | an astral scalar is two units, so it consumed two of the 12; `start + 12` counted UTF-16 units |
//!
//! No assertion in either oracle suite is sensitive to the change: no test puts
//! a non-ASCII scalar inside an entity scan window, and a CRLF collapses
//! identically whether it is one collapsible grapheme or two collapsible
//! scalars. Only the two heads' doc comments describing the old unit become
//! false.
//!
//! # Panic-free by construction
//!
//! The workspace release profile sets `panic = "abort"` and this input is
//! remote, so a panic here is a crash on a hostile feed. `Cargo.toml` denies
//! `clippy::indexing_slicing`, `clippy::unwrap_used` and `clippy::expect_used`
//! — all three are `clippy::restriction` lints, **allow-by-default and not part
//! of `clippy::all`**, so CI's `cargo clippy --all-targets -- -D warnings` does
//! NOT enable them on its own; the `[lints.clippy]` table is what turns them on.
//! Every slice read goes through `slice::get`, and every arithmetic step that
//! could wrap uses a checked or saturating form. A `cargo-fuzz` target over both
//! entry points lives in `fuzz/` (see its README; CI never runs it).
//!
//! The re-allow below is scoped to `cfg(test)` because test code legitimately
//! unwraps and indexes. Unit tests therefore live *inside* the lib — a `tests/`
//! integration file would be a separate crate that never sees this attribute.

#![cfg_attr(
    test,
    allow(clippy::unwrap_used, clippy::expect_used, clippy::indexing_slicing)
)]

mod entity;
mod inline;
mod tag;

pub use inline::{parse_inline, plain_text, Run};
