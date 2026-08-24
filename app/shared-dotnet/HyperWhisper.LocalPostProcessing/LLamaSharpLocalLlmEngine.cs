using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace HyperWhisper.LocalPostProcessing;

/// <summary>
/// LLamaSharp 0.27 host with a warm weight set and a fresh context for every
/// transcript. LLamaSharp's native backend is process-wide; the first request
/// pins CPU, Vulkan, or CUDA for the lifetime of the process.
/// </summary>
public sealed class LLamaSharpLocalLlmEngine : ILocalLlmEngine
{
    public const int ContextSize = 16_384;
    public const int MaxTokens = 8_192;
    public const int ChatTemplateTokenReserve = 512;

    private static readonly object NativeBackendLock = new();
    private static LocalLlmBackend? _processBackend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<LocalLlmBackend, bool> _runtimeAvailable;
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private string? _activeModelPath;
    private string? _runtime;
    private bool _disposed;

    public LLamaSharpLocalLlmEngine(Func<LocalLlmBackend, bool>? runtimeAvailable = null)
    {
        _runtimeAvailable = runtimeAvailable ?? (backend => PackagedLlamaRuntime.IsAvailable(backend));
    }

    /// <remarks>
    /// Cancellation is cooperative during queued work and token generation.
    /// LLamaSharp's in-process native weight loader has no abort callback, so a
    /// cancellation or timeout that arrives after weight loading begins is
    /// observed only when that native call returns. Callers must not treat the
    /// request timeout as a hard upper bound for first model load.
    /// </remarks>
    public async ValueTask<LocalLlmGenerationResult> GenerateAsync(
        LocalLlmGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.ModelPath))
        {
            return LocalLlmGenerationResult.Failed(
                LocalPostProcessingErrorCode.ModelUnavailable,
                "The local LLM model file does not exist.");
        }
        if (request.Timeout <= TimeSpan.Zero)
        {
            return LocalLlmGenerationResult.Failed(
                LocalPostProcessingErrorCode.InvalidRequest,
                "The local LLM timeout must be positive.");
        }
        if (!_runtimeAvailable(request.Backend))
        {
            return LocalLlmGenerationResult.Failed(
                LocalPostProcessingErrorCode.RuntimeUnavailable,
                $"The requested {request.Backend} local LLM runtime is unavailable.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CancellationFailure(cancellationToken, null);
        }

        try
        {
            var chosenBackend = ConfigureNativeBackendOnce(request.Backend, request.AllowCpuFallback);
            if (chosenBackend != request.Backend)
            {
                return LocalLlmGenerationResult.Failed(
                    LocalPostProcessingErrorCode.RuntimeUnavailable,
                    $"The process already selected the {chosenBackend} local LLM runtime.",
                    _runtime);
            }

            var load = await EnsureModelLoadedAsync(request, timeout.Token).ConfigureAwait(false);
            if (load is not null)
            {
                return load;
            }

            try
            {
                EnsurePromptFits(
                    _weights!.Tokenize(request.SystemPrompt, true, true, Encoding.UTF8).Length,
                    _weights.Tokenize(request.UserMessage, false, true, Encoding.UTF8).Length);

                using var context = _weights.CreateContext(_parameters!);
                var history = new ChatHistory();
                history.AddMessage(AuthorRole.System, request.SystemPrompt);
                var session = new ChatSession(new InteractiveExecutor(context), history);
                var inference = new InferenceParams
                {
                    MaxTokens = Math.Clamp(request.MaxOutputTokens, 1, MaxTokens),
                    AntiPrompts = ["User:", "\nUser:", "<|end|>", "<|eot_id|>", "</s>"],
                    SamplingPipeline = new DefaultSamplingPipeline(),
                };
                var result = new StringBuilder();
                await foreach (var token in session.ChatAsync(
                        new ChatHistory.Message(AuthorRole.User, request.UserMessage), inference)
                    .WithCancellation(timeout.Token).ConfigureAwait(false))
                {
                    result.Append(token);
                }
                return LocalLlmGenerationResult.Success(result.ToString(), _runtime!);
            }
            catch (OperationCanceledException)
            {
                return CancellationFailure(cancellationToken, _runtime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return LocalLlmGenerationResult.Failed(
                    LocalPostProcessingErrorCode.PromptTooLarge,
                    "The local LLM prompt does not fit in the model context.",
                    _runtime);
            }
            catch (Exception)
            {
                return LocalLlmGenerationResult.Failed(
                    LocalPostProcessingErrorCode.InferenceFailed,
                    "Local LLM inference failed.",
                    _runtime);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    public static void EnsurePromptFits(int systemTokens, int userTokens)
    {
        if (systemTokens < 0 || userTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(systemTokens));
        }
        var promptBudget = ContextSize - MaxTokens - ChatTemplateTokenReserve;
        if (checked(systemTokens + userTokens) > promptBudget)
        {
            throw new ArgumentOutOfRangeException(
                nameof(userTokens),
                $"The prompt exceeds the {promptBudget}-token input budget.");
        }
    }

    private async ValueTask<LocalLlmGenerationResult?> EnsureModelLoadedAsync(
        LocalLlmGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var modelPath = Path.GetFullPath(request.ModelPath);
        if (_weights is not null
            && string.Equals(_activeModelPath, modelPath, StringComparison.Ordinal))
        {
            return null;
        }

        Unload();
        var requestedGpuLayers = request.Backend == LocalLlmBackend.Cpu ? 0 : 99;
        var parameters = CreateParameters(modelPath, requestedGpuLayers);
        try
        {
            _weights = await Task.Run(
                () => LLamaWeights.LoadFromFile(parameters), cancellationToken).ConfigureAwait(false);
            if (requestedGpuLayers > 0 && !NativeApi.llama_supports_gpu_offload())
            {
                Unload();
                if (!request.AllowCpuFallback)
                {
                    return LocalLlmGenerationResult.Failed(
                        LocalPostProcessingErrorCode.RuntimeUnavailable,
                        $"The {request.Backend} runtime loaded but no GPU offload device is available.");
                }

                parameters = CreateParameters(modelPath, 0);
                _weights = await Task.Run(
                    () => LLamaWeights.LoadFromFile(parameters), cancellationToken).ConfigureAwait(false);
                _parameters = parameters;
                _activeModelPath = modelPath;
                _runtime = $"LLamaSharp/{request.Backend.ToString().ToLowerInvariant()}-cpu-fallback";
                return null;
            }
            _parameters = parameters;
            _activeModelPath = modelPath;
            _runtime = $"LLamaSharp/{request.Backend.ToString().ToLowerInvariant()}";
            return null;
        }
        catch (OperationCanceledException)
        {
            return CancellationFailure(cancellationToken, _runtime);
        }
        catch (Exception) when (requestedGpuLayers > 0 && request.AllowCpuFallback)
        {
            Unload();
            parameters = CreateParameters(modelPath, 0);
            try
            {
                _weights = await Task.Run(
                    () => LLamaWeights.LoadFromFile(parameters), cancellationToken).ConfigureAwait(false);
                _parameters = parameters;
                _activeModelPath = modelPath;
                // This is zero-layer execution on the already-selected GPU build,
                // not a second process-wide native-library selection. The GPU
                // directories ship libggml-cpu for this exact fallback path.
                _runtime = $"LLamaSharp/{request.Backend.ToString().ToLowerInvariant()}-cpu-fallback";
                return null;
            }
            catch (OperationCanceledException)
            {
                return CancellationFailure(cancellationToken, _runtime);
            }
            catch (Exception)
            {
                Unload();
                return LocalLlmGenerationResult.Failed(
                    LocalPostProcessingErrorCode.ModelLoadFailed,
                    "The local LLM model could not be loaded.");
            }
        }
        catch (Exception)
        {
            Unload();
            return LocalLlmGenerationResult.Failed(
                LocalPostProcessingErrorCode.ModelLoadFailed,
                "The local LLM model could not be loaded.");
        }
    }

    private static LocalLlmBackend ConfigureNativeBackendOnce(
        LocalLlmBackend backend,
        bool allowCpuFallback)
    {
        lock (NativeBackendLock)
        {
            if (_processBackend is { } selected)
            {
                return selected;
            }
            NativeLibraryConfig.All
                .WithCuda(backend == LocalLlmBackend.Cuda)
                .WithVulkan(backend == LocalLlmBackend.Vulkan)
                .WithAutoFallback(allowCpuFallback);
            _processBackend = backend;
            return backend;
        }
    }

    private static ModelParams CreateParameters(string modelPath, int gpuLayers) =>
        new(modelPath)
        {
            ContextSize = ContextSize,
            GpuLayerCount = gpuLayers,
        };

    private static LocalLlmGenerationResult CancellationFailure(
        CancellationToken callerToken,
        string? runtime) =>
        callerToken.IsCancellationRequested
            ? LocalLlmGenerationResult.Failed(
                LocalPostProcessingErrorCode.Cancelled,
                "Local LLM inference was cancelled.", runtime)
            : LocalLlmGenerationResult.Failed(
                LocalPostProcessingErrorCode.TimedOut,
                "Local LLM inference timed out.", runtime);

    private void Unload()
    {
        _weights?.Dispose();
        _weights = null;
        _parameters = null;
        _activeModelPath = null;
        _runtime = null;
    }
}
