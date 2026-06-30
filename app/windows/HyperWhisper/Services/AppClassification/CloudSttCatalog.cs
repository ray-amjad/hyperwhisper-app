using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
// Rust shared-core binding. `NormalizedCloudProvider` lives here; no native
// type of that name, so no qualification needed.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.AppClassification;

/// <summary>
/// Loads and exposes <c>shared-app-classification/cloud-stt-catalog.json</c> — the
/// cross-platform source of truth for cloud STT provider capabilities driving UI
/// affordances (credits/min caption, custom-vocab field visibility,
/// cloud-tier-vs-BYOK list filtering).
///
/// Mirrors the loader pattern in <see cref="AppTypeClassifier"/>.
/// </summary>
public sealed class CloudSttCatalog
{
    public static CloudSttCatalog Shared { get; } = LoadCatalog();

    public int Version { get; init; }
    public string Updated { get; init; } = "missing";
    public CloudSttCatalogEntry[] Providers { get; init; } = [];

    /// <summary>Lookup by id (matches <see cref="Models.CloudAccuracyTierExtensions.ToStorageValue"/>).</summary>
    public CloudSttCatalogEntry? GetById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var entry in Providers)
            if (string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    /// <summary>
    /// Look up an entry whose <c>MigrateFrom</c> list contains the given alias
    /// (case-insensitive). Drives legacy <c>cloudAccuracyTier</c> resolution —
    /// NOT <c>cloudProvider</c> rewriting (see <see cref="GetByLegacyCloudProviderAlias"/>).
    /// </summary>
    public CloudSttCatalogEntry? GetByMigrateFromAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        var needle = alias.Trim();
        foreach (var entry in Providers)
        {
            if (entry.MigrateFrom is null) continue;
            foreach (var candidate in entry.MigrateFrom)
            {
                if (string.Equals(candidate, needle, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Look up an entry whose <c>LegacyCloudProviderAliases</c> list contains
    /// the given alias (case-insensitive). Drives <see cref="NormalizeCloudProvider"/>
    /// only — kept deliberately separate from <c>MigrateFrom</c> so BYOK
    /// provider names never get misinterpreted as cloud-tier migrations.
    /// </summary>
    public CloudSttCatalogEntry? GetByLegacyCloudProviderAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        var needle = alias.Trim();
        foreach (var entry in Providers)
        {
            if (entry.LegacyCloudProviderAliases is null) continue;
            foreach (var candidate in entry.LegacyCloudProviderAliases)
            {
                if (string.Equals(candidate, needle, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        return null;
    }

    /// <summary>Display-only cost in credits per minute for the given tier; 0 if unknown.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public double CreditsPerMinute(string? id)
        => string.IsNullOrEmpty(id) ? 0 : HyperwhisperCoreMethods.CloudSttCreditsPerMinute(id);

    // =========================================================================
    // CLOUD-TIER PROVIDER + MODEL ACCESSORS
    //
    // Drive the two-level HyperWhisper Cloud picker (Provider tier → Model).
    // The provider axis is the catalog `id` (== CloudAccuracyTier storage
    // value); the model axis is the per-tier `models[]` `id` (the X-STT-Model
    // header value). `sttProvider` is the X-STT-Provider header value.
    // =========================================================================

    /// <summary>All catalog entries flagged <c>access.cloudTierEligible == true</c>, in catalog order.</summary>
    public IReadOnlyList<CloudSttCatalogEntry> CloudTierEligibleProviders()
    {
        var list = new List<CloudSttCatalogEntry>();
        foreach (var entry in Providers)
            if (entry.Access?.CloudTierEligible == true)
                list.Add(entry);
        return list;
    }

    /// <summary>The X-STT-Provider header value for the given tier id, or null if unknown.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public string? SttProviderForId(string? id)
        => string.IsNullOrEmpty(id) ? null : HyperwhisperCoreMethods.CloudSttProvider(id);

    /// <summary>
    /// Raw, upstream-native supported language codes for the given tier id, or
    /// null when unknown or the catalog leaves the set unspecified
    /// (<c>"unverified"</c>). These are in whatever format the upstream declares
    /// (ISO-639-1 two-letter, BCP-47 like <c>en-AU</c>, or ISO-639-2/3 three-letter
    /// like <c>eng</c>) — do NOT intersect them directly against the two-letter
    /// picker. Use <see cref="PickerLanguageCodesForId"/> for the language picker.
    /// </summary>
    // TODO-verify (Windows/CI): Rust shared-core swap. Core returns List<string>?;
    // adapt to the string[]? the picker consumes (null preserved for "unverified").
    public string[]? LanguageCodesForId(string? id)
        => string.IsNullOrEmpty(id) ? null : HyperwhisperCoreMethods.CloudSttLanguageCodes(id)?.ToArray();

    /// <summary>
    /// The tier's supported languages normalized to the ISO-639-1 two-letter base
    /// codes the language picker (<c>LanguageInfo.AllLanguages</c>) uses, or null
    /// when the catalog leaves the set unspecified (<c>"unverified"</c>) so the
    /// caller falls back to the full list. The catalog stores upstream-native
    /// codes in mixed formats — BCP-47 (<c>en-AU</c>, <c>cmn-Hans-CN</c>),
    /// ISO-639-2/3 (<c>eng</c>, <c>nld</c>), region variants (<c>ar-AE</c>) and
    /// sentinels (<c>multi</c>). We take the primary subtag, map three-letter codes
    /// to 639-1 via <see cref="Iso6392ToIso6391"/>, drop anything with no two-letter
    /// equivalent, and dedup (so the dozens of <c>ar-XX</c>/<c>en-XX</c> Deepgram
    /// variants collapse to <c>ar</c>/<c>en</c>). <c>"auto"</c> is always included.
    /// </summary>
    public HashSet<string>? PickerLanguageCodesForId(string? id)
    {
        var raw = LanguageCodesForId(id);
        if (raw is null) return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto" };
        foreach (var code in raw)
        {
            var normalized = NormalizeToIso6391(code);
            if (normalized != null) result.Add(normalized);
        }
        return result;
    }

    /// <summary>
    /// Reduce a single upstream language code to its ISO-639-1 two-letter base, or
    /// null when there's no clean two-letter equivalent (so it's dropped rather
    /// than poisoning the picker). Splits on <c>-</c>/<c>_</c> to take the primary
    /// subtag, then: two-letter subtags pass through; three-letter subtags are
    /// looked up in <see cref="Iso6392ToIso6391"/>; everything else (sentinels like
    /// <c>multi</c>, codes with no 639-1 form like <c>fil</c>/<c>ceb</c>) → null.
    /// </summary>
    private static string? NormalizeToIso6391(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var primary = code.Replace('_', '-').Split('-')[0].ToLowerInvariant();
        string? two = primary.Length switch
        {
            2 => primary,
            3 when Iso6392ToIso6391.TryGetValue(primary, out var mapped) => mapped,
            _ => null,
        };
        if (two is null) return null;
        // Fold picker-only aliases: the shared catalog carries provider-native codes
        // (Azure `nb`, Google Chirp `iw-IL`/`jv-ID`) whose two-letter base differs from
        // the code LanguageInfo.AllLanguages exposes for the same language (`no`/`he`/`jw`).
        // Without this fold those languages never match a picker row and silently vanish
        // from the dropdown for the Azure / Google Chirp tiers.
        return PickerLanguageAliases.TryGetValue(two, out var folded) ? folded : two;
    }

    /// <summary>
    /// Two-letter base codes that differ between an upstream's catalog declaration
    /// and the code <c>LanguageInfo.AllLanguages</c> exposes for the same language.
    /// Applied after <see cref="NormalizeToIso6391"/> reduces to a 639-1 base so the
    /// picker can match the row. Note the three-letter forms (<c>nor</c>/<c>heb</c>/
    /// <c>jav</c>) already map straight to <c>no</c>/<c>he</c>/<c>jw</c> via
    /// <see cref="Iso6392ToIso6391"/>; this only catches the two-letter/BCP-47 aliases.
    /// </summary>
    private static readonly Dictionary<string, string> PickerLanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nb"] = "no", // Norwegian Bokmål → picker's macrolanguage "no"
        ["iw"] = "he", // deprecated Hebrew code (Azure/Google) → "he"
        ["jv"] = "jw", // ISO-639-1 Javanese → picker's legacy "jw"
    };

    /// <summary>
    /// ISO-639-2/3 → ISO-639-1 map, scoped to the three-letter codes that actually
    /// appear in the catalog AND have a clean two-letter form the picker exposes.
    /// Codes without a 639-1 equivalent (<c>fil</c>, <c>ceb</c>, <c>kea</c>,
    /// <c>nso</c>, <c>nya</c>, <c>ful</c>, <c>luo</c>, <c>lug</c>, <c>xho</c>,
    /// <c>zul</c>, <c>ibo</c>, <c>kur</c>, <c>wol</c>, <c>ast</c>, <c>haw</c>…) are
    /// intentionally omitted → they normalize to null and are dropped.
    /// </summary>
    private static readonly Dictionary<string, string> Iso6392ToIso6391 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["afr"] = "af", ["amh"] = "am", ["ara"] = "ar", ["asm"] = "as", ["aze"] = "az",
        ["bel"] = "be", ["ben"] = "bn", ["bos"] = "bs", ["bul"] = "bg", ["cat"] = "ca",
        ["ces"] = "cs", ["cmn"] = "zh", ["cym"] = "cy", ["dan"] = "da", ["deu"] = "de",
        ["ell"] = "el", ["eng"] = "en", ["est"] = "et", ["fas"] = "fa", ["fil"] = "tl", ["fin"] = "fi",
        ["fra"] = "fr", ["glg"] = "gl", ["guj"] = "gu", ["hau"] = "ha", ["heb"] = "he",
        ["hin"] = "hi", ["hrv"] = "hr", ["hun"] = "hu", ["hye"] = "hy", ["ind"] = "id",
        ["isl"] = "is", ["ita"] = "it", ["jav"] = "jw", ["jpn"] = "ja", ["kan"] = "kn",
        ["kat"] = "ka", ["kaz"] = "kk", ["khm"] = "km", ["kor"] = "ko", ["lao"] = "lo",
        ["lav"] = "lv", ["lin"] = "ln", ["lit"] = "lt", ["ltz"] = "lb", ["mal"] = "ml",
        ["mar"] = "mr", ["mkd"] = "mk", ["mlt"] = "mt", ["mon"] = "mn", ["mri"] = "mi",
        ["msa"] = "ms", ["mya"] = "my", ["nep"] = "ne", ["nld"] = "nl", ["nor"] = "no",
        ["oci"] = "oc", ["pan"] = "pa", ["pol"] = "pl", ["por"] = "pt", ["pus"] = "ps",
        ["ron"] = "ro", ["rus"] = "ru", ["slk"] = "sk", ["slv"] = "sl", ["sna"] = "sn",
        ["snd"] = "sd", ["som"] = "so", ["spa"] = "es", ["srp"] = "sr", ["swa"] = "sw",
        ["swe"] = "sv", ["tam"] = "ta", ["tel"] = "te", ["tgk"] = "tg", ["tha"] = "th",
        ["tur"] = "tr", ["ukr"] = "uk", ["urd"] = "ur", ["uzb"] = "uz", ["vie"] = "vi",
        ["yor"] = "yo", ["yue"] = "yue",
    };

    /// <summary>Models offered by the given tier id, in catalog order; empty when unknown.</summary>
    public IReadOnlyList<CloudSttModel> ModelsForId(string? id) => GetById(id)?.Models ?? [];

    /// <summary>
    /// The default model id (X-STT-Model value) for the given tier — the entry
    /// flagged <c>isDefault</c>, else the first model, else null. Note: a model
    /// id may legitimately be the empty string (e.g. Grok's single implicit
    /// model), which the backend treats as "provider default".
    /// </summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public string? DefaultModelIdForId(string? id)
        => string.IsNullOrEmpty(id) ? null : HyperwhisperCoreMethods.CloudSttDefaultModelId(id);

    /// <summary>Look up a single model within a tier by its id (case-insensitive); null if not found.</summary>
    public CloudSttModel? GetModel(string? id, string? modelId)
    {
        if (modelId is null) return null;
        foreach (var m in ModelsForId(id))
            if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    /// <summary>Credits/min for a specific model within a tier; falls back to the tier cost, then 0.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap. Core owns the model→tier→0
    // fallback. Null id/modelId routes to the tier-level CreditsPerMinute shim.
    public double CreditsPerMinuteForModel(string? id, string? modelId)
    {
        if (string.IsNullOrEmpty(id) || modelId is null) return CreditsPerMinute(id);
        return HyperwhisperCoreMethods.CloudSttCreditsPerMinuteForModel(id, modelId);
    }

    /// <summary>True when the specific model within the tier supports custom vocabulary biasing.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap. No core fn returns the
    // per-model vocab flag directly, so derive it from the core's model list.
    public bool ModelSupportsCustomVocabulary(string? id, string? modelId)
    {
        if (string.IsNullOrEmpty(id) || modelId is null) return false;
        foreach (var m in HyperwhisperCoreMethods.CloudSttModels(id))
        {
            if (string.Equals(m.@id, modelId, StringComparison.OrdinalIgnoreCase))
                return m.@supportsCustomVocabulary == true;
        }
        return false;
    }

    /// <summary>
    /// Normalize a persisted <c>cloudProvider</c> storage value. If the value
    /// is a legacy standalone-provider alias for an entry now surfaced as a
    /// HyperWhisper Cloud accuracy tier (e.g. <c>microsoftazurespeech</c> →
    /// <c>azureMaiTranscribe</c>), returns <c>("hyperwhisper", &lt;new tier id&gt;)</c>.
    /// Otherwise returns the input unchanged with <c>AccuracyTier == null</c> —
    /// critically, BYOK provider names like <c>"deepgram"</c> or <c>"groq"</c>
    /// pass through untouched even though they appear in <c>migrateFrom</c>
    /// for tier-alias resolution.
    /// </summary>
    // TODO-verify (Windows/CI): Rust shared-core swap. Core owns the alias→tier
    // resolution + BYOK pass-through; we adapt its NormalizedCloudProvider record
    // to the (Provider, AccuracyTier) tuple callers expect.
    public (string? Provider, string? AccuracyTier) NormalizeCloudProvider(string? value)
    {
        NormalizedCloudProvider normalized = HyperwhisperCoreMethods.CloudSttNormalizeCloudProvider(value);
        return (normalized.@provider, normalized.@accuracyTier);
    }

    /// <summary>True when the catalog explicitly flags this tier as supporting vocabulary biasing through our backend.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public bool SupportsCustomVocabulary(string? id)
        => !string.IsNullOrEmpty(id) && HyperwhisperCoreMethods.CloudSttSupportsCustomVocabulary(id);

    /// <summary>Localized "~X credits/min" caption shown under the picker (matches macOS).</summary>
    public static string FormatCreditsPerMinute(double creditsPerMinute, string template)
    {
        var formatted = creditsPerMinute >= 10
            ? creditsPerMinute.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
            : creditsPerMinute.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return string.Format(template, formatted);
    }

    private static CloudSttCatalog LoadCatalog()
    {
        const string resourceName = "HyperWhisper.SharedAppClassification.cloud-stt-catalog.json";
        var assembly = Assembly.GetExecutingAssembly();

        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                LoggingService.Error($"CloudSttCatalog resource {resourceName} not found — falling back to empty catalog");
                return new CloudSttCatalog();
            }

            return JsonSerializer.Deserialize<CloudSttCatalog>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CloudSttCatalog();
        }
        catch (Exception ex)
        {
            // Must never propagate out of this static initializer — that would
            // poison the CLR's cached TypeInitializationException and brick the
            // mode editor for every user.
            LoggingService.Error("CloudSttCatalog deserialization failed — falling back to empty catalog", ex);
            return new CloudSttCatalog();
        }
    }
}

public sealed class CloudSttCatalogEntry
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? DisplayModel { get; init; }
    public string Vendor { get; init; } = string.Empty;

    /// <summary>The <c>X-STT-Provider</c> header value the backend routes on (e.g. "openai", "azure-mai").</summary>
    public string? SttProvider { get; init; }

    public CloudSttAccess? Access { get; init; }

    /// <summary>Per-provider model variants surfaced as the second-level picker axis.</summary>
    public CloudSttModel[] Models { get; init; } = [];

    public CloudSttCloudTier? CloudTier { get; init; }
    public CloudSttCustomVocabulary? CustomVocabulary { get; init; }
    public CloudSttLanguages? Languages { get; init; }
    public bool? PreviewStatus { get; init; }
    public string[]? MigrateFrom { get; init; }
    public string[]? LegacyCloudProviderAliases { get; init; }
}

/// <summary>
/// A single routable model within a cloud-tier provider. <see cref="Id"/> is the
/// <c>X-STT-Model</c> header value (may be the empty string for single-model
/// providers like Grok, which the backend treats as "use the provider default").
/// </summary>
public sealed class CloudSttModel
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public double CreditsPerMinute { get; init; }
    public bool IsDefault { get; init; }
    public bool PreviewStatus { get; init; }
    public bool SupportsCustomVocabulary { get; init; }
}

public sealed class CloudSttAccess
{
    public bool CloudTierEligible { get; init; }
    public bool ByokEligible { get; init; }
}

public sealed class CloudSttCloudTier
{
    public string Accuracy { get; init; } = string.Empty;
    public double CreditsPerMinute { get; init; }
}

public sealed class CloudSttCustomVocabulary
{
    /// <summary>Stringified for tri-state handling: "true" / "false" / "unverified".</summary>
    [JsonConverter(typeof(BoolOrStringConverter))]
    public string Supported { get; init; } = "false";

    public string? FieldName { get; init; }
    public string? Caveats { get; init; }
}

public sealed class CloudSttLanguages
{
    public string? Notes { get; init; }

    [JsonConverter(typeof(IntOrStringConverter))]
    public int? Count { get; init; }

    [JsonConverter(typeof(BoolOrNullableBoolConverter))]
    public bool? AutoDetect { get; init; }

    [JsonConverter(typeof(StringArrayOrStringConverter))]
    public string[]? Codes { get; init; }
}

/// <summary>Accepts <c>true</c>, <c>false</c>, or <c>"unverified"</c> and normalises to a lowercase string.</summary>
internal sealed class BoolOrStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.String => reader.GetString() ?? "false",
            _ => "false"
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>Accepts <c>true</c>, <c>false</c>, or <c>"unverified"</c> and yields null for the unverified case.</summary>
internal sealed class BoolOrNullableBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.True) return true;
        if (reader.TokenType == JsonTokenType.False) return false;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (s != null && s != "unverified")
            {
                LoggingService.Error($"CloudSttCatalog: BoolOrNullableBoolConverter invalid value=\"{s}\" — defaulting to null");
            }
            return null;
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteBooleanValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>Accepts an integer or <c>"unverified"</c> and yields null for the unverified case.</summary>
internal sealed class IntOrStringConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n)) return n;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (s != null && s != "unverified")
            {
                LoggingService.Error($"CloudSttCatalog: IntOrStringConverter invalid value=\"{s}\" — defaulting to null");
            }
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>
/// Accepts either a JSON array of strings or the literal <c>"unverified"</c> string
/// (which yields null). Used for the documented <c>"unverified"</c> escape hatch on
/// <c>languages.codes</c> — without this converter, a single malformed entry poisons
/// the entire catalog deserialization.
/// </summary>
internal sealed class StringArrayOrStringConverter : JsonConverter<string[]?>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return list.ToArray();
                if (reader.TokenType == JsonTokenType.String)
                {
                    var s = reader.GetString();
                    if (s != null) list.Add(s);
                }
            }
            return list.ToArray();
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            // Accept the documented "unverified" sentinel; anything else also
            // yields null rather than throwing — matches Swift's behaviour.
            return null;
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
