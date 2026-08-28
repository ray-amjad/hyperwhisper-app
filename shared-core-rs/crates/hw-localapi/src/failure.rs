//! The failure envelope: the closed error-code enum, and the rule that decides
//! which HTTP status carries it.
//!
//! # The closed enum
//!
//! `mintlify-help/api-reference/local-api/overview.mdx` documents 14 codes and
//! macOS declares them as a Swift `Codable` enum. A `Codable` enum is closed:
//! **a client that shares the macOS decoder fails to decode the whole envelope**
//! when it meets a code that is not in the list — it does not get an unknown
//! code, it gets nothing. Linux emitted four codes outside the list
//! (`PAYLOAD_TOO_LARGE`, `CANCELLED`, `UNAUTHORIZED`, `RECORDING_NOT_FOUND`),
//! so every one of those responses was undecodable for such a client (#289).
//!
//! [`LocalApiErrorCode`] is that closed list, and it is the only way to name a
//! code in this crate. There is no `Other(String)` variant, on purpose: adding
//! one would let a head emit a fifteenth code again and the type would stop
//! being the contract.
//!
//! # The HTTP-200 rule
//!
//! `overview.mdx:47` mandates HTTP 200 for a *business* failure — a mode that
//! does not exist, an engine that is not installed, audio that will not decode.
//! MCP wrappers cannot surface error text from a non-200 with an empty body, so
//! a 404 turns a readable "no mode with that id" into a blank failure at the
//! agent. macOS and Windows hardcode 200; Linux returned 404/413/503/408 and
//! hit exactly the failure mode the rule exists to prevent.
//!
//! [`FailureKind`] is that rule, written down. Only three things are not
//! business failures, and all three are cases where there is nothing useful for
//! a wrapper to surface:
//!
//! * [`FailureKind::BadRequest`] — 400, the request is malformed as a request
//!   (unparsable JSON, a missing required field).
//! * [`FailureKind::Unauthorized`] — 401, the credential is missing or wrong.
//! * [`FailureKind::Forbidden`] — 403, the origin guard rejected the request.
//!
//! Everything else is [`FailureKind::Business`] and carries HTTP 200.

/// The closed set of Local API error codes.
///
/// MCP wrappers map these 1:1 to client-side errors, so adding a variant is a
/// contract change: it needs the docs, the macOS `Codable` enum, the Windows
/// constants and the .NET constants in the same commit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LocalApiErrorCode {
    /// The requested model is not downloaded.
    ModelNotInstalled,
    /// No model with that id exists in the catalog.
    ModelNotFound,
    /// The transcription or post-processing engine cannot serve the request.
    EngineUnavailable,
    /// A BYOK provider has no API key configured.
    MissingApiKey,
    /// A referenced file does not exist.
    FileNotFound,
    /// A referenced file exists but cannot be read.
    FileAccessDenied,
    /// A caller-supplied path resolves outside HyperWhisper's audio folders
    /// (the path-containment guard, #740).
    FileNotAllowed,
    /// The audio could not be decoded.
    AudioDecodeFailed,
    /// Transcription ran and failed.
    TranscriptionFailed,
    /// No mode with that id.
    ModeNotFound,
    /// A mode with that name already exists.
    ModeNameTaken,
    /// The request is malformed, out of range, or self-contradictory.
    InvalidRequest,
    /// An upstream provider rate-limited the request.
    RateLimited,
    /// The operation ran out of time, or the caller went away.
    Timeout,
}

/// Every code, in the order the docs and the macOS enum list them.
///
/// The array is the enumeration the tests count and the FFI mirrors; keeping it
/// next to the enum is what makes "the enum is closed at 14" checkable rather
/// than asserted in a comment.
pub const ALL_ERROR_CODES: [LocalApiErrorCode; 14] = [
    LocalApiErrorCode::ModelNotInstalled,
    LocalApiErrorCode::ModelNotFound,
    LocalApiErrorCode::EngineUnavailable,
    LocalApiErrorCode::MissingApiKey,
    LocalApiErrorCode::FileNotFound,
    LocalApiErrorCode::FileAccessDenied,
    LocalApiErrorCode::FileNotAllowed,
    LocalApiErrorCode::AudioDecodeFailed,
    LocalApiErrorCode::TranscriptionFailed,
    LocalApiErrorCode::ModeNotFound,
    LocalApiErrorCode::ModeNameTaken,
    LocalApiErrorCode::InvalidRequest,
    LocalApiErrorCode::RateLimited,
    LocalApiErrorCode::Timeout,
];

impl LocalApiErrorCode {
    /// The exact string that goes on the wire.
    #[must_use]
    pub fn wire_value(self) -> &'static str {
        match self {
            LocalApiErrorCode::ModelNotInstalled => "MODEL_NOT_INSTALLED",
            LocalApiErrorCode::ModelNotFound => "MODEL_NOT_FOUND",
            LocalApiErrorCode::EngineUnavailable => "ENGINE_UNAVAILABLE",
            LocalApiErrorCode::MissingApiKey => "MISSING_API_KEY",
            LocalApiErrorCode::FileNotFound => "FILE_NOT_FOUND",
            LocalApiErrorCode::FileAccessDenied => "FILE_ACCESS_DENIED",
            LocalApiErrorCode::FileNotAllowed => "FILE_NOT_ALLOWED",
            LocalApiErrorCode::AudioDecodeFailed => "AUDIO_DECODE_FAILED",
            LocalApiErrorCode::TranscriptionFailed => "TRANSCRIPTION_FAILED",
            LocalApiErrorCode::ModeNotFound => "MODE_NOT_FOUND",
            LocalApiErrorCode::ModeNameTaken => "MODE_NAME_TAKEN",
            LocalApiErrorCode::InvalidRequest => "INVALID_REQUEST",
            LocalApiErrorCode::RateLimited => "RATE_LIMITED",
            LocalApiErrorCode::Timeout => "TIMEOUT",
        }
    }

    /// Parse a wire value back into the enum, or `None` when it is not one of
    /// the 14.
    ///
    /// This is what a conformance check runs: hand it every code a head emits
    /// and a `None` names a code that would break the macOS decoder.
    #[must_use]
    pub fn from_wire_value(value: &str) -> Option<LocalApiErrorCode> {
        ALL_ERROR_CODES
            .into_iter()
            .find(|code| code.wire_value() == value)
    }
}

/// Which HTTP status a failure travels on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FailureKind {
    /// A business outcome the caller can read and act on. HTTP 200.
    Business,
    /// The request is malformed as a request. HTTP 400.
    BadRequest,
    /// The bearer credential is missing or wrong. HTTP 401.
    Unauthorized,
    /// The origin guard rejected the request. HTTP 403.
    Forbidden,
}

impl FailureKind {
    /// The HTTP status code for this kind.
    #[must_use]
    pub fn http_status(self) -> u16 {
        match self {
            FailureKind::Business => 200,
            FailureKind::BadRequest => 400,
            FailureKind::Unauthorized => 401,
            FailureKind::Forbidden => 403,
        }
    }
}

/// A complete failure response: the status to send, the code, and the text.
///
/// The heads build the JSON themselves with their own encoders, so this crate
/// does not dictate key order or how a `None` hint is elided. [`Failure::to_json`]
/// is available for a head that has no typed model to reach for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Failure {
    /// Which HTTP status carries it.
    pub kind: FailureKind,
    /// The wire code, always one of the 14.
    pub code: LocalApiErrorCode,
    /// Human-readable, shown to the agent by an MCP wrapper.
    pub message: String,
    /// What to do about it, when there is something to say.
    pub hint: Option<String>,
}

impl Failure {
    /// A business failure — HTTP 200.
    #[must_use]
    pub fn business(code: LocalApiErrorCode, message: impl Into<String>) -> Failure {
        Failure {
            kind: FailureKind::Business,
            code,
            message: message.into(),
            hint: None,
        }
    }

    /// A malformed request — HTTP 400, always `INVALID_REQUEST`.
    #[must_use]
    pub fn bad_request(message: impl Into<String>) -> Failure {
        Failure {
            kind: FailureKind::BadRequest,
            code: LocalApiErrorCode::InvalidRequest,
            message: message.into(),
            hint: None,
        }
    }

    /// Attach a hint.
    #[must_use]
    pub fn with_hint(mut self, hint: impl Into<String>) -> Failure {
        self.hint = Some(hint.into());
        self
    }

    /// The HTTP status this failure travels on.
    #[must_use]
    pub fn http_status(&self) -> u16 {
        self.kind.http_status()
    }

    /// The envelope as JSON: `{"ok":false,"error":{"code":…,"message":…}}`,
    /// with `hint` present only when there is one.
    ///
    /// Key order is fixed and documented rather than sorted; no client depends
    /// on it, and JSON objects are unordered. A head with a typed model should
    /// keep using it — this exists so a head without one does not hand-roll a
    /// fifteenth spelling of the envelope.
    #[must_use]
    pub fn to_json(&self) -> String {
        let mut json = String::from(r#"{"ok":false,"error":{"code":""#);
        json.push_str(self.code.wire_value());
        json.push_str(r#"","message":"#);
        push_json_string(&mut json, &self.message);
        if let Some(hint) = &self.hint {
            json.push_str(r#","hint":"#);
            push_json_string(&mut json, hint);
        }
        json.push_str("}}");
        json
    }
}

/// The response the origin guard returns, byte for byte what macOS already
/// sends (`LocalAPIServer.swift:311-316`).
///
/// HTTP 403 carrying `INVALID_REQUEST` — **not** a new `FORBIDDEN` code. Issue
/// #289 is explicit that inventing one "is itself a contract change on the one
/// platform that ships the guard".
#[must_use]
pub fn forbidden_origin() -> Failure {
    Failure {
        kind: FailureKind::Forbidden,
        code: LocalApiErrorCode::InvalidRequest,
        message: String::from("Request rejected: Host/Origin not permitted."),
        hint: Some(String::from(
            "The Local API only serves loopback clients on 127.0.0.1/localhost.",
        )),
    }
}

/// The response a failed bearer check returns — HTTP 401 carrying
/// `INVALID_REQUEST`, matching `LocalAPIServer.swift:344-353`.
///
/// `hint` is a parameter because it names the platform's own discovery-file
/// path, which is the one part of this response that legitimately differs per
/// head. Pass `None` to send no hint.
///
/// The caller must still send `WWW-Authenticate: Bearer realm="hyperwhisper"`,
/// as macOS does; a header is not part of the envelope.
#[must_use]
pub fn unauthorized(hint: Option<String>) -> Failure {
    Failure {
        kind: FailureKind::Unauthorized,
        code: LocalApiErrorCode::InvalidRequest,
        message: String::from("Missing or invalid bearer token"),
        hint,
    }
}

/// Append a JSON string literal, escaping per RFC 8259 §7.
fn push_json_string(json: &mut String, value: &str) {
    json.push('"');
    for character in value.chars() {
        match character {
            '"' => json.push_str("\\\""),
            '\\' => json.push_str("\\\\"),
            '\n' => json.push_str("\\n"),
            '\r' => json.push_str("\\r"),
            '\t' => json.push_str("\\t"),
            '\u{08}' => json.push_str("\\b"),
            '\u{0C}' => json.push_str("\\f"),
            control if control < '\u{20}' => {
                json.push_str("\\u00");
                json.push(hex_nibble((control as u32 >> 4) as u8));
                json.push(hex_nibble(control as u32 as u8));
            }
            other => json.push(other),
        }
    }
    json.push('"');
}

/// The lower-case hex digit for the low 4 bits of `value`.
fn hex_nibble(value: u8) -> char {
    match value & 0x0F {
        digit @ 0..=9 => char::from(b'0'.saturating_add(digit)),
        letter => char::from(b'a'.saturating_add(letter.saturating_sub(10))),
    }
}

#[cfg(test)]
mod tests {
    use super::{
        forbidden_origin, unauthorized, Failure, FailureKind, LocalApiErrorCode, ALL_ERROR_CODES,
    };

    /// The wire values, spelled out once more so a rename of a Rust variant
    /// cannot silently rename a wire code.
    const WIRE_VALUES: [&str; 14] = [
        "MODEL_NOT_INSTALLED",
        "MODEL_NOT_FOUND",
        "ENGINE_UNAVAILABLE",
        "MISSING_API_KEY",
        "FILE_NOT_FOUND",
        "FILE_ACCESS_DENIED",
        "FILE_NOT_ALLOWED",
        "AUDIO_DECODE_FAILED",
        "TRANSCRIPTION_FAILED",
        "MODE_NOT_FOUND",
        "MODE_NAME_TAKEN",
        "INVALID_REQUEST",
        "RATE_LIMITED",
        "TIMEOUT",
    ];

    #[test]
    fn the_enum_is_the_documented_fourteen() {
        assert_eq!(ALL_ERROR_CODES.len(), 14);
        let wire: Vec<&str> = ALL_ERROR_CODES
            .iter()
            .map(|code| code.wire_value())
            .collect();
        assert_eq!(wire, WIRE_VALUES.to_vec());
    }

    #[test]
    fn every_code_round_trips_through_its_wire_value() {
        for code in ALL_ERROR_CODES {
            assert_eq!(
                LocalApiErrorCode::from_wire_value(code.wire_value()),
                Some(code)
            );
        }
    }

    /// The four codes Linux emitted outside the enum. Each one must fail to
    /// parse — that failure is what the Linux re-point is fixing, and a test
    /// that stops seeing it means someone widened the enum.
    #[test]
    fn the_linux_out_of_enum_codes_do_not_parse() {
        for code in [
            "PAYLOAD_TOO_LARGE",
            "CANCELLED",
            "UNAUTHORIZED",
            "RECORDING_NOT_FOUND",
            // Declared on the .NET side and never emitted; it is not one of
            // the 14 either, so it must stay unused.
            "INTERNAL_ERROR",
            // Shape mistakes a head could make.
            "",
            "invalid_request",
            "INVALID REQUEST",
            "INVALID_REQUEST ",
        ] {
            assert_eq!(
                LocalApiErrorCode::from_wire_value(code),
                None,
                "{code} must not be in the closed set"
            );
        }
    }

    #[test]
    fn business_failures_are_http_200() {
        for code in ALL_ERROR_CODES {
            assert_eq!(Failure::business(code, "x").http_status(), 200);
        }
        assert_eq!(FailureKind::Business.http_status(), 200);
        assert_eq!(FailureKind::BadRequest.http_status(), 400);
        assert_eq!(FailureKind::Unauthorized.http_status(), 401);
        assert_eq!(FailureKind::Forbidden.http_status(), 403);
    }

    /// The forbidden response is the macOS one, verbatim. Changing any of these
    /// three strings changes the wire on the one platform that already ships
    /// the guard.
    #[test]
    fn the_forbidden_response_matches_macos() {
        let failure = forbidden_origin();
        assert_eq!(failure.http_status(), 403);
        assert_eq!(failure.code, LocalApiErrorCode::InvalidRequest);
        assert_eq!(
            failure.message,
            "Request rejected: Host/Origin not permitted."
        );
        assert_eq!(
            failure.hint.as_deref(),
            Some("The Local API only serves loopback clients on 127.0.0.1/localhost.")
        );
    }

    #[test]
    fn the_unauthorized_response_matches_macos() {
        let failure = unauthorized(Some(String::from("Send Authorization: Bearer <token>.")));
        assert_eq!(failure.http_status(), 401);
        assert_eq!(failure.code, LocalApiErrorCode::InvalidRequest);
        assert_eq!(failure.message, "Missing or invalid bearer token");
        assert_eq!(unauthorized(None).hint, None);
    }

    #[test]
    fn the_json_envelope_has_the_documented_shape() {
        assert_eq!(
            Failure::business(LocalApiErrorCode::ModeNotFound, "Mode not found.").to_json(),
            r#"{"ok":false,"error":{"code":"MODE_NOT_FOUND","message":"Mode not found."}}"#
        );
        assert_eq!(
            Failure::business(LocalApiErrorCode::Timeout, "Gone.")
                .with_hint("Try again.")
                .to_json(),
            r#"{"ok":false,"error":{"code":"TIMEOUT","message":"Gone.","hint":"Try again."}}"#
        );
    }

    /// A message built from user input must not be able to break out of the
    /// JSON string — the messages carry file names and mode names.
    #[test]
    fn the_json_encoder_escapes_hostile_text() {
        let failure = Failure::business(
            LocalApiErrorCode::InvalidRequest,
            "quote \" backslash \\ newline \n tab \t bell \u{7} ünïcode 😀",
        );
        assert_eq!(
            failure.to_json(),
            r#"{"ok":false,"error":{"code":"INVALID_REQUEST","message":"quote \" backslash \\ newline \n tab \t bell \u0007 ünïcode 😀"}}"#
        );
        // And the same for the hint.
        assert!(Failure::bad_request("x")
            .with_hint("a\"b")
            .to_json()
            .contains(r#""hint":"a\"b""#));
    }
}
