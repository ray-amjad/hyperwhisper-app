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
        LiveProvider::Deepgram | LiveProvider::Grok | LiveProvider::HyperWhisperCloud => true,
        LiveProvider::ElevenLabs | LiveProvider::OpenAi => false,
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
        LiveProvider::HyperWhisperCloud => "HyperWhisper Cloud (Streaming)",
    }
}
