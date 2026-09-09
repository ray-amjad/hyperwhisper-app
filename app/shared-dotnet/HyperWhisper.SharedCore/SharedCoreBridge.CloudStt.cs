using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

public static partial class SharedCoreBridge
{
    public sealed record CloudSttFileConstraints(
        long? MaximumBytes,
        TimeSpan? MaximumDuration,
        IReadOnlyList<string> AcceptedFormats);
    public static string CanonicalCloudSttTier(string? value) =>
        HyperwhisperCoreMethods.MigrateCloudAccuracyTier(value);

    public static string? CloudSttProvider(string tierId) =>
        HyperwhisperCoreMethods.CloudSttProvider(tierId);

    /// <summary>
    /// Cloud-tier entry ids HyperWhisper Cloud can also serve LIVE, in catalog
    /// order — the eligible set for the streaming cloud-tier picker.
    ///
    /// Catalog-derived (<c>cloudTierEligible</c> AND some model with
    /// <c>streaming: true</c>), never a hand-kept list. Note this is NOT the
    /// entry-level <c>features.streaming</c> hint, which is true for six vendors
    /// we serve no WebSocket route for.
    /// </summary>
    public static IReadOnlyList<string> StreamingCloudSttTiers() =>
        HyperwhisperCoreMethods.CloudSttStreamingCloudTierEntryIds();

    /// <summary>
    /// A HyperWhisper Cloud accuracy tier's own display name, e.g. "ElevenLabs Scribe v2" for
    /// `elevenLabsScribeV2`. The tier is the whole model choice a HyperWhisper Cloud mode has, so
    /// this is the string to draw wherever such a mode is asked what model it runs. Null when the
    /// id is not a catalog tier, so the caller can decide what to draw instead.
    /// </summary>
    public static string? CloudSttTierLabel(string? tierId)
    {
        if (string.IsNullOrWhiteSpace(tierId)) return null;
        var entry = HyperwhisperCoreMethods.CloudSttEntry(tierId.Trim());
        return string.IsNullOrWhiteSpace(entry?.@displayName) ? null : entry!.@displayName;
    }

    public static string? CloudSttDefaultModel(string tierId) =>
        HyperwhisperCoreMethods.CloudSttDefaultModelId(tierId);

    /// <summary>
    /// PER-MODEL supported language codes from <c>shared-models/models-catalog.json</c>,
    /// or null when that file carries no explicit list for the model (a wildcard
    /// row, a <c>supportsAllLanguages</c> row, or no row at all) and the caller
    /// should keep whatever broader set it already has.
    ///
    /// This is a DIFFERENT file and a different code space from
    /// <c>cloud-stt-catalog.json</c>'s <c>languages.codes</c>: that field is
    /// PROVIDER-level and holds upstream's own codes, this one is per model and
    /// folded to the picker space. Where a provider's models disagree — Azure
    /// MAI's 60 vs 42 — only this one can answer "what does THIS model do".
    ///
    /// <paramref name="provider"/> is the models-catalog provider key
    /// (<c>microsoftAzureSpeech</c>), not the cloud-stt entry id
    /// (<c>azureMaiTranscribe</c>) and not the <c>sttProvider</c> dispatch key
    /// (<c>azure-mai</c>). The three namespaces are deliberately separate.
    /// </summary>
    public static IReadOnlyList<string>? SharedModelVoiceLanguageCodes(string provider, string? modelId)
    {
        var support = HyperwhisperCoreMethods.ModelsLanguageSupport(provider, HwKind.Voice, modelId ?? "");
        return support.@supportsAll || support.@codes.Count == 0 ? null : support.@codes;
    }

    public static CloudSttFileConstraints? CloudSttFileLimits(string tierId)
    {
        var entry = HyperwhisperCoreMethods.CloudSttEntry(tierId);
        if (entry is null) return null;
        var maximumBytes = entry.@maxFileSizeMb is { } mb
            ? checked((long)Math.Round(mb * 1024 * 1024, MidpointRounding.AwayFromZero))
            : (long?)null;
        var maximumDuration = entry.@maxDurationMinutes is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : (TimeSpan?)null;
        return new(maximumBytes, maximumDuration, entry.@acceptedFormats);
    }

    public static bool CloudSttContainsModel(string tierId, string modelId) =>
        HyperwhisperCoreMethods.CloudSttModels(tierId)
            .Any(model => string.Equals(model.id, modelId, StringComparison.Ordinal));

    /// <summary>
    /// Model ids HyperWhisper Cloud serves ONLY over its live WebSocket route.
    /// A pre-recorded POST carrying one of these is an HTTP 400 from the
    /// upstream vendor, on every dictation, for as long as the mode keeps it.
    ///
    /// NOT derivable from the per-model <c>streaming</c> flag, despite how that
    /// reads. <c>streaming: true</c> means "HyperWhisper Cloud routes this model
    /// live", and <c>deepgramNova3</c> carries it on BOTH <c>nova-3-general</c>
    /// and <c>nova-3-medical</c> — the default pre-recorded models. Filtering on
    /// that flag would delete Deepgram's default dictation model.
    ///
    /// The catalog has no live-only field, so this is the shared-.NET mirror of
    /// the same literal the other heads keep:
    /// <c>CloudSttCatalog.LiveOnlyModelIds</c> (Windows),
    /// <c>CloudSTTCatalog.liveOnlyModelIds</c> (macOS). All three are pinned
    /// against <c>shared-conformance/live-only-models.json</c> so they cannot
    /// drift apart.
    /// </summary>
    public static IReadOnlySet<string> LiveOnlyCloudSttModelIds { get; } =
        new HashSet<string>(
            ["gemini-3.5-transcribe-live", "gpt-live-transcribe"],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="modelId"/> is one of
    /// <see cref="LiveOnlyCloudSttModelIds"/> (trimmed, case-insensitive).
    /// False for null/blank — "no model chosen" resolves to the tier default,
    /// which is never live-only.
    /// </summary>
    public static bool IsLiveOnlyCloudSttModel(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && LiveOnlyCloudSttModelIds.Contains(modelId.Trim());

    /// <summary>
    /// Tier membership for a PRE-RECORDED request: the model must be in the
    /// tier AND not live-only. Plain <see cref="CloudSttContainsModel"/> accepts
    /// a live-only id, because it genuinely IS a model of the tier — the Linux
    /// model box is a bare text field, and a backup restore or a Local API write
    /// can put one there on any platform. Callers that route a file or a
    /// dictation must use this one and fall back to the tier default.
    /// </summary>
    public static bool CloudSttContainsDictationModel(string tierId, string modelId) =>
        !IsLiveOnlyCloudSttModel(modelId) && CloudSttContainsModel(tierId, modelId);

    /// <summary>
    /// A legacy cloud-STT model id resolved to the id the catalog still carries,
    /// scoped by the persisted <c>cloudProvider</c> identifier.
    /// </summary>
    /// <remarks>
    /// The one alias table lives in <c>hw-catalog</c>
    /// (<c>model_alias.rs</c>) and is what Windows
    /// <c>CloudTranscriptionModels.ResolveModelAlias</c> and macOS
    /// <c>CloudTranscriptionModels.resolveModelAlias</c> both call. This head had
    /// no wrapper for it at all, which is why its Local API could only compare
    /// model ids as raw strings (issue #566).
    ///
    /// <paramref name="providerIdentifier"/> is the storage spelling
    /// (<c>deepgram</c>, <c>microsoftAzureSpeech</c>) — NOT the cloud-STT entry
    /// id (<c>deepgramNova3</c>) and not the <c>sttProvider</c> dispatch key
    /// (<c>azure-mai</c>). Null or an unrecognised identifier means "provider
    /// unknown", and the core then chains every table, exactly as a C# null
    /// provider does on Windows.
    /// </remarks>
    public static string ResolveCloudSttModelAlias(string? modelId, string? providerIdentifier) =>
        string.IsNullOrEmpty(modelId)
            ? modelId ?? string.Empty
            : HyperwhisperCoreMethods.CloudSttResolveModelAlias(modelId, providerIdentifier);

    /// <summary>
    /// Tier membership that resolves legacy aliases first — the alias-resolving
    /// half of a foreign-model guard.
    /// </summary>
    /// <remarks>
    /// <see cref="CloudSttContainsModel"/> is an exact, case-sensitive scan, so a
    /// legacy-but-serviceable id such as AssemblyAI <c>universal</c> or Gemini
    /// <c>gemini-2.0-flash</c> reads as "not one of this provider's models" and a
    /// guard built on it would silently upgrade the request to a different-priced
    /// model. That is the failure #528's second correction exists to prevent, and
    /// it is why this method exists rather than the raw scan (issue #566).
    ///
    /// The comparison is case-insensitive to match Windows
    /// <c>CloudTranscriptionModels.GetById</c>, whose final compare is
    /// <c>OrdinalIgnoreCase</c>.
    /// </remarks>
    public static bool CloudSttContainsModelResolvingAlias(
        string tierId,
        string? modelId,
        string? providerIdentifier)
    {
        var trimmed = modelId?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        var canonical = ResolveCloudSttModelAlias(trimmed, providerIdentifier);
        return HyperwhisperCoreMethods.CloudSttModels(tierId)
            .Any(model => string.Equals(model.id, canonical, StringComparison.OrdinalIgnoreCase));
    }
}
