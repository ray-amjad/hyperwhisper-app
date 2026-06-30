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

/// The 12 cloud speech-to-text providers. Mirrors `hw_net::Provider`.
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

// ===========================================================================
// Multi-step provider intermediate types
// ===========================================================================

/// AssemblyAI poll outcome. Mirrors `assemblyai::PollOutcome` (tuple variant
/// `Done(HwTranscript)` flattened to a named field for UniFFI).
#[derive(uniffi::Enum)]
pub enum AssemblyaiPollOutcome {
    Pending,
    Done { transcript: HwTranscript },
}

impl From<hw_net::providers::assemblyai::PollOutcome> for AssemblyaiPollOutcome {
    fn from(o: hw_net::providers::assemblyai::PollOutcome) -> Self {
        match o {
            hw_net::providers::assemblyai::PollOutcome::Pending => AssemblyaiPollOutcome::Pending,
            hw_net::providers::assemblyai::PollOutcome::Done(t) => AssemblyaiPollOutcome::Done {
                transcript: t.into(),
            },
        }
    }
}

/// A Gemini file resource. Mirrors `gemini::GeminiFile`.
#[derive(uniffi::Record)]
pub struct GeminiFile {
    pub name: Option<String>,
    pub uri: Option<String>,
    pub mime_type: Option<String>,
    pub state: Option<String>,
}

impl From<hw_net::providers::gemini::GeminiFile> for GeminiFile {
    fn from(f: hw_net::providers::gemini::GeminiFile) -> Self {
        GeminiFile {
            name: f.name,
            uri: f.uri,
            mime_type: f.mime_type,
            state: f.state,
        }
    }
}

impl From<GeminiFile> for hw_net::providers::gemini::GeminiFile {
    fn from(f: GeminiFile) -> Self {
        hw_net::providers::gemini::GeminiFile {
            name: f.name,
            uri: f.uri,
            mime_type: f.mime_type,
            state: f.state,
        }
    }
}

/// Gemini file-poll outcome. Mirrors `gemini::FilePollOutcome`.
#[derive(uniffi::Enum)]
pub enum GeminiFilePollOutcome {
    Pending,
    Active { file: GeminiFile },
}

impl From<hw_net::providers::gemini::FilePollOutcome> for GeminiFilePollOutcome {
    fn from(o: hw_net::providers::gemini::FilePollOutcome) -> Self {
        match o {
            hw_net::providers::gemini::FilePollOutcome::Pending => GeminiFilePollOutcome::Pending,
            hw_net::providers::gemini::FilePollOutcome::Active(f) => {
                GeminiFilePollOutcome::Active { file: f.into() }
            }
        }
    }
}

/// Soniox transcription job status. Mirrors `soniox::PollStatus`.
#[derive(uniffi::Enum)]
pub enum SonioxPollStatus {
    Pending,
    Completed,
}

impl From<hw_net::providers::soniox::PollStatus> for SonioxPollStatus {
    fn from(s: hw_net::providers::soniox::PollStatus) -> Self {
        match s {
            hw_net::providers::soniox::PollStatus::Pending => SonioxPollStatus::Pending,
            hw_net::providers::soniox::PollStatus::Completed => SonioxPollStatus::Completed,
        }
    }
}

// ===========================================================================
// retry
// ===========================================================================

/// Total transcription attempts before giving up.
#[uniffi::export]
pub fn retry_max_attempts() -> u32 {
    hw_net::retry::MAX_ATTEMPTS
}

/// Upper bound, in seconds, on a single honored `Retry-After` sleep.
#[uniffi::export]
pub fn retry_max_retry_after_secs() -> u64 {
    hw_net::retry::MAX_RETRY_AFTER_SECS
}

/// Map an HTTP status + response body to a `HwTranscriptionError`.
#[uniffi::export]
pub fn classify_error(status: u16, body: String) -> HwTranscriptionError {
    hw_net::retry::classify_error(status, &body).into()
}

/// Whether a classified error should be retried.
#[uniffi::export]
pub fn is_retryable(status: u16, body: String) -> bool {
    let err = hw_net::retry::classify_error(status, &body);
    hw_net::retry::is_retryable(&err)
}

/// Decide whether to retry, given the raw HTTP `status` + response `body`.
#[uniffi::export]
pub fn next_retry(
    attempt: u32,
    status: u16,
    body: String,
    retry_after: Option<u64>,
) -> RetryDecision {
    hw_net::retry::next_retry(attempt, status, &body, retry_after).into()
}

// ===========================================================================
// health
// ===========================================================================

/// Default HyperWhisper Cloud health URL for routed providers.
#[uniffi::export]
pub fn hw_cloud_health_default() -> String {
    hw_net::health::HW_CLOUD_HEALTH_DEFAULT.to_string()
}

/// Build a lightweight health-check request for `provider`.
#[uniffi::export]
pub fn build_health_request(provider: HwProvider, api_key: String) -> HttpRequest {
    hw_net::health::build_health_request(provider.into(), &api_key).into()
}

/// Like `build_health_request` but with an explicit HW Cloud base URL.
#[uniffi::export]
pub fn build_health_request_with_base(
    provider: HwProvider,
    api_key: String,
    base_url: Option<String>,
) -> HttpRequest {
    hw_net::health::build_health_request_with_base(provider.into(), &api_key, base_url.as_deref())
        .into()
}

/// Parse a health-check response into a verdict.
#[uniffi::export]
pub fn parse_health_response(provider: HwProvider, resp: HttpResponse) -> HwProviderHealth {
    hw_net::health::parse_health_response(provider.into(), &resp.into()).into()
}

// ===========================================================================
// Single-shot providers: build_transcribe_request / parse_transcribe_response
// ===========================================================================

macro_rules! single_shot {
    ($build:ident, $parse:ident, $module:path) => {
        #[uniffi::export]
        pub fn $build(params: TranscribeParams) -> Result<HttpRequest, HwTranscriptionError> {
            use $module as m;
            m::build_transcribe_request(&params.into())
                .map(Into::into)
                .map_err(Into::into)
        }

        #[uniffi::export]
        pub fn $parse(resp: HttpResponse) -> Result<HwTranscript, HwTranscriptionError> {
            use $module as m;
            m::parse_transcribe_response(&resp.into())
                .map(Into::into)
                .map_err(Into::into)
        }
    };
}

single_shot!(
    openai_build_transcribe_request,
    openai_parse_transcribe_response,
    hw_net::providers::openai
);
single_shot!(
    groq_build_transcribe_request,
    groq_parse_transcribe_response,
    hw_net::providers::groq
);
single_shot!(
    mistral_build_transcribe_request,
    mistral_parse_transcribe_response,
    hw_net::providers::mistral
);
single_shot!(
    grok_build_transcribe_request,
    grok_parse_transcribe_response,
    hw_net::providers::grok
);
single_shot!(
    deepgram_build_transcribe_request,
    deepgram_parse_transcribe_response,
    hw_net::providers::deepgram
);
single_shot!(
    elevenlabs_build_transcribe_request,
    elevenlabs_parse_transcribe_response,
    hw_net::providers::elevenlabs
);
single_shot!(
    azure_mai_build_transcribe_request,
    azure_mai_parse_transcribe_response,
    hw_net::providers::azure_mai
);
single_shot!(
    google_chirp_build_transcribe_request,
    google_chirp_parse_transcribe_response,
    hw_net::providers::google_chirp
);

// ===========================================================================
// HyperWhisper Cloud (single-shot + credit helpers)
// ===========================================================================

#[uniffi::export]
pub fn hyperwhisper_cloud_build_transcribe_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::hyperwhisper_cloud::build_transcribe_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn hyperwhisper_cloud_parse_transcribe_response(
    resp: HttpResponse,
) -> Result<HwTranscript, HwTranscriptionError> {
    hw_net::providers::hyperwhisper_cloud::parse_transcribe_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn hyperwhisper_cloud_parse_credits_remaining(resp: HttpResponse) -> Option<f64> {
    hw_net::providers::hyperwhisper_cloud::parse_credits_remaining(&resp.into())
}

#[uniffi::export]
pub fn hyperwhisper_cloud_parse_credits_used(resp: HttpResponse) -> Option<f64> {
    hw_net::providers::hyperwhisper_cloud::parse_credits_used(&resp.into())
}

// ===========================================================================
// AssemblyAI (multi-step: upload -> create -> poll)
// ===========================================================================

#[uniffi::export]
pub fn assemblyai_build_upload_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::assemblyai::build_upload_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_parse_upload_response(
    resp: HttpResponse,
) -> Result<String, HwTranscriptionError> {
    hw_net::providers::assemblyai::parse_upload_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_build_create_request(
    params: TranscribeParams,
    audio_url: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::assemblyai::build_create_request(&params.into(), &audio_url)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_parse_create_response(
    resp: HttpResponse,
) -> Result<String, HwTranscriptionError> {
    hw_net::providers::assemblyai::parse_create_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_build_poll_request(
    params: TranscribeParams,
    id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::assemblyai::build_poll_request(&params.into(), &id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn assemblyai_parse_poll_response(
    resp: HttpResponse,
) -> Result<AssemblyaiPollOutcome, HwTranscriptionError> {
    hw_net::providers::assemblyai::parse_poll_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

// ===========================================================================
// Gemini (multi-step: upload-start -> upload-bytes -> poll -> generate -> delete)
// ===========================================================================

#[uniffi::export]
pub fn gemini_build_upload_start_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_upload_start_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_upload_start_response(
    resp: HttpResponse,
) -> Result<String, HwTranscriptionError> {
    hw_net::providers::gemini::parse_upload_start_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_upload_bytes_request(
    params: TranscribeParams,
    upload_url: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_upload_bytes_request(&params.into(), &upload_url)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_upload_bytes_response(
    resp: HttpResponse,
) -> Result<GeminiFile, HwTranscriptionError> {
    hw_net::providers::gemini::parse_upload_bytes_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_poll_request(
    params: TranscribeParams,
    name: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_poll_request(&params.into(), &name)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_poll_response(
    resp: HttpResponse,
) -> Result<GeminiFilePollOutcome, HwTranscriptionError> {
    hw_net::providers::gemini::parse_poll_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_generate_request(
    params: TranscribeParams,
    file: GeminiFile,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_generate_request(&params.into(), &file.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_parse_generate_response(
    resp: HttpResponse,
) -> Result<HwTranscript, HwTranscriptionError> {
    hw_net::providers::gemini::parse_generate_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_delete_request(
    params: TranscribeParams,
    name: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::gemini::build_delete_request(&params.into(), &name)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn gemini_build_prompt(params: TranscribeParams) -> String {
    hw_net::providers::gemini::build_prompt(&params.into())
}

// ===========================================================================
// Soniox (multi-step: upload -> create -> status -> transcript -> delete)
// ===========================================================================

#[uniffi::export]
pub fn soniox_build_upload_request(
    params: TranscribeParams,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_upload_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_upload_response(resp: HttpResponse) -> Result<String, HwTranscriptionError> {
    hw_net::providers::soniox::parse_upload_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_create_request(
    params: TranscribeParams,
    file_id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_create_request(&params.into(), &file_id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_create_response(resp: HttpResponse) -> Result<String, HwTranscriptionError> {
    hw_net::providers::soniox::parse_create_response(&resp.into()).map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_status_request(
    params: TranscribeParams,
    transcription_id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_status_request(&params.into(), &transcription_id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_status_response(
    resp: HttpResponse,
) -> Result<SonioxPollStatus, HwTranscriptionError> {
    hw_net::providers::soniox::parse_status_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_transcript_request(
    params: TranscribeParams,
    transcription_id: String,
) -> Result<HttpRequest, HwTranscriptionError> {
    hw_net::providers::soniox::build_transcript_request(&params.into(), &transcription_id)
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_parse_transcript_response(
    resp: HttpResponse,
) -> Result<HwTranscript, HwTranscriptionError> {
    hw_net::providers::soniox::parse_transcript_response(&resp.into())
        .map(Into::into)
        .map_err(Into::into)
}

#[uniffi::export]
pub fn soniox_build_delete_transcription_request(
    params: TranscribeParams,
    transcription_id: String,
) -> HttpRequest {
    hw_net::providers::soniox::build_delete_transcription_request(&params.into(), &transcription_id)
        .into()
}

#[uniffi::export]
pub fn soniox_build_delete_file_request(
    params: TranscribeParams,
    file_id: String,
) -> HttpRequest {
    hw_net::providers::soniox::build_delete_file_request(&params.into(), &file_id).into()
}
