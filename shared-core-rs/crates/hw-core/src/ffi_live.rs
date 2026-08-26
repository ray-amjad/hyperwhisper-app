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
}
