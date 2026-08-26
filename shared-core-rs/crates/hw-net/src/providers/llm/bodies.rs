//! The three post-processing request bodies.
//!
//! Each of these was written out three times before #282 — the OpenAI chat body,
//! the Anthropic Messages body (down to `cache_control: ephemeral` and
//! `max_tokens: 8192`) and the hosted `/post-process` body. `serde_json` gives a
//! stable key order per `Map` insertion, so the produced bytes are pinned by the
//! tests in this module's parent.

use crate::contract::Body;
use serde_json::{json, Map, Value};

/// `anthropic-version` header value. All three copies pinned this same date.
pub const ANTHROPIC_VERSION: &str = "2023-06-01";

/// Gemma sampling parameters for the embedded llama-server, from Google's
/// documentation (<https://ai.google.dev/gemma/docs/core>). PARITY: macOS
/// `AIPostProcessor.localLLMSamplingParameters`.
pub const LOCAL_LLAMA_SAMPLING: &[(&str, f64)] = &[
    ("temperature", 1.0),
    ("top_p", 0.95),
    ("top_k", 40.0),
    ("min_p", 0.0),
];

/// Per-request switches on the OpenAI chat body.
#[derive(Debug, Clone, Copy, Default)]
pub struct OpenAiChatOptions {
    /// `max_completion_tokens` — Groq only (including a custom endpoint that
    /// resolves to `api.groq.com`).
    pub max_completion_tokens: Option<u32>,
    /// `max_tokens` — the local llama-server only.
    pub max_tokens: Option<u32>,
    /// Merge [`LOCAL_LLAMA_SAMPLING`] into the body.
    pub sampling: bool,
    /// Ask for an SSE stream.
    pub stream: bool,
}

fn json_body(value: Value) -> Body {
    Body::Bytes {
        content_type: "application/json".to_string(),
        data: serde_json::to_vec(&value).expect("serde_json cannot fail on an owned Value"),
    }
}

/// The OpenAI chat-completions body every provider but Anthropic and the hosted
/// route uses.
pub fn openai_chat(
    model: &str,
    system_prompt: &str,
    user_message: &str,
    options: OpenAiChatOptions,
) -> Body {
    let mut map = Map::new();
    map.insert("model".to_string(), json!(model));
    map.insert(
        "messages".to_string(),
        json!([
            { "role": "system", "content": system_prompt },
            { "role": "user", "content": user_message },
        ]),
    );
    if options.stream {
        map.insert("stream".to_string(), json!(true));
    }
    if options.sampling {
        for (key, value) in LOCAL_LLAMA_SAMPLING {
            // `top_k` is an integer parameter; emitting it as `40.0` makes
            // llama-server reject the request, so whole values go out as
            // integers and fractional ones as floats.
            let number = if value.fract() == 0.0 && *key != "temperature" {
                json!(*value as i64)
            } else {
                json!(value)
            };
            map.insert((*key).to_string(), number);
        }
    }
    if let Some(max_tokens) = options.max_tokens {
        map.insert("max_tokens".to_string(), json!(max_tokens));
    }
    if let Some(cap) = options.max_completion_tokens {
        map.insert("max_completion_tokens".to_string(), json!(cap));
    }
    json_body(Value::Object(map))
}

/// The Anthropic native Messages body.
///
/// The static system prompt is a cacheable `cache_control: ephemeral` text
/// block; the dynamic context travels in the user message so the cached prefix
/// stays byte-identical between requests.
pub fn anthropic_messages(model: &str, system_prompt: &str, user_message: &str) -> Body {
    json_body(json!({
        "model": model,
        "max_tokens": super::MAX_OUTPUT_TOKENS,
        "system": [
            {
                "type": "text",
                "text": system_prompt,
                "cache_control": { "type": "ephemeral" },
            }
        ],
        "messages": [ { "role": "user", "content": user_message } ],
    }))
}

/// The hosted `/post-process` body.
///
/// The backend builds its own provider call from `prompt`, so the system prompt
/// and the wrapped user message are concatenated into that one field.
///
/// PARITY NOTE — this fixes a real divergence. macOS sent the transcript
/// **once** (`prompt` = system prompt + system info, `text` = transcript, no
/// `--TRANSCRIPT--` block), while Windows and Linux sent it **twice** (`text`
/// plus a wrapped copy inside `prompt`) — different input-token count, different
/// credit cost and a different prompt for the same recording. This builder sends
/// it once, the cheaper macOS shape, and `text` remains the only copy.
pub fn hw_cloud_post_process(
    transcript: &str,
    system_prompt: &str,
    user_message: &str,
    identity: (&str, &str),
) -> Body {
    // The wrapped user message carries the transcript, so strip it back out and
    // keep only the dynamic context ahead of the markers. That is exactly what
    // macOS sent: `systemPrompt + "\n\n" + systemInfo`.
    let system_info = user_message
        .split("\n\n--TRANSCRIPT--\n")
        .next()
        .unwrap_or("");
    let mut map = Map::new();
    map.insert("text".to_string(), json!(transcript));
    map.insert(
        "prompt".to_string(),
        json!(format!("{system_prompt}\n\n{system_info}")),
    );
    map.insert(identity.0.to_string(), json!(identity.1));
    json_body(Value::Object(map))
}
