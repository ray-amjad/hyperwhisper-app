//! The per-provider capability table: sample rate, vocabulary support, label.
//!
//! All three are free functions keyed on [`LiveProvider`], not members of a
//! session or a connection descriptor. Windows reads
//! [`supports_vocabulary`] on the settings page with no credential and no
//! session in hand, so anything that needs a live object is unusable there.

use super::LiveProvider;

/// The PCM sample rate, in hertz, the provider's socket expects.
///
/// The capture graph is configured from this before a session opens, so it is a
/// hard requirement rather than a preference: sending 16 kHz audio to the
/// 24 kHz endpoint produces a transcript at the wrong speed, not an error.
///
/// OpenAI's realtime endpoint is the only one at 24 kHz; the other four take
/// 16 kHz. Five providers × three implementations hardcoded these fifteen
/// literals.
pub fn required_sample_rate(provider: LiveProvider) -> u32 {
    match provider {
        LiveProvider::OpenAi => 24_000,
        LiveProvider::Deepgram
        | LiveProvider::ElevenLabs
        | LiveProvider::Grok
        | LiveProvider::GeminiTranscribe
        | LiveProvider::HyperWhisperCloud => 16_000,
    }
}

/// Whether the provider's live API takes a custom-vocabulary parameter at all.
///
/// `false` means the terms are dropped before the socket opens, and the settings
/// UI says so. Deepgram takes `keyterm`, xAI takes a keyword list and
/// HyperWhisper Cloud forwards one; ElevenLabs' realtime API has no vocabulary
/// parameter (its *batch* Scribe endpoint does, which is the trap this table
/// removes), and OpenAI's realtime transcription session has no equivalent
/// either.
pub fn supports_vocabulary(provider: LiveProvider) -> bool {
    match provider {
        LiveProvider::Deepgram
        | LiveProvider::Grok
        | LiveProvider::GeminiTranscribe
        | LiveProvider::HyperWhisperCloud => true,
        LiveProvider::ElevenLabs | LiveProvider::OpenAi => false,
    }
}

/// Whether the provider honours custom vocabulary while the language is left on
/// auto-detect.
///
/// A SECOND question from [`supports_vocabulary`], and conflating them is what
/// this function exists to stop. Deepgram Nova-3 accepts `keyterm` only in
/// monolingual mode and *silently* ignores it otherwise, so its heads withhold
/// the terms and the settings page warns. That is a Deepgram constraint and not
/// a general one: Gemini accepts `custom_vocabulary` under auto-detect
/// (verified live), and vocabulary is the headline reason to pick that
/// provider — applying Deepgram's rule to it would delete the feature for every
/// auto-detect user. xAI applies `keyterm` under auto-detect too.
///
/// HyperWhisper Cloud is the interesting arm. It answers for *the tier that
/// will actually serve the session*, because the relay forwards to a different
/// vendor per tier — so this one takes the resolved upstream rather than the
/// provider. See [`super::hw_cloud::stt_provider_for_tier`].
///
/// `false` from the providers with no vocabulary at all is unreachable in the
/// UI: every caller returns on [`supports_vocabulary`] first.
pub fn supports_vocabulary_without_language(
    provider: LiveProvider,
    cloud_tier: Option<&str>,
) -> bool {
    match provider {
        LiveProvider::Deepgram => false,
        LiveProvider::HyperWhisperCloud => {
            super::hw_cloud::stt_provider_for_tier(cloud_tier) != super::hw_cloud::DEEPGRAM_STT
        }
        LiveProvider::Grok | LiveProvider::GeminiTranscribe => true,
        LiveProvider::ElevenLabs | LiveProvider::OpenAi => false,
    }
}

/// Whether a session-complete event ends the session even when the client has
/// **not** asked to stop yet.
///
/// True for a vendor whose completion signal is emitted once, at the end of the
/// session: xAI's `transcript.done`, and HyperWhisper Cloud's
/// `session_complete` (the relay only forwards that once the client stopped —
/// `hyperwhisper-cloud/src/routes/ws-streaming-shared.ts` models exactly this
/// rule). Deepgram, ElevenLabs and OpenAI never produce a completion event at
/// all, so the answer cannot reach them and `true` keeps the pre-existing
/// unconditional behaviour.
///
/// FALSE for Gemini, and this is the whole reason the function exists.
/// `serverContent.generationComplete` is a TURN boundary: Google emits it every
/// time it finishes generating for an utterance, so a two-sentence dictation
/// sees it mid-stream with more audio still to come. Read as terminal it ends
/// the session at the first pause — the client stops waiting, the socket closes
/// straight after `audio_stream_end`, and the last utterance's final never
/// arrives.
pub fn complete_ends_session_before_stop(provider: LiveProvider) -> bool {
    match provider {
        LiveProvider::GeminiTranscribe => false,
        LiveProvider::Deepgram
        | LiveProvider::ElevenLabs
        | LiveProvider::OpenAi
        | LiveProvider::Grok
        | LiveProvider::HyperWhisperCloud => true,
    }
}

/// How long a client should hold the audio pump waiting for the provider's
/// session-started frame, in milliseconds. `0` means "send from the moment the
/// socket opens".
///
/// NOT the same question as [`LiveConnect::session_starts_on_open`]. Four
/// providers answer `false` there — they do send a session-started frame — and
/// none of them discards audio that arrives first, so no client has ever waited
/// for them. Deriving a wait from that flag would add a handshake pause to
/// OpenAI, xAI, ElevenLabs and HyperWhisper Cloud for nothing.
///
/// Gemini is the one provider that needs it: audio sent before `setupComplete`
/// arrives is dropped by the server, which costs the opening words of the
/// dictation. Five seconds is a ceiling on a failure, not an expected wait — the
/// frame arrives in tens of milliseconds, and the bound only turns a socket that
/// opened and then said nothing into a clean error instead of a hang.
///
/// [`LiveConnect::session_starts_on_open`]: super::LiveConnect::session_starts_on_open
pub fn start_timeout_ms(provider: LiveProvider) -> u32 {
    match provider {
        LiveProvider::GeminiTranscribe => 5_000,
        LiveProvider::Deepgram
        | LiveProvider::ElevenLabs
        | LiveProvider::OpenAi
        | LiveProvider::Grok
        | LiveProvider::HyperWhisperCloud => 0,
    }
}

/// The human-readable provider label stored on a history entry.
///
/// The " (Streaming)" suffix is load-bearing: it is what distinguishes a live
/// session from the same vendor's batch transcription in the history list. These
/// strings are persisted, so changing one silently re-labels nothing that is
/// already saved and splits the vendor in two going forward.
pub fn provider_label(provider: LiveProvider) -> &'static str {
    match provider {
        LiveProvider::Deepgram => "Deepgram (Streaming)",
        LiveProvider::ElevenLabs => "ElevenLabs (Streaming)",
        LiveProvider::OpenAi => "OpenAI (Streaming)",
        LiveProvider::Grok => "xAI (Streaming)",
        LiveProvider::GeminiTranscribe => "Gemini 3.5 Transcribe (Streaming)",
        LiveProvider::HyperWhisperCloud => "HyperWhisper Cloud (Streaming)",
    }
}
