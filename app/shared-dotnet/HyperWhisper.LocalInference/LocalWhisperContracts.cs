namespace HyperWhisper.LocalInference;

public enum LocalWhisperBackend
{
    Cpu,
    Vulkan,
    Cuda12,
}

public sealed record LocalWhisperLoadOptions(
    string ModelPath,
    LocalWhisperBackend Backend,
    int GpuDevice = 0,
    bool AllowCpuFallback = true);

public sealed record LocalWhisperRequest(
    string AudioPath,
    string? Language = null,
    int? ThreadCount = null,
    bool SingleSegment = true);

public enum LocalWhisperErrorCode
{
    InvalidRequest,
    RuntimeUnavailable,
    ModelLoadFailed,
    TranscriptionFailed,
    Cancelled,
}

public sealed record LocalWhisperFailure(LocalWhisperErrorCode Code, string Message);

public sealed record LocalWhisperResult(
    string? Text,
    LocalWhisperFailure? Failure,
    string? Runtime)
{
    public bool IsSuccess => Text is not null && Failure is null;

    public static LocalWhisperResult Success(string text, string runtime) =>
        new(text, null, runtime);

    public static LocalWhisperResult Failed(LocalWhisperErrorCode code, string message, string? runtime = null) =>
        new(null, new LocalWhisperFailure(code, message), runtime);
}

public interface ILocalWhisperService : IAsyncDisposable
{
    bool IsLoaded { get; }
    string? Runtime { get; }
    Task<LocalWhisperResult> LoadAsync(LocalWhisperLoadOptions options, CancellationToken cancellationToken = default);
    Task<LocalWhisperResult> TranscribeAsync(LocalWhisperRequest request, CancellationToken cancellationToken = default);
}
