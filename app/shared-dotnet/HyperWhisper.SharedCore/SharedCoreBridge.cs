using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

public sealed record BackupValidationFailure(string Path, string Message);

public sealed record PortablePromptContext(
    string Preset,
    string CustomInstructions,
    string EnglishSpelling,
    string Language,
    string UserSystemPrompt,
    IReadOnlyList<string> VocabularyWords,
    bool Punctuation,
    bool Capitalization,
    bool ProfanityFilter,
    string Time,
    string Timezone,
    string Locale,
    string ComputerName,
    string AppType = "other",
    string AppName = "",
    string Category = "",
    string Description = "",
    string TextFormat = "",
    string BrowserHost = "",
    string BrowserTabTitle = "",
    string FocusedElement = "",
    string FocusedContent = "",
    string ScreenOcrText = "",
    string AppTypeConfidence = "unknown",
    string AppTypeSource = "default",
    bool HasApplicationContext = false);

public sealed record PortablePostProcessingPrompt(string SystemPrompt, string SystemInfo);

public enum PortableLlmWireProtocol
{
    OpenAiChat,
    AnthropicMessages,
}

public sealed record PortableCompletionEvaluation(
    string Text,
    bool Accepted,
    string? Failure);

public enum PortableCursorContext
{
    Unknown,
    StartOfSentence,
    MidSentence,
}

/// <summary>
/// Stable public surface over the generated UniFFI binding. Platform projects
/// consume this assembly instead of compiling private copies of the binding.
/// </summary>
public static class SharedCoreBridge
{
    public static bool ContainsCjk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ContainsCjk(text);
    }

    public static string NormalizeAppType(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var appType = HyperwhisperCoreMethods.AppTypeFromRaw(value);
        return HyperwhisperCoreMethods.AppTypePromptValue(appType);
    }

    public static string AppendTrailingSpace(string text, string language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        return HyperwhisperCoreMethods.AppendTrailingSpace(text, language);
    }

    public static string ApplyAutocapitalize(string text, PortableCursorContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        var native = context switch
        {
            PortableCursorContext.Unknown => CursorContext.Unknown,
            PortableCursorContext.StartOfSentence => CursorContext.StartOfSentence,
            PortableCursorContext.MidSentence => CursorContext.MidSentence,
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null),
        };
        return HyperwhisperCoreMethods.ApplyAutocapitalize(text, native);
    }

    public static IReadOnlyList<BackupValidationFailure> ValidateBackup(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return HyperwhisperCoreMethods.ValidateBackupJson(json)
            .Select(error => new BackupValidationFailure(error.path, error.message))
            .ToArray();
    }

    public static PortablePostProcessingPrompt BuildPostProcessingPrompt(PortablePromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var native = new PromptContext(
            PresetFromRaw(context.Preset),
            context.CustomInstructions ?? string.Empty,
            EnglishSpellingFromRaw(context.EnglishSpelling),
            context.Language ?? string.Empty,
            context.UserSystemPrompt ?? string.Empty,
            AppTypeFromRaw(context.AppType),
            Bound(context.AppName, 256),
            Bound(context.Category, 128),
            Bound(context.Description, 500),
            Bound(context.TextFormat, 64),
            Bound(context.BrowserHost, 253),
            Bound(context.BrowserTabTitle, 500),
            Bound(context.FocusedElement, 128),
            BoundWithEllipsis(context.FocusedContent, 100),
            Bound(context.ScreenOcrText, 2000),
            Bound(context.AppTypeConfidence, 32, "unknown"),
            Bound(context.AppTypeSource, 64, "default"),
            context.HasApplicationContext,
            context.VocabularyWords?.Where(item => !string.IsNullOrWhiteSpace(item)).ToList() ?? [],
            context.Time ?? string.Empty,
            context.Timezone ?? string.Empty,
            context.Locale ?? string.Empty,
            context.ComputerName ?? string.Empty,
            context.Punctuation,
            context.Capitalization,
            context.ProfanityFilter);
        return new PortablePostProcessingPrompt(
            HyperwhisperCoreMethods.BuildSystemPrompt(native),
            HyperwhisperCoreMethods.BuildSystemInfo(native));
    }

    public static PortableCompletionEvaluation EvaluateLlmResponseJson(
        PortableLlmWireProtocol wireProtocol,
        string responseJson,
        string original)
    {
        ArgumentNullException.ThrowIfNull(responseJson);
        ArgumentNullException.ThrowIfNull(original);
        var native = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
            wireProtocol switch
            {
                PortableLlmWireProtocol.OpenAiChat => WireProtocol.OpenAiChat,
                PortableLlmWireProtocol.AnthropicMessages => WireProtocol.AnthropicMessages,
                _ => throw new ArgumentOutOfRangeException(nameof(wireProtocol), wireProtocol, null),
            },
            responseJson,
            original);
        return new PortableCompletionEvaluation(
            native.text,
            native.accepted,
            native.failure.ToString());
    }

    public static string RemoveTrailingPeriod(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.RemoveTrailingPeriod(text);
    }

    public static string RemoveFillerWords(string text, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.RemoveFillerWords(text, language);
    }

    /// <summary>
    /// The canonical vocabulary-egress normalization: sanitize each term (strip
    /// <c>&lt;</c>/<c>&gt;</c>, collapse whitespace runs, cap at the core's
    /// 80-character term limit), drop the ones that sanitize to nothing,
    /// de-duplicate case-insensitively keeping first-seen order and casing, and
    /// optionally stop after <paramref name="limit"/> terms.
    ///
    /// <paramref name="limit"/> is <c>null</c> for "no cap"; <c>0</c> means zero
    /// terms, exactly like <c>.Take(0)</c>. Each call site owns its own cap and
    /// its own join separator — only this rule is shared.
    /// </summary>
    public static IReadOnlyList<string> NormalizeVocabularyTerms(
        IReadOnlyList<string>? words,
        uint? limit)
        => HyperwhisperCoreMethods.NormalizeVocabularyTerms(
            // Guarding empties here keeps a null/blank row out of the FFI call;
            // the core drops them anyway once they sanitize to nothing.
            words?.Where(word => !string.IsNullOrEmpty(word)).ToList() ?? [],
            limit);

    public static string ProcessVoiceCommands(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ProcessVoiceCommands(text);
    }

    public static string ApplyHardenedReplacement(string text, string word, string replacement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(replacement);
        return HyperwhisperCoreMethods.ApplyHardenedReplacement(text, word, replacement);
    }

    /// <summary>
    /// The <c>Mode.EnglishSpelling</c> token to SEED into a brand-new mode, from
    /// an ISO 3166-1 alpha-2 region code. Trimming, case folding and the region
    /// table are the core's; an unknown, empty or null code gives "american".
    ///
    /// This is a seeding call and is NOT the inverse of the prompt path's
    /// spelling parse: an empty stored value there means "the user never chose"
    /// and suppresses the spelling instruction entirely, which is never a thing
    /// to seed. The core never answers <c>HwEnglishSpelling.None</c> here, so
    /// this needs no "american" fallback of its own.
    /// </summary>
    public static string EnglishSpellingForRegion(string? regionCode) =>
        HyperwhisperCoreMethods.EnglishSpellingRawValue(
            HyperwhisperCoreMethods.EnglishSpellingForRegion(regionCode));

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

    public static string? CloudSttDefaultModel(string tierId) =>
        HyperwhisperCoreMethods.CloudSttDefaultModelId(tierId);

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

    private static Preset PresetFromRaw(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "message" => Preset.Message,
        "mail" => Preset.Mail,
        "note" => Preset.Note,
        "meeting" => Preset.Meeting,
        "code" => Preset.Code,
        "custom" => Preset.Custom,
        _ => Preset.Hyper,
    };

    private static HwAppType AppTypeFromRaw(string? value) =>
        HyperwhisperCoreMethods.AppTypeFromRaw(value ?? string.Empty);

    private static string Bound(string? value, int maxCharacters, string fallback = "")
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return value.Length <= maxCharacters ? value : value[..maxCharacters];
    }

    private static string BoundWithEllipsis(string? value, int maxSourceCharacters)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxSourceCharacters
            ? value
            : string.Concat(value.AsSpan(0, maxSourceCharacters), "...");
    }

    private static HwEnglishSpelling EnglishSpellingFromRaw(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "american" => HwEnglishSpelling.American,
            "british" => HwEnglishSpelling.British,
            "australian" => HwEnglishSpelling.Australian,
            "canadian" => HwEnglishSpelling.Canadian,
            _ => HwEnglishSpelling.None,
        };
}
