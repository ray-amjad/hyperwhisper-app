//! The conformance suite for the `live` module.
//!
//! The classification cases are a port of macOS
//! `hyperwhisperTests/StreamingProviderErrorPolicyTests.swift`, assertion for
//! assertion. That Swift file stays in place and unchanged: while the Swift
//! facade survives, it is the proof that the Rust port is behaviour-identical.
//!
//! Fixtures use generic provider wording only: no real endpoints, keys,
//! organisation ids or user ids.

use super::*;

// ===========================================================================
// Error messages — terminal
// ===========================================================================

#[test]
fn no_credits_remaining_is_terminal() {
    assert_eq!(
        classify_error_message("You have no credits remaining. Add credits to continue."),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn insufficient_quota_code_is_terminal() {
    // The wording that produced this cluster: the machine-readable
    // `insufficient_quota` code is dropped by the strategy's decoder, so it is
    // only ever seen where the provider also echoes it into the message.
    assert_eq!(
        classify_error_message("insufficient_quota: the account has run out of credit."),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn exceeded_current_quota_is_terminal() {
    assert_eq!(
        classify_error_message(
            "You exceeded your current quota, please check your plan and billing details."
        ),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn incorrect_api_key_is_terminal() {
    assert_eq!(
        classify_error_message(
            "Incorrect API key provided. Check the key configured for this provider."
        ),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn unauthorized_is_terminal_regardless_of_case() {
    // Pins the lowercasing: providers capitalise these words inconsistently
    // between the status line and the message body.
    assert_eq!(
        classify_error_message("Unauthorized: authentication failed for this session."),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn forbidden_is_terminal() {
    assert_eq!(
        classify_error_message("Forbidden: this key is not permitted to use realtime transcription."),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn inactive_account_is_terminal() {
    assert_eq!(
        classify_error_message("The account is not active. Reactivate it to keep transcribing."),
        LiveErrorOutcome::Terminal
    );
}

/// Every marker has to be reachable. A typo in one of the twenty is otherwise
/// invisible: the list still compiles and the suite above only covers seven.
#[test]
fn every_terminal_marker_classifies_its_own_wording() {
    for marker in TERMINAL_ERROR_MARKERS {
        assert_eq!(
            classify_error_message(&format!("Provider said: {marker}. Please act.")),
            LiveErrorOutcome::Terminal,
            "marker {marker:?} did not match its own wording"
        );
    }
}

#[test]
fn the_marker_list_is_twenty_lowercase_entries() {
    // 20, not 19 — the count is called out in the issue because an earlier
    // audit miscounted it. Uppercase in a marker can never match, because the
    // haystack is lowercased and the needle is not.
    assert_eq!(TERMINAL_ERROR_MARKERS.len(), 20);
    for marker in TERMINAL_ERROR_MARKERS {
        assert_eq!(marker, marker.to_lowercase(), "marker {marker:?} is not lowercase");
    }
}

// ===========================================================================
// Error messages — transient
// ===========================================================================

#[test]
fn network_drop_is_transient() {
    // The case auto-reconnect exists for — misclassifying it would remove a
    // working recovery path, which is the expensive direction of this split.
    assert_eq!(
        classify_error_message("Connection reset by peer while streaming audio."),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn rate_limited_message_is_transient() {
    // The deliberate asymmetry with `exceeded_current_quota_is_terminal`:
    // providers return quota exhaustion under a `rate_limit_error` type, but
    // classification reads the message text, so a plain rate limit — which
    // clears by itself in seconds — stays retryable.
    assert_eq!(
        classify_error_message("Rate limit reached for requests. Please try again in 20s."),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn request_id_that_contains_four_zero_one_stays_transient() {
    // Guards the trap the policy is written around: matching a bare "401" (or
    // "403") substring would brand this transient upstream failure terminal,
    // because provider payloads embed request ids with those digits in them.
    assert_eq!(
        classify_error_message("Stream interrupted (request_id: req_4013f2c8). Please retry."),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn generic_server_failure_is_transient() {
    assert_eq!(
        classify_error_message("Internal server error while processing the audio stream."),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn strategy_fallback_message_is_transient() {
    // A provider error frame with no message body falls back to a generic
    // string in the strategy. Nothing about it says the credentials are dead.
    assert_eq!(
        classify_error_message("Realtime transcription failed"),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn empty_message_is_transient() {
    // Unknown wording keeps today's behaviour rather than silently losing its
    // reconnect — the default has to be the conservative direction.
    assert_eq!(classify_error_message(""), LiveErrorOutcome::Transient);
}

// ===========================================================================
// Wording this codebase actually emits
//
// Everything above is hypothetical provider wording, which guards the matching
// rules but not the coupling that matters: the policy only helps if it
// recognises the exact strings the app's own providers put on the wire.
// ===========================================================================

#[test]
fn hyperwhisper_cloud_credit_exhaustion_is_terminal() {
    // THE FLAGSHIP CASE: the default provider's credit-exhaustion frame.
    assert_eq!(
        classify_error_message("Credit balance exhausted"),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn elevenlabs_auth_error_wording_is_terminal() {
    // The ElevenLabs `auth_error` message, verbatim. It also arrives before the
    // session-started frame, which is the case that has to fail startup rather
    // than wait out the connection timeout.
    assert_eq!(
        classify_error_message(
            "ElevenLabs authentication failed. Please check your API key in Settings."
        ),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn elevenlabs_quota_exceeded_wording_is_terminal() {
    assert_eq!(
        classify_error_message("ElevenLabs quota exceeded. Please check your account billing."),
        LiveErrorOutcome::Terminal
    );
}

#[test]
fn elevenlabs_rate_limit_wording_is_transient() {
    // The live half of the rate-limit/quota asymmetry. Too many concurrent
    // sockets from one key clears by itself, so the reconnect must survive.
    assert_eq!(
        classify_error_message("ElevenLabs rate limit reached. Please try again in a moment."),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn openai_strategy_fallback_wording_is_transient() {
    assert_eq!(
        classify_error_message("OpenAI Realtime transcription failed"),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn xai_strategy_fallback_wording_is_transient() {
    assert_eq!(
        classify_error_message("xAI streaming transcription failed"),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn hyperwhisper_cloud_strategy_fallback_wording_is_transient() {
    assert_eq!(
        classify_error_message("Unknown server error"),
        LiveErrorOutcome::Transient
    );
}

#[test]
fn hyperwhisper_cloud_transient_frames_keep_their_reconnect() {
    // The rest of the HyperWhisper Cloud error frames. Every one is a
    // service-side or in-flight condition a fresh socket can recover from.
    //
    // "Deepgram API key not configured" names the *service's* upstream key, not
    // the user's, so it is deliberately not one of the user-fixable account
    // states; it falls through to the conservative default.
    for message in [
        "Transcription service error",
        "Transcription service busy, audio dropped",
        "Audio stream too large",
        "Audio chunk too large",
        "WebSocket error",
        "Deepgram API key not configured",
    ] {
        assert_eq!(
            classify_error_message(message),
            LiveErrorOutcome::Transient,
            "expected transient for {message}"
        );
    }
}

// ===========================================================================
// Refused upgrades
// ===========================================================================

#[test]
fn payment_required_upgrade_is_insufficient_credits() {
    // The other half of running out of credits: the user who already has none.
    assert_eq!(
        upgrade_refusal(402),
        Some(LiveUpgradeRefusal::InsufficientCredits)
    );
}

#[test]
fn refused_credential_upgrades_are_unauthorized() {
    assert_eq!(upgrade_refusal(401), Some(LiveUpgradeRefusal::Unauthorized));
    assert_eq!(upgrade_refusal(403), Some(LiveUpgradeRefusal::Unauthorized));
}

#[test]
fn recoverable_upgrade_statuses_keep_their_reconnect() {
    // A rate limit clears on its own, a 5xx is the service's problem and not
    // the user's, and a proxy mangling the upgrade is exactly what the
    // reconnect exists for.
    for status in [0, 200, 400, 404, 408, 429, 500, 502, 503, 504] {
        assert_eq!(
            upgrade_refusal(status),
            None,
            "expected no refusal for HTTP {status}"
        );
    }
}

#[test]
fn a_successful_upgrade_is_not_a_refusal() {
    // 101 is the status a socket that actually opened carries, so every
    // mid-session drop reads its response and finds nothing here.
    assert_eq!(upgrade_refusal(101), None);
}

// ===========================================================================
// Close codes
// ===========================================================================

#[test]
fn the_rfc_6455_non_recoverable_codes_are_terminal() {
    for code in [1002u16, 1003, 1007, 1008, 1009, 1011] {
        assert!(is_terminal_close_code(code), "expected {code} terminal");
    }
}

#[test]
fn the_standard_transient_close_codes_keep_their_reconnect() {
    // 1000 is handled separately by the caller; 1001/1006/1012/1013 are exactly
    // what the reconnect path exists for. 1010 and 1015 are unassigned to this
    // policy and default to recoverable, like anything else unrecognised.
    for code in [1000u16, 1001, 1004, 1005, 1006, 1010, 1012, 1013, 1015] {
        assert!(!is_terminal_close_code(code), "expected {code} transient");
    }
}

#[test]
fn provider_private_close_codes_are_not_this_functions_business() {
    // HyperWhisper Cloud's own 4001/4002 are a provider extension macOS layers
    // on top of this set in its client. This function must not claim them, or
    // the two heads that do not know about them would inherit half a rule.
    assert!(!is_terminal_close_code(4001));
    assert!(!is_terminal_close_code(4002));
}

// ===========================================================================
// Language normalization
//
// Cases drawn from all seven copies this replaces.
// ===========================================================================

#[test]
fn omitted_language_selections_normalize_to_none() {
    assert_eq!(normalize_language(None), None);
    assert_eq!(normalize_language(Some("")), None);
    assert_eq!(normalize_language(Some("   ")), None);
    assert_eq!(normalize_language(Some("\t\n")), None);
}

#[test]
fn the_auto_sentinel_omits_the_parameter() {
    // "auto" is the app's own sentinel, never a provider language code. Every
    // shipped copy dropped it; sending the literal string would be a request
    // for a language called "auto".
    assert_eq!(normalize_language(Some("auto")), None);
    assert_eq!(normalize_language(Some("AUTO")), None);
    assert_eq!(normalize_language(Some("  Auto  ")), None);
}

#[test]
fn a_plain_code_survives_trimmed_and_lowercased() {
    assert_eq!(normalize_language(Some("en")), Some("en".to_string()));
    assert_eq!(normalize_language(Some(" EN ")), Some("en".to_string()));
    assert_eq!(normalize_language(Some("Fr")), Some("fr".to_string()));
}

#[test]
fn a_tagged_code_drops_to_its_primary_subtag() {
    assert_eq!(normalize_language(Some("en-US")), Some("en".to_string()));
    assert_eq!(normalize_language(Some("en-GB")), Some("en".to_string()));
    assert_eq!(normalize_language(Some("zh-Hans")), Some("zh".to_string()));
    assert_eq!(
        normalize_language(Some("  PT-BR  ")),
        Some("pt".to_string())
    );
    // More than one subtag: only the first survives.
    assert_eq!(
        normalize_language(Some("zh-Hant-TW")),
        Some("zh".to_string())
    );
}

#[test]
fn a_leading_hyphen_cannot_emit_an_empty_language() {
    // Unreachable from the picker, but the function has to be total: an empty
    // primary subtag must never reach a query string as `language=`.
    assert_eq!(normalize_language(Some("-en")), None);
    assert_eq!(normalize_language(Some("-")), None);
}

// ===========================================================================
// Capabilities
// ===========================================================================

#[test]
fn each_provider_carries_its_own_capability_row() {
    // Asserted as one table so a swapped arm shows up as a wrong value rather
    // than compiling silently.
    let expected = [
        (LiveProvider::Deepgram, 16_000u32, true, "Deepgram (Streaming)"),
        (
            LiveProvider::ElevenLabs,
            16_000,
            false,
            "ElevenLabs (Streaming)",
        ),
        (LiveProvider::OpenAi, 24_000, false, "OpenAI (Streaming)"),
        (LiveProvider::Grok, 16_000, true, "xAI (Streaming)"),
        (
            LiveProvider::HyperWhisperCloud,
            16_000,
            true,
            "HyperWhisper Cloud (Streaming)",
        ),
    ];

    for (provider, rate, vocabulary, label) in expected {
        assert_eq!(required_sample_rate(provider), rate, "{provider:?} rate");
        assert_eq!(
            supports_vocabulary(provider),
            vocabulary,
            "{provider:?} vocabulary"
        );
        assert_eq!(provider_label(provider), label, "{provider:?} label");
    }
}

#[test]
fn every_provider_label_is_distinct_and_marked_streaming() {
    // The suffix is what separates a live session from the same vendor's batch
    // entry in the history list.
    let mut labels: Vec<&str> = LiveProvider::ALL.iter().copied().map(provider_label).collect();
    labels.sort_unstable();
    let count = labels.len();
    labels.dedup();
    assert_eq!(labels.len(), count, "two providers share a history label");
    for label in labels {
        assert!(label.ends_with(" (Streaming)"), "{label} lost its suffix");
    }
}

#[test]
fn openai_is_the_only_provider_off_16_khz() {
    // Pins the one divergence the capture graph has to be configured for.
    for provider in LiveProvider::ALL {
        let rate = required_sample_rate(provider);
        if provider == LiveProvider::OpenAi {
            assert_eq!(rate, 24_000);
        } else {
            assert_eq!(rate, 16_000, "{provider:?} moved off 16 kHz");
        }
    }
}
