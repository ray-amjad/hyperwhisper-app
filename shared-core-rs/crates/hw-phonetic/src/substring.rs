//! The local providers' vocabulary pass: unanchored, case-insensitive AND
//! diacritic-insensitive substring replacement.
//!
//! This is a port of the unified macOS implementation —
//! `VocabularyProcessor.applySubstringReplacement` /
//! `applySubstringVocabulary`, commit 136071d, which itself replaced four
//! byte-identical private `applyVocabulary` copies in `ParakeetProvider`,
//! `NemotronProvider`, `AppleSpeechAnalyzerProvider` and `Qwen3AsrProvider`.
//! Windows and Linux had no counterpart at all; issue #283 gives them one.
//!
//! # This is NOT `hw_text::apply_hardened_replacement`
//!
//! The two rule sets sit next to each other on purpose, exactly as macOS put
//! them next to each other after finding the four copies. They differ in two
//! ways that matter:
//!
//! | | hardened | substring (here) |
//! |---|---|---|
//! | anchoring | `\b…\b`, whole word | none, plain substring |
//! | diacritics | sensitive | **insensitive** |
//!
//! The hardened pass runs later, over the pipeline's own vocabulary list. This
//! one runs first, inside the on-device provider, over its own raw output.
//!
//! # Why this needed a byte-offset map
//!
//! `String.replacingOccurrences(of:with:options: [.caseInsensitive, .diacriticInsensitive])`
//! does not hand back a folded string. Foundation compares under the folding and
//! then splices the replacement into **the original text**, at the original
//! range: everything the match did not cover keeps its own case and its own
//! accents. A port that folds, replaces and returns the folded copy would
//! silently lowercase the whole transcript and strip every accent off it.
//!
//! So the search runs over a folded copy and each match's folded byte range is
//! mapped back to a byte range in the original — see [`crate::fold`] for that
//! map and for the two boundary rules that fall out of it. That is the piece
//! issue #283 calls the genuinely hard one, and it is why this pass lives here
//! rather than in `hw-text`: it needs real NFD, and `hw-text/Cargo.toml` carries
//! an explicit "no deps beyond regex" constraint.
//!
//! # The rules, unchanged from macOS
//!
//! * Both `word` and `replacement` are trimmed on whitespace and newlines, and
//!   an empty trimmed word or replacement is a no-op.
//! * A row with no word or no replacement is skipped — the `guard let … else
//!   { continue }` the four provider copies used.
//! * Entries apply in list order, each over the result of the last.
//! * Matches are non-overlapping, left to right, and the replacement is never
//!   rescanned — `replacingOccurrences` works the same way.
//! * The text is NOT normalized. Foundation returns the original bytes for
//!   everything outside a match, and so does this: the fold is a search index,
//!   not an output form. (The matcher DOES normalize, because its output is
//!   compared and replaced with `regex`, which is code-unit based. Different
//!   pass, different reason.)

use crate::fold::{fold, fold_text};
use crate::matcher::VocabularyEntry;

/// Apply every entry to `text` with the local-provider substring rules, in list
/// order.
pub fn apply_substring_vocabulary(text: &str, entries: &[VocabularyEntry]) -> String {
    let mut result = text.to_string();
    for entry in entries {
        let Some(replacement) = entry.replacement.as_deref() else {
            continue;
        };
        result = apply_substring_replacement(&result, &entry.word, replacement);
    }
    result
}

/// One diacritic-insensitive, case-insensitive, unanchored substring
/// replacement.
fn apply_substring_replacement(text: &str, word: &str, replacement: &str) -> String {
    let trimmed_word = word.trim();
    if trimmed_word.is_empty() {
        return text.to_string();
    }
    let trimmed_replacement = replacement.trim();
    if trimmed_replacement.is_empty() {
        return text.to_string();
    }

    let needle = fold_text(trimmed_word);
    // A word that is nothing but combining marks folds away to nothing. An empty
    // needle matches at every position, which would splice the replacement
    // between every pair of characters in the transcript. Foundation returns the
    // original for an empty search string; so do we.
    if needle.is_empty() {
        return text.to_string();
    }

    let haystack = fold(text);
    let mut out = String::with_capacity(text.len());
    // Byte offset in the ORIGINAL that has already been copied into `out`.
    let mut copied = 0usize;
    // Byte offset in the FOLDED text to resume searching from.
    let mut cursor = 0usize;
    let mut matched = false;

    while let Some(found) = haystack
        .text
        .get(cursor..)
        .and_then(|rest| rest.find(&needle))
    {
        let start = cursor + found;
        let end = start + needle.len();

        match haystack.original_range(start, end) {
            // The match lines up with character boundaries in the original:
            // copy everything before it verbatim, then splice the replacement.
            Some((original_start, original_end)) if original_start >= copied => {
                if let Some(prefix) = text.get(copied..original_start) {
                    out.push_str(prefix);
                }
                out.push_str(trimmed_replacement);
                copied = original_end;
                matched = true;
                cursor = end;
            }
            // Either the match starts or ends part-way through one character's
            // fold expansion, or it overlaps a range already replaced. Skip it
            // and resume one character later, which is what keeps this loop
            // finite for every input.
            _ => {
                let step = haystack
                    .text
                    .get(start..)
                    .and_then(|rest| rest.chars().next())
                    .map_or(1, char::len_utf8);
                cursor = start + step;
            }
        }
    }

    if !matched {
        return text.to_string();
    }
    if let Some(tail) = text.get(copied..) {
        out.push_str(tail);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn replace(text: &str, word: &str, replacement: &str) -> String {
        apply_substring_replacement(text, word, replacement)
    }

    #[test]
    fn replaces_a_plain_substring() {
        assert_eq!(
            replace("say hyperwisper now", "hyperwisper", "HyperWhisper"),
            "say HyperWhisper now"
        );
    }

    #[test]
    fn is_unanchored_unlike_the_hardened_pass() {
        // No `\b`: a substring inside a longer word is replaced. That is the
        // documented difference from `hw_text::apply_hardened_replacement`.
        assert_eq!(replace("category", "cat", "dog"), "dogegory");
    }

    #[test]
    fn is_case_insensitive() {
        assert_eq!(
            replace("HYPERWISPER", "hyperwisper", "HyperWhisper"),
            "HyperWhisper"
        );
    }

    #[test]
    fn is_diacritic_insensitive_and_keeps_the_rest_of_the_text_intact() {
        // The whole reason for the offset map: "Café" matches "cafe", and the
        // accented "Zoë" that was NOT matched keeps its diaeresis and its case.
        assert_eq!(
            replace(
                "Zo\u{eb} went to the Caf\u{e9} today",
                "cafe",
                "Coffee House"
            ),
            "Zo\u{eb} went to the Coffee House today"
        );
    }

    #[test]
    fn a_decomposed_accent_is_consumed_by_the_match() {
        // "cafe" + combining acute. The accent folds to nothing, so it has no
        // offset of its own and falls inside the replaced range rather than
        // being left behind as a stray mark on the replacement.
        assert_eq!(
            replace("a cafe\u{301} here", "cafe", "diner"),
            "a diner here"
        );
    }

    #[test]
    fn an_accented_search_word_matches_unaccented_text() {
        assert_eq!(replace("the cafe", "Caf\u{e9}", "Diner"), "the Diner");
    }

    #[test]
    fn replaces_every_occurrence() {
        assert_eq!(replace("cat cat cat", "cat", "dog"), "dog dog dog");
    }

    #[test]
    fn the_replacement_is_not_rescanned() {
        // "aa" -> "aaa" would loop forever if the output were rescanned.
        assert_eq!(replace("aa", "aa", "aaa"), "aaa");
        assert_eq!(replace("aaaa", "aa", "aaa"), "aaaaaa");
    }

    #[test]
    fn an_empty_or_blank_word_is_a_no_op() {
        assert_eq!(replace("unchanged", "", "x"), "unchanged");
        assert_eq!(replace("unchanged", "   ", "x"), "unchanged");
    }

    #[test]
    fn an_empty_or_blank_replacement_is_a_no_op() {
        assert_eq!(replace("unchanged", "unchanged", ""), "unchanged");
        assert_eq!(replace("unchanged", "unchanged", "  \n "), "unchanged");
    }

    #[test]
    fn both_sides_are_trimmed() {
        assert_eq!(replace("say cat now", "  cat  ", "  dog  "), "say dog now");
    }

    #[test]
    fn a_word_that_folds_to_nothing_is_a_no_op() {
        // A lone combining acute: non-empty after trimming, empty after folding.
        // An empty needle would otherwise match between every pair of characters.
        assert_eq!(replace("unchanged", "\u{301}", "X"), "unchanged");
    }

    #[test]
    fn no_match_returns_the_original_bytes_untouched() {
        // Nothing is normalized or case-folded on the way through.
        let original = "Zo\u{eb} and cafe\u{301} and \u{130}";
        assert_eq!(replace(original, "nothing", "X"), original);
    }

    #[test]
    fn a_match_at_the_very_end_uses_the_sentinel() {
        assert_eq!(replace("the caf\u{e9}", "cafe", "diner"), "the diner");
    }

    #[test]
    fn applies_entries_in_list_order() {
        let entries = vec![
            VocabularyEntry::replacing("cat", "dog"),
            VocabularyEntry::replacing("dog", "fox"),
        ];
        // Entry 1 makes "dog", entry 2 then rewrites it — list order, each over
        // the result of the last, exactly as the macOS `reduce` did.
        assert_eq!(apply_substring_vocabulary("cat", &entries), "fox");
    }

    #[test]
    fn a_row_without_a_replacement_is_skipped() {
        let entries = vec![VocabularyEntry::word_only("cat")];
        assert_eq!(apply_substring_vocabulary("cat", &entries), "cat");
    }

    #[test]
    fn an_empty_vocabulary_is_a_no_op() {
        assert_eq!(apply_substring_vocabulary("cat", &[]), "cat");
    }

    #[test]
    fn a_match_ending_mid_expansion_is_refused_rather_than_truncated() {
        // A precomposed Hangul syllable folds into two jamo from ONE character.
        // A needle of just the leading jamo would end part-way through that
        // expansion, so there is no original range to splice and the text is
        // left alone.
        assert_eq!(replace("\u{AC00}", "\u{1100}", "X"), "\u{AC00}");
        // The whole syllable still matches, precomposed or decomposed.
        assert_eq!(replace("\u{AC00}", "\u{AC00}", "X"), "X");
        assert_eq!(replace("\u{AC00}", "\u{1100}\u{1161}", "X"), "X");
    }

    #[test]
    fn overlapping_candidates_are_replaced_left_to_right() {
        // "aaa" contains "aa" at offsets 0 and 1; only the first is taken and
        // the search resumes after it, which is Foundation's behaviour.
        assert_eq!(replace("aaa", "aa", "X"), "Xa");
    }
}
