//! UniFFI surface for the streaming word-agreement engines (`hw_text::agreement`).
//!
//! Two objects, not one, because the leaf is two engines: macOS's pairwise
//! LocalAgreement-2 over timed, confidence-scored words
//! ([`HwWordAgreementSession`]) and the parakeet daemon's bounded
//! LocalAgreement-3 over plain text ([`HwBoundedAgreementSession`]). The leaf's
//! module docs explain why they are deliberately not unified (#286); this file
//! only carries them across the boundary.
//!
//! Both are `#[uniffi::Object]`s rather than free functions because both are
//! stateful across passes — the whole point of a LocalAgreement engine is what
//! it remembers from the previous decode. The leaf types take `&mut self`, so
//! each object holds its engine in a `std::sync::Mutex`, exactly as
//! [`crate::ffi_live::HwLiveSession`] does.
//!
//! Every record carries back the values a caller would otherwise have to fetch
//! with a second call. That is deliberate: both call sites poll on an audio
//! path, and an accessor per chunk would put an FFI crossing on it.

// The leaf crate itself: `hw_text::agreement` is a private module whose types
// are re-exported at the crate root, so the alias points at the crate.
use hw_text as leaf;

// ===========================================================================
// macOS: pairwise agreement over timed words
// ===========================================================================

/// One decoded word with its timings and confidence. Mirrors
/// `hw_text::TimedWord`, and Swift's `TimedWord`.
///
/// The `f64` times and `f32` confidence keep the Swift split as it is, so
/// `WordAgreementEngine` maps its own struct across with no numeric conversion.
#[derive(uniffi::Record)]
pub struct HwTimedWord {
    pub text: String,
    pub start_time: f64,
    pub end_time: f64,
    pub confidence: f32,
}

impl From<HwTimedWord> for leaf::TimedWord {
    fn from(word: HwTimedWord) -> Self {
        leaf::TimedWord {
            text: word.text,
            start_time: word.start_time,
            end_time: word.end_time,
            confidence: word.confidence,
        }
    }
}

/// macOS `AgreementConfig`. Mirrors `hw_text::PairwiseConfig`.
///
/// `transcribeIntervalSeconds` is deliberately absent: it schedules the Swift
/// pass timer in `ParakeetStreamingSession.start()` and never reaches the
/// engine. The Swift struct keeps the field; it just does not cross here.
///
/// The counts are `u32` because UniFFI has no `usize`; they are widened on the
/// way in, which is lossless on every target this ships to.
#[derive(uniffi::Record)]
pub struct HwAgreementConfig {
    pub token_confirmations_needed: u32,
    pub min_words_to_confirm: u32,
    pub min_words_to_confirm_without_punctuation: u32,
    pub trailing_words_to_hold_without_punctuation: u32,
    /// Passes below this show as hypothesis but do not count toward
    /// confirmation.
    pub min_pass_confidence: f32,
    /// Every word in the last three positions before the confirmation boundary
    /// must meet this.
    pub min_word_confidence: f32,
}

impl From<HwAgreementConfig> for leaf::PairwiseConfig {
    fn from(config: HwAgreementConfig) -> Self {
        leaf::PairwiseConfig {
            token_confirmations_needed: config.token_confirmations_needed as usize,
            min_words_to_confirm: config.min_words_to_confirm as usize,
            min_words_to_confirm_without_punctuation: config
                .min_words_to_confirm_without_punctuation
                as usize,
            trailing_words_to_hold_without_punctuation: config
                .trailing_words_to_hold_without_punctuation
                as usize,
            min_pass_confidence: config.min_pass_confidence,
            min_word_confidence: config.min_word_confidence,
        }
    }
}

/// What one pass produced. Mirrors `hw_text::PairwiseOutcome`.
///
/// The two times are **returned** on every pass, committing or not, so a caller
/// can assign its cached properties unconditionally.
/// `ParakeetStreamingSession.swift` reads both at three points per pass; taking
/// them from this record keeps every one of those reads off the FFI.
#[derive(uniffi::Record)]
pub struct HwAgreementPass {
    /// Confirmed text plus the current hypothesis, space-joined, with empty
    /// parts dropped.
    pub full_text: String,
    /// What this pass confirmed, or `""`.
    pub newly_confirmed_text: String,
    pub confirmed_end_time: f64,
    pub hypothesis_start_time: f64,
}

impl From<leaf::PairwiseOutcome> for HwAgreementPass {
    fn from(outcome: leaf::PairwiseOutcome) -> Self {
        HwAgreementPass {
            full_text: outcome.full_text,
            newly_confirmed_text: outcome.newly_confirmed_text,
            confirmed_end_time: outcome.confirmed_end_time,
            hypothesis_start_time: outcome.hypothesis_start_time,
        }
    }
}

/// The macOS streaming agreement engine: one pass in, the confirmed/hypothesis
/// split out.
///
/// The lifecycle is `new` → `observe`\* → `reset`, where `reset` returns it to
/// the state the constructor left it in so the next recording can reuse the
/// object.
///
/// Disposal differs by head, and only one head has to act. The Rust side is an
/// `Arc` the platform holds a raw handle to. In **Swift** the generated class
/// frees that handle in its own `deinit`, so ARC is the disposal: releasing the
/// last reference is enough and there is nothing to call. In **C#** the class is
/// `IDisposable` (with a finalizer as a backstop), so a consumer should
/// `Dispose` it to release the handle deterministically rather than whenever the
/// GC gets to it.
///
/// Thread safety is a plain `Mutex`, not a re-entrant one: the decode timer and
/// the stop path both reach the same instance. No method calls another through
/// the FFI, so there is nothing to re-enter.
#[derive(uniffi::Object)]
pub struct HwWordAgreementSession {
    inner: std::sync::Mutex<leaf::PairwiseAgreement>,
}

impl HwWordAgreementSession {
    /// Take the lock, recovering from a poisoned one.
    ///
    /// The release profile is `panic = "abort"`, so a poisoned mutex cannot
    /// happen in a shipped binary — but `cargo test` unwinds, and a plain
    /// `unwrap()` here would turn one failing assertion into a cascade of
    /// unrelated failures. Nothing reachable from the FFI may `unwrap()`.
    fn locked(&self) -> std::sync::MutexGuard<'_, leaf::PairwiseAgreement> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }
}

#[uniffi::export]
impl HwWordAgreementSession {
    /// Build an engine for `config`.
    ///
    /// An exported constructor, not a `word_agreement_session_new(config)` free
    /// function: a `#[uniffi::Object]` gets no foreign constructor unless one is
    /// exported, and without it a consumer can name the type and never
    /// instantiate it. This renders as `new HwWordAgreementSession(config)` in
    /// C# and `HwWordAgreementSession(config:)` in Swift.
    #[uniffi::constructor]
    pub fn new(config: HwAgreementConfig) -> std::sync::Arc<Self> {
        std::sync::Arc::new(Self {
            inner: std::sync::Mutex::new(leaf::PairwiseAgreement::new(config.into())),
        })
    }

    /// One decode pass. Mirrors Swift
    /// `processTranscriptionResult(words:resultConfidence:)`.
    ///
    /// `pass_confidence` is the whole pass's score, separate from the per-word
    /// confidences: a pass below `min_pass_confidence` is still shown as
    /// hypothesis, but it resets the agreement counter.
    pub fn observe(&self, words: Vec<HwTimedWord>, pass_confidence: f32) -> HwAgreementPass {
        let words: Vec<leaf::TimedWord> = words.into_iter().map(Into::into).collect();
        self.locked().observe(&words, pass_confidence).into()
    }

    /// Forget every pass this engine has seen, including the confirmed prefix
    /// and both cached times. What lets one object serve consecutive recordings.
    pub fn reset(&self) {
        self.locked().reset();
    }

    /// The confirmed prefix, space-joined.
    ///
    /// [`HwWordAgreementSession::observe`] already returns this inside
    /// `full_text`; this is for a caller that needs the confirmed half alone,
    /// off the audio path.
    pub fn confirmed_text(&self) -> String {
        self.locked().confirmed_text()
    }
}

// ===========================================================================
// Daemon: bounded agreement over plain text
// ===========================================================================

/// What one `observe`/`finish` produced. Mirrors `hw_text::Update`, and the
/// daemon's `LiveEngineUpdate`.
#[derive(uniffi::Record)]
pub struct HwStreamUpdate {
    /// The committed prefix plus the newest hypothesis — the whole live
    /// transcript to display.
    pub preview: String,
    /// Only what this call newly committed, or `""`.
    pub committed: String,
}

impl From<leaf::Update> for HwStreamUpdate {
    fn from(update: leaf::Update) -> Self {
        HwStreamUpdate {
            preview: update.preview,
            committed: update.committed,
        }
    }
}

/// Why a bounded session refused a hypothesis. Mirrors
/// `hw_text::AgreementError`.
///
/// One arm on purpose: the engine is otherwise infallible. `Display` is
/// hand-written to match the leaf's message, the same way [`crate::HwLiveError`]
/// does, so hw-core needs no extra dependency — and the wording is the daemon's
/// verbatim, because that string reaches the Local API wire.
#[derive(uniffi::Error, Debug)]
pub enum HwStreamError {
    /// The live transcript would exceed the 512 KiB cap.
    LimitExceeded,
}

impl std::fmt::Display for HwStreamError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            HwStreamError::LimitExceeded => {
                write!(f, "Live transcript exceeded the 512 KiB limit")
            }
        }
    }
}

impl std::error::Error for HwStreamError {}

impl From<leaf::AgreementError> for HwStreamError {
    fn from(e: leaf::AgreementError) -> Self {
        match e {
            leaf::AgreementError::LimitExceeded => HwStreamError::LimitExceeded,
        }
    }
}

/// The parakeet daemon's streaming agreement engine: LocalAgreement-3 over the
/// last three retained hypotheses, with a size cap.
///
/// The lifecycle is `new` → `observe`\* → `finish`. There is no `reset`, because
/// the daemon builds one of these per recording and disposes it — mirroring
/// `LiveEngineSession`, which owns the engine for exactly one session.
///
/// Disposal differs by head, and only one head has to act. The Rust side is an
/// `Arc` the platform holds a raw handle to. In **Swift** the generated class
/// frees that handle in its own `deinit`, so ARC is the disposal: releasing the
/// last reference is enough and there is nothing to call. In **C#** the class is
/// `IDisposable` (with a finalizer as a backstop), so a consumer should
/// `Dispose` it to release the handle deterministically rather than whenever the
/// GC gets to it — which is what `LiveEngineSession` wires into its dispose
/// action.
///
/// Thread safety is a plain `Mutex`, not a re-entrant one: the decode loop and
/// the stop path both reach the same instance. No method calls another through
/// the FFI, so there is nothing to re-enter.
#[derive(uniffi::Object)]
pub struct HwBoundedAgreementSession {
    inner: std::sync::Mutex<leaf::BoundedAgreement>,
}

impl HwBoundedAgreementSession {
    /// Take the lock, recovering from a poisoned one.
    ///
    /// The release profile is `panic = "abort"`, so a poisoned mutex cannot
    /// happen in a shipped binary — but `cargo test` unwinds, and a plain
    /// `unwrap()` here would turn one failing assertion into a cascade of
    /// unrelated failures. Nothing reachable from the FFI may `unwrap()`.
    fn locked(&self) -> std::sync::MutexGuard<'_, leaf::BoundedAgreement> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }
}

#[uniffi::export]
impl HwBoundedAgreementSession {
    /// Build an engine that joins words with `join`.
    ///
    /// `join` is the session's only degree of freedom, matching the daemon's
    /// `new BoundedWordAgreement(join)`: `" "` for spaced languages and `""` for
    /// the continuous scripts (`hw_text::is_no_space_language`). Everything else
    /// — three confirmations, eight minimum words, three trailing words, the
    /// 512 KiB cap — is fixed, exactly as it is in C#.
    ///
    /// An exported constructor for the same reason
    /// [`HwWordAgreementSession::new`] is one: without it a consumer can name
    /// the type and never instantiate it.
    #[uniffi::constructor]
    pub fn new(join: String) -> std::sync::Arc<Self> {
        std::sync::Arc::new(Self {
            inner: std::sync::Mutex::new(leaf::BoundedAgreement::new(
                leaf::BoundedConfig::with_join(join),
            )),
        })
    }

    /// One decoded hypothesis. Mirrors C# `Observe`.
    ///
    /// Fails only when the transcript would cross the cap. **The failure is not
    /// atomic**, and deliberately so: the C# original commits word by word and
    /// throws on the first word that would not fit, so words accepted before the
    /// throw stay committed and the hypothesis that triggered it has already been
    /// pushed and trimmed. Read [`HwBoundedAgreementSession::preview`] after a
    /// `LimitExceeded` to see what survived; do not assume the call was a no-op.
    /// The daemon treats the error as fatal to the session, which is why the
    /// partial state is never observed in practice.
    pub fn observe(&self, hypothesis: String) -> Result<HwStreamUpdate, HwStreamError> {
        self.locked()
            .observe(&hypothesis)
            .map(Into::into)
            .map_err(HwStreamError::from)
    }

    /// The final decode. Mirrors C# `Finish`: the unconfirmed tail is committed
    /// with its overlap against the already-committed text removed.
    ///
    /// The cap is checked per word here too, so `LimitExceeded` carries the same
    /// non-atomic guarantee as [`HwBoundedAgreementSession::observe`]: the words
    /// that fit before the throw are already committed.
    pub fn finish(&self, final_hypothesis: String) -> Result<HwStreamUpdate, HwStreamError> {
        self.locked()
            .finish(&final_hypothesis)
            .map(Into::into)
            .map_err(HwStreamError::from)
    }

    /// The committed prefix plus the newest hypothesis.
    ///
    /// **Do not call this on a per-audio-chunk path.** `observe` and `finish`
    /// already return the same string in `HwStreamUpdate::preview`; a caller
    /// that polls per chunk must cache what they returned, which is why
    /// `LiveEngineSession.cs` reads a field rather than an accessor. This exists
    /// for a caller that has no recent update to cache.
    pub fn preview(&self) -> String {
        self.locked().preview()
    }
}

// ===========================================================================
// Tests
// ===========================================================================

#[cfg(test)]
mod tests {
    use super::*;

    fn config() -> HwAgreementConfig {
        HwAgreementConfig {
            token_confirmations_needed: 3,
            min_words_to_confirm: 5,
            min_words_to_confirm_without_punctuation: 8,
            trailing_words_to_hold_without_punctuation: 3,
            min_pass_confidence: 0.15,
            min_word_confidence: 0.6,
        }
    }

    fn words() -> Vec<HwTimedWord> {
        [
            "this", "is", "me", "testing", "out", "to", "make", "sure", "it", "works",
        ]
        .iter()
        .enumerate()
        .map(|(index, text)| HwTimedWord {
            text: (*text).to_string(),
            start_time: index as f64,
            end_time: index as f64 + 0.5,
            confidence: 0.95,
        })
        .collect()
    }

    /// The wrapper keeps state across calls and carries both times back, so the
    /// shipping macOS scenario survives the boundary unchanged.
    #[test]
    fn pairwise_session_commits_on_the_fourth_pass_through_the_ffi() {
        let session = HwWordAgreementSession::new(config());
        for _ in 0..3 {
            assert_eq!(session.observe(words(), 0.95).newly_confirmed_text, "");
        }

        let fourth = session.observe(words(), 0.95);
        assert_eq!(
            fourth.newly_confirmed_text,
            "this is me testing out to make"
        );
        assert_eq!(fourth.confirmed_end_time, 6.5);
        assert_eq!(fourth.hypothesis_start_time, 7.0);
        assert_eq!(session.confirmed_text(), "this is me testing out to make");

        session.reset();
        assert_eq!(session.confirmed_text(), "");
    }

    /// The daemon's engine commits on the third pass, and `preview` agrees with
    /// the `preview` the update already carried.
    #[test]
    fn bounded_session_commits_on_the_third_pass_through_the_ffi() {
        let session = HwBoundedAgreementSession::new(" ".to_string());
        let value = "one two three four five six seven eight nine ten";
        assert_eq!(
            session.observe(value.to_string()).unwrap().committed,
            String::new()
        );
        assert_eq!(
            session.observe(value.to_string()).unwrap().committed,
            String::new()
        );
        let third = session.observe(value.to_string()).unwrap();
        assert_eq!(third.committed, "one two three four five six seven");
        assert_eq!(third.preview, value);
        assert_eq!(session.preview(), value);

        let finished = session
            .finish("six seven eight nine ten eleven".to_string())
            .unwrap();
        assert_eq!(finished.committed, "eight nine ten eleven");
    }

    #[test]
    fn bounded_session_join_is_honoured() {
        let session = HwBoundedAgreementSession::new(String::new());
        assert_eq!(
            session
                .finish("alpha beta gamma".to_string())
                .unwrap()
                .preview,
            "alphabetagamma"
        );
    }

    /// The cap error crosses as `HwStreamError::LimitExceeded`, and its message
    /// is the one the daemon puts on the wire.
    #[test]
    fn bounded_session_reports_the_cap_with_the_daemon_message() {
        let session = HwBoundedAgreementSession::new(" ".to_string());
        let huge = "a".repeat(512 * 1024 + 1);
        // Destructured rather than `unwrap_err()`, which would force a `Debug`
        // derive onto the record for a test's benefit.
        let Err(error) = session.observe(huge) else {
            panic!("a word longer than the cap must be rejected");
        };
        assert!(matches!(error, HwStreamError::LimitExceeded));
        assert_eq!(
            error.to_string(),
            "Live transcript exceeded the 512 KiB limit"
        );
        assert_eq!(session.preview(), "");
    }
}
