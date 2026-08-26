//! UniFFI surface for the LLM post-processing builder (`hw_net::providers::llm`).
//!
//! Mirrors the LLM types as UniFFI records/enums and exposes the seven entry
//! points the platforms call. The HTTP contract types (`HttpRequest`,
//! `HttpResponse`, `Header`, `Body`) are **not** redeclared here — they are the
//! same records [`crate::ffi_net`] already exports, so a post-processing request
//! goes through the very same `RustHttpTransport.cs` / `RustHTTPExecutor.swift`
//! that a transcription request does.
//!
//! Every function name is `llm_`-prefixed to keep the flat UniFFI namespace
//! readable next to the STT builders.

use hw_net::providers::llm as l;

use crate::ffi_net::{HttpRequest, HttpResponse};

// ===========================================================================
// Types
// ===========================================================================

/// Every post-processing provider. Mirrors `l::LlmProvider`.
#[derive(uniffi::Enum)]
pub enum HwLlmProvider {
    HyperWhisperCloud,
    OpenAi,
    Anthropic,
    Gemini,
    Groq,
    Grok,
    Cerebras,
    Mistral,
    LocalLlama,
    Custom,
}

impl From<HwLlmProvider> for l::LlmProvider {
    fn from(p: HwLlmProvider) -> Self {
        match p {
            HwLlmProvider::HyperWhisperCloud => l::LlmProvider::HyperWhisperCloud,
            HwLlmProvider::OpenAi => l::LlmProvider::OpenAi,
            HwLlmProvider::Anthropic => l::LlmProvider::Anthropic,
            HwLlmProvider::Gemini => l::LlmProvider::Gemini,
            HwLlmProvider::Groq => l::LlmProvider::Groq,
            HwLlmProvider::Grok => l::LlmProvider::Grok,
            HwLlmProvider::Cerebras => l::LlmProvider::Cerebras,
            HwLlmProvider::Mistral => l::LlmProvider::Mistral,
            HwLlmProvider::LocalLlama => l::LlmProvider::LocalLlama,
            HwLlmProvider::Custom => l::LlmProvider::Custom,
        }
    }
}

impl From<l::LlmProvider> for HwLlmProvider {
    fn from(p: l::LlmProvider) -> Self {
        match p {
            l::LlmProvider::HyperWhisperCloud => HwLlmProvider::HyperWhisperCloud,
            l::LlmProvider::OpenAi => HwLlmProvider::OpenAi,
            l::LlmProvider::Anthropic => HwLlmProvider::Anthropic,
            l::LlmProvider::Gemini => HwLlmProvider::Gemini,
            l::LlmProvider::Groq => HwLlmProvider::Groq,
            l::LlmProvider::Grok => HwLlmProvider::Grok,
            l::LlmProvider::Cerebras => HwLlmProvider::Cerebras,
            l::LlmProvider::Mistral => HwLlmProvider::Mistral,
            l::LlmProvider::LocalLlama => HwLlmProvider::LocalLlama,
            l::LlmProvider::Custom => HwLlmProvider::Custom,
        }
    }
}

/// Which parser reads a provider's 200 body. Mirrors `l::LlmWireProtocol`.
///
/// Deliberately distinct from [`crate::ffi_completion::WireProtocol`], which has
/// no HyperWhisper Cloud arm: the hosted route answers `{ "corrected": … }` and
/// must NOT go through the provider-native wrapper contract a second time.
#[derive(uniffi::Enum)]
pub enum HwLlmWireProtocol {
    OpenAiChat,
    AnthropicMessages,
    HyperWhisperCloud,
}

impl From<l::LlmWireProtocol> for HwLlmWireProtocol {
    fn from(p: l::LlmWireProtocol) -> Self {
        match p {
            l::LlmWireProtocol::OpenAiChat => HwLlmWireProtocol::OpenAiChat,
            l::LlmWireProtocol::AnthropicMessages => HwLlmWireProtocol::AnthropicMessages,
            l::LlmWireProtocol::HyperWhisperCloud => HwLlmWireProtocol::HyperWhisperCloud,
        }
    }
}

/// Inputs for [`llm_build_request`]. Mirrors `l::LlmParams`.
///
/// `system_prompt` and `system_info` both come from `hw-text` (see
/// [`crate::build_system_prompt`] / [`crate::build_system_info`]), so the caller
/// never assembles a prompt by hand.
#[derive(uniffi::Record)]
pub struct HwLlmParams {
    pub provider: HwLlmProvider,
    pub model: String,
    pub api_key: String,
    pub system_prompt: String,
    pub system_info: String,
    pub transcript: String,
    #[uniffi(default = None)]
    pub custom_endpoint: Option<String>,
    #[uniffi(default = None)]
    pub base_url: Option<String>,
    #[uniffi(default = None)]
    pub local_llama_port: Option<u16>,
    #[uniffi(default = None)]
    pub license_key: Option<String>,
    #[uniffi(default = None)]
    pub device_id: Option<String>,
    #[uniffi(default = None)]
    pub llm_provider_header: Option<String>,
    #[uniffi(default = None)]
    pub llm_model_header: Option<String>,
    #[uniffi(default = false)]
    pub stream: bool,
}

impl From<HwLlmParams> for l::LlmParams {
    fn from(p: HwLlmParams) -> Self {
        l::LlmParams {
            provider: p.provider.into(),
            model: p.model,
            api_key: p.api_key,
            system_prompt: p.system_prompt,
            system_info: p.system_info,
            transcript: p.transcript,
            custom_endpoint: p.custom_endpoint,
            base_url: p.base_url,
            local_llama_port: p.local_llama_port,
            license_key: p.license_key,
            device_id: p.device_id,
            llm_provider_header: p.llm_provider_header,
            llm_model_header: p.llm_model_header,
            stream: p.stream,
        }
    }
}

/// Why a post-processing request could not be built. Mirrors `l::LlmError`.
/// `Display` is hand-written to match the leaf crate's `thiserror` messages, so
/// `hw-core` needs no extra dependency.
#[derive(uniffi::Error, Debug)]
pub enum HwLlmError {
    MissingField { field: String },
    InvalidEndpoint { message: String },
    MissingIdentity,
    Parse { message: String },
}

impl std::fmt::Display for HwLlmError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            HwLlmError::MissingField { field } => write!(f, "missing {field}"),
            HwLlmError::InvalidEndpoint { message } => {
                write!(f, "invalid custom endpoint: {message}")
            }
            HwLlmError::MissingIdentity => write!(f, "no HyperWhisper Cloud identity"),
            HwLlmError::Parse { message } => write!(f, "response parse error: {message}"),
        }
    }
}

impl std::error::Error for HwLlmError {}

impl From<l::LlmError> for HwLlmError {
    fn from(e: l::LlmError) -> Self {
        match e {
            l::LlmError::MissingField { field } => HwLlmError::MissingField { field },
            l::LlmError::InvalidEndpoint { message } => HwLlmError::InvalidEndpoint { message },
            l::LlmError::MissingIdentity => HwLlmError::MissingIdentity,
            l::LlmError::Parse { message } => HwLlmError::Parse { message },
        }
    }
}

/// How strictly to judge a custom endpoint. Mirrors `l::EndpointValidationMode`.
#[derive(uniffi::Enum)]
pub enum HwEndpointValidationMode {
    /// A new or edited endpoint — every rule break is fatal.
    Strict,
    /// An endpoint already saved (or arriving in a backup) — a rule break
    /// becomes a repair prompt, not a deletion.
    Lenient,
}

impl From<HwEndpointValidationMode> for l::EndpointValidationMode {
    fn from(m: HwEndpointValidationMode) -> Self {
        match m {
            HwEndpointValidationMode::Strict => l::EndpointValidationMode::Strict,
            HwEndpointValidationMode::Lenient => l::EndpointValidationMode::Lenient,
        }
    }
}

/// The verdict's headline. Mirrors `l::EndpointStatus`.
#[derive(uniffi::Enum, PartialEq, Eq, Debug)]
pub enum HwEndpointStatus {
    Valid,
    NeedsRepair,
    Invalid,
}

impl From<l::EndpointStatus> for HwEndpointStatus {
    fn from(s: l::EndpointStatus) -> Self {
        match s {
            l::EndpointStatus::Valid => HwEndpointStatus::Valid,
            l::EndpointStatus::NeedsRepair => HwEndpointStatus::NeedsRepair,
            l::EndpointStatus::Invalid => HwEndpointStatus::Invalid,
        }
    }
}

/// The rule that failed. Mirrors `l::EndpointIssue`.
#[derive(uniffi::Enum, PartialEq, Eq, Debug)]
pub enum HwEndpointIssue {
    EmptyUrl,
    NotAbsolute,
    UnsupportedScheme,
    UserInfoNotAllowed,
    FragmentNotAllowed,
    UrlTooLong,
    EmptyModel,
    ModelTooLong,
}

impl From<l::EndpointIssue> for HwEndpointIssue {
    fn from(i: l::EndpointIssue) -> Self {
        match i {
            l::EndpointIssue::EmptyUrl => HwEndpointIssue::EmptyUrl,
            l::EndpointIssue::NotAbsolute => HwEndpointIssue::NotAbsolute,
            l::EndpointIssue::UnsupportedScheme => HwEndpointIssue::UnsupportedScheme,
            l::EndpointIssue::UserInfoNotAllowed => HwEndpointIssue::UserInfoNotAllowed,
            l::EndpointIssue::FragmentNotAllowed => HwEndpointIssue::FragmentNotAllowed,
            l::EndpointIssue::UrlTooLong => HwEndpointIssue::UrlTooLong,
            l::EndpointIssue::EmptyModel => HwEndpointIssue::EmptyModel,
            l::EndpointIssue::ModelTooLong => HwEndpointIssue::ModelTooLong,
        }
    }
}

/// The verdict on one custom endpoint. Mirrors `l::EndpointVerdict`.
///
/// `url` is the single check a runtime caller needs: **empty means do not call
/// it**, in either mode. `message` is the human-readable form of `issue`, so a
/// platform never has to duplicate the wording in a switch.
#[derive(uniffi::Record)]
pub struct HwEndpointVerdict {
    pub status: HwEndpointStatus,
    pub url: String,
    pub model: String,
    pub issue: Option<HwEndpointIssue>,
    pub message: Option<String>,
    pub suggestion: Option<String>,
}

impl From<l::EndpointVerdict> for HwEndpointVerdict {
    fn from(v: l::EndpointVerdict) -> Self {
        HwEndpointVerdict {
            status: v.status.into(),
            url: v.url,
            model: v.model,
            issue: v.issue.map(Into::into),
            message: v.issue.map(|i| i.to_string()),
            suggestion: v.suggestion,
        }
    }
}

// ===========================================================================
// Request building
// ===========================================================================

/// Build the post-processing request for any provider.
///
/// The platform executes it with its own timeout and retry policy, then parses
/// the 200 body with the protocol [`llm_wire_protocol_for`] names.
#[uniffi::export]
pub fn llm_build_request(params: HwLlmParams) -> Result<HttpRequest, HwLlmError> {
    l::build_llm_request(&params.into())
        .map(Into::into)
        .map_err(Into::into)
}

/// The URL a request would go to, without building the body. For logging and
/// for the health checker.
#[uniffi::export]
pub fn llm_endpoint_for(params: HwLlmParams) -> Result<String, HwLlmError> {
    l::endpoint_for(&params.into()).map_err(Into::into)
}

/// Which parser handles this provider's 200 response.
#[uniffi::export]
pub fn llm_wire_protocol_for(provider: HwLlmProvider) -> HwLlmWireProtocol {
    l::wire_protocol_for(provider.into()).into()
}

/// The `systemInfo` + `--TRANSCRIPT--` user message, for callers that need the
/// string itself (e.g. a native streaming body).
#[uniffi::export]
pub fn llm_wrap_transcript(system_info: String, transcript: String) -> String {
    l::wrap_transcript(&system_info, &transcript)
}

/// Parse the hosted `/post-process` 200 body (`{ "corrected": "..." }`).
///
/// The hosted contract already validates provider termination and strips the
/// wrapper markers, so the caller must NOT run the provider-native wrapper
/// contract over this a second time.
#[uniffi::export]
pub fn llm_parse_hw_cloud_post_process(resp: HttpResponse) -> Result<String, HwLlmError> {
    l::parse_hw_cloud_post_process(&resp.into()).map_err(Into::into)
}

// ===========================================================================
// Custom endpoints
// ===========================================================================

/// The one custom-endpoint validation rule, replacing the four the platforms
/// each had.
#[uniffi::export]
pub fn llm_normalize_custom_endpoint(
    raw: String,
    model: String,
    mode: HwEndpointValidationMode,
) -> HwEndpointVerdict {
    l::normalize_custom_endpoint(&raw, &model, mode.into()).into()
}

/// Validate an endpoint that is already saved, or arriving in a backup.
///
/// Lenient: an endpoint that fails the tightened rules comes back as
/// [`HwEndpointStatus::NeedsRepair`] with a `suggestion`, and keeps a callable
/// `url` wherever calling it is still safe — so tightening validation cannot
/// silently delete a user's endpoint or stop their post-processing.
#[uniffi::export]
pub fn llm_validate_existing_custom_endpoint(raw: String, model: String) -> HwEndpointVerdict {
    l::validate_existing(&raw, &model).into()
}

/// The "Hello world" probe the Add/Edit endpoint sheet sends.
#[uniffi::export]
pub fn llm_build_custom_endpoint_test_request(
    raw_url: String,
    model: String,
    api_key: Option<String>,
) -> Result<HttpRequest, HwLlmError> {
    l::build_custom_endpoint_test_request(&raw_url, &model, api_key.as_deref())
        .map(Into::into)
        .map_err(Into::into)
}

/// The UUID inside a Mode's `"custom:<uuid>"` provider string, canonical
/// lowercase. `None` when the string is not a custom-endpoint reference.
#[uniffi::export]
pub fn llm_parse_custom_provider_string(provider_string: String) -> Option<String> {
    l::parse_custom_provider_string(&provider_string)
}

/// Whether a Mode's stored provider string names a custom endpoint.
#[uniffi::export]
pub fn llm_is_custom_provider_string(provider_string: String) -> bool {
    l::is_custom_provider_string(&provider_string)
}

/// The next name when the user duplicates an endpoint:
/// `"Name"` → `"Name (copy)"` → `"Name (copy 2)"`.
#[uniffi::export]
pub fn llm_next_copy_name(original_name: String) -> String {
    l::next_copy_name(&original_name)
}

// ===========================================================================
// Constants
// ===========================================================================

/// HyperWhisper Cloud production base URL.
#[uniffi::export]
pub fn llm_hw_cloud_prod_base() -> String {
    l::HW_CLOUD_PROD_BASE.to_string()
}

/// HyperWhisper Cloud staging base URL, for DEBUG builds.
///
/// The Linux head hardcoded the production host with no DEBUG switch, so every
/// dev run billed production credits. Both constants live here now.
#[uniffi::export]
pub fn llm_hw_cloud_staging_base() -> String {
    l::HW_CLOUD_STAGING_BASE.to_string()
}

/// Max output tokens requested from any post-processing LLM.
#[uniffi::export]
pub fn llm_max_output_tokens() -> u32 {
    l::MAX_OUTPUT_TOKENS
}

/// Output-token cap sent to Groq (lower than [`llm_max_output_tokens`] — see the
/// leaf crate for why).
#[uniffi::export]
pub fn llm_groq_max_completion_tokens() -> u32 {
    l::GROQ_MAX_COMPLETION_TOKENS
}

/// Port the embedded llama-server listens on.
#[uniffi::export]
pub fn llm_default_local_llama_port() -> u16 {
    l::DEFAULT_LOCAL_LLAMA_PORT
}

/// Max custom endpoint URL length, for a UI character counter.
#[uniffi::export]
pub fn llm_max_custom_endpoint_url_chars() -> u32 {
    l::MAX_CUSTOM_ENDPOINT_URL_CHARS as u32
}

/// Max custom endpoint model-name length, for a UI character counter.
#[uniffi::export]
pub fn llm_max_custom_endpoint_model_chars() -> u32 {
    l::MAX_CUSTOM_ENDPOINT_MODEL_CHARS as u32
}

// ===========================================================================
// Tests
// ===========================================================================

/// Tests for the FFI bridge itself, not for `hw-net`'s logic.
///
/// Same bug class as `ffi_net`'s inline tests: every `From` arm here has the
/// same shape, so a swapped arm (`Grok` → `Groq`, `EmptyUrl` → `EmptyModel`)
/// still compiles and still round-trips. Each test therefore pins a conversion
/// to something OBSERVABLE — a URL, a header, a status — through the exported
/// functions the platforms actually call.
#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi_net::Body;

    fn params(provider: HwLlmProvider) -> HwLlmParams {
        HwLlmParams {
            provider,
            model: "test-model".to_string(),
            api_key: "sk-test".to_string(),
            system_prompt: "You clean up transcripts.".to_string(),
            system_info: "App: Slack".to_string(),
            transcript: "hello there".to_string(),
            custom_endpoint: None,
            base_url: None,
            local_llama_port: None,
            license_key: None,
            device_id: None,
            llm_provider_header: None,
            llm_model_header: None,
            stream: false,
        }
    }

    fn body_json(request: &HttpRequest) -> serde_json::Value {
        match &request.body {
            Body::Bytes { data, .. } => serde_json::from_slice(data).expect("body is JSON"),
            // `ffi_net`'s records derive no `Debug`, so report the shape by name.
            _ => panic!("expected a Bytes body"),
        }
    }

    /// `Result::unwrap_err` needs `T: Debug`, which `ffi_net::HttpRequest` does
    /// not have. Pull the error out by hand instead.
    fn build_err(params: HwLlmParams) -> HwLlmError {
        match llm_build_request(params) {
            Err(e) => e,
            Ok(_) => panic!("expected the request build to fail"),
        }
    }

    #[test]
    fn every_provider_arm_maps_to_its_own_endpoint() {
        // A swapped arm would show up as the wrong URL here, not as a compile
        // error, so assert all ten distinctly.
        let cases = [
            (
                HwLlmProvider::OpenAi,
                "https://api.openai.com/v1/chat/completions",
            ),
            (
                HwLlmProvider::Anthropic,
                "https://api.anthropic.com/v1/messages",
            ),
            (
                HwLlmProvider::Gemini,
                "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            ),
            (
                HwLlmProvider::Groq,
                "https://api.groq.com/openai/v1/chat/completions",
            ),
            (HwLlmProvider::Grok, "https://api.x.ai/v1/chat/completions"),
            (
                HwLlmProvider::Cerebras,
                "https://api.cerebras.ai/v1/chat/completions",
            ),
            (
                HwLlmProvider::Mistral,
                "https://api.mistral.ai/v1/chat/completions",
            ),
            (
                HwLlmProvider::LocalLlama,
                "http://127.0.0.1:37219/v1/chat/completions",
            ),
        ];
        for (provider, expected) in cases {
            assert_eq!(llm_endpoint_for(params(provider)).unwrap(), expected);
        }
    }

    #[test]
    fn params_fields_are_not_crossed() {
        // `system_prompt` and `system_info` are both strings and adjacent in the
        // record; crossing them compiles and only shows up on the wire.
        let request = llm_build_request(params(HwLlmProvider::OpenAi)).unwrap();
        let body = body_json(&request);
        assert_eq!(body["messages"][0]["content"], "You clean up transcripts.");
        assert_eq!(
            body["messages"][1]["content"],
            "App: Slack\n\n--TRANSCRIPT--\nhello there\n--ENDTRANSCRIPT--"
        );
    }

    #[test]
    fn the_identity_fields_are_not_crossed() {
        let mut p = params(HwLlmProvider::HyperWhisperCloud);
        p.license_key = Some("LIC-1".to_string());
        p.llm_provider_header = Some("cerebras".to_string());
        p.llm_model_header = Some("gpt-oss-120b".to_string());
        let request = llm_build_request(p).unwrap();
        assert_eq!(body_json(&request)["license_key"], "LIC-1");
        let header = |name: &str| {
            request
                .headers
                .iter()
                .find(|h| h.name == name)
                .map(|h| h.value.as_str())
        };
        assert_eq!(header("X-LLM-Provider"), Some("cerebras"));
        assert_eq!(header("X-LLM-Model"), Some("gpt-oss-120b"));
    }

    #[test]
    fn the_base_url_override_reaches_the_builder() {
        let mut p = params(HwLlmProvider::HyperWhisperCloud);
        p.device_id = Some("dev-1".to_string());
        p.base_url = Some(llm_hw_cloud_staging_base());
        assert_eq!(
            llm_endpoint_for(p).unwrap(),
            "https://transcribe-staging-v2.hyperwhisper.com/post-process"
        );
    }

    #[test]
    fn errors_cross_the_boundary_as_distinct_variants() {
        let err = build_err(params(HwLlmProvider::HyperWhisperCloud));
        assert!(matches!(err, HwLlmError::MissingIdentity));

        let mut p = params(HwLlmProvider::Custom);
        p.custom_endpoint = Some("llm.example.com/v1".to_string());
        assert!(matches!(build_err(p), HwLlmError::InvalidEndpoint { .. }));

        let mut p = params(HwLlmProvider::OpenAi);
        p.transcript = "  ".to_string();
        assert!(matches!(build_err(p), HwLlmError::MissingField { .. }));
    }

    #[test]
    fn wire_protocol_arms_are_not_swapped() {
        assert!(matches!(
            llm_wire_protocol_for(HwLlmProvider::Anthropic),
            HwLlmWireProtocol::AnthropicMessages
        ));
        assert!(matches!(
            llm_wire_protocol_for(HwLlmProvider::HyperWhisperCloud),
            HwLlmWireProtocol::HyperWhisperCloud
        ));
        assert!(matches!(
            llm_wire_protocol_for(HwLlmProvider::Custom),
            HwLlmWireProtocol::OpenAiChat
        ));
    }

    #[test]
    fn the_verdict_carries_status_issue_message_and_suggestion() {
        let verdict = llm_normalize_custom_endpoint(
            "https://tok@llm.example.com/v1".to_string(),
            "llama3".to_string(),
            HwEndpointValidationMode::Strict,
        );
        assert_eq!(verdict.status, HwEndpointStatus::Invalid);
        assert_eq!(verdict.issue, Some(HwEndpointIssue::UserInfoNotAllowed));
        assert_eq!(
            verdict.message.as_deref(),
            Some("the endpoint URL must not contain a username or password")
        );
        assert_eq!(
            verdict.suggestion.as_deref(),
            Some("https://llm.example.com/v1")
        );
        assert!(verdict.url.is_empty(), "strict must not hand back a URL");
    }

    #[test]
    fn the_lenient_mode_keeps_a_saved_endpoint_callable() {
        let verdict = llm_validate_existing_custom_endpoint(
            "llm.example.com/v1/chat/completions".to_string(),
            "llama3".to_string(),
        );
        assert_eq!(verdict.status, HwEndpointStatus::NeedsRepair);
        assert_eq!(verdict.url, "https://llm.example.com/v1/chat/completions");
    }

    #[test]
    fn the_custom_endpoint_helpers_round_trip() {
        assert_eq!(
            llm_parse_custom_provider_string(
                "custom:3F2504E0-4F89-11D3-9A0C-0305E82C3301".to_string()
            )
            .as_deref(),
            Some("3f2504e0-4f89-11d3-9a0c-0305e82c3301")
        );
        assert_eq!(llm_parse_custom_provider_string("openai".to_string()), None);
        assert!(llm_is_custom_provider_string("custom:x".to_string()));
        assert_eq!(llm_next_copy_name("Ollama".to_string()), "Ollama (copy)");
    }

    #[test]
    fn the_test_request_crosses_the_boundary() {
        let request = llm_build_custom_endpoint_test_request(
            "https://llm.example.com/v1/chat/completions".to_string(),
            "llama3".to_string(),
            Some("sk-live".to_string()),
        )
        .unwrap();
        assert_eq!(request.url, "https://llm.example.com/v1/chat/completions");
        assert_eq!(body_json(&request)["model"], "llama3");
    }

    #[test]
    fn parses_the_hosted_route_response() {
        let resp = HttpResponse {
            status: 200,
            headers: vec![],
            body: br#"{"corrected":"Hello there."}"#.to_vec(),
        };
        assert_eq!(
            llm_parse_hw_cloud_post_process(resp).unwrap(),
            "Hello there."
        );
    }

    #[test]
    fn the_exported_constants_match_the_leaf_crate() {
        assert_eq!(llm_max_output_tokens(), 8192);
        assert_eq!(llm_groq_max_completion_tokens(), 4096);
        assert_eq!(llm_default_local_llama_port(), 37219);
        assert_eq!(llm_max_custom_endpoint_url_chars(), 2048);
        assert_eq!(llm_max_custom_endpoint_model_chars(), 256);
        assert_eq!(
            llm_hw_cloud_prod_base(),
            "https://transcribe-prod-v2.hyperwhisper.com"
        );
    }
}
