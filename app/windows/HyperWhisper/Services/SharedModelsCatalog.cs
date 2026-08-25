// SHARED MODELS CATALOG
// Windows facade over shared-models/models-catalog.json — the cross-platform
// source of truth for per-model metadata (custom-vocabulary support,
// HyperWhisper Cloud routability, cloud language sets). See shared-models/CLAUDE.md.
//
// The native decoder that used to live here (CatalogFile / RawEntry /
// LoadCatalog / GetEntries, plus the embedded-resource load and its
// Debug.Assert + Sentry failure reporting) is gone: every lookup already
// delegated to the Rust core, so nothing outside this file ever called it
// (issue #280). The catalog JSON is include_str!'d into the core at compile
// time, so there is no resource to load and no load failure to report.

using HyperWhisper.Models;
// Rust shared-core binding. `HwKind`/`HwLanguageSupport` live here. No collision
// with the native `CatalogKind`/`LanguageSupport` (different names).
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Voice vs text disambiguates IDs that exist as both a transcription model
/// and a post-processing LLM (the Gemini family is the canonical example).
/// Lookups must pass the kind to avoid inheriting the wrong row's flags.
/// </summary>
public enum CatalogKind
{
    Voice,
    Text
}

/// <summary>
/// Resolved language-filter capability for a single (cloud) voice model.
/// Mirrors macOS <c>SharedModelsCatalog.LanguageSupport</c>.
/// </summary>
public sealed class LanguageSupport
{
    /// <summary>Base ISO codes (region stripped). Empty when <see cref="SupportsAll"/> is true.</summary>
    public IReadOnlySet<string> Codes { get; }
    public bool SupportsAll { get; }

    public LanguageSupport(IReadOnlySet<string> codes, bool supportsAll)
    {
        Codes = codes;
        SupportsAll = supportsAll;
    }

    /// <summary>
    /// Whether the model should pass the library filter for <paramref name="baseCode"/>
    /// (already region-stripped, e.g. "es"). A prefix check tolerates any stray
    /// region-qualified entry that slipped past normalization.
    /// </summary>
    public bool Supports(string baseCode)
        => SupportsAll
           || Codes.Contains(baseCode)
           || Codes.Any(c => c.StartsWith(baseCode + "-", StringComparison.Ordinal));
}

/// <summary>
/// Per-model metadata from the shared catalog, resolved by the Rust core.
///
/// Lookup precedence (owned by <c>hw-catalog</c>, identical on every platform):
///   1. Exact <c>(provider, kind, id)</c>
///   2. Wildcard <c>(provider, kind, "*")</c>
///   3. Miss ⇒ <c>false</c> for both flags, and "every language" for the
///      language filter, so an uncatalogued model is never wrongly hidden.
/// </summary>
public static class SharedModelsCatalog
{
    /// <summary>Map the native <see cref="CatalogKind"/> to the shared-core <c>HwKind</c>.</summary>
    private static HwKind ToHwKind(CatalogKind kind) => kind switch
    {
        CatalogKind.Voice => HwKind.Voice,
        CatalogKind.Text => HwKind.Text,
        _ => HwKind.Voice
    };

    public static bool SupportsCustomVocabulary(string provider, CatalogKind kind, string id)
        => HyperwhisperCoreMethods.ModelsSupportsCustomVocabulary(provider, ToHwKind(kind), id ?? "");

    public static bool AvailableViaHyperWhisperCloud(string provider, CatalogKind kind, string id)
        => HyperwhisperCoreMethods.ModelsAvailableViaHwCloud(provider, ToHwKind(kind), id ?? "");

    /// <summary>
    /// Language-filter capability for a CLOUD voice model. Local providers carry
    /// no language data in the catalog (their rows are wildcards), so callers
    /// resolve those in-code. A cloud row with neither <c>supportedLanguages</c>
    /// nor <c>supportsAllLanguages</c> returns <see cref="LanguageSupport.SupportsAll"/>
    /// = true so an uncatalogued model is never wrongly hidden.
    /// </summary>
    public static LanguageSupport GetLanguageSupport(string provider, CatalogKind kind, string id)
    {
        HwLanguageSupport support = HyperwhisperCoreMethods.ModelsLanguageSupport(provider, ToHwKind(kind), id ?? "");
        return new LanguageSupport(new HashSet<string>(support.@codes), support.@supportsAll);
    }

    // -------------------------------------------------------------------------
    // Provider-key bridging — Windows enums are PascalCase, catalog is camelCase.
    //
    // Both switches are exhaustive (no `_ => ""`) so adding a new enum case
    // becomes a compile error here rather than silently mismatching the catalog.
    // -------------------------------------------------------------------------

    public static string CatalogKey(CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.None => "",
        CloudTranscriptionProvider.OpenAI => "openai",
        CloudTranscriptionProvider.Groq => "groq",
        CloudTranscriptionProvider.Deepgram => "deepgram",
        CloudTranscriptionProvider.AssemblyAI => "assemblyAI",
        CloudTranscriptionProvider.ElevenLabs => "elevenLabs",
        CloudTranscriptionProvider.Mistral => "mistral",
        CloudTranscriptionProvider.Soniox => "soniox",
        CloudTranscriptionProvider.Gemini => "gemini",
        CloudTranscriptionProvider.Grok => "grok",
        CloudTranscriptionProvider.HyperWhisperCloud => "hyperwhisper",
        CloudTranscriptionProvider.MicrosoftAzureSpeech => "microsoftAzureSpeech",
        CloudTranscriptionProvider.GoogleSpeech => "googleSpeech",
    };

    public static string CatalogKey(PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.None => "",
        PostProcessingProvider.OpenAI => "openai",
        PostProcessingProvider.Anthropic => "anthropic",
        PostProcessingProvider.Groq => "groq",
        PostProcessingProvider.Grok => "grok",
        PostProcessingProvider.Gemini => "gemini",
        PostProcessingProvider.Cerebras => "cerebras",
        PostProcessingProvider.Mistral => "mistral",
        PostProcessingProvider.LocalLlm => "localLLM",
        PostProcessingProvider.HyperWhisperCloud => "hyperwhisper",
    };

    // -------------------------------------------------------------------------
    // Local provider sentinels — Windows row builders use these directly so the
    // catalog stays the single source of truth even for providers where every
    // model shares the same flags.
    // -------------------------------------------------------------------------

    public const string LocalWhisperKey = "localWhisper";
    public const string ParakeetKey = "parakeet";
    public const string LocalLlmKey = "localLLM";
}
