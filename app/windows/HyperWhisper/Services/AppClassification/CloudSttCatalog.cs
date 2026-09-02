// Rust shared-core binding. `NormalizedCloudProvider` lives here; no native
// type of that name, so no qualification needed.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.AppClassification;

/// <summary>
/// Windows facade over the shared cloud-STT catalog.
///
/// This class used to decode <c>shared-app-classification/cloud-stt-catalog.json</c>
/// itself — 4 hand-written <c>JsonConverter</c>s for the polymorphic fields, a
/// vendor-group fold, and a 90-entry ISO-639 table for the language picker. All
/// of that now lives once in <c>shared-core-rs/crates/hw-catalog</c> (issue #280);
/// the copies had already drifted from macOS (<c>deepgramNova3</c> offered
/// <c>gu</c>/<c>th</c>/<c>zh</c> here and not there). What is left is a mapping
/// layer: the generated UniFFI types are <c>internal</c>, so the public methods
/// below hand back the small DTOs at the bottom of this file rather than binding
/// types. <c>shared-conformance/catalog-vectors.json</c> pins the answers.
///
/// Nothing here reads a file or an embedded resource — the catalog JSON is
/// <c>include_str!</c>'d into the Rust core at compile time.
/// </summary>
public sealed class CloudSttCatalog
{
    public static CloudSttCatalog Shared { get; } = Load();

    /// <summary>All provider entries, in catalog order.</summary>
    public CloudSttCatalogEntry[] Providers { get; private init; } = [];

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
    /// (case-insensitive, trimmed). Drives legacy <c>cloudAccuracyTier</c>
    /// resolution — NOT <c>cloudProvider</c> rewriting, which is
    /// <see cref="NormalizeCloudProvider"/>.
    /// </summary>
    public CloudSttCatalogEntry? GetByMigrateFromAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        var needle = alias.Trim();
        foreach (var entry in Providers)
        {
            foreach (var candidate in entry.MigrateFrom)
            {
                if (string.Equals(candidate, needle, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        return null;
    }

    /// <summary>Display-only cost in credits per minute for the given tier; 0 if unknown.</summary>
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

    /// <summary>
    /// The cloud-tier entries HyperWhisper Cloud can also serve LIVE, in catalog
    /// order — the eligible set for the streaming cloud-tier picker.
    ///
    /// Catalog-derived in Rust (<c>cloudTierEligible</c> AND some model with
    /// <c>streaming: true</c>), never a hand-kept list here. Deliberately NOT the
    /// entry-level <c>features.streaming</c> hint, which is true for six vendors
    /// we serve no WebSocket route for — offering one of those would ship a 404 at
    /// dictation time, and the STT catalog has no <c>enabled</c> gate to hide it.
    ///
    /// Ids are re-resolved through <see cref="GetById"/> exactly like the vendor
    /// groups are, so a listed entry and the same entry fetched by id are one
    /// object rather than two equal copies.
    /// </summary>
    public IReadOnlyList<CloudSttCatalogEntry> StreamingCloudTierEntries()
    {
        var list = new List<CloudSttCatalogEntry>();
        foreach (var id in HyperwhisperCoreMethods.CloudSttStreamingCloudTierEntryIds())
        {
            var entry = GetById(id);
            if (entry != null) list.Add(entry);
        }
        return list;
    }

    /// <summary>
    /// The Provider dropdown's rows: cloud-tier entries grouped by <c>vendor</c>
    /// and sorted by company name, so the list reads alphabetically and each
    /// company appears exactly once. Google owns two entries (Chirp + Gemini)
    /// and so contributes one row whose model list spans both.
    /// </summary>
    public IReadOnlyList<CloudSttVendorGroup> CloudTierVendorGroups() => _vendorGroups;

    /// <summary>The vendor group a cloud-tier id belongs to, or null if unknown.</summary>
    public CloudSttVendorGroup? VendorGroupForId(string? tierId)
    {
        var vendor = GetById(tierId)?.Vendor;
        return string.IsNullOrEmpty(vendor) ? null : VendorGroupForVendorKey(vendor);
    }

    /// <summary>The vendor group with the given <c>vendor</c> key, or null if unknown.</summary>
    public CloudSttVendorGroup? VendorGroupForVendorKey(string? vendorKey)
    {
        if (string.IsNullOrEmpty(vendorKey)) return null;
        foreach (var group in _vendorGroups)
            if (string.Equals(group.VendorKey, vendorKey, StringComparison.OrdinalIgnoreCase))
                return group;
        return null;
    }

    /// <summary>
    /// Cloud model ids HyperWhisper Cloud serves ONLY over the live WebSocket
    /// route. They must never be offered as — or accepted as — a mode's dictation
    /// model: <c>/transcribe</c> answers one with a 400, so every dictation in
    /// such a mode fails.
    ///
    /// NOT derivable from the per-model <see cref="CloudSttModel.Streaming"/>
    /// flag, despite how that reads. <c>streaming: true</c> means "HyperWhisper
    /// Cloud routes this model live", and <c>deepgramNova3</c> carries it on BOTH
    /// <c>nova-3-general</c> and <c>nova-3-medical</c> — which are the DEFAULT
    /// pre-recorded models. Filtering the dictation picker on that flag would
    /// delete the default dictation model from it.
    ///
    /// The catalog has no "live-only" field, so this list is the Windows mirror of
    /// the same fact the other heads state literally:
    /// <c>GEMINI_TRANSCRIBE_LIVE_MODEL</c> in
    /// <c>hyperwhisper-cloud/src/providers/gemini-transcribe.ts</c> (which raises
    /// the 400), <c>LIVE_MODEL</c> in hw-net's <c>gemini_transcribe.rs</c>,
    /// <c>CloudSTTCatalog.liveOnlyModelIds</c> on macOS, and the deliberate
    /// omission from <c>CloudTranscriptionModel.GeminiTranscribe</c> here. Adding
    /// a catalog field would let all of them derive it — raised as a follow-up.
    /// </summary>
    public static readonly IReadOnlySet<string> LiveOnlyModelIds =
        new HashSet<string>(
            ["gemini-3.5-transcribe-live", "gpt-live-transcribe"],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="modelId"/> is one of <see cref="LiveOnlyModelIds"/>
    /// (case-insensitive and trimmed, matching the rest of this catalog's model
    /// lookups). False for null/blank — "no model chosen" resolves to the tier
    /// default, which is never live-only.
    /// </summary>
    public static bool IsLiveOnlyModel(string? modelId)
        => !string.IsNullOrWhiteSpace(modelId) && LiveOnlyModelIds.Contains(modelId.Trim());

    /// <summary>
    /// Every DICTATION model offered by a vendor group, each paired with the tier
    /// that owns it. The owning tier is what becomes the X-STT-Provider header, so
    /// a merged row (Google) can still route each model correctly.
    ///
    /// Live-only models are excluded — see <see cref="LiveOnlyModelIds"/>, and
    /// note in particular that the per-model <c>streaming</c> flag is NOT the
    /// filter. Offering a live-only model in the Mode editor's Model dropdown
    /// ships a selectable row on which every dictation fails with HTTP 400. The
    /// live picker is a different list built from
    /// <see cref="StreamingCloudTierEntries"/>.
    /// </summary>
    public IReadOnlyList<CloudSttVendorModel> ModelsForVendorKey(string? vendorKey)
    {
        var group = VendorGroupForVendorKey(vendorKey);
        if (group == null) return [];

        var models = new List<CloudSttVendorModel>();
        foreach (var entry in group.Entries)
            foreach (var model in entry.Models)
                if (!IsLiveOnlyModel(model.Id))
                    models.Add(new CloudSttVendorModel { TierId = entry.Id, Model = model });
        return models;
    }

    /// <summary>
    /// The given tier's own models minus the live-only ones — the set the SEND
    /// path validates a stored <c>CloudTranscriptionModel</c> against.
    ///
    /// The picker no longer offers a live-only id, but a backup restore, a Local
    /// API write or a mode saved before that filter existed can all still put one
    /// in the field, and a plain tier-membership test accepts it because it IS a
    /// model of the tier.
    /// </summary>
    public IReadOnlyList<CloudSttModel> DictationModelsForId(string? id)
        => [.. ModelsForId(id).Where(model => !IsLiveOnlyModel(model.Id))];

    /// <summary>The X-STT-Provider header value for the given tier id, or null if unknown.</summary>
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
    public string[]? LanguageCodesForId(string? id)
        => string.IsNullOrEmpty(id) ? null : HyperwhisperCoreMethods.CloudSttLanguageCodes(id)?.ToArray();

    /// <summary>
    /// The tier's supported languages normalized to two-letter base codes, or
    /// null when the catalog leaves the set unspecified (<c>"unverified"</c>) so
    /// the caller falls back to the full list. The fold itself — primary subtag,
    /// the ISO-639-2/3 map, the <c>nb</c>/<c>iw</c>/<c>jv</c> picker aliases,
    /// dedup, and the always-present <c>"auto"</c> — lives in the Rust core, so
    /// macOS and Windows cannot answer differently.
    ///
    /// <para>Note that this stayed two-letter-only while the picker
    /// (<c>LanguageInfo.AllLanguages</c>) did not: since issue #285 the picker
    /// also carries region and script rows (<c>en-GB</c>, <c>pt-BR</c>,
    /// <c>zh-Hant</c>), and none of them is in this set. Intersecting against it
    /// therefore hides every variant row on a tier with a verified language set.
    /// That errs safe — it never offers a language the tier cannot do — but it
    /// is why a HW Cloud tier shows fewer languages than a BYOK one.</para>
    /// </summary>
    public HashSet<string>? PickerLanguageCodesForId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var folded = HyperwhisperCoreMethods.CloudSttPickerLanguageCodes(id);
        return folded is null ? null : new HashSet<string>(folded, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Models offered by the given tier id, in catalog order; empty when unknown.</summary>
    public IReadOnlyList<CloudSttModel> ModelsForId(string? id) => GetById(id)?.Models ?? [];

    /// <summary>
    /// The default model id (X-STT-Model value) for the given tier — the entry
    /// flagged <c>isDefault</c>, else the first model, else null. Note: a model
    /// id may legitimately be the empty string (e.g. Grok's single implicit
    /// model), which the backend treats as "provider default".
    /// </summary>
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
    public double CreditsPerMinuteForModel(string? id, string? modelId)
    {
        if (string.IsNullOrEmpty(id) || modelId is null) return CreditsPerMinute(id);
        return HyperwhisperCoreMethods.CloudSttCreditsPerMinuteForModel(id, modelId);
    }

    /// <summary>True when the specific model within the tier supports custom vocabulary biasing.</summary>
    public bool ModelSupportsCustomVocabulary(string? id, string? modelId)
        => GetModel(id, modelId)?.SupportsCustomVocabulary == true;

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
    public (string? Provider, string? AccuracyTier) NormalizeCloudProvider(string? value)
    {
        NormalizedCloudProvider normalized = HyperwhisperCoreMethods.CloudSttNormalizeCloudProvider(value);
        return (normalized.@provider, normalized.@accuracyTier);
    }

    /// <summary>True when the catalog explicitly flags this tier as supporting vocabulary biasing through our backend.</summary>
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

    // =========================================================================
    // LOADING
    // =========================================================================

    private IReadOnlyList<CloudSttVendorGroup> _vendorGroups = [];

    /// <summary>
    /// Snapshot the catalog once from the Rust core. The vendor grouping is
    /// materialised here rather than per call: it is read inside the Provider
    /// dropdown's build loop, and the fold allocates.
    /// </summary>
    private static CloudSttCatalog Load()
    {
        try
        {
            var catalog = new CloudSttCatalog
            {
                Providers = [.. HyperwhisperCoreMethods.CloudSttEntries().Select(MapEntry)],
            };
            // Re-resolve each group member out of `Providers` by id, so a group
            // entry and the same entry fetched through GetById are one object
            // rather than two equal copies.
            catalog._vendorGroups =
            [
                .. HyperwhisperCoreMethods.CloudSttCloudTierVendorGroups().Select(g =>
                    new CloudSttVendorGroup
                    {
                        VendorKey = g.@vendorKey,
                        DisplayName = g.@displayName,
                        Entries = [.. g.@entries
                            .Select(e => catalog.GetById(e.@id))
                            .Where(e => e is not null)
                            .Select(e => e!)],
                    }),
            ];
            return catalog;
        }
        catch (Exception ex)
        {
            // Must never propagate out of this static initializer — that would
            // poison the CLR's cached TypeInitializationException and brick the
            // mode editor for every user. The only realistic cause now is the
            // native core failing to load; the empty catalog keeps the Provider
            // dropdown on its enum fallback rows.
            LoggingService.Error("CloudSttCatalog failed to load from the shared core — falling back to empty catalog", ex);
            return new CloudSttCatalog();
        }
    }

    private static CloudSttCatalogEntry MapEntry(SttEntry e) => new()
    {
        Id = e.@id,
        DisplayName = e.@displayName,
        DisplayModel = e.@displayModel,
        Vendor = e.@vendor,
        VendorDisplayName = e.@vendorDisplayName,
        VendorLabel = e.@vendorLabel,
        SttProvider = e.@sttProvider,
        Access = e.@access is null ? null : new CloudSttAccess
        {
            CloudTierEligible = e.@access.@cloudTierEligible,
            ByokEligible = e.@access.@byokEligible,
        },
        Models = [.. e.@models.Select(m => new CloudSttModel
        {
            Id = m.@id,
            DisplayName = m.@displayName,
            // The core models each of these as optional because the catalog may
            // omit them; absent means the same thing the JSON decoder's default
            // meant — no price, not the default, not preview, no vocabulary.
            CreditsPerMinute = m.@creditsPerMinute ?? 0,
            IsDefault = m.@isDefault ?? false,
            PreviewStatus = m.@previewStatus ?? false,
            SupportsCustomVocabulary = m.@supportsCustomVocabulary ?? false,
            Streaming = m.@streaming ?? false,
        })],
        CloudTier = e.@cloudTier is null ? null : new CloudSttCloudTier
        {
            Accuracy = e.@cloudTier.@accuracy,
            CreditsPerMinute = e.@cloudTier.@creditsPerMinute,
        },
        Features = new CloudSttFeatures
        {
            WordTimestamps = e.@features.@wordTimestamps,
            Diarization = e.@features.@diarization,
            Streaming = e.@features.@streaming,
            CodeSwitching = e.@features.@codeSwitching,
            Endpointing = e.@features.@endpointing,
            ContextBias = e.@features.@contextBias,
            LanguageBias = e.@features.@languageBias,
            TurnTimestamps = e.@features.@turnTimestamps,
        },
        CustomVocabulary = e.@customVocabulary is null ? null : new CloudSttCustomVocabulary
        {
            Supported = e.@customVocabulary.@supported switch
            {
                VocabSupport.Yes => "true",
                VocabSupport.Unverified => "unverified",
                _ => "false",
            },
            FieldName = e.@customVocabulary.@fieldName,
            Caveats = e.@customVocabulary.@caveats,
        },
        Languages = new CloudSttLanguages
        {
            Count = e.@languages.@count is { } count ? (int)count : null,
            AutoDetect = e.@languages.@autoDetect,
            CodeFormat = e.@languages.@codeFormat,
            Notes = e.@languages.@notes,
            HasCodes = e.@languages.@hasCodes,
        },
        MaxFileSizeMb = e.@maxFileSizeMb,
        MaxDurationMinutes = e.@maxDurationMinutes is { } minutes ? (int)minutes : null,
        AcceptedFormats = [.. e.@acceptedFormats],
        PreviewStatus = e.@previewStatus,
        MigrateFrom = [.. e.@migrateFrom],
        LegacyCloudProviderAliases = [.. e.@legacyCloudProviderAliases],
    };
}

/// <summary>
/// One row of the Provider dropdown: a company and every cloud-tier entry it
/// owns. <see cref="Entries"/> is never empty and stays in catalog order.
/// </summary>
public sealed class CloudSttVendorGroup
{
    /// <summary>The catalog <c>vendor</c> key — the dropdown's selection tag.</summary>
    public string VendorKey { get; init; } = string.Empty;

    /// <summary>Plain company name shown in the dropdown.</summary>
    public string DisplayName { get; init; } = string.Empty;

    public IReadOnlyList<CloudSttCatalogEntry> Entries { get; init; } = [];

    /// <summary>The entry a fresh selection lands on — the first in catalog order.</summary>
    public CloudSttCatalogEntry DefaultEntry => Entries[0];
}

/// <summary>A model together with the cloud tier that owns it.</summary>
public sealed class CloudSttVendorModel
{
    public string TierId { get; init; } = string.Empty;
    public CloudSttModel Model { get; init; } = new();
}

public sealed class CloudSttCatalogEntry
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? DisplayModel { get; init; }
    public string Vendor { get; init; } = string.Empty;

    /// <summary>
    /// Plain company name shown in the Provider dropdown ("Deepgram", "xAI").
    /// Catalog v7+ — carries no model family or version, unlike
    /// <see cref="DisplayName"/> ("Deepgram Nova 3"), which stays for tooltips
    /// and diagnostics. Null on an older catalog; use <see cref="VendorLabel"/>,
    /// which has the fallback applied.
    /// </summary>
    public string? VendorDisplayName { get; init; }

    /// <summary><see cref="VendorDisplayName"/> falling back to <see cref="DisplayName"/>.</summary>
    public string VendorLabel { get; init; } = string.Empty;

    /// <summary>The <c>X-STT-Provider</c> header value the backend routes on (e.g. "openai", "azure-mai").</summary>
    public string? SttProvider { get; init; }

    public CloudSttAccess? Access { get; init; }

    /// <summary>Per-provider model variants surfaced as the second-level picker axis.</summary>
    public IReadOnlyList<CloudSttModel> Models { get; init; } = [];

    public CloudSttCloudTier? CloudTier { get; init; }
    public CloudSttFeatures Features { get; init; } = new();
    public CloudSttCustomVocabulary? CustomVocabulary { get; init; }
    public CloudSttLanguages Languages { get; init; } = new();

    /// <summary>Upload size ceiling in MB; null when the catalog says "unverified".</summary>
    public double? MaxFileSizeMb { get; init; }

    /// <summary>Per-request duration ceiling in minutes; null when absent or "unverified".</summary>
    public int? MaxDurationMinutes { get; init; }

    public IReadOnlyList<string> AcceptedFormats { get; init; } = [];
    public bool? PreviewStatus { get; init; }

    /// <summary>Legacy tier aliases. Empty rather than null when the catalog lists none.</summary>
    public IReadOnlyList<string> MigrateFrom { get; init; } = [];

    /// <summary>Legacy standalone-provider aliases. Empty rather than null when the catalog lists none.</summary>
    public IReadOnlyList<string> LegacyCloudProviderAliases { get; init; } = [];
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

    /// <summary>
    /// Catalog v8: HyperWhisper Cloud routes THIS model over a live WebSocket.
    /// It says nothing about whether the model also has a pre-recorded endpoint —
    /// <c>nova-3-general</c> and <c>nova-3-medical</c> both carry it and are
    /// Deepgram's default DICTATION models. So this is emphatically NOT the
    /// dictation-picker filter; that is
    /// <see cref="CloudSttCatalog.LiveOnlyModelIds"/>. What it does drive is the
    /// live tier list (<see cref="CloudSttCatalog.StreamingCloudTierEntries"/>),
    /// derived in Rust from this same flag.
    ///
    /// Distinct again from the entry-level <see cref="CloudSttFeatures.Streaming"/>
    /// vendor hint, which is merely "this vendor offers streaming somewhere".
    /// </summary>
    public bool Streaming { get; init; }
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

/// <summary>Per-provider capability flags (catalog v7). All false when absent.</summary>
public sealed class CloudSttFeatures
{
    public bool WordTimestamps { get; init; }
    public bool Diarization { get; init; }
    public bool Streaming { get; init; }
    public bool CodeSwitching { get; init; }
    public bool Endpointing { get; init; }
    public bool ContextBias { get; init; }
    public bool LanguageBias { get; init; }
    public bool TurnTimestamps { get; init; }
}

public sealed class CloudSttCustomVocabulary
{
    /// <summary>Stringified tri-state: "true" / "false" / "unverified".</summary>
    public string Supported { get; init; } = "false";

    public string? FieldName { get; init; }
    public string? Caveats { get; init; }
}

/// <summary>
/// The provider's <c>languages</c> metadata, WITHOUT the code list — that is a
/// per-provider lookup (<see cref="CloudSttCatalog.LanguageCodesForId"/> /
/// <see cref="CloudSttCatalog.PickerLanguageCodesForId"/>) so the ~736 codes in
/// the catalog never travel with every entry.
/// </summary>
public sealed class CloudSttLanguages
{
    public string? Notes { get; init; }

    /// <summary>Upstream's declared count; null when "unverified". May differ from the code count.</summary>
    public int? Count { get; init; }

    public bool? AutoDetect { get; init; }

    /// <summary>Description of the code space <c>codes</c> is written in.</summary>
    public string? CodeFormat { get; init; }

    /// <summary>False when the catalog leaves the code set "unverified".</summary>
    public bool HasCodes { get; init; }
}
