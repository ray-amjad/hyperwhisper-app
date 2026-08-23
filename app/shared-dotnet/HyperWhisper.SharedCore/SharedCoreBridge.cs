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

    public static string CanonicalCloudSttTier(string? value) =>
        HyperwhisperCoreMethods.MigrateCloudAccuracyTier(value);

    public static string? CloudSttProvider(string tierId) =>
        HyperwhisperCoreMethods.CloudSttProvider(tierId);

    public static string? CloudSttDefaultModel(string tierId) =>
        HyperwhisperCoreMethods.CloudSttDefaultModelId(tierId);

    public static bool CloudSttContainsModel(string tierId, string modelId) =>
        HyperwhisperCoreMethods.CloudSttModels(tierId)
            .Any(model => string.Equals(model.id, modelId, StringComparison.Ordinal));

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
