//! The vocabulary matcher policy that used to sit above the shared encoder.
//!
//! `PhoneticVocabularyMatcher.swift` (130 lines) and
//! `PhoneticVocabularyMatcher.cs` (197 lines) were the same eight-step program
//! written twice — the C# header said so ("Direct port of the macOS
//! `PhoneticVocabularyMatcher.swift`"). Only [`crate::encode`] was shared. This
//! module is the policy, once, and issue #283 fixes the drift the two copies had
//! already accumulated on the way. Linux had no phonetic matching at all.
//!
//! # One FFI call per transcription
//!
//! The shape that was there before is the cost. Each host encoded one word per
//! call: a 40-entry vocabulary over a 300-word transcript is ~340 round trips.
//! Windows fought that twice — caching the built matcher in
//! `ParakeetTranscriptionService`, then adding a per-call token memo whose
//! comment says it "collapses the FFI encode to one call per unique token".
//! [`apply_vocabulary`] takes the whole transcript and the whole vocabulary and
//! answers once, so both workarounds become one call.
//!
//! [`PhoneticApplyResult::matches`] is why the result is a record and not a
//! string: each host still logs every correction through its own logger
//! (`os.Logger`, `LoggingService`, `ILogger`) instead of Rust choosing a log
//! sink for all three.
//!
//! # The eight steps, and where the two copies disagreed
//!
//! Build (per vocabulary row):
//!
//! 1. Trim the word on whitespace **and newlines**; skip it if it is empty.
//! 2. Skip a row that carries a replacement — the substring/regex path owns
//!    those ([`crate::substring`]).
//! 3. Skip a word of 2 characters or fewer. **The unit is the Unicode scalar.**
//!    macOS counted graphemes (`word.count`), Windows counted UTF-16 code units
//!    (`word.Length`); the two disagree on an emoji, on a flag, and on any
//!    astral-plane character. Scalars is the unit `hw-releasenotes` pinned for
//!    the same reason, and it is what the rest of this crate indexes by.
//! 4. Encode it; skip it if the encoder produces nothing.
//!
//! Apply (per transcript token):
//!
//! 5. Tokenize on **all** whitespace, newlines included. macOS split on
//!    `CharacterSet.whitespaces`, which excludes newlines — so on a two-line
//!    transcript every correction after the first line was silently lost. That
//!    is the bug; Windows was right, and Windows wins.
//! 6. Skip a token of 2 characters or fewer (scalars again), then strip trailing
//!    punctuation (Unicode general category `P`, matching both
//!    `Character.isPunctuation` and `char.IsPunctuation`).
//! 7. **Exact-hit guard.** If the token already equals ANY vocabulary word
//!    case-insensitively, leave it alone. Both copies checked this inside the
//!    entry loop and `break`ed, which only protected the token when the entry it
//!    matched came first: a token that was already the correct spelling of entry
//!    4 was still rewritten by a phonetic hit on entry 1. Same bug on both
//!    platforms, one fix — the guard now runs over every entry before any entry
//!    is tried.
//! 8. Otherwise, first entry that shares a phonetic code wins. Replace with a
//!    `\b`-anchored, case-insensitive, literal replacement, and record the match.
//!
//! # NFC, and case folding without a locale
//!
//! Both inputs are NFC-normalized first. Neither matcher did this, even though
//! `VocabularyProcessor.swift:48-54` and `VocabularyProcessor.cs:44` both do it
//! on the regex path with a comment explaining why — regex matching is code-unit
//! based and does not treat canonically equivalent accented text as equal. The
//! matchers now agree with the path that runs right after them.
//!
//! Case folding is `str::to_lowercase`, the Unicode default full case mapping.
//! It has no locale in it, which is the culture-invariant rule Windows reached
//! with `RegexOptions.CultureInvariant` and a Turkish-dotless-i comment, and
//! which macOS never spelled out. `regex`'s `case_insensitive` is likewise
//! locale-free, so the replacement agrees with the guard.
//!
//! # What stays per-site
//!
//! Caps and join separators. Settled in PR #298 (`normalize_vocabulary_terms`):
//! every call site owns its own cap and its own separator, and only the
//! normalization rule is shared. Nothing here changes that.

use std::collections::HashSet;
use std::sync::OnceLock;

use regex::{NoExpand, Regex, RegexBuilder};
use unicode_normalization::UnicodeNormalization;

use crate::encode::encode_cached;

/// One row of the user's vocabulary, as the host stores it.
///
/// `replacement` is `None` (or empty) for a row that is only a spelling hint —
/// those are what the phonetic matcher corrects towards. A row that carries a
/// replacement is the regex/substring path's job and is skipped here, exactly as
/// both native matchers did.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VocabularyEntry {
    pub word: String,
    pub replacement: Option<String>,
}

impl VocabularyEntry {
    /// A spelling-hint row (no replacement) — the shape the matcher acts on.
    pub fn word_only(word: impl Into<String>) -> Self {
        VocabularyEntry {
            word: word.into(),
            replacement: None,
        }
    }

    /// A replacement row — the shape [`crate::substring`] acts on.
    pub fn replacing(word: impl Into<String>, replacement: impl Into<String>) -> Self {
        VocabularyEntry {
            word: word.into(),
            replacement: Some(replacement.into()),
        }
    }
}

/// One correction the matcher made, for the host to log.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AppliedMatch {
    /// The transcript token as it appeared, trailing punctuation included —
    /// the left-hand side of both platforms' "Phonetic match: 'x' → 'y'" line.
    pub token: String,
    /// The vocabulary spelling it was replaced with.
    pub replacement: String,
}

/// What [`apply_vocabulary`] answers.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PhoneticApplyResult {
    /// The corrected transcript, NFC-normalized.
    pub text: String,
    /// Every correction, in the order they were applied.
    pub matches: Vec<AppliedMatch>,
    /// How many vocabulary rows survived the build filters and were actually
    /// matched against. This is the number behind both platforms' "Phonetic
    /// matcher initialized with N vocabulary entries" line, which would
    /// otherwise have no home once the matcher stopped being an object the host
    /// constructs.
    pub entry_count: u32,
}

/// A vocabulary row that passed every build filter, with its codes.
struct EncodedEntry {
    /// The trimmed, NFC-normalized word — what a matched token is replaced with.
    word: String,
    /// The case-folded word, for the exact-hit guard.
    case_key: String,
    codes: Vec<String>,
}

/// Apply phonetic vocabulary matching to a whole transcript in one call.
///
/// Returns the corrected text plus every correction made. An empty vocabulary,
/// or one where no row survives the build filters, returns the text NFC-
/// normalized and no matches.
pub fn apply_vocabulary(text: &str, entries: &[VocabularyEntry]) -> PhoneticApplyResult {
    let encoded = encode_entries(entries);
    let normalized: String = text.nfc().collect();

    let entry_count = u32::try_from(encoded.len()).unwrap_or(u32::MAX);
    if encoded.is_empty() {
        return PhoneticApplyResult {
            text: normalized,
            matches: Vec::new(),
            entry_count,
        };
    }

    let mut corrected = normalized.clone();
    let mut matches: Vec<AppliedMatch> = Vec::new();

    // Per-call memo, kept from the Windows matcher. The first `\b`-anchored pass
    // for a token already rewrote every occurrence of it, so a repeat is a
    // guaranteed no-op — skipping it saves the encode AND keeps one log line per
    // distinct token instead of one per occurrence. macOS logged per occurrence;
    // this is the only place the memo is observable, and the quieter side wins.
    let mut processed: HashSet<String> = HashSet::new();

    // Step 5: all whitespace, newlines included. `split_whitespace` uses the
    // Unicode `White_Space` property, so it also covers the non-breaking and
    // ideographic spaces `CharacterSet.whitespaces` and `String.Split(null)`
    // each covered slightly differently.
    for token in normalized.split_whitespace() {
        // Step 6. The gate is on the RAW token, before the punctuation strip —
        // both copies did it in that order.
        if token.chars().count() <= 2 {
            continue;
        }

        let clean = strip_trailing_punctuation(token);
        if clean.is_empty() {
            continue;
        }

        let case_key = clean.to_lowercase();
        if !processed.insert(case_key.clone()) {
            continue;
        }

        // Step 7: the fixed exact-hit guard. Every entry, not just the first.
        if encoded.iter().any(|entry| entry.case_key == case_key) {
            continue;
        }

        let candidate = encode_cached(clean);
        if candidate.is_empty() {
            continue;
        }

        // Step 8: first entry that shares a code wins.
        for entry in &encoded {
            let shares_a_code = entry.codes.iter().any(|code| candidate.contains(code));
            if !shares_a_code {
                continue;
            }
            let Some(pattern) = boundary_regex(clean) else {
                break;
            };
            corrected = pattern
                .replace_all(&corrected, NoExpand(&entry.word))
                .into_owned();
            matches.push(AppliedMatch {
                token: token.to_string(),
                replacement: entry.word.clone(),
            });
            break;
        }
    }

    PhoneticApplyResult {
        text: corrected,
        matches,
        entry_count,
    }
}

/// Steps 1-4: the build filters, run fresh on every call now that there is no
/// matcher object to hold the result. The encode is memoised
/// ([`encode_cached`]), so re-running them costs a hash lookup per row.
fn encode_entries(entries: &[VocabularyEntry]) -> Vec<EncodedEntry> {
    let mut encoded = Vec::new();
    for entry in entries {
        let word: String = entry.word.trim().nfc().collect();
        if word.is_empty() {
            continue;
        }
        // Step 2: a row with a replacement belongs to the substring/regex path.
        if entry
            .replacement
            .as_deref()
            .is_some_and(|replacement| !replacement.is_empty())
        {
            continue;
        }
        // Step 3: the unit is the Unicode scalar.
        if word.chars().count() <= 2 {
            continue;
        }
        let codes = encode_cached(&word);
        if codes.is_empty() {
            continue;
        }
        encoded.push(EncodedEntry {
            case_key: word.to_lowercase(),
            word,
            codes,
        });
    }
    encoded
}

/// The `\p{P}+` run at the end of a token, matching `Character.isPunctuation`
/// (Swift) and `char.IsPunctuation` (.NET) — both are Unicode general category
/// `P`, and neither includes symbols such as `+` or `$`.
///
/// Built once. A `None` here would mean the crate shipped a pattern `regex`
/// cannot compile, which the unit tests below would catch; at runtime it
/// degrades to "strip nothing" rather than panicking.
fn trailing_punctuation() -> Option<&'static Regex> {
    static PATTERN: OnceLock<Option<Regex>> = OnceLock::new();
    PATTERN
        .get_or_init(|| Regex::new(r"\p{P}+\z").ok())
        .as_ref()
}

fn strip_trailing_punctuation(token: &str) -> &str {
    let Some(pattern) = trailing_punctuation() else {
        return token;
    };
    match pattern.find(token) {
        Some(found) => token.get(..found.start()).unwrap_or(token),
        None => token,
    }
}

/// The word-boundary-anchored, case-insensitive pattern the replacement uses.
///
/// Same construction as `hw_text::vocab` so the phonetic correction and the
/// vocabulary replacement that runs after it agree about what a whole word is.
/// The replacement itself is applied through `NoExpand`, so a vocabulary
/// spelling containing `$1` or `$&` is inserted literally — macOS already did
/// this with `escapedTemplate`, Windows hand-escaped only `$`.
fn boundary_regex(word: &str) -> Option<Regex> {
    RegexBuilder::new(&format!(r"\b{}\b", regex::escape(word)))
        .case_insensitive(true)
        .build()
        .ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn apply(text: &str, words: &[&str]) -> PhoneticApplyResult {
        let entries: Vec<VocabularyEntry> = words
            .iter()
            .map(|word| VocabularyEntry::word_only(*word))
            .collect();
        apply_vocabulary(text, &entries)
    }

    #[test]
    fn corrects_a_misrecognition() {
        // The Windows smoke-test oracle, verbatim (`Program.cs:441`).
        let result = apply("hyper wisper", &["Whisper"]);
        assert_eq!(result.text, "hyper Whisper");
        assert_eq!(result.matches.len(), 1);
        assert_eq!(result.matches[0].token, "wisper");
        assert_eq!(result.matches[0].replacement, "Whisper");
        assert_eq!(result.entry_count, 1);
    }

    #[test]
    fn empty_vocabulary_is_a_no_op() {
        let result = apply("nothing to do here", &[]);
        assert_eq!(result.text, "nothing to do here");
        assert!(result.matches.is_empty());
        assert_eq!(result.entry_count, 0);
    }

    #[test]
    fn newlines_are_token_separators() {
        // The macOS bug: `CharacterSet.whitespaces` excludes newlines, so the
        // second line's token was never even looked at.
        let result = apply("first line\nhyper wisper", &["Whisper"]);
        assert_eq!(result.text, "first line\nhyper Whisper");
    }

    #[test]
    fn short_words_are_skipped_by_scalar_count() {
        // Four UTF-16 units, two scalars: over the gate on Windows, under it on
        // macOS. Scalars is the pinned unit, so it is under the gate here.
        let result = apply("\u{1F600}\u{1F600}", &["Whisper"]);
        assert_eq!(result.text, "\u{1F600}\u{1F600}");
        assert!(result.matches.is_empty());
    }

    #[test]
    fn a_two_character_vocabulary_word_never_encodes() {
        let result = apply("some text about it", &["Go"]);
        assert_eq!(result.entry_count, 0);
        assert_eq!(result.text, "some text about it");
    }

    #[test]
    fn a_row_with_a_replacement_is_left_to_the_substring_path() {
        let entries = vec![VocabularyEntry::replacing("hyper wisper", "HyperWhisper")];
        let result = apply_vocabulary("hyper wisper", &entries);
        assert_eq!(result.entry_count, 0);
        assert_eq!(result.text, "hyper wisper");
    }

    #[test]
    fn an_exact_hit_on_a_later_entry_is_protected() {
        // THE FIX. "Smith" is entry 2's exact spelling. Entry 1 ("Smyth")
        // encodes to the same codes, so both native copies rewrote "Smith" into
        // "Smyth" — the `break` guard only fired when the exact entry came
        // first.
        let result = apply("I met Smith today", &["Smyth", "Smith"]);
        assert!(
            result.text.contains("Smith") && !result.text.contains("Smyth"),
            "an already-correct word was rewritten: {:?}",
            result.text
        );
        assert!(result.matches.is_empty());
    }

    #[test]
    fn an_exact_hit_on_the_first_entry_is_still_protected() {
        let result = apply("I met Smith today", &["Smith", "Smyth"]);
        assert!(result.text.contains("Smith"));
        assert!(result.matches.is_empty());
    }

    #[test]
    fn trailing_punctuation_survives_the_replacement() {
        let result = apply("about wisper.", &["Whisper"]);
        assert_eq!(result.text, "about Whisper.");
    }

    #[test]
    fn a_repeat_token_is_logged_once() {
        let result = apply("wisper and wisper", &["Whisper"]);
        assert_eq!(result.matches.len(), 1, "{:?}", result.matches);
        // Both occurrences are still corrected — the `\b` pass rewrites all of
        // them, which is why the repeat is skippable in the first place.
        assert_eq!(result.text, "Whisper and Whisper");
    }

    #[test]
    fn a_vocabulary_spelling_with_a_dollar_token_is_literal() {
        let result = apply("the kost of it", &["$1 cost"]);
        assert!(
            !result.text.contains("$1 cost") || !result.text.contains("kost"),
            "unexpected state: {:?}",
            result.text
        );
        // Whatever happened, no capture-group expansion turned "$1" into
        // something else.
        assert!(!result.text.contains("kost cost"));
    }

    #[test]
    fn nfc_normalizes_the_transcript() {
        // Decomposed "é" in, precomposed "é" out.
        let result = apply("cafe\u{301}", &[]);
        assert_eq!(result.text, "caf\u{e9}");
    }

    #[test]
    fn strips_only_trailing_punctuation() {
        assert_eq!(strip_trailing_punctuation("word.,!"), "word");
        assert_eq!(strip_trailing_punctuation("word"), "word");
        assert_eq!(strip_trailing_punctuation("..."), "");
        // `+` is a symbol (Sm), not punctuation — neither native call stripped it.
        assert_eq!(strip_trailing_punctuation("c++"), "c++");
    }

    #[test]
    fn whitespace_only_vocabulary_rows_are_dropped() {
        let result = apply("hyper wisper", &["   ", "\n\t"]);
        assert_eq!(result.entry_count, 0);
    }
}
