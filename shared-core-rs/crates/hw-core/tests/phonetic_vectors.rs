//! Conformance-vector tests for the shared phonetic vocabulary matcher.
//!
//! `shared-conformance/phonetic-vectors.json` is the cross-platform source of
//! truth for the policy issue #283 moved into `hw-phonetic`. It is a **decision
//! table**, not just a golden file: every row carries a `decision` field naming
//! which of the two native matchers the unified answer came from —
//!
//! * `agreed` — `PhoneticVocabularyMatcher.swift` and
//!   `PhoneticVocabularyMatcher.cs` already did this, and so do we.
//! * `windows` — the two copies disagreed and the Windows behaviour is the
//!   documented-correct one.
//! * `macos` — the two copies disagreed and the macOS behaviour is the
//!   documented-correct one.
//! * `neither` — both copies were wrong in the same way, and this row is the
//!   fix.
//! * `new` — neither copy had a rule here; the unified core picks one and pins
//!   it.
//!
//! Read the `decision` column before the inputs. Every row that is not `agreed`
//! is a behaviour change that belongs in the pull-request notes, and the
//! coverage test below fails if any of those four buckets loses its last row.
//!
//! Regenerate after an intended policy change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-core --test phonetic_vectors -- --ignored regenerate
//! ```
//!
//! Then read the diff. An expectation that moves without a matching `hw-phonetic`
//! edit is a regression, not a refresh.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use hyperwhisper_core::ffi_phonetic::{phonetic_apply_vocabulary, HwVocabularyEntry};

const VECTORS_PATH: &str = "../../../shared-conformance/phonetic-vectors.json";

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
    matcher_cases: Vec<MatcherVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct MatcherVector {
    /// What this row proves.
    name: String,
    /// Which native copy the unified answer came from. See the module docs.
    decision: String,
    /// One sentence on what each copy used to do. Empty for `agreed` rows.
    was: String,
    text: String,
    entries: Vec<EntryVector>,
    expected: ExpectedVector,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct EntryVector {
    word: String,
    #[serde(default)]
    replacement: Option<String>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct ExpectedVector {
    text: String,
    matches: Vec<MatchVector>,
    entry_count: u32,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct MatchVector {
    token: String,
    replacement: String,
}

// ---------------------------------------------------------------------------
// Decision labels. One constant per bucket so a typo cannot silently create a
// new, uncounted one.
// ---------------------------------------------------------------------------

const AGREED: &str = "agreed";
const WINDOWS: &str = "windows";
const MACOS: &str = "macos";
const NEITHER: &str = "neither";
const NEW: &str = "new";

const DECISIONS: [&str; 5] = [AGREED, WINDOWS, MACOS, NEITHER, NEW];

/// The buckets that MUST keep at least one row: each is a behaviour change the
/// pull-request notes list, and a vector set that loses its last one stops
/// proving the change happened.
const CHANGED: [&str; 4] = [WINDOWS, MACOS, NEITHER, NEW];

fn word_only(word: &str) -> EntryVector {
    EntryVector {
        word: word.to_string(),
        replacement: None,
    }
}

fn replacing(word: &str, replacement: &str) -> EntryVector {
    EntryVector {
        word: word.to_string(),
        replacement: Some(replacement.to_string()),
    }
}

// ---------------------------------------------------------------------------
// The inputs. Expectations are NOT written here — `build_document` fills them
// from the shared core, and the committed file is what review reads.
// ---------------------------------------------------------------------------

#[allow(clippy::vec_init_then_push)]
fn cases() -> Vec<(
    &'static str,
    &'static str,
    &'static str,
    &'static str,
    Vec<EntryVector>,
)> {
    vec![
        // --- Rows both copies already agreed on --------------------------
        (
            "corrects a misrecognized token towards the vocabulary spelling",
            AGREED,
            "",
            "hyper wisper",
            vec![word_only("Whisper")],
        ),
        (
            "an empty vocabulary leaves the transcript alone",
            AGREED,
            "",
            "nothing to correct here",
            vec![],
        ),
        (
            "trailing punctuation survives the replacement",
            AGREED,
            "",
            "about wisper.",
            vec![word_only("Whisper")],
        ),
        (
            "a vocabulary word of two characters never encodes",
            AGREED,
            "",
            "some text about it",
            vec![word_only("Go")],
        ),
        (
            "a row that carries a replacement is left to the substring path",
            AGREED,
            "",
            "hyper wisper",
            vec![replacing("wisper", "Whisper")],
        ),
        (
            "a whitespace-only vocabulary row is dropped",
            AGREED,
            "",
            "hyper wisper",
            vec![word_only("   "), word_only("\n\t")],
        ),
        (
            "a token of two characters or fewer is never matched",
            AGREED,
            "",
            // "an" encodes to the same codes as "Ann", so the only thing
            // stopping the correction is the gate on the token.
            "an apple a day",
            vec![word_only("Ann")],
        ),
        (
            "an exact hit on the FIRST entry is left alone",
            AGREED,
            "",
            "I met Smith today",
            vec![word_only("Smith"), word_only("Smyth")],
        ),
        // --- Rows where the two copies disagreed -------------------------
        (
            "a newline separates tokens, so a correction on line 2 still lands",
            WINDOWS,
            "macOS split on CharacterSet.whitespaces, which EXCLUDES newlines, so \
             every correction after the first line of a multi-line transcript was \
             silently lost. Windows split on all whitespace.",
            "first line\nhyper wisper",
            vec![word_only("Whisper")],
        ),
        (
            "a repeated token is corrected everywhere but reported once",
            WINDOWS,
            "Windows carried a per-call memo of processed tokens; macOS re-encoded \
             and re-logged every occurrence. The text is identical either way — the \
             \\b-anchored pass already rewrote every occurrence on the first hit — so \
             the quieter side wins.",
            "wisper and wisper",
            vec![word_only("Whisper")],
        ),
        (
            "a $-token in the vocabulary spelling is inserted literally",
            MACOS,
            "macOS used NSRegularExpression.escapedTemplate, so every template token \
             was literal. Windows hand-escaped only `$`, leaving $&, $`, $' and \
             ${name} expandable.",
            "the kost of it",
            vec![word_only("$& cost")],
        ),
        // --- Rows where both copies were wrong the same way --------------
        (
            "an exact hit on a LATER entry is protected too",
            NEITHER,
            "Both copies checked the exact hit inside the entry loop and broke out of \
             it, so the guard only fired when the exact entry came first: a token that \
             was already the correct spelling of entry 2 was still rewritten by a \
             phonetic hit on entry 1.",
            "I met Smith today",
            vec![word_only("Smyth"), word_only("Smith")],
        ),
        (
            "an exact hit is protected even when an earlier entry also matches it",
            NEITHER,
            "The same bug, with three entries — the protection has to survive every \
             entry ahead of the exact one, not just the first.",
            "I met Smith today",
            vec![word_only("Smyth"), word_only("Smoth"), word_only("Smith")],
        ),
        // --- Rows neither copy had a rule for ----------------------------
        (
            "the <=2-character gate counts Unicode scalars, not graphemes or UTF-16 units",
            NEW,
            "macOS counted graphemes (word.count) and Windows counted UTF-16 code \
             units (word.Length). Two astral characters are 2 scalars, 2 graphemes and \
             4 UTF-16 units, so Windows let this token through and macOS did not. \
             Scalars is the unit hw-releasenotes pinned, and it is under the gate.",
            "\u{1F600}\u{1F600}",
            vec![word_only("Whisper")],
        ),
        (
            "a decomposed transcript token equals a precomposed vocabulary word",
            NEW,
            "Neither matcher normalized, though VocabularyProcessor.swift:48-54 and \
             VocabularyProcessor.cs:44 both do on the regex path that runs right after, \
             with a comment explaining why. Un-normalized, the decomposed token here \
             does not compare equal to the precomposed vocabulary word, the exact-hit \
             guard misses, the phonetic codes match anyway and the user's own lowercase \
             spelling is rewritten to the vocabulary's capital. NFC first, and it is \
             left alone.",
            "at the cafe\u{301} today",
            vec![word_only("Caf\u{e9}")],
        ),
        (
            "case folding has no locale in it",
            NEW,
            "Windows added RegexOptions.CultureInvariant with a Turkish-dotless-i \
             comment; macOS used caseInsensitiveCompare with no such note. \
             str::to_lowercase is the Unicode default mapping, which is invariant \
             under every host locale.",
            "I met \u{130}STANBUL today",
            vec![word_only("Istanbul")],
        ),
    ]
}

fn build_document() -> Document {
    Document {
        description: "Golden phonetic vocabulary-matcher vectors (issue #283). \
            Generated from hw-phonetic by `cargo test -p hw-core --test phonetic_vectors \
            -- --ignored regenerate`. The `decision` field names which native matcher \
            each unified answer came from: agreed / windows / macos / neither / new."
            .to_string(),
        matcher_cases: cases()
            .into_iter()
            .map(|(name, decision, was, text, entries)| MatcherVector {
                name: name.to_string(),
                decision: decision.to_string(),
                was: was.to_string(),
                expected: run(text, &entries),
                text: text.to_string(),
                entries,
            })
            .collect(),
    }
}

fn run(text: &str, entries: &[EntryVector]) -> ExpectedVector {
    let ffi: Vec<HwVocabularyEntry> = entries
        .iter()
        .map(|entry| HwVocabularyEntry {
            word: entry.word.clone(),
            replacement: entry.replacement.clone(),
        })
        .collect();
    let result = phonetic_apply_vocabulary(text.to_string(), ffi);
    ExpectedVector {
        text: result.text,
        matches: result
            .matches
            .into_iter()
            .map(|applied| MatchVector {
                token: applied.token,
                replacement: applied.replacement,
            })
            .collect(),
        entry_count: result.entry_count,
    }
}

fn load_document() -> Document {
    let raw = std::fs::read_to_string(vectors_path())
        .expect("shared-conformance/phonetic-vectors.json must exist");
    serde_json::from_str(&raw).expect("phonetic-vectors.json must parse")
}

/// The committed vectors are exactly what the shared core answers today. This
/// is the whole point of the file: it fails on a behaviour change that was not
/// deliberately regenerated and reviewed.
#[test]
fn vectors_match_the_shared_core() {
    let expected = load_document();
    let actual = build_document();

    assert_eq!(
        expected.matcher_cases.len(),
        actual.matcher_cases.len(),
        "the committed vectors and the generator disagree on the case list — regenerate"
    );
    for (want, got) in expected
        .matcher_cases
        .iter()
        .zip(actual.matcher_cases.iter())
    {
        assert_eq!(want.name, got.name, "case order changed — regenerate");
        assert_eq!(
            want.text, got.text,
            "input text changed for {:?}",
            want.name
        );
        assert_eq!(
            want.entries, got.entries,
            "input vocabulary changed for {:?}",
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
        .matcher_cases
        .iter()
        .map(|case| case.decision.as_str())
        .filter(|decision| !DECISIONS.contains(decision))
        .collect();
    assert!(unknown.is_empty(), "unknown decision labels: {unknown:?}");

    for decision in CHANGED {
        let count = doc
            .matcher_cases
            .iter()
            .filter(|case| case.decision == decision)
            .count();
        assert!(
            count >= 1,
            "decision bucket {decision} lost its last vector — that is a documented \
             behaviour change with nothing proving it any more"
        );
    }

    // Every changed row explains what the natives used to do; every agreed row
    // has nothing to explain.
    for case in &doc.matcher_cases {
        if case.decision == AGREED {
            assert!(
                case.was.is_empty(),
                "{:?} is agreed but carries a `was`",
                case.name
            );
        } else {
            assert!(
                !case.was.is_empty(),
                "{:?} changes behaviour with no `was` note",
                case.name
            );
        }
    }
}

/// Writes the vectors from the current shared-core answer. Ignored by default;
/// run it deliberately after an intended policy change, then read the diff.
#[test]
#[ignore = "regenerates shared-conformance/phonetic-vectors.json"]
fn regenerate() {
    let doc = build_document();
    let mut json = serde_json::to_string_pretty(&doc).expect("vectors must serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("vectors must be writable");
    eprintln!("wrote {}", vectors_path().display());
}
