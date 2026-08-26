//! The post-processing endpoint table — one copy, not three.
//!
//! Before #282 the same URL strings lived in
//! `PostProcessingProvider.swift:77-104` (9 arms),
//! `PostProcessingService.cs:632-644` (6 arms plus Anthropic inline) and
//! `CloudPostProcessingService.cs:16-26` (7 arms plus HW Cloud). They were
//! byte-identical; only the arm *count* drifted, so a provider added to one head
//! silently kept working on the other two with a stale or missing URL.

use super::{LlmError, LlmParams, LlmProvider};

/// HyperWhisper Cloud production base. PARITY: macOS `NetworkConfig` RELEASE
/// branch, Windows `NetworkConfig`, `CloudPostProcessingService.cs:14-15`.
pub const HW_CLOUD_PROD_BASE: &str = "https://transcribe-prod-v2.hyperwhisper.com";

/// HyperWhisper Cloud staging base, used by DEBUG builds.
///
/// `CloudPostProcessingService.cs:14-15` hardcoded the *production* host with no
/// DEBUG switch, so every Linux dev run billed production credits. Exposing both
/// constants here is what lets each head pick the right one from one place.
pub const HW_CLOUD_STAGING_BASE: &str = "https://transcribe-staging-v2.hyperwhisper.com";

/// Path appended to the HyperWhisper Cloud base for post-processing.
pub const HW_CLOUD_POST_PROCESS_PATH: &str = "/post-process";

/// Port the embedded llama-server listens on (macOS
/// `LlamaServerController.Configuration.default.port`).
pub const DEFAULT_LOCAL_LLAMA_PORT: u16 = 37219;

const OPENAI: &str = "https://api.openai.com/v1/chat/completions";
const ANTHROPIC: &str = "https://api.anthropic.com/v1/messages";
const GEMINI: &str = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
const GROQ: &str = "https://api.groq.com/openai/v1/chat/completions";
const GROK: &str = "https://api.x.ai/v1/chat/completions";
const CEREBRAS: &str = "https://api.cerebras.ai/v1/chat/completions";
const MISTRAL: &str = "https://api.mistral.ai/v1/chat/completions";

/// The URL a post-processing request goes to.
///
/// Fixed-vendor arms ignore every field but `provider`. The three variable arms
/// read `base_url` (HyperWhisper Cloud), `local_llama_port` (local llama) and
/// `custom_endpoint` (custom).
pub fn endpoint_for(params: &LlmParams) -> Result<String, LlmError> {
    Ok(match params.provider {
        LlmProvider::HyperWhisperCloud => {
            let base = params
                .base_url
                .as_deref()
                .map(str::trim)
                .filter(|b| !b.is_empty())
                .unwrap_or(HW_CLOUD_PROD_BASE)
                .trim_end_matches('/')
                .to_string();
            format!("{base}{HW_CLOUD_POST_PROCESS_PATH}")
        }
        LlmProvider::OpenAi => OPENAI.to_string(),
        LlmProvider::Anthropic => ANTHROPIC.to_string(),
        LlmProvider::Gemini => GEMINI.to_string(),
        LlmProvider::Groq => GROQ.to_string(),
        LlmProvider::Grok => GROK.to_string(),
        LlmProvider::Cerebras => CEREBRAS.to_string(),
        LlmProvider::Mistral => MISTRAL.to_string(),
        LlmProvider::LocalLlama => {
            let port = params.local_llama_port.unwrap_or(DEFAULT_LOCAL_LLAMA_PORT);
            format!("http://127.0.0.1:{port}/v1/chat/completions")
        }
        LlmProvider::Custom => {
            let raw = params
                .custom_endpoint
                .as_deref()
                .map(str::trim)
                .unwrap_or("");
            let verdict = super::normalize_custom_endpoint(
                raw,
                &params.model,
                super::EndpointValidationMode::Strict,
            );
            if verdict.url.is_empty() {
                return Err(LlmError::InvalidEndpoint {
                    message: verdict
                        .issue
                        .map(|i| i.to_string())
                        .unwrap_or_else(|| "unusable custom endpoint".to_string()),
                });
            }
            verdict.url
        }
    })
}
