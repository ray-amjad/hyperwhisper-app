//! Gemini 3.5 Transcribe (sans-I/O) via Google's dedicated speech API,
//! `POST /v1beta/interactions`.
//!
//! ## Why this is a separate module from [`crate::providers::gemini`]
//!
//! **Do not fold this into `gemini.rs`, and do not route this model through
//! `:generateContent`.** `gemini.rs` is the multimodal path: Files API upload +
//! `models/{model}:generateContent`. That endpoint *accepts*
//! `gemini-3.5-transcribe`, *bills the audio*, and then returns a content part
//! with **empty text and no error** — a silent, paid no-op. Verified against the
//! live API. The transcribe models only work through `/v1beta/interactions`.
//!
//! The two also differ in auth (`x-goog-api-key` header + an `Api-Revision`
//! header, not `?key=`), in transport (inline base64, not a file reference), and
//! in BYOK key slot (`geminiTranscribeAPIKey`, not `geminiAPIKey`).
//!
//! ## Wire shape (pre-recorded)
//!
//! ```text
//! POST https://generativelanguage.googleapis.com/v1beta/interactions
//! x-goog-api-key: <key>
//! Api-Revision: 2026-05-20
//! Content-Type: application/json
//!
//! {"model":"gemini-3.5-transcribe",
//!  "input":[{"type":"audio","mime_type":"audio/mp3","data":"<base64>"}],
//!  "generation_config":{"transcription_config":{
//!     "language_codes":["en-US"],
//!     "custom_vocabulary":["HyperWhisper"]}}}
//! ```
//!
//! Transcript at `steps[0].content[0].text`; optional word timings at
//! `steps[0].content[0].annotations[]` (`"type":"word_info"`).
//!
//! ## Inline base64 and the FFI contract
//!
//! The endpoint has no file-reference form, so the audio must be inline. Rust
//! still never touches the bytes: the builder emits
//! [`Body::JsonWithBase64File`], which names the path and the two literal JSON
//! fragments that surround the base64. The platform reads and encodes the file
//! while writing the request body.
//!
//! ## The mutual-exclusion rule (TRAP 2)
//!
//! `custom_vocabulary` is rejected with HTTP 400 when it is sent alongside
//! either `diarization_mode` ("incompatible with diarization") or
//! `timestamp_granularities` ("incompatible with timestamps"). HyperWhisper is a
//! dictation app, so **vocabulary wins**. The rule is enforced in exactly one
//! place, [`transcription_config`], which every builder in this module (REST and
//! live) goes through — never at a call site.
//!
//! ## Live (`gemini-3.5-transcribe-live`)
//!
//! The live model speaks `BidiGenerateContent` over a WebSocket, and its
//! transcription config lives at a **different path**:
//! `setup.input_audio_transcription`, *not*
//! `setup.generation_config.transcription_config`. Sending the pre-recorded
//! shape closes the socket with 1007. The frame builders here are pure Rust and
//! are deliberately **not** exposed over UniFFI (no platform streaming strategy
//! builds frames across FFI, and per-chunk audio marshalling would add a hot-path
//! copy); `shared-conformance/live-frame-vectors.json` pins their shape so the
//! native clients can be checked against the same answers.

#[cfg(doc)]
use crate::contract::Body;
use crate::helpers::keyword_boost_terms;

mod base64;
mod batch;
mod live;

pub use base64::*;
pub use batch::*;
pub use live::*;

/// Gemini API root. `params.base_url` overrides it (tests/staging).
pub const API_ROOT: &str = "https://generativelanguage.googleapis.com";

/// The `Api-Revision` header value the `/v1beta/interactions` shape is pinned
/// to. Google versions this endpoint by date; omitting it selects whatever the
/// current default revision is, which is not a stable contract.
pub const API_REVISION: &str = "2026-05-20";

/// The `cloud-stt-catalog.json` entry this provider's models live under.
pub const CATALOG_ENTRY_ID: &str = "geminiTranscribe";

/// Default model when the caller leaves `params.model` empty. Read from the
/// shared catalog, which macOS and Windows read too — see
/// [`super::defaults`] and issue #580.
pub fn default_model() -> &'static str {
    super::defaults::default_model(CATALOG_ENTRY_ID)
}

/// The live (WebSocket) model id. It is **not** routable through
/// [`build_transcribe_request`] — see [`is_live_model`].
pub const LIVE_MODEL: &str = "gemini-3.5-transcribe-live";

/// MIME the live socket expects for its audio frames.
pub const LIVE_AUDIO_MIME: &str = "audio/pcm;rate=16000";

/// The WebSocket endpoint for `BidiGenerateContent`.
pub const LIVE_WS_ROOT: &str = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

/// Maximum `custom_vocabulary` terms sent on either path (pre-recorded or live).
///
/// This is the number the rest of the feature already agrees on:
/// `customVocabulary.maxPhrases` in `shared-app-classification/cloud-stt-catalog.json`,
/// `MAX_VOCABULARY_TERMS` in `hyperwhisper-cloud/src/providers/gemini-transcribe.ts`,
/// `maxVocabularyTerms` in the macOS `GeminiStreamingStrategy` and
/// `MaxVocabularyTerms` in the Windows one. The BYOK path was the only one
/// sending the list uncapped, which made the catalog's own caveat ("both forward
/// it, capped at 100 terms of 80 chars") false for exactly the path it names.
///
/// The per-term 80-character cap is the other half, and comes for free:
/// [`keyword_boost_terms`] truncates every term through
/// `hw_text::sanitize_vocabulary_word`.
pub const MAX_VOCABULARY_TERMS: usize = 100;

/// Whether `model` names the live (WebSocket) variant. The REST builder refuses
/// it: `/v1beta/interactions` does not serve the live model, and letting it
/// through would produce a routable-but-unserved model id.
pub fn is_live_model(model: &str) -> bool {
    model.trim().eq_ignore_ascii_case(LIVE_MODEL)
}

/// The `language_codes` array, or `None` for auto-detect.
///
/// An empty or `auto` language means auto-detect, which the API expresses by
/// **omitting** the field (an empty array is also accepted, but omitting keeps
/// the body minimal and matches the verified request).
fn language_codes(language: Option<&str>) -> Option<Vec<String>> {
    language
        .map(str::trim)
        .filter(|t| !t.is_empty() && !t.eq_ignore_ascii_case("auto"))
        .map(|t| vec![t.to_string()])
}

/// Optional extras a caller may *request* from [`transcription_config`]. Both
/// are dropped when vocabulary terms are present (TRAP 2).
///
/// `TranscribeParams` carries no diarization or timestamp fields and this module
/// deliberately does not add any — the dictation paths always pass
/// [`TranscriptionExtras::none`]. The parameters exist so the exclusion rule has
/// a single, directly testable home rather than being an unwritten convention.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TranscriptionExtras {
    /// `generation_config.transcription_config.diarization_mode`, e.g.
    /// `"speaker"`.
    pub diarization_mode: Option<String>,
    /// `generation_config.transcription_config.timestamp_granularities`, e.g.
    /// `["word"]`.
    pub timestamp_granularities: Vec<String>,
}

impl TranscriptionExtras {
    /// No diarization, no timestamps — what every HyperWhisper call site uses.
    pub fn none() -> Self {
        Self::default()
    }

    fn is_empty(&self) -> bool {
        self.diarization_mode.is_none() && self.timestamp_granularities.is_empty()
    }
}

/// Build the `transcription_config` object shared by the REST body and the live
/// setup frame.
///
/// **This is the single enforcement point for TRAP 2.** Google rejects
/// `custom_vocabulary` with HTTP 400 if it arrives together with
/// `diarization_mode` ("incompatible with diarization") or
/// `timestamp_granularities` ("incompatible with timestamps"). Rather than
/// letting each call site remember that, this function resolves the conflict
/// itself: when vocabulary terms survive normalization, they are sent and the
/// extras are dropped. A dictation app gets far more from correct spellings of
/// the user's own jargon than from speaker labels or word offsets.
///
/// Vocabulary is normalized through [`keyword_boost_terms`] and capped at
/// [`MAX_VOCABULARY_TERMS`], the same limit the Cloud adapter and the three
/// native live strategies apply.
///
/// Returns a `serde_json::Value` object, possibly empty (`{}`) when nothing is
/// configured.
pub fn transcription_config(
    language: Option<&str>,
    vocabulary: &[String],
    extras: &TranscriptionExtras,
) -> serde_json::Value {
    let mut config = serde_json::Map::new();

    if let Some(codes) = language_codes(language) {
        config.insert("language_codes".to_string(), serde_json::json!(codes));
    }

    let terms = keyword_boost_terms(vocabulary, Some(MAX_VOCABULARY_TERMS));
    if !terms.is_empty() {
        // TRAP 2: vocabulary wins. `extras` is discarded, not merged.
        config.insert("custom_vocabulary".to_string(), serde_json::json!(terms));
        return serde_json::Value::Object(config);
    }

    if !extras.is_empty() {
        if let Some(mode) = extras.diarization_mode.as_deref().map(str::trim) {
            if !mode.is_empty() {
                config.insert("diarization_mode".to_string(), serde_json::json!(mode));
            }
        }
        if !extras.timestamp_granularities.is_empty() {
            config.insert(
                "timestamp_granularities".to_string(),
                serde_json::json!(extras.timestamp_granularities),
            );
        }
    }

    serde_json::Value::Object(config)
}

#[cfg(test)]
mod live_tests;
#[cfg(test)]
mod response_tests;
#[cfg(test)]
mod tests;
