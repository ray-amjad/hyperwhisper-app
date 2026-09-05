//! UniFFI surface for the M2 sans-I/O networking core (`hw_net`).
//!
//! Mirrors the HTTP contract types as UniFFI records/enums (bidirectional `From`
//! conversions), then exposes every provider's `build_*`/`parse_*` step plus the
//! retry policy and health probes as thin `#[uniffi::export]` wrappers.
//!
//! Audio bytes never cross FFI — a `Body::FileStream`/`HwPart::FileRef` only names
//! a path the platform streams.
use hw_net::contract as c;

// ===========================================================================
// Contract types
// ===========================================================================

/// The 15 cloud speech-to-text providers. Mirrors `hw_net::Provider`.
#[derive(uniffi::Enum)]
pub enum HwProvider {
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
    GeminiTranscribe,
    GeminiTranscribeLive,
    Meta,
}

impl From<HwProvider> for c::Provider {
    fn from(p: HwProvider) -> Self {
        match p {
            HwProvider::HyperWhisperCloud => c::Provider::HyperWhisperCloud,
            HwProvider::Openai => c::Provider::Openai,
            HwProvider::Groq => c::Provider::Groq,
            HwProvider::Elevenlabs => c::Provider::Elevenlabs,
            HwProvider::Mistral => c::Provider::Mistral,
            HwProvider::Grok => c::Provider::Grok,
            HwProvider::Deepgram => c::Provider::Deepgram,
            HwProvider::Soniox => c::Provider::Soniox,
            HwProvider::Assemblyai => c::Provider::Assemblyai,
            HwProvider::Gemini => c::Provider::Gemini,
            HwProvider::AzureMai => c::Provider::AzureMai,
            HwProvider::GoogleChirp => c::Provider::GoogleChirp,
            HwProvider::GeminiTranscribe => c::Provider::GeminiTranscribe,
            HwProvider::GeminiTranscribeLive => c::Provider::GeminiTranscribeLive,
            HwProvider::Meta => c::Provider::Meta,
        }
    }
}

impl From<c::Provider> for HwProvider {
    fn from(p: c::Provider) -> Self {
        match p {
            c::Provider::HyperWhisperCloud => HwProvider::HyperWhisperCloud,
            c::Provider::Openai => HwProvider::Openai,
            c::Provider::Groq => HwProvider::Groq,
            c::Provider::Elevenlabs => HwProvider::Elevenlabs,
            c::Provider::Mistral => HwProvider::Mistral,
            c::Provider::Grok => HwProvider::Grok,
            c::Provider::Deepgram => HwProvider::Deepgram,
            c::Provider::Soniox => HwProvider::Soniox,
            c::Provider::Assemblyai => HwProvider::Assemblyai,
            c::Provider::Gemini => HwProvider::Gemini,
            c::Provider::AzureMai => HwProvider::AzureMai,
            c::Provider::GoogleChirp => HwProvider::GoogleChirp,
            c::Provider::GeminiTranscribe => HwProvider::GeminiTranscribe,
            c::Provider::GeminiTranscribeLive => HwProvider::GeminiTranscribeLive,
            c::Provider::Meta => HwProvider::Meta,
        }
    }
}

/// HTTP verb. Mirrors `hw_net::HttpMethod`.
#[derive(uniffi::Enum)]
pub enum HttpMethod {
    Get,
    Post,
    Put,
    Delete,
}

impl From<HttpMethod> for c::HttpMethod {
    fn from(m: HttpMethod) -> Self {
        match m {
            HttpMethod::Get => c::HttpMethod::Get,
            HttpMethod::Post => c::HttpMethod::Post,
            HttpMethod::Put => c::HttpMethod::Put,
            HttpMethod::Delete => c::HttpMethod::Delete,
        }
    }
}

impl From<c::HttpMethod> for HttpMethod {
    fn from(m: c::HttpMethod) -> Self {
        match m {
            c::HttpMethod::Get => HttpMethod::Get,
            c::HttpMethod::Post => HttpMethod::Post,
            c::HttpMethod::Put => HttpMethod::Put,
            c::HttpMethod::Delete => HttpMethod::Delete,
        }
    }
}

/// A single HTTP header. Mirrors `hw_net::Header`.
#[derive(uniffi::Record)]
pub struct Header {
    pub name: String,
    pub value: String,
}

impl From<Header> for c::Header {
    fn from(h: Header) -> Self {
        c::Header {
            name: h.name,
            value: h.value,
        }
    }
}

impl From<c::Header> for Header {
    fn from(h: c::Header) -> Self {
        Header {
            name: h.name,
            value: h.value,
        }
    }
}

/// One part of a multipart body. Mirrors `hw_net::Part`.
#[derive(uniffi::Enum)]
pub enum HwPart {
    Field {
        name: String,
        value: String,
    },
    FileRef {
        field: String,
        path: String,
        mime: String,
        filename: String,
    },
    InlineFile {
        field: String,
        filename: String,
        mime: String,
        data: Vec<u8>,
    },
}

impl From<c::Part> for HwPart {
    fn from(p: c::Part) -> Self {
        match p {
            c::Part::Field { name, value } => HwPart::Field { name, value },
            c::Part::FileRef {
                field,
                path,
                mime,
                filename,
            } => HwPart::FileRef {
                field,
                path,
                mime,
                filename,
            },
            c::Part::InlineFile {
                field,
                filename,
                mime,
                data,
            } => HwPart::InlineFile {
                field,
                filename,
                mime,
                data,
            },
        }
    }
}

impl From<HwPart> for c::Part {
    fn from(p: HwPart) -> Self {
        match p {
            HwPart::Field { name, value } => c::Part::Field { name, value },
            HwPart::FileRef {
                field,
                path,
                mime,
                filename,
            } => c::Part::FileRef {
                field,
                path,
                mime,
                filename,
            },
            HwPart::InlineFile {
                field,
                filename,
                mime,
                data,
            } => c::Part::InlineFile {
                field,
                filename,
                mime,
                data,
            },
        }
    }
}

/// The request body the platform must send. Mirrors `hw_net::Body`.
#[derive(uniffi::Enum)]
pub enum Body {
    Empty,
    Bytes {
        content_type: String,
        data: Vec<u8>,
    },
    Multipart {
        boundary: String,
        parts: Vec<HwPart>,
    },
    FileStream {
        path: String,
        content_type: String,
    },
    /// `prefix` ++ base64(bytes of the file at `path`) ++ `suffix`, written by
    /// the platform with `Content-Type: application/json`. Rust never sees the
    /// audio — only the path — but unlike `FileStream` the platform must
    /// base64-encode as it writes (standard alphabet, padded, no line breaks).
    ///
    /// Used only by Gemini 3.5 Transcribe's `/v1beta/interactions`, which has no
    /// file-reference form.
    JsonWithBase64File {
        prefix: Vec<u8>,
        path: String,
        suffix: Vec<u8>,
    },
}

impl From<c::Body> for Body {
    fn from(b: c::Body) -> Self {
        match b {
            c::Body::Empty => Body::Empty,
            c::Body::Bytes { content_type, data } => Body::Bytes { content_type, data },
            c::Body::Multipart { boundary, parts } => Body::Multipart {
                boundary,
                parts: parts.into_iter().map(Into::into).collect(),
            },
            c::Body::FileStream { path, content_type } => Body::FileStream { path, content_type },
            c::Body::JsonWithBase64File {
                prefix,
                path,
                suffix,
            } => Body::JsonWithBase64File {
                prefix,
                path,
                suffix,
            },
        }
    }
}

impl From<Body> for c::Body {
    fn from(b: Body) -> Self {
        match b {
            Body::Empty => c::Body::Empty,
            Body::Bytes { content_type, data } => c::Body::Bytes { content_type, data },
            Body::Multipart { boundary, parts } => c::Body::Multipart {
                boundary,
                parts: parts.into_iter().map(Into::into).collect(),
            },
            Body::FileStream { path, content_type } => c::Body::FileStream { path, content_type },
            Body::JsonWithBase64File {
                prefix,
                path,
                suffix,
            } => c::Body::JsonWithBase64File {
                prefix,
                path,
                suffix,
            },
        }
    }
}

/// A fully-described HTTP request for the platform to execute.
/// Mirrors `hw_net::HttpRequest`.
#[derive(uniffi::Record)]
pub struct HttpRequest {
    pub method: HttpMethod,
    pub url: String,
    pub headers: Vec<Header>,
    pub body: Body,
}

impl From<c::HttpRequest> for HttpRequest {
    fn from(r: c::HttpRequest) -> Self {
        HttpRequest {
            method: r.method.into(),
            url: r.url,
            headers: r.headers.into_iter().map(Into::into).collect(),
            body: r.body.into(),
        }
    }
}

impl From<HttpRequest> for c::HttpRequest {
    fn from(r: HttpRequest) -> Self {
        c::HttpRequest {
            method: r.method.into(),
            url: r.url,
            headers: r.headers.into_iter().map(Into::into).collect(),
            body: r.body.into(),
        }
    }
}

/// The platform-captured HTTP response handed back to Rust for parsing.
/// Mirrors `hw_net::HttpResponse`.
#[derive(uniffi::Record)]
pub struct HttpResponse {
    pub status: u16,
    pub headers: Vec<Header>,
    pub body: Vec<u8>,
}

impl From<HttpResponse> for c::HttpResponse {
    fn from(r: HttpResponse) -> Self {
        c::HttpResponse {
            status: r.status,
            headers: r.headers.into_iter().map(Into::into).collect(),
            body: r.body,
        }
    }
}

impl From<c::HttpResponse> for HttpResponse {
    fn from(r: c::HttpResponse) -> Self {
        HttpResponse {
            status: r.status,
            headers: r.headers.into_iter().map(Into::into).collect(),
            body: r.body,
        }
    }
}

/// Inputs needed to build a transcription request. Mirrors
/// `hw_net::TranscribeParams` field-for-field.
#[derive(uniffi::Record)]
pub struct TranscribeParams {
    pub api_key: String,
    pub model: String,
    pub language: Option<String>,
    pub vocabulary: Vec<String>,
    pub prompt: Option<String>,
    pub temperature: Option<f64>,
    pub audio_path: String,
    pub audio_mime: Option<String>,
    pub base_url: Option<String>,
    pub license_key: Option<String>,
    pub device_id: Option<String>,
    pub routed_provider: Option<String>,
    pub routed_model: Option<String>,
    pub routed_domain: Option<String>,
    /// `true` = the user shares anonymous latency data, and no opt-out header
    /// is sent. Deliberately **required** (no `#[uniffi(default)]`): a host that
    /// forgets it must fail to compile rather than silently keep sharing on.
    pub share_anonymous_speed_data: bool,
}

impl From<TranscribeParams> for c::TranscribeParams {
    fn from(p: TranscribeParams) -> Self {
        c::TranscribeParams {
            api_key: p.api_key,
            model: p.model,
            language: p.language,
            vocabulary: p.vocabulary,
            prompt: p.prompt,
            temperature: p.temperature,
            audio_path: p.audio_path,
            audio_mime: p.audio_mime,
            base_url: p.base_url,
            license_key: p.license_key,
            device_id: p.device_id,
            routed_provider: p.routed_provider,
            routed_model: p.routed_model,
            routed_domain: p.routed_domain,
            share_anonymous_speed_data: p.share_anonymous_speed_data,
        }
    }
}

/// The parsed result of a successful transcription. Mirrors `hw_net::Transcript`.
#[derive(uniffi::Record)]
pub struct HwTranscript {
    pub text: String,
    pub credits_remaining: Option<f64>,
    pub cost: Option<f64>,
    pub raw_provider: Option<String>,
}

impl From<c::Transcript> for HwTranscript {
    fn from(t: c::Transcript) -> Self {
        HwTranscript {
            text: t.text,
            credits_remaining: t.credits_remaining,
            cost: t.cost,
            raw_provider: t.raw_provider,
        }
    }
}

/// Normalized transcription failures. Mirrors `hw_net::TranscriptionError` as a
/// UniFFI error enum. `Display` is implemented by hand (matching the leaf's
/// `thiserror` messages) so hw-core needs no extra dependency.
#[derive(uniffi::Error, Debug)]
pub enum HwTranscriptionError {
    Unauthorized,
    QuotaExceeded,
    FileTooLarge,
    RateLimited { retry_after_secs: Option<u64> },
    ProviderUnavailable { status: u16 },
    NoSpeech,
    BadRequest { status: u16, message: String },
    Parse { message: String },
}

impl std::fmt::Display for HwTranscriptionError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            HwTranscriptionError::Unauthorized => write!(f, "unauthorized"),
            HwTranscriptionError::QuotaExceeded => write!(f, "quota exceeded"),
            HwTranscriptionError::FileTooLarge => write!(f, "file too large"),
            HwTranscriptionError::RateLimited { .. } => write!(f, "rate limited"),
            HwTranscriptionError::ProviderUnavailable { status } => {
                write!(f, "provider unavailable (status {status})")
            }
            HwTranscriptionError::NoSpeech => write!(f, "no speech detected"),
            HwTranscriptionError::BadRequest { status, message } => {
                write!(f, "bad request (status {status}): {message}")
            }
            HwTranscriptionError::Parse { message } => {
                write!(f, "response parse error: {message}")
            }
        }
    }
}

impl std::error::Error for HwTranscriptionError {}

impl From<c::TranscriptionError> for HwTranscriptionError {
    fn from(e: c::TranscriptionError) -> Self {
        match e {
            c::TranscriptionError::Unauthorized => HwTranscriptionError::Unauthorized,
            c::TranscriptionError::QuotaExceeded => HwTranscriptionError::QuotaExceeded,
            c::TranscriptionError::FileTooLarge => HwTranscriptionError::FileTooLarge,
            c::TranscriptionError::RateLimited { retry_after_secs } => {
                HwTranscriptionError::RateLimited { retry_after_secs }
            }
            c::TranscriptionError::ProviderUnavailable { status } => {
                HwTranscriptionError::ProviderUnavailable { status }
            }
            c::TranscriptionError::NoSpeech => HwTranscriptionError::NoSpeech,
            c::TranscriptionError::BadRequest { status, message } => {
                HwTranscriptionError::BadRequest { status, message }
            }
            c::TranscriptionError::Parse { message } => HwTranscriptionError::Parse { message },
        }
    }
}

/// Whether and when to retry a failed attempt. Mirrors `hw_net::RetryDecision`.
#[derive(uniffi::Enum)]
pub enum RetryDecision {
    Retry { delay_ms: u64 },
    GiveUp,
}

impl From<c::RetryDecision> for RetryDecision {
    fn from(d: c::RetryDecision) -> Self {
        match d {
            c::RetryDecision::Retry { delay_ms } => RetryDecision::Retry { delay_ms },
            c::RetryDecision::GiveUp => RetryDecision::GiveUp,
        }
    }
}

/// Result of a provider health probe. Mirrors `hw_net::ProviderHealth`.
#[derive(uniffi::Record)]
pub struct HwProviderHealth {
    pub provider: HwProvider,
    pub healthy: bool,
    pub status: Option<u16>,
}

impl From<c::ProviderHealth> for HwProviderHealth {
    fn from(h: c::ProviderHealth) -> Self {
        HwProviderHealth {
            provider: h.provider.into(),
            healthy: h.healthy,
            status: h.status,
        }
    }
}

mod policy;
mod provider_types;
mod providers;

#[allow(unused_imports)]
pub use policy::*;
pub use provider_types::*;
#[allow(unused_imports)]
pub use providers::*;

/// Tests for the FFI bridge itself, not for `hw-net`'s logic (the leaf crate
/// covers that). The tests pin each conversion to an observable difference and
/// exercise it through the `#[uniffi::export]` functions the platforms call.
#[cfg(test)]
mod tests;
