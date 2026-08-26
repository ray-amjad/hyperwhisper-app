//! The value types a live session is built from and answers with.
//!
//! Every type here is a plain value: a session takes a [`LiveConfig`] and hands
//! back a [`LiveConnect`] descriptor, [`LiveFrame`]s to send, [`StopStep`]s to
//! run and [`LiveEvent`]s parsed out of what arrived. Nothing here opens a
//! socket, reads a clock or touches audio samples.

use crate::contract::Header;

/// Everything a live session needs to build its connection.
///
/// Mirrors `shared-dotnet`'s `LiveTranscriptionConfig` field for field, with two
/// differences that the shape of a shared core forces:
///
/// - `vocabulary` is a **term list**, not the comma-joined string the two .NET
///   heads and macOS pass around. Joining is a per-provider wire decision (xAI
///   repeats `keyterm=`, HyperWhisper Cloud sends one comma-joined
///   `vocabulary=`), so the core takes the terms and each protocol decides.
/// - `base_url` exists so a head can point HyperWhisper Cloud at its own
///   backend. macOS's `#if DEBUG` build talks to `transcribe-staging-v2`, and a
///   hardcoded production host in the core would silently re-point it at
///   production. `None` means the production host. Every other provider ignores
///   it: their endpoints are the vendor's and are not ours to move.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LiveConfig {
    pub provider: super::LiveProvider,
    pub api_key: Option<String>,
    pub license_key: Option<String>,
    pub device_id: Option<String>,
    pub language: Option<String>,
    pub vocabulary: Vec<String>,
    pub model: Option<String>,
    pub fast_formatting: bool,
    pub base_url: Option<String>,
}

impl LiveConfig {
    /// A config for `provider` with no credential and no options set.
    pub fn new(provider: super::LiveProvider) -> Self {
        Self {
            provider,
            api_key: None,
            license_key: None,
            device_id: None,
            language: None,
            vocabulary: Vec::new(),
            model: None,
            fast_formatting: false,
            base_url: None,
        }
    }
}

/// Everything the platform needs to open the socket, answered once.
///
/// There is deliberately no `drain_timeout_ms` here. The ordered
/// [`StopStep`] list carries every wait the stop path performs, including the
/// event waits a flat timeout cannot express; a second timeout on the connect
/// descriptor would be a second source of truth for the same behaviour. See the
/// stop-sequence section of the module docs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LiveConnect {
    /// The full `wss://` URL including the query string.
    pub url: String,
    /// Handshake headers, in the order the shipped strategies set them.
    ///
    /// Client-identity headers (`X-HyperWhisper-Platform`, `X-HyperWhisper-Version`)
    /// are **not** here: the core does not know which platform it is linked
    /// into, or what the host app's version is. Each head adds its own on top,
    /// as it does today.
    pub headers: Vec<Header>,
    /// `Sec-WebSocket-Protocol` values. Only Deepgram uses them — it carries the
    /// API key as the second subprotocol rather than as a header.
    pub subprotocols: Vec<String>,
    /// The PCM sample rate the capture graph must be configured to. Always
    /// equal to [`super::required_sample_rate`] for the provider; repeated here
    /// so a connecting caller needs one call, not two.
    pub sample_rate: u32,
    /// How a PCM chunk becomes a websocket frame. See [`AudioFraming`].
    pub framing: AudioFraming,
    /// Frames to send immediately after the socket opens, in order.
    pub start_frames: Vec<LiveFrame>,
    /// Whether the session is live the moment the handshake completes, or only
    /// once the provider sends its own session-started message.
    ///
    /// Only Deepgram is `true`: its one session-shaped frame (`Metadata`) does
    /// not arrive until after audio has been sent, so waiting for it would
    /// deadlock the first chunk.
    pub session_starts_on_open: bool,
}

/// How a PCM chunk becomes a websocket frame.
///
/// This descriptor is the whole reason audio never crosses FFI. The core says
/// *how* to wrap a chunk once, at connect time, and the platform does the
/// base64 and the concatenation on the bytes it already holds. See the
/// "audio never crosses this boundary" section of the module docs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AudioFraming {
    /// Send the PCM bytes as a binary frame, unchanged.
    Binary,
    /// Send `prefix + base64(pcm) + suffix` as a text frame.
    ///
    /// Both JSON framers in the five protocols are a single mid-string
    /// substitution into a fixed envelope, so two literals describe them
    /// exactly. Nothing else in either envelope varies per chunk.
    Base64Json { prefix: String, suffix: String },
}

/// One frame to put on the wire.
///
/// `data` is a `String` because every frame the core produces is JSON text —
/// the only binary frames in these protocols are audio, which the core never
/// sees. `binary` exists so the platform's mapping onto its own message-type
/// enum is total and explicit rather than a hardcoded `Text` at the adapter;
/// it is `false` on every frame this module emits today.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LiveFrame {
    pub data: String,
    pub binary: bool,
}

impl LiveFrame {
    /// A text frame carrying `data`.
    pub fn text(data: impl Into<String>) -> Self {
        Self {
            data: data.into(),
            binary: false,
        }
    }
}

/// One step of the stop path, run in order.
///
/// A flat frame list plus one drain timeout cannot express these protocols.
/// Deepgram needs `Finalize` → **wait 500 ms** → `CloseStream` (sending both
/// back to back loses the finalized tail), and HyperWhisper Cloud and xAI both
/// wait on an *event* rather than a duration — the `session_complete` that
/// carries `credits_used`, which is billing data. Ordering and the
/// event-vs-duration distinction are both load-bearing, so the core answers an
/// ordered list.
///
/// The arms match Windows' shipped `StreamingStopAction` one for one, so a head
/// that already runs stop steps maps this by renaming.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StopStep {
    /// Send a text frame.
    SendText { text: String },
    /// Sleep for `ms`, unconditionally.
    Wait { ms: u64 },
    /// Wait until the provider's session-complete event arrives, or `timeout_ms`
    /// elapses — whichever comes first. Returns immediately if it already did.
    WaitForSessionComplete { timeout_ms: u64 },
    /// Close the socket.
    Close,
}

/// What one parsed provider message means.
///
/// This is deliberately the macOS `StreamingProviderEvent` superset, not
/// `shared-dotnet`'s six-case `LiveProtocolEvent`. The extra payload —
/// `session_id`, `duration_seconds`, `credits_used`, and the `Warning` and
/// `Metadata` arms — exists on the wire for providers that send it, and a core
/// that dropped it would make the macOS client impossible to move here later.
/// A consumer that does not want an arm ignores it; a consumer that needs one
/// the core never produced has nowhere to go.
#[derive(Debug, Clone, PartialEq)]
pub enum LiveEvent {
    /// The provider accepted the session. `session_id` is `None` for the
    /// providers that do not name their sessions (ElevenLabs, xAI).
    SessionStarted { session_id: Option<String> },
    /// Interim text that will be revised. Replaces the previous partial.
    PartialTranscript { text: String },
    /// Committed text. Appends to the transcript.
    FinalTranscript { text: String },
    /// xAI's `transcript.done` when it carries new text: the last final and the
    /// end of the session in one frame. Splitting it into two events would let
    /// a client that stops on `SessionComplete` drop the trailing words.
    FinalTranscriptAndSessionComplete {
        text: String,
        duration_seconds: f64,
        credits_used: f64,
    },
    /// The provider finished. `credits_used` is billing data on HyperWhisper
    /// Cloud; providers that do not bill through us report `0.0`.
    SessionComplete {
        duration_seconds: f64,
        credits_used: f64,
    },
    /// The provider reported a failure. Feed `message` to
    /// [`super::classify_error_message`] to learn whether reconnecting can help.
    Error { message: String },
    /// A non-fatal notice — only HyperWhisper Cloud sends these.
    Warning { message: String },
    /// A frame worth logging and nothing else. Carries the raw JSON.
    Metadata { raw: String },
    /// A frame with no meaning for the transcript. The great majority.
    Ignore,
}

/// Why a session could not produce a connection descriptor.
///
/// One arm on purpose. Everything else that can go wrong on a live connection —
/// DNS, TLS, a refused upgrade, a mid-session close — is transport, and
/// transport stays in the head. The only failure the core can see is that the
/// caller handed it a config it cannot build a URL from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum LiveError {
    /// No usable credential: a blank API key, or (HyperWhisper Cloud) neither a
    /// license key nor a device id.
    #[error("A streaming provider credential is required.")]
    MissingCredential,
}

// ===========================================================================
// URL building
// ===========================================================================

/// Percent-encode a query component the way .NET's `Uri.EscapeDataString` does:
/// RFC 3986 strictly, leaving only `A-Z a-z 0-9 - . _ ~` literal.
///
/// This is **not** [`crate::providers::hyperwhisper_cloud::encode_query`]'s
/// encoder and must not be folded into it. That one reproduces macOS
/// `URLComponents` byte for byte — it leaves `, / : ; ? @ ! $ ' ( ) * +`
/// literal — because the batch path's verified reference platform is macOS.
/// The live path's consumers are the two .NET heads, both of which call
/// `Uri.EscapeDataString`, and macOS's live strategies are explicitly out of
/// scope for this move (issue #281). Encoding a vocabulary term or a license
/// key one way when the shipped client encodes it the other changes the bytes
/// on a live wire, so the live path takes the .NET rule. Both forms decode
/// identically at every provider; the point is to match the client being
/// replaced, not to pick a favourite.
pub(super) fn escape_data(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                out.push(b as char)
            }
            _ => {
                out.push('%');
                out.push(hex_upper(b >> 4));
                out.push(hex_upper(b & 0x0f));
            }
        }
    }
    out
}

fn hex_upper(nibble: u8) -> char {
    match nibble {
        0..=9 => (b'0' + nibble) as char,
        _ => (b'A' + (nibble - 10)) as char,
    }
}

/// Accumulates `name=value` query pairs in insertion order.
///
/// Order is asserted in the tests against the shipped URLs, so a pair added in
/// the wrong place is a test failure rather than a silent wire change.
#[derive(Default)]
pub(super) struct Query(Vec<String>);

impl Query {
    /// Append `name=<escaped value>`.
    pub(super) fn push(&mut self, name: &str, value: &str) {
        self.0.push(format!("{name}={}", escape_data(value)));
    }

    /// Append a pre-formatted literal such as `encoding=linear16`, with no
    /// escaping. Only for constants written in this crate.
    pub(super) fn push_literal(&mut self, pair: &str) {
        self.0.push(pair.to_string());
    }

    /// `?a=1&b=2`, or the empty string when nothing was added.
    pub(super) fn suffix(&self) -> String {
        if self.0.is_empty() {
            String::new()
        } else {
            format!("?{}", self.0.join("&"))
        }
    }
}

/// A blank-safe credential read: `None` for missing, empty or whitespace-only.
///
/// Every shipped strategy guards its credential with
/// `IsNullOrWhiteSpace` / `isEmpty`, and a whitespace-only key is a
/// misconfiguration, not a credential.
pub(super) fn present(value: &Option<String>) -> Option<&str> {
    match value {
        Some(v) if !v.trim().is_empty() => Some(v.as_str()),
        _ => None,
    }
}

// ===========================================================================
// JSON reading
// ===========================================================================

/// A string property, or `None` when absent or not a string.
pub(super) fn str_field<'a>(root: &'a serde_json::Value, name: &str) -> Option<&'a str> {
    root.get(name).and_then(serde_json::Value::as_str)
}

/// A boolean property, or `None` when absent or not a boolean.
pub(super) fn bool_field(root: &serde_json::Value, name: &str) -> Option<bool> {
    root.get(name).and_then(serde_json::Value::as_bool)
}

/// A numeric property as `f64`, defaulting to `0.0`. Matches the `?? 0` every
/// shipped parser applies to `duration_seconds` / `credits_used`.
pub(super) fn num_field(root: &serde_json::Value, name: &str) -> f64 {
    root.get(name).and_then(serde_json::Value::as_f64).unwrap_or(0.0)
}

/// A non-empty, trimmed string property, or `None`.
pub(super) fn text_field<'a>(root: &'a serde_json::Value, name: &str) -> Option<&'a str> {
    match str_field(root, name) {
        Some(v) if !v.trim().is_empty() => Some(v),
        _ => None,
    }
}

/// The human wording out of a provider's error frame, whichever of the three
/// shapes it uses: `{"message":…}` (HyperWhisper Cloud, xAI),
/// `{"error":{"message":…}}` (OpenAI Realtime) or a bare `{"error":"…"}`.
///
/// The message is what [`super::classify_error_message`] reads. Before issue
/// #281 the `shared-dotnet` parsers reduced every one of these frames to a bare
/// failure code and discarded the wording, so a terminal "Credit balance
/// exhausted" from the default provider looked exactly like a transient outage
/// and drove a reconnect that could only fail the same way.
///
/// `fallback` is the provider's own generic sentence, used when the frame
/// carries no wording at all. It is never empty: an [`LiveEvent::Error`] with an
/// empty message reaches a user as a blank alert, and the classifier reads an
/// unrecognised sentence and an empty one the same way (transient), so the
/// fallback costs nothing and buys a legible failure.
pub(super) fn error_message(root: &serde_json::Value, fallback: &str) -> String {
    if let Some(direct) = text_field(root, "message") {
        return direct.to_string();
    }
    let nested = match root.get("error") {
        Some(serde_json::Value::String(s)) => s.trim().to_string(),
        Some(obj @ serde_json::Value::Object(_)) => {
            text_field(obj, "message").unwrap_or_default().to_string()
        }
        _ => String::new(),
    };
    if nested.is_empty() {
        fallback.to_string()
    } else {
        nested
    }
}

/// The longest common-prefix delta between what was already committed and a
/// newly arrived full transcript, or `None` when there is nothing new.
///
/// The prefix-delta algorithm the issue counted five copies of. Two variants
/// existed: this one — xAI's, on a single running transcript — which also
/// handles the *shrinking* case, and OpenAI's per-item variant, which does not
/// (see [`super::openai`] for why the two cannot be merged).
///
/// - No previous text: the whole thing is new.
/// - `next` extends `previous`: the trimmed suffix is new (`None` if it trims
///   to nothing).
/// - `previous` extends `next`: the provider walked its own transcript back.
///   Nothing is new and `previous` is left alone — replacing it would re-emit
///   the retracted tail on the next frame.
/// - Neither is a prefix of the other: the provider started a fresh utterance.
///   Append it with a separating space and emit it whole.
pub(super) fn commit_delta(previous: &mut String, next: &str) -> Option<String> {
    let normalized = next.trim();
    if normalized.is_empty() {
        return None;
    }
    if previous.is_empty() {
        *previous = normalized.to_string();
        return Some(normalized.to_string());
    }
    let extended = normalized
        .strip_prefix(previous.as_str())
        .map(|suffix| suffix.trim().to_string());
    if let Some(suffix) = extended {
        *previous = normalized.to_string();
        return if suffix.is_empty() { None } else { Some(suffix) };
    }
    if previous.starts_with(normalized) {
        return None;
    }
    previous.push(' ');
    previous.push_str(normalized);
    Some(normalized.to_string())
}
