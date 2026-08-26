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
/// What a live streaming provider's error frame means for the reconnect path.
/// Mirrors the core's <c>HwLiveErrorOutcome</c> (issue #281).
/// </summary>
public enum PortableLiveErrorOutcome
{
    /// <summary>
    /// Reconnecting cannot help — the account, key, quota or permission is the
    /// problem. Mark the provider's follow-up close as expected and surface the
    /// message as it stands.
    /// </summary>
    Terminal,

    /// <summary>The failure may clear on its own; keep the reconnect path.</summary>
    Transient,
}

/// <summary>
/// Why a server refused a WebSocket upgrade outright, when the refusal is one
/// the user has to act on. Mirrors the core's <c>HwLiveUpgradeRefusal</c>.
/// </summary>
public enum PortableLiveUpgradeRefusal
{
    /// <summary>HTTP 402 — no balance to open a session with.</summary>
    InsufficientCredits,

    /// <summary>HTTP 401 / 403 — the key is missing, wrong, revoked or not permitted.</summary>
    Unauthorized,
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

    public static string? CloudSttDefaultModel(string tierId) =>
        HyperwhisperCoreMethods.CloudSttDefaultModelId(tierId);

    public static bool CloudSttContainsModel(string tierId, string modelId) =>
        HyperwhisperCoreMethods.CloudSttModels(tierId)
            .Any(model => string.Equals(model.id, modelId, StringComparison.Ordinal));

    // -----------------------------------------------------------------------
    // Live streaming (issue #281)
    //
    // Seven session-free functions the core owns for all three heads. The
    // terminal-error policy behind the first two shipped on macOS only, so
    // Windows and Linux gain it here: a mid-session "Credit balance exhausted"
    // from the default provider stops driving a reconnect that can only fail
    // the same way.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Classifies the message payload of a streaming provider's error frame.
    /// Unrecognised wording — including an empty message — is
    /// <see cref="PortableLiveErrorOutcome.Transient"/>, so a payload nobody has
    /// seen yet keeps today's reconnect behaviour.
    /// </summary>
    public static PortableLiveErrorOutcome ClassifyLiveErrorMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return HyperwhisperCoreMethods.LiveClassifyErrorMessage(message) switch
        {
            HwLiveErrorOutcome.Terminal => PortableLiveErrorOutcome.Terminal,
            _ => PortableLiveErrorOutcome.Transient,
        };
    }

    /// <summary>
    /// Classifies the HTTP status of a WebSocket upgrade that never reached
    /// 101. <c>null</c> means the ordinary reconnect path still applies — 429,
    /// 5xx and a proxy mangling the upgrade all keep it.
    /// </summary>
    /// <param name="status">
    /// The status carried by the response that came back instead of a
    /// <c>101 Switching Protocols</c>. Takes an <see cref="int"/> because that
    /// is what the .NET WebSocket stacks hand over; a value outside
    /// <see cref="ushort"/> cannot be an HTTP status and takes the same "no
    /// refusal" answer every other unrecognised status gets.
    /// </param>
    public static PortableLiveUpgradeRefusal? LiveUpgradeRefusal(int status)
    {
        if (status is < ushort.MinValue or > ushort.MaxValue)
        {
            return null;
        }

        return HyperwhisperCoreMethods.LiveUpgradeRefusal((ushort)status) switch
        {
            HwLiveUpgradeRefusal.InsufficientCredits => PortableLiveUpgradeRefusal.InsufficientCredits,
            HwLiveUpgradeRefusal.Unauthorized => PortableLiveUpgradeRefusal.Unauthorized,
            _ => null,
        };
    }

    /// <summary>
    /// Whether a WebSocket close code is one of the RFC 6455 §7.4.1
    /// non-recoverable codes (1002, 1003, 1007, 1008, 1009, 1011). A provider
    /// that signals an unrecoverable session with a private close code combines
    /// it with this answer rather than replacing it.
    /// </summary>
    public static bool IsTerminalLiveCloseCode(int closeCode) =>
        closeCode is >= ushort.MinValue and <= ushort.MaxValue
        && HyperwhisperCoreMethods.LiveIsTerminalCloseCode((ushort)closeCode);

    /// <summary>
    /// Normalizes a language selection to the primary subtag a provider wants.
    /// <c>null</c> means "omit the language parameter entirely" and covers no
    /// selection, a blank string and the app's <c>"auto"</c> sentinel alike.
    /// </summary>
    public static string? NormalizeLiveLanguage(string? code) =>
        HyperwhisperCoreMethods.LiveNormalizeLanguage(code);

    /// <summary>
    /// The PCM sample rate, in hertz, the provider's socket expects. The
    /// capture graph is configured from this before a session opens.
    /// </summary>
    public static int LiveRequiredSampleRate(LiveTranscriptionProvider provider) =>
        (int)HyperwhisperCoreMethods.LiveRequiredSampleRate(CoreLiveProvider(provider));

    /// <summary>
    /// Whether the provider's live API takes a custom-vocabulary parameter at
    /// all. <c>false</c> means the terms are dropped before the socket opens.
    /// </summary>
    public static bool LiveSupportsVocabulary(LiveTranscriptionProvider provider) =>
        HyperwhisperCoreMethods.LiveSupportsVocabulary(CoreLiveProvider(provider));

    /// <summary>
    /// The human-readable provider label stored on a history entry. The
    /// " (Streaming)" suffix is what distinguishes a live session from the same
    /// vendor's batch transcription.
    /// </summary>
    public static string LiveProviderLabel(LiveTranscriptionProvider provider) =>
        HyperwhisperCoreMethods.LiveProviderLabel(CoreLiveProvider(provider));

    /// <summary>
    /// Maps the .NET live-provider enum onto the core's. The two local engines
    /// have no arm on purpose: Parakeet and Nemotron are not WebSocket
    /// protocols and share none of this, which is the same line
    /// <c>LiveTranscriptionProtocolFactory.Create</c> draws.
    /// </summary>
    private static HwLiveProvider CoreLiveProvider(LiveTranscriptionProvider provider) => provider switch
    {
        LiveTranscriptionProvider.Deepgram => HwLiveProvider.Deepgram,
        LiveTranscriptionProvider.ElevenLabs => HwLiveProvider.ElevenLabs,
        LiveTranscriptionProvider.OpenAi => HwLiveProvider.OpenAi,
        LiveTranscriptionProvider.Grok => HwLiveProvider.Grok,
        LiveTranscriptionProvider.HyperWhisperCloud => HwLiveProvider.HyperWhisperCloud,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "not a WebSocket streaming provider"),
    };

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
