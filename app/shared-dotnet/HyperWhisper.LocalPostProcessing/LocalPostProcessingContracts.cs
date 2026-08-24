namespace HyperWhisper.LocalPostProcessing;

public enum LocalLlmBackend
{
    Cpu,
    Vulkan,
    Cuda,
}

public enum LocalPostProcessingErrorCode
{
    InvalidRequest,
    ModelUnavailable,
    ModelInvalid,
    RuntimeUnavailable,
    ModelLoadFailed,
    ModelDownloadFailed,
    PromptTooLarge,
    InferenceFailed,
    RejectedResponse,
    Cancelled,
    TimedOut,
}

public sealed record LocalPostProcessingFailure(
    LocalPostProcessingErrorCode Code,
    string Message);

public sealed record LocalPostProcessingResult(
    string Text,
    bool WasApplied,
    string? Runtime,
    LocalPostProcessingFailure? Failure)
{
    public bool IsSuccess => WasApplied && Failure is null;

    public static LocalPostProcessingResult Applied(string text, string runtime) =>
        new(text, true, runtime, null);

    public static LocalPostProcessingResult Failed(
        string original,
        LocalPostProcessingErrorCode code,
        string message,
        string? runtime = null) =>
        new(original, false, runtime, new LocalPostProcessingFailure(code, message));
}

public sealed record LocalPostProcessingRequest(
    string ModelPath,
    LocalLlmBackend Backend,
    string SystemPrompt,
    string DynamicSystemInfo,
    string Transcript,
    bool AllowCpuFallback = true,
    TimeSpan? Timeout = null,
    int? MaxOutputTokens = null);

public sealed record LocalLlmGenerationRequest(
    string ModelPath,
    LocalLlmBackend Backend,
    string SystemPrompt,
    string UserMessage,
    bool AllowCpuFallback,
    TimeSpan Timeout,
    int MaxOutputTokens = 8_192);

public sealed record LocalLlmGenerationResult(
    string? Completion,
    string? Runtime,
    LocalPostProcessingFailure? Failure)
{
    public bool IsSuccess => Completion is not null && Failure is null;

    public static LocalLlmGenerationResult Success(string completion, string runtime) =>
        new(completion, runtime, null);

    public static LocalLlmGenerationResult Failed(
        LocalPostProcessingErrorCode code,
        string message,
        string? runtime = null) =>
        new(null, runtime, new LocalPostProcessingFailure(code, message));
}

public interface ILocalLlmEngine : IAsyncDisposable
{
    ValueTask<LocalLlmGenerationResult> GenerateAsync(
        LocalLlmGenerationRequest request,
        CancellationToken cancellationToken = default);
}
