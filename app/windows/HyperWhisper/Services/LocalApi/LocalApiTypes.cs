using System.Text.Json.Serialization;
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

internal sealed class ApiError
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("hint")] public string? Hint { get; init; }
}

/// <summary>
/// `{ok:false, error:{...}}` envelope returned by failures.
/// </summary>
internal sealed class ApiFailureEnvelope
{
    [JsonPropertyName("ok")] public bool Ok => false;
    [JsonPropertyName("error")] public ApiError Error { get; init; } = new();
}

// MARK: - /health -----------------------------------------------------------

internal sealed class HealthProviderStatus
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key_present")] public bool KeyPresent { get; init; }
    [JsonPropertyName("reachable")] public bool Reachable { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "unknown";
}

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

internal sealed class HealthResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("app_version")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("api_version")] public int ApiVersion { get; init; } = LocalApiVersion.Current;
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("providers")] public List<HealthProviderStatus> Providers { get; init; } = new();
    [JsonPropertyName("post_processing_providers")] public List<HealthProviderStatus> PostProcessingProviders { get; init; } = new();
    [JsonPropertyName("local_models")] public HealthLocalModels LocalModels { get; init; } = new();
}

// MARK: - /models -----------------------------------------------------------

internal sealed class ModelEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = ""; // "voice" | "text"
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("installed")] public bool Installed { get; init; }
    [JsonPropertyName("size_mb")] public double? SizeMb { get; init; }
}

internal sealed class ModelsListResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("models")] public List<ModelEntry> Models { get; init; } = new();
}

// MARK: - /modes -----------------------------------------------------------

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
    [JsonPropertyName("customVocabulary")] public List<string>? CustomVocabulary { get; set; }
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
    [JsonPropertyName("customVocabulary")] public List<string>? CustomVocabulary { get; set; }
    [JsonPropertyName("providerType")] public string? ProviderType { get; set; }
}

internal sealed class ModesListResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("modes")] public List<ModeDto> Modes { get; init; } = new();
}

internal sealed class ModeResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("mode")] public ModeDto Mode { get; init; } = new();
}

internal sealed class OkResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
}

// MARK: - /transcribe -------------------------------------------------------

internal sealed class TranscribeRequest
{
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("audio_base64")] public string? AudioBase64 { get; set; }
    [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
    [JsonPropertyName("mode_id")] public string? ModeId { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("applicationContext")] public LocalApiApplicationContext? ApplicationContext { get; set; }
}

internal sealed class TranscribeTimings
{
    [JsonPropertyName("load_ms")] public int LoadMs { get; init; }
    [JsonPropertyName("decode_ms")] public int DecodeMs { get; init; }
}

internal sealed class TranscribeResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("engine")] public string Engine { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("timings")] public TranscribeTimings Timings { get; init; } = new();
    [JsonPropertyName("latency_ms")] public int LatencyMs { get; init; }
}

// MARK: - /post-process ----------------------------------------------------

/// <summary>
/// Wire shape for the `POST /post-process` request body. Core field names mirror
/// the macOS `PostProcessRequest`. All four content fields are
/// optional; the endpoint validates that at least one of mode_id / preset /
/// prompt is present and that preset+prompt are mutually exclusive. Windows
/// additionally accepts optional `applicationContext` for app-aware formatting.
/// </summary>
internal sealed class PostProcessRequest
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("mode_id")] public string? ModeId { get; set; }
    [JsonPropertyName("preset")] public string? Preset { get; set; }
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("applicationContext")] public LocalApiApplicationContext? ApplicationContext { get; set; }
}

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

        return AppTypeClassifier.Shared.Classify(
            TrimOrEmpty(ProcessName),
            TrimOrNull(BrowserHost),
            string.IsNullOrWhiteSpace(BrowserHost) ? "unknown" : "manual",
            TrimOrNull(WindowTitle),
            TrimOrNull(BrowserTabTitle),
            TrimOrNull(FocusedElementType),
            TrimOrNull(FocusedContent));
    }

    private static bool TryParseAppType(string? value, out HyperWhisper.Services.AppClassification.AppType appType)
    {
        appType = HyperWhisper.Services.AppClassification.AppType.Other;
        var normalized = value?.Trim()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return false;

        foreach (var candidate in Enum.GetValues<HyperWhisper.Services.AppClassification.AppType>())
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

/// <summary>
/// Success body for `POST /post-process`. `provider`, `model`, `preset` are
/// projected from the resolved working Mode so callers see the labels that
/// actually drove the dispatch.
/// </summary>
internal sealed class PostProcessResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("preset")] public string Preset { get; init; } = "";
    [JsonPropertyName("latency_ms")] public int LatencyMs { get; init; }
}

// MARK: - /recordings ------------------------------------------------------

/// <summary>
/// Wire projection of the <c>Transcript</c> EF entity. Keys match the macOS
/// `RecordingDto` 1:1. Windows-only entity columns (FailedReason, RetryCount,
/// LastRetryDate, TrimmedAudioFilePath, ModeId, RecordingSessionId) are
/// deliberately omitted to keep the wire shape narrow and identical across
/// platforms.
/// </summary>
internal sealed class RecordingDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("postProcessedText")] public string? PostProcessedText { get; init; }
    [JsonPropertyName("transcribedText")] public string? TranscribedText { get; init; }
    [JsonPropertyName("date")] public DateTime Date { get; init; }
    [JsonPropertyName("duration")] public double Duration { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("transcriptionProvider")] public string? TranscriptionProvider { get; init; }
    [JsonPropertyName("postProcessingProvider")] public string? PostProcessingProvider { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("audioFilePath")] public string? AudioFilePath { get; init; }
}

internal sealed class RecordingsListResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("returned")] public int Returned { get; init; }
    [JsonPropertyName("recordings")] public List<RecordingDto> Recordings { get; init; } = new();
}

internal sealed class RecordingResponse
{
    [JsonPropertyName("ok")] public bool Ok => true;
    [JsonPropertyName("recording")] public RecordingDto Recording { get; init; } = new();
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
