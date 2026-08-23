using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperWhisper.LocalApi;

public static class LocalApiErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string FileNotAllowed = "FILE_NOT_ALLOWED";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string ModeNotFound = "MODE_NOT_FOUND";
    public const string Cancelled = "CANCELLED";
    public const string InternalError = "INTERNAL_ERROR";
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
public sealed record AudioUpload(string FileName, string ContentType, ReadOnlyMemory<byte> Content, string? ModeId, string? Engine, string? Model, string? Language);
public sealed record TranscriptionResult(string Text, string Engine, string Model, string? Language, int LoadMs, int DecodeMs, int LatencyMs);
public sealed record PostProcessRequest(string Text, [property: JsonPropertyName("mode_id")] string? ModeId, string? Preset, string? Prompt, string? Provider, string? Model);
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
