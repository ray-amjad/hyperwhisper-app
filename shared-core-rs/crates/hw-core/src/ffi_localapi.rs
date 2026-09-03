//! UniFFI surface for the Local API wire contract (`hw_localapi`, #289).
//!
//! Follows the `ffi_releasenotes` shape: the leaf crate's types are **mirrored**
//! here as owned `uniffi::Record`/`uniffi::Enum` types with `From` impls, rather
//! than re-exported, so `hw-localapi` stays a plain, dependency-free crate that
//! can be fuzzed and unit-tested without UniFFI in the way.
//!
//! # The `HwLocalApi` prefix is not cosmetic
//!
//! An unprefixed `Failure` or `OriginHeaders` would generate a bare type in
//! `hyperwhisper_core.cs`, in the same namespace every .NET head already
//! imports next to its own `LocalApiFailure` / `ApiFailureEnvelope`. The prefix
//! keeps both reachable without a namespace alias in the head.
//!
//! # The host owns entropy, the crate owns encoding
//!
//! [`local_api_generate_token`] takes 32 bytes and encodes them. It does not
//! draw them. The workspace release profile sets `panic = "abort"`, so a
//! `rand` failure inside Rust would abort the whole app where
//! `SecRandomCopyBytes` / `RandomNumberGenerator.GetBytes` failing is something
//! the head can handle. Issue #289 calls this out by name.
//!
//! # The status code comes back from Rust too
//!
//! [`HwLocalApiFailure::http_status`] is a field, not something the head
//! derives. That is the whole point of the envelope half of #289: Linux
//! returned 404/413/503/408 for business failures where the docs and the two
//! other platforms mandate 200, and it did so because each head decided the
//! status itself.
//!
//! # The engine and mode surfaces (#356)
//!
//! [`local_api_resolve_engine_alias`] resolves `POST /transcribe`'s `engine`
//! field and **says nothing about availability** — macOS has engines the .NET
//! heads do not, so one shared answer to "can this build serve it" would be
//! wrong on two platforms. `None` also covers the whole
//! `<CloudProvider rawValue>` half of the documented field, which belongs to
//! `hw-catalog`'s `CloudSttCatalog` and must not be reimplemented here.
//!
//! Two of the mode exports are deliberately called by **two heads, not three**:
//! [`local_api_mode_key_classification`] and
//! [`local_api_missing_required_mode_keys`]. macOS discards an unmapped key
//! inside `JSONDecoder` and enforces the required seven through `ModeDTO`'s
//! non-optional `let`s, so it conforms by construction and calling either one
//! there would mean adding a `JSONSerialization` pass to the head this sandbox
//! cannot build. That is not the `lib.rs:36-42` failure of exporting something
//! no head can call — each names its real call sites in its doc comment.
//!
//! # The transcription failure table (#356 item 4)
//!
//! [`local_api_map_transcription_error`] is the **only** export item 4 adds.
//! `HwLocalApiTranscriptionFailureReason::code` and the reason list are not
//! exported: a head hands a reason in and reads the `HwLocalApiFailure` that
//! comes back, so a separate code getter or an enumeration would be an export
//! with no call site — the `lib.rs:36-42` rule again.
//!
//! Its hint is a parameter on three of its twenty-five rows, for the same
//! reason [`local_api_unauthorized_failure`]'s is: the string names the
//! platform's own product surface (`Settings → API Keys` on macOS, the
//! `Model Library API keys manager` on Windows) and no shared string is right
//! on both. On the other twenty-two the wording is the crate's and the
//! parameter is ignored.

/// The three request headers the DNS-rebind guard reads. Mirrors
/// `hw_localapi::OriginHeaders`.
///
/// The head does the lookup, and the lookup must be case-insensitive (RFC 7230
/// §3.2). Every head already has a case-insensitive header map — FlyingFox's
/// `HTTPHeader`, ASP.NET Core's `IHeaderDictionary` — so this record carries
/// values, never names.
#[derive(uniffi::Record)]
pub struct HwLocalApiOriginHeaders {
    /// `Host`. `None` means the request carried no `Host` header at all, which
    /// is itself a denial.
    pub host: Option<String>,
    /// `Origin`. `None` or empty is fine; a non-loopback value is a denial.
    pub origin: Option<String>,
    /// `Sec-Fetch-Site`. `None` is fine — curl and the MCP wrapper omit it.
    pub sec_fetch_site: Option<String>,
}

impl From<&HwLocalApiOriginHeaders> for hw_localapi::OriginHeaders {
    fn from(headers: &HwLocalApiOriginHeaders) -> Self {
        hw_localapi::OriginHeaders {
            host: headers.host.clone(),
            origin: headers.origin.clone(),
            sec_fetch_site: headers.sec_fetch_site.clone(),
        }
    }
}

/// Why the guard let a request through, or why it did not. Mirrors
/// `hw_localapi::OriginDecision`.
///
/// A head only needs the allow/deny bit — the wire response is the same 403
/// whichever denial fired, and no reason ever reaches a client. The reasons
/// cross the boundary anyway so a head can log which check rejected a request,
/// which is the difference between "someone is probing us" and "the MCP wrapper
/// is sending the wrong `Host`".
#[derive(uniffi::Enum, Debug, Clone, Copy, PartialEq, Eq)]
pub enum HwLocalApiOriginDecision {
    /// Safe to dispatch.
    Allow,
    /// The server is not bound yet, so no `Host` can be checked against a port.
    DeniedPortUnknown,
    /// No `Host` header, or one that is empty after trimming.
    DeniedMissingHost,
    /// The `Host` header does not name loopback on the bound port.
    DeniedHost,
    /// `Sec-Fetch-Site` was present and was neither `same-origin` nor `none`.
    DeniedFetchSite,
    /// `Origin` was present, non-empty, and did not name loopback on the bound
    /// port.
    DeniedOrigin,
}

impl From<hw_localapi::OriginDecision> for HwLocalApiOriginDecision {
    fn from(decision: hw_localapi::OriginDecision) -> Self {
        match decision {
            hw_localapi::OriginDecision::Allow => HwLocalApiOriginDecision::Allow,
            hw_localapi::OriginDecision::DeniedPortUnknown => {
                HwLocalApiOriginDecision::DeniedPortUnknown
            }
            hw_localapi::OriginDecision::DeniedMissingHost => {
                HwLocalApiOriginDecision::DeniedMissingHost
            }
            hw_localapi::OriginDecision::DeniedHost => HwLocalApiOriginDecision::DeniedHost,
            hw_localapi::OriginDecision::DeniedFetchSite => {
                HwLocalApiOriginDecision::DeniedFetchSite
            }
            hw_localapi::OriginDecision::DeniedOrigin => HwLocalApiOriginDecision::DeniedOrigin,
        }
    }
}

/// Decide whether a request is safe to dispatch.
///
/// Call this on EVERY route, including the unauthenticated `GET /health`,
/// before the bearer check and before any dispatch. Pass the port the server is
/// really bound to — a fallback bind lands somewhere other than the configured
/// preference, and the `Host` header names where the client actually connected.
#[uniffi::export]
pub fn local_api_check_origin(
    headers: HwLocalApiOriginHeaders,
    port: u16,
) -> HwLocalApiOriginDecision {
    hw_localapi::check_origin(&(&headers).into(), port).into()
}

/// Whether a decision means "dispatch it".
///
/// A head can match the enum instead. This exists so the common case is one
/// call and cannot get the polarity wrong.
#[uniffi::export]
pub fn local_api_origin_decision_is_allowed(decision: HwLocalApiOriginDecision) -> bool {
    matches!(decision, HwLocalApiOriginDecision::Allow)
}

/// Why [`local_api_generate_token`] refused. Mirrors `hw_localapi::TokenError`.
#[derive(uniffi::Error, Debug)]
pub enum HwLocalApiTokenError {
    /// The host passed something other than 32 bytes.
    WrongEntropyLength {
        /// Always 32.
        expected: u32,
        /// What the caller actually passed.
        actual: u32,
    },
}

impl std::fmt::Display for HwLocalApiTokenError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            HwLocalApiTokenError::WrongEntropyLength { expected, actual } => write!(
                formatter,
                "local API token needs exactly {expected} entropy bytes, got {actual}"
            ),
        }
    }
}

impl std::error::Error for HwLocalApiTokenError {}

impl From<hw_localapi::TokenError> for HwLocalApiTokenError {
    fn from(error: hw_localapi::TokenError) -> Self {
        match error {
            hw_localapi::TokenError::WrongEntropyLength { expected, actual } => {
                HwLocalApiTokenError::WrongEntropyLength {
                    // Both are lengths of a 32-byte buffer in practice; a
                    // saturating cast keeps the conversion total rather than
                    // panicking on a value no caller can produce.
                    expected: u32::try_from(expected).unwrap_or(u32::MAX),
                    actual: u32::try_from(actual).unwrap_or(u32::MAX),
                }
            }
        }
    }
}

/// How many entropy bytes [`local_api_generate_token`] requires.
///
/// A constant rather than a magic 32 in three heads. `SecRandomCopyBytes` and
/// `RandomNumberGenerator.GetBytes` both take a count; this is that count.
#[uniffi::export]
pub fn local_api_token_entropy_bytes() -> u32 {
    u32::try_from(hw_localapi::TOKEN_ENTROPY_BYTES).unwrap_or(32)
}

/// Encode 32 host-supplied CSPRNG bytes as a Local API bearer token: unpadded
/// base64url, always 43 characters.
///
/// The host draws the bytes from its own platform CSPRNG and decides what to do
/// if that fails. See the module docs for why the entropy does not come from
/// Rust.
#[uniffi::export]
pub fn local_api_generate_token(entropy: Vec<u8>) -> Result<String, HwLocalApiTokenError> {
    hw_localapi::generate_token(&entropy).map_err(HwLocalApiTokenError::from)
}

/// Whether a stored credential still has the shape
/// [`local_api_generate_token`] produces.
///
/// A *shape* check, for a head deciding whether to regenerate. Never use it in
/// place of [`local_api_authorize`]: it looks only at the presented value and
/// compares nothing.
#[uniffi::export]
pub fn local_api_is_well_formed_token(token: String) -> bool {
    hw_localapi::is_well_formed_token(&token)
}

/// Whether an `Authorization` header presents the expected token.
///
/// One constant-time compare of SHA-256 digests, replacing three divergent
/// native behaviours. `authorization_header` is the raw header value, or `None`
/// when the request carried none. An empty `expected_token` always denies.
#[uniffi::export]
pub fn local_api_authorize(authorization_header: Option<String>, expected_token: String) -> bool {
    hw_localapi::authorize(authorization_header.as_deref(), &expected_token)
}

/// The SHA-256 fingerprint of a token, hex-encoded — for a log line that has to
/// identify a credential without carrying it.
#[uniffi::export]
pub fn local_api_token_fingerprint(token: String) -> String {
    hw_localapi::token_fingerprint(&token)
}

/// The closed set of Local API error codes. Mirrors
/// `hw_localapi::LocalApiErrorCode`.
///
/// Closed is the property that matters: the macOS decoder is a Swift `Codable`
/// enum, so a client sharing it fails to decode the whole envelope on a
/// fifteenth code rather than seeing an unknown one. There is no `Other`
/// variant here for the same reason.
#[derive(uniffi::Enum, Debug, Clone, Copy, PartialEq, Eq)]
pub enum HwLocalApiErrorCode {
    ModelNotInstalled,
    ModelNotFound,
    EngineUnavailable,
    MissingApiKey,
    FileNotFound,
    FileAccessDenied,
    FileNotAllowed,
    AudioDecodeFailed,
    TranscriptionFailed,
    ModeNotFound,
    ModeNameTaken,
    InvalidRequest,
    RateLimited,
    Timeout,
}

impl From<hw_localapi::LocalApiErrorCode> for HwLocalApiErrorCode {
    fn from(code: hw_localapi::LocalApiErrorCode) -> Self {
        use hw_localapi::LocalApiErrorCode as Code;
        match code {
            Code::ModelNotInstalled => HwLocalApiErrorCode::ModelNotInstalled,
            Code::ModelNotFound => HwLocalApiErrorCode::ModelNotFound,
            Code::EngineUnavailable => HwLocalApiErrorCode::EngineUnavailable,
            Code::MissingApiKey => HwLocalApiErrorCode::MissingApiKey,
            Code::FileNotFound => HwLocalApiErrorCode::FileNotFound,
            Code::FileAccessDenied => HwLocalApiErrorCode::FileAccessDenied,
            Code::FileNotAllowed => HwLocalApiErrorCode::FileNotAllowed,
            Code::AudioDecodeFailed => HwLocalApiErrorCode::AudioDecodeFailed,
            Code::TranscriptionFailed => HwLocalApiErrorCode::TranscriptionFailed,
            Code::ModeNotFound => HwLocalApiErrorCode::ModeNotFound,
            Code::ModeNameTaken => HwLocalApiErrorCode::ModeNameTaken,
            Code::InvalidRequest => HwLocalApiErrorCode::InvalidRequest,
            Code::RateLimited => HwLocalApiErrorCode::RateLimited,
            Code::Timeout => HwLocalApiErrorCode::Timeout,
        }
    }
}

impl From<HwLocalApiErrorCode> for hw_localapi::LocalApiErrorCode {
    fn from(code: HwLocalApiErrorCode) -> Self {
        use hw_localapi::LocalApiErrorCode as Code;
        match code {
            HwLocalApiErrorCode::ModelNotInstalled => Code::ModelNotInstalled,
            HwLocalApiErrorCode::ModelNotFound => Code::ModelNotFound,
            HwLocalApiErrorCode::EngineUnavailable => Code::EngineUnavailable,
            HwLocalApiErrorCode::MissingApiKey => Code::MissingApiKey,
            HwLocalApiErrorCode::FileNotFound => Code::FileNotFound,
            HwLocalApiErrorCode::FileAccessDenied => Code::FileAccessDenied,
            HwLocalApiErrorCode::FileNotAllowed => Code::FileNotAllowed,
            HwLocalApiErrorCode::AudioDecodeFailed => Code::AudioDecodeFailed,
            HwLocalApiErrorCode::TranscriptionFailed => Code::TranscriptionFailed,
            HwLocalApiErrorCode::ModeNotFound => Code::ModeNotFound,
            HwLocalApiErrorCode::ModeNameTaken => Code::ModeNameTaken,
            HwLocalApiErrorCode::InvalidRequest => Code::InvalidRequest,
            HwLocalApiErrorCode::RateLimited => Code::RateLimited,
            HwLocalApiErrorCode::Timeout => Code::Timeout,
        }
    }
}

/// The wire string for a code — what goes in `error.code`.
#[uniffi::export]
pub fn local_api_error_code_wire_value(code: HwLocalApiErrorCode) -> String {
    hw_localapi::LocalApiErrorCode::from(code)
        .wire_value()
        .to_string()
}

/// Parse a wire string back into the closed set, or `None` when it is not one
/// of the 14.
///
/// This is the conformance check a head runs over the codes it emits: a `None`
/// names a code that would break the macOS decoder.
#[uniffi::export]
pub fn local_api_error_code_from_wire_value(value: String) -> Option<HwLocalApiErrorCode> {
    hw_localapi::LocalApiErrorCode::from_wire_value(&value).map(HwLocalApiErrorCode::from)
}

/// Every code, in the order the docs and the macOS enum list them.
#[uniffi::export]
pub fn local_api_all_error_codes() -> Vec<HwLocalApiErrorCode> {
    hw_localapi::ALL_ERROR_CODES
        .into_iter()
        .map(HwLocalApiErrorCode::from)
        .collect()
}

/// A complete failure response. Mirrors `hw_localapi::Failure`.
///
/// The head serializes the envelope with its own encoder, so this record does
/// not dictate key order or how an absent hint is elided. What it does dictate
/// is `http_status`, which is the half of #289 Linux got wrong.
#[derive(uniffi::Record)]
pub struct HwLocalApiFailure {
    /// The status to send. 200 for a business failure; 400/401/403 for the
    /// three protocol cases.
    pub http_status: u16,
    /// The wire code, always one of the 14.
    pub code: HwLocalApiErrorCode,
    /// Human-readable, shown to the agent by an MCP wrapper.
    pub message: String,
    /// What to do about it, when there is something to say.
    pub hint: Option<String>,
    /// The whole envelope as JSON, for a head with no typed model to hand.
    /// `{"ok":false,"error":{"code":…,"message":…[,"hint":…]}}`.
    pub json: String,
}

impl From<hw_localapi::Failure> for HwLocalApiFailure {
    fn from(failure: hw_localapi::Failure) -> Self {
        HwLocalApiFailure {
            http_status: failure.http_status(),
            code: failure.code.into(),
            message: failure.message.clone(),
            hint: failure.hint.clone(),
            json: failure.to_json(),
        }
    }
}

/// The HTTP status a business failure travels on: 200, on every platform.
///
/// Exported as a function rather than left to each head, because "each head
/// decides" is how Linux ended up returning 404/413/503/408 for outcomes the
/// docs mandate 200 for.
#[uniffi::export]
pub fn local_api_business_failure(
    code: HwLocalApiErrorCode,
    message: String,
    hint: Option<String>,
) -> HwLocalApiFailure {
    let mut failure = hw_localapi::Failure::business(code.into(), message);
    if let Some(hint) = hint {
        failure = failure.with_hint(hint);
    }
    failure.into()
}

/// A malformed request — HTTP 400, always `INVALID_REQUEST`.
#[uniffi::export]
pub fn local_api_bad_request_failure(message: String, hint: Option<String>) -> HwLocalApiFailure {
    let mut failure = hw_localapi::Failure::bad_request(message);
    if let Some(hint) = hint {
        failure = failure.with_hint(hint);
    }
    failure.into()
}

/// The response the origin guard returns — HTTP 403 carrying `INVALID_REQUEST`,
/// exactly what macOS already sends (`LocalAPIServer.swift:311-316`).
///
/// Not a new `FORBIDDEN` code. Issue #289 is explicit that inventing one would
/// itself be a contract change on the one platform that ships the guard.
#[uniffi::export]
pub fn local_api_forbidden_origin_failure() -> HwLocalApiFailure {
    hw_localapi::forbidden_origin().into()
}

/// The response a failed bearer check returns — HTTP 401 carrying
/// `INVALID_REQUEST`, matching `LocalAPIServer.swift:344-353`.
///
/// `hint` names the platform's own discovery-file path, which is the one part
/// of this response that legitimately differs per head. The caller must still
/// send `WWW-Authenticate: Bearer realm="hyperwhisper"`; a header is not part
/// of the envelope.
#[uniffi::export]
pub fn local_api_unauthorized_failure(hint: Option<String>) -> HwLocalApiFailure {
    hw_localapi::unauthorized(hint).into()
}

/// The largest request body any head accepts, in bytes: 50 MiB.
///
/// A constant rather than a magic `52_428_800` in three heads, for the same
/// reason as [`local_api_token_entropy_bytes`]. macOS shipped no cap at all
/// (#375): `HTTPServer` takes an address and a timeout, and every write
/// endpoint read the whole body with `try await request.bodyData`, so the
/// caller chose the app's peak resident memory. The number is
/// `PortableLocalApiOptions.MaxRequestBytes` (`PortableLocalApi.cs:18`), which
/// the Linux head already enforces.
#[uniffi::export]
pub fn local_api_max_request_bytes() -> u64 {
    hw_localapi::MAX_REQUEST_BYTES
}

/// The largest piece of audio any head accepts, in bytes: 48 MiB.
///
/// `PortableLocalApiOptions.MaxUploadBytes` (`PortableLocalApi.cs:19`).
/// Applies to the decoded bytes of an `audio_base64` payload and to a
/// multipart `audio` part — the two shapes a head buffers whole. Always less
/// than or equal to [`local_api_max_request_bytes`].
#[uniffi::export]
pub fn local_api_max_upload_bytes() -> u64 {
    hw_localapi::MAX_UPLOAD_BYTES
}

/// The longest base64 string that can decode to
/// [`local_api_max_upload_bytes`] or fewer bytes.
///
/// Check the trimmed string's length against this **before** decoding. That
/// pre-check is the half of #375 that stops the amplification: without it a
/// caller makes the head allocate the decoded buffer only to be told the
/// decoded buffer is too big. Derived from the upload cap, not the request cap
/// — same as `PortableLocalApi.cs:244`.
#[uniffi::export]
pub fn local_api_max_base64_length_for_upload() -> u64 {
    hw_localapi::max_base64_length_for_upload()
}

/// The response an over-sized request body gets, byte for byte what the Linux
/// head already sends (`PortableLocalApi.cs:185`).
///
/// **HTTP 200 carrying `INVALID_REQUEST`, not 413.** #375 suggests a 413, but
/// a 413 wants a `PAYLOAD_TOO_LARGE` code, and that is one of the four codes
/// outside the closed 14 — a client sharing the macOS `Codable` decoder cannot
/// decode *any* envelope carrying it. See the `failure.rs` module docs.
#[uniffi::export]
pub fn local_api_request_too_large_failure() -> HwLocalApiFailure {
    hw_localapi::request_too_large().into()
}

/// The response an over-sized audio upload gets, byte for byte what the Linux
/// head already sends (`PortableLocalApi.cs:193`, `:245`, `:251`).
///
/// One message for the multipart part, the base64 string before decoding and
/// the decoded bytes alike — a caller cannot tell those apart and the .NET head
/// does not distinguish them either. HTTP 200 carrying `INVALID_REQUEST`.
#[uniffi::export]
pub fn local_api_upload_too_large_failure() -> HwLocalApiFailure {
    hw_localapi::upload_too_large().into()
}

// ===========================================================================
// #356 item 3 — the engine alias table.
// ===========================================================================

/// A canonical transcription engine id. Mirrors `hw_localapi::EngineId`.
///
/// The five names `openapi.yaml` documents for `engine` outside the
/// `<CloudProvider rawValue>` set. Two of them,
/// [`HwLocalApiEngineId::Nemotron`] and [`HwLocalApiEngineId::AppleSpeech`], are
/// macOS-only capabilities — resolving one on a .NET head is correct and the
/// head then answers `ENGINE_UNAVAILABLE`, which is a better response than the
/// `Unknown engine '…'` it sends today.
#[derive(uniffi::Enum, Debug, Clone, Copy, PartialEq, Eq)]
pub enum HwLocalApiEngineId {
    /// Local `whisper.cpp`.
    WhisperLocal,
    /// Local Parakeet.
    Parakeet,
    /// Local Nemotron. macOS only.
    Nemotron,
    /// Local Qwen3-ASR.
    Qwen3Asr,
    /// Apple `SpeechAnalyzer`. macOS only.
    AppleSpeech,
}

impl From<hw_localapi::EngineId> for HwLocalApiEngineId {
    fn from(id: hw_localapi::EngineId) -> Self {
        match id {
            hw_localapi::EngineId::WhisperLocal => HwLocalApiEngineId::WhisperLocal,
            hw_localapi::EngineId::Parakeet => HwLocalApiEngineId::Parakeet,
            hw_localapi::EngineId::Nemotron => HwLocalApiEngineId::Nemotron,
            hw_localapi::EngineId::Qwen3Asr => HwLocalApiEngineId::Qwen3Asr,
            hw_localapi::EngineId::AppleSpeech => HwLocalApiEngineId::AppleSpeech,
        }
    }
}

impl From<HwLocalApiEngineId> for hw_localapi::EngineId {
    fn from(id: HwLocalApiEngineId) -> Self {
        match id {
            HwLocalApiEngineId::WhisperLocal => hw_localapi::EngineId::WhisperLocal,
            HwLocalApiEngineId::Parakeet => hw_localapi::EngineId::Parakeet,
            HwLocalApiEngineId::Nemotron => hw_localapi::EngineId::Nemotron,
            HwLocalApiEngineId::Qwen3Asr => hw_localapi::EngineId::Qwen3Asr,
            HwLocalApiEngineId::AppleSpeech => hw_localapi::EngineId::AppleSpeech,
        }
    }
}

/// Resolve `POST /transcribe`'s `engine` string to a canonical id. Trim, then
/// lowercase, then match.
///
/// `None` means "not one of the five", **not** "rejected": the caller's next
/// step is `CloudSttCatalog.normalizeCloudProvider`, which owns the
/// `<CloudProvider rawValue>` half of the documented field, and only after that
/// does the string become `ENGINE_UNAVAILABLE`.
///
/// **Call sites: all three heads, four switches.** macOS has two —
/// `TranscribeEndpoint.applyEngineModel` and
/// `TranscriptionProviderRouter.resolveProvider`, whose own comments admit they
/// are hand-synced, so both must call this or the drift only moves inside one
/// platform. Then `TranscribeEndpoints.ApplyEngineModel` on Windows and
/// `ApplicationLocalApiBackend.ApplyTranscriptionOverrides` on the portable head.
#[uniffi::export]
pub fn local_api_resolve_engine_alias(alias: String) -> Option<HwLocalApiEngineId> {
    hw_localapi::resolve_engine_alias(&alias).map(HwLocalApiEngineId::from)
}

/// The spelling a response's `engine` field must carry — `openapi.yaml`'s.
///
/// Windows and the portable head emit `qwen3_asr` today, which their own sibling
/// does not accept as a request. Reading the label from here is what closes that
/// round trip.
#[uniffi::export]
pub fn local_api_engine_wire_label(id: HwLocalApiEngineId) -> String {
    hw_localapi::EngineId::from(id).wire_label().to_string()
}

/// Every engine id, in the order `openapi.yaml` lists them.
#[uniffi::export]
pub fn local_api_all_engine_ids() -> Vec<HwLocalApiEngineId> {
    hw_localapi::ALL_ENGINE_IDS
        .into_iter()
        .map(HwLocalApiEngineId::from)
        .collect()
}

// ===========================================================================
// #356 items 2 and 5 — the mode contract.
// ===========================================================================

/// Which request a mode body belongs to. Mirrors `hw_localapi::ModeOperation`.
///
/// The required-key rule is create-only, and the `MODE_NAME_TAKEN` hint follows
/// the same split — both heads that ship the string attach it on create and omit
/// it on patch.
#[derive(uniffi::Enum, Debug, Clone, Copy, PartialEq, Eq)]
pub enum HwLocalApiModeOperation {
    /// `POST /modes`.
    Create,
    /// `PATCH /modes/{id}`.
    Patch,
}

impl From<HwLocalApiModeOperation> for hw_localapi::ModeOperation {
    fn from(operation: HwLocalApiModeOperation) -> Self {
        match operation {
            HwLocalApiModeOperation::Create => hw_localapi::ModeOperation::Create,
            HwLocalApiModeOperation::Patch => hw_localapi::ModeOperation::Patch,
        }
    }
}

/// What a head should do with a top-level key in a mode body. Mirrors
/// `hw_localapi::ModeKeyClass`.
///
/// **Every variant means "keep going".** None of them is a rejection: the
/// verdict for an unrecognised key is *ignore*, because `openapi.yaml` already
/// documents five keys as "Windows only. macOS ignores this key" and so invites
/// a cross-platform client to send keys a given head does not implement.
#[derive(uniffi::Enum, Debug, Clone, Copy, PartialEq, Eq)]
pub enum HwLocalApiModeKeyClass {
    /// A documented key every head is expected to honour.
    Known,
    /// A documented key marked "Windows only. macOS ignores this key".
    PlatformOnly,
    /// A documented key the server owns: returned on a `GET`, ignored on a write.
    ReadOnly,
    /// Not in the union. Ignore it, and log it.
    Unknown,
}

impl From<hw_localapi::ModeKeyClass> for HwLocalApiModeKeyClass {
    fn from(class: hw_localapi::ModeKeyClass) -> Self {
        match class {
            hw_localapi::ModeKeyClass::Known => HwLocalApiModeKeyClass::Known,
            hw_localapi::ModeKeyClass::PlatformOnly => HwLocalApiModeKeyClass::PlatformOnly,
            hw_localapi::ModeKeyClass::ReadOnly => HwLocalApiModeKeyClass::ReadOnly,
            hw_localapi::ModeKeyClass::Unknown => HwLocalApiModeKeyClass::Unknown,
        }
    }
}

/// Classify one top-level key of a mode body. Exact, case-sensitive.
///
/// **Call sites: the portable head and Windows. Not macOS** — see the module
/// docs. On the portable head this replaces
/// `default: throw new ArgumentException($"Unsupported mode field …")` with
/// classify-and-ignore, which is the one head that enforced; on Windows it is a
/// debug log beside a decoder that already conforms.
#[uniffi::export]
pub fn local_api_mode_key_classification(key: String) -> HwLocalApiModeKeyClass {
    hw_localapi::mode_key_classification(&key).into()
}

/// The seven keys `openapi.yaml`'s `Mode` schema marks `required:`.
///
/// macOS's `ModeDTO` declares exactly these as non-optional, so macOS already
/// conforms; Windows requires only `name` and the portable head requires
/// nothing. Adopting the published set tightens two heads — call it out in the
/// PR body, because `{"name":"Only"}` creates a mode on both today.
#[uniffi::export]
pub fn local_api_required_mode_keys() -> Vec<String> {
    hw_localapi::REQUIRED_MODE_KEYS
        .into_iter()
        .map(String::from)
        .collect()
}

/// Which required keys a create body did not carry, in the documented order.
///
/// **Call sites: the portable head and Windows only, deliberately.** The portable
/// head already walks `document.EnumerateObject()`. Windows needs a new
/// `ReadJsonBodyWithKeysAsync<T>` because it cannot infer presence from
/// `ModeDto` — `Punctuation`/`Capitalization`/`ProfanityFilter` are non-nullable
/// `bool`, so an absent key and `false` are the same value. macOS needs neither:
/// its decoder *is* this check.
#[uniffi::export]
pub fn local_api_missing_required_mode_keys(present: Vec<String>) -> Vec<String> {
    hw_localapi::missing_required_mode_keys(&present)
}

/// The fields [`local_api_validate_mode`] bounds. Mirrors
/// `hw_localapi::ModeValidationInput`.
///
/// The two numbers are `i64`, not `i32`/`i16`, so a head hands over an
/// out-of-range value instead of pre-truncating it. That matters concretely: the
/// portable head's `property.Value.GetInt32()` throws `FormatException` on
/// `{"sortOrder": 99999999999}` and nothing in its middleware catches it — an
/// unhandled HTTP 500 with no envelope. A wide crossing turns that into an
/// ordinary `INVALID_REQUEST`.
///
/// Every field is optional because a `PATCH` legitimately omits any of them. On
/// a create it is `present_keys` that makes an omission a failure, not a `None`
/// here.
#[derive(uniffi::Record)]
pub struct HwLocalApiModeValidationInput {
    /// Create or patch. Only a create is checked against the required seven.
    pub operation: HwLocalApiModeOperation,
    /// The body's top-level key names. A patch may pass an empty list.
    pub present_keys: Vec<String>,
    /// `name`, as sent. Trimmed before it is measured or compared.
    pub name: Option<String>,
    /// `language`.
    pub language: Option<String>,
    /// `preset`.
    pub preset: Option<String>,
    /// `postProcessingMode`. 0 = off, 1 = cloud, 2 = local.
    pub post_processing_mode: Option<i64>,
    /// `sortOrder`. Bounded to the `Int16` range, which is the only bound backed
    /// by a storage column and the one `openapi.yaml` already publishes.
    pub sort_order: Option<i64>,
    /// `userSystemPrompt`.
    pub user_system_prompt: Option<String>,
    /// `geminiCustomPrompt`.
    pub gemini_custom_prompt: Option<String>,
    /// `customVocabulary`.
    pub custom_vocabulary: Option<Vec<String>>,
}

impl From<HwLocalApiModeValidationInput> for hw_localapi::ModeValidationInput {
    fn from(input: HwLocalApiModeValidationInput) -> Self {
        hw_localapi::ModeValidationInput {
            operation: input.operation.into(),
            present_keys: input.present_keys,
            name: input.name,
            language: input.language,
            preset: input.preset,
            post_processing_mode: input.post_processing_mode,
            sort_order: input.sort_order,
            user_system_prompt: input.user_system_prompt,
            gemini_custom_prompt: input.gemini_custom_prompt,
            custom_vocabulary: input.custom_vocabulary,
        }
    }
}

/// Validate a mode body's shape and value ranges. `None` means acceptable.
///
/// **Call sites: all three heads**, on create and on patch.
///
/// Every failure this returns is a business failure: HTTP 200 carrying
/// `INVALID_REQUEST`, including a missing required key. The published envelope
/// rule reserves 4xx for protocol failures — a body that is not JSON, a bad
/// token, a rejected origin — and a body that is well-formed JSON but omits
/// `preset` is none of those. All three heads now answer it identically.
/// `INVALID_REQUEST` is in the closed 14; item 5 adds no code.
///
/// Lengths are counted in **Unicode scalar values**, which is a deliberate
/// choice and is client-visible: `string.Length` on .NET is UTF-16 code units,
/// so a 60-emoji name is 120 units and is refused by the portable head today,
/// while it is 60 scalars and accepted here.
#[uniffi::export]
pub fn local_api_validate_mode(input: HwLocalApiModeValidationInput) -> Option<HwLocalApiFailure> {
    hw_localapi::validate_mode(&input.into()).map(HwLocalApiFailure::from)
}

/// The key a mode name is compared *by*: trim Unicode whitespace, then
/// lowercase. `None` when nothing is left.
///
/// **This is the definition of "the same name"**, replacing .NET's
/// `OrdinalIgnoreCase` and Core Data's `==[c]`. It is not a storage name — each
/// head still writes whatever its own pre-normalisation produced.
///
/// **Call sites: all three heads.** On macOS it sits *behind*
/// `ModeNamePolicy.normalized`, which keeps NFC and the Unicode general-category
/// boundary trim. That half stays native because it needs
/// `unicode-normalization` and a category table, and `hw-localapi` takes no
/// dependency — it runs under `panic = "abort"` in front of a loopback socket.
#[uniffi::export]
pub fn local_api_mode_name_comparison_key(name: String) -> Option<String> {
    hw_localapi::mode_name_comparison_key(&name)
}

/// Whether `candidate` collides with any of `other_names`.
///
/// `other_names` is the caller's **already-filtered** list: every head excludes
/// the record it is writing before comparing, and only the head knows which one
/// that is. An empty candidate never collides — that is
/// [`local_api_validate_mode`]'s failure, and reporting it as a collision would
/// name the wrong problem.
///
/// **Call sites: all three heads.** macOS needs `fetchMode(byName:in:)` to stop
/// being a store-side `name ==[c] %@` fetch and become a candidate-name fetch
/// plus this call — six lines, on a head that already does full-table mode
/// fetches to list and delete.
#[uniffi::export]
pub fn local_api_mode_name_conflict(candidate: String, other_names: Vec<String>) -> bool {
    hw_localapi::mode_name_conflict(&candidate, &other_names)
}

/// The response a colliding name gets: HTTP 200 carrying `MODE_NAME_TAKEN`.
///
/// The message is macOS's and Windows's verbatim. The portable head sends
/// `"A mode with this name already exists."` as an `ArgumentException`, which its
/// middleware turns into HTTP **400 `INVALID_REQUEST`** — `MODE_NAME_TAKEN` is
/// declared on that head and never emitted.
///
/// `operation` picks the hint, preserving the two heads' existing split rather
/// than flattening it: both attach "choose a different name" on create and send
/// no hint on patch.
///
/// **Call sites: all three heads.**
#[uniffi::export]
pub fn local_api_mode_name_taken_failure(
    name: String,
    operation: HwLocalApiModeOperation,
) -> HwLocalApiFailure {
    hw_localapi::mode_name_taken_failure(&name, operation.into()).into()
}

/// Why a transcription failed. Mirrors
/// `hw_localapi::TranscriptionFailureReason`.
///
/// An **input** enum: the union of macOS's `TranscriptionError`, Windows's
/// `TranscriptionErrorCode` and the portable head's
/// `PortableTranscriptionErrorCode`. Every variant maps onto one of the closed
/// fourteen [`HwLocalApiErrorCode`]s, so #356 item 4 adds no error code —
/// `transcription.rs` says so and a unit test walks all 25 to prove it.
///
/// Each head keeps its own error type and maps it onto this on the way to the
/// wire. Nothing here replaces `TranscriptionError` or `TranscriptionException`.
#[derive(uniffi::Enum, Debug, Clone, Copy, PartialEq, Eq)]
pub enum HwLocalApiTranscriptionFailureReason {
    /// macOS `.modelNotDownloaded`; Windows `ModelNotLoaded`.
    ModelNotInstalled,
    /// Windows `OnnxModelFileMissing`.
    ModelFilesMissing,
    /// macOS `.modelProtected`.
    ModelProtected,
    /// macOS `.apiKeyMissing`; Windows `ApiKeyMissing`.
    ApiKeyMissing,
    /// macOS `.unauthorized` (except the HyperWhisper Cloud 403); Windows
    /// `Unauthorized`.
    ApiKeyInvalid,
    /// macOS `.cloudAccountRequired`; Windows `CloudAccountRequired`.
    CloudAccountRequired,
    /// macOS `.unauthorized(provider: "HyperWhisper Cloud", statusCode: 403)`
    /// — the abuse guard, which must not be reported as a key problem.
    CloudRequestForbidden,
    /// macOS `.audioFileNotFound`; Windows `AudioFileNotFound`.
    AudioFileNotFound,
    /// macOS `.invalidAudioFormat` / `.audioConversionFailed`; Windows
    /// `UnsupportedFormat`.
    AudioDecodeFailed,
    /// macOS `.audioFileTooLarge`; Windows `FileTooLarge`.
    AudioFileTooLarge,
    /// macOS `.invalidRequest`; Windows `InvalidRequest`; portable
    /// `InvalidRequest`.
    InvalidRequest,
    /// macOS `.rateLimited`; Windows `RateLimited`.
    RateLimited,
    /// macOS `.quotaExceeded` / `.insufficientCredits`; Windows
    /// `QuotaExceeded`.
    QuotaExceeded,
    /// macOS `.timeout`.
    Timeout,
    /// Windows `Cancelled`; portable `Cancelled`.
    Cancelled,
    /// macOS `.providerNotAvailable`; Windows `ProviderUnavailable`; portable
    /// `BackendUnavailable`.
    EngineUnavailable,
    /// macOS `.transientNetwork`; Windows `NetworkError`.
    NetworkUnavailable,
    /// Windows `DaemonStartFailed`.
    EngineStartFailed,
    /// Windows `DaemonCrashed`.
    EngineCrashed,
    /// Windows `DaemonTimeout`.
    EngineTimeout,
    /// macOS `.localSpeechModelEvicted`.
    LocalModelEvicted,
    /// macOS `.invalidResponse`.
    InvalidProviderResponse,
    /// macOS `.serverError`.
    ProviderServerError,
    /// macOS `.noSpeechDetected`; Windows `NoSpeechDetected`.
    NoSpeechDetected,
    /// The `default:` arm of both tables, and the portable head's
    /// `TranscriptionFailed`. Put the head's own text in
    /// [`HwLocalApiTranscriptionFailureParams::detail`].
    TranscriptionFailed,
}

impl From<HwLocalApiTranscriptionFailureReason> for hw_localapi::TranscriptionFailureReason {
    fn from(reason: HwLocalApiTranscriptionFailureReason) -> Self {
        use hw_localapi::TranscriptionFailureReason as Reason;
        match reason {
            HwLocalApiTranscriptionFailureReason::ModelNotInstalled => Reason::ModelNotInstalled,
            HwLocalApiTranscriptionFailureReason::ModelFilesMissing => Reason::ModelFilesMissing,
            HwLocalApiTranscriptionFailureReason::ModelProtected => Reason::ModelProtected,
            HwLocalApiTranscriptionFailureReason::ApiKeyMissing => Reason::ApiKeyMissing,
            HwLocalApiTranscriptionFailureReason::ApiKeyInvalid => Reason::ApiKeyInvalid,
            HwLocalApiTranscriptionFailureReason::CloudAccountRequired => {
                Reason::CloudAccountRequired
            }
            HwLocalApiTranscriptionFailureReason::CloudRequestForbidden => {
                Reason::CloudRequestForbidden
            }
            HwLocalApiTranscriptionFailureReason::AudioFileNotFound => Reason::AudioFileNotFound,
            HwLocalApiTranscriptionFailureReason::AudioDecodeFailed => Reason::AudioDecodeFailed,
            HwLocalApiTranscriptionFailureReason::AudioFileTooLarge => Reason::AudioFileTooLarge,
            HwLocalApiTranscriptionFailureReason::InvalidRequest => Reason::InvalidRequest,
            HwLocalApiTranscriptionFailureReason::RateLimited => Reason::RateLimited,
            HwLocalApiTranscriptionFailureReason::QuotaExceeded => Reason::QuotaExceeded,
            HwLocalApiTranscriptionFailureReason::Timeout => Reason::Timeout,
            HwLocalApiTranscriptionFailureReason::Cancelled => Reason::Cancelled,
            HwLocalApiTranscriptionFailureReason::EngineUnavailable => Reason::EngineUnavailable,
            HwLocalApiTranscriptionFailureReason::NetworkUnavailable => Reason::NetworkUnavailable,
            HwLocalApiTranscriptionFailureReason::EngineStartFailed => Reason::EngineStartFailed,
            HwLocalApiTranscriptionFailureReason::EngineCrashed => Reason::EngineCrashed,
            HwLocalApiTranscriptionFailureReason::EngineTimeout => Reason::EngineTimeout,
            HwLocalApiTranscriptionFailureReason::LocalModelEvicted => Reason::LocalModelEvicted,
            HwLocalApiTranscriptionFailureReason::InvalidProviderResponse => {
                Reason::InvalidProviderResponse
            }
            HwLocalApiTranscriptionFailureReason::ProviderServerError => {
                Reason::ProviderServerError
            }
            HwLocalApiTranscriptionFailureReason::NoSpeechDetected => Reason::NoSpeechDetected,
            HwLocalApiTranscriptionFailureReason::TranscriptionFailed => {
                Reason::TranscriptionFailed
            }
        }
    }
}

/// The runtime values the message and hint interpolate. Mirrors
/// `hw_localapi::TranscriptionFailureParams`.
///
/// All seven are optional and every row has a wording for the absent case, so a
/// head that knows only the reason still gets a complete sentence. A blank or
/// whitespace-only string counts as absent.
#[derive(uniffi::Record)]
pub struct HwLocalApiTranscriptionFailureParams {
    /// The provider or engine display name — `"OpenAI"`, `"HyperWhisper
    /// Cloud"`, `"Parakeet"`.
    pub provider: Option<String>,
    /// Free text from the failure itself: macOS's `reason` / `details` /
    /// `message` associated values, Windows's `ex.Message`, the portable head's
    /// `PortableTranscriptionFailure.Message`.
    pub detail: Option<String>,
    /// The model involved, for the two model-shaped reasons.
    pub model: Option<String>,
    /// The provider's byte limit, for `AudioFileTooLarge`.
    pub limit_bytes: Option<u64>,
    /// The HTTP status the provider returned, for `ProviderServerError`.
    pub http_status: Option<u16>,
    /// The provider's `Retry-After`, in seconds, for `RateLimited`.
    pub retry_after_seconds: Option<u32>,
    /// The head's own hint. Used **only** by `ApiKeyMissing`, `ApiKeyInvalid`
    /// and `CloudAccountRequired`, whose hint has to name a product surface:
    /// macOS's `Settings → API Keys` against Windows's
    /// `Model Library API keys manager`. Ignored for every other reason — the
    /// wording there is the crate's, which is the whole point of item 4.
    pub hint: Option<String>,
}

impl From<HwLocalApiTranscriptionFailureParams> for hw_localapi::TranscriptionFailureParams {
    fn from(params: HwLocalApiTranscriptionFailureParams) -> Self {
        hw_localapi::TranscriptionFailureParams {
            provider: params.provider,
            detail: params.detail,
            model: params.model,
            limit_bytes: params.limit_bytes,
            http_status: params.http_status,
            retry_after_seconds: params.retry_after_seconds,
            hint: params.hint,
        }
    }
}

/// The one `(code, message, hint)` table for a transcription failure — HTTP
/// 200, always, carrying one of the closed fourteen.
///
/// **Call sites: all three heads.** macOS's
/// `LocalAPIResponder.mapTranscriptionError`
/// (`LocalAPIErrors.swift:130-198`) and Windows's
/// `LocalApiResponder.MapTranscriptionException` (`LocalApiErrors.cs:84-147`)
/// each become a mapping from their own error type onto
/// [`HwLocalApiTranscriptionFailureReason`] plus params, then one call to this.
/// The portable head, which has no table at all and today collapses all four
/// `PortableTranscriptionErrorCode` values into one fixed `ENGINE_UNAVAILABLE`
/// string, gains one through `LocalApiSharedFailure`.
///
/// Do not confuse the name with `RustCoreMapping.mapTranscriptionError`
/// (`RustRetry.swift:344`), which maps a Rust `HwTranscriptionError` into the
/// Swift `TranscriptionError` and is *upstream* of this.
#[uniffi::export]
pub fn local_api_map_transcription_error(
    reason: HwLocalApiTranscriptionFailureReason,
    params: HwLocalApiTranscriptionFailureParams,
) -> HwLocalApiFailure {
    hw_localapi::map_transcription_error(reason.into(), &params.into()).into()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The guard's decision crosses the boundary unchanged, for the allow case
    /// and for the attack the guard exists to stop.
    #[test]
    fn the_origin_decision_crosses_the_boundary() {
        let loopback = HwLocalApiOriginHeaders {
            host: Some("127.0.0.1:51671".to_string()),
            origin: None,
            sec_fetch_site: None,
        };
        assert_eq!(
            local_api_check_origin(loopback, 51671),
            HwLocalApiOriginDecision::Allow
        );

        let rebound = HwLocalApiOriginHeaders {
            host: Some("attacker.com:51671".to_string()),
            origin: Some("http://attacker.com".to_string()),
            sec_fetch_site: Some("cross-site".to_string()),
        };
        assert_eq!(
            local_api_check_origin(rebound, 51671),
            HwLocalApiOriginDecision::DeniedHost
        );
        assert!(local_api_origin_decision_is_allowed(
            HwLocalApiOriginDecision::Allow
        ));
        assert!(!local_api_origin_decision_is_allowed(
            HwLocalApiOriginDecision::DeniedOrigin
        ));
    }

    /// `/health` is not special. It goes through the same guard on every
    /// platform, which is the drift #289 closes.
    #[test]
    fn health_is_guarded_like_every_other_route() {
        let rebound = HwLocalApiOriginHeaders {
            host: Some("attacker.com".to_string()),
            origin: None,
            sec_fetch_site: None,
        };
        assert!(!local_api_origin_decision_is_allowed(
            local_api_check_origin(rebound, 51671)
        ));
    }

    #[test]
    fn the_token_surface_matches_the_leaf_crate() {
        assert_eq!(local_api_token_entropy_bytes(), 32);
        let token = local_api_generate_token(vec![0u8; 32]).expect("32 bytes is the contract");
        assert_eq!(token.len(), 43);
        assert!(local_api_is_well_formed_token(token.clone()));
        assert_eq!(token, hw_localapi::generate_token(&[0u8; 32]).unwrap());

        let error = local_api_generate_token(vec![0u8; 31]).unwrap_err();
        assert!(matches!(
            error,
            HwLocalApiTokenError::WrongEntropyLength {
                expected: 32,
                actual: 31
            }
        ));
        assert_eq!(local_api_token_fingerprint(String::new()).len(), 64);
    }

    #[test]
    fn the_bearer_check_crosses_the_boundary() {
        let token = local_api_generate_token(vec![9u8; 32]).expect("32 bytes is the contract");
        assert!(local_api_authorize(
            Some(format!("Bearer {token}")),
            token.clone()
        ));
        assert!(!local_api_authorize(
            Some("Bearer nope".to_string()),
            token.clone()
        ));
        assert!(!local_api_authorize(None, token.clone()));
        // The empty-credential gap stays closed across the boundary.
        assert!(!local_api_authorize(
            Some("Bearer ".to_string()),
            String::new()
        ));
    }

    /// The closed enum survives the mirror: 14 codes, each round-tripping
    /// through its wire value in both directions.
    #[test]
    fn the_error_code_enum_stays_closed_at_fourteen() {
        let codes = local_api_all_error_codes();
        assert_eq!(codes.len(), 14);
        for code in codes {
            let wire = local_api_error_code_wire_value(code);
            assert_eq!(local_api_error_code_from_wire_value(wire), Some(code));
        }
        // The four Linux emitted outside the set, plus the declared-and-unused
        // one, must all fail to parse.
        for value in [
            "PAYLOAD_TOO_LARGE",
            "CANCELLED",
            "UNAUTHORIZED",
            "RECORDING_NOT_FOUND",
            "INTERNAL_ERROR",
        ] {
            assert_eq!(
                local_api_error_code_from_wire_value(value.to_string()),
                None,
                "{value} must not be in the closed set"
            );
        }
    }

    /// The status codes are the contract. A business failure is 200 whatever
    /// the code says; only the three protocol cases are not.
    #[test]
    fn the_failure_statuses_are_the_contract() {
        for code in local_api_all_error_codes() {
            let failure = local_api_business_failure(code, "x".to_string(), None);
            assert_eq!(failure.http_status, 200, "{code:?} should be 200");
        }
        assert_eq!(
            local_api_bad_request_failure("x".to_string(), None).http_status,
            400
        );
        assert_eq!(local_api_unauthorized_failure(None).http_status, 401);
        assert_eq!(local_api_forbidden_origin_failure().http_status, 403);
    }

    /// The forbidden response is macOS's, verbatim — message, hint and code.
    #[test]
    fn the_forbidden_response_is_the_macos_one() {
        let failure = local_api_forbidden_origin_failure();
        assert_eq!(failure.code, HwLocalApiErrorCode::InvalidRequest);
        assert_eq!(
            failure.message,
            "Request rejected: Host/Origin not permitted."
        );
        assert_eq!(
            failure.hint.as_deref(),
            Some("The Local API only serves loopback clients on 127.0.0.1/localhost.")
        );
        assert!(failure.json.contains(r#""code":"INVALID_REQUEST""#));
        assert!(failure.json.starts_with(r#"{"ok":false,"error":{"#));
    }

    /// The two caps cross the boundary as the .NET numbers, and the invariant
    /// `PortableLocalApiOptions.Validate()` asserts (`PortableLocalApi.cs:27`)
    /// holds on this side too. A head reads these instead of writing
    /// `52_428_800` into its own server config, which is how the three heads
    /// stay on one contract.
    #[test]
    fn the_size_caps_are_the_dotnet_numbers() {
        assert_eq!(local_api_max_request_bytes(), 52_428_800);
        assert_eq!(local_api_max_upload_bytes(), 50_331_648);
        assert!(local_api_max_upload_bytes() <= local_api_max_request_bytes());
        assert_eq!(
            local_api_max_request_bytes(),
            hw_localapi::MAX_REQUEST_BYTES
        );
        assert_eq!(local_api_max_upload_bytes(), hw_localapi::MAX_UPLOAD_BYTES);
    }

    /// The base64 threshold is `((long)MaxUploadBytes + 2) / 3 * 4`, the
    /// expansion `PortableLocalApi.cs:244` computes inline — evaluated against
    /// the *upload* cap, not the request cap. A head that used the other cap
    /// would accept payloads its sibling rejects.
    ///
    /// `manual_div_ceil` is allowed on purpose: the line is a transcription of
    /// the C# expression so a reader can diff it against that file. Rewriting
    /// it as `div_ceil` would make it agree with the implementation by
    /// construction.
    #[test]
    #[allow(clippy::manual_div_ceil)]
    fn the_base64_threshold_crosses_the_boundary() {
        let expected = (local_api_max_upload_bytes() + 2) / 3 * 4;
        assert_eq!(local_api_max_base64_length_for_upload(), expected);
        assert_eq!(local_api_max_base64_length_for_upload(), 67_108_864);
        assert!(local_api_max_base64_length_for_upload() > local_api_max_upload_bytes());

        // Note the shape of the shipped numbers, because it is surprising and
        // it is .NET's shape too: base64 expands by 4/3, and 48 MiB expands to
        // 64 MiB — which is *more* than the 50 MiB request cap. So with the
        // default options the base64 length pre-check can never fire on its
        // own: any string long enough to trip it is in a body the request cap
        // already refused. It is not dead code — `PortableLocalApiOptions`
        // lets a host lower `MaxRequestBytes` (the .NET test fixture uses
        // 4096), and it is the check that keeps the decode from running on a
        // head whose request cap is looser or absent. But at these defaults
        // the request cap subsumes the whole base64 path: 50 MiB of characters
        // decode to at most 37.5 MiB, under the 48 MiB upload cap, so the
        // post-decode check cannot fire either. The upload cap earns its keep
        // on the multipart `audio` part, which is bytes rather than base64.
        assert!(local_api_max_base64_length_for_upload() > local_api_max_request_bytes());
    }

    /// Both size failures are HTTP 200 + `INVALID_REQUEST`, with the .NET
    /// message strings verbatim.
    ///
    /// The 200 is load-bearing and is a deliberate departure from #375's
    /// suggested 413: `PAYLOAD_TOO_LARGE` is one of the four codes outside the
    /// closed 14, and emitting it makes the whole envelope undecodable for a
    /// client sharing the macOS `Codable` enum. This test is the guard on that
    /// decision, and it asserts the code stays unparseable.
    #[test]
    fn the_size_failures_are_business_failures_with_the_dotnet_messages() {
        let request = local_api_request_too_large_failure();
        assert_eq!(request.http_status, 200);
        assert_eq!(request.code, HwLocalApiErrorCode::InvalidRequest);
        assert_eq!(request.message, "Request exceeds the configured limit.");
        assert_eq!(request.hint, None);
        assert_eq!(
            request.json,
            r#"{"ok":false,"error":{"code":"INVALID_REQUEST","message":"Request exceeds the configured limit."}}"#
        );

        let upload = local_api_upload_too_large_failure();
        assert_eq!(upload.http_status, 200);
        assert_eq!(upload.code, HwLocalApiErrorCode::InvalidRequest);
        assert_eq!(upload.message, "Audio exceeds the configured upload limit.");
        assert_eq!(upload.hint, None);
        assert_eq!(
            upload.json,
            r#"{"ok":false,"error":{"code":"INVALID_REQUEST","message":"Audio exceeds the configured upload limit."}}"#
        );

        assert_eq!(
            local_api_error_code_from_wire_value("PAYLOAD_TOO_LARGE".to_string()),
            None
        );
    }

    /// The three exported numbers are ordered the way every head's three
    /// comparisons need them to be.
    ///
    /// This used to assert `exceeds_request_limit(cap)` /
    /// `exceeds_request_limit(cap + 1)` against three `hw_localapi` predicates.
    /// Those were deleted in review: no head could call them — macOS compares
    /// against a `limit` parameter and the .NET head against a per-host
    /// `options.MaxRequestBytes` — so they pinned a comparison that shipped
    /// nowhere. The boundary itself is now pinned where the comparison lives:
    /// `exactlyTheCapIsAccepted` in `LocalAPIBodyLimitTests.swift` and
    /// `SharedSizeLimits` in `HyperWhisper.LocalApi.Tests/Program.cs`. What is
    /// still Rust's to guarantee is the *relationship* between the values a
    /// head reads out, which is what this asserts.
    #[test]
    fn the_exported_values_are_ordered_for_the_heads() {
        let request = local_api_max_request_bytes();
        let upload = local_api_max_upload_bytes();
        let encoded = local_api_max_base64_length_for_upload();

        // An upload at its cap is always an acceptable request, so an oversized
        // upload is never reported as an oversized request instead.
        assert!(upload <= request);
        // The pre-decode ceiling is above the decoded cap, so the cheap check
        // never refuses a payload the expensive one would have taken.
        assert!(encoded > upload);
        // None of them is zero, which would refuse every request ever sent.
        assert_ne!(request, 0);
        assert_ne!(upload, 0);
        assert_ne!(encoded, 0);
    }

    /// The alias union crosses the boundary, including the two spellings macOS
    /// does not accept today (`qwen3_asr`, `qwen`) and the nine that are
    /// macOS-only capabilities (`nemotron*`, `apple*`).
    #[test]
    fn the_engine_alias_union_crosses_the_boundary() {
        for (alias, expected) in [
            ("whisper", HwLocalApiEngineId::WhisperLocal),
            ("WhisperLocal", HwLocalApiEngineId::WhisperLocal),
            ("  libwhisper  ", HwLocalApiEngineId::WhisperLocal),
            ("PARAKEET", HwLocalApiEngineId::Parakeet),
            ("nemotron-asr", HwLocalApiEngineId::Nemotron),
            ("nemotronlocal", HwLocalApiEngineId::Nemotron),
            ("qwen3_asr", HwLocalApiEngineId::Qwen3Asr),
            ("qwen", HwLocalApiEngineId::Qwen3Asr),
            ("speech-analyzer", HwLocalApiEngineId::AppleSpeech),
            ("apple", HwLocalApiEngineId::AppleSpeech),
        ] {
            assert_eq!(
                local_api_resolve_engine_alias(alias.to_string()),
                Some(expected),
                "{alias}"
            );
        }
    }

    /// The five ids and their wire labels, and the round trip between them.
    ///
    /// The round trip is the point: Windows and the portable head answer
    /// `qwen3_asr`, macOS does not accept that spelling, so echoing one head's
    /// `engine` back to another is an `Unknown engine` error today.
    #[test]
    fn every_engine_wire_label_round_trips() {
        let ids = local_api_all_engine_ids();
        assert_eq!(ids.len(), 5);
        let labels: Vec<String> = ids
            .iter()
            .map(|id| local_api_engine_wire_label(*id))
            .collect();
        assert_eq!(
            labels,
            vec![
                "whisperLocal",
                "parakeet",
                "nemotron",
                "qwen3Asr",
                "appleSpeech"
            ]
        );
        for id in ids {
            assert_eq!(
                local_api_resolve_engine_alias(local_api_engine_wire_label(id)),
                Some(id)
            );
        }
    }

    /// `None` is "not one of the five", not "rejected". The cloud half of the
    /// documented field is `hw-catalog`'s `CloudSttCatalog`, and a head tries it
    /// next — re-implementing it here would be the second catalog #356 exists to
    /// stop.
    #[test]
    fn cloud_selectors_do_not_resolve_to_an_engine_id() {
        for cloud in [
            "cloud",
            "hyperwhisper",
            "openai",
            "meta",
            "googlespeech",
            "",
        ] {
            assert_eq!(
                local_api_resolve_engine_alias(cloud.to_string()),
                None,
                "{cloud}"
            );
        }
    }

    /// The required set crosses as the documented seven, and the classifier
    /// agrees with it — a required key that classified as `Unknown` would mean a
    /// conformant body carried a key this crate then told the head to ignore.
    #[test]
    fn the_required_mode_keys_are_the_documented_seven() {
        assert_eq!(
            local_api_required_mode_keys(),
            vec![
                "name",
                "preset",
                "language",
                "model",
                "punctuation",
                "capitalization",
                "profanityFilter"
            ]
        );
        for key in local_api_required_mode_keys() {
            assert_eq!(
                local_api_mode_key_classification(key.clone()),
                HwLocalApiModeKeyClass::Known,
                "{key}"
            );
        }
        assert!(local_api_missing_required_mode_keys(local_api_required_mode_keys()).is_empty());
        // The Windows create body that works today.
        assert_eq!(
            local_api_missing_required_mode_keys(vec!["name".to_string()]).len(),
            6
        );
    }

    /// The four buckets, and the verdict that matters: an unrecognised key is
    /// `Unknown`, which means *ignore*. The portable head answers HTTP 400 for
    /// one today.
    #[test]
    fn mode_keys_classify_across_the_boundary() {
        for (key, expected) in [
            ("name", HwLocalApiModeKeyClass::Known),
            ("sortOrder", HwLocalApiModeKeyClass::Known),
            ("customVocabulary", HwLocalApiModeKeyClass::PlatformOnly),
            ("providerType", HwLocalApiModeKeyClass::PlatformOnly),
            ("id", HwLocalApiModeKeyClass::ReadOnly),
            ("createdDate", HwLocalApiModeKeyClass::ReadOnly),
            ("notAField", HwLocalApiModeKeyClass::Unknown),
            ("Name", HwLocalApiModeKeyClass::Unknown),
        ] {
            assert_eq!(
                local_api_mode_key_classification(key.to_string()),
                expected,
                "{key}"
            );
        }
    }

    fn patch_input() -> HwLocalApiModeValidationInput {
        HwLocalApiModeValidationInput {
            operation: HwLocalApiModeOperation::Patch,
            present_keys: Vec::new(),
            name: None,
            language: None,
            preset: None,
            post_processing_mode: None,
            sort_order: None,
            user_system_prompt: None,
            gemini_custom_prompt: None,
            custom_vocabulary: None,
        }
    }

    /// Every mode validation failure is 200 + `INVALID_REQUEST` — a missing
    /// required key as much as a value bound. No new code, and no 413/404/500
    /// anywhere.
    #[test]
    fn mode_validation_keeps_the_status_contract() {
        let incomplete = HwLocalApiModeValidationInput {
            operation: HwLocalApiModeOperation::Create,
            present_keys: vec!["name".to_string()],
            ..patch_input()
        };
        let failure = local_api_validate_mode(incomplete).expect("incomplete create");
        assert_eq!(failure.http_status, 200);
        assert_eq!(failure.code, HwLocalApiErrorCode::InvalidRequest);
        assert!(failure.message.contains("preset"));

        for input in [
            HwLocalApiModeValidationInput {
                name: Some("  ".to_string()),
                ..patch_input()
            },
            HwLocalApiModeValidationInput {
                sort_order: Some(32768),
                ..patch_input()
            },
            HwLocalApiModeValidationInput {
                post_processing_mode: Some(3),
                ..patch_input()
            },
        ] {
            let failure = local_api_validate_mode(input).expect("out of range");
            assert_eq!(failure.http_status, 200);
            assert_eq!(failure.code, HwLocalApiErrorCode::InvalidRequest);
        }

        assert!(local_api_validate_mode(patch_input()).is_none());
    }

    /// `i64` in, not `i32`. This is the value that is an unhandled HTTP 500 on
    /// the portable head today, because `GetInt32()` throws `FormatException`
    /// and nothing in that middleware catches it.
    #[test]
    fn an_out_of_int32_sort_order_crosses_as_a_failure_not_a_crash() {
        for order in [99_999_999_999_i64, i64::MAX, i64::MIN] {
            let failure = local_api_validate_mode(HwLocalApiModeValidationInput {
                sort_order: Some(order),
                ..patch_input()
            })
            .expect("out of Int16 range");
            assert_eq!(failure.http_status, 200);
            assert!(failure.message.contains("-32768 and 32767"));
        }
    }

    /// Length is Unicode scalar values, so a 60-emoji name is 60 and not the
    /// 120 UTF-16 units the portable head counts today.
    #[test]
    fn mode_length_crosses_as_scalar_values() {
        assert!(local_api_validate_mode(HwLocalApiModeValidationInput {
            name: Some("😀".repeat(60)),
            ..patch_input()
        })
        .is_none());
        assert!(local_api_validate_mode(HwLocalApiModeValidationInput {
            name: Some("😀".repeat(101)),
            ..patch_input()
        })
        .is_some());
    }

    /// The collision rule crosses whole: the key, the predicate and the
    /// envelope.
    #[test]
    fn the_collision_rule_crosses_the_boundary() {
        assert_eq!(
            local_api_mode_name_comparison_key("  Work Mode ".to_string()).as_deref(),
            Some("work mode")
        );
        assert_eq!(local_api_mode_name_comparison_key("   ".to_string()), None);

        let existing = vec!["Work".to_string(), "  Mail  ".to_string()];
        assert!(local_api_mode_name_conflict(
            "  WORK ".to_string(),
            existing.clone()
        ));
        assert!(!local_api_mode_name_conflict(
            "Meeting".to_string(),
            existing.clone()
        ));
        // An empty candidate is validate_mode's failure, not a collision.
        assert!(!local_api_mode_name_conflict("  ".to_string(), existing));

        let create =
            local_api_mode_name_taken_failure("Work".to_string(), HwLocalApiModeOperation::Create);
        assert_eq!(create.http_status, 200);
        assert_eq!(create.code, HwLocalApiErrorCode::ModeNameTaken);
        assert_eq!(create.message, "A mode named 'Work' already exists");
        assert_eq!(
            create.hint.as_deref(),
            Some("Choose a different name or PATCH the existing mode instead.")
        );
        let patch =
            local_api_mode_name_taken_failure("Work".to_string(), HwLocalApiModeOperation::Patch);
        assert_eq!(patch.hint, None);
        assert!(patch.json.contains(r#""code":"MODE_NAME_TAKEN""#));
    }

    /// The transcription table crosses the boundary with its status, its code
    /// and its interpolated text intact — including the hint slot the head
    /// fills and the twenty-two rows where it does not.
    #[test]
    fn the_transcription_table_crosses_the_boundary() {
        fn params() -> HwLocalApiTranscriptionFailureParams {
            HwLocalApiTranscriptionFailureParams {
                provider: None,
                detail: None,
                model: None,
                limit_bytes: None,
                http_status: None,
                retry_after_seconds: None,
                hint: None,
            }
        }

        let byok = local_api_map_transcription_error(
            HwLocalApiTranscriptionFailureReason::ApiKeyMissing,
            HwLocalApiTranscriptionFailureParams {
                provider: Some("OpenAI".to_string()),
                hint: Some("Add the API key in Settings → API Keys.".to_string()),
                ..params()
            },
        );
        assert_eq!(byok.http_status, 200);
        assert_eq!(byok.code, HwLocalApiErrorCode::MissingApiKey);
        assert_eq!(byok.message, "API key for OpenAI is missing.");
        assert_eq!(
            byok.hint.as_deref(),
            Some("Add the API key in Settings → API Keys.")
        );
        assert!(byok.json.contains(r#""code":"MISSING_API_KEY""#));

        // A row whose hint is the crate's: the head's hint does not displace it.
        let network = local_api_map_transcription_error(
            HwLocalApiTranscriptionFailureReason::NetworkUnavailable,
            HwLocalApiTranscriptionFailureParams {
                detail: Some("timed out after 30s".to_string()),
                hint: Some("Something the head made up.".to_string()),
                ..params()
            },
        );
        assert_eq!(network.code, HwLocalApiErrorCode::EngineUnavailable);
        assert_eq!(network.message, "Network error: timed out after 30s");
        assert_eq!(
            network.hint.as_deref(),
            Some("Check connectivity and retry.")
        );

        // The numeric slots survive the boundary as numbers, not as text.
        let too_large = local_api_map_transcription_error(
            HwLocalApiTranscriptionFailureReason::AudioFileTooLarge,
            HwLocalApiTranscriptionFailureParams {
                provider: Some("OpenAI".to_string()),
                limit_bytes: Some(26_214_400),
                ..params()
            },
        );
        assert_eq!(too_large.code, HwLocalApiErrorCode::InvalidRequest);
        assert_eq!(
            too_large.message,
            "Audio file exceeds OpenAI limit (26214400 bytes)."
        );

        // The portable head's four cases, which today collapse into one.
        assert_eq!(
            local_api_map_transcription_error(
                HwLocalApiTranscriptionFailureReason::Cancelled,
                params()
            )
            .code,
            HwLocalApiErrorCode::Timeout
        );
        assert_eq!(
            local_api_map_transcription_error(
                HwLocalApiTranscriptionFailureReason::EngineUnavailable,
                params()
            )
            .message,
            "The transcription engine is unavailable."
        );
    }

    #[test]
    fn the_json_field_carries_the_whole_envelope() {
        let failure = local_api_business_failure(
            HwLocalApiErrorCode::ModeNotFound,
            "Mode not found.".to_string(),
            None,
        );
        assert_eq!(
            failure.json,
            r#"{"ok":false,"error":{"code":"MODE_NOT_FOUND","message":"Mode not found."}}"#
        );
        let hinted = local_api_business_failure(
            HwLocalApiErrorCode::Timeout,
            "Gone.".to_string(),
            Some("Try again.".to_string()),
        );
        assert_eq!(
            hinted.json,
            r#"{"ok":false,"error":{"code":"TIMEOUT","message":"Gone.","hint":"Try again."}}"#
        );
    }
}
