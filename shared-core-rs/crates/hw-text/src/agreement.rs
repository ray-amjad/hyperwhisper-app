//! Streaming word-agreement — the two shipping LocalAgreement policies, ported
//! from macOS `WordAgreementEngine.swift` and the parakeet daemon's
//! `BoundedWordAgreement` (`LiveEngineSession.cs`).
//!
//! A streaming decoder re-decodes an overlapping audio window every pass, so the
//! tail of every pass is unstable. Both platforms solve that the same way — hold
//! a prefix back until repeated passes agree on it — and both arrived at a
//! different policy for "agree".
//!
//! Unification note (issue #286): the two engines here are deliberately **not**
//! merged into one algorithm with a mode flag. They differ in their inputs
//! (per-word times and confidence vs text only), in their state (the previous
//! pass alone vs three retained hypotheses), in when they commit (a fourth
//! identical pass vs the third) and in their failure mode (infallible vs a
//! throwing size cap). Closing that drift needs product decisions plus per-word
//! times and confidence that the daemon's sherpa `OfflineRecognizer` path does
//! not produce (`LiveEngineSession.CreateRollingOffline`'s decode closure yields
//! `stream.Result.Text` and nothing else) — a separate issue.
//! What is shared is what is genuinely common: the word normalizers, the
//! common-prefix helper and the tests that pin each policy. The drift itself is
//! asserted in `agreement_engines_disagree_on_when_a_stable_pass_commits`, so it
//! cannot move silently.
//!
//! Three Unicode notes, all deliberate:
//!
//! * [`BoundedAgreement`] counts its size cap in **UTF-16 code units**, because
//!   the C# original caps on `string.Length`. Japanese is ~3 UTF-8 bytes per
//!   UTF-16 unit, so counting bytes would fire the cap three times too early.
//! * The daemon's `NormalizeWord` is `char.IsLetterOrDigit` over UTF-16 code
//!   units, which drops every astral-plane scalar (it sees two surrogates, and a
//!   surrogate is neither a letter nor a digit). [`normalize_bounded_word`]
//!   reproduces that, including the drop, rather than silently widening the
//!   comparison.
//! * The macOS normalizer is the opposite case: Swift's `String.filter` iterates
//!   **grapheme clusters** and `String ==` is **canonical equivalence**, so
//!   [`normalize_timed_text`] segments and NFC-normalizes to keep the two units
//!   of comparison Swift's. The two normalizers are therefore not
//!   interchangeable and must not be merged — see each one's doc comment.
//!
//! The two Unicode crates this module pulls into `hw-text` (whose written policy
//! is "no deps beyond regex") exist only for that third note. They are already
//! linked by `hw-core` and `hw-phonetic`, so nothing new reaches the artifact.

use std::fmt;
use std::sync::OnceLock;

use regex::Regex;
use unicode_normalization::UnicodeNormalization;
use unicode_segmentation::UnicodeSegmentation;

// =============================================================================
// Shared pieces
// =============================================================================

/// One decoded word with the timings and confidence the macOS Parakeet path
/// produces. Mirrors Swift `TimedWord` minus its cached `normalizedText`, which
/// [`PairwiseAgreement`] keeps internally instead.
#[derive(Clone, Debug, PartialEq)]
pub struct TimedWord {
    pub text: String,
    pub start_time: f64,
    pub end_time: f64,
    pub confidence: f32,
}

impl TimedWord {
    pub fn new(text: impl Into<String>, start_time: f64, end_time: f64, confidence: f32) -> Self {
        Self {
            text: text.into(),
            start_time,
            end_time,
            confidence,
        }
    }
}

/// Length of the longest common prefix of two slices under `same`.
///
/// Both engines need exactly this: macOS compares the current pass against the
/// previous one, and the daemon folds it across its three retained hypotheses.
fn common_prefix_len<T>(left: &[T], right: &[T], same: impl Fn(&T, &T) -> bool) -> usize {
    left.iter()
        .zip(right.iter())
        .take_while(|(l, r)| same(l, r))
        .count()
}

/// `value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)`.
fn split_words(value: &str) -> Vec<String> {
    value.split_whitespace().map(str::to_string).collect()
}

/// Cached `[\p{L}\p{Nd}]` matcher — exactly .NET `char.IsLetterOrDigit`, which
/// is the five letter categories plus `Nd` (**not** `Nl`/`No`, which Rust's
/// `char::is_alphanumeric` would also accept). The same trick
/// `smart_spacing::punctuation_re` uses to reach a .NET category predicate
/// without adding a Unicode-tables dependency.
fn letter_or_digit_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"^[\p{L}\p{Nd}]$").unwrap())
}

fn is_letter_or_digit(c: char) -> bool {
    if c.is_ascii() {
        return c.is_ascii_alphanumeric();
    }
    let mut buf = [0u8; 4];
    letter_or_digit_re().is_match(c.encode_utf8(&mut buf))
}

/// macOS `TimedWord.normalize` (the `private static func normalize` on Swift
/// `TimedWord`, deleted by this PR — see `WordAgreementEngine.swift` at
/// `03cbc92`): lowercase the **whole** string (so Swift's final-sigma rule
/// applies), map `-` to a space, keep letters/digits/whitespace, then trim.
///
/// Interior whitespace is deliberately **not** collapsed — the Swift original
/// does not collapse it either.
///
/// Two Swift semantics that a naive per-`char` port loses, and that the results
/// of this function are compared under (`PairwiseAgreement::observe`):
///
/// * **The filter unit is a grapheme cluster, not a scalar.** Swift
///   `filter { $0.isLetter || $0.isNumber || $0.isWhitespace }` runs over
///   `Character`, and each of those three properties reads the **first scalar**
///   of the cluster, keeping or dropping the whole cluster with it. Filtering
///   per scalar instead drops combining marks that are not themselves
///   `Alphabetic` — Devanagari virama (U+094D), Thai tone marks (U+0E48-U+0E4B)
///   — so `कर्म` and `करम` would normalize alike and a prefix the decoder never
///   agreed on would be committed.
/// * **Equality is canonical, not bytewise.** Swift `String ==` compares under
///   canonical equivalence, so a precomposed `é` (U+00E9) from one pass equals a
///   decomposed `é` (U+0065 U+0301) from the next; the words are rebuilt from
///   sub-word tokens, so both forms really do occur. Rust `String ==` is
///   bytewise, so the canonical form is applied **here** instead — NFC on the
///   way out makes every later `==` canonical for free.
///
/// The `.nfc()` + `graphemes(true)` pairing is the same shape
/// `hw-core`'s `ffi_backup::normalize_mode_name` uses to reproduce Swift string
/// handling.
///
/// Residual divergence, unchanged from the per-scalar port: Rust
/// `char::is_numeric` is the `N*` categories where Swift `isNumber` is
/// `numericType != nil`, which also covers a handful of `Lo` ideographic
/// numerals — every one of which is `Alphabetic` and so kept by the first test
/// anyway.
fn normalize_timed_text(text: &str) -> String {
    let replaced = text.to_lowercase().replace('-', " ");
    let kept: String = replaced
        .graphemes(true)
        .filter(|cluster| cluster.chars().next().is_some_and(is_swift_word_character))
        .collect();
    kept.trim().nfc().collect()
}

/// Swift `Character.isLetter || .isNumber || .isWhitespace`, read off the first
/// scalar of a cluster as Swift reads it. `char::is_alphabetic` is the Unicode
/// `Alphabetic` property, which is what Swift's `isLetter` reports.
fn is_swift_word_character(first_scalar: char) -> bool {
    first_scalar.is_alphabetic() || first_scalar.is_numeric() || first_scalar.is_whitespace()
}

/// Daemon `NormalizeWord` (the `private static string NormalizeWord` on
/// `LiveEngineSession`, deleted by this PR — see `LiveEngineSession.cs` at
/// `03cbc92`): `ToLowerInvariant()` then keep `char.IsLetterOrDigit` over UTF-16
/// code units.
///
/// Lowercasing is per-`char`, not per-string, because `ToLowerInvariant` applies
/// no context-sensitive rule; `str::to_lowercase` would lower a final `Σ` to `ς`
/// where .NET gives `σ`. The `<= 0xFFFF` guard reproduces the surrogate drop
/// described in the module docs.
fn normalize_bounded_word(value: &str) -> String {
    value
        .chars()
        .flat_map(char::to_lowercase)
        .filter(|c| (*c as u32) <= 0xFFFF && is_letter_or_digit(*c))
        .collect()
}

/// Daemon `Equivalent`: ordinal comparison of the normalized forms.
fn equivalent(left: &str, right: &str) -> bool {
    normalize_bounded_word(left) == normalize_bounded_word(right)
}

// =============================================================================
// macOS: pairwise LocalAgreement-2 over timed, confidence-scored words
// =============================================================================

/// Sentence terminators, from `confirmationWordCount`.
const SENTENCE_ENDERS: [char; 4] = ['.', '!', '?', ';'];

/// How many sentence enders the punctuation rule needs before it may cut.
/// Hard-coded in Swift, not configurable.
const SENTENCE_ENDERS_REQUIRED: usize = 3;

/// How many words before the confirmation boundary must clear
/// `min_word_confidence`. Hard-coded in Swift (`.suffix(3)`), and independent of
/// [`PairwiseConfig::trailing_words_to_hold_without_punctuation`].
const BOUNDARY_CONFIDENCE_WORDS: usize = 3;

/// macOS `AgreementConfig`.
///
/// `transcribeIntervalSeconds` is deliberately absent: its only reader is
/// `ParakeetStreamingSession.start()`, which turns it into the pass timer's
/// sleep interval. It never reaches the engine.
#[derive(Clone, Debug, PartialEq)]
pub struct PairwiseConfig {
    pub token_confirmations_needed: usize,
    pub min_words_to_confirm: usize,
    pub min_words_to_confirm_without_punctuation: usize,
    pub trailing_words_to_hold_without_punctuation: usize,
    /// Passes below this are shown as hypothesis but do not count toward
    /// confirmation.
    pub min_pass_confidence: f32,
    /// Every word in the last [`BOUNDARY_CONFIDENCE_WORDS`] positions before the
    /// boundary must meet this to be confirmed.
    pub min_word_confidence: f32,
}

impl Default for PairwiseConfig {
    fn default() -> Self {
        Self {
            token_confirmations_needed: 3,
            min_words_to_confirm: 5,
            min_words_to_confirm_without_punctuation: 8,
            trailing_words_to_hold_without_punctuation: 3,
            min_pass_confidence: 0.15,
            min_word_confidence: 0.6,
        }
    }
}

/// What one pass produced. `full_text` and `newly_confirmed_text` are Swift's
/// `AgreementResult`; the two times are the engine properties
/// `ParakeetStreamingSession` reads once per pass, returned here so a caller can
/// cache them instead of calling back across an FFI boundary.
#[derive(Clone, Debug, PartialEq)]
pub struct PairwiseOutcome {
    pub full_text: String,
    pub newly_confirmed_text: String,
    pub confirmed_end_time: f64,
    pub hypothesis_start_time: f64,
}

/// macOS `WordAgreementEngine`'s engine half — pairwise agreement against the
/// previous pass only.
///
/// `is_first_pass` burns the first pass without counting it, so a **fourth**
/// identical pass is what commits, not the third. That is not a bug being
/// carried over blindly; it is what the shipping macOS test
/// `agreementEngineCommitsStableSpeechWithoutSentenceEnders`
/// (`hyperwhisperTests.swift`) asserts, and
/// `agreement_commits_on_the_fourth_identical_pass` pins it here.
#[derive(Debug)]
pub struct PairwiseAgreement {
    config: PairwiseConfig,
    confirmed_texts: Vec<String>,
    /// Normalized text of the previous pass. The engine only ever compares
    /// previous words, never re-reads their timings.
    previous_normalized: Vec<String>,
    consecutive_agreement_count: usize,
    is_first_pass: bool,
    confirmed_end_time: f64,
    hypothesis_start_time: f64,
}

impl PairwiseAgreement {
    pub fn new(config: PairwiseConfig) -> Self {
        Self {
            config,
            confirmed_texts: Vec::new(),
            previous_normalized: Vec::new(),
            consecutive_agreement_count: 0,
            is_first_pass: true,
            confirmed_end_time: 0.0,
            hypothesis_start_time: 0.0,
        }
    }

    pub fn reset(&mut self) {
        self.confirmed_texts.clear();
        self.previous_normalized.clear();
        self.consecutive_agreement_count = 0;
        self.is_first_pass = true;
        self.confirmed_end_time = 0.0;
        self.hypothesis_start_time = 0.0;
    }

    /// The confirmed prefix, space-joined (Swift `confirmedText`).
    pub fn confirmed_text(&self) -> String {
        self.confirmed_texts.join(" ")
    }

    /// One pass. Mirrors `processTranscriptionResult(words:resultConfidence:)`.
    pub fn observe(&mut self, words: &[TimedWord], pass_confidence: f32) -> PairwiseOutcome {
        // An empty pass returns before the first-pass branch, so it neither
        // burns pass 1 nor resets the agreement counter.
        if words.is_empty() {
            return self.make_result(&[], &[]);
        }

        if self.is_first_pass {
            self.is_first_pass = false;
            self.previous_normalized = normalized_texts(words);
            return self.make_result(words, &[]);
        }

        // Low-confidence pass: shown as hypothesis, does not count toward
        // agreement, but still becomes the previous pass.
        if pass_confidence < self.config.min_pass_confidence {
            self.consecutive_agreement_count = 0;
            self.previous_normalized = normalized_texts(words);
            return self.make_result(words, &[]);
        }

        let current_normalized = normalized_texts(words);
        let common_prefix_length =
            common_prefix_len(&current_normalized, &self.previous_normalized, |l, r| {
                l == r
            });
        self.previous_normalized = current_normalized;

        if common_prefix_length >= self.config.min_words_to_confirm {
            self.consecutive_agreement_count = self.consecutive_agreement_count.saturating_add(1);
        } else {
            self.consecutive_agreement_count = 0;
            return self.make_result(words, &[]);
        }

        if self.consecutive_agreement_count < self.config.token_confirmations_needed {
            return self.make_result(words, &[]);
        }

        // The sentence rule runs over the agreed prefix, not the whole pass.
        let agreed = words.get(..common_prefix_length).unwrap_or(words);
        let confirm_up_to = self.confirmation_word_count(agreed);
        if confirm_up_to == 0 {
            return self.make_result(words, &[]);
        }

        // Every word at the confirmation boundary must clear the floor. Written
        // as a positive test because Swift's `guard x >= floor` rejects NaN,
        // where `if x < floor` would accept it.
        let boundary_start = confirm_up_to.saturating_sub(BOUNDARY_CONFIDENCE_WORDS);
        let boundary = words.get(boundary_start..confirm_up_to).unwrap_or(&[]);
        let confident = min_confidence(boundary) >= self.config.min_word_confidence;
        if !confident {
            return self.make_result(words, &[]);
        }

        let newly_confirmed = words.get(..confirm_up_to).unwrap_or(&[]);
        let hypothesis = words.get(confirm_up_to..).unwrap_or(&[]);

        self.confirmed_texts
            .extend(newly_confirmed.iter().map(|word| word.text.clone()));
        if let Some(last) = newly_confirmed.last() {
            self.confirmed_end_time = last.end_time;
        }
        // Reads the just-updated `confirmed_end_time` when the hypothesis is
        // empty — Swift `hypothesisStartTime = hypothesis.first?.startTime ??
        // confirmedEndTime` in `processTranscriptionResult`, deleted by this PR
        // (see `WordAgreementEngine.swift` at `03cbc92`).
        self.hypothesis_start_time = hypothesis
            .first()
            .map_or(self.confirmed_end_time, |word| word.start_time);

        // The remaining hypothesis words already appeared in this pass, so their
        // count starts at 1.
        self.consecutive_agreement_count = usize::from(!hypothesis.is_empty());
        self.previous_normalized = normalized_texts(hypothesis);
        self.is_first_pass = hypothesis.is_empty();

        self.make_result(hypothesis, newly_confirmed)
    }

    /// Swift `confirmationWordCount`. Confirms at sentence boundaries when three
    /// enders exist, otherwise holds a trailing window back.
    fn confirmation_word_count(&self, words: &[TimedWord]) -> usize {
        if words.is_empty() {
            return 0;
        }

        let punctuation_indices: Vec<usize> = words
            .iter()
            .enumerate()
            .filter(|(_, word)| {
                word.text
                    .chars()
                    .next_back()
                    .is_some_and(|last| SENTENCE_ENDERS.contains(&last))
            })
            .map(|(index, _)| index)
            .collect();

        if punctuation_indices.len() >= SENTENCE_ENDERS_REQUIRED {
            // The third-from-last ender: the last two sentences stay hypothesis.
            let cut = punctuation_indices
                .get(punctuation_indices.len() - SENTENCE_ENDERS_REQUIRED)
                .copied()
                .unwrap_or(0);
            let confirm_count = cut.saturating_add(1);
            if confirm_count >= self.config.min_words_to_confirm {
                return confirm_count;
            }
            // Otherwise fall through to the no-punctuation rule — Swift does not
            // return 0 here.
        }

        let trailing_words_to_hold = self
            .config
            .trailing_words_to_hold_without_punctuation
            .max(1);
        if words.len() < self.config.min_words_to_confirm_without_punctuation {
            return 0;
        }

        let fallback_confirm_count = words.len().saturating_sub(trailing_words_to_hold);
        if fallback_confirm_count < self.config.min_words_to_confirm {
            return 0;
        }
        fallback_confirm_count
    }

    /// Swift `makeResult`: each list is space-joined, then the two non-empty
    /// parts are space-joined — so a confirmed-only result carries no trailing
    /// space and a hypothesis-only result no leading one.
    fn make_result(
        &self,
        hypothesis: &[TimedWord],
        newly_confirmed: &[TimedWord],
    ) -> PairwiseOutcome {
        let confirmed_text = self.confirmed_text();
        let hypothesis_text = join_texts(hypothesis);
        let mut parts: Vec<&str> = Vec::new();
        if !confirmed_text.is_empty() {
            parts.push(&confirmed_text);
        }
        if !hypothesis_text.is_empty() {
            parts.push(&hypothesis_text);
        }

        PairwiseOutcome {
            full_text: parts.join(" "),
            newly_confirmed_text: join_texts(newly_confirmed),
            confirmed_end_time: self.confirmed_end_time,
            hypothesis_start_time: self.hypothesis_start_time,
        }
    }
}

fn normalized_texts(words: &[TimedWord]) -> Vec<String> {
    words
        .iter()
        .map(|word| normalize_timed_text(&word.text))
        .collect()
}

fn join_texts(words: &[TimedWord]) -> String {
    words
        .iter()
        .map(|word| word.text.as_str())
        .collect::<Vec<_>>()
        .join(" ")
}

/// Swift `[Float].min()`: a left-to-right `<` fold seeded with the first
/// element, `?? 1.0` on empty. Folded in the same order rather than through
/// `f32::min`, which treats NaN differently.
fn min_confidence(words: &[TimedWord]) -> f32 {
    let mut iter = words.iter();
    let Some(first) = iter.next() else {
        return 1.0;
    };
    let mut smallest = first.confidence;
    for word in iter {
        if word.confidence < smallest {
            smallest = word.confidence;
        }
    }
    smallest
}

// =============================================================================
// Daemon: bounded LocalAgreement-3 over plain text
// =============================================================================

/// The daemon's `BoundedWordAgreement` constants.
#[derive(Clone, Debug, PartialEq)]
pub struct BoundedConfig {
    pub confirmations_needed: usize,
    pub minimum_words: usize,
    pub trailing_words: usize,
    /// Cap on the live transcript, in **UTF-16 code units** (C#
    /// `MaximumCharacters`, compared against `string.Length`).
    pub maximum_utf16_units: usize,
    /// Separator between words — `" "` for spaced languages, `""` for the
    /// continuous scripts (`Program.IsNoSpaceLanguage`).
    pub join: String,
}

impl Default for BoundedConfig {
    fn default() -> Self {
        Self {
            confirmations_needed: 3,
            minimum_words: 8,
            trailing_words: 3,
            maximum_utf16_units: 512 * 1024,
            join: " ".to_string(),
        }
    }
}

impl BoundedConfig {
    /// The daemon's only degree of freedom: `new BoundedWordAgreement(join)`.
    pub fn with_join(join: impl Into<String>) -> Self {
        Self {
            join: join.into(),
            ..Self::default()
        }
    }
}

/// What one `observe`/`finish` produced — C# `LiveEngineUpdate`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Update {
    pub preview: String,
    pub committed: String,
}

/// The only way [`BoundedAgreement`] fails.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AgreementError {
    /// The live transcript would exceed [`BoundedConfig::maximum_utf16_units`].
    LimitExceeded,
}

impl fmt::Display for AgreementError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            // Verbatim from the daemon, which puts this text on the wire. It
            // names the default cap even if the config carries another one,
            // because changing the wording would change observed behaviour.
            Self::LimitExceeded => write!(f, "Live transcript exceeded the 512 KiB limit"),
        }
    }
}

impl std::error::Error for AgreementError {}

/// The parakeet daemon's `BoundedWordAgreement` — true LocalAgreement-3 over the
/// last three retained hypotheses, with no timing or confidence data.
///
/// Commits on the **third** agreeing `observe`, where [`PairwiseAgreement`]
/// needs a fourth pass. See the module's unification note.
#[derive(Debug)]
pub struct BoundedAgreement {
    config: BoundedConfig,
    hypotheses: Vec<Vec<String>>,
    committed: Vec<String>,
}

impl BoundedAgreement {
    pub fn new(config: BoundedConfig) -> Self {
        Self {
            config,
            hypotheses: Vec::new(),
            committed: Vec::new(),
        }
    }

    /// The committed prefix plus the newest hypothesis.
    ///
    /// `observe` and `finish` already return this string. A caller on a
    /// per-audio-chunk path must cache what they return rather than call this,
    /// which is why `LiveEngineSession.cs` reads a field.
    pub fn preview(&self) -> String {
        self.join(
            self.committed
                .iter()
                .map(String::as_str)
                .chain(self.last_hypothesis().iter().map(String::as_str)),
        )
    }

    /// One decoded hypothesis. Mirrors C# `Observe`.
    pub fn observe(&mut self, hypothesis: &str) -> Result<Update, AgreementError> {
        let words = self.without_committed_overlap(split_words(hypothesis));
        let projected = self.joined_utf16_len(
            self.committed
                .iter()
                .map(String::as_str)
                .chain(words.iter().map(String::as_str)),
        );
        if projected > self.config.maximum_utf16_units {
            return Err(AgreementError::LimitExceeded);
        }

        self.hypotheses.push(words);
        if self.hypotheses.len() > self.config.confirmations_needed {
            self.hypotheses.remove(0);
        }

        let mut newly_committed: Vec<String> = Vec::new();
        if self.hypotheses.len() == self.config.confirmations_needed {
            let common_length = self.common_hypothesis_prefix_len();
            let confirmation_count = if common_length >= self.config.minimum_words {
                common_length.saturating_sub(self.config.trailing_words)
            } else {
                0
            };
            if confirmation_count
                >= self
                    .config
                    .minimum_words
                    .saturating_sub(self.config.trailing_words)
            {
                // The committed spelling comes from the OLDEST retained
                // hypothesis — C# `CommonPrefix` returns `values[0][..count]`.
                newly_committed = self
                    .hypotheses
                    .first()
                    .map(|oldest| oldest.iter().take(confirmation_count).cloned().collect())
                    .unwrap_or_default();
                self.append_committed(&newly_committed)?;
                for hypothesis in &mut self.hypotheses {
                    *hypothesis = hypothesis
                        .iter()
                        .skip(confirmation_count)
                        .cloned()
                        .collect();
                }
            }
        }

        Ok(Update {
            preview: self.preview(),
            committed: self.join(newly_committed.iter().map(String::as_str)),
        })
    }

    /// The final decode. Mirrors C# `Finish`: the tail is appended **before**
    /// the hypotheses are cleared, so the cap check inside
    /// [`Self::append_committed`] still sees the last hypothesis.
    pub fn finish(&mut self, final_hypothesis: &str) -> Result<Update, AgreementError> {
        let tail = self.without_committed_overlap(split_words(final_hypothesis));
        self.append_committed(&tail)?;
        self.hypotheses.clear();
        Ok(Update {
            preview: self.join(self.committed.iter().map(String::as_str)),
            committed: self.join(tail.iter().map(String::as_str)),
        })
    }

    fn last_hypothesis(&self) -> &[String] {
        self.hypotheses.last().map_or(&[], Vec::as_slice)
    }

    /// C# `AppendCommitted`. The budget is re-measured **per word**, against a
    /// preview that still includes the untrimmed last hypothesis. Hoisting the
    /// measurement out of the loop would move the throw point.
    fn append_committed(&mut self, words: &[String]) -> Result<(), AgreementError> {
        for word in words {
            let preview_length = self.joined_utf16_len(
                self.committed
                    .iter()
                    .map(String::as_str)
                    .chain(self.last_hypothesis().iter().map(String::as_str)),
            );
            let projected = preview_length
                .saturating_add(word.encode_utf16().count())
                .saturating_add(1);
            if projected > self.config.maximum_utf16_units {
                return Err(AgreementError::LimitExceeded);
            }
            self.committed.push(word.clone());
        }
        Ok(())
    }

    /// C# `WithoutCommittedOverlap`: longest-first, the greatest `count` whose
    /// last `count` committed words match the first `count` of `words`.
    fn without_committed_overlap(&self, words: Vec<String>) -> Vec<String> {
        let maximum = self.committed.len().min(words.len());
        for count in (1..=maximum).rev() {
            let start = self.committed.len().saturating_sub(count);
            let matches = self
                .committed
                .iter()
                .skip(start)
                .zip(words.iter())
                .take(count)
                .all(|(left, right)| equivalent(left, right));
            if matches {
                return words.into_iter().skip(count).collect();
            }
        }
        words
    }

    /// C# `CommonPrefix`'s length.
    ///
    /// C# advances one position at a time while every hypothesis agrees with the
    /// first; folding the pairwise prefix length against the first hypothesis is
    /// the same number, because "all agree up to `k`" is a prefix property.
    fn common_hypothesis_prefix_len(&self) -> usize {
        let Some(first) = self.hypotheses.first() else {
            return 0;
        };
        let mut length = first.len();
        for other in self.hypotheses.iter().skip(1) {
            length = length.min(common_prefix_len(first, other, |l, r| equivalent(l, r)));
        }
        length
    }

    fn join<'a>(&self, parts: impl Iterator<Item = &'a str>) -> String {
        let mut joined = String::new();
        for (index, part) in parts.enumerate() {
            if index > 0 {
                joined.push_str(&self.config.join);
            }
            joined.push_str(part);
        }
        joined
    }

    /// The UTF-16 length [`Self::join`] would produce, without building the
    /// string. Same number, no half-megabyte allocation per committed word.
    fn joined_utf16_len<'a>(&self, parts: impl Iterator<Item = &'a str>) -> usize {
        let separator = self.config.join.encode_utf16().count();
        let mut total = 0usize;
        for (index, part) in parts.enumerate() {
            if index > 0 {
                total = total.saturating_add(separator);
            }
            total = total.saturating_add(part.encode_utf16().count());
        }
        total
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const CAP: usize = 512 * 1024;

    // -- macOS helpers --------------------------------------------------------

    /// `makeWords` from `hyperwhisperTests.swift`.
    fn make_words(texts: &[&str], confidence: f32) -> Vec<TimedWord> {
        texts
            .iter()
            .enumerate()
            .map(|(index, text)| {
                let start = index as f64;
                TimedWord::new(*text, start, start + 0.5, confidence)
            })
            .collect()
    }

    fn ten_words() -> Vec<TimedWord> {
        make_words(
            &[
                "this", "is", "me", "testing", "out", "to", "make", "sure", "it", "works",
            ],
            0.95,
        )
    }

    // -- macOS: the shipping test -------------------------------------------

    /// The exact scenario of `hyperwhisperTests.swift`'s
    /// `agreementEngineCommitsStableSpeechWithoutSentenceEnders`, including the
    /// fact that the third identical pass commits nothing.
    #[test]
    fn agreement_commits_on_the_fourth_identical_pass() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = ten_words();

        let first = engine.observe(&words, 0.95);
        assert_eq!(first.newly_confirmed_text, "");
        assert_eq!(
            first.full_text,
            "this is me testing out to make sure it works"
        );

        assert_eq!(engine.observe(&words, 0.95).newly_confirmed_text, "");
        let third = engine.observe(&words, 0.95);
        assert_eq!(third.newly_confirmed_text, "");
        assert_eq!(
            third.full_text,
            "this is me testing out to make sure it works"
        );

        let fourth = engine.observe(&words, 0.95);
        assert_eq!(
            fourth.newly_confirmed_text,
            "this is me testing out to make"
        );
        assert_eq!(
            fourth.full_text,
            "this is me testing out to make sure it works"
        );
        // `make` ends at index 6 + 0.5; `sure` starts at index 7.
        assert_eq!(fourth.confirmed_end_time, 6.5);
        assert_eq!(fourth.hypothesis_start_time, 7.0);
        assert_eq!(engine.confirmed_text(), "this is me testing out to make");
    }

    /// After a commit the retained hypothesis is the previous pass, so the same
    /// full ten-word pass no longer agrees with it and the text repeats. This is
    /// current shipping behaviour, pinned so a "fix" cannot land unnoticed.
    #[test]
    fn agreement_state_after_a_commit_restarts_the_count() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = ten_words();
        for _ in 0..4 {
            let _ = engine.observe(&words, 0.95);
        }

        let next = engine.observe(&words, 0.95);
        assert_eq!(next.newly_confirmed_text, "");
        assert_eq!(
            next.full_text,
            "this is me testing out to make this is me testing out to make sure it works"
        );
        // The confirmed times survive a non-committing pass.
        assert_eq!(next.confirmed_end_time, 6.5);
        assert_eq!(next.hypothesis_start_time, 7.0);
    }

    #[test]
    fn agreement_first_pass_is_hypothesis_only() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let outcome = engine.observe(&make_words(&["alpha", "beta"], 0.9), 0.95);
        assert_eq!(outcome.full_text, "alpha beta");
        assert_eq!(outcome.newly_confirmed_text, "");
        assert_eq!(outcome.confirmed_end_time, 0.0);
        assert_eq!(outcome.hypothesis_start_time, 0.0);
    }

    /// Three enders late in the pass cut before the last two sentences, which is
    /// earlier than the no-punctuation fallback would (6 words, not 7).
    #[test]
    fn agreement_sentence_enders_cut_before_the_last_two_sentences() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = make_words(
            &[
                "one", "two", "three", "four", "five", "six.", "seven.", "eight.", "nine", "ten",
            ],
            0.95,
        );
        for _ in 0..3 {
            let _ = engine.observe(&words, 0.95);
        }
        let outcome = engine.observe(&words, 0.95);
        assert_eq!(outcome.newly_confirmed_text, "one two three four five six.");
        assert_eq!(
            outcome.full_text,
            "one two three four five six. seven. eight. nine ten"
        );
    }

    /// Three enders exist, but the cut lands before `min_words_to_confirm`, so
    /// the punctuation branch **falls through** to the no-punctuation rule
    /// instead of returning 0.
    #[test]
    fn agreement_sentence_ender_branch_falls_through_when_the_cut_is_too_early() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = make_words(
            &[
                "one.", "two.", "three.", "four", "five", "six", "seven", "eight", "nine", "ten",
            ],
            0.95,
        );
        for _ in 0..3 {
            let _ = engine.observe(&words, 0.95);
        }
        let outcome = engine.observe(&words, 0.95);
        // Cut would be index 0 -> 1 word, below the floor of 5; the fallback
        // confirms len - 3 = 7.
        assert_eq!(
            outcome.newly_confirmed_text,
            "one. two. three. four five six seven"
        );
    }

    #[test]
    fn agreement_low_confidence_pass_resets_the_count_and_becomes_previous() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = ten_words();
        let _ = engine.observe(&words, 0.95);
        let _ = engine.observe(&words, 0.95);
        // Below `min_pass_confidence`: no counting, but it still becomes the
        // previous pass.
        let low = engine.observe(&words, 0.1);
        assert_eq!(low.newly_confirmed_text, "");
        // The counter restarts, so two more passes are not enough...
        assert_eq!(engine.observe(&words, 0.95).newly_confirmed_text, "");
        assert_eq!(engine.observe(&words, 0.95).newly_confirmed_text, "");
        // ...and the third one after the reset commits.
        assert_eq!(
            engine.observe(&words, 0.95).newly_confirmed_text,
            "this is me testing out to make"
        );
    }

    /// A low-confidence pass with different words also replaces `previousWords`,
    /// so the next pass has nothing to agree with.
    #[test]
    fn agreement_low_confidence_pass_replaces_the_previous_words() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = ten_words();
        let _ = engine.observe(&words, 0.95);
        let _ = engine.observe(&make_words(&["totally", "different", "words"], 0.9), 0.1);
        // Prefix length 0 against the low-confidence pass -> counter stays at 0.
        let _ = engine.observe(&words, 0.95);
        let _ = engine.observe(&words, 0.95);
        assert_eq!(engine.observe(&words, 0.95).newly_confirmed_text, "");
        assert_eq!(
            engine.observe(&words, 0.95).newly_confirmed_text,
            "this is me testing out to make"
        );
    }

    /// One weak word inside the three-word boundary window blocks the commit
    /// forever, even though the passes agree.
    #[test]
    fn agreement_boundary_confidence_below_the_floor_blocks_the_commit() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let mut words = ten_words();
        // Index 5 sits inside `words[..7].suffix(3)` = indices 4, 5, 6.
        words[5].confidence = 0.5;
        for _ in 0..4 {
            let outcome = engine.observe(&words, 0.95);
            assert_eq!(outcome.newly_confirmed_text, "");
        }
        // A weak word outside the window does not block anything.
        let mut other = ten_words();
        other[8].confidence = 0.5;
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        for _ in 0..3 {
            let _ = engine.observe(&other, 0.95);
        }
        assert_eq!(
            engine.observe(&other, 0.95).newly_confirmed_text,
            "this is me testing out to make"
        );
    }

    /// An empty pass returns before the first-pass branch: it does not burn
    /// pass 1 and does not reset the counter.
    #[test]
    fn agreement_empty_pass_neither_burns_the_first_pass_nor_resets_the_count() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = ten_words();

        let empty = engine.observe(&[], 0.95);
        assert_eq!(empty.full_text, "");
        assert_eq!(empty.newly_confirmed_text, "");

        let _ = engine.observe(&words, 0.95); // still the first pass
        let _ = engine.observe(&words, 0.95);
        let _ = engine.observe(&words, 0.95);
        let _ = engine.observe(&[], 0.95); // mid-stream, counter untouched
        assert_eq!(
            engine.observe(&words, 0.95).newly_confirmed_text,
            "this is me testing out to make"
        );
    }

    /// An empty pass after a commit reports the confirmed text alone — no
    /// trailing space, because `makeResult` drops empty parts.
    #[test]
    fn agreement_empty_pass_after_a_commit_reports_the_confirmed_text_alone() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = ten_words();
        for _ in 0..4 {
            let _ = engine.observe(&words, 0.95);
        }
        let outcome = engine.observe(&[], 0.95);
        assert_eq!(outcome.full_text, "this is me testing out to make");
        assert_eq!(outcome.newly_confirmed_text, "");
    }

    /// A pass shorter than `min_words_to_confirm` never accumulates agreement.
    #[test]
    fn agreement_short_passes_never_commit() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = make_words(&["one", "two", "three", "four"], 0.95);
        for _ in 0..6 {
            assert_eq!(engine.observe(&words, 0.95).newly_confirmed_text, "");
        }
    }

    /// Normalization is case- and punctuation-insensitive, so passes that differ
    /// only in those still agree — and the committed spelling is the **current**
    /// pass's, not the previous one's.
    #[test]
    fn agreement_normalization_ignores_case_and_punctuation() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let plain = ten_words();
        let fancy = make_words(
            &[
                "This,", "IS", "me", "testing", "out", "to", "make", "sure", "it", "works",
            ],
            0.95,
        );
        let _ = engine.observe(&plain, 0.95);
        let _ = engine.observe(&fancy, 0.95);
        let _ = engine.observe(&plain, 0.95);
        let outcome = engine.observe(&fancy, 0.95);
        assert_eq!(
            outcome.newly_confirmed_text,
            "This, IS me testing out to make"
        );
    }

    /// A hyphen is one of the drifts between the two normalizers: macOS turns it
    /// into a space (so `test-ing` and `testing` do **not** agree), while the
    /// daemon deletes it (so they do). Neither side is changed here.
    #[test]
    fn agreement_hyphen_policy_differs_between_the_two_normalizers() {
        assert_ne!(
            normalize_timed_text("test-ing"),
            normalize_timed_text("testing")
        );
        assert!(equivalent("test-ing", "testing"));

        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let plain = ten_words();
        let hyphenated = make_words(
            &[
                "this", "is", "me", "test-ing", "out", "to", "make", "sure", "it", "works",
            ],
            0.95,
        );
        for _ in 0..3 {
            let _ = engine.observe(&plain, 0.95);
        }
        // Prefix length 3, below `min_words_to_confirm`: the counter resets.
        assert_eq!(engine.observe(&hyphenated, 0.95).newly_confirmed_text, "");
    }

    #[test]
    fn agreement_normalizer_matches_the_swift_rules() {
        assert_eq!(normalize_timed_text("Hello,"), "hello");
        assert_eq!(normalize_timed_text("test-ing"), "test ing");
        assert_eq!(normalize_timed_text("  spaced  "), "spaced");
        // Interior whitespace is not collapsed.
        assert_eq!(normalize_timed_text("a - b"), "a   b");
        assert_eq!(normalize_timed_text("...!"), "");
        assert_eq!(normalize_timed_text("日本語。"), "日本語");
    }

    /// Swift filters per `Character`, and `Character.isLetter` reads the FIRST
    /// scalar of the cluster, so a combining mark that is not itself
    /// `Alphabetic` rides along with the base it attaches to. A per-scalar
    /// filter drops it, which would make two words the decoder spelled
    /// differently normalize alike and commit a prefix that never agreed.
    #[test]
    fn agreement_normalizer_keeps_combining_marks_with_their_base() {
        // Devanagari: कर्म is क र ् म — U+094D (virama) is `Mn`, not
        // `Other_Alphabetic`, so a per-scalar filter erases it.
        let with_virama = "\u{0915}\u{0930}\u{094D}\u{092E}";
        let without_virama = "\u{0915}\u{0930}\u{092E}";
        assert_eq!(normalize_timed_text(with_virama), with_virama);
        assert_ne!(
            normalize_timed_text(with_virama),
            normalize_timed_text(without_virama)
        );

        // Thai tone marks, same shape.
        let with_tone = "\u{0E01}\u{0E48}";
        assert_eq!(normalize_timed_text(with_tone), with_tone);
        assert_ne!(
            normalize_timed_text(with_tone),
            normalize_timed_text("\u{0E01}")
        );

        // A cluster whose first scalar fails the test is dropped whole, marks
        // and all — Swift drops the `Character`, not just its base.
        assert_eq!(normalize_timed_text("\u{0021}\u{0301}"), "");
    }

    /// Two passes that spell the same word with different Unicode composition
    /// must still agree, because Swift `String ==` is canonical equivalence.
    /// NFC in the normalizer is what makes the later bytewise `==` canonical.
    #[test]
    fn agreement_normalizer_is_canonically_equivalent() {
        let precomposed = "caf\u{00E9}";
        let decomposed = "cafe\u{0301}";
        assert_ne!(precomposed, decomposed);
        assert_eq!(
            normalize_timed_text(precomposed),
            normalize_timed_text(decomposed)
        );

        // And end to end: the composition flips every pass, yet the prefix
        // keeps accumulating and the fourth pass still commits.
        let spellings = [precomposed, decomposed];
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let mut confirmed = String::new();
        for pass in 0..4 {
            let words = make_words(
                &[
                    "this",
                    "is",
                    "me",
                    "testing",
                    spellings[pass % 2],
                    "to",
                    "make",
                    "sure",
                    "it",
                    "works",
                ],
                0.95,
            );
            confirmed = engine.observe(&words, 0.95).newly_confirmed_text;
        }
        assert_eq!(confirmed, format!("this is me testing {decomposed} to make"));
    }

    #[test]
    fn agreement_reset_clears_every_field() {
        let mut engine = PairwiseAgreement::new(PairwiseConfig::default());
        let words = ten_words();
        for _ in 0..4 {
            let _ = engine.observe(&words, 0.95);
        }
        engine.reset();
        assert_eq!(engine.confirmed_text(), "");

        let outcome = engine.observe(&words, 0.95);
        assert_eq!(outcome.confirmed_end_time, 0.0);
        assert_eq!(outcome.hypothesis_start_time, 0.0);
        // A fresh first pass is burned again, so four more passes are needed.
        assert_eq!(engine.observe(&words, 0.95).newly_confirmed_text, "");
        assert_eq!(engine.observe(&words, 0.95).newly_confirmed_text, "");
        assert_eq!(
            engine.observe(&words, 0.95).newly_confirmed_text,
            "this is me testing out to make"
        );
    }

    // -- daemon: the four shipping tests -------------------------------------

    fn bounded(join: &str) -> BoundedAgreement {
        BoundedAgreement::new(BoundedConfig::with_join(join))
    }

    /// `parakeet-engine-dotnet.Tests/Program.cs` `StableAgreement`.
    #[test]
    fn bounded_three_stable_passes_commit_while_retaining_a_tail() {
        let mut engine = bounded(" ");
        let value = "one two three four five six seven eight nine ten";
        assert_eq!(engine.observe(value).unwrap().committed, "");
        assert_eq!(engine.observe(value).unwrap().committed, "");
        let third = engine.observe(value).unwrap();
        assert_eq!(third.committed, "one two three four five six seven");
        assert_eq!(third.preview, value);
    }

    /// `UnstableAgreement`.
    #[test]
    fn bounded_unstable_hypotheses_remain_volatile() {
        let mut engine = bounded(" ");
        let _ = engine
            .observe("one two three four five six seven eight")
            .unwrap();
        let _ = engine
            .observe("one two changed four five six seven eight")
            .unwrap();
        let third = engine
            .observe("one two three four five six seven eight")
            .unwrap();
        assert_eq!(third.committed, "");
        assert_eq!(third.preview, "one two three four five six seven eight");
    }

    /// `FinishDeduplicates`.
    #[test]
    fn bounded_finish_commits_the_unconfirmed_tail_without_overlap() {
        let mut engine = bounded(" ");
        let value = "one two three four five six seven eight nine ten";
        for _ in 0..3 {
            let _ = engine.observe(value).unwrap();
        }
        let final_update = engine.finish("six seven eight nine ten eleven").unwrap();
        assert_eq!(final_update.committed, "eight nine ten eleven");
        assert_eq!(
            final_update.preview,
            "one two three four five six seven eight nine ten eleven"
        );
    }

    /// `NoSpaceJoin`.
    #[test]
    fn bounded_no_space_join_preserves_its_policy() {
        let mut engine = bounded("");
        assert_eq!(
            engine.finish("alpha beta gamma").unwrap().preview,
            "alphabetagamma"
        );
    }

    // -- daemon: the gotchas -------------------------------------------------

    /// `CommonPrefix` returns `values[0][..count]`, so the committed spelling is
    /// the **oldest** retained hypothesis's, not the newest.
    #[test]
    fn bounded_commits_the_oldest_hypothesis_spelling() {
        let mut engine = bounded(" ");
        let _ = engine
            .observe("One, Two, Three, Four, Five, Six, Seven, eight nine ten")
            .unwrap();
        let value = "one two three four five six seven eight nine ten";
        let _ = engine.observe(value).unwrap();
        let third = engine.observe(value).unwrap();
        assert_eq!(third.committed, "One, Two, Three, Four, Five, Six, Seven,");
    }

    /// The overlap trim scans longest-first, so a repeated tail is not committed
    /// twice even when several suffix lengths would match.
    #[test]
    fn bounded_overlap_trim_prefers_the_longest_match() {
        let mut engine = bounded(" ");
        let value = "one two three four five six seven eight nine ten";
        for _ in 0..3 {
            let _ = engine.observe(value).unwrap();
        }
        // "five six seven" is already committed; only "eight" is new.
        let update = engine.finish("five six seven eight").unwrap();
        assert_eq!(update.committed, "eight");
        assert_eq!(update.preview, "one two three four five six seven eight");
    }

    /// Nine words are not enough on their own: the commit needs eight agreeing
    /// words after the overlap trim, and holds three back.
    #[test]
    fn bounded_holds_everything_below_the_minimum_word_count() {
        let mut engine = bounded(" ");
        let value = "one two three four five six seven";
        for _ in 0..3 {
            assert_eq!(engine.observe(value).unwrap().committed, "");
        }
    }

    /// The cap counts UTF-16 code units. This transcript is over 512 KiB of
    /// UTF-8 but well under the cap, and must be accepted.
    #[test]
    fn bounded_cap_counts_utf16_units_not_utf8_bytes() {
        let hypothesis = vec!["あ"; 200_000].join(" ");
        assert!(
            hypothesis.len() > CAP,
            "the fixture must exceed the cap in bytes"
        );
        assert!(
            hypothesis.encode_utf16().count() < CAP,
            "the fixture must stay under the cap in UTF-16 units"
        );

        let mut engine = bounded("");
        let update = engine.observe(&hypothesis).unwrap();
        assert_eq!(update.committed, "");
        assert_eq!(update.preview.chars().count(), 200_000);
    }

    /// A single word longer than the whole budget is rejected, and the engine is
    /// left untouched.
    #[test]
    fn bounded_word_longer_than_the_cap_is_rejected() {
        let mut engine = bounded(" ");
        let huge = "a".repeat(CAP + 1);
        assert_eq!(engine.observe(&huge), Err(AgreementError::LimitExceeded));
        assert_eq!(engine.preview(), "");
        assert_eq!(
            engine.finish(&huge).unwrap_err().to_string(),
            "Live transcript exceeded the 512 KiB limit"
        );
    }

    /// The per-word budget check in `AppendCommitted` adds one for the separator
    /// whatever the join is, so the cap bites just under the limit.
    #[test]
    fn bounded_finish_rejects_a_tail_that_would_cross_the_cap() {
        let mut engine = BoundedAgreement::new(BoundedConfig {
            maximum_utf16_units: 12,
            ..BoundedConfig::with_join(" ")
        });
        // Preview 0 + "alpha" 5 + 1 = 6 fits; preview 5 + "beta" 4 + 1 = 10 fits;
        // preview 10 + "gamma" 5 + 1 = 16 does not.
        assert_eq!(
            engine.finish("alpha beta gamma"),
            Err(AgreementError::LimitExceeded)
        );
        // The words accepted before the throw stay committed, as in C#.
        assert_eq!(engine.preview(), "alpha beta");
    }

    #[test]
    fn bounded_degenerate_inputs_are_inert() {
        let mut engine = bounded(" ");
        for input in ["", "   ", "\t\n "] {
            let update = engine.observe(input).unwrap();
            assert_eq!(
                update,
                Update {
                    preview: String::new(),
                    committed: String::new()
                }
            );
        }
        assert_eq!(engine.preview(), "");

        let mut engine = bounded(" ");
        let update = engine.observe("solo").unwrap();
        assert_eq!(update.preview, "solo");
        assert_eq!(engine.finish("").unwrap().preview, "");
    }

    #[test]
    fn bounded_normalizer_matches_the_dotnet_rules() {
        assert_eq!(normalize_bounded_word("Hello,"), "hello");
        assert_eq!(normalize_bounded_word("test-ing"), "testing");
        assert!(equivalent("One,", "one"));
        assert!(equivalent("...", ""));
        assert!(!equivalent("one", "two"));
        // `char.IsLetterOrDigit` accepts Nd but not No/Nl, which Rust's
        // `is_alphanumeric` would.
        assert_eq!(normalize_bounded_word("½"), "");
        assert_eq!(normalize_bounded_word("Ⅻ"), "");
        assert_eq!(normalize_bounded_word("٣"), "٣");
        // Astral-plane scalars are dropped, because .NET sees two surrogates.
        assert_eq!(normalize_bounded_word("𝐀bc"), "bc");
        assert!(equivalent("𝐀", "𝐁"));
    }

    // -- the drift the shared module deliberately preserves -------------------

    /// The same stable input commits the same seven words on both engines, but
    /// the daemon needs three passes and macOS four. If this test starts
    /// failing, one platform's behaviour moved.
    #[test]
    fn agreement_engines_disagree_on_when_a_stable_pass_commits() {
        let value = "one two three four five six seven eight nine ten";
        let expected = "one two three four five six seven";

        let mut daemon = bounded(" ");
        assert_eq!(daemon.observe(value).unwrap().committed, "");
        assert_eq!(daemon.observe(value).unwrap().committed, "");
        assert_eq!(daemon.observe(value).unwrap().committed, expected);

        let words = make_words(
            &[
                "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            ],
            0.95,
        );
        let mut macos = PairwiseAgreement::new(PairwiseConfig::default());
        assert_eq!(macos.observe(&words, 0.95).newly_confirmed_text, "");
        assert_eq!(macos.observe(&words, 0.95).newly_confirmed_text, "");
        assert_eq!(macos.observe(&words, 0.95).newly_confirmed_text, "");
        assert_eq!(macos.observe(&words, 0.95).newly_confirmed_text, expected);
    }

    #[test]
    fn common_prefix_len_stops_at_the_first_difference() {
        let left = ["a", "b", "c"];
        let right = ["a", "b", "d", "e"];
        assert_eq!(common_prefix_len(&left, &right, |l, r| l == r), 2);
        assert_eq!(common_prefix_len::<&str>(&[], &right, |l, r| l == r), 0);
    }
}
