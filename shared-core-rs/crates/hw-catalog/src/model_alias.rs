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

/// OpenAI STT ids deprecated 2026-08-26, shutdown 2027-02-26
/// (developers.openai.com/api/docs/deprecations). OpenAI names `gpt-transcribe`
/// and `gpt-4o-mini-transcribe-2025-12-15` as the replacements, and both are
/// already picker rows on macOS and Windows.
///
/// `openai` was a KNOWN provider with NO table, so all three ids passed through
/// unchanged and nothing migrated a saved mode off them. This table is the
/// alias half of the retirement; the three picker rows stay for now and come out
/// a release later, once a shipped build has had the chance to rewrite settings.
///
/// The targets keep each id in its own price tier, so a migrated mode does not
/// silently get more expensive: the two $0.006/min ids go to `gpt-transcribe`
/// ($0.0045/min, flat per-audio-minute like `whisper-1` was), and the
/// $0.003/min mini id goes to its own dated snapshot at the same rate.
const OPENAI_ALIASES: &[(&str, &str)] = &[
    ("whisper-1", "gpt-transcribe"),
    ("gpt-4o-transcribe", "gpt-transcribe"),
    ("gpt-4o-mini-transcribe", "gpt-4o-mini-transcribe-2025-12-15"),
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

/// Resolve a deprecated OpenAI STT model id to its current equivalent.
pub fn resolve_openai_model_alias(model_id: &str) -> String {
    resolve_in(OPENAI_ALIASES, model_id)
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
    "meta",
];

/// Resolve provider-specific model aliases before display, import or request
/// configuration. Mirrors `CloudTranscriptionModels.ResolveModelAlias` exactly:
///
/// - an empty `model_id` is returned unchanged (no table is consulted);
/// - a provider with its own table uses only that table;
/// - a KNOWN provider without a table passes the id through unchanged;
/// - `None`, an empty provider, or an UNKNOWN provider identifier chains every
///   table in the C# nesting order — ElevenLabs, AssemblyAI, Deepgram, Soniox,
///   Gemini, then OpenAI — because `CloudTranscriptionProviderExtensions
///   .FromIdentifier` returns the concrete `None` enum value for an unrecognized
///   string and the C# `null or CloudTranscriptionProvider.None` arm chains
///   everything.
///
/// OpenAI is the one table added after the C# dictionaries were folded into this
/// file, so it has no C# nesting position to mirror. It goes outermost. That is
/// safe because its three keys (`whisper-1`, `gpt-4o-transcribe`,
/// `gpt-4o-mini-transcribe`) appear in no other table and are no other table's
/// target, so the pass cannot collide with an earlier rewrite in either
/// direction. It is in the chain at all because a restored backup is exactly the
/// case this module exists for, and a backup can carry a model id with no
/// provider beside it.
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
        Some("openai") => resolve_openai_model_alias(model_id),
        // Known provider with no alias table → pass through.
        Some(p) if KNOWN_PROVIDER_IDS.contains(&p) => model_id.to_string(),
        // `null` / `None` / unrecognized → chain everything, innermost first.
        _ => resolve_openai_model_alias(&resolve_gemini_model_alias(
            &resolve_soniox_model_alias(&resolve_deepgram_model_alias(
                &resolve_assemblyai_model_alias(&resolve_elevenlabs_model_alias(model_id)),
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
    fn openai_stt_deprecations_resolve() {
        // Deprecated 2026-08-26, shutdown 2027-02-26. Before this table `openai`
        // was a known provider with no table, so all three passed through and no
        // saved mode ever migrated off them.
        assert_eq!(resolve_model_alias("whisper-1", Some("openai")), "gpt-transcribe");
        assert_eq!(
            resolve_model_alias("gpt-4o-transcribe", Some("openai")),
            "gpt-transcribe"
        );
        assert_eq!(
            resolve_model_alias("gpt-4o-mini-transcribe", Some("openai")),
            "gpt-4o-mini-transcribe-2025-12-15"
        );
        // Case-insensitive like every other table.
        assert_eq!(resolve_model_alias("Whisper-1", Some("OpenAI")), "gpt-transcribe");
        // The replacements are already current and must not rewrite again — a
        // second hop would be a loop.
        assert_eq!(resolve_model_alias("gpt-transcribe", Some("openai")), "gpt-transcribe");
        assert_eq!(
            resolve_model_alias("gpt-4o-mini-transcribe-2025-12-15", Some("openai")),
            "gpt-4o-mini-transcribe-2025-12-15"
        );
        // A backup can carry the id with no provider beside it.
        assert_eq!(resolve_model_alias("whisper-1", None), "gpt-transcribe");
        // An unrelated openai id still passes through.
        assert_eq!(resolve_model_alias("nova-2", Some("openai")), "nova-2");
    }

    #[test]
    fn openai_keys_collide_with_no_other_table() {
        // The chain arm applies OPENAI_ALIASES outermost. That is only safe while
        // its keys are absent from every other table and are no other table's
        // target, so pin it rather than trusting a future edit to remember.
        let others: &[&[(&str, &str)]] = &[
            ASSEMBLYAI_ALIASES,
            ELEVENLABS_ALIASES,
            DEEPGRAM_ALIASES,
            SONIOX_ALIASES,
            GEMINI_ALIASES,
        ];
        for (key, target) in OPENAI_ALIASES {
            for table in others {
                for (other_key, other_target) in *table {
                    assert_ne!(key, other_key, "{key} is also a key in another table");
                    assert_ne!(key, other_target, "{key} is another table's target");
                    assert_ne!(target, other_key, "{target} is another table's key");
                }
            }
        }
    }

    #[test]
    fn known_provider_without_a_table_passes_through() {
        // Windows' `_ => modelId` arm.
        assert_eq!(resolve_model_alias("nova-2", Some("groq")), "nova-2");
        assert_eq!(resolve_model_alias("scribe_v1", Some("hyperwhisper")), "scribe_v1");
        assert_eq!(
            resolve_model_alias("nova-2", Some("meta")),
            "nova-2",
            "canonical Meta is recognized without exposing a picker row"
        );
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
