//! The sans-I/O HTTP contract shared by every cloud STT provider.
//!
//! Rust builds an [`HttpRequest`] *value* and parses an [`HttpResponse`] *value*;
//! the platform performs the actual network I/O. **Audio is never marshalled
//! across FFI** — a multipart body carries a [`Part::FileRef`] that names a path
//! and field, and the platform streams that file into the request body itself.
//!
//! These are plain Rust types (no `uniffi` dependency). `hw-core` mirrors them as
//! UniFFI records/enums with `From` conversions — the same pattern `hw-text`'s
//! `CursorContext` uses — so the leaf crate stays dependency-light and all
//! `#[uniffi::export]` items land in one place (`hw-core/src/lib.rs`).

/// The 12 cloud speech-to-text providers HyperWhisper integrates.
///
/// `AzureMai` and `GoogleChirp` are *routed* through HyperWhisper Cloud (their
/// requests go to the HW Cloud endpoint with `X-STT-Provider`/`-Model`/`-Domain`
/// headers); the rest talk to their vendor endpoints directly.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Provider {
    HyperWhisperCloud,
    Openai,
    Groq,
    Elevenlabs,
    Mistral,
    Grok,
    Deepgram,
    Soniox,
    Assemblyai,
    Gemini,
    AzureMai,
    GoogleChirp,
}

/// HTTP verb for a built request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HttpMethod {
    Get,
    Post,
    Put,
    Delete,
}

/// A single HTTP header. Headers are an ordered list (not a map) because some
/// providers and multipart assembly are order-sensitive.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Header {
    pub name: String,
    pub value: String,
}

impl Header {
    pub fn new(name: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            value: value.into(),
        }
    }
}

/// The request body the platform must send.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Body {
    /// No body (e.g. a GET poll).
    Empty,
    /// An in-memory body Rust fully assembled (JSON, form-urlencoded, etc.).
    Bytes {
        /// `Content-Type` to set for this body.
        content_type: String,
        data: Vec<u8>,
    },
    /// A `multipart/form-data` body. The platform writes each [`Part`] in order
    /// using `boundary`; for a [`Part::FileRef`] it streams the file from disk
    /// (audio bytes never cross FFI).
    Multipart { boundary: String, parts: Vec<Part> },
    /// A raw (non-multipart) request body whose bytes are the file at `path`,
    /// streamed from disk by the platform with `content_type` as the request's
    /// `Content-Type`. Used by providers that POST the audio file as the bare
    /// request body (e.g. Deepgram's `/v1/listen`, which takes raw audio bytes,
    /// **not** `multipart/form-data`). Audio bytes never cross FFI — only the
    /// path is marshalled, exactly like [`Part::FileRef`].
    FileStream { path: String, content_type: String },
}

/// One part of a multipart body.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Part {
    /// A plain text form field.
    Field { name: String, value: String },
    /// A file the platform streams from `path` under form field `field`.
    FileRef {
        field: String,
        path: String,
        mime: String,
        filename: String,
    },
}

/// A fully-described HTTP request for the platform to execute.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpRequest {
    pub method: HttpMethod,
    pub url: String,
    pub headers: Vec<Header>,
    pub body: Body,
}

/// The platform-captured HTTP response handed back to Rust for parsing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpResponse {
    pub status: u16,
    pub headers: Vec<Header>,
    pub body: Vec<u8>,
}

impl HttpResponse {
    /// First header value matching `name`, case-insensitively.
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|h| h.name.eq_ignore_ascii_case(name))
            .map(|h| h.value.as_str())
    }

    /// Body decoded as UTF-8 (lossy).
    pub fn text(&self) -> std::borrow::Cow<'_, str> {
        String::from_utf8_lossy(&self.body)
    }
}

/// Inputs needed to build a transcription request. A superset POD — each
/// provider reads only the fields it needs (Wave 1 fills the per-provider
/// builders). Optional fields default to `None`/empty.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct TranscribeParams {
    pub api_key: String,
    pub model: String,
    pub language: Option<String>,
    pub vocabulary: Vec<String>,
    pub prompt: Option<String>,
    pub temperature: Option<f64>,
    /// Path to the audio file on disk — referenced, never read across FFI.
    pub audio_path: String,
    /// Audio MIME; when `None` the builder resolves it from `audio_path`.
    pub audio_mime: Option<String>,
    /// Override the provider base URL/endpoint (tests, staging).
    pub base_url: Option<String>,
    // ---- routed / HyperWhisper Cloud ----
    pub license_key: Option<String>,
    pub device_id: Option<String>,
    /// `X-STT-Provider` for routed providers (Azure MAI, Google Chirp).
    pub routed_provider: Option<String>,
    /// `X-STT-Model` for routed providers.
    pub routed_model: Option<String>,
    /// `X-STT-Domain` for routed providers.
    pub routed_domain: Option<String>,
}

/// The parsed result of a successful transcription.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Transcript {
    pub text: String,
    /// Credits/balance remaining after the call (HyperWhisper Cloud).
    pub credits_remaining: Option<f64>,
    /// Cost of this call, when the provider reports it.
    pub cost: Option<f64>,
    /// Provider name echoed back by a routed response, when present.
    pub raw_provider: Option<String>,
}

/// Normalized transcription failures, shared across providers. The HTTP status →
/// variant mapping lives in `classify_error` (WP-B5).
#[derive(thiserror::Error, Debug, Clone, PartialEq, Eq)]
pub enum TranscriptionError {
    /// 401 — bad/expired API key.
    #[error("unauthorized")]
    Unauthorized,
    /// 402 — out of credits / quota.
    #[error("quota exceeded")]
    QuotaExceeded,
    /// 413 — audio file too large for the provider.
    #[error("file too large")]
    FileTooLarge,
    /// 429 — rate limited; `retry_after_secs` from the `Retry-After` header.
    #[error("rate limited")]
    RateLimited { retry_after_secs: Option<u64> },
    /// 5xx — provider down/unavailable.
    #[error("provider unavailable (status {status})")]
    ProviderUnavailable { status: u16 },
    /// 200 but no transcript text (silence).
    #[error("no speech detected")]
    NoSpeech,
    /// 4xx other than the specific cases above.
    #[error("bad request (status {status}): {message}")]
    BadRequest { status: u16, message: String },
    /// Response shape did not match the provider's expected schema.
    #[error("response parse error: {message}")]
    Parse { message: String },
}

/// Whether and when to retry a failed attempt (WP-B5).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RetryDecision {
    /// Wait `delay_ms` then retry.
    Retry { delay_ms: u64 },
    /// Stop — terminal failure or attempts exhausted.
    GiveUp,
}

/// Result of a provider health probe (WP-B5).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderHealth {
    pub provider: Provider,
    pub healthy: bool,
    pub status: Option<u16>,
}
