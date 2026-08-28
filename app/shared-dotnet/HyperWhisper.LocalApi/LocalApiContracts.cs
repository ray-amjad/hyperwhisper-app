using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.LocalApi;

/// <summary>
/// The closed set of Local API error codes (issue #289).
///
/// The macOS decoder is a Swift `Codable` enum, so a code outside this set does
/// not merely read as "unknown" — it makes the WHOLE envelope undecodable, and
/// the caller sees a parse failure instead of the message. This head used to
/// emit four codes that were never in the set: `PAYLOAD_TOO_LARGE`,
/// `CANCELLED`, `UNAUTHORIZED` and `RECORDING_NOT_FOUND`. They are now mapped
/// onto the codes the other two platforms already use for the same outcome.
///
/// `hw-localapi` owns the list; `LocalApiErrorCodes` is checked against
/// `LocalApiAllErrorCodes()` by the portable test suite, so a code added on one
/// side and not the other fails the build's tests rather than a client's parse.
/// </summary>
public static class LocalApiErrorCodes
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

    /// <summary>
    /// NOT part of the closed set, and deliberately never emitted. It is kept
    /// declared so that a reader who goes looking for it finds this note rather
    /// than adding it back: a response carrying this code cannot be decoded by
    /// a macOS or MCP client at all. Use <see cref="TranscriptionFailed"/> for
    /// an unexpected server-side failure.
    /// </summary>
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>The 14 codes a client may receive, in the shared order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ModelNotInstalled, ModelNotFound, EngineUnavailable, MissingApiKey,
        FileNotFound, FileAccessDenied, FileNotAllowed, AudioDecodeFailed,
        TranscriptionFailed, ModeNotFound, ModeNameTaken, InvalidRequest,
        RateLimited, Timeout,
    ];
}

public sealed record LocalApiError(string Code, string Message, string? Hint = null);
public sealed record LocalApiFailure([property: JsonPropertyName("error")] LocalApiError Error)
{
    [JsonPropertyName("ok")] public bool Ok => false;
}

public sealed record HealthSnapshot(
    [property: JsonPropertyName("app_version")] string AppVersion,
    [property: JsonPropertyName("providers")] IReadOnlyList<ProviderStatus> Providers,
    [property: JsonPropertyName("post_processing_providers")] IReadOnlyList<ProviderStatus> PostProcessingProviders,
    [property: JsonPropertyName("local_models")] object LocalModels);
public sealed record ProviderStatus(string Id, [property: JsonPropertyName("key_present")] bool KeyPresent, bool Reachable, string Status);
public sealed record ModelEntry(string Id, string Kind, string Provider, string DisplayName, bool Installed, [property: JsonPropertyName("size_mb")] double? SizeMb = null);
public sealed record RecordingEntry(
    string Id,
    string Text,
    DateTime Date,
    double Duration,
    string? Mode,
    string Status,
    string? PostProcessedText = null,
    string? TranscribedText = null,
    string? TranscriptionProvider = null,
    string? PostProcessingProvider = null,
    string? AudioFilePath = null);
public sealed record RecordingQuery(string? Search, DateTime? Since, DateTime? Until, int Limit);
public sealed record RecordingState(bool IsRecording, string State);
public sealed record LocalApiApplicationContext(
    string? ProcessName,
    string? WindowTitle,
    string? Category,
    string? BrowserTabTitle,
    string? BrowserHost,
    string? FocusedElementType,
    string? FocusedContent,
    string? TextFormat,
    string? AppType,
    string? AppTypeConfidence,
    string? AppTypeSource,
    [property: JsonPropertyName("screenOCRText")] string? ScreenOcrText)
{
    public ApplicationContextSnapshot ToSnapshot() => new()
    {
        ProcessName = Bound(ProcessName, 512),
        WindowTitle = Bound(WindowTitle, 2_000),
        Category = Bound(Category, 256),
        BrowserTabTitle = NullIfEmpty(Bound(BrowserTabTitle, 2_000)),
        BrowserHost = NullIfEmpty(Bound(BrowserHost, 512)),
        FocusedElementType = NullIfEmpty(Bound(FocusedElementType, 256)),
        FocusedContent = NullIfEmpty(Bound(FocusedContent, 4_000)),
        TextFormat = Bound(TextFormat, 128),
        AppType = Bound(AppType, 128, "other"),
        AppTypeConfidence = Bound(AppTypeConfidence, 32, "unknown"),
        AppTypeSource = Bound(AppTypeSource, 64, "localApi"),
        ScreenOcrText = NullIfEmpty(Bound(ScreenOcrText, 4_000)),
    };

    private static string Bound(string? value, int maximum, string fallback = "")
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? fallback
            : normalized.Length <= maximum ? normalized : normalized[..maximum];
    }
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

public sealed record AudioUpload(
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Content,
    string? ModeId,
    string? Engine,
    string? Model,
    string? Language,
    LocalApiApplicationContext? ApplicationContext = null,
    IReadOnlyList<string>? TimestampGranularities = null);
public sealed record TranscriptionResult(
    string Text,
    string Engine,
    string Model,
    string? Language,
    int LoadMs,
    int DecodeMs,
    int LatencyMs,
    string? RawText = null,
    IReadOnlyList<TranscriptionSegmentTimestamp>? Segments = null,
    IReadOnlyList<TranscriptionWordTimestamp>? Words = null);
public sealed record PostProcessRequest(
    string Text,
    [property: JsonPropertyName("mode_id")] string? ModeId,
    string? Preset,
    string? Prompt,
    string? Provider,
    string? Model,
    LocalApiApplicationContext? ApplicationContext = null);
public sealed record PostProcessResult(string Text, string Provider, string Model, string Preset, int LatencyMs);

public interface ILocalApiBackend
{
    ValueTask<HealthSnapshot> GetHealthAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ModelEntry>> GetModelsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<JsonElement>> GetModesAsync(CancellationToken cancellationToken);
    ValueTask<JsonElement?> GetModeAsync(string id, CancellationToken cancellationToken);
    ValueTask<JsonElement> CreateModeAsync(JsonElement mode, CancellationToken cancellationToken);
    ValueTask<JsonElement?> PatchModeAsync(string id, JsonElement patch, CancellationToken cancellationToken);
    ValueTask<bool> DeleteModeAsync(string id, CancellationToken cancellationToken);
    ValueTask<RecordingState> ToggleRecordingAsync(CancellationToken cancellationToken);
    ValueTask<RecordingState> CancelRecordingAsync(CancellationToken cancellationToken);
    ValueTask<TranscriptionResult> TranscribeAsync(AudioUpload upload, CancellationToken cancellationToken);
    ValueTask<PostProcessResult> PostProcessAsync(PostProcessRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<RecordingEntry>> GetRecordingsAsync(RecordingQuery query, CancellationToken cancellationToken);
    ValueTask<RecordingEntry?> GetRecordingAsync(string id, CancellationToken cancellationToken);
}
