//! [`LiveSession`] — the one stateful object in this module, and what it holds.
//!
//! Three of the five protocols carry state across frames: Deepgram tracks when
//! it last saw audio (to decide a keepalive), OpenAI counts un-committed PCM
//! bytes and when it last committed, and OpenAI and xAI both accumulate the
//! transcript so far so they can emit only the delta. That state is why this is
//! an object and not five more free functions.
//!
//! Everything time-shaped is a **parameter**. `control_frames(now_ms)` and
//! `stop_sequence(now_ms)` take the clock reading from the caller; the module
//! reads no clock of its own, which is what keeps the commit gate and the
//! keepalive testable without sleeping.

use super::config::{LiveConfig, LiveConnect, LiveError, LiveEvent, LiveFrame, StopStep};
use super::LiveProvider;
use super::{deepgram, elevenlabs, gemini, hw_cloud, openai, xai};

/// Everything a protocol remembers between frames.
///
/// One struct for all five rather than an enum per provider: the fields are few,
/// the providers use disjoint subsets, and [`SessionState::reset`] then has one
/// obvious implementation that cannot miss a field when a protocol grows one.
#[derive(Debug, Default)]
pub(super) struct SessionState {
    /// Deepgram: `now_ms` at the last audio send opportunity. `None` until the
    /// first one — the shipped strategies seed this from the constructor's
    /// clock read, which a sans-I/O module cannot do, so the first opportunity
    /// seeds it and never sends a keepalive. Same behaviour: a keepalive
    /// needs 3 s of silence, and no time has passed at the first chunk.
    pub(super) last_audio_ms: Option<u64>,

    /// OpenAI: PCM bytes appended since the last commit frame.
    ///
    /// A "did any audio arrive" flag is not enough. The send-opportunity hook
    /// runs BEFORE the append, so right after a periodic commit exactly one
    /// capture buffer is outstanding — and that buffer can be short. Counting
    /// bytes is what lets both the periodic path and the stop sequence answer
    /// "how much", not just "whether".
    pub(super) pending_audio_bytes: u64,

    /// OpenAI: `now_ms` at the last commit frame. `None` until the first send
    /// opportunity, which seeds it — see [`SessionState::last_audio_ms`].
    pub(super) last_commit_ms: Option<u64>,

    /// OpenAI: the last committed transcript per `item_id`.
    pub(super) committed_items: std::collections::BTreeMap<String, String>,

    /// OpenAI: the accumulated delta text per `item_id`.
    pub(super) partial_items: std::collections::BTreeMap<String, String>,

    /// xAI: the running committed transcript for the whole session.
    pub(super) committed_transcript: String,
}

impl SessionState {
    /// Forget everything. A reconnect reuses one session object and must not
    /// carry the previous socket's transcript, byte counter or clock marks into
    /// the new one.
    pub(super) fn reset(&mut self) {
        *self = Self::default();
    }
}

/// One live-streaming session: a config in, and every value the platform's
/// socket loop needs out.
///
/// The lifecycle is `new` → `connect` → (`note_audio` / `control_frames` /
/// `parse`)\* → `stop_sequence`, with `reset` returning it to the state `new`
/// left it in so a reconnect can reuse the object.
///
/// Deliberately **not** a state machine: it does not track whether it is
/// connected, and it will answer `parse` before `connect`. Connection state
/// lives in the transport, which is native, and duplicating it here would give
/// two answers to one question.
#[derive(Debug)]
pub struct LiveSession {
    config: LiveConfig,
    state: SessionState,
}

impl LiveSession {
    /// Build a session for `config`. Infallible on purpose: a missing
    /// credential is reported by [`LiveSession::connect`], which is the call
    /// that needs it, so the constructor a foreign language sees never throws.
    pub fn new(config: LiveConfig) -> Self {
        Self {
            config,
            state: SessionState::default(),
        }
    }

    /// The provider this session speaks.
    pub fn provider(&self) -> LiveProvider {
        self.config.provider
    }

    /// Everything needed to open the socket. Resets the per-connection state,
    /// so calling it again after a drop is the whole reconnect preparation.
    ///
    /// Fails only with [`LiveError::MissingCredential`] — see that arm.
    pub fn connect(&mut self) -> Result<LiveConnect, LiveError> {
        self.state.reset();
        match self.config.provider {
            LiveProvider::Deepgram => deepgram::connect(&self.config),
            LiveProvider::ElevenLabs => elevenlabs::connect(&self.config),
            LiveProvider::OpenAi => openai::connect(&self.config),
            LiveProvider::Grok => xai::connect(&self.config),
            LiveProvider::GeminiTranscribe => gemini::connect(&self.config),
            LiveProvider::HyperWhisperCloud => hw_cloud::connect(&self.config),
        }
    }

    /// Record that `byte_count` bytes of PCM were just handed to the socket.
    ///
    /// A **count**, never the bytes. The samples themselves never enter this
    /// crate — see the module docs. Only OpenAI reads the counter; the call is
    /// free for the other four and callers send it unconditionally.
    pub fn note_audio(&mut self, byte_count: u64) {
        self.state.pending_audio_bytes = self.state.pending_audio_bytes.saturating_add(byte_count);
    }

    /// Frames to send at an audio send opportunity, given the caller's clock.
    ///
    /// Called once per captured chunk, **before** that chunk is encoded and
    /// sent. Usually empty. Deepgram answers a keepalive after 3 s of silence;
    /// OpenAI answers a periodic commit when both its interval and its byte
    /// floor are clear.
    pub fn control_frames(&mut self, now_ms: u64) -> Vec<LiveFrame> {
        match self.config.provider {
            LiveProvider::Deepgram => deepgram::control_frames(&mut self.state, now_ms),
            LiveProvider::OpenAi => openai::control_frames(&mut self.state, now_ms),
            LiveProvider::ElevenLabs
            | LiveProvider::Grok
            | LiveProvider::GeminiTranscribe
            | LiveProvider::HyperWhisperCloud => Vec::new(),
        }
    }

    /// Read one text message off the socket.
    ///
    /// Anything that is not valid JSON is [`LiveEvent::Ignore`], matching every
    /// shipped parser: all five wrap their decode in a try/catch and return
    /// "no event" rather than failing the session, because a provider adding a
    /// frame shape must not end a recording in progress.
    pub fn parse(&mut self, text: &str) -> LiveEvent {
        let Ok(root) = serde_json::from_str::<serde_json::Value>(text) else {
            return LiveEvent::Ignore;
        };
        if !root.is_object() {
            return LiveEvent::Ignore;
        }
        match self.config.provider {
            LiveProvider::Deepgram => deepgram::parse(&root, text),
            LiveProvider::ElevenLabs => elevenlabs::parse(&root),
            LiveProvider::OpenAi => openai::parse(&mut self.state, &root),
            LiveProvider::Grok => xai::parse(&mut self.state, &root),
            LiveProvider::GeminiTranscribe => gemini::parse(&root, text),
            LiveProvider::HyperWhisperCloud => hw_cloud::parse(&root),
        }
    }

    /// The ordered stop path, given the caller's clock.
    ///
    /// `now_ms` is used only by OpenAI, and only to mark the commit clock when
    /// the stop path claims the outstanding audio — so a session that is
    /// stopped, reset and reconnected does not carry a stale commit mark. The
    /// other four answer a constant list.
    pub fn stop_sequence(&mut self, now_ms: u64) -> Vec<StopStep> {
        match self.config.provider {
            LiveProvider::Deepgram => deepgram::stop_sequence(),
            LiveProvider::ElevenLabs => elevenlabs::stop_sequence(),
            LiveProvider::OpenAi => openai::stop_sequence(&mut self.state, now_ms),
            LiveProvider::Grok => xai::stop_sequence(),
            LiveProvider::GeminiTranscribe => gemini::stop_sequence(),
            LiveProvider::HyperWhisperCloud => hw_cloud::stop_sequence(),
        }
    }

    /// Forget every frame this session has seen. What makes a reconnect able to
    /// reuse one object instead of rebuilding it from the config.
    pub fn reset(&mut self) {
        self.state.reset();
    }
}
