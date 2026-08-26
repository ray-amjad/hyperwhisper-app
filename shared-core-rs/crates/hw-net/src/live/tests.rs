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
    //
    // The wording carries `unauthorized` and NOTHING else from the marker list.
    // It used to read "Unauthorized: authentication failed for this session.",
    // which also matched `authentication failed` — so the test named for one
    // marker passed even with that marker misspelled.
    assert_eq!(
        classify_error_message("Unauthorized: this key cannot open a realtime session."),
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

/// One realistic provider sentence per marker, and each one matches ITS OWN
/// marker and no other.
///
/// This replaced a loop that built the haystack out of the needle
/// (`format!("Provider said: {marker}.")`) against a `contains()` matcher. That
/// loop could not fail: it asserted that a string containing a marker contains
/// that marker. Misspelling each of the twenty entries in turn and running the
/// whole suite caught 8 and let 12 through green — including `unauthorized`,
/// `quota exceeded`, `invalid api key`, `permission denied`, `billing` and
/// `payment required`. Phase 1 deleted the Swift list, so there is no second
/// source left to catch a typo either.
///
/// The uniqueness half is what makes it bite. If a fixture matched two markers,
/// breaking one of them would leave the other to rescue the assertion and the
/// typo would stay invisible — which is exactly how
/// `unauthorized_is_terminal_regardless_of_case` used to pass with
/// `unauthorized` misspelled.
///
/// Keys are spelled out here rather than indexed out of
/// [`TERMINAL_ERROR_MARKERS`], so a typo in the policy shows up as a missing
/// key as well as a fixture that stopped classifying.
const MARKER_FIXTURES: [(&str, &str); 20] = [
    (
        "credit balance exhausted",
        "Credit balance exhausted. Top up to keep streaming.",
    ),
    (
        "no credits remaining",
        "You have no credits remaining on this workspace.",
    ),
    (
        "insufficient credits",
        "Session refused: insufficient credits for a realtime stream.",
    ),
    (
        "insufficient_quota",
        r#"{"code":"insufficient_quota","type":"invalid_request_error"}"#,
    ),
    (
        "insufficient quota",
        "The organization has insufficient quota for this model.",
    ),
    (
        "exceeded your current quota",
        "You exceeded your current quota, please check your plan.",
    ),
    (
        "quota exceeded",
        "Quota exceeded for realtime transcription minutes.",
    ),
    (
        "invalid api key",
        "Invalid API key. Generate a new one and try again.",
    ),
    (
        "incorrect api key",
        "Incorrect API key provided for this endpoint.",
    ),
    ("invalid_api_key", r#"{"error":{"code":"invalid_api_key"}}"#),
    (
        "api key not valid",
        "API key not valid for realtime transcription.",
    ),
    (
        "unauthorized",
        "Unauthorized: this key cannot open a realtime session.",
    ),
    (
        "authentication_error",
        r#"{"type":"authentication_error","message":"key rejected"}"#,
    ),
    (
        "authentication failed",
        "Authentication failed for the realtime endpoint.",
    ),
    (
        "forbidden",
        "Forbidden: realtime transcription is not enabled for this workspace.",
    ),
    (
        "permission_denied",
        r#"{"status":"PERMISSION_DENIED","reason":"model access"}"#,
    ),
    (
        "permission denied",
        "Permission denied for the streaming scope on this token.",
    ),
    (
        "billing",
        "Add a billing method to continue using realtime transcription.",
    ),
    (
        "payment required",
        "Payment required before another streaming session can start.",
    ),
    (
        "account is not active",
        "This account is not active. Reactivate it to keep transcribing.",
    ),
];

#[test]
fn every_terminal_marker_classifies_a_realistic_sentence_of_its_own() {
    for (marker, fixture) in MARKER_FIXTURES {
        assert_eq!(
            classify_error_message(fixture),
            LiveErrorOutcome::Terminal,
            "marker {marker:?} did not classify its own wording: {fixture:?}"
        );
    }
}

#[test]
fn each_marker_fixture_matches_exactly_one_marker() {
    for (marker, fixture) in MARKER_FIXTURES {
        let haystack = fixture.to_lowercase();
        let matched: Vec<&str> = TERMINAL_ERROR_MARKERS
            .iter()
            .copied()
            .filter(|candidate| haystack.contains(candidate))
            .collect();
        assert_eq!(
            matched,
            [marker],
            "fixture {fixture:?} must match {marker:?} and nothing else"
        );
    }
}

#[test]
fn the_fixture_table_covers_the_marker_list_exactly() {
    // 20, not 19 — the count is called out in the issue because an earlier
    // audit miscounted it.
    assert_eq!(TERMINAL_ERROR_MARKERS.len(), 20);
    assert_eq!(MARKER_FIXTURES.len(), TERMINAL_ERROR_MARKERS.len());

    for marker in TERMINAL_ERROR_MARKERS {
        // Uppercase in a marker can never match: the haystack is lowercased and
        // the needle is not.
        assert_eq!(
            marker,
            marker.to_lowercase(),
            "marker {marker:?} is not lowercase"
        );
        assert!(
            MARKER_FIXTURES.iter().any(|(key, _)| *key == marker),
            "marker {marker:?} has no fixture - add one to MARKER_FIXTURES"
        );
    }
    for (key, _) in MARKER_FIXTURES {
        assert!(
            TERMINAL_ERROR_MARKERS.contains(&key),
            "fixture key {key:?} is not a marker - the policy list moved under this table"
        );
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
            "ElevenLabs authentication failed. Check that your ElevenLabs API key is correct and still active."
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

#[test]
fn the_two_readings_agree_on_when_to_omit_the_parameter() {
    // The half the five providers share. Whatever else the split does, a blank
    // selection and the `auto` sentinel must reach neither wire.
    for omitted in [None, Some(""), Some("   "), Some("\t\n"), Some("auto"), Some("AUTO"), Some("  Auto  ")] {
        assert_eq!(normalize_language(omitted), None, "{omitted:?}");
        assert_eq!(language_tag(omitted), None, "{omitted:?}");
    }
}

#[test]
fn the_language_tag_reading_keeps_the_region_and_the_case() {
    // Deepgram and the HyperWhisper Cloud relay: the subtags are content, and
    // the published codes are mixed case.
    assert_eq!(language_tag(Some("zh-TW")), Some("zh-TW".to_string()));
    assert_eq!(language_tag(Some("zh-Hans")), Some("zh-Hans".to_string()));
    assert_eq!(language_tag(Some("en-GB")), Some("en-GB".to_string()));
    assert_eq!(language_tag(Some("  pt-BR  ")), Some("pt-BR".to_string()));
    assert_eq!(language_tag(Some("es-419")), Some("es-419".to_string()));
    assert_eq!(language_tag(Some("en")), Some("en".to_string()));
}

#[test]
fn the_two_readings_diverge_exactly_where_the_providers_do() {
    // The one assertion that would have caught the truncation: `zh-TW` must
    // survive on the two tag providers and must NOT on the three ISO-639-1 ones,
    // whose parameters would reject or ignore a region.
    assert_eq!(normalize_language(Some("zh-TW")), Some("zh".to_string()));
    assert_eq!(language_tag(Some("zh-TW")), Some("zh-TW".to_string()));
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

// ===========================================================================
// LiveSession — the five protocols
//
// The connect assertions pin the URL, headers and subprotocols against what
// the shipped strategies send today, parameter for parameter and in order: a
// query pair moved or dropped is a wire change, and this is the only place it
// can be caught before a provider sees it.
// ===========================================================================

/// A session for `provider` with an API key, nothing else set.
fn keyed(provider: LiveProvider) -> LiveSession {
    let mut config = LiveConfig::new(provider);
    config.api_key = Some("test-key".to_string());
    LiveSession::new(config)
}

/// A connectable session for any provider: four want an API key, HyperWhisper
/// Cloud wants a license key. Setting both keeps the every-provider loops from
/// having to special-case one arm.
fn credentialed(provider: LiveProvider) -> LiveSession {
    let mut config = LiveConfig::new(provider);
    config.api_key = Some("k".to_string());
    config.license_key = Some("k".to_string());
    LiveSession::new(config)
}

fn connect_of(session: &mut LiveSession) -> LiveConnect {
    session.connect().expect("connect")
}

fn header_of(connect: &LiveConnect, name: &str) -> Option<String> {
    connect
        .headers
        .iter()
        .find(|h| h.name == name)
        .map(|h| h.value.clone())
}

// ---------------------------------------------------------------------------
// Credentials
// ---------------------------------------------------------------------------

#[test]
fn every_provider_refuses_to_connect_without_a_credential() {
    // Whitespace counts as missing: a key of spaces is a misconfiguration, and
    // every shipped strategy guards with IsNullOrWhiteSpace / isEmpty.
    for provider in LiveProvider::ALL {
        for blank in [None, Some(String::new()), Some("   ".to_string())] {
            let mut config = LiveConfig::new(provider);
            config.api_key = blank.clone();
            config.license_key = blank.clone();
            config.device_id = blank;
            assert_eq!(
                LiveSession::new(config).connect(),
                Err(LiveError::MissingCredential),
                "{provider:?} built a URL with no credential"
            );
        }
    }
}

#[test]
fn hyperwhisper_cloud_falls_back_from_the_license_key_to_the_device_id() {
    let mut trial = LiveConfig::new(LiveProvider::HyperWhisperCloud);
    trial.device_id = Some("device-1".to_string());
    let url = LiveSession::new(trial).connect().expect("connect").url;
    assert!(url.ends_with("?device_id=device-1"), "{url}");

    let mut licensed = LiveConfig::new(LiveProvider::HyperWhisperCloud);
    licensed.device_id = Some("device-1".to_string());
    licensed.license_key = Some("HW-1".to_string());
    let url = LiveSession::new(licensed).connect().expect("connect").url;
    assert!(
        url.ends_with("?license_key=HW-1"),
        "a license key must win outright, not append: {url}"
    );
}

// ---------------------------------------------------------------------------
// Deepgram
// ---------------------------------------------------------------------------

#[test]
fn deepgram_connect_sends_the_thirteen_parameters_and_detects_the_language() {
    let connect = connect_of(&mut keyed(LiveProvider::Deepgram));
    assert_eq!(
        connect.url,
        "wss://api.deepgram.com/v1/listen\
         ?model=nova-3-general&encoding=linear16&sample_rate=16000&channels=1\
         &smart_format=true&punctuate=true&filler_words=true&no_delay=false\
         &endpointing=300&utterance_end_ms=1500&interim_results=true&vad_events=true\
         &mip_opt_out=true&detect_language=true"
    );
    // The key rides as a subprotocol, not a header - Deepgram's own rule.
    assert!(connect.headers.is_empty());
    assert_eq!(connect.subprotocols, ["token", "test-key"]);
    assert_eq!(connect.framing, AudioFraming::Binary);
    assert!(connect.start_frames.is_empty());
    assert!(
        connect.session_starts_on_open,
        "Deepgram's Metadata frame only arrives after audio; waiting for it deadlocks"
    );
}

#[test]
fn deepgram_fast_formatting_flips_no_delay() {
    let mut config = LiveConfig::new(LiveProvider::Deepgram);
    config.api_key = Some("k".to_string());
    config.fast_formatting = true;
    let url = LiveSession::new(config).connect().expect("connect").url;
    assert!(url.contains("&no_delay=true&"), "{url}");
}

#[test]
fn deepgram_resolves_a_removed_model_and_never_emits_a_bare_model_parameter() {
    // The leak the audit found: an empty-but-present model returned Some("")
    // on macOS and reached the wire as `model=`.
    for model in [None, Some(String::new()), Some("  ".to_string())] {
        let mut config = LiveConfig::new(LiveProvider::Deepgram);
        config.api_key = Some("k".to_string());
        config.model = model.clone();
        let url = LiveSession::new(config).connect().expect("connect").url;
        assert!(
            url.starts_with("wss://api.deepgram.com/v1/listen?model=nova-3-general&"),
            "{model:?} produced {url}"
        );
    }

    let mut removed = LiveConfig::new(LiveProvider::Deepgram);
    removed.api_key = Some("k".to_string());
    removed.model = Some("nova-2-meeting".to_string());
    let url = LiveSession::new(removed).connect().expect("connect").url;
    assert!(url.contains("model=nova-3-general&"), "{url}");

    let mut live = LiveConfig::new(LiveProvider::Deepgram);
    live.api_key = Some("k".to_string());
    live.model = Some("nova-3-medical".to_string());
    let url = LiveSession::new(live).connect().expect("connect").url;
    assert!(url.contains("model=nova-3-medical&"), "{url}");
}

#[test]
fn deepgram_sends_keyterms_only_with_an_explicit_language() {
    let mut config = LiveConfig::new(LiveProvider::Deepgram);
    config.api_key = Some("k".to_string());
    config.vocabulary = vec!["UniFFI".to_string(), "Rust core".to_string()];

    let auto = LiveSession::new(config.clone()).connect().expect("connect");
    assert!(
        auto.url.ends_with("&detect_language=true"),
        "Deepgram ignores keyterms under auto-detect, so they must not be sent: {}",
        auto.url
    );

    // The tag is sent verbatim, region included: `en-US` and `en-GB` are
    // different Deepgram codes. See `deepgram_sends_the_language_tag_verbatim`.
    config.language = Some("en-US".to_string());
    let explicit = LiveSession::new(config).connect().expect("connect");
    assert!(
        explicit
            .url
            .ends_with("&language=en-US&keyterm=UniFFI&keyterm=Rust%20core"),
        "{}",
        explicit.url
    );
}

/// The regression this pins is silent and wrong in the worst direction: a user
/// who picked Traditional Chinese would have been transcribed in Simplified.
///
/// `zh-TW` is the one region-tagged entry in the Windows picker's language list
/// and it is stored verbatim, so it is what arrives here. Deepgram's code list
/// treats it as a different language from `zh`, and both shipped .NET strategies
/// sent it unchanged. Truncating to the primary subtag — which the three
/// ISO-639-1 providers need — must not reach this one.
#[test]
fn deepgram_sends_the_language_tag_verbatim() {
    let cases = [
        ("zh-TW", "language=zh-TW"),
        ("zh-Hans", "language=zh-Hans"),
        ("pt-BR", "language=pt-BR"),
        ("es-419", "language=es-419"),
        // Case is preserved too: Deepgram publishes mixed-case codes.
        ("  en-GB  ", "language=en-GB"),
        ("en", "language=en"),
    ];
    for (selection, expected) in cases {
        let mut config = LiveConfig::new(LiveProvider::Deepgram);
        config.api_key = Some("k".to_string());
        config.language = Some(selection.to_string());
        let url = LiveSession::new(config).connect().expect("connect").url;
        assert!(
            url.contains(&format!("&{expected}")),
            "{selection:?} must reach Deepgram as {expected:?}: {url}"
        );
        assert!(
            !url.contains("detect_language=true"),
            "an explicit language must not also ask for auto-detect: {url}"
        );
    }
}

#[test]
fn deepgram_keepalives_only_after_three_seconds_of_silence() {
    let mut session = keyed(LiveProvider::Deepgram);
    connect_of(&mut session);

    // The first opportunity seeds the clock and never keepalives: no time has
    // passed at the first chunk.
    assert!(session.control_frames(10_000).is_empty());
    assert!(session.control_frames(12_000).is_empty(), "2 s is not idle");
    assert!(
        session.control_frames(15_000).is_empty(),
        "exactly 3 s is not yet idle - the shipped rule is a strict >"
    );
    assert_eq!(
        session.control_frames(18_001),
        [LiveFrame::text(r#"{"type":"KeepAlive"}"#)]
    );
    assert!(
        session.control_frames(18_002).is_empty(),
        "the keepalive must reset the idle clock"
    );
}

#[test]
fn deepgram_stop_waits_between_finalize_and_close_stream() {
    let mut session = keyed(LiveProvider::Deepgram);
    assert_eq!(
        session.stop_sequence(0),
        [
            StopStep::SendText { text: r#"{"type":"Finalize"}"#.to_string() },
            StopStep::Wait { ms: 500 },
            StopStep::SendText { text: r#"{"type":"CloseStream"}"#.to_string() },
            StopStep::Close,
        ],
        "sending both frames back to back lets the close beat the flush"
    );
}

#[test]
fn deepgram_parses_results_metadata_and_the_polymorphic_channel() {
    let mut session = keyed(LiveProvider::Deepgram);

    assert_eq!(
        session.parse(r#"{"type":"Metadata","request_id":"req-1"}"#),
        LiveEvent::SessionStarted { session_id: Some("req-1".to_string()) }
    );
    assert_eq!(
        session.parse(
            r#"{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"hello"}]}}"#
        ),
        LiveEvent::FinalTranscript { text: "hello".to_string() }
    );
    assert_eq!(
        session.parse(
            r#"{"type":"Results","is_final":false,"channel":{"alternatives":[{"transcript":"hel"}]}}"#
        ),
        LiveEvent::PartialTranscript { text: "hel".to_string() }
    );
    assert_eq!(
        session.parse(r#"{"type":"Results","channel":{"alternatives":[{"transcript":"   "}]}}"#),
        LiveEvent::Ignore,
        "a whitespace-only alternative is not a transcript"
    );

    // Issue #106: `channel` is an object on Results and an array of channel
    // indices on the voice-activity frames. A decoder that insists on the
    // object shape throws here and makes this arm unreachable.
    let raw = r#"{"type":"UtteranceEnd","channel":[0,1],"last_word_end":1.2}"#;
    assert_eq!(session.parse(raw), LiveEvent::Metadata { raw: raw.to_string() });
    let raw = r#"{"type":"SpeechStarted","channel":[0,1]}"#;
    assert_eq!(session.parse(raw), LiveEvent::Metadata { raw: raw.to_string() });
}

// ---------------------------------------------------------------------------
// ElevenLabs
// ---------------------------------------------------------------------------

#[test]
fn elevenlabs_connect_carries_the_key_in_a_header() {
    let connect = connect_of(&mut keyed(LiveProvider::ElevenLabs));
    assert_eq!(
        connect.url,
        "wss://api.elevenlabs.io/v1/speech-to-text/realtime\
         ?model_id=scribe_v2_realtime&audio_format=pcm_16000&commit_strategy=vad\
         &vad_silence_threshold_secs=1.5&vad_threshold=0.4"
    );
    assert_eq!(header_of(&connect, "xi-api-key").as_deref(), Some("test-key"));
    assert!(connect.subprotocols.is_empty());
    assert!(!connect.session_starts_on_open);
}

#[test]
fn elevenlabs_language_code_is_the_normalized_primary_subtag() {
    let mut config = LiveConfig::new(LiveProvider::ElevenLabs);
    config.api_key = Some("k".to_string());
    config.language = Some("EN-US".to_string());
    let url = LiveSession::new(config.clone()).connect().expect("connect").url;
    assert!(url.ends_with("&language_code=en"), "{url}");

    config.language = Some("auto".to_string());
    let url = LiveSession::new(config).connect().expect("connect").url;
    assert!(
        url.ends_with("&vad_threshold=0.4"),
        "auto means omit the parameter, not send the word: {url}"
    );
}

#[test]
fn elevenlabs_stop_is_a_bare_close() {
    let mut session = keyed(LiveProvider::ElevenLabs);
    assert_eq!(
        session.stop_sequence(0),
        [StopStep::Close],
        "commit_strategy=vad leaves nothing to flush and nothing to drain for"
    );
}

#[test]
fn elevenlabs_parses_transcripts_and_its_three_error_types() {
    let mut session = keyed(LiveProvider::ElevenLabs);
    assert_eq!(
        session.parse(r#"{"message_type":"session_started"}"#),
        LiveEvent::SessionStarted { session_id: None }
    );
    assert_eq!(
        session.parse(r#"{"message_type":"partial_transcript","text":"hel"}"#),
        LiveEvent::PartialTranscript { text: "hel".to_string() }
    );
    assert_eq!(
        session.parse(r#"{"message_type":"committed_transcript","text":"hello"}"#),
        LiveEvent::FinalTranscript { text: "hello".to_string() }
    );
    // The wording is ours - these frames carry no message. Only auth_error
    // ever diverged between the heads.
    assert_eq!(
        session.parse(r#"{"message_type":"auth_error"}"#),
        LiveEvent::Error {
            message: "ElevenLabs authentication failed. Check that your ElevenLabs API key is correct and still active."
                .to_string(),
            kind: Some(LiveErrorKind::Unauthorized),
        }
    );
    assert_eq!(
        session.parse(r#"{"message_type":"quota_exceeded"}"#),
        LiveEvent::Error {
            message: "ElevenLabs quota exceeded. Please check your account billing.".to_string(),
            kind: Some(LiveErrorKind::QuotaExceeded),
        }
    );
    assert_eq!(
        session.parse(r#"{"message_type":"rate_limited"}"#),
        LiveEvent::Error {
            message: "ElevenLabs rate limit reached. Please try again in a moment.".to_string(),
            kind: Some(LiveErrorKind::RateLimited),
        }
    );
}

// ---------------------------------------------------------------------------
// OpenAI - connect and the commit gate
// ---------------------------------------------------------------------------

#[test]
fn openai_connect_sends_the_session_update_byte_for_byte() {
    let connect = connect_of(&mut keyed(LiveProvider::OpenAi));
    assert_eq!(connect.url, "wss://api.openai.com/v1/realtime?intent=transcription");
    assert_eq!(header_of(&connect, "Authorization").as_deref(), Some("Bearer test-key"));
    assert_eq!(connect.sample_rate, 24_000);
    // Byte-identical to the frame both .NET heads serialize today, so a head
    // that swaps its own for this one changes nothing on the wire.
    assert_eq!(
        connect.start_frames,
        [LiveFrame::text(
            r#"{"type":"session.update","session":{"type":"transcription","audio":{"input":{"format":{"type":"audio/pcm","rate":24000},"transcription":{"model":"gpt-realtime-whisper"},"turn_detection":null}}}}"#
        )],
        "turn_detection:null is load-bearing - it disables server-side VAD"
    );
}

#[test]
fn openai_session_update_carries_a_normalized_language_when_one_is_chosen() {
    let mut config = LiveConfig::new(LiveProvider::OpenAi);
    config.api_key = Some("k".to_string());
    config.language = Some("PT-BR".to_string());
    let connect = LiveSession::new(config).connect().expect("connect");
    assert_eq!(
        connect.start_frames[0].data,
        r#"{"type":"session.update","session":{"type":"transcription","audio":{"input":{"format":{"type":"audio/pcm","rate":24000},"transcription":{"model":"gpt-realtime-whisper","language":"pt"},"turn_detection":null}}}}"#
    );
    assert!(
        serde_json::from_str::<serde_json::Value>(&connect.start_frames[0].data).is_ok(),
        "the hand-built frame must still be valid JSON"
    );
}

#[test]
fn openai_commits_only_once_both_the_interval_and_the_byte_floor_are_clear() {
    let commit = LiveFrame::text(r#"{"type":"input_audio_buffer.commit"}"#);
    let mut session = keyed(LiveProvider::OpenAi);
    connect_of(&mut session);

    // First opportunity seeds the commit clock.
    assert!(session.control_frames(0).is_empty());

    // Interval clear, byte floor not: no commit, and the clock stays stale so
    // the next chunk over the floor commits immediately rather than waiting
    // out another 1.2 s.
    session.note_audio(4_799);
    assert!(session.control_frames(5_000).is_empty(), "4799 bytes is under 100 ms");
    session.note_audio(1);
    assert_eq!(session.control_frames(5_001), [commit.clone()], "4800 bytes is exactly 100 ms");

    // Byte floor clear, interval not.
    session.note_audio(9_600);
    assert!(
        session.control_frames(6_000).is_empty(),
        "999 ms after the last commit is inside the 1.2 s interval"
    );
    assert_eq!(session.control_frames(6_201), [commit], "1200 ms is the boundary and it fires");
    assert!(
        session.control_frames(9_000).is_empty(),
        "the commit consumed the bytes; there is nothing left to commit"
    );
}

#[test]
fn openai_stop_commits_the_tail_only_when_the_server_would_accept_it() {
    // The HYPERWHISPER-S8 / -S9 boundary. Below the floor the tail is dropped
    // on purpose: committing it is rejected outright and surfaces as a
    // spurious error toast, and it was lost to the rejection anyway.
    let mut short = keyed(LiveProvider::OpenAi);
    connect_of(&mut short);
    short.note_audio(4_799);
    assert_eq!(
        short.stop_sequence(1_000),
        [StopStep::Wait { ms: 1_000 }, StopStep::Close],
        "under 100 ms of tail must send no commit"
    );

    let mut exact = keyed(LiveProvider::OpenAi);
    connect_of(&mut exact);
    exact.note_audio(4_800);
    assert_eq!(
        exact.stop_sequence(1_000),
        [
            StopStep::SendText { text: r#"{"type":"input_audio_buffer.commit"}"#.to_string() },
            StopStep::Wait { ms: 1_000 },
            StopStep::Close,
        ],
        "exactly 100 ms qualifies - the server's rule is 'at least'"
    );
}

#[test]
fn openai_keeps_the_stop_wait_even_when_nothing_was_committed() {
    // The completed event for the LAST PERIODIC commit can still be in flight.
    // Closing immediately trades the toast for a truncated transcript.
    let mut session = keyed(LiveProvider::OpenAi);
    connect_of(&mut session);
    assert_eq!(session.stop_sequence(0), [StopStep::Wait { ms: 1_000 }, StopStep::Close]);
}

#[test]
fn openai_periodic_and_stop_paths_cannot_both_claim_the_same_bytes() {
    let mut session = keyed(LiveProvider::OpenAi);
    connect_of(&mut session);
    assert!(session.control_frames(0).is_empty());
    session.note_audio(4_800);
    assert_eq!(session.control_frames(2_000).len(), 1, "the periodic path claims them");
    assert_eq!(
        session.stop_sequence(2_001),
        [StopStep::Wait { ms: 1_000 }, StopStep::Close],
        "the stop path must not commit the same buffer a second time"
    );
}

/// Ported from macOS `OpenAIStreamingCommitGateTests`
/// `stopAfterAPeriodicCommitStillCommitsATailOverTheMinimum`.
///
/// The test above pins one direction — the stop path must not RE-commit what the
/// periodic path already took — and on its own it is satisfied by a stop path
/// that never commits again at all once a periodic commit has fired. This is the
/// case that says the gate is a CLAIM ON THE BUFFER, not a latch on the session:
/// audio that arrived after the last periodic commit is still owed a commit, and
/// dropping it truncates the last words of every recording longer than 1.2 s.
#[test]
fn openai_stop_still_commits_a_tail_that_arrived_after_a_periodic_commit() {
    let mut session = keyed(LiveProvider::OpenAi);
    connect_of(&mut session);
    assert!(
        session.control_frames(0).is_empty(),
        "the first opportunity only seeds the clock"
    );

    session.note_audio(4_800);
    assert_eq!(
        session.control_frames(2_000).len(),
        1,
        "the periodic path claims the first buffer"
    );

    // A fresh tail over the floor, accumulated after that commit.
    session.note_audio(4_800);
    assert_eq!(
        session.stop_sequence(2_001),
        [
            StopStep::SendText { text: r#"{"type":"input_audio_buffer.commit"}"#.to_string() },
            StopStep::Wait { ms: 1_000 },
            StopStep::Close,
        ],
        "the tail after the last periodic commit must still be committed"
    );
}

/// Ported from macOS `OpenAIStreamingCommitGateTests`
/// `stopSequenceCommitsTheSameAudioOnlyOnce`.
///
/// `claim_committable` checks and resets in one operation, which is what stops
/// the periodic path and the stop path both claiming one buffer. This asserts
/// the same property within the stop path itself: a second `stop_sequence` — a
/// double stop, or a reconnect that stops the old session — must not commit the
/// buffer the first one already sent, which the server answers with a rejected
/// empty commit and the client surfaces as a spurious error toast.
#[test]
fn openai_stop_commits_the_same_audio_only_once() {
    let mut session = keyed(LiveProvider::OpenAi);
    connect_of(&mut session);
    session.note_audio(4_800);

    let first = session.stop_sequence(1_000);
    assert_eq!(
        first,
        [
            StopStep::SendText { text: r#"{"type":"input_audio_buffer.commit"}"#.to_string() },
            StopStep::Wait { ms: 1_000 },
            StopStep::Close,
        ],
        "the first stop claims the pending buffer"
    );
    assert_eq!(
        session.stop_sequence(1_001),
        [StopStep::Wait { ms: 1_000 }, StopStep::Close],
        "the second stop must not commit the buffer the first one already claimed"
    );
}

#[test]
fn openai_accumulates_deltas_per_item_and_finals_the_new_suffix() {
    let mut session = keyed(LiveProvider::OpenAi);
    let delta = |item: &str, text: &str| {
        format!(
            r#"{{"type":"conversation.item.input_audio_transcription.delta","item_id":"{item}","delta":"{text}"}}"#
        )
    };
    assert_eq!(
        session.parse(&delta("a", "Hello")),
        LiveEvent::PartialTranscript { text: "Hello".to_string() }
    );
    assert_eq!(
        session.parse(&delta("a", " there")),
        LiveEvent::PartialTranscript { text: "Hello there".to_string() },
        "a partial is the whole interim utterance, not the fragment"
    );
    // Two items in one session are unrelated strings, not a continuation.
    assert_eq!(
        session.parse(&delta("b", "Second")),
        LiveEvent::PartialTranscript { text: "Second".to_string() }
    );

    let completed = |item: &str, text: &str| {
        format!(
            r#"{{"type":"conversation.item.input_audio_transcription.completed","item_id":"{item}","transcript":"{text}"}}"#
        )
    };
    assert_eq!(
        session.parse(&completed("a", "Hello there")),
        LiveEvent::FinalTranscript { text: "Hello there".to_string() }
    );
    assert_eq!(
        session.parse(&completed("a", "Hello there world")),
        LiveEvent::FinalTranscript { text: "world".to_string() },
        "a re-completion of the same item emits only what is new"
    );
    assert_eq!(
        session.parse(&completed("a", "Hello there world")),
        LiveEvent::Ignore,
        "nothing new means no event"
    );
    assert_eq!(
        session.parse(&completed("a", "Entirely different")),
        LiveEvent::FinalTranscript { text: "Entirely different".to_string() },
        "a revision that is not an extension is emitted whole"
    );
}

/// A delta that is only a space is the word break, and dropping it runs two
/// words together.
///
/// The port took `shared-dotnet`'s `IsNullOrWhiteSpace` test, which was 1 of the
/// 3 shipped behaviours: macOS and Windows both accumulated a whitespace-only
/// delta. And it is not display-only — the Windows client inserts the current
/// partial as the session's final text on a terminal close, so `Helloworld` can
/// reach the document.
#[test]
fn openai_accumulates_a_whitespace_only_delta_instead_of_dropping_it() {
    let mut session = keyed(LiveProvider::OpenAi);
    let delta = |text: &str| {
        format!(
            r#"{{"type":"conversation.item.input_audio_transcription.delta","item_id":"a","delta":"{text}"}}"#
        )
    };

    assert_eq!(
        session.parse(&delta("Hello")),
        LiveEvent::PartialTranscript { text: "Hello".to_string() }
    );
    assert_eq!(
        session.parse(&delta(" ")),
        LiveEvent::PartialTranscript { text: "Hello ".to_string() },
        "the space between two words is content"
    );
    assert_eq!(
        session.parse(&delta("world")),
        LiveEvent::PartialTranscript { text: "Hello world".to_string() },
        "dropping the space would accumulate to Helloworld"
    );

    // A genuinely empty delta still carries nothing and is still ignored.
    assert_eq!(session.parse(&delta("")), LiveEvent::Ignore);
}

#[test]
fn openai_error_frames_carry_the_nested_wording_to_the_classifier() {
    let mut session = keyed(LiveProvider::OpenAi);
    assert_eq!(
        session.parse(r#"{"type":"error","error":{"message":"You exceeded your current quota"}}"#),
        LiveEvent::Error { message: "You exceeded your current quota".to_string(), kind: None }
    );
    assert_eq!(
        classify_error_message("You exceeded your current quota"),
        LiveErrorOutcome::Terminal,
        "the whole point of carrying the wording"
    );
    assert_eq!(
        session.parse(r#"{"type":"error","error":{}}"#),
        LiveEvent::Error { message: "OpenAI Realtime transcription failed".to_string(), kind: None },
        "a wordless error frame must not reach a user as a blank alert"
    );
}

// ---------------------------------------------------------------------------
// xAI
// ---------------------------------------------------------------------------

#[test]
fn xai_connect_filters_the_language_through_the_batch_support_set() {
    let mut config = LiveConfig::new(LiveProvider::Grok);
    config.api_key = Some("test-key".to_string());
    let connect = LiveSession::new(config.clone()).connect().expect("connect");
    assert_eq!(
        connect.url,
        "wss://api.x.ai/v1/stt?sample_rate=16000&encoding=pcm&interim_results=true&endpointing=300"
    );
    assert_eq!(header_of(&connect, "Authorization").as_deref(), Some("Bearer test-key"));

    config.language = Some("tl".to_string());
    let url = LiveSession::new(config.clone()).connect().expect("connect").url;
    assert!(url.ends_with("&language=fil"), "tl aliases to fil: {url}");

    config.language = Some("cy".to_string());
    let url = LiveSession::new(config).connect().expect("connect").url;
    assert!(
        url.ends_with("&endpointing=300"),
        "an unsupported code means omit the parameter: {url}"
    );
}

#[test]
fn xai_keyterms_use_the_batch_paths_vendor_caps_with_no_language_gate() {
    let mut config = LiveConfig::new(LiveProvider::Grok);
    config.api_key = Some("k".to_string());
    config.vocabulary = vec![
        "UniFFI".to_string(),
        "a".repeat(51),
        "uniffi".to_string(),
        "Rust core".to_string(),
    ];
    let url = LiveSession::new(config).connect().expect("connect").url;
    assert!(
        url.ends_with("&keyterm=UniFFI&keyterm=Rust%20core"),
        "over 50 characters is an xAI vendor limit; the duplicate is case-insensitive; \
         and unlike Deepgram there is no language gate: {url}"
    );
}

#[test]
fn xai_stop_waits_on_the_completion_event_not_a_duration() {
    let mut session = keyed(LiveProvider::Grok);
    assert_eq!(
        session.stop_sequence(0),
        [
            StopStep::SendText { text: r#"{"type":"audio.done"}"#.to_string() },
            StopStep::WaitForSessionComplete { timeout_ms: 10_000 },
            StopStep::Close,
        ]
    );
}

#[test]
fn xai_emits_prefix_deltas_and_folds_the_last_final_into_the_completion() {
    let mut session = keyed(LiveProvider::Grok);
    assert_eq!(
        session.parse(r#"{"type":"transcript.created"}"#),
        LiveEvent::SessionStarted { session_id: None }
    );
    assert_eq!(
        session.parse(r#"{"type":"transcript.partial","text":"Hel"}"#),
        LiveEvent::PartialTranscript { text: "Hel".to_string() }
    );
    assert_eq!(
        session.parse(r#"{"type":"transcript.partial","text":"Hello","is_final":true}"#),
        LiveEvent::FinalTranscript { text: "Hello".to_string() }
    );
    assert_eq!(
        session.parse(r#"{"type":"transcript.partial","text":"Hello there","is_final":true}"#),
        LiveEvent::FinalTranscript { text: "there".to_string() },
        "xAI resends the whole transcript; only the delta is new"
    );
    assert_eq!(
        session.parse(r#"{"type":"transcript.partial","text":"Hello","is_final":true}"#),
        LiveEvent::Ignore,
        "a retraction must not re-emit, and must not rewind the committed text"
    );
    // transcript.done is BOTH the last final and the end of the session. A
    // client that saw only SessionComplete here would drop the trailing words.
    assert_eq!(
        session.parse(r#"{"type":"transcript.done","text":"Hello there world","duration":4.5}"#),
        LiveEvent::FinalTranscriptAndSessionComplete {
            text: "world".to_string(),
            duration_seconds: 4.5,
            credits_used: 0.0,
        }
    );
    assert_eq!(
        session.parse(r#"{"type":"transcript.done","text":"Hello there world","duration":4.5}"#),
        LiveEvent::SessionComplete { duration_seconds: 4.5, credits_used: 0.0 },
        "nothing new left means a plain completion"
    );
    assert_eq!(
        session.parse(r#"{"type":"error","message":"Credit balance exhausted"}"#),
        LiveEvent::Error { message: "Credit balance exhausted".to_string(), kind: None }
    );
}

// ---------------------------------------------------------------------------
// HyperWhisper Cloud
// ---------------------------------------------------------------------------

#[test]
fn hyperwhisper_cloud_connect_gates_vocabulary_on_an_explicit_language() {
    let mut config = LiveConfig::new(LiveProvider::HyperWhisperCloud);
    config.license_key = Some("HW-KEY-1".to_string());
    config.vocabulary = vec!["UniFFI".to_string(), "Rust core".to_string()];

    let auto = LiveSession::new(config.clone()).connect().expect("connect");
    assert_eq!(
        auto.url,
        "wss://transcribe-prod-v2.hyperwhisper.com/ws/streaming-deepgram?license_key=HW-KEY-1",
        "this endpoint relays to Deepgram, which ignores keyterms under auto-detect"
    );
    assert!(auto.headers.is_empty(), "client identity is the platform's to add, not the core's");
    assert_eq!(auto.framing, AudioFraming::Binary);

    // Verbatim, for the reason Deepgram's is: this endpoint relays the tag on to
    // Deepgram, whose code list distinguishes the regions.
    config.language = Some("en-GB".to_string());
    let explicit = LiveSession::new(config).connect().expect("connect");
    assert_eq!(
        explicit.url,
        "wss://transcribe-prod-v2.hyperwhisper.com/ws/streaming-deepgram\
         ?license_key=HW-KEY-1&language=en-GB&vocabulary=UniFFI%2C%20Rust%20core"
    );
}

/// The relay's half of `deepgram_sends_the_language_tag_verbatim`.
#[test]
fn hyperwhisper_cloud_sends_the_language_tag_verbatim() {
    let mut config = LiveConfig::new(LiveProvider::HyperWhisperCloud);
    config.license_key = Some("HW-KEY-1".to_string());
    config.language = Some("zh-TW".to_string());
    let url = LiveSession::new(config).connect().expect("connect").url;
    assert!(url.contains("&language=zh-TW"), "{url}");
}

#[test]
fn hyperwhisper_cloud_honours_a_configured_backend_and_maps_the_scheme() {
    // Without this a DEBUG build silently bills a developer's key against
    // production. The heads store one https base URL for REST and websockets.
    let cases = [
        ("https://transcribe-staging-v2.hyperwhisper.com", "wss://transcribe-staging-v2.hyperwhisper.com"),
        ("http://localhost:8080/", "ws://localhost:8080"),
        ("wss://already.example.com", "wss://already.example.com"),
    ];
    for (base, expected) in cases {
        let mut config = LiveConfig::new(LiveProvider::HyperWhisperCloud);
        config.device_id = Some("d".to_string());
        config.base_url = Some(base.to_string());
        let url = LiveSession::new(config).connect().expect("connect").url;
        assert_eq!(url, format!("{expected}/ws/streaming-deepgram?device_id=d"));
    }
}

#[test]
fn hyperwhisper_cloud_stop_waits_ten_seconds_for_the_credits_figure() {
    let mut config = LiveConfig::new(LiveProvider::HyperWhisperCloud);
    config.device_id = Some("d".to_string());
    let mut session = LiveSession::new(config);
    assert_eq!(
        session.stop_sequence(0),
        [
            StopStep::SendText { text: r#"{"type":"stop"}"#.to_string() },
            StopStep::WaitForSessionComplete { timeout_ms: 10_000 },
            StopStep::Close,
        ],
        "macOS hard-closes 500 ms after stop and loses the credits_used it carries"
    );
}

#[test]
fn hyperwhisper_cloud_parses_the_full_superset_including_warnings() {
    let mut config = LiveConfig::new(LiveProvider::HyperWhisperCloud);
    config.device_id = Some("d".to_string());
    let mut session = LiveSession::new(config);

    assert_eq!(
        session.parse(r#"{"type":"ready","sessionId":"s-1"}"#),
        LiveEvent::SessionStarted { session_id: Some("s-1".to_string()) }
    );
    assert_eq!(
        session.parse(r#"{"type":"transcript","text":"hel"}"#),
        LiveEvent::PartialTranscript { text: "hel".to_string() }
    );
    assert_eq!(
        session.parse(r#"{"type":"transcript","text":"hello","is_final":true}"#),
        LiveEvent::FinalTranscript { text: "hello".to_string() }
    );
    // credits_used is billing data. It arrives once, here, and nowhere else.
    assert_eq!(
        session.parse(r#"{"type":"session_complete","duration_seconds":12.5,"credits_used":3.25}"#),
        LiveEvent::SessionComplete { duration_seconds: 12.5, credits_used: 3.25 }
    );
    assert_eq!(
        session.parse(r#"{"type":"session_complete"}"#),
        LiveEvent::SessionComplete { duration_seconds: 0.0, credits_used: 0.0 }
    );
    assert_eq!(
        session.parse(r#"{"type":"error","message":"Credit balance exhausted"}"#),
        LiveEvent::Error { message: "Credit balance exhausted".to_string(), kind: None }
    );
    assert_eq!(
        session.parse(r#"{"type":"error"}"#),
        LiveEvent::Error { message: "Unknown server error".to_string(), kind: None }
    );
    // The only provider that warns. remaining_seconds rides along and no head
    // has ever read it, so it is not carried.
    assert_eq!(
        session.parse(r#"{"type":"warning","message":"90 seconds remaining","remaining_seconds":90}"#),
        LiveEvent::Warning { message: "90 seconds remaining".to_string() }
    );
    assert_eq!(
        session.parse(r#"{"type":"warning"}"#),
        LiveEvent::Warning { message: "Server warning".to_string() }
    );
}

// ---------------------------------------------------------------------------
// Cross-cutting
// ---------------------------------------------------------------------------

#[test]
fn the_audio_framing_descriptor_reproduces_the_shipped_json_frame_byte_for_byte() {
    // The controlling constraint: audio never crosses the FFI boundary, so the
    // core answers a prefix and a suffix once and the platform does the base64
    // and the concatenation. This proves the two literals are enough - that
    // there is no per-chunk variation the descriptor cannot express.
    //
    // The base64 here is real: `"HyperWhisper"` encoded. It has no `+`, so the
    // .NET heads' System.Text.Json output is byte-identical too (their default
    // encoder escapes a plus sign; see the module docs).
    let base64 = "SHlwZXJXaGlzcGVy";

    let cases = [
        (
            LiveProvider::ElevenLabs,
            format!(
                r#"{{"message_type":"input_audio_chunk","audio_base_64":"{base64}","commit":false,"sample_rate":16000}}"#
            ),
        ),
        (
            LiveProvider::OpenAi,
            format!(r#"{{"type":"input_audio_buffer.append","audio":"{base64}"}}"#),
        ),
    ];

    for (provider, expected) in cases {
        let connect = connect_of(&mut keyed(provider));
        let AudioFraming::Base64Json { prefix, suffix } = connect.framing else {
            panic!("{provider:?} must frame its audio as base64 JSON");
        };
        assert_eq!(
            format!("{prefix}{base64}{suffix}"),
            expected,
            "{provider:?} frame is not what the shipped strategy sends"
        );
        assert!(
            serde_json::from_str::<serde_json::Value>(&expected).is_ok(),
            "{provider:?} frame is not valid JSON"
        );
    }
}

/// The end-to-end shape of the two-reading split, on the wire each provider
/// actually sees.
///
/// The unit tests above pin the two functions; this pins the wiring, which is
/// where the truncation bug lived — one normalizer was called from all five
/// arms. A future arm that picks the wrong reading changes a line here.
#[test]
fn a_region_tagged_selection_reaches_each_provider_the_way_its_api_spells_it() {
    // Everything a provider is told at connect time: four carry the language in
    // the query string, OpenAI carries it in its `session.update` start frame.
    let wire = |provider: LiveProvider, selection: &str| -> String {
        let mut config = LiveConfig::new(provider);
        config.api_key = Some("k".to_string());
        config.license_key = Some("k".to_string());
        config.language = Some(selection.to_string());
        let connect = LiveSession::new(config).connect().expect("connect");
        let frames = connect
            .start_frames
            .iter()
            .map(|frame| frame.data.as_str())
            .collect::<Vec<_>>()
            .join(" ");
        format!("{} {frames}", connect.url)
    };

    // Verbatim: their code lists distinguish the regions.
    assert!(wire(LiveProvider::Deepgram, "zh-TW").contains("&language=zh-TW"));
    assert!(wire(LiveProvider::HyperWhisperCloud, "zh-TW").contains("&language=zh-TW"));

    // Primary subtag: their parameters are documented ISO-639-1.
    let elevenlabs = wire(LiveProvider::ElevenLabs, "zh-TW");
    assert!(elevenlabs.contains("&language_code=zh"), "{elevenlabs}");
    assert!(!elevenlabs.contains("zh-TW"), "{elevenlabs}");

    let openai = wire(LiveProvider::OpenAi, "zh-TW");
    assert!(openai.contains(r#""language":"zh""#), "{openai}");
    assert!(!openai.contains("zh-TW"), "{openai}");

    // xAI's list is 25 primary subtags and `zh` is not one of them, so `zh-TW`
    // omits the parameter entirely. `pt-BR` is the arm that shows the
    // truncation rather than the support filter.
    let xai_zh = wire(LiveProvider::Grok, "zh-TW");
    assert!(!xai_zh.contains("language="), "{xai_zh}");
    let xai_pt = wire(LiveProvider::Grok, "pt-BR");
    assert!(xai_pt.contains("&language=pt"), "{xai_pt}");
    assert!(!xai_pt.contains("pt-BR"), "{xai_pt}");
}

#[test]
fn note_audio_is_a_count_and_the_framing_is_the_only_route_audio_takes() {
    // The guard for the rule `hw-net/src/contract.rs` states. Every provider
    // either sends the PCM as-is or wraps it in a fixed envelope; there is no
    // third shape, and in neither case does the core see a sample.
    for provider in LiveProvider::ALL {
        let mut session = credentialed(provider);
        let connect = connect_of(&mut session);
        match &connect.framing {
            AudioFraming::Binary => {}
            AudioFraming::Base64Json { prefix, suffix } => {
                assert!(prefix.ends_with('"'), "{provider:?} prefix must end mid-string");
                assert!(suffix.starts_with('"'), "{provider:?} suffix must resume mid-string");
            }
        }
        // A count, not bytes. Accepting a `Vec<u8>` here is the shape this
        // whole module is built to avoid.
        session.note_audio(u64::from(connect.sample_rate) * 2);
    }
}

#[test]
fn reset_clears_every_protocol_s_accumulated_state() {
    // What makes a reconnect reuse one session object instead of rebuilding it.
    let mut xai = keyed(LiveProvider::Grok);
    assert_eq!(
        xai.parse(r#"{"type":"transcript.partial","text":"Hello","is_final":true}"#),
        LiveEvent::FinalTranscript { text: "Hello".to_string() }
    );
    xai.reset();
    assert_eq!(
        xai.parse(r#"{"type":"transcript.partial","text":"Hello","is_final":true}"#),
        LiveEvent::FinalTranscript { text: "Hello".to_string() },
        "the previous socket's transcript must not suppress the new one's first final"
    );

    let mut openai = keyed(LiveProvider::OpenAi);
    connect_of(&mut openai);
    openai.note_audio(9_600);
    openai.reset();
    assert_eq!(
        openai.stop_sequence(0),
        [StopStep::Wait { ms: 1_000 }, StopStep::Close],
        "bytes appended to the dropped socket must not be committed to the new one"
    );

    // connect() resets too - that is the whole reconnect preparation.
    let mut deepgram = keyed(LiveProvider::Deepgram);
    connect_of(&mut deepgram);
    assert!(deepgram.control_frames(0).is_empty());
    connect_of(&mut deepgram);
    assert!(
        deepgram.control_frames(60_000).is_empty(),
        "a reconnect must not open with a keepalive for the old socket's silence"
    );
}

#[test]
fn a_frame_that_is_not_a_json_object_is_ignored_by_every_provider() {
    // Every shipped parser wraps its decode in a try/catch and returns "no
    // event": a provider adding a frame shape must never end a recording.
    for provider in LiveProvider::ALL {
        let mut session = credentialed(provider);
        for frame in ["", "not json", "[1,2,3]", "null", "\"a string\"", "{}", "{\"type\":42}"] {
            assert_eq!(
                session.parse(frame),
                LiveEvent::Ignore,
                "{provider:?} must ignore {frame:?}"
            );
        }
    }
}

#[test]
fn the_connect_descriptor_agrees_with_the_free_capability_functions() {
    // Two sources for one number is how the fifteen implementations drifted.
    for provider in LiveProvider::ALL {
        let connect = credentialed(provider).connect().expect("connect");
        assert_eq!(
            connect.sample_rate,
            required_sample_rate(provider),
            "{provider:?} connect rate disagrees with the capability table"
        );
    }
}

#[test]
fn only_deepgram_starts_its_session_on_the_handshake() {
    for provider in LiveProvider::ALL {
        let connect = credentialed(provider).connect().expect("connect");
        assert_eq!(
            connect.session_starts_on_open,
            provider == LiveProvider::Deepgram,
            "{provider:?} start-on-open changed"
        );
    }
}
