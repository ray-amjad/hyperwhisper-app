//! Legacy `cloudAccuracyTier` / `cloudPostProcessingModel` alias migration.
//!
//! Parity source:
//! - `CloudPostProcessingModel.fromStorageValue` and
//!   `CloudAccuracyTier.fromStorageValue` in macOS
//!   `app/macos/hyperwhisper/Views/Modes/Models/ModeModels.swift`.
//! - The legacy-value notes in the schema `cloudPostProcessingModel` description.
//!
//! On the real platforms these tables are catalog-driven
//! (`shared-app-classification/cloud-{stt,pp}-catalog.json`). This leaf crate
//! must not read those files (sans-I/O, no new deps), so the well-known
//! `migrateFrom` aliases are inlined here as a frozen snapshot. The mapping is
//! the SAME logic on both platforms; macOS is the verified reference. If the
//! catalogs gain new aliases, extend these tables to match.

/// Default cloud accuracy tier when the stored value is empty/unknown.
/// macOS `CloudAccuracyTier.fromStorageValue` defaults to `deepgramNova3`.
pub const DEFAULT_CLOUD_ACCURACY_TIER: &str = "deepgramNova3";

/// Migrate a persisted `cloudAccuracyTier` storage string to its canonical
/// catalog id. Mirrors `CloudAccuracyTier.fromStorageValue`:
/// 1. exact canonical-id match (case-insensitive),
/// 2. catalog `migrateFrom` alias match,
/// 3. fall back to `deepgramNova3`.
///
/// Returns the canonical id; `None` input or empty maps to the default.
pub fn migrate_cloud_accuracy_tier(value: Option<&str>) -> String {
    let trimmed = value.unwrap_or("").trim();
    if trimmed.is_empty() {
        return DEFAULT_CLOUD_ACCURACY_TIER.to_string();
    }
    let lower = trimmed.to_ascii_lowercase();

    // Canonical ids (case-insensitive exact match).
    for id in CANONICAL_TIERS {
        if id.eq_ignore_ascii_case(trimmed) {
            return (*id).to_string();
        }
    }
    // Legacy `migrateFrom` aliases, snapshot of cloud-stt-catalog.json.
    for (alias, canonical) in TIER_ALIASES {
        if alias.eq_ignore_ascii_case(&lower) {
            return (*canonical).to_string();
        }
    }
    DEFAULT_CLOUD_ACCURACY_TIER.to_string()
}

/// Canonical accuracy-tier ids (cloud-stt-catalog.json `id`s).
const CANONICAL_TIERS: &[&str] = &[
    "groqWhisper",
    "deepgramNova3",
    "grokStt",
    "azureMaiTranscribe",
    "googleChirp3",
    "elevenLabsScribeV2",
    "openaiWhisper",
    "assemblyAI",
    "mistralVoxtral",
    "soniox",
    "gemini",
];

/// Legacy `(alias, canonical_id)` pairs from each tier's `migrateFrom` list.
/// Aliases are matched case-insensitively (lowercased here for clarity).
const TIER_ALIASES: &[(&str, &str)] = &[
    // groqWhisper
    ("medium", "groqWhisper"),
    ("groq", "groqWhisper"),
    ("fireworks", "groqWhisper"),
    // deepgramNova3
    ("high", "deepgramNova3"),
    ("deepgram", "deepgramNova3"),
    // grokStt
    ("grok", "grokStt"),
    // azureMaiTranscribe
    ("microsoftazurespeech", "azureMaiTranscribe"),
    ("azure", "azureMaiTranscribe"),
    ("azure-mai", "azureMaiTranscribe"),
    ("azuremai", "azureMaiTranscribe"),
    // googleChirp3
    ("googlespeech", "googleChirp3"),
    ("chirp", "googleChirp3"),
    ("google-chirp", "googleChirp3"),
    ("googlechirp", "googleChirp3"),
    // elevenLabsScribeV2
    ("highest", "elevenLabsScribeV2"),
    ("elevenlabs", "elevenLabsScribeV2"),
];

/// Default cloud post-processing key when the stored value is empty/unknown.
/// macOS `CloudPostProcessingModel.fallback` is `grokFast` → `grok:grok-4.3`.
pub const DEFAULT_CLOUD_PP_MODEL: &str = "grok:grok-4.3";

/// Migrate a persisted `cloudPostProcessingModel` storage string to its
/// canonical provider-qualified `"<engineId>:<modelId>"` form. Mirrors
/// `CloudPostProcessingModel.fromStorageValue`:
/// 1. an already-qualified `engine:model` value with a known engine is kept,
/// 2. otherwise the legacy single-token table (case-insensitive),
/// 3. otherwise the `grok:grok-4.3` fallback.
///
/// NOTE: unlike the macOS impl this does not validate the model id against the
/// catalog (no catalog access here) — a qualified value with a known engine is
/// passed through verbatim. Documented limitation; the legacy single-token
/// aliases (the actual migration surface) are fully covered.
pub fn migrate_cloud_pp_model(value: Option<&str>) -> String {
    let trimmed = value.unwrap_or("").trim();
    if trimmed.is_empty() {
        return DEFAULT_CLOUD_PP_MODEL.to_string();
    }

    // Already provider-qualified "<engineId>:<modelId>" with a known engine →
    // pass through (canonicalize engine casing).
    if let Some(colon) = trimmed.find(':') {
        let raw_engine = &trimmed[..colon];
        let model = &trimmed[colon + 1..];
        if let Some(canon_engine) = KNOWN_PP_ENGINES
            .iter()
            .find(|e| e.eq_ignore_ascii_case(raw_engine))
        {
            return format!("{}:{}", canon_engine, model);
        }
        // Unknown engine → fall through to the legacy single-token table.
    }

    // Legacy single-token values (case-insensitive). Mirrors the macOS switch.
    match trimmed.to_ascii_lowercase().as_str() {
        "cerebras" | "cerebras-gpt-oss-120b" | "cerebrasgptoss120b" | "gpt-oss-120b"
        | "default" => "cerebras:gpt-oss-120b".to_string(),
        "groq" | "groq-gpt-oss-120b" | "groqgptoss120b" | "openai/gpt-oss-120b" => {
            "groq:openai/gpt-oss-120b".to_string()
        }
        "anthropic" | "claude-haiku-4-5" | "claude-haiku-4.5" | "claudehaiku" => {
            "anthropic:claude-haiku-4-5".to_string()
        }
        "grok" | "grok-4.3" | "grokfast" | "grok-4-1-fast-non-reasoning"
        | "grok-4.1-fast-non-reasoning" | "grok-4-fast-non-reasoning"
        | "grok-4-1-fast-reasoning" | "grok-4-fast-reasoning" => "grok:grok-4.3".to_string(),
        _ => DEFAULT_CLOUD_PP_MODEL.to_string(),
    }
}

/// Known post-processing engine ids (cloud-pp-catalog.json provider ids).
const KNOWN_PP_ENGINES: &[&str] = &[
    "cerebras",
    "groq",
    "anthropic",
    "grok",
    "openai",
    "gemini",
    "mistral",
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tier_empty_and_none_default() {
        assert_eq!(migrate_cloud_accuracy_tier(None), "deepgramNova3");
        assert_eq!(migrate_cloud_accuracy_tier(Some("  ")), "deepgramNova3");
    }

    #[test]
    fn tier_canonical_passthrough_case_insensitive() {
        assert_eq!(migrate_cloud_accuracy_tier(Some("grokStt")), "grokStt");
        assert_eq!(migrate_cloud_accuracy_tier(Some("GROKSTT")), "grokStt");
        assert_eq!(
            migrate_cloud_accuracy_tier(Some("azureMaiTranscribe")),
            "azureMaiTranscribe"
        );
    }

    #[test]
    fn tier_legacy_aliases() {
        assert_eq!(migrate_cloud_accuracy_tier(Some("high")), "deepgramNova3");
        assert_eq!(migrate_cloud_accuracy_tier(Some("highest")), "elevenLabsScribeV2");
        assert_eq!(migrate_cloud_accuracy_tier(Some("medium")), "groqWhisper");
        assert_eq!(migrate_cloud_accuracy_tier(Some("grok")), "grokStt");
        assert_eq!(migrate_cloud_accuracy_tier(Some("azure")), "azureMaiTranscribe");
    }

    #[test]
    fn tier_unknown_falls_back() {
        assert_eq!(migrate_cloud_accuracy_tier(Some("zzz")), "deepgramNova3");
    }

    #[test]
    fn pp_empty_and_none_default() {
        assert_eq!(migrate_cloud_pp_model(None), "grok:grok-4.3");
        assert_eq!(migrate_cloud_pp_model(Some("")), "grok:grok-4.3");
    }

    #[test]
    fn pp_legacy_single_token() {
        assert_eq!(migrate_cloud_pp_model(Some("claudeHaiku")), "anthropic:claude-haiku-4-5");
        assert_eq!(
            migrate_cloud_pp_model(Some("cerebrasGptOss120B")),
            "cerebras:gpt-oss-120b"
        );
        assert_eq!(
            migrate_cloud_pp_model(Some("groqGptOss120B")),
            "groq:openai/gpt-oss-120b"
        );
        assert_eq!(migrate_cloud_pp_model(Some("grokFast")), "grok:grok-4.3");
    }

    #[test]
    fn pp_qualified_passthrough() {
        assert_eq!(
            migrate_cloud_pp_model(Some("openai:gpt-5-mini")),
            "openai:gpt-5-mini"
        );
        // engine casing canonicalized, model preserved verbatim
        assert_eq!(
            migrate_cloud_pp_model(Some("ANTHROPIC:claude-haiku-4-5")),
            "anthropic:claude-haiku-4-5"
        );
    }

    #[test]
    fn pp_unknown_falls_back() {
        assert_eq!(migrate_cloud_pp_model(Some("nonsense")), "grok:grok-4.3");
    }
}
