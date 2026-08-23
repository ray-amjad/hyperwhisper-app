namespace HyperWhisper.LocalPostProcessing;

public sealed class LocalPostProcessingService(ILocalLlmEngine engine) : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly ILocalLlmEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async ValueTask<LocalPostProcessingResult> ProcessAsync(
        LocalPostProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Transcript))
        {
            return LocalPostProcessingResult.Failed(
                request.Transcript,
                LocalPostProcessingErrorCode.InvalidRequest,
                "The transcript is empty.");
        }
        if (string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            return LocalPostProcessingResult.Failed(
                request.Transcript,
                LocalPostProcessingErrorCode.InvalidRequest,
                "The system prompt is empty.");
        }
        if (!File.Exists(request.ModelPath))
        {
            return LocalPostProcessingResult.Failed(
                request.Transcript,
                LocalPostProcessingErrorCode.ModelUnavailable,
                "The local LLM model file does not exist.");
        }

        var generation = await _engine.GenerateAsync(
            new LocalLlmGenerationRequest(
                request.ModelPath,
                request.Backend,
                request.SystemPrompt,
                LocalPostProcessingProtocol.BuildUserMessage(
                    request.DynamicSystemInfo, request.Transcript),
                request.AllowCpuFallback,
                request.Timeout ?? DefaultTimeout,
                Math.Clamp(request.MaxOutputTokens ?? LLamaSharpLocalLlmEngine.MaxTokens,
                    1, LLamaSharpLocalLlmEngine.MaxTokens)),
            cancellationToken).ConfigureAwait(false);
        if (!generation.IsSuccess)
        {
            return LocalPostProcessingResult.Failed(
                request.Transcript,
                generation.Failure!.Code,
                generation.Failure.Message,
                generation.Runtime);
        }

        if (!LocalPostProcessingProtocol.TryEvaluateCompletion(
                generation.Completion!, out var cleaned))
        {
            return LocalPostProcessingResult.Failed(
                request.Transcript,
                LocalPostProcessingErrorCode.RejectedResponse,
                "The local LLM response was empty or leaked prompt scaffolding.",
                generation.Runtime);
        }

        return LocalPostProcessingResult.Applied(cleaned, generation.Runtime!);
    }

    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
