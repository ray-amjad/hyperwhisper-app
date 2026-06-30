// SHARED MODELS CATALOG
// Loader for shared-models/models-catalog.json — cross-platform source of
// truth for per-model metadata (custom-vocabulary support, HyperWhisper
// Cloud routability). See shared-models/CLAUDE.md.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.Models;
using Sentry;
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

public sealed record CatalogEntry(
    string Provider,
    string Id,
    string Kind,
    bool SupportsCustomVocabulary,
    bool AvailableViaHyperWhisperCloud,
    IReadOnlyList<string> Platforms,
    string? DisplayName,
    string? Notes,
    IReadOnlyList<string>? SupportedLanguages = null,
    bool? IsEnglishOnly = null,
    bool? SupportsAllLanguages = null);

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
/// Loads <c>shared-models/models-catalog.json</c> once on first access and
/// exposes per-model metadata that's defined cross-platform.
///
/// Lookup precedence (mirrors macOS <c>SharedModelsCatalog.swift</c>):
///   1. Exact <c>(provider, kind, id)</c>
///   2. Wildcard <c>(provider, kind, "*")</c>
///   3. Defaults <c>(false, false)</c>
///
/// Missing or malformed catalog ⇒ DEBUG asserts to surface the regression
/// immediately, RELEASE captures a single Sentry event so the failure isn't
/// silent. The catalog is small (≤ 50 entries) so the lookup is on the hot
/// path of <see cref="ModelLibraryManager.Rebuild"/>.
/// </summary>
public static class SharedModelsCatalog
{
    private const string ResourceName = "HyperWhisper.SharedModels.models-catalog.json";

    private enum LoadStatus { Loaded, Absent, Malformed }

    private static readonly Lazy<LoadResult> Catalog = new(LoadCatalog, isThreadSafe: true);
    private static readonly object ReportLock = new();
    private static bool _reportedLoadFailure;

    private sealed record LoadResult(
        LoadStatus Status,
        Dictionary<(string Provider, CatalogKind Kind, string Id), CatalogEntry> Entries,
        string? Detail);

    private static LoadResult LoadCatalog()
    {
        var map = new Dictionary<(string, CatalogKind, string), CatalogEntry>();

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                return new LoadResult(LoadStatus.Absent, map, $"embedded resource '{ResourceName}' not found");
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            var file = JsonSerializer.Deserialize<CatalogFile>(stream, options);
            if (file?.Models == null)
            {
                return new LoadResult(LoadStatus.Malformed, map, "parsed file has no models array");
            }

            foreach (var raw in file.Models)
            {
                if (raw == null) continue;
                if (string.IsNullOrEmpty(raw.Provider) || raw.Id == null) continue;

                var kindString = raw.Kind ?? "voice";
                var kind = ParseKind(kindString);

                var entry = new CatalogEntry(
                    Provider: raw.Provider,
                    Id: raw.Id,
                    Kind: kindString,
                    SupportsCustomVocabulary: raw.SupportsCustomVocabulary,
                    AvailableViaHyperWhisperCloud: raw.AvailableViaHyperWhisperCloud,
                    Platforms: (IReadOnlyList<string>?)raw.Platforms ?? Array.Empty<string>(),
                    DisplayName: raw.DisplayName,
                    Notes: raw.Notes,
                    SupportedLanguages: (IReadOnlyList<string>?)raw.SupportedLanguages,
                    IsEnglishOnly: raw.IsEnglishOnly,
                    SupportsAllLanguages: raw.SupportsAllLanguages);
                map[(raw.Provider, kind, raw.Id)] = entry;
            }

            return new LoadResult(LoadStatus.Loaded, map, null);
        }
        catch (Exception ex)
        {
            return new LoadResult(LoadStatus.Malformed, map, ex.Message);
        }
    }

    private static CatalogKind ParseKind(string raw) => raw switch
    {
        "text" => CatalogKind.Text,
        _ => CatalogKind.Voice
    };

    private static Dictionary<(string Provider, CatalogKind Kind, string Id), CatalogEntry>? GetEntries()
    {
        var result = Catalog.Value;
        if (result.Status == LoadStatus.Loaded)
        {
            return result.Entries;
        }

        ReportLoadFailureOnce(result.Status, result.Detail);
        return null;
    }

    private static void ReportLoadFailureOnce(LoadStatus status, string? detail)
    {
        lock (ReportLock)
        {
            if (_reportedLoadFailure) return;
            _reportedLoadFailure = true;
        }

        var label = status == LoadStatus.Absent ? "absent" : "malformed";
        var message = $"SharedModelsCatalog load failed ({label}): {detail ?? "unknown"}";

        LoggingService.Warn(message);

        // DEBUG: trip an assertion so a developer notices immediately during
        // a clean build. The embedded resource being dropped from the csproj
        // is the usual culprit.
        Debug.Assert(false, message + ". Check the EmbeddedResource for models-catalog.json in HyperWhisper.csproj.");

        // RELEASE: surface a single Sentry event so a regression in the
        // bundle layout isn't silent. Logger alone goes unnoticed in prod.
        try
        {
            SentrySdk.CaptureMessage(message, SentryLevel.Error);
        }
        catch
        {
            // Sentry might not be initialized yet — never let telemetry
            // crash the row builder.
        }
    }

    /// <summary>
    /// Exact lookup with wildcard fallback. Returns null when neither
    /// <c>(provider, kind, id)</c> nor <c>(provider, kind, "*")</c> is in the
    /// catalog.
    /// </summary>
    public static CatalogEntry? GetEntry(string provider, CatalogKind kind, string id)
    {
        if (string.IsNullOrEmpty(provider)) return null;
        id ??= "";

        var map = GetEntries();
        if (map == null) return null;
        if (map.TryGetValue((provider, kind, id), out var exact)) return exact;
        if (map.TryGetValue((provider, kind, "*"), out var wildcard)) return wildcard;
        return null;
    }

    /// <summary>Map the native <see cref="CatalogKind"/> to the shared-core <c>HwKind</c>.</summary>
    private static HwKind ToHwKind(CatalogKind kind) => kind switch
    {
        CatalogKind.Voice => HwKind.Voice,
        CatalogKind.Text => HwKind.Text,
        _ => HwKind.Voice
    };

    // TODO-verify (Windows/CI): Rust shared-core swap.
    public static bool SupportsCustomVocabulary(string provider, CatalogKind kind, string id)
        => HyperwhisperCoreMethods.ModelsSupportsCustomVocabulary(provider, ToHwKind(kind), id ?? "");

    // TODO-verify (Windows/CI): Rust shared-core swap.
    public static bool AvailableViaHyperWhisperCloud(string provider, CatalogKind kind, string id)
        => HyperwhisperCoreMethods.ModelsAvailableViaHwCloud(provider, ToHwKind(kind), id ?? "");

    /// <summary>
    /// Language-filter capability for a CLOUD voice model. Local providers carry
    /// no language data in the catalog (their rows are wildcards), so callers
    /// resolve those in-code. A cloud row with neither <c>SupportedLanguages</c>
    /// nor <c>SupportsAllLanguages</c> returns <see cref="LanguageSupport.SupportsAll"/>
    /// = true so an uncatalogued model is never wrongly hidden.
    /// </summary>
    // TODO-verify (Windows/CI): Rust shared-core swap. Core resolves the wildcard
    // fallback + "uncatalogued ⇒ supportsAll" rule; we adapt its HwLanguageSupport
    // to the app-facing LanguageSupport.
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

    // -------------------------------------------------------------------------
    // Internal JSON shape — kept private; callers consume CatalogEntry instead.
    // -------------------------------------------------------------------------

    private sealed class CatalogFile
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("lastUpdated")] public string? LastUpdated { get; set; }
        [JsonPropertyName("models")] public List<RawEntry>? Models { get; set; }
    }

    private sealed class RawEntry
    {
        [JsonPropertyName("provider")] public string Provider { get; set; } = "";
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("supportsCustomVocabulary")] public bool SupportsCustomVocabulary { get; set; }
        [JsonPropertyName("availableViaHyperWhisperCloud")] public bool AvailableViaHyperWhisperCloud { get; set; }
        [JsonPropertyName("platforms")] public List<string>? Platforms { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("supportedLanguages")] public List<string>? SupportedLanguages { get; set; }
        [JsonPropertyName("isEnglishOnly")] public bool? IsEnglishOnly { get; set; }
        [JsonPropertyName("supportsAllLanguages")] public bool? SupportsAllLanguages { get; set; }
    }
}
