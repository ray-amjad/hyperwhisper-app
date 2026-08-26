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

/// The five websocket transcription providers. Mirrors `lv::LiveProvider`.
///
/// Local engines (Parakeet, Nemotron) are deliberately absent — they are not
/// websocket protocols. Windows spells this vendor set with `Xai` where this
/// enum says `Grok`; the head maps across at its boundary.
#[derive(uniffi::Enum)]
pub enum HwLiveProvider {
    Deepgram,
    ElevenLabs,
    OpenAi,
    Grok,
    HyperWhisperCloud,
}

impl From<HwLiveProvider> for lv::LiveProvider {
    fn from(p: HwLiveProvider) -> Self {
        match p {
            HwLiveProvider::Deepgram => lv::LiveProvider::Deepgram,
            HwLiveProvider::ElevenLabs => lv::LiveProvider::ElevenLabs,
            HwLiveProvider::OpenAi => lv::LiveProvider::OpenAi,
            HwLiveProvider::Grok => lv::LiveProvider::Grok,
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
            lv::LiveEvent::Error { message } => HwLiveEvent::Error { message },
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

    const ALL: [lv::LiveProvider; 5] = lv::LiveProvider::ALL;

    fn hw_tag(p: &HwLiveProvider) -> &'static str {
        match p {
            HwLiveProvider::Deepgram => "deepgram",
            HwLiveProvider::ElevenLabs => "elevenlabs",
            HwLiveProvider::OpenAi => "openai",
            HwLiveProvider::Grok => "grok",
            HwLiveProvider::HyperWhisperCloud => "hyperwhisper_cloud",
        }
    }

    fn live_tag(p: &lv::LiveProvider) -> &'static str {
        match p {
            lv::LiveProvider::Deepgram => "deepgram",
            lv::LiveProvider::ElevenLabs => "elevenlabs",
            lv::LiveProvider::OpenAi => "openai",
            lv::LiveProvider::Grok => "grok",
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
        let cases: [(lv::LiveProvider, u32, bool, &str); 5] = [
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
            (lv::LiveProvider::OpenAi, 24_000, false, "OpenAI (Streaming)"),
            (lv::LiveProvider::Grok, 16_000, true, "xAI (Streaming)"),
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
        })
    }

    /// AUDIO MUST NEVER CROSS THIS BOUNDARY. The batch path is guarded by
    /// `ffi_net`'s `audio_is_referenced_by_path_and_never_carried_as_bytes`;
    /// this is the live path's version of the same rule.
    ///
    /// Two things make it hold, and both are asserted here. `note_audio` takes
    /// a COUNT — if it ever grew a `Vec<u8>` parameter, the call below would
    /// stop compiling. And [`HwAudioFraming`] *describes* the wrapping rather
    /// than performing it, so the base64 and the concatenation happen natively
    /// on bytes the platform already holds.
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
        assert_eq!(session.control_frames(2_000).len(), 1, "the periodic commit");

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
                HwLiveEvent::Error { message } => {
                    assert_eq!(message, "e");
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
