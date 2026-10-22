using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.AppClassification;
using HyperWhisper.Services.AppClassification;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// Codable request/response shapes for the in-app Local HTTP API
/// (Settings → Local API). Field shapes match macOS exactly so cross-platform
/// MCP/cURL snippets work against either build.
/// Additive Windows-only request fields are called out on the relevant DTOs.
/// </summary>
internal static class LocalApiVersion
{
    public const int Current = 1;
}

/// <summary>
/// Closed set of machine-readable error codes. Adding a new code is a contract
/// change — pick from this list when possible.
/// </summary>
internal static class LocalApiErrorCode
{
    public const string ModelNotInstalled = "MODEL_NOT_INSTALLED";
    public const string ModelNotFound = "MODEL_NOT_FOUND";
    public const string EngineUnavailable = "ENGINE_UNAVAILABLE";
    public const string MissingApiKey = "MISSING_API_KEY";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileAccessDenied = "FILE_ACCESS_DENIED";
    public const string FileNotAllowed = "FILE_NOT_ALLOWED";
    public const string AudioDecodeFailed = "AUDIO_DECODE_FAILED";
    public const string TranscriptionFailed = "TRANSCRIPTION_FAILED";
    public const string ModeNotFound = "MODE_NOT_FOUND";
    public const string ModeNameTaken = "MODE_NAME_TAKEN";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string RateLimited = "RATE_LIMITED";
    public const string Timeout = "TIMEOUT";
}

// MARK: - /health -----------------------------------------------------------

internal sealed class HealthLocalModelEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("installed")] public bool Installed { get; init; }
}

internal sealed class HealthLocalModels
{
    [JsonPropertyName("whisper")] public List<HealthLocalModelEntry> Whisper { get; init; } = new();
    [JsonPropertyName("parakeet")] public List<HealthLocalModelEntry> Parakeet { get; init; } = new();
    [JsonPropertyName("qwen3_asr")] public List<HealthLocalModelEntry> Qwen3Asr { get; init; } = new();
    [JsonPropertyName("apple_speech")] public List<HealthLocalModelEntry> AppleSpeech { get; init; } = new();
    [JsonPropertyName("local_llm")] public List<HealthLocalModelEntry> LocalLlm { get; init; } = new();
}

// MARK: - /modes -----------------------------------------------------------

/// <summary>
/// A JSON array of strings in which <c>null</c> is not a string.
/// </summary>
/// <remarks>
/// <para>
/// System.Text.Json erases nullable reference types unless
/// <c>RespectNullableAnnotations</c> is set, which it is nowhere in this repo,
/// so <c>["ok", null]</c> deserialises into <c>List&lt;string&gt;</c> with a
/// null element. That element is not merely odd, it is unsendable: the
/// generated <c>FfiConverterSequenceString.AllocationSize</c> sums
/// <c>Encoding.UTF8.GetByteCount(item)</c> with no per-element guard, so the
/// first null throws <see cref="ArgumentNullException"/> BEFORE Rust sees the
/// call. None of this server's middlewares wrap <c>next()</c> and the host
/// installs no <c>UseExceptionHandler</c>, so Kestrel answered a bare HTTP 500
/// with no body — on a route whose whole contract is the 200 failure envelope
/// (issue #356, review round 1).
/// </para>
/// <para>
/// The guard belongs HERE, at the parse, rather than at the three call sites
/// that hand a vocabulary to <c>hw-localapi</c>: a value that cannot cross the
/// boundary should never be built in the first place, and a fourth call site
/// added later inherits the guard instead of the crash. A null element is a
/// wrong-typed value, so it gets this head's wrong-typed answer — HTTP 400
/// <c>"Invalid JSON body"</c>, which is what <c>{"name":5}</c> already gets and
/// what macOS answers for the same body, because <c>[String]</c> refuses it in
/// <c>Codable</c>.
/// </para>
/// <para>
/// A JSON <c>null</c> for the whole property is untouched: a converter's
/// <c>HandleNull</c> is <c>false</c> for a reference type, so
/// System.Text.Json assigns null without calling <see cref="Read"/>.
/// </para>
/// </remarks>
internal sealed class NonNullStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of strings.");
        }
        var items = new List<string>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return items;
                case JsonTokenType.String:
                    items.Add(reader.GetString()!);
                    break;
                default:
                    throw new JsonException("An array of strings cannot contain null or a non-string element.");
            }
        }
        throw new JsonException("Expected an array of strings.");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        foreach (var item in value)
        {
            // Defensive on the way out too: a null that reached storage before
            // this converter existed must not take the response down.
            writer.WriteStringValue(item ?? "");
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Full Mode JSON shape — every field on the Windows Mode entity, plus
/// `useStreamingTranscription` (mapped to the Windows global Streaming setting
/// because Windows no longer stores streaming as a per-mode flag). Used for both
/// list/get responses and create/patch
/// request bodies. Keys are camelCase, matching the macOS `ModeDTO`.
/// </summary>
internal sealed class ModeDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("preset")] public string Preset { get; set; } = "hyper";
    [JsonPropertyName("language")] public string Language { get; set; } = "en";
    [JsonPropertyName("model")] public string Model { get; set; } = "base";
    [JsonPropertyName("punctuation")] public bool Punctuation { get; set; }
    [JsonPropertyName("capitalization")] public bool Capitalization { get; set; }
    [JsonPropertyName("profanityFilter")] public bool ProfanityFilter { get; set; }
    [JsonPropertyName("customInstructions")] public string? CustomInstructions { get; set; }
    [JsonPropertyName("userSystemPrompt")] public string? UserSystemPrompt { get; set; }
    [JsonPropertyName("isDefault")] public bool? IsDefault { get; set; }
    [JsonPropertyName("isSystemProvided")] public bool? IsSystemProvided { get; set; }
    [JsonPropertyName("sortOrder")] public int? SortOrder { get; set; }
    [JsonPropertyName("createdDate")] public DateTime? CreatedDate { get; set; }
    [JsonPropertyName("modifiedDate")] public DateTime? ModifiedDate { get; set; }
    [JsonPropertyName("languageModel")] public string? LanguageModel { get; set; }
    [JsonPropertyName("cloudTranscriptionModel")] public string? CloudTranscriptionModel { get; set; }
    [JsonPropertyName("cloudTranscriptionDomain")] public string? CloudTranscriptionDomain { get; set; }
    [JsonPropertyName("cloudProvider")] public string? CloudProvider { get; set; }
    [JsonPropertyName("postProcessingMode")] public int? PostProcessingMode { get; set; }
    [JsonPropertyName("postProcessingProvider")] public string? PostProcessingProvider { get; set; }
    [JsonPropertyName("englishSpelling")] public string? EnglishSpelling { get; set; }
    [JsonPropertyName("useStreamingTranscription")] public bool? UseStreamingTranscription { get; set; }
    [JsonPropertyName("cloudAccuracyTier")] public string? CloudAccuracyTier { get; set; }
    [JsonPropertyName("removeTrailingPeriod")] public bool? RemoveTrailingPeriod { get; set; }
    [JsonPropertyName("enableScreenOCR")] public bool? EnableScreenOcr { get; set; }
    [JsonPropertyName("geminiCustomPrompt")] public string? GeminiCustomPrompt { get; set; }
    [JsonPropertyName("cloudPostProcessingModel")] public string? CloudPostProcessingModel { get; set; }

    // Windows-only extras emitted on read so the GUI fields round-trip via the
    // API. Tolerated as extra keys when seen by macOS clients.
    [JsonPropertyName("localEngine")] public string? LocalEngine { get; set; }
    [JsonPropertyName("localParakeetModel")] public string? LocalParakeetModel { get; set; }
    [JsonPropertyName("localPostProcessingModel")] public string? LocalPostProcessingModel { get; set; }
    [JsonPropertyName("customVocabulary")]
    [JsonConverter(typeof(NonNullStringListConverter))]
    public List<string>? CustomVocabulary { get; set; }
    [JsonPropertyName("providerType")] public string? ProviderType { get; set; }
}

/// <summary>
/// Partial Mode body used by `PATCH /modes/{id}`. All fields optional —
/// any present key replaces the stored value; absent keys are left untouched.
/// </summary>
internal sealed class ModePatchDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("preset")] public string? Preset { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("punctuation")] public bool? Punctuation { get; set; }
    [JsonPropertyName("capitalization")] public bool? Capitalization { get; set; }
    [JsonPropertyName("profanityFilter")] public bool? ProfanityFilter { get; set; }
    [JsonPropertyName("customInstructions")] public string? CustomInstructions { get; set; }
    [JsonPropertyName("userSystemPrompt")] public string? UserSystemPrompt { get; set; }
    [JsonPropertyName("isDefault")] public bool? IsDefault { get; set; }
    [JsonPropertyName("sortOrder")] public int? SortOrder { get; set; }
    [JsonPropertyName("languageModel")] public string? LanguageModel { get; set; }
    [JsonPropertyName("cloudTranscriptionModel")] public string? CloudTranscriptionModel { get; set; }
    [JsonPropertyName("cloudTranscriptionDomain")] public string? CloudTranscriptionDomain { get; set; }
    [JsonPropertyName("cloudProvider")] public string? CloudProvider { get; set; }
    [JsonPropertyName("postProcessingMode")] public int? PostProcessingMode { get; set; }
    [JsonPropertyName("postProcessingProvider")] public string? PostProcessingProvider { get; set; }
    [JsonPropertyName("englishSpelling")] public string? EnglishSpelling { get; set; }
    [JsonPropertyName("useStreamingTranscription")] public bool? UseStreamingTranscription { get; set; }
    [JsonPropertyName("cloudAccuracyTier")] public string? CloudAccuracyTier { get; set; }
    [JsonPropertyName("removeTrailingPeriod")] public bool? RemoveTrailingPeriod { get; set; }
    [JsonPropertyName("enableScreenOCR")] public bool? EnableScreenOcr { get; set; }
    [JsonPropertyName("geminiCustomPrompt")] public string? GeminiCustomPrompt { get; set; }
    [JsonPropertyName("cloudPostProcessingModel")] public string? CloudPostProcessingModel { get; set; }
    [JsonPropertyName("localEngine")] public string? LocalEngine { get; set; }
    [JsonPropertyName("localParakeetModel")] public string? LocalParakeetModel { get; set; }
    [JsonPropertyName("localPostProcessingModel")] public string? LocalPostProcessingModel { get; set; }
    [JsonPropertyName("customVocabulary")]
    [JsonConverter(typeof(NonNullStringListConverter))]
    public List<string>? CustomVocabulary { get; set; }
    [JsonPropertyName("providerType")] public string? ProviderType { get; set; }
}

// MARK: - applicationContext ----------------------------------------------

/// <summary>
/// Optional caller-supplied app context for Local API requests. The API never
/// gathers foreground context on its own; automation clients can opt in by
/// passing this object when they want the same app-aware formatting used by GUI
/// recording. Field names are camelCase to match the public `applicationContext`
/// object documented by clients.
/// </summary>
internal sealed class LocalApiApplicationContext
{
    [JsonPropertyName("processName")] public string? ProcessName { get; set; }
    [JsonPropertyName("windowTitle")] public string? WindowTitle { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("browserTabTitle")] public string? BrowserTabTitle { get; set; }
    [JsonPropertyName("browserHost")] public string? BrowserHost { get; set; }
    [JsonPropertyName("focusedElementType")] public string? FocusedElementType { get; set; }
    [JsonPropertyName("focusedContent")] public string? FocusedContent { get; set; }
    [JsonPropertyName("textFormat")] public string? TextFormat { get; set; }
    [JsonPropertyName("appType")] public string? AppType { get; set; }
    [JsonPropertyName("appTypeConfidence")] public string? AppTypeConfidence { get; set; }
    [JsonPropertyName("appTypeSource")] public string? AppTypeSource { get; set; }
    [JsonPropertyName("screenOCRText")] public string? ScreenOcrText { get; set; }

    /// <summary>
    /// The shared server's parsed context as this head's class, so
    /// <see cref="ToApplicationContext"/> keeps one implementation. Null in,
    /// null out.
    /// </summary>
    public static LocalApiApplicationContext? From(HyperWhisper.LocalApi.LocalApiApplicationContext? shared) =>
        shared is null
            ? null
            : new LocalApiApplicationContext
            {
                ProcessName = shared.ProcessName,
                WindowTitle = shared.WindowTitle,
                Category = shared.Category,
                BrowserTabTitle = shared.BrowserTabTitle,
                BrowserHost = shared.BrowserHost,
                FocusedElementType = shared.FocusedElementType,
                FocusedContent = shared.FocusedContent,
                TextFormat = shared.TextFormat,
                AppType = shared.AppType,
                AppTypeConfidence = shared.AppTypeConfidence,
                AppTypeSource = shared.AppTypeSource,
                ScreenOcrText = shared.ScreenOcrText
            };

    public HyperWhisper.Services.ApplicationContext ToApplicationContext()
    {
        var classification = ResolveClassification();
        return new HyperWhisper.Services.ApplicationContext
        {
            ProcessName = TrimOrEmpty(ProcessName),
            WindowTitle = TrimOrEmpty(WindowTitle),
            Category = TrimOrDefault(Category, classification.AppType.ToCategory()),
            BrowserTabTitle = TrimOrNull(BrowserTabTitle),
            BrowserHost = TrimOrNull(BrowserHost),
            FocusedElementType = TrimOrNull(FocusedElementType),
            FocusedContent = TrimOrNull(FocusedContent),
            TextFormat = TrimOrDefault(TextFormat, classification.AppType.ToTextFormat()),
            AppType = classification.AppType,
            AppTypeConfidence = TrimOrDefault(AppTypeConfidence, classification.Confidence),
            AppTypeSource = TrimOrDefault(AppTypeSource, classification.Source),
            ScreenOCRText = TrimOrNull(ScreenOcrText)
        };
    }

    private AppClassificationResult ResolveClassification()
    {
        if (TryParseAppType(AppType, out var explicitType))
        {
            return new AppClassificationResult(
                explicitType,
                TrimOrDefault(AppTypeConfidence, "manual"),
                TrimOrDefault(AppTypeSource, "localApi"),
                null);
        }

        return AppTypeClassifier.Classify(new AppClassificationRequest(
            ProcessName: TrimOrEmpty(ProcessName),
            Host: TrimOrNull(BrowserHost),
            HostConfidence: string.IsNullOrWhiteSpace(BrowserHost) ? "unknown" : "manual",
            Title: string.Join(" ", new[] { TrimOrNull(BrowserTabTitle), TrimOrNull(WindowTitle) }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            FocusedPieces:
            [
                TrimOrNull(FocusedElementType) ?? string.Empty,
                TrimOrNull(FocusedContent) ?? string.Empty
            ]));
    }

    private static bool TryParseAppType(string? value, out HyperWhisper.AppClassification.AppType appType)
    {
        appType = HyperWhisper.AppClassification.AppType.Other;
        var normalized = value?.Trim()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return false;

        foreach (var candidate in Enum.GetValues<HyperWhisper.AppClassification.AppType>())
        {
            var candidateName = candidate.ToString().ToLowerInvariant();
            var promptName = candidate.ToPromptValue()
                .Replace("_", "", StringComparison.Ordinal)
                .ToLowerInvariant();
            if (normalized == candidateName || normalized == promptName)
            {
                appType = candidate;
                return true;
            }
        }
        return false;
    }

    private static string TrimOrEmpty(string? value) => value?.Trim() ?? "";
    private static string? TrimOrNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
    private static string TrimOrDefault(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? fallback : trimmed;
    }
}

// MARK: - Port file --------------------------------------------------------

/// <summary>
/// Written to %LOCALAPPDATA%\HyperWhisper\local-api.json with restricted
/// (current-user-only) NTFS ACL. Clients read this file to find the port and
/// bearer token. Matches the macOS port-file schema 1:1.
/// </summary>
internal sealed class LocalApiPortFile
{
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("started_at")] public string StartedAt { get; init; } = "";
    [JsonPropertyName("api_version")] public int ApiVersion { get; init; } = LocalApiVersion.Current;
    [JsonPropertyName("app_version")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("token")] public string Token { get; init; } = "";
}
