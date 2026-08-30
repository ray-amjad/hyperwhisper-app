//! Case- and diacritic-folding with a byte-offset map back to the original.
//!
//! # Why this is not "NFD + strip Mn"
//!
//! The macOS local providers replace vocabulary with
//! `replacingOccurrences(of:with:options: [.caseInsensitive, .diacriticInsensitive])`.
//! Foundation does NOT hand back a folded string: it compares under the folding
//! and then splices the replacement into **the original text**, at the original
//! range. Everything the match did not cover keeps its own case and its own
//! accents.
//!
//! So a port cannot fold the transcript, replace, and return the folded copy —
//! that would silently lowercase and strip the accents off the whole transcript.
//! It needs to search a folded copy and then map the match's folded byte range
//! back to a byte range in the original. That map is what this module builds.
//!
//! # The map
//!
//! [`fold`] walks the original one `char` at a time. Each character folds to
//! zero or more bytes, and every byte it emits records:
//!
//! * the byte offset in the ORIGINAL string of the character that produced it,
//! * whether it is the FIRST byte that character produced.
//!
//! A trailing sentinel entry carries the original length, so a match that runs
//! to the end of the text has an end offset to read.
//!
//! Two consequences fall out of that shape, and both are the behaviour we want:
//!
//! * A combining mark folds to **zero** bytes, so its own original offset never
//!   appears in the map. A match that ends just before one therefore ends at the
//!   *next* character's offset — the mark is swallowed into the replaced range.
//!   That is Foundation's composed-character-sequence behaviour: replacing "e"
//!   in "é" (decomposed) consumes the accent rather than orphaning it.
//! * A folded byte range that starts or ends part-way through one character's
//!   fold expansion is **rejected** ([`Folded::original_range`] returns `None`),
//!   rather than truncated. Foundation matches on composed-character-sequence
//!   boundaries; refusing the match is the closer answer, and it is the safe one
//!   — the alternative is splicing the original at an offset the match never
//!   really covered.
//!
//! # The fold itself
//!
//! Canonical decomposition (NFD), then drop every character with a non-zero
//! canonical combining class, then Unicode default lowercase. `char::to_lowercase`
//! is the Unicode default full case mapping: it has no locale in it at all, which
//! is the culture-invariant rule issue #283 unifies on (the Windows matcher
//! reached the same place with `RegexOptions.CultureInvariant` and a
//! Turkish-dotless-i comment; the macOS matcher had no such option).
//!
//! Non-zero combining class is the definition of "a diacritic" used here. After
//! NFD every accent the fold is meant to ignore carries one, and no base letter
//! does.

use unicode_normalization::char::{canonical_combining_class, decompose_canonical};

/// One folded byte's provenance.
#[derive(Clone, Copy)]
struct Mark {
    /// Byte offset, in the original string, of the character that emitted this
    /// folded byte. The sentinel entry carries the original string's length.
    original: usize,
    /// Whether this is the first folded byte its source character emitted.
    /// Always true for the sentinel.
    boundary: bool,
}

/// A folded copy of a string, plus the map back to it.
pub(crate) struct Folded {
    /// The case- and diacritic-folded text. Search happens in here.
    pub(crate) text: String,
    /// `text.len() + 1` entries: one per folded byte, plus the end sentinel.
    marks: Vec<Mark>,
}

/// Fold `input` and record where every folded byte came from.
pub(crate) fn fold(input: &str) -> Folded {
    let mut text = String::with_capacity(input.len());
    let mut marks: Vec<Mark> = Vec::with_capacity(input.len() + 1);

    for (offset, source) in input.char_indices() {
        let before = text.len();
        decompose_canonical(source, |decomposed| {
            if canonical_combining_class(decomposed) != 0 {
                return;
            }
            for lowered in decomposed.to_lowercase() {
                text.push(lowered);
            }
        });
        // One entry per byte this character contributed; the first is the
        // character boundary a match is allowed to start or end on.
        for index in before..text.len() {
            marks.push(Mark {
                original: offset,
                boundary: index == before,
            });
        }
    }

    marks.push(Mark {
        original: input.len(),
        boundary: true,
    });

    Folded { text, marks }
}

impl Folded {
    /// Map a byte range in [`Folded::text`] back to a byte range in the original.
    ///
    /// `None` when either end sits part-way through a character's fold expansion
    /// — see the module docs for why that is a refusal rather than a truncation.
    pub(crate) fn original_range(&self, start: usize, end: usize) -> Option<(usize, usize)> {
        let start_mark = self.marks.get(start)?;
        let end_mark = self.marks.get(end)?;
        if !start_mark.boundary || !end_mark.boundary {
            return None;
        }
        if end_mark.original < start_mark.original {
            return None;
        }
        Some((start_mark.original, end_mark.original))
    }
}

/// Fold a string with no offset map, for comparisons that only need equality.
pub(crate) fn fold_text(input: &str) -> String {
    fold(input).text
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folds_case_and_diacritics() {
        assert_eq!(fold_text("CAFÉ"), "cafe");
        assert_eq!(fold_text("Café"), "cafe");
        // Decomposed input folds to the same thing as precomposed input.
        assert_eq!(fold_text("cafe\u{301}"), "cafe");
    }

    #[test]
    fn turkish_capital_i_folds_without_a_locale() {
        // U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE. NFD splits it into "I"
        // plus a combining dot (class 230), the dot is dropped as a diacritic,
        // and the "I" lowercases to a plain ASCII "i" — the culture-invariant
        // answer, under any host locale.
        assert_eq!(fold_text("\u{130}"), "i");
        // And the dotless capital I folds the same way, which is the whole point
        // of the invariant rule.
        assert_eq!(fold_text("I"), "i");
    }

    #[test]
    fn offsets_map_back_to_the_original() {
        let original = "Café au lait";
        let folded = fold(original);
        assert_eq!(folded.text, "cafe au lait");
        // "cafe" -> the original "Café", accent included.
        let (start, end) = folded.original_range(0, 4).expect("aligned range");
        assert_eq!(original.get(start..end), Some("Café"));
    }

    #[test]
    fn a_decomposed_accent_is_swallowed_by_the_range_before_it() {
        let original = "cafe\u{301} au lait";
        let folded = fold(original);
        assert_eq!(folded.text, "cafe au lait");
        let (start, end) = folded.original_range(0, 4).expect("aligned range");
        // The combining acute folds to nothing, so it has no offset of its own
        // and falls inside the replaced range rather than being orphaned.
        assert_eq!(original.get(start..end), Some("cafe\u{301}"));
    }

    #[test]
    fn a_range_ending_mid_expansion_is_refused() {
        // A precomposed Hangul syllable decomposes into two jamo, both with
        // combining class 0, so ONE source character emits six folded bytes. A
        // range that stops after the first jamo is not a character boundary.
        let folded = fold("\u{AC00}");
        assert_eq!(folded.text, "\u{1100}\u{1161}");
        assert!(folded.original_range(0, 3).is_none());
        assert!(folded.original_range(0, folded.text.len()).is_some());
    }

    #[test]
    fn the_sentinel_covers_a_match_at_the_end() {
        let original = "ok";
        let folded = fold(original);
        let (start, end) = folded.original_range(0, 2).expect("aligned range");
        assert_eq!((start, end), (0, original.len()));
    }

    #[test]
    fn empty_input_still_has_a_sentinel() {
        let folded = fold("");
        assert!(folded.text.is_empty());
        assert_eq!(folded.original_range(0, 0), Some((0, 0)));
    }
}
