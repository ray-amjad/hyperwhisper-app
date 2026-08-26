//! Tests for the LLM post-processing builder.
//!
//! Bodies are asserted as parsed JSON, not as byte strings, so a key-order
//! change in `serde_json` cannot fail a test for the wrong reason — except in
//! [`groq_cap_is_the_only_difference_from_a_plain_openai_body`], which compares
//! two bodies this crate produced against each other.

use super::*;
use crate::contract::Body;

fn body_json(request: &HttpRequest) -> serde_json::Value {
    match &request.body {
        Body::Bytes { content_type, data } => {
            assert_eq!(content_type, "application/json");
            serde_json::from_slice(data).expect("body is JSON")
        }
        other => panic!("expected a Bytes body, got {other:?}"),
    }
}

fn header<'a>(request: &'a HttpRequest, name: &str) -> Option<&'a str> {
    request
        .headers
        .iter()
        .find(|h| h.name.eq_ignore_ascii_case(name))
        .map(|h| h.value.as_str())
}

fn params(provider: LlmProvider) -> LlmParams {
    LlmParams {
        provider,
        model: "test-model".to_string(),
        api_key: "sk-test".to_string(),
        system_prompt: "You clean up transcripts.".to_string(),
        system_info: "App: Slack".to_string(),
        transcript: "hello there".to_string(),
        ..Default::default()
    }
}

// ---------------------------------------------------------------------------
// Endpoint table
// ---------------------------------------------------------------------------

#[test]
fn endpoint_table_matches_every_shipped_url() {
    let cases = [
        (
            LlmProvider::OpenAi,
            "https://api.openai.com/v1/chat/completions",
        ),
        (
            LlmProvider::Anthropic,
            "https://api.anthropic.com/v1/messages",
        ),
        (
            LlmProvider::Gemini,
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        ),
        (
            LlmProvider::Groq,
            "https://api.groq.com/openai/v1/chat/completions",
        ),
        (LlmProvider::Grok, "https://api.x.ai/v1/chat/completions"),
        (
            LlmProvider::Cerebras,
            "https://api.cerebras.ai/v1/chat/completions",
        ),
        (
            LlmProvider::Mistral,
            "https://api.mistral.ai/v1/chat/completions",
        ),
    ];
    for (provider, expected) in cases {
        let request = build_llm_request(&params(provider)).unwrap();
        assert_eq!(request.url, expected, "provider={provider:?}");
        assert_eq!(request.method, HttpMethod::Post);
    }
}

#[test]
fn hw_cloud_defaults_to_prod_and_honours_a_staging_base() {
    let mut p = params(LlmProvider::HyperWhisperCloud);
    p.license_key = Some("LIC-1".to_string());
    let request = build_llm_request(&p).unwrap();
    assert_eq!(
        request.url,
        "https://transcribe-prod-v2.hyperwhisper.com/post-process"
    );

    // The Linux head hardcoded prod with no DEBUG switch, so every dev run
    // billed production credits. One base override settles it for all heads.
    p.base_url = Some(HW_CLOUD_STAGING_BASE.to_string());
    let request = build_llm_request(&p).unwrap();
    assert_eq!(
        request.url,
        "https://transcribe-staging-v2.hyperwhisper.com/post-process"
    );
}

#[test]
fn hw_cloud_base_tolerates_a_trailing_slash() {
    let mut p = params(LlmProvider::HyperWhisperCloud);
    p.device_id = Some("dev-1".to_string());
    p.base_url = Some("https://transcribe-staging-v2.hyperwhisper.com/".to_string());
    let request = build_llm_request(&p).unwrap();
    assert_eq!(
        request.url,
        "https://transcribe-staging-v2.hyperwhisper.com/post-process"
    );
}

#[test]
fn local_llama_uses_the_llama_server_port() {
    let request = build_llm_request(&params(LlmProvider::LocalLlama)).unwrap();
    assert_eq!(request.url, "http://127.0.0.1:37219/v1/chat/completions");

    let mut p = params(LlmProvider::LocalLlama);
    p.local_llama_port = Some(9090);
    let request = build_llm_request(&p).unwrap();
    assert_eq!(request.url, "http://127.0.0.1:9090/v1/chat/completions");
}

// ---------------------------------------------------------------------------
// Auth headers
// ---------------------------------------------------------------------------

#[test]
fn bearer_auth_for_every_openai_style_provider() {
    for provider in [
        LlmProvider::OpenAi,
        LlmProvider::Gemini,
        LlmProvider::Groq,
        LlmProvider::Grok,
        LlmProvider::Cerebras,
        LlmProvider::Mistral,
    ] {
        let request = build_llm_request(&params(provider)).unwrap();
        assert_eq!(
            header(&request, "Authorization"),
            Some("Bearer sk-test"),
            "provider={provider:?}"
        );
        assert_eq!(header(&request, "x-api-key"), None, "provider={provider:?}");
    }
}

#[test]
fn anthropic_uses_x_api_key_and_pins_the_version() {
    let request = build_llm_request(&params(LlmProvider::Anthropic)).unwrap();
    assert_eq!(header(&request, "x-api-key"), Some("sk-test"));
    assert_eq!(header(&request, "anthropic-version"), Some("2023-06-01"));
    assert_eq!(header(&request, "Authorization"), None);
}

#[test]
fn a_keyless_endpoint_sends_no_authorization_header() {
    // The local llama-server and a keyless custom endpoint both take no key;
    // sending `Bearer ` with an empty key is a 401 on some servers.
    let mut p = params(LlmProvider::LocalLlama);
    p.api_key = String::new();
    let request = build_llm_request(&p).unwrap();
    assert_eq!(header(&request, "Authorization"), None);
}

// ---------------------------------------------------------------------------
// Bodies
// ---------------------------------------------------------------------------

#[test]
fn openai_body_shape() {
    let request = build_llm_request(&params(LlmProvider::OpenAi)).unwrap();
    let body = body_json(&request);
    assert_eq!(body["model"], "test-model");
    assert_eq!(body["messages"][0]["role"], "system");
    assert_eq!(body["messages"][0]["content"], "You clean up transcripts.");
    assert_eq!(body["messages"][1]["role"], "user");
    assert_eq!(
        body["messages"][1]["content"],
        "App: Slack\n\n--TRANSCRIPT--\nhello there\n--ENDTRANSCRIPT--"
    );
    assert!(body.get("max_completion_tokens").is_none());
    assert!(body.get("stream").is_none());
}

#[test]
fn anthropic_body_keeps_the_cacheable_system_block() {
    let request = build_llm_request(&params(LlmProvider::Anthropic)).unwrap();
    let body = body_json(&request);
    assert_eq!(body["model"], "test-model");
    assert_eq!(body["max_tokens"], 8192);
    assert_eq!(body["system"][0]["type"], "text");
    assert_eq!(body["system"][0]["text"], "You clean up transcripts.");
    assert_eq!(body["system"][0]["cache_control"]["type"], "ephemeral");
    assert_eq!(body["messages"][0]["role"], "user");
    assert_eq!(
        body["messages"][0]["content"],
        "App: Slack\n\n--TRANSCRIPT--\nhello there\n--ENDTRANSCRIPT--"
    );
    // The dynamic half must never leak into the cached prefix.
    assert!(!body["system"][0]["text"]
        .as_str()
        .unwrap()
        .contains("Slack"));
}

#[test]
fn groq_sends_the_completion_cap() {
    let request = build_llm_request(&params(LlmProvider::Groq)).unwrap();
    assert_eq!(body_json(&request)["max_completion_tokens"], 4096);
}

#[test]
fn groq_cap_is_the_only_difference_from_a_plain_openai_body() {
    let groq = body_json(&build_llm_request(&params(LlmProvider::Groq)).unwrap());
    let openai = body_json(&build_llm_request(&params(LlmProvider::OpenAi)).unwrap());
    let mut groq_without_cap = groq.clone();
    groq_without_cap
        .as_object_mut()
        .unwrap()
        .remove("max_completion_tokens");
    assert_eq!(groq_without_cap, openai);
}

#[test]
fn local_llama_body_carries_sampling_and_max_tokens() {
    let request = build_llm_request(&params(LlmProvider::LocalLlama)).unwrap();
    let body = body_json(&request);
    assert_eq!(body["temperature"], 1.0);
    assert_eq!(body["top_p"], 0.95);
    assert_eq!(body["min_p"], 0.0);
    assert_eq!(body["max_tokens"], 8192);
    // `top_k` is an integer parameter — `40.0` makes llama-server reject it.
    assert!(body["top_k"].is_i64(), "top_k must be an integer");
    assert_eq!(body["top_k"], 40);
}

#[test]
fn only_the_local_llama_arm_gets_sampling_parameters() {
    for provider in [
        LlmProvider::OpenAi,
        LlmProvider::Groq,
        LlmProvider::Gemini,
        LlmProvider::Mistral,
    ] {
        let body = body_json(&build_llm_request(&params(provider)).unwrap());
        assert!(body.get("temperature").is_none(), "provider={provider:?}");
        assert!(body.get("max_tokens").is_none(), "provider={provider:?}");
    }
}

#[test]
fn stream_flag_adds_stream_true() {
    let mut p = params(LlmProvider::LocalLlama);
    p.stream = true;
    assert_eq!(body_json(&build_llm_request(&p).unwrap())["stream"], true);
}

#[test]
fn hw_cloud_body_sends_the_transcript_exactly_once() {
    // macOS sent it once, Windows and Linux sent it twice — different input
    // tokens, different credit cost, different prompt for one recording.
    let mut p = params(LlmProvider::HyperWhisperCloud);
    p.license_key = Some("LIC-1".to_string());
    let body = body_json(&build_llm_request(&p).unwrap());
    assert_eq!(body["text"], "hello there");
    let prompt = body["prompt"].as_str().unwrap();
    assert_eq!(prompt, "You clean up transcripts.\n\nApp: Slack");
    assert!(!prompt.contains("hello there"), "transcript sent twice");
    assert!(!prompt.contains("--TRANSCRIPT--"));
}

#[test]
fn hw_cloud_prefers_the_license_key_and_falls_back_to_the_device_id() {
    let mut p = params(LlmProvider::HyperWhisperCloud);
    p.license_key = Some("LIC-1".to_string());
    p.device_id = Some("dev-1".to_string());
    let body = body_json(&build_llm_request(&p).unwrap());
    assert_eq!(body["license_key"], "LIC-1");
    assert!(body.get("device_id").is_none());

    p.license_key = None;
    let body = body_json(&build_llm_request(&p).unwrap());
    assert_eq!(body["device_id"], "dev-1");
    assert!(body.get("license_key").is_none());
}

#[test]
fn hw_cloud_without_an_identity_is_an_error_not_an_anonymous_call() {
    let p = params(LlmProvider::HyperWhisperCloud);
    assert_eq!(
        build_llm_request(&p).unwrap_err(),
        LlmError::MissingIdentity
    );
}

#[test]
fn hw_cloud_routes_on_catalog_headers() {
    let mut p = params(LlmProvider::HyperWhisperCloud);
    p.device_id = Some("dev-1".to_string());
    p.llm_provider_header = Some("cerebras".to_string());
    p.llm_model_header = Some("gpt-oss-120b".to_string());
    let request = build_llm_request(&p).unwrap();
    assert_eq!(header(&request, "X-LLM-Provider"), Some("cerebras"));
    assert_eq!(header(&request, "X-LLM-Model"), Some("gpt-oss-120b"));
}

#[test]
fn missing_transcript_or_prompt_or_model_is_rejected() {
    let mut p = params(LlmProvider::OpenAi);
    p.transcript = "   ".to_string();
    assert!(matches!(
        build_llm_request(&p).unwrap_err(),
        LlmError::MissingField { .. }
    ));

    let mut p = params(LlmProvider::OpenAi);
    p.system_prompt = String::new();
    assert!(matches!(
        build_llm_request(&p).unwrap_err(),
        LlmError::MissingField { .. }
    ));

    let mut p = params(LlmProvider::OpenAi);
    p.model = String::new();
    assert!(matches!(
        build_llm_request(&p).unwrap_err(),
        LlmError::MissingField { .. }
    ));
}

// ---------------------------------------------------------------------------
// Groq host sniff through a custom endpoint
// ---------------------------------------------------------------------------

fn custom(url: &str) -> LlmParams {
    let mut p = params(LlmProvider::Custom);
    p.custom_endpoint = Some(url.to_string());
    p
}

#[test]
fn a_custom_endpoint_pointed_at_groq_still_gets_the_cap() {
    let request =
        build_llm_request(&custom("https://api.groq.com/openai/v1/chat/completions")).unwrap();
    assert_eq!(body_json(&request)["max_completion_tokens"], 4096);
}

#[test]
fn the_groq_sniff_is_host_exact_and_case_insensitive() {
    for url in [
        "https://API.GROQ.COM/openai/v1/chat/completions",
        "https://api.groq.com:443/openai/v1/chat/completions",
    ] {
        let body = body_json(&build_llm_request(&custom(url)).unwrap());
        assert_eq!(body["max_completion_tokens"], 4096, "url={url}");
    }
    // A look-alike host is not Groq. A path or query mentioning it is not either.
    for url in [
        "https://api.groq.com.evil.test/v1/chat/completions",
        "https://proxy.test/api.groq.com/v1/chat/completions",
        "https://proxy.test/v1/chat/completions?host=api.groq.com",
    ] {
        let body = body_json(&build_llm_request(&custom(url)).unwrap());
        assert!(
            body.get("max_completion_tokens").is_none(),
            "url={url} must not be treated as Groq"
        );
    }
}

#[test]
fn a_custom_endpoint_that_fails_validation_never_builds_a_request() {
    let err = build_llm_request(&custom("llm.example.com/v1/chat/completions")).unwrap_err();
    assert!(matches!(err, LlmError::InvalidEndpoint { .. }));
}

// ---------------------------------------------------------------------------
// Wire protocol
// ---------------------------------------------------------------------------

#[test]
fn wire_protocol_per_provider() {
    assert_eq!(
        wire_protocol_for(LlmProvider::Anthropic),
        LlmWireProtocol::AnthropicMessages
    );
    assert_eq!(
        wire_protocol_for(LlmProvider::HyperWhisperCloud),
        LlmWireProtocol::HyperWhisperCloud
    );
    for provider in [
        LlmProvider::OpenAi,
        LlmProvider::Gemini,
        LlmProvider::Groq,
        LlmProvider::Grok,
        LlmProvider::Cerebras,
        LlmProvider::Mistral,
        LlmProvider::LocalLlama,
        LlmProvider::Custom,
    ] {
        assert_eq!(
            wire_protocol_for(provider),
            LlmWireProtocol::OpenAiChat,
            "provider={provider:?}"
        );
    }
}

// ---------------------------------------------------------------------------
// HyperWhisper Cloud response parsing
// ---------------------------------------------------------------------------

fn response(body: &str) -> HttpResponse {
    HttpResponse {
        status: 200,
        headers: vec![],
        body: body.as_bytes().to_vec(),
    }
}

#[test]
fn parses_corrected_text_and_trims_it() {
    let parsed = parse_hw_cloud_post_process(&response(r#"{"corrected":"  Hello there. "}"#));
    assert_eq!(parsed.unwrap(), "Hello there.");
}

#[test]
fn rejects_a_missing_blank_or_unparseable_corrected_field() {
    for body in [r#"{"other":"x"}"#, r#"{"corrected":"   "}"#, "not json"] {
        assert!(
            matches!(
                parse_hw_cloud_post_process(&response(body)),
                Err(LlmError::Parse { .. })
            ),
            "body={body}"
        );
    }
}

// ---------------------------------------------------------------------------
// normalize_custom_endpoint
// ---------------------------------------------------------------------------

fn strict(url: &str, model: &str) -> EndpointVerdict {
    normalize_custom_endpoint(url, model, EndpointValidationMode::Strict)
}

#[test]
fn a_well_formed_endpoint_is_valid_and_trimmed() {
    let verdict = strict(
        "  https://llm.example.com/v1/chat/completions  ",
        "  llama3  ",
    );
    assert_eq!(verdict.status, EndpointStatus::Valid);
    assert_eq!(verdict.url, "https://llm.example.com/v1/chat/completions");
    assert_eq!(verdict.model, "llama3");
    assert!(verdict.issue.is_none());
    assert!(verdict.is_usable());
}

#[test]
fn http_is_accepted_for_a_loopback_server() {
    // LM Studio / Ollama are plain http on localhost; banning http would break
    // the most common custom endpoint there is.
    let verdict = strict("http://localhost:11434/v1/chat/completions", "llama3.2");
    assert_eq!(verdict.status, EndpointStatus::Valid);
}

#[test]
fn a_schemeless_url_is_rejected_strictly_but_repaired_leniently() {
    // The macOS bug: `URL(string:)` accepted this, then every recording failed
    // with `unsupportedURL` and silently returned the raw transcript.
    let verdict = strict("llm.example.com/v1/chat/completions", "llama3");
    assert_eq!(verdict.status, EndpointStatus::Invalid);
    assert_eq!(verdict.issue, Some(EndpointIssue::NotAbsolute));
    assert_eq!(
        verdict.suggestion.as_deref(),
        Some("https://llm.example.com/v1/chat/completions")
    );
    assert!(!verdict.is_usable(), "must not be callable when strict");

    let verdict = validate_existing("llm.example.com/v1/chat/completions", "llama3");
    assert_eq!(verdict.status, EndpointStatus::NeedsRepair);
    assert_eq!(verdict.url, "https://llm.example.com/v1/chat/completions");
    assert!(verdict.is_usable(), "a saved endpoint must not vanish");
}

#[test]
fn userinfo_is_rejected_strictly_and_prompts_a_repair_leniently() {
    // The exact case from the issue: Windows accepted it, Linux silently
    // skipped post-processing for good.
    let url = "https://tok@llm.example.com/v1/chat/completions";
    let verdict = strict(url, "llama3");
    assert_eq!(verdict.status, EndpointStatus::Invalid);
    assert_eq!(verdict.issue, Some(EndpointIssue::UserInfoNotAllowed));
    assert_eq!(
        verdict.suggestion.as_deref(),
        Some("https://llm.example.com/v1/chat/completions")
    );

    let verdict = validate_existing(url, "llama3");
    assert_eq!(verdict.status, EndpointStatus::NeedsRepair);
    assert_eq!(verdict.url, url, "keeps working while the user is prompted");
    assert_eq!(
        verdict.suggestion.as_deref(),
        Some("https://llm.example.com/v1/chat/completions")
    );
}

#[test]
fn a_fragment_is_rejected_strictly_and_stripped_leniently() {
    let url = "https://llm.example.com/v1/chat/completions#notes";
    assert_eq!(strict(url, "llama3").status, EndpointStatus::Invalid);
    assert_eq!(
        strict(url, "llama3").suggestion.as_deref(),
        Some("https://llm.example.com/v1/chat/completions")
    );

    let verdict = validate_existing(url, "llama3");
    assert_eq!(verdict.status, EndpointStatus::NeedsRepair);
    assert_eq!(verdict.url, "https://llm.example.com/v1/chat/completions");
}

#[test]
fn a_non_http_scheme_is_fatal_in_both_modes() {
    for url in ["ftp://llm.example.com/v1", "file:///etc/passwd"] {
        for verdict in [strict(url, "llama3"), validate_existing(url, "llama3")] {
            assert_eq!(verdict.status, EndpointStatus::Invalid, "url={url}");
            assert_eq!(verdict.issue, Some(EndpointIssue::UnsupportedScheme));
            assert!(!verdict.is_usable(), "url={url}");
        }
    }
}

#[test]
fn an_empty_url_or_model_is_fatal_in_both_modes() {
    for verdict in [strict("   ", "llama3"), validate_existing("   ", "llama3")] {
        assert_eq!(verdict.status, EndpointStatus::Invalid);
        assert_eq!(verdict.issue, Some(EndpointIssue::EmptyUrl));
    }
    let url = "https://llm.example.com/v1/chat/completions";
    for verdict in [strict(url, "  "), validate_existing(url, "  ")] {
        assert_eq!(verdict.status, EndpointStatus::Invalid);
        assert_eq!(verdict.issue, Some(EndpointIssue::EmptyModel));
        assert!(!verdict.is_usable());
    }
}

#[test]
fn the_length_caps_are_enforced() {
    let long_url = format!("https://llm.example.com/{}", "a".repeat(2100));
    let verdict = strict(&long_url, "llama3");
    assert_eq!(verdict.issue, Some(EndpointIssue::UrlTooLong));
    assert_eq!(verdict.status, EndpointStatus::Invalid);

    let long_model = "m".repeat(300);
    let verdict = strict("https://llm.example.com/v1", &long_model);
    assert_eq!(verdict.issue, Some(EndpointIssue::ModelTooLong));

    let verdict = validate_existing("https://llm.example.com/v1", &long_model);
    assert_eq!(verdict.status, EndpointStatus::NeedsRepair);
    assert_eq!(verdict.model.chars().count(), 256);
}

#[test]
fn a_schemeless_url_with_a_port_is_still_repaired() {
    // The Ollama / LM Studio shape. The colon is a port, so `https://` is the
    // right repair — unlike `mailto:`, where the colon is a scheme.
    let verdict = validate_existing("localhost:11434/v1/chat/completions", "llama3.2");
    assert_eq!(verdict.status, EndpointStatus::NeedsRepair);
    assert_eq!(verdict.url, "https://localhost:11434/v1/chat/completions");
    assert!(
        verdict.is_usable(),
        "a saved Ollama endpoint must not vanish"
    );

    let verdict = strict("localhost:11434/v1/chat/completions", "llama3.2");
    assert_eq!(verdict.status, EndpointStatus::Invalid);
    assert_eq!(
        verdict.suggestion.as_deref(),
        Some("https://localhost:11434/v1/chat/completions")
    );
}

#[test]
fn a_scheme_we_cannot_call_is_never_repaired() {
    for raw in [
        "mailto:someone@example.com",
        "file:/etc/passwd",
        "custom:not-a-url",
    ] {
        let verdict = validate_existing(raw, "llama3");
        assert!(
            !verdict.is_usable(),
            "{raw} must not be repaired into an HTTP call"
        );
        assert_eq!(verdict.suggestion, None, "{raw} needs no suggestion");
    }
}

#[test]
fn a_url_with_no_host_is_not_an_endpoint() {
    let verdict = strict("https:///v1/chat/completions", "llama3");
    assert_eq!(verdict.issue, Some(EndpointIssue::NotAbsolute));
}

// ---------------------------------------------------------------------------
// The custom-endpoint test request
// ---------------------------------------------------------------------------

#[test]
fn the_test_request_matches_a_real_call_minus_the_prompt() {
    let request = build_custom_endpoint_test_request(
        " https://llm.example.com/v1/chat/completions ",
        " llama3 ",
        Some("sk-live"),
    )
    .unwrap();
    assert_eq!(request.url, "https://llm.example.com/v1/chat/completions");
    assert_eq!(header(&request, "Authorization"), Some("Bearer sk-live"));
    let body = body_json(&request);
    assert_eq!(body["model"], "llama3");
    assert_eq!(body["messages"][0]["role"], "system");
    assert_eq!(body["messages"][1]["content"], "Say hello in one word.");
}

#[test]
fn the_test_request_omits_auth_when_there_is_no_key() {
    for key in [None, Some(""), Some("   ")] {
        let request = build_custom_endpoint_test_request(
            "http://localhost:11434/v1/chat/completions",
            "llama3.2",
            key,
        )
        .unwrap();
        assert_eq!(header(&request, "Authorization"), None, "key={key:?}");
    }
}

#[test]
fn the_test_request_refuses_an_invalid_endpoint() {
    let err = build_custom_endpoint_test_request("llm.example.com/v1", "llama3", None).unwrap_err();
    assert!(matches!(err, LlmError::InvalidEndpoint { .. }));
}

// ---------------------------------------------------------------------------
// parse_custom_provider_string
// ---------------------------------------------------------------------------

#[test]
fn parses_a_custom_provider_string_in_either_platform_casing() {
    // macOS writes `UUID.uuidString` (uppercase), .NET writes `Guid.ToString()`
    // (lowercase); a backup carries both for the same endpoint.
    let upper = "custom:3F2504E0-4F89-11D3-9A0C-0305E82C3301";
    let lower = "custom:3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    assert_eq!(
        parse_custom_provider_string(upper),
        parse_custom_provider_string(lower)
    );
    assert_eq!(
        parse_custom_provider_string(upper).as_deref(),
        Some("3f2504e0-4f89-11d3-9a0c-0305e82c3301")
    );
}

#[test]
fn rejects_a_non_custom_or_malformed_provider_string() {
    for value in [
        "openai",
        "custom:",
        "custom:not-a-uuid",
        "custom:3f2504e04f8911d39a0c0305e82c3301",
        "custom:3f2504e0-4f89-11d3-9a0c-0305e82c330",
        "custom:3f2504e0-4f89-11d3-9a0c-0305e82c330g",
        "  custom:3f2504e0-4f89-11d3-9a0c-0305e82c3301",
    ] {
        assert_eq!(parse_custom_provider_string(value), None, "value={value}");
    }
}

#[test]
fn is_custom_provider_string_matches_the_prefix() {
    assert!(is_custom_provider_string("custom:anything"));
    assert!(!is_custom_provider_string("openai"));
}

// ---------------------------------------------------------------------------
// next_copy_name
// ---------------------------------------------------------------------------

#[test]
fn copy_names_increment_the_way_both_platforms_do() {
    assert_eq!(next_copy_name("My Ollama"), "My Ollama (copy)");
    assert_eq!(next_copy_name("My Ollama (copy)"), "My Ollama (copy 2)");
    assert_eq!(next_copy_name("My Ollama (copy 2)"), "My Ollama (copy 3)");
    assert_eq!(
        next_copy_name("My Ollama (copy 99)"),
        "My Ollama (copy 100)"
    );
}

#[test]
fn copy_name_matching_is_case_sensitive_and_end_anchored() {
    // Matches the shipped regex `\s\(copy(?:\s(\d+))?\)$` on both platforms.
    assert_eq!(next_copy_name("Name (COPY)"), "Name (COPY) (copy)");
    assert_eq!(next_copy_name("Name (Copy)"), "Name (Copy) (copy)");
    assert_eq!(
        next_copy_name("Name (copy) Extra"),
        "Name (copy) Extra (copy)"
    );
    // No whitespace before the parenthesis, so `\s` does not match.
    assert_eq!(next_copy_name("Name(copy)"), "Name(copy) (copy)");
    // Not a number.
    assert_eq!(next_copy_name("Name (copy x)"), "Name (copy x) (copy)");
    assert_eq!(next_copy_name(""), " (copy)");
}
