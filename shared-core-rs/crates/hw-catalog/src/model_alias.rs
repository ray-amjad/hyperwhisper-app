//! Legacy cloud-STT **model id** aliases.
//!
//! Parity source: `CloudTranscriptionModels.ResolveModelAlias` and its five
//! per-provider dictionaries in
//! `app/windows/HyperWhisper/Models/CloudTranscriptionModel.cs` — which now
//! delegate here, so this file is the only copy.
//!
//! Unlike the tier/provider `migrateFrom` data, these tables have **no catalog
//! backing**: `cloud-stt-catalog.json` carries no per-model alias list, so the
//! Windows dictionaries were the sole source of truth. They are ported verbatim
//! (including the case-insensitive lookup and the exact fallthrough order) so a
//! backup written by any shipping build keeps resolving onto current model ids.
//!
//! Matching is case-insensitive on the WHOLE trimmed-free key: the C# tables use
//! `StringComparer.OrdinalIgnoreCase` and do no trimming, so neither do we.

/// Legacy AssemblyAI model ids retired on 2026-05-11. `"universal"` →
/// `"universal-2"` (same multilingual behavior); the retired Universal-3 ids and
/// `"slam-1"` resolve to Universal-3.5 Pro.
const ASSEMBLYAI_ALIASES: &[(&str, &str)] = &[
    ("universal", "universal-2"),
    ("slam-1", "universal-3-5-pro"),
    ("universal-3-pro", "universal-3-5-pro"),
    ("universal-3-pro-medical", "universal-3-5-pro-medical"),
];

/// Legacy ElevenLabs model ids retired by ElevenLabs. `scribe_v1` was retired
/// 2026-07-09; `scribe_v2` is the direct successor.
const ELEVENLABS_ALIASES: &[(&str, &str)] = &[("scribe_v1", "scribe_v2")];

/// Legacy Windows Deepgram ids used before the catalog mirrored the macOS
/// domain-specific ids, plus the 25 ids removed in the 2026-05 catalog cleanup.
/// Removed ids collapse to `nova-3-general` so existing modes, settings and
/// backups continue to resolve.
const DEEPGRAM_ALIASES: &[(&str, &str)] = &[
    // Pre-cleanup short aliases. `enhanced` and `base` previously resolved to
    // their `-general` siblings, but those were removed in the cleanup, so they
    // now collapse straight to Nova 3 General.
    ("nova-3", "nova-3-general"),
    ("nova-2", "nova-2-general"),
    ("enhanced", "nova-3-general"),
    ("base", "nova-3-general"),
    // 2026-05 cleanup — every removed id maps to Nova 3 General.
    ("nova-2-meeting", "nova-3-general"),
    ("nova-2-phonecall", "nova-3-general"),
    ("nova-2-voicemail", "nova-3-general"),
    ("nova-2-finance", "nova-3-general"),
    ("nova-2-conversationalai", "nova-3-general"),
    ("nova-2-automotive", "nova-3-general"),
    ("nova-2-video", "nova-3-general"),
    ("nova", "nova-3-general"),
    ("nova-phonecall", "nova-3-general"),
    ("enhanced-general", "nova-3-general"),
    ("enhanced-meeting", "nova-3-general"),
    ("enhanced-phonecall", "nova-3-general"),
    ("enhanced-finance", "nova-3-general"),
    ("base-general", "nova-3-general"),
    ("base-meeting", "nova-3-general"),
    ("base-phonecall", "nova-3-general"),
    ("base-voicemail", "nova-3-general"),
    ("base-finance", "nova-3-general"),
    ("base-conversationalai", "nova-3-general"),
    ("base-video", "nova-3-general"),
    ("whisper-tiny", "nova-3-general"),
    ("whisper-base", "nova-3-general"),
    ("whisper-small", "nova-3-general"),
    ("whisper-medium", "nova-3-general"),
    ("whisper-large", "nova-3-general"),
];

const SONIOX_ALIASES: &[(&str, &str)] = &[("stt-async-v4", "stt-async-v5")];

const GEMINI_ALIASES: &[(&str, &str)] = &[
    ("gemini-3.1-flash-lite-preview", "gemini-3.1-flash-lite"),
    ("gemini-2.0-flash", "gemini-3.6-flash"),
];

/// Resolve a legacy AssemblyAI model id to its current equivalent. Non-AssemblyAI
/// and already-current ids pass through unchanged.
pub fn resolve_assemblyai_model_alias(model_id: &str) -> String {
    resolve_in(ASSEMBLYAI_ALIASES, model_id)
}

/// Resolve a legacy ElevenLabs model id to its current equivalent.
pub fn resolve_elevenlabs_model_alias(model_id: &str) -> String {
    resolve_in(ELEVENLABS_ALIASES, model_id)
}

/// Resolve a legacy Deepgram model id to its current equivalent.
pub fn resolve_deepgram_model_alias(model_id: &str) -> String {
    resolve_in(DEEPGRAM_ALIASES, model_id)
}

/// Resolve a legacy Soniox model id to its current equivalent.
pub fn resolve_soniox_model_alias(model_id: &str) -> String {
    resolve_in(SONIOX_ALIASES, model_id)
}

/// Resolve a legacy Gemini model id to its current equivalent.
pub fn resolve_gemini_model_alias(model_id: &str) -> String {
    resolve_in(GEMINI_ALIASES, model_id)
}

fn resolve_in(table: &[(&str, &str)], model_id: &str) -> String {
    if model_id.is_empty() {
        return String::new();
    }
    table
        .iter()
        .find(|(alias, _)| alias.eq_ignore_ascii_case(model_id))
        .map(|(_, canonical)| (*canonical).to_string())
        .unwrap_or_else(|| model_id.to_string())
}

/// Cloud-STT provider identifiers that the Windows `CloudTranscriptionProvider`
/// enum knows. Anything outside this list parses to `CloudTranscriptionProvider
/// .None` on Windows, which shares the chain-everything arm with a C# `null`
/// provider — so an unknown identifier must behave like "provider unknown", NOT
/// like a known provider without an alias table.
const KNOWN_PROVIDER_IDS: &[&str] = &[
    "openai",
    "groq",
    "deepgram",
    "assemblyai",
    "elevenlabs",
    "mistral",
    "soniox",
    "hyperwhisper",
    "gemini",
    "grok",
    "microsoftazurespeech",
    "googlespeech",
];

/// Resolve provider-specific model aliases before display, import or request
/// configuration. Mirrors `CloudTranscriptionModels.ResolveModelAlias` exactly:
///
/// - an empty `model_id` is returned unchanged (no table is consulted);
/// - a provider with its own table uses only that table;
/// - a KNOWN provider without a table passes the id through unchanged;
/// - `None`, an empty provider, or an UNKNOWN provider identifier chains every
///   table in the C# nesting order — ElevenLabs, AssemblyAI, Deepgram, Soniox,
///   Gemini — because `CloudTranscriptionProviderExtensions.FromIdentifier`
///   returns the concrete `None` enum value for an unrecognized string and the
///   C# `null or CloudTranscriptionProvider.None` arm chains everything.
pub fn resolve_model_alias(model_id: &str, provider: Option<&str>) -> String {
    if model_id.is_empty() {
        return String::new();
    }

    let provider_key = provider.map(|p| p.to_ascii_lowercase());
    match provider_key.as_deref() {
        Some("assemblyai") => resolve_assemblyai_model_alias(model_id),
        Some("deepgram") => resolve_deepgram_model_alias(model_id),
        Some("elevenlabs") => resolve_elevenlabs_model_alias(model_id),
        Some("soniox") => resolve_soniox_model_alias(model_id),
        Some("gemini") => resolve_gemini_model_alias(model_id),
        // Known provider with no alias table → pass through.
        Some(p) if KNOWN_PROVIDER_IDS.contains(&p) => model_id.to_string(),
        // `null` / `None` / unrecognized → chain everything, innermost first.
        _ => resolve_gemini_model_alias(&resolve_soniox_model_alias(
            &resolve_deepgram_model_alias(&resolve_assemblyai_model_alias(
                &resolve_elevenlabs_model_alias(model_id),
            )),
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_model_id_passes_through() {
        assert_eq!(resolve_model_alias("", Some("deepgram")), "");
        assert_eq!(resolve_model_alias("", None), "");
    }

    #[test]
    fn provider_scoped_tables() {
        assert_eq!(
            resolve_model_alias("universal-3-pro", Some("assemblyai")),
            "universal-3-5-pro"
        );
        assert_eq!(resolve_model_alias("nova-2", Some("deepgram")), "nova-2-general");
        assert_eq!(resolve_model_alias("scribe_v1", Some("elevenlabs")), "scribe_v2");
        assert_eq!(
            resolve_model_alias("stt-async-v4", Some("soniox")),
            "stt-async-v5"
        );
        assert_eq!(
            resolve_model_alias("gemini-2.0-flash", Some("gemini")),
            "gemini-3.6-flash"
        );
    }

    #[test]
    fn case_insensitive_like_the_csharp_dictionaries() {
        assert_eq!(resolve_model_alias("NOVA-2", Some("deepgram")), "nova-2-general");
        assert_eq!(resolve_model_alias("Scribe_V1", Some("ElevenLabs")), "scribe_v2");
    }

    #[test]
    fn known_provider_without_a_table_passes_through() {
        // Windows' `_ => modelId` arm.
        assert_eq!(resolve_model_alias("nova-2", Some("openai")), "nova-2");
        assert_eq!(resolve_model_alias("scribe_v1", Some("hyperwhisper")), "scribe_v1");
        assert_eq!(
            resolve_model_alias("mai-transcribe-1.5", Some("microsoftAzureSpeech")),
            "mai-transcribe-1.5"
        );
    }

    #[test]
    fn unknown_and_absent_provider_chain_everything() {
        // `FromIdentifier` maps an unrecognized string to the `None` enum value,
        // which shares the chain-everything arm with a C# `null` provider.
        assert_eq!(resolve_model_alias("nova-2", None), "nova-2-general");
        assert_eq!(resolve_model_alias("nova-2", Some("notaprovider")), "nova-2-general");
        assert_eq!(resolve_model_alias("scribe_v1", None), "scribe_v2");
        assert_eq!(resolve_model_alias("stt-async-v4", None), "stt-async-v5");
        assert_eq!(resolve_model_alias("universal", None), "universal-2");
    }

    #[test]
    fn unknown_model_id_passes_through() {
        assert_eq!(resolve_model_alias("nova-9-imaginary", Some("deepgram")), "nova-9-imaginary");
        assert_eq!(resolve_model_alias("nova-9-imaginary", None), "nova-9-imaginary");
    }

    #[test]
    fn chain_order_matches_the_csharp_nesting() {
        // "universal" is an AssemblyAI alias only; the ElevenLabs pass runs first
        // and leaves it alone, then AssemblyAI rewrites it, and the remaining
        // passes leave "universal-2" alone.
        assert_eq!(resolve_model_alias("universal", None), "universal-2");
    }
}
