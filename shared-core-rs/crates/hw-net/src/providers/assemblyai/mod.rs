//! AssemblyAI transcription (sans-I/O). Two products live under this one
//! module:
//!
//! - [`async_flow`] — the original **three-step async workflow**:
//!   1. **Upload** the raw audio bytes → `{ "upload_url": "..." }`.
//!   2. **Create** a transcript job from that URL → `{ "id": "...", "status": "queued" }`.
//!   3. **Poll** `GET /transcript/{id}` until `status == "completed"` (or `"error"`).
//!
//!   The platform drives the loop (sleeping between polls — no clock/RNG in Rust);
//!   Rust builds each step's [`HttpRequest`] and parses each step's [`HttpResponse`].
//!   Audio bytes never cross FFI — the upload body is a [`Body::FileStream`] the
//!   platform streams from disk.
//!
//! - [`sync_flow`] — the **sync fast path** for short clips (<120s): one
//!   blocking multipart request/response, no upload/create/poll. See that
//!   module's doc comment for the verified contract.
//!
//! Both submodules are re-exported here (`pub use async_flow::*; pub use
//! sync_flow::*;`) so the public API — `assemblyai::build_upload_request`,
//! `assemblyai::build_sync_request`, etc. — and every FFI wrapper in
//! `hw-core/src/ffi_net.rs` are unaffected by this file's split into a module
//! directory (previously one 1139-line file).
//!
//! ## Parity references
//! - macOS `AssemblyAIProvider.swift` (`uploadFile` / `startTranscript` /
//!   `waitForTranscript` / `tryTranscribeSync`)
//! - Windows `AssemblyAIService.cs` (`UploadAudioAsync` / `CreateTranscriptAsync` /
//!   `PollTranscriptAsync` / `TryTranscribeSyncAsync`)
//!
//! ## Endpoints
//! - Upload:  `POST https://api.assemblyai.com/v2/upload`
//! - Create:  `POST https://api.assemblyai.com/v2/transcript`
//! - Poll:    `GET  https://api.assemblyai.com/v2/transcript/{id}`
//! - Sync:    `POST https://sync.assemblyai.com/v1/transcribe`
//!
//! ## Auth
//! AssemblyAI uses a bare `Authorization: <key>` header — **no `Bearer` prefix**
//! (both reference impls set the key directly). See [`auth_header`].
//!
//! ## Parity notes / unification choices
//! - **`speech_model` vs `speech_models`**: macOS sends `speech_models` as a
//!   one-element **array**; Windows sends `speech_model` as a **string**. We
//!   follow macOS (the verified platform) and send `speech_models: [model]`.
//!   AssemblyAI accepts both; this is a documented divergence from Windows.
//! - **Model default / aliases**: empty model → `universal-2`. Legacy IDs
//!   `universal` → `universal-2`, `slam-1` → `universal-3-pro` (both platforms).
//!   A trailing `-medical` suffix is stripped and surfaces as
//!   `domain: "medical-v1"` (Medical Mode add-on).
//! - **Vocabulary** (`keyterms_prompt`): trimmed, drop empties, drop phrases
//!   with > 6 words, capped at 1000 for `universal-3-pro` else 200. (`word_boost`
//!   is deprecated; both platforms moved to `keyterms_prompt`.)
//! - **Poll status mapping**: `completed` → text (empty text → `NoSpeech`);
//!   `error` → `BadRequest`; `queued`/`processing`/unknown →
//!   [`PollOutcome::Pending`] so the platform keeps polling.

use crate::contract::{Header, TranscribeParams};

mod async_flow;
mod sync_flow;

pub use async_flow::*;
pub use sync_flow::*;

/// AssemblyAI API base. `params.base_url` overrides it (tests/staging).
pub const BASE_URL: &str = "https://api.assemblyai.com/v2";

/// Default model when the caller leaves `params.model` empty.
/// PARITY: macOS `defaultModel(for: .assemblyAI)` / Windows default = `universal-2`.
pub const DEFAULT_MODEL: &str = "universal-2";

/// Max `keyterms_prompt` terms for `universal-3-pro` (else [`MAX_KEYTERMS_DEFAULT`]).
pub const MAX_KEYTERMS_PRO: usize = 1000;
/// Max `keyterms_prompt` terms for non-pro models.
pub const MAX_KEYTERMS_DEFAULT: usize = 200;
/// Max words per `keyterms_prompt` phrase (AssemblyAI spec). Shared by both the
/// async create-request term cap ([`async_flow`]) and the sync fast path's
/// char-budget cap ([`sync_flow`]) — sync must drop the same over-long phrases
/// async silently drops, not just cap by total characters.
pub const MAX_KEYTERM_WORDS: usize = 6;

/// `Authorization: <key>` — AssemblyAI uses the bare key, **no `Bearer`**.
/// Shared by the async and sync request builders.
fn auth_header(api_key: &str) -> Header {
    Header::new("Authorization", api_key.to_string())
}

/// Resolve a legacy AssemblyAI model alias to its current ID.
/// PARITY: macOS `legacyAssemblyAIAliases` / Windows `LegacyAssemblyAIAliases`.
pub fn resolve_model_alias(id: &str) -> &str {
    match id {
        "universal" => "universal-2",
        "slam-1" => "universal-3-pro",
        other => other,
    }
}

/// Split a (possibly `-medical`) model ID into `(speech_model, medical)`.
/// PARITY: macOS `assemblyAIRequestParams(for:)` / Windows `GetAssemblyAIRequestParams`.
pub fn request_params(id: &str) -> (String, bool) {
    let resolved = resolve_model_alias(id);
    if let Some(stripped) = resolved.strip_suffix("-medical") {
        (stripped.to_string(), true)
    } else {
        (resolved.to_string(), false)
    }
}

/// Resolve the audio MIME for `params` (used only when a caller wants it; the
/// async upload itself always sends `application/octet-stream` per parity —
/// see `async_flow::build_upload_request`).
pub fn audio_mime(params: &TranscribeParams) -> String {
    params
        .audio_mime
        .clone()
        .unwrap_or_else(|| crate::helpers::resolve_mime(&params.audio_path))
}

/// Test-only fixtures shared by [`async_flow`]'s and [`sync_flow`]'s own
/// `#[cfg(test)]` modules, so each submodule's tests stay self-contained
/// without duplicating the `TranscribeParams` fixture.
#[cfg(test)]
mod test_support {
    use super::*;

    /// `pub(super)`: visible to `assemblyai` and all its descendants
    /// (`async_flow::tests`, `sync_flow::tests`) but not outside the module.
    pub(super) fn params() -> TranscribeParams {
        TranscribeParams {
            api_key: "aai-key".to_string(),
            model: "universal-2".to_string(),
            audio_path: "/tmp/rec.m4a".to_string(),
            ..Default::default()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ---- aliases / medical -------------------------------------------------

    #[test]
    fn resolves_legacy_aliases() {
        assert_eq!(resolve_model_alias("universal"), "universal-2");
        assert_eq!(resolve_model_alias("slam-1"), "universal-3-pro");
        assert_eq!(resolve_model_alias("universal-3-pro"), "universal-3-pro");
    }

    #[test]
    fn request_params_strips_medical_suffix() {
        assert_eq!(request_params("universal-2"), ("universal-2".into(), false));
        assert_eq!(
            request_params("universal-2-medical"),
            ("universal-2".into(), true)
        );
        // alias then medical strip
        assert_eq!(request_params("slam-1"), ("universal-3-pro".into(), false));
    }
}
