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
