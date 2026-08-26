//! Sans-I/O LLM post-processing request building.
//!
//! Before this module the post-processing request was assembled three times —
//! macOS `AIPostProcessor.swift`, Windows `PostProcessingService.cs` and
//! `shared-dotnet` `CloudPostProcessingService.cs` — with a byte-identical
//! endpoint table, two byte-identical JSON bodies, the same auth headers, the
//! same `api.groq.com` host sniff and the same `systemInfo` + `--TRANSCRIPT--`
//! wrapper in each. Issue #282 collapses all of that into one builder.
//!
//! The shape matches the STT providers in this crate: Rust builds an
//! [`HttpRequest`] *value*, the platform performs the I/O with its own timeout,
//! retry policy and logging, then hands the [`HttpResponse`] back for parsing.
//!
//! ## What stays native, deliberately
//!
//! - **Timeouts.** [`HttpRequest`] carries none, and the shipped paths use three
//!   different ones (30 s BYO key, 60 s HW Cloud, 60 s streaming). The timeout
//!   is an argument to the platform executor, not part of the request value.
//! - **Retry.** macOS wraps post-processing in `performWithRetry`; the .NET
//!   heads do a single send. That is a real parity gap, but it is a transport
//!   policy, not request building.
//! - **SSE.** Streaming is macOS-local-LLM-only (`grep '[DONE]'` finds no hits
//!   under `app/windows`, `app/shared-dotnet` or `app/linux`). The stream
//!   *body* is built here — [`LlmParams::stream`] adds `"stream": true` — but
//!   the line reader stays in Swift.
//!
//! ## Prompt inputs are already shared
//!
//! `hw-text`'s `build_system_prompt` / `build_system_info` (exported as
//! `ffi_prompt`) already produce both string inputs on every platform, so
//! [`build_llm_request`] takes them as plain strings and only owns the
//! `--TRANSCRIPT--` wrapper that frames them.

mod bodies;
mod custom;
mod endpoints;

pub use bodies::{ANTHROPIC_VERSION, LOCAL_LLAMA_SAMPLING};
pub use custom::{
    build_custom_endpoint_test_request, is_custom_provider_string, next_copy_name,
    normalize_custom_endpoint, parse_custom_provider_string, validate_existing, EndpointIssue,
    EndpointStatus, EndpointValidationMode, EndpointVerdict, CUSTOM_PROVIDER_PREFIX,
    MAX_CUSTOM_ENDPOINT_MODEL_CHARS, MAX_CUSTOM_ENDPOINT_URL_CHARS,
};
pub use endpoints::{
    endpoint_for, DEFAULT_LOCAL_LLAMA_PORT, HW_CLOUD_POST_PROCESS_PATH, HW_CLOUD_PROD_BASE,
    HW_CLOUD_STAGING_BASE,
};

use crate::contract::{Body, Header, HttpMethod, HttpRequest, HttpResponse};

/// Every post-processing provider the apps offer.
///
/// `HyperWhisperCloud` is credit-billed and routed (`X-LLM-Provider` /
/// `X-LLM-Model` headers against the hosted `/post-process` route);
/// `LocalLlama` is the embedded llama-server on macOS; `Custom` is a
/// user-supplied OpenAI-compatible URL. Everything else is a direct BYO-key
/// vendor call.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LlmProvider {
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

/// The response shape a provider answers with, i.e. which parser the platform
/// must feed the body to. Mirrors `hw_text::completion::WireProtocol` for the
/// two BYO shapes and adds the hosted route's own `{ "corrected": ... }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LlmWireProtocol {
    OpenAiChat,
    AnthropicMessages,
    HyperWhisperCloud,
}

/// Which parser handles this provider's 200 response.
///
/// Anthropic is the only provider on its native Messages API; HyperWhisper
/// Cloud answers the hosted contract; everything else — including the local
/// llama-server and any custom endpoint — is OpenAI chat-completions shaped.
pub fn wire_protocol_for(provider: LlmProvider) -> LlmWireProtocol {
    match provider {
        LlmProvider::Anthropic => LlmWireProtocol::AnthropicMessages,
        LlmProvider::HyperWhisperCloud => LlmWireProtocol::HyperWhisperCloud,
        _ => LlmWireProtocol::OpenAiChat,
    }
}

/// Max output tokens requested from any LLM during post-processing.
/// PARITY: macOS `AIPostProcessor.maxOutputTokens`, Windows/`shared-dotnet`
/// `max_tokens = 8192` in the Anthropic body.
pub const MAX_OUTPUT_TOKENS: u32 = 8_192;

/// Output-token cap sent to Groq. Groq applies a low default completion cap
/// when the request omits one (its reasoning docs cite 1,024) and gpt-oss
/// reasoning tokens spend from that same budget, so long dictations truncate
/// with `finish_reason=length`. Kept at 4,096 — not [`MAX_OUTPUT_TOKENS`] —
/// because Groq's free-tier TPM ceiling for `openai/gpt-oss-120b` is 8,000 and
/// the admission check counts prompt + requested cap, not actual usage.
pub const GROQ_MAX_COMPLETION_TOKENS: u32 = 4_096;

/// The host whose OpenAI-compatible endpoint needs [`GROQ_MAX_COMPLETION_TOKENS`],
/// including when it is reached through a *custom* endpoint URL. All three
/// shipped copies sniffed this host by hand.
pub const GROQ_HOST: &str = "api.groq.com";

/// Everything [`build_llm_request`] needs. A superset POD, like
/// [`crate::contract::TranscribeParams`] — each provider arm reads only the
/// fields it needs and the rest stay `None`/empty.
#[derive(Debug, Clone, PartialEq)]
pub struct LlmParams {
    pub provider: LlmProvider,
    /// Model id. Ignored by [`LlmProvider::HyperWhisperCloud`], which routes on
    /// headers instead.
    pub model: String,
    /// BYO key. Empty means "send no auth header" — which is legitimate for
    /// [`LlmProvider::LocalLlama`] and for a keyless custom endpoint.
    pub api_key: String,
    /// The static, cacheable system prompt (from `hw-text`).
    pub system_prompt: String,
    /// The dynamic per-request context (from `hw-text`). Goes in the *user*
    /// message so the system prompt stays byte-identical across requests and
    /// stays cacheable.
    pub system_info: String,
    /// The raw transcript to clean up.
    pub transcript: String,
    /// The user-supplied URL for [`LlmProvider::Custom`]. Must already have
    /// passed [`normalize_custom_endpoint`].
    pub custom_endpoint: Option<String>,
    /// Base URL override. For [`LlmProvider::HyperWhisperCloud`] this selects
    /// prod vs staging ([`HW_CLOUD_PROD_BASE`] / [`HW_CLOUD_STAGING_BASE`]);
    /// `None` means prod.
    pub base_url: Option<String>,
    /// llama-server port for [`LlmProvider::LocalLlama`]; `None` uses
    /// [`DEFAULT_LOCAL_LLAMA_PORT`].
    pub local_llama_port: Option<u16>,
    /// HyperWhisper Cloud identity. Exactly one of these is sent, license key
    /// first, matching all three shipped copies.
    pub license_key: Option<String>,
    pub device_id: Option<String>,
    /// `X-LLM-Provider` for the hosted route (from the cloud-PP catalog).
    pub llm_provider_header: Option<String>,
    /// `X-LLM-Model` for the hosted route (from the cloud-PP catalog).
    pub llm_model_header: Option<String>,
    /// Ask for an SSE stream (`"stream": true`). Only the macOS local-LLM path
    /// sets this; the platform still owns the line reader.
    pub stream: bool,
}

impl Default for LlmParams {
    fn default() -> Self {
        Self {
            provider: LlmProvider::OpenAi,
            model: String::new(),
            api_key: String::new(),
            system_prompt: String::new(),
            system_info: String::new(),
            transcript: String::new(),
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
}

/// Why a post-processing request could not be built, or a response could not be
/// read. Transport failures are not modelled here — the platform owns those.
#[derive(thiserror::Error, Debug, Clone, PartialEq, Eq)]
pub enum LlmError {
    /// A required field was empty (transcript, system prompt, model, …).
    #[error("missing {field}")]
    MissingField { field: String },
    /// A custom endpoint URL that never passed [`normalize_custom_endpoint`].
    #[error("invalid custom endpoint: {message}")]
    InvalidEndpoint { message: String },
    /// HyperWhisper Cloud with neither a license key nor a device id.
    #[error("no HyperWhisper Cloud identity")]
    MissingIdentity,
    /// A 200 body that did not match the provider's contract.
    #[error("response parse error: {message}")]
    Parse { message: String },
}

fn missing(field: &str) -> LlmError {
    LlmError::MissingField {
        field: field.to_string(),
    }
}

/// Frame the transcript exactly the way all four shipped copies do.
///
/// The dynamic `system_info` leads, then the transcript between
/// `--TRANSCRIPT--` / `--ENDTRANSCRIPT--`. Keeping the dynamic half in the user
/// message is what lets the static system prompt stay cacheable — Anthropic's
/// `cache_control: ephemeral` block depends on it.
pub fn wrap_transcript(system_info: &str, transcript: &str) -> String {
    format!("{system_info}\n\n--TRANSCRIPT--\n{transcript}\n--ENDTRANSCRIPT--")
}

/// Build the post-processing request for any provider.
///
/// The platform executes it with its own timeout and retry policy, then parses
/// the 200 body with the protocol [`wire_protocol_for`] names.
pub fn build_llm_request(params: &LlmParams) -> Result<HttpRequest, LlmError> {
    if params.transcript.trim().is_empty() {
        return Err(missing("transcript"));
    }
    if params.system_prompt.trim().is_empty() {
        return Err(missing("system prompt"));
    }

    let url = endpoint_for(params)?;
    let user_message = wrap_transcript(&params.system_info, &params.transcript);

    let (headers, body) = match params.provider {
        LlmProvider::HyperWhisperCloud => hw_cloud_request(params, &user_message)?,
        LlmProvider::Anthropic => {
            let model = require_model(params)?;
            let headers = vec![
                Header::new("x-api-key", params.api_key.clone()),
                Header::new("anthropic-version", bodies::ANTHROPIC_VERSION),
            ];
            (
                headers,
                bodies::anthropic_messages(model, &params.system_prompt, &user_message),
            )
        }
        _ => {
            let model = require_model(params)?;
            let mut headers = Vec::new();
            if !params.api_key.is_empty() {
                headers.push(Header::new(
                    "Authorization",
                    format!("Bearer {}", params.api_key),
                ));
            }
            let cap = completion_token_cap(params, &url);
            (
                headers,
                bodies::openai_chat(
                    model,
                    &params.system_prompt,
                    &user_message,
                    bodies::OpenAiChatOptions {
                        max_completion_tokens: cap,
                        max_tokens: (params.provider == LlmProvider::LocalLlama)
                            .then_some(MAX_OUTPUT_TOKENS),
                        sampling: params.provider == LlmProvider::LocalLlama,
                        stream: params.stream,
                    },
                ),
            )
        }
    };

    Ok(HttpRequest {
        method: HttpMethod::Post,
        url,
        headers,
        body,
    })
}

fn require_model(params: &LlmParams) -> Result<&str, LlmError> {
    let model = params.model.trim();
    if model.is_empty() {
        return Err(missing("model"));
    }
    Ok(model)
}

/// `max_completion_tokens`, sent only when the request will actually reach
/// Groq's API — either the dedicated Groq provider or a custom endpoint whose
/// host *is* `api.groq.com`. This is the host sniff the three copies each wrote
/// by hand.
fn completion_token_cap(params: &LlmParams, url: &str) -> Option<u32> {
    let is_groq = params.provider == LlmProvider::Groq
        || (params.provider == LlmProvider::Custom
            && custom::host_of(url).as_deref() == Some(GROQ_HOST));
    is_groq.then_some(GROQ_MAX_COMPLETION_TOKENS)
}

fn hw_cloud_request(
    params: &LlmParams,
    user_message: &str,
) -> Result<(Vec<Header>, Body), LlmError> {
    let license = params.license_key.as_deref().unwrap_or("").trim();
    let device = params.device_id.as_deref().unwrap_or("").trim();
    if license.is_empty() && device.is_empty() {
        return Err(LlmError::MissingIdentity);
    }

    let mut headers = Vec::new();
    if let Some(provider_header) = non_empty(params.llm_provider_header.as_deref()) {
        headers.push(Header::new("X-LLM-Provider", provider_header));
    }
    if let Some(model_header) = non_empty(params.llm_model_header.as_deref()) {
        headers.push(Header::new("X-LLM-Model", model_header));
    }

    let identity = if license.is_empty() {
        ("device_id", device)
    } else {
        ("license_key", license)
    };
    Ok((
        headers,
        bodies::hw_cloud_post_process(
            &params.transcript,
            &params.system_prompt,
            user_message,
            identity,
        ),
    ))
}

fn non_empty(value: Option<&str>) -> Option<&str> {
    value.map(str::trim).filter(|v| !v.is_empty())
}

/// Parse the hosted `/post-process` 200 body (`{ "corrected": "..." }`).
///
/// The hosted contract already validates provider termination and strips the
/// wrapper markers, so the caller must NOT run the provider-native wrapper
/// contract over this a second time.
pub fn parse_hw_cloud_post_process(resp: &HttpResponse) -> Result<String, LlmError> {
    let value: serde_json::Value =
        serde_json::from_slice(&resp.body).map_err(|e| LlmError::Parse {
            message: e.to_string(),
        })?;
    let corrected = value
        .get("corrected")
        .and_then(|v| v.as_str())
        .unwrap_or("");
    if corrected.trim().is_empty() {
        return Err(LlmError::Parse {
            message: "HyperWhisper Cloud returned no corrected text".to_string(),
        });
    }
    Ok(corrected.trim().to_string())
}

#[cfg(test)]
mod tests;
