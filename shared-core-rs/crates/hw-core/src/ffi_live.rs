//! UniFFI surface for the live-streaming module (`hw_net::live`).
//!
//! Mirrors the `live` types as UniFFI enums and exposes the seven free functions
//! all three heads call. Everything here is session-free on purpose: Windows
//! reads [`live_supports_vocabulary`] on its streaming settings page with no
//! credential and no open socket, so a capability that needed a live object
//! would be unusable at the one call site that most wants it.
//!
//! Every function name is `live_`-prefixed to keep the flat UniFFI namespace
//! readable next to the STT and LLM builders.

use crate::ffi_net::Header;
use hw_net::live as lv;

// ===========================================================================
// Types
// ===========================================================================

/// The six websocket transcription providers. Mirrors `lv::LiveProvider`.
///
/// Local engines (Parakeet, Nemotron) are deliberately absent — they are not
/// websocket protocols. Windows spells this vendor set with `Xai` where this
/// enum says `Grok`; the head maps across at its boundary.
///
/// `GeminiTranscribe` is the BYOK Gemini 3.5 Transcribe Live socket, distinct
/// from the same vendor reached through `HyperWhisperCloud`'s `geminiTranscribe`
/// tier — only the latter bills credits.
#[derive(uniffi::Enum)]
pub enum HwLiveProvider {
    Deepgram,
    ElevenLabs,
    OpenAi,
    Grok,
    GeminiTranscribe,
    HyperWhisperCloud,
}

impl From<HwLiveProvider> for lv::LiveProvider {
    fn from(p: HwLiveProvider) -> Self {
        match p {
            HwLiveProvider::Deepgram => lv::LiveProvider::Deepgram,
            HwLiveProvider::ElevenLabs => lv::LiveProvider::ElevenLabs,
            HwLiveProvider::OpenAi => lv::LiveProvider::OpenAi,
            HwLiveProvider::Grok => lv::LiveProvider::Grok,
            HwLiveProvider::GeminiTranscribe => lv::LiveProvider::GeminiTranscribe,
            HwLiveProvider::HyperWhisperCloud => lv::LiveProvider::HyperWhisperCloud,
        }
    }
}

impl From<lv::LiveProvider> for HwLiveProvider {
    fn from(p: lv::LiveProvider) -> Self {
        match p {
            lv::LiveProvider::Deepgram => HwLiveProvider::Deepgram,
            lv::LiveProvider::ElevenLabs => HwLiveProvider::ElevenLabs,
            lv::LiveProvider::OpenAi => HwLiveProvider::OpenAi,
            lv::LiveProvider::Grok => HwLiveProvider::Grok,
            lv::LiveProvider::GeminiTranscribe => HwLiveProvider::GeminiTranscribe,
            lv::LiveProvider::HyperWhisperCloud => HwLiveProvider::HyperWhisperCloud,
        }
    }
}

/// What a provider error frame means for the reconnect path. Mirrors
/// `lv::LiveErrorOutcome`.
#[derive(uniffi::Enum)]
pub enum HwLiveErrorOutcome {
    /// Reconnecting cannot help. Mark the provider's follow-up close as
    /// expected and surface the message as it stands.
    Terminal,
    /// May clear on its own; leave the reconnect path alone.
    Transient,
}

impl From<lv::LiveErrorOutcome> for HwLiveErrorOutcome {
    fn from(o: lv::LiveErrorOutcome) -> Self {
        match o {
            lv::LiveErrorOutcome::Terminal => HwLiveErrorOutcome::Terminal,
            lv::LiveErrorOutcome::Transient => HwLiveErrorOutcome::Transient,
        }
    }
}

impl From<HwLiveErrorOutcome> for lv::LiveErrorOutcome {
    fn from(o: HwLiveErrorOutcome) -> Self {
        match o {
            HwLiveErrorOutcome::Terminal => lv::LiveErrorOutcome::Terminal,
            HwLiveErrorOutcome::Transient => lv::LiveErrorOutcome::Transient,
        }
    }
}

/// The machine-readable kind an error frame carried, when the provider sends one
/// instead of wording. Mirrors `lv::LiveErrorKind`.
///
/// ElevenLabs alone: its error frames are a bare `message_type` with no message,
/// so the wording a head would classify is the core's own. A head that keeps a
/// failure taxonomy reads this; a head that classifies the wording ignores it and
/// nothing changes. See `hw_net::live::LiveErrorKind` for why collapsing the
/// three kinds cost `rate_limited` its "no reconnect" verdict.
#[derive(uniffi::Enum)]
pub enum HwLiveErrorKind {
    /// The credential was rejected.
    Unauthorized,
    /// The account's allowance for the period is spent.
    QuotaExceeded,
    /// Too many requests, or too many concurrent sessions, right now.
    RateLimited,
}

impl From<lv::LiveErrorKind> for HwLiveErrorKind {
    fn from(k: lv::LiveErrorKind) -> Self {
        match k {
            lv::LiveErrorKind::Unauthorized => HwLiveErrorKind::Unauthorized,
            lv::LiveErrorKind::QuotaExceeded => HwLiveErrorKind::QuotaExceeded,
            lv::LiveErrorKind::RateLimited => HwLiveErrorKind::RateLimited,
        }
    }
}

impl From<HwLiveErrorKind> for lv::LiveErrorKind {
    fn from(k: HwLiveErrorKind) -> Self {
        match k {
            HwLiveErrorKind::Unauthorized => lv::LiveErrorKind::Unauthorized,
            HwLiveErrorKind::QuotaExceeded => lv::LiveErrorKind::QuotaExceeded,
            HwLiveErrorKind::RateLimited => lv::LiveErrorKind::RateLimited,
        }
    }
}

/// Why a server refused the websocket upgrade. Mirrors
/// `lv::LiveUpgradeRefusal`.
#[derive(uniffi::Enum)]
pub enum HwLiveUpgradeRefusal {
    /// HTTP 402 — no balance to open a session with.
    InsufficientCredits,
    /// HTTP 401 / 403 — the key is missing, wrong, revoked or not permitted.
    Unauthorized,
}

impl From<lv::LiveUpgradeRefusal> for HwLiveUpgradeRefusal {
    fn from(r: lv::LiveUpgradeRefusal) -> Self {
        match r {
            lv::LiveUpgradeRefusal::InsufficientCredits => {
                HwLiveUpgradeRefusal::InsufficientCredits
            }
            lv::LiveUpgradeRefusal::Unauthorized => HwLiveUpgradeRefusal::Unauthorized,
        }
    }
}

impl From<HwLiveUpgradeRefusal> for lv::LiveUpgradeRefusal {
    fn from(r: HwLiveUpgradeRefusal) -> Self {
        match r {
            HwLiveUpgradeRefusal::InsufficientCredits => {
                lv::LiveUpgradeRefusal::InsufficientCredits
            }
            HwLiveUpgradeRefusal::Unauthorized => lv::LiveUpgradeRefusal::Unauthorized,
        }
    }
}

// ===========================================================================
// Policy
// ===========================================================================

/// Classify a provider error frame's `message` payload.
///
/// See `hw_net::live::classify_error_message` for the twenty markers, the
/// deliberate rate-limit/quota asymmetry and why no bare `"401"` is matched.
/// Unrecognised wording — including an empty message — is
/// [`HwLiveErrorOutcome::Transient`], so a payload nobody has seen yet keeps its
/// reconnect.
#[uniffi::export]
pub fn live_classify_error_message(message: String) -> HwLiveErrorOutcome {
    lv::classify_error_message(&message).into()
}

/// Classify the HTTP status of a websocket upgrade that never reached 101.
///
/// `None` means the ordinary reconnect path still applies — 429, 5xx and a
/// proxy mangling the upgrade all keep it.
#[uniffi::export]
pub fn live_upgrade_refusal(status: u16) -> Option<HwLiveUpgradeRefusal> {
    lv::upgrade_refusal(status).map(Into::into)
}

/// Whether a websocket close code is one of the RFC-6455 non-recoverable set
/// (1002, 1003, 1007, 1008, 1009, 1011).
///
/// A provider that signals an unrecoverable session with a private close code
/// combines it *with* this answer rather than replacing it.
#[uniffi::export]
pub fn live_is_terminal_close_code(code: u16) -> bool {
    lv::is_terminal_close_code(code)
}

// ===========================================================================
// Language
// ===========================================================================

/// Normalize a language selection to the primary subtag a provider wants.
///
/// `None` means "omit the language parameter entirely" and covers no selection,
/// a blank string and the app's `"auto"` sentinel alike.
#[uniffi::export]
pub fn live_normalize_language(code: Option<String>) -> Option<String> {
    lv::normalize_language(code.as_deref())
}

// ===========================================================================
// Capabilities
// ===========================================================================

/// The PCM sample rate, in hertz, the provider's socket expects. The capture
/// graph is configured from this before a session opens.
#[uniffi::export]
pub fn live_required_sample_rate(provider: HwLiveProvider) -> u32 {
    lv::required_sample_rate(provider.into())
}

/// Whether the provider's live API takes a custom-vocabulary parameter at all.
/// `false` means the terms are dropped before the socket opens.
#[uniffi::export]
pub fn live_supports_vocabulary(provider: HwLiveProvider) -> bool {
    lv::supports_vocabulary(provider.into())
}

/// Whether the provider honours custom vocabulary while the language is left on
/// auto-detect. A SECOND question from `live_supports_vocabulary`: Deepgram
/// Nova-3 accepts `keyterm` only in monolingual mode and silently ignores it
/// otherwise, while Gemini and xAI accept theirs either way.
///
/// `cloud_tier` is read for `HyperWhisperCloud` only, where the answer belongs
/// to whichever vendor the relay will forward to. `None` means the default tier.
#[uniffi::export]
pub fn live_supports_vocabulary_without_language(
    provider: HwLiveProvider,
    cloud_tier: Option<String>,
) -> bool {
    lv::supports_vocabulary_without_language(provider.into(), cloud_tier.as_deref())
}

/// Whether a session-complete event ends the session even when the client has
/// not asked to stop yet.
///
/// `false` for Gemini alone: `generationComplete` is a TURN boundary, so a
/// terminal reading silently ends a live dictation at the first pause in speech.
#[uniffi::export]
pub fn live_complete_ends_session_before_stop(provider: HwLiveProvider) -> bool {
    lv::complete_ends_session_before_stop(provider.into())
}

/// How long to hold the audio pump waiting for the provider's session-started
/// frame, in milliseconds. `0` means send from the moment the socket opens.
///
/// Non-zero for Gemini alone: audio sent before `setupComplete` is discarded by
/// the server, which costs the opening words of the dictation.
#[uniffi::export]
pub fn live_start_timeout_ms(provider: HwLiveProvider) -> u32 {
    lv::start_timeout_ms(provider.into())
}

/// The human-readable provider label stored on a history entry. The
/// " (Streaming)" suffix is what distinguishes a live session from the same
/// vendor's batch transcription.
#[uniffi::export]
pub fn live_provider_label(provider: HwLiveProvider) -> String {
    lv::provider_label(provider.into()).to_string()
}

// ===========================================================================
// LiveSession — the workspace's first Rust-owned UniFFI object
//
// Everything above this line is session-free. Everything below carries the
// five wire protocols, and needs somewhere to keep what it has already seen —
// which is why this is an object.
//
// `KeyValueStore` in `lib.rs` is the only object-shaped precedent, and it is a
// *callback interface*: the platform implements it and Rust calls back. This is
// the opposite direction — Rust owns the instance and the platform holds a
// handle. The generated binding is a class with `FreeRustArcPtr` and a
// finalizer, so every consumer must dispose it.
// ===========================================================================

/// Everything a live session needs to build its connection. Mirrors
/// `lv::LiveConfig`.
///
/// `vocabulary` is a term LIST, not the comma-joined string the heads pass
/// around today: joining is a per-provider wire decision (xAI repeats
/// `keyterm=`, HyperWhisper Cloud sends one `vocabulary=`), so the core takes
/// the terms and each protocol decides.
///
/// `base_url` re-points HyperWhisper Cloud at another backend — macOS's `#if
/// DEBUG` build talks to staging, and a hardcoded production host here would
/// bill a developer's key against production. `None` means the production host,
/// and every other provider ignores the field.
#[derive(uniffi::Record)]
pub struct HwLiveConfig {
    pub provider: HwLiveProvider,
    pub api_key: Option<String>,
    pub license_key: Option<String>,
    pub device_id: Option<String>,
    pub language: Option<String>,
    pub vocabulary: Vec<String>,
    pub model: Option<String>,
    pub fast_formatting: bool,
    pub base_url: Option<String>,

    /// Which upstream vendor HyperWhisper Cloud should relay to: a
    /// `cloud-stt-catalog.json` entry id, which is what the app's global
    /// `streamingCloudTier` setting stores. A path selector, deliberately not a
    /// `HwLiveProvider` arm — the credit and entitlement wiring keys off "the
    /// provider is HyperWhisper Cloud" and must keep matching. `None` or an
    /// unknown id means the default tier. Ignored by every other provider.
    pub cloud_tier: Option<String>,
}

impl From<HwLiveConfig> for lv::LiveConfig {
    fn from(c: HwLiveConfig) -> Self {
        lv::LiveConfig {
            provider: c.provider.into(),
            api_key: c.api_key,
            license_key: c.license_key,
            device_id: c.device_id,
            language: c.language,
            vocabulary: c.vocabulary,
            model: c.model,
            fast_formatting: c.fast_formatting,
            base_url: c.base_url,
            cloud_tier: c.cloud_tier,
        }
    }
}

/// How a PCM chunk becomes a websocket frame. Mirrors `lv::AudioFraming`.
///
/// **This descriptor is why audio never crosses this boundary.** The core says
/// how to wrap a chunk once, at connect time; the platform does the base64 and
/// the concatenation on bytes it already holds. A variant that carried the
/// samples themselves would put a recording's worth of PCM through the FFI on
/// every chunk — see `hw_net::contract` for the rule and
/// `ffi_net`'s `audio_is_referenced_by_path_and_never_carried_as_bytes` for the
/// batch path's version of this guard.
#[derive(uniffi::Enum)]
pub enum HwAudioFraming {
    /// Send the PCM bytes as a binary frame, unchanged.
    Binary,
    /// Send `prefix + base64(pcm) + suffix` as a text frame.
    Base64Json { prefix: String, suffix: String },
}

impl From<lv::AudioFraming> for HwAudioFraming {
    fn from(f: lv::AudioFraming) -> Self {
        match f {
            lv::AudioFraming::Binary => HwAudioFraming::Binary,
            lv::AudioFraming::Base64Json { prefix, suffix } => {
                HwAudioFraming::Base64Json { prefix, suffix }
            }
        }
    }
}

/// One frame to put on the wire. Mirrors `lv::LiveFrame`.
///
/// `data` is a string because every frame the core produces is JSON text; the
/// only binary frames in these protocols are audio, which the core never sees.
/// `binary` keeps the platform's mapping onto its own message-type enum total
/// rather than a hardcoded `Text` at the adapter.
#[derive(uniffi::Record)]
pub struct HwLiveFrame {
    pub data: String,
    pub binary: bool,
}

impl From<lv::LiveFrame> for HwLiveFrame {
    fn from(f: lv::LiveFrame) -> Self {
        HwLiveFrame {
            data: f.data,
            binary: f.binary,
        }
    }
}

/// Everything the platform needs to open the socket. Mirrors `lv::LiveConnect`.
///
/// Reuses `ffi_net`'s [`Header`] rather than declaring a second name for a
/// name/value pair, the same way `ffi_llm` reuses its `HttpRequest`.
#[derive(uniffi::Record)]
pub struct HwLiveConnect {
    pub url: String,
    pub headers: Vec<Header>,
    pub subprotocols: Vec<String>,
    pub sample_rate: u32,
    pub framing: HwAudioFraming,
    pub start_frames: Vec<HwLiveFrame>,
    pub session_starts_on_open: bool,
}

impl From<lv::LiveConnect> for HwLiveConnect {
    fn from(c: lv::LiveConnect) -> Self {
        HwLiveConnect {
            url: c.url,
            headers: c.headers.into_iter().map(Into::into).collect(),
            subprotocols: c.subprotocols,
            sample_rate: c.sample_rate,
            framing: c.framing.into(),
            start_frames: c.start_frames.into_iter().map(Into::into).collect(),
            session_starts_on_open: c.session_starts_on_open,
        }
    }
}

/// One step of the stop path, run in order. Mirrors `lv::StopStep`.
///
/// The arms match Windows' shipped `StreamingStopAction` one for one, so a head
/// that already runs stop steps maps this by renaming. A flat frame list plus a
/// drain timeout cannot express these protocols: Deepgram needs a wait *between*
/// two frames, and two providers wait on the completion *event* that carries
/// `credits_used`.
#[derive(uniffi::Enum)]
pub enum HwLiveStopStep {
    SendText { text: String },
    Wait { ms: u64 },
    WaitForSessionComplete { timeout_ms: u64 },
    Close,
}

impl From<lv::StopStep> for HwLiveStopStep {
    fn from(s: lv::StopStep) -> Self {
        match s {
            lv::StopStep::SendText { text } => HwLiveStopStep::SendText { text },
            lv::StopStep::Wait { ms } => HwLiveStopStep::Wait { ms },
            lv::StopStep::WaitForSessionComplete { timeout_ms } => {
                HwLiveStopStep::WaitForSessionComplete { timeout_ms }
            }
            lv::StopStep::Close => HwLiveStopStep::Close,
        }
    }
}

/// What one parsed provider message means. Mirrors `lv::LiveEvent`.
///
/// Deliberately the macOS `StreamingProviderEvent` superset, not
/// `shared-dotnet`'s six-case `LiveProtocolEvent`: a consumer that does not want
/// an arm ignores it, but a consumer that needs one the core never produced has
/// nowhere to go.
#[derive(uniffi::Enum)]
pub enum HwLiveEvent {
    SessionStarted {
        session_id: Option<String>,
    },
    PartialTranscript {
        text: String,
    },
    FinalTranscript {
        text: String,
    },
    FinalTranscriptAndSessionComplete {
        text: String,
        duration_seconds: f64,
        credits_used: f64,
    },
    SessionComplete {
        duration_seconds: f64,
        credits_used: f64,
    },
    Error {
        message: String,
        /// The machine-readable kind, when the provider sent one instead of
        /// wording. `None` for four of the five — see [`HwLiveErrorKind`].
        kind: Option<HwLiveErrorKind>,
    },
    Warning {
        message: String,
    },
    Metadata {
        raw: String,
    },
    Ignore,
}

impl From<lv::LiveEvent> for HwLiveEvent {
    fn from(e: lv::LiveEvent) -> Self {
        match e {
            lv::LiveEvent::SessionStarted { session_id } => {
                HwLiveEvent::SessionStarted { session_id }
            }
            lv::LiveEvent::PartialTranscript { text } => HwLiveEvent::PartialTranscript { text },
            lv::LiveEvent::FinalTranscript { text } => HwLiveEvent::FinalTranscript { text },
            lv::LiveEvent::FinalTranscriptAndSessionComplete {
                text,
                duration_seconds,
                credits_used,
            } => HwLiveEvent::FinalTranscriptAndSessionComplete {
                text,
                duration_seconds,
                credits_used,
            },
            lv::LiveEvent::SessionComplete {
                duration_seconds,
                credits_used,
            } => HwLiveEvent::SessionComplete {
                duration_seconds,
                credits_used,
            },
            lv::LiveEvent::Error { message, kind } => HwLiveEvent::Error {
                message,
                kind: kind.map(Into::into),
            },
            lv::LiveEvent::Warning { message } => HwLiveEvent::Warning { message },
            lv::LiveEvent::Metadata { raw } => HwLiveEvent::Metadata { raw },
            lv::LiveEvent::Ignore => HwLiveEvent::Ignore,
        }
    }
}

/// Why a session could not produce a connection descriptor. Mirrors
/// `lv::LiveError`.
///
/// One arm on purpose: everything else that can go wrong on a live connection —
/// DNS, TLS, a refused upgrade, a mid-session close — is transport, and
/// transport stays native. `Display` is hand-written to match the leaf's
/// `thiserror` message, the same way [`HwTranscriptionError`] does, so hw-core
/// needs no extra dependency.
#[derive(uniffi::Error, Debug)]
pub enum HwLiveError {
    /// No usable credential: a blank API key, or (HyperWhisper Cloud) neither a
    /// license key nor a device id.
    MissingCredential,
}

impl std::fmt::Display for HwLiveError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            HwLiveError::MissingCredential => {
                write!(f, "A streaming provider credential is required.")
            }
        }
    }
}

impl std::error::Error for HwLiveError {}

impl From<lv::LiveError> for HwLiveError {
    fn from(e: lv::LiveError) -> Self {
        match e {
            lv::LiveError::MissingCredential => HwLiveError::MissingCredential,
        }
    }
}

/// One live-streaming session: a config in, and every value the platform's
/// socket loop needs out.
///
/// The lifecycle is `new` → `connect` → (`note_audio` / `control_frames` /
/// `parse`)\* → `stop_sequence`, with `reset` returning it to the state the
/// constructor left it in so a reconnect can reuse the object.
///
/// The generated binding is `IDisposable` in C# and reference-counted in Swift.
/// **A consumer must dispose it** — the Rust side is an `Arc` the platform holds
/// a raw handle to, and dropping the last reference without disposing leaks it
/// for the life of the process.
///
/// Thread safety is a `Mutex`, not a re-entrant one: the socket loop, the
/// capture thread and the stop path all reach the same instance, and the shipped
/// strategies already guard their counters with a lock for exactly that reason.
/// No method calls another through the FFI, so there is nothing to re-enter.
#[derive(uniffi::Object)]
pub struct HwLiveSession {
    inner: std::sync::Mutex<lv::LiveSession>,
}

impl HwLiveSession {
    /// Take the lock, recovering from a poisoned one.
    ///
    /// The release profile is `panic = "abort"`, so a poisoned mutex cannot
    /// happen in a shipped binary — but `cargo test` unwinds, and a plain
    /// `unwrap()` here would turn one failing assertion into a cascade of
    /// unrelated failures. Nothing reachable from the FFI may `unwrap()`.
    fn locked(&self) -> std::sync::MutexGuard<'_, lv::LiveSession> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }
}

#[uniffi::export]
impl HwLiveSession {
    /// Build a session for `config`.
    ///
    /// An exported constructor, not a `live_session_new(config)` free function:
    /// a `#[uniffi::Object]` gets no foreign constructor unless one is exported,
    /// and without it a consumer can name the type and never instantiate it.
    /// This renders as `new HwLiveSession(config)` in C# and
    /// `HwLiveSession(config:)` in Swift.
    ///
    /// Infallible on purpose. A missing credential is reported by
    /// [`HwLiveSession::connect`], which is the call that needs it, so the
    /// constructor a foreign language sees never throws.
    #[uniffi::constructor]
    pub fn new(config: HwLiveConfig) -> std::sync::Arc<Self> {
        std::sync::Arc::new(Self {
            inner: std::sync::Mutex::new(lv::LiveSession::new(config.into())),
        })
    }

    /// The provider this session speaks.
    pub fn provider(&self) -> HwLiveProvider {
        self.locked().provider().into()
    }

    /// Everything needed to open the socket. Resets the per-connection state,
    /// so calling it again after a drop is the whole reconnect preparation.
    pub fn connect(&self) -> Result<HwLiveConnect, HwLiveError> {
        self.locked()
            .connect()
            .map(Into::into)
            .map_err(HwLiveError::from)
    }

    /// Record that `byte_count` bytes of PCM were just handed to the socket.
    ///
    /// A **count**, never the bytes: this is the one place a live session is
    /// told about audio, and it is told a number. Only OpenAI's commit gate
    /// reads it; the call is free for the other four and callers send it
    /// unconditionally.
    pub fn note_audio(&self, byte_count: u64) {
        self.locked().note_audio(byte_count);
    }

    /// Frames to send at an audio send opportunity, given the caller's clock.
    ///
    /// `now_ms` is a parameter, never a clock read here — that is what makes
    /// OpenAI's 1.2 s commit interval and Deepgram's 3 s keepalive testable
    /// without sleeping. Usually empty.
    pub fn control_frames(&self, now_ms: u64) -> Vec<HwLiveFrame> {
        self.locked()
            .control_frames(now_ms)
            .into_iter()
            .map(Into::into)
            .collect()
    }

    /// Read one text message off the socket. Anything unrecognised — including
    /// text that is not JSON — is [`HwLiveEvent::Ignore`]: a provider adding a
    /// frame shape must never end a recording in progress.
    pub fn parse(&self, text: String) -> HwLiveEvent {
        self.locked().parse(&text).into()
    }

    /// The ordered stop path, given the caller's clock. Run the steps in order;
    /// do not reorder them and do not collapse the waits.
    pub fn stop_sequence(&self, now_ms: u64) -> Vec<HwLiveStopStep> {
        self.locked()
            .stop_sequence(now_ms)
            .into_iter()
            .map(Into::into)
            .collect()
    }

    /// Forget every frame this session has seen. What makes a reconnect able to
    /// reuse one object instead of rebuilding it from the config.
    pub fn reset(&self) {
        self.locked().reset();
    }
}

// ===========================================================================
// Tests
// ===========================================================================

#[cfg(test)]
mod tests {
    use super::*;

    const ALL: [lv::LiveProvider; 6] = lv::LiveProvider::ALL;

    fn hw_tag(p: &HwLiveProvider) -> &'static str {
        match p {
            HwLiveProvider::Deepgram => "deepgram",
            HwLiveProvider::ElevenLabs => "elevenlabs",
            HwLiveProvider::OpenAi => "openai",
            HwLiveProvider::Grok => "grok",
            HwLiveProvider::GeminiTranscribe => "gemini_transcribe",
            HwLiveProvider::HyperWhisperCloud => "hyperwhisper_cloud",
        }
    }

    fn live_tag(p: &lv::LiveProvider) -> &'static str {
        match p {
            lv::LiveProvider::Deepgram => "deepgram",
            lv::LiveProvider::ElevenLabs => "elevenlabs",
            lv::LiveProvider::OpenAi => "openai",
            lv::LiveProvider::Grok => "grok",
            lv::LiveProvider::GeminiTranscribe => "gemini_transcribe",
            lv::LiveProvider::HyperWhisperCloud => "hyperwhisper_cloud",
        }
    }

    /// Every `From` arm in this file has the same shape, so a swapped pair
    /// compiles. Round-trip each arm through both directions.
    #[test]
    fn provider_maps_to_the_same_live_arm_in_both_directions() {
        for provider in ALL {
            let expected = live_tag(&provider);
            let hw: HwLiveProvider = provider.into();
            assert_eq!(hw_tag(&hw), expected, "live -> Hw is wrong for {expected}");
            let back: lv::LiveProvider = hw.into();
            assert_eq!(
                live_tag(&back),
                expected,
                "Hw -> live is wrong for {expected}"
            );
        }
    }

    /// A round trip cannot see two arms swapped in *both* directions, so pin
    /// each provider to an observable value as well — through the exported
    /// functions, which is what the bindings actually call.
    ///
    /// `HwLiveProvider` is a UniFFI enum and derives no `Clone`, so each call
    /// takes a freshly built arm from `hw`.
    #[test]
    fn each_provider_arm_carries_its_own_capability_row() {
        let hw = |p: lv::LiveProvider| -> HwLiveProvider { p.into() };
        let cases: [(lv::LiveProvider, u32, bool, &str); 6] = [
            (
                lv::LiveProvider::Deepgram,
                16_000,
                true,
                "Deepgram (Streaming)",
            ),
            (
                lv::LiveProvider::ElevenLabs,
                16_000,
                false,
                "ElevenLabs (Streaming)",
            ),
            (
                lv::LiveProvider::OpenAi,
                24_000,
                false,
                "OpenAI (Streaming)",
            ),
            (lv::LiveProvider::Grok, 16_000, true, "SpaceXAI (Streaming)"),
            (
                lv::LiveProvider::GeminiTranscribe,
                16_000,
                true,
                "Gemini 3.5 Transcribe (Streaming)",
            ),
            (
                lv::LiveProvider::HyperWhisperCloud,
                16_000,
                true,
                "HyperWhisper Cloud (Streaming)",
            ),
        ];

        for (provider, rate, vocabulary, label) in cases {
            let tag = live_tag(&provider);
            assert_eq!(live_provider_label(hw(provider)), label, "{tag} label");
            assert_eq!(live_required_sample_rate(hw(provider)), rate, "{tag} rate");
            assert_eq!(
                live_supports_vocabulary(hw(provider)),
                vocabulary,
                "{tag} vocabulary"
            );
        }
    }

    #[test]
    fn error_outcome_maps_in_both_directions() {
        for (hw, leaf) in [
            (HwLiveErrorOutcome::Terminal, lv::LiveErrorOutcome::Terminal),
            (
                HwLiveErrorOutcome::Transient,
                lv::LiveErrorOutcome::Transient,
            ),
        ] {
            let to_leaf: lv::LiveErrorOutcome = hw.into();
            assert_eq!(to_leaf, leaf);
            let back: HwLiveErrorOutcome = leaf.into();
            assert!(matches!(
                (back, leaf),
                (HwLiveErrorOutcome::Terminal, lv::LiveErrorOutcome::Terminal)
                    | (
                        HwLiveErrorOutcome::Transient,
                        lv::LiveErrorOutcome::Transient
                    )
            ));
        }
    }

    #[test]
    fn error_kind_maps_in_both_directions() {
        for leaf in [
            lv::LiveErrorKind::Unauthorized,
            lv::LiveErrorKind::QuotaExceeded,
            lv::LiveErrorKind::RateLimited,
        ] {
            let mirrored: HwLiveErrorKind = leaf.into();
            let back: lv::LiveErrorKind = mirrored.into();
            assert_eq!(back, leaf, "a swapped arm would relabel a failure");
        }
    }

    #[test]
    fn upgrade_refusal_maps_in_both_directions() {
        for (hw, leaf) in [
            (
                HwLiveUpgradeRefusal::InsufficientCredits,
                lv::LiveUpgradeRefusal::InsufficientCredits,
            ),
            (
                HwLiveUpgradeRefusal::Unauthorized,
                lv::LiveUpgradeRefusal::Unauthorized,
            ),
        ] {
            let to_leaf: lv::LiveUpgradeRefusal = hw.into();
            assert_eq!(to_leaf, leaf);
            let back: HwLiveUpgradeRefusal = leaf.into();
            assert!(matches!(
                (back, leaf),
                (
                    HwLiveUpgradeRefusal::InsufficientCredits,
                    lv::LiveUpgradeRefusal::InsufficientCredits
                ) | (
                    HwLiveUpgradeRefusal::Unauthorized,
                    lv::LiveUpgradeRefusal::Unauthorized
                )
            ));
        }
    }

    /// The three cases the two .NET heads gain here, asserted through the FFI
    /// entry points rather than the leaf functions.
    #[test]
    fn the_exported_classifiers_answer_the_flagship_cases() {
        assert!(matches!(
            live_classify_error_message("Credit balance exhausted".to_string()),
            HwLiveErrorOutcome::Terminal
        ));
        assert!(matches!(
            live_classify_error_message(
                "Stream interrupted (request_id: req_4013f2c8). Please retry.".to_string()
            ),
            HwLiveErrorOutcome::Transient
        ));
        assert!(matches!(
            live_upgrade_refusal(402),
            Some(HwLiveUpgradeRefusal::InsufficientCredits)
        ));
        assert!(live_upgrade_refusal(429).is_none());
        assert!(live_is_terminal_close_code(1011));
        assert!(!live_is_terminal_close_code(1006));
    }

    #[test]
    fn the_exported_normalizer_omits_auto_and_keeps_the_primary_subtag() {
        assert_eq!(live_normalize_language(None), None);
        assert_eq!(live_normalize_language(Some("auto".to_string())), None);
        assert_eq!(
            live_normalize_language(Some(" EN-us ".to_string())),
            Some("en".to_string())
        );
    }

    // -----------------------------------------------------------------------
    // HwLiveSession
    // -----------------------------------------------------------------------

    fn session(provider: lv::LiveProvider) -> std::sync::Arc<HwLiveSession> {
        HwLiveSession::new(HwLiveConfig {
            provider: provider.into(),
            api_key: Some("k".to_string()),
            license_key: Some("k".to_string()),
            device_id: None,
            language: None,
            vocabulary: Vec::new(),
            model: None,
            fast_formatting: false,
            base_url: None,
            cloud_tier: None,
        })
    }

    /// `source` with everything that is not code blanked out: line comments,
    /// block comments (nested, as Rust allows), and the CONTENTS of string, raw
    /// string and char literals. One output line per input line, so a region
    /// built from these still reads like the file.
    ///
    /// This is what makes [`uniffi_regions`] trustworthy. Its first version
    /// ended a region at the next line that was exactly `}`, and a reviewer
    /// defeated it with a multi-line raw-string JSON literal holding a
    /// column-zero `}`: the exported impl was truncated there, every item below
    /// went unscanned, and `send_audio_bytes(&self, pcm: Vec<u8>)` added after it
    /// passed. A brace inside a literal or a comment can no longer end anything.
    fn code_lines(source: &str) -> Vec<String> {
        enum Mode {
            Code,
            Block(u32),
            Text,
            Raw(usize),
            Char,
        }
        let mut mode = Mode::Code;
        let mut out = Vec::new();
        for line in source.lines() {
            let chars: Vec<char> = line.chars().collect();
            let mut code = String::new();
            let mut index = 0;
            while index < chars.len() {
                let rest = &chars[index..];
                match mode {
                    Mode::Code => match rest {
                        ['/', '/', ..] => break,
                        ['/', '*', ..] => {
                            mode = Mode::Block(1);
                            index += 2;
                        }
                        // A raw string: `r"…"`, `r#"…"#`, `br##"…"##`. The hashes
                        // decide where it ends, so they are counted.
                        ['b' | 'r', ..] if raw_opener(rest).is_some() => {
                            let (skip, hashes) = raw_opener(rest).unwrap_or_default();
                            mode = Mode::Raw(hashes);
                            index += skip;
                        }
                        ['"', ..] => {
                            mode = Mode::Text;
                            index += 1;
                        }
                        // `'a'` is a char literal; `'_` and `'static` are
                        // lifetimes and stay code — mistaking one for the other
                        // would swallow the rest of the line, braces included.
                        ['\'', ..] if is_char_literal(rest) => {
                            mode = Mode::Char;
                            index += 1;
                        }
                        [character, ..] => {
                            code.push(*character);
                            index += 1;
                        }
                        [] => break,
                    },
                    Mode::Block(depth) => match rest {
                        ['/', '*', ..] => {
                            mode = Mode::Block(depth + 1);
                            index += 2;
                        }
                        ['*', '/', ..] => {
                            mode = if depth == 1 {
                                Mode::Code
                            } else {
                                Mode::Block(depth - 1)
                            };
                            index += 2;
                        }
                        _ => index += 1,
                    },
                    Mode::Text => match rest {
                        ['\\', _, ..] => index += 2,
                        ['"', ..] => {
                            mode = Mode::Code;
                            index += 1;
                        }
                        _ => index += 1,
                    },
                    Mode::Raw(hashes) => {
                        if rest[0] == '"'
                            && rest[1..].iter().take(hashes).all(|c| *c == '#')
                            && rest.len() > hashes
                        {
                            mode = Mode::Code;
                            index += 1 + hashes;
                        } else {
                            index += 1;
                        }
                    }
                    Mode::Char => match rest {
                        ['\\', _, ..] => index += 2,
                        ['\'', ..] => {
                            mode = Mode::Code;
                            index += 1;
                        }
                        _ => index += 1,
                    },
                }
            }
            out.push(code);
        }
        out
    }

    /// The length of a raw-string opener at `rest`, and its hash count.
    fn raw_opener(rest: &[char]) -> Option<(usize, usize)> {
        let start = usize::from(rest[0] == 'b');
        if rest.get(start) != Some(&'r') {
            return None;
        }
        let hashes = rest[start + 1..].iter().take_while(|c| **c == '#').count();
        (rest.get(start + 1 + hashes) == Some(&'"')).then_some((start + 2 + hashes, hashes))
    }

    /// Whether `rest` opens a char literal rather than a lifetime.
    fn is_char_literal(rest: &[char]) -> bool {
        matches!(rest, ['\'', '\\', ..]) || rest.get(2) == Some(&'\'')
    }

    /// Every `#[uniffi::…]` item declared in `source`, as code text.
    ///
    /// A region runs from the attribute line to the line where the item's brace
    /// depth returns to zero — or, for an item with no body, to its `;`. Braces
    /// are counted over [`code_lines`], so only real braces count. Over-reading
    /// (an item that never closes) runs to the end of the file and would only
    /// make the guard stricter.
    fn uniffi_regions(source: &str) -> Vec<String> {
        let lines = code_lines(source);
        let mut regions = Vec::new();
        for (index, line) in lines.iter().enumerate() {
            let trimmed = line.trim_start();
            if !(trimmed.starts_with("#[") && trimmed.contains("uniffi::")) {
                continue;
            }
            let mut depth: i64 = 0;
            let mut opened = false;
            let mut end = lines.len();
            for (offset, candidate) in lines[index..].iter().enumerate() {
                for character in candidate.chars() {
                    match character {
                        '{' => {
                            depth += 1;
                            opened = true;
                        }
                        '}' => depth -= 1,
                        _ => {}
                    }
                }
                if (opened && depth <= 0) || (!opened && candidate.trim_end().ends_with(';')) {
                    end = index + offset + 1;
                    break;
                }
            }
            regions.push(lines[index..end].join("\n"));
        }
        regions
    }

    /// The tokens that name a byte type: `u8` and `i8`, plus every alias
    /// declared in `source` that resolves to one, chains included.
    ///
    /// `type Pcm = Vec<u8>;` with `pub fn send(&self, pcm: Pcm)` is a real defeat
    /// of a plain token match, and Codex found it. An alias declared in a file
    /// this guard reads is resolvable — it is right there — so it is resolved.
    /// An alias declared anywhere else is not; the test's doc says what happens
    /// then.
    fn byte_type_tokens(source: &str) -> Vec<String> {
        let mut tokens = vec!["u8".to_string(), "i8".to_string()];
        let lines = code_lines(source);
        // One pass per link in the longest chain. The bound is the file itself.
        for _ in 0..lines.len().min(16) {
            let mut added = false;
            for line in &lines {
                let trimmed = line.trim_start();
                let Some(rest) = trimmed
                    .strip_prefix("pub type ")
                    .or_else(|| trimmed.strip_prefix("type "))
                    .or_else(|| {
                        trimmed
                            .split_once("type ")
                            .filter(|(before, _)| before.starts_with("pub("))
                            .map(|(_, after)| after)
                    })
                else {
                    continue;
                };
                let Some((name, value)) = rest.split_once('=') else {
                    continue;
                };
                let name = name.split(['<', ' ']).next().unwrap_or_default().trim();
                if name.is_empty() || tokens.iter().any(|token| token == name) {
                    continue;
                }
                if mentions_a_byte_type(value, &tokens) {
                    tokens.push(name.to_string());
                    added = true;
                }
            }
            if !added {
                break;
            }
        }
        tokens
    }

    /// The types this file imports from elsewhere in the crate, as written.
    fn crate_imports(source: &str) -> Vec<String> {
        code_lines(source)
            .iter()
            .filter_map(|line| {
                let trimmed = line.trim();
                trimmed
                    .strip_prefix("use ")
                    .filter(|rest| rest.starts_with("crate::"))
                    .map(|rest| rest.trim_end_matches(';').to_string())
            })
            .collect()
    }

    /// Whether `code` names one of `tokens` as a whole token, so `u16`, `u32`
    /// and `u64` are untouched.
    fn mentions_a_byte_type(code: &str, tokens: &[String]) -> bool {
        tokens.iter().any(|token| {
            code.match_indices(token.as_str()).any(|(at, _)| {
                let before = code[..at].chars().next_back();
                let after = code[at + token.len()..].chars().next();
                let word =
                    |c: Option<char>| c.is_some_and(|c| c.is_ascii_alphanumeric() || c == '_');
                !word(before) && !word(after)
            })
        })
    }

    /// AUDIO MUST NEVER CROSS THIS BOUNDARY — asserted against the exported
    /// surface itself, so breaking the rule fails this test.
    ///
    /// The previous version of this test only called the existing signature and
    /// claimed in a comment that a `Vec<u8>` parameter "would stop compiling".
    /// It would not: a reviewer added
    /// `pub fn send_audio_bytes(&self, pcm: Vec<u8>)` to the `#[uniffi::export]
    /// impl HwLiveSession` block below and `cargo test --workspace` reported 21
    /// suites, 0 failed. `ffi_net`'s named counterpart
    /// (`audio_is_referenced_by_path_and_never_carried_as_bytes`) asserts on the
    /// batch multipart builders and never looks at the live surface, so nothing
    /// enforced the rule `hw-net/src/contract.rs` opens by stating.
    ///
    /// So the rule is checked where it lives: no `#[uniffi::…]` item on the live
    /// surface — exported function, exported method, constructor, record field
    /// or enum payload, in this file or in the one record it imports — may name
    /// a byte type. A count (`u64`), a descriptor
    /// ([`HwAudioFraming`]) and a `String` frame are the only shapes allowed
    /// through, and the platform does the base64 on bytes it already holds.
    ///
    /// # What this guard covers, exactly
    ///
    /// It is a source-text scan, so its reach is worth stating rather than
    /// implying. Comments and string contents are removed before matching
    /// ([`code_lines`]), so prose may still say `Vec<u8>`; only code is scanned.
    ///
    /// * **A byte type named outright** — caught.
    /// * **An item hidden by truncating the scan** (the raw-string `}` defeat) —
    ///   caught: regions end on brace depth over code, not on a literal line.
    /// * **An alias declared in a scanned file** (`type Pcm = Vec<u8>;`) —
    ///   caught, chains included ([`byte_type_tokens`]).
    /// * **A type declared in another file** — the live surface has exactly one,
    ///   `ffi_net::Header` on [`HwLiveConnect`], and it is scanned where it is
    ///   declared. A byte field added to `Header` fails this test. `ffi_net`
    ///   carries bytes legitimately elsewhere, so only that one record is read,
    ///   not the file.
    /// * **A SECOND cross-file type** — not scanned, and cannot arrive quietly:
    ///   the import list and the absence of in-crate paths are both asserted, so
    ///   adding one fails this test until the guard is taught about it. That is
    ///   fail-closed, not covered.
    /// * **A byte type nested one level deeper** — a cross-file record whose own
    ///   field names a type in a THIRD file — is OUT OF REACH. The scan does not
    ///   recurse, and resolving a name to a declaration is the compiler's job,
    ///   not a text scan's. `Header` is two `String`s, so nothing reachable today
    ///   is in that shape.
    /// * A `hw_net` type cannot appear on this surface at all: UniFFI exports
    ///   only types this crate declares, or ones it explicitly adopts with a
    ///   remote/custom-type declaration — of which the workspace has none — and
    ///   `hw-net` has no uniffi dependency to declare them with.
    #[test]
    fn no_exported_live_item_can_carry_audio_bytes() {
        let live = include_str!("ffi_live.rs");
        let net = include_str!("ffi_net.rs");
        let regions = uniffi_regions(live);

        // The guard has teeth only if it actually found the surface. A scanner
        // that silently matched nothing, or that stopped at the attribute line,
        // would pass forever - so the floor and the landmarks are asserted
        // before anything is asserted about their contents.
        assert!(
            regions.len() >= 20,
            "the region scanner found only {} `#[uniffi::…]` items; this file declares the whole live FFI surface",
            regions.len()
        );
        assert!(
            regions.iter().all(|region| region.lines().count() >= 2),
            "a region that is only its attribute line scans nothing"
        );
        // `note_audio` is the one call told about audio; `reset` is the LAST
        // method in that impl. Requiring both means the block was scanned from
        // end to end, which is what the truncation defeat broke.
        assert!(
            regions
                .iter()
                .any(|region| region.contains("impl HwLiveSession")
                    && region.contains("fn note_audio")
                    && region.contains("fn reset")),
            "the session object's exported impl - the block an audio method would land in - \
             was not scanned whole"
        );
        assert!(
            regions
                .iter()
                .any(|region| region.contains("pub struct HwLiveConfig")),
            "the config record - the other shape audio could be smuggled in - was not scanned"
        );

        // The cross-file boundary, held in place. One import, no in-crate paths:
        // every other name on this surface is declared in this file or is a
        // primitive, and both facts are checked rather than assumed.
        assert_eq!(
            crate_imports(live),
            vec!["crate::ffi_net::Header".to_string()],
            "the live surface imports a type this guard does not scan; scan its declaration \
             the way ffi_net::Header is scanned below, then update this list"
        );
        assert!(
            regions.iter().all(|region| !region.contains("crate::")),
            "an exported item names an in-crate type by path; the cross-file scan follows \
             imports, so a path would go unscanned"
        );
        let header = uniffi_regions(net)
            .into_iter()
            .find(|region| region.contains("pub struct Header"))
            .expect("ffi_net declares the Header record the live connect descriptor carries");

        // Aliases from both scanned files, so an alias declared beside `Header`
        // counts the same as one declared here.
        let tokens = byte_type_tokens(&format!("{live}\n{net}"));
        for region in regions.into_iter().chain([header]) {
            let head = region
                .lines()
                .find(|line| !line.trim().is_empty())
                .unwrap_or_default()
                .trim()
                .to_string();
            for line in region.lines() {
                assert!(
                    !mentions_a_byte_type(line, &tokens),
                    "audio must never cross the FFI: `{}` names a byte type inside `{head}`",
                    line.trim()
                );
            }
        }
    }

    /// The other half of the same rule: [`HwAudioFraming`] *describes* the
    /// wrapping rather than performing it, so the base64 and the concatenation
    /// happen natively on bytes the platform already holds, and `note_audio`
    /// takes a count.
    #[test]
    fn audio_is_described_by_a_framing_rule_and_never_carried_as_bytes() {
        for provider in ALL {
            let session = session(provider);
            let connect = session.connect().expect("connect");

            // A byte COUNT: one 100 ms buffer at the provider's own rate.
            let byte_count: u64 = u64::from(connect.sample_rate) * 2 / 10;
            session.note_audio(byte_count);

            match connect.framing {
                // The samples go out untouched; the core never sees them.
                HwAudioFraming::Binary => {}
                // A fixed envelope with one hole in the middle. Everything the
                // core contributes is in these two literals, so nothing that
                // varies per chunk can require the samples to come here.
                HwAudioFraming::Base64Json { prefix, suffix } => {
                    assert!(
                        prefix.ends_with('"'),
                        "{provider:?} prefix must end inside a JSON string"
                    );
                    assert!(
                        suffix.starts_with('"'),
                        "{provider:?} suffix must resume inside a JSON string"
                    );
                    let framed = format!("{prefix}SHlwZXJXaGlzcGVy{suffix}");
                    assert!(
                        serde_json::from_str::<serde_json::Value>(&framed).is_ok(),
                        "{provider:?} envelope does not close: {framed}"
                    );
                }
            }
        }
    }

    /// The constructor is the whole point of making this an object rather than
    /// more free functions taking a config: the state has to live somewhere
    /// across calls. Exercise the lifecycle through the exported surface, which
    /// is what the bindings call.
    #[test]
    fn the_exported_session_carries_state_across_calls_and_resets() {
        let session = session(lv::LiveProvider::OpenAi);
        let connect = session.connect().expect("connect");
        assert_eq!(connect.sample_rate, 24_000);
        assert_eq!(connect.start_frames.len(), 1);
        assert!(!connect.session_starts_on_open);

        // now_ms is a PARAMETER: seed the clock, then clear the interval and
        // the byte floor, without a clock read and without a sleep.
        assert!(session.control_frames(0).is_empty());
        session.note_audio(4_800);
        assert_eq!(
            session.control_frames(2_000).len(),
            1,
            "the periodic commit"
        );

        session.note_audio(4_799);
        assert!(
            matches!(
                session.stop_sequence(3_000).first(),
                Some(HwLiveStopStep::Wait { ms: 1_000 })
            ),
            "a sub-100 ms tail must not be committed"
        );

        session.note_audio(4_800);
        session.reset();
        assert!(
            matches!(
                session.stop_sequence(4_000).first(),
                Some(HwLiveStopStep::Wait { ms: 1_000 })
            ),
            "reset must drop the pending bytes"
        );
    }

    /// The five providers reach five different endpoints through one object,
    /// and the error arm is reachable from the foreign side.
    #[test]
    fn every_provider_connects_through_the_object_or_names_the_missing_credential() {
        let hosts = [
            (lv::LiveProvider::Deepgram, "wss://api.deepgram.com/"),
            (lv::LiveProvider::ElevenLabs, "wss://api.elevenlabs.io/"),
            (lv::LiveProvider::OpenAi, "wss://api.openai.com/"),
            (lv::LiveProvider::Grok, "wss://api.x.ai/"),
            (
                lv::LiveProvider::HyperWhisperCloud,
                "wss://transcribe-prod-v2.hyperwhisper.com/",
            ),
        ];
        for (provider, host) in hosts {
            let url = session(provider).connect().expect("connect").url;
            assert!(url.starts_with(host), "{provider:?} -> {url}");
        }

        let bare = HwLiveSession::new(HwLiveConfig {
            provider: HwLiveProvider::Deepgram,
            api_key: None,
            license_key: None,
            device_id: None,
            language: None,
            vocabulary: Vec::new(),
            model: None,
            fast_formatting: false,
            base_url: None,
            cloud_tier: None,
        });
        // `Result::expect_err` needs `T: Debug`, which the UniFFI records here
        // deliberately do not derive — the same reason `ffi_net`'s tests unwrap
        // by hand.
        let Err(err) = bare.connect() else {
            panic!("a Deepgram session with no API key must not build a URL");
        };
        assert!(matches!(err, HwLiveError::MissingCredential));
        assert_eq!(
            err.to_string(),
            "A streaming provider credential is required.",
            "the hand-written Display must match the leaf's thiserror message"
        );
    }

    /// Every `LiveEvent` arm has to survive the mirror. The arms are shaped
    /// alike, so a swapped pair compiles; only an observable value catches it.
    #[test]
    fn every_event_arm_crosses_the_boundary_intact() {
        let cases: Vec<(lv::LiveEvent, &str)> = vec![
            (
                lv::LiveEvent::SessionStarted {
                    session_id: Some("s".to_string()),
                },
                "session_started",
            ),
            (
                lv::LiveEvent::PartialTranscript {
                    text: "p".to_string(),
                },
                "partial",
            ),
            (
                lv::LiveEvent::FinalTranscript {
                    text: "f".to_string(),
                },
                "final",
            ),
            (
                lv::LiveEvent::FinalTranscriptAndSessionComplete {
                    text: "f".to_string(),
                    duration_seconds: 1.5,
                    credits_used: 2.5,
                },
                "final_and_complete",
            ),
            (
                lv::LiveEvent::SessionComplete {
                    duration_seconds: 1.5,
                    credits_used: 2.5,
                },
                "complete",
            ),
            (
                lv::LiveEvent::Error {
                    message: "e".to_string(),
                    // Carried, not reconstructed: this is the field a head's own
                    // failure taxonomy reads. See [`HwLiveErrorKind`].
                    kind: Some(lv::LiveErrorKind::RateLimited),
                },
                "error",
            ),
            (
                lv::LiveEvent::Warning {
                    message: "w".to_string(),
                },
                "warning",
            ),
            (
                lv::LiveEvent::Metadata {
                    raw: "m".to_string(),
                },
                "metadata",
            ),
            (lv::LiveEvent::Ignore, "ignore"),
        ];
        for (event, tag) in cases {
            let mirrored: HwLiveEvent = event.into();
            let seen = match mirrored {
                HwLiveEvent::SessionStarted { session_id } => {
                    assert_eq!(session_id.as_deref(), Some("s"));
                    "session_started"
                }
                HwLiveEvent::PartialTranscript { text } => {
                    assert_eq!(text, "p");
                    "partial"
                }
                HwLiveEvent::FinalTranscript { text } => {
                    assert_eq!(text, "f");
                    "final"
                }
                HwLiveEvent::FinalTranscriptAndSessionComplete {
                    text,
                    duration_seconds,
                    credits_used,
                } => {
                    assert_eq!(
                        (text.as_str(), duration_seconds, credits_used),
                        ("f", 1.5, 2.5)
                    );
                    "final_and_complete"
                }
                HwLiveEvent::SessionComplete {
                    duration_seconds,
                    credits_used,
                } => {
                    // credits_used is billing data — a mirror that dropped it
                    // would lose money silently.
                    assert_eq!((duration_seconds, credits_used), (1.5, 2.5));
                    "complete"
                }
                HwLiveEvent::Error { message, kind } => {
                    assert_eq!(message, "e");
                    assert!(
                        matches!(kind, Some(HwLiveErrorKind::RateLimited)),
                        "the error kind must survive the mirror - it is what stops \
                         a rate-limited ElevenLabs key earning two more connects"
                    );
                    "error"
                }
                HwLiveEvent::Warning { message } => {
                    assert_eq!(message, "w");
                    "warning"
                }
                HwLiveEvent::Metadata { raw } => {
                    assert_eq!(raw, "m");
                    "metadata"
                }
                HwLiveEvent::Ignore => "ignore",
            };
            assert_eq!(seen, tag, "{tag} came back as {seen}");
        }
    }

    /// The stop steps are the ordered list a head runs verbatim. All four arms,
    /// each carrying its own payload.
    #[test]
    fn every_stop_step_arm_crosses_the_boundary_intact() {
        let steps: Vec<HwLiveStopStep> = vec![
            lv::StopStep::SendText {
                text: "t".to_string(),
            },
            lv::StopStep::Wait { ms: 500 },
            lv::StopStep::WaitForSessionComplete { timeout_ms: 10_000 },
            lv::StopStep::Close,
        ]
        .into_iter()
        .map(Into::into)
        .collect();
        assert!(matches!(&steps[0], HwLiveStopStep::SendText { text } if text == "t"));
        assert!(matches!(steps[1], HwLiveStopStep::Wait { ms: 500 }));
        assert!(matches!(
            steps[2],
            HwLiveStopStep::WaitForSessionComplete { timeout_ms: 10_000 }
        ));
        assert!(matches!(steps[3], HwLiveStopStep::Close));
    }
}
