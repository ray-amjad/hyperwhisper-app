using System.IO;
using System.Text;
using HyperWhisper.Models;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using static HyperWhisper.Services.LocalLlmGpuLoadCrashStore;

namespace HyperWhisper.Services;

/// <summary>
/// Runs local GGUF post-processing models through LLamaSharp.
/// Model weights are loaded lazily and kept warm; each request uses a fresh context
/// so prior transcripts cannot bleed into the next post-processing job.
/// </summary>
public sealed class LocalLlmService : IDisposable
{
    internal const int ContextSize = 16_384;
    internal const int MaxTokens = 8_192;
    // Chat templates add model-specific role/control tokens that are not present when
    // the system and user strings are tokenized directly. Reserve enough room for them
    // so the full source prompt plus the full output allowance always fits in context.
    internal const int ChatTemplateTokenReserve = 512;
    private static readonly TimeSpan InferenceTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    // Cancelled by Dispose() so an in-flight LoadModel/Generate that holds _inferenceLock bails out
    // promptly instead of forcing shutdown to block on a held lock for up to InferenceTimeout.
    private readonly CancellationTokenSource _shutdownCts = new();

    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private string? _activeModelPath;
    private LocalLlmBackend _activeBackend = LocalLlmBackend.None;
    private bool _disposed;

    /// <summary>
    /// The backend the process-wide LLamaSharp native library was chosen for.
    /// LLamaSharp freezes <see cref="NativeLibraryConfig"/> on first load
    /// (every mutator throws once LibraryHasLoaded), so the first model to
    /// load decides the library for the whole process. Null until then.
    /// </summary>
    private static LocalLlmBackend? _processNativeBackend;
    private static readonly object ProcessBackendLock = new();

    public bool IsModelLoaded => _weights != null;
    public string? ActiveModelPath => _activeModelPath;
    public bool IsUsingGpu { get; private set; }

    public async Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdownCts.Token);

        await _inferenceLock.WaitAsync(loadCts.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await LoadModelInternalAsync(modelPath, loadCts.Token);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<string> GenerateAsync(
        string modelPath,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdownCts.Token);

        await _inferenceLock.WaitAsync(timeoutCts.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            await LoadModelInternalAsync(modelPath, timeoutCts.Token);

            timeoutCts.CancelAfter(InferenceTimeout);

            EnsurePromptFitsLocalContext(systemPrompt, userMessage);

            using var context = _weights!.CreateContext(_parameters!);
            var executor = new InteractiveExecutor(context);
            var chatHistory = new ChatHistory();
            chatHistory.AddMessage(AuthorRole.System, systemPrompt);

            var session = new ChatSession(executor, chatHistory);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = MaxTokens,
                AntiPrompts =
                [
                    "User:",
                    "\nUser:",
                    "<|end|>",
                    "<|eot_id|>",
                    "</s>"
                ],
                SamplingPipeline = new DefaultSamplingPipeline()
            };

            var builder = new StringBuilder();
            await foreach (var token in session.ChatAsync(
                new ChatHistory.Message(AuthorRole.User, userMessage),
                inferenceParams).WithCancellation(timeoutCts.Token))
            {
                builder.Append(token);
            }

            return builder.ToString();
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public void UnloadModel()
    {
        _weights?.Dispose();
        _weights = null;
        _parameters = null;
        _activeModelPath = null;
        _activeBackend = LocalLlmBackend.None;
        IsUsingGpu = false;
    }

    private async Task LoadModelInternalAsync(string modelPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Local LLM model file was not found.", modelPath);
        }

        if (_weights != null && string.Equals(_activeModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        UnloadModel();

        // A native GPU crash during weight upload (driver mismatch, VRAM
        // exhaustion, GGUF corruption beyond the magic-byte check) surfaces as an
        // AccessViolationException. On .NET 5+ that is a corrupted-state exception
        // and is NOT delivered to managed catch handlers, so the CPU fallback
        // below cannot run — the process simply dies. An in-flight record written
        // before the GPU load and cleared on success (or on a clean process exit,
        // see ClearInFlightGpuLoads) lets the next launch detect the prior crash —
        // a record still flagged in-flight survived a hard death — and skip that
        // backend for that model. A clean quit mid-load no longer counts as a crash.
        //
        // The crash cause is usually durable (incompatible driver/GPU, corrupt
        // GGUF), so the record must SURVIVE this recovery — clearing it here would
        // let the next launch/reload retry the crashed backend and crash again, an
        // endless crash loop that only ever recovers its first post-crash load.
        //
        // The store keeps one entry PER (model path, GPU identity, backend) so that:
        //   * Loading a different model never erases another model's crash memory
        //     (the single-sentinel design lost it on model switching, so a
        //     crash-prone model re-crashed once per selection — issue surfaced in
        //     review of #563).
        //   * A changed GPU environment (driver update, GPU swap, different
        //     adapter name) naturally invalidates the pin, because the recorded
        //     GPU identity no longer matches and the GPU path is re-probed.
        //   * Each entry expires after a bounded number of launches so a one-off
        //     non-crash process death (force-quit, OS kill, unrelated crash on the
        //     load thread) cannot pin a model to CPU permanently.
        //   * A crash pins ONLY the backend that crashed: a CUDA crash on an
        //     NVIDIA card still lets the next launch try Vulkan for that model.
        //     Entries written before the Backend field existed pin every backend
        //     (see CrashEntryPinsBackend), preserving their pre-upgrade meaning.
        var gpuIdentity = GetGpuIdentity();

        // What the hardware alone would pick vs. what this model's crash pins allow.
        var hardwarePlan = GetRuntimePlanSafely(modelPath: null, restrictToBackend: null);
        var pinnedPlan = GetRuntimePlanSafely(modelPath, restrictToBackend: null);
        if (hardwarePlan.Backend != LocalLlmBackend.None && pinnedPlan.Backend != hardwarePlan.Backend)
        {
            LoggingService.Warn(
                $"LocalLlmService: Previous {LocalLlmGpuHelper.BackendDisplayName(hardwarePlan.Backend)} load of this model crashed the process; " +
                $"continuing on {LocalLlmGpuHelper.BackendDisplayName(pinnedPlan.Backend)} backend");
            ReportRecoveredGpuLoadCrash(modelPath, hardwarePlan.Backend, pinnedPlan.Backend);
        }

        // The native library choice is PROCESS-WIDE and one-shot: the first model
        // to load picks the library (Vulkan plans pin the shipped vulkan build
        // explicitly; CUDA and CPU plans keep LLamaSharp's default selection,
        // which already works today). Any later model whose plan disagrees with
        // that frozen choice is re-planned against the loaded backend only —
        // usually degrading to GpuLayerCount 0 on the already-loaded library,
        // exactly the pre-existing forced-CPU behaviour. Its preferred backend
        // applies again from the next launch.
        var processBackend = PinProcessNativeLibraryOnce(pinnedPlan.Backend);
        var plan = processBackend == pinnedPlan.Backend
            ? pinnedPlan
            : GetRuntimePlanSafely(modelPath, processBackend);
        if (plan.Backend != pinnedPlan.Backend)
        {
            LoggingService.Info(
                $"LocalLlmService: Planned {LocalLlmGpuHelper.BackendDisplayName(pinnedPlan.Backend)} backend is unavailable this session " +
                $"(process already loaded the {LocalLlmGpuHelper.BackendDisplayName(processBackend)} library); using {LocalLlmGpuHelper.BackendDisplayName(plan.Backend)}");
        }

        var gpuLayerCount = plan.GpuLayerCount;
        var backendId = LocalLlmGpuHelper.BackendId(plan.Backend);

        try
        {
            var parameters = CreateParameters(modelPath, gpuLayerCount);
            if (gpuLayerCount > 0 && backendId != null)
            {
                WriteGpuLoadSentinel(modelPath, gpuIdentity, backendId);
            }
            _weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), cancellationToken);
            // GPU load survived: this model is safe on this backend + GPU, so drop
            // its crash record for THIS backend only. A crash record for a
            // different backend (e.g. the CUDA pin that routed us to Vulkan) must
            // persist — the scoped clear is what preserves it, replacing the old
            // forcedCpuAfterCrash bookkeeping. On a CPU load nothing was written
            // and nothing is cleared, so pre-existing pins survive untouched.
            if (gpuLayerCount > 0 && backendId != null)
            {
                ClearGpuLoadSentinel(modelPath, backendId);
            }
            _parameters = parameters;
            _activeModelPath = modelPath;
            IsUsingGpu = gpuLayerCount > 0;
            _activeBackend = gpuLayerCount > 0 ? plan.Backend : LocalLlmBackend.None;
            LoggingService.Info(BuildRuntimeStatusMessage(modelPath));
        }
        catch (OperationCanceledException)
        {
            // A managed exception means the process survived the load, so the
            // in-flight record written above would be a false positive on the next
            // launch. Scoped to the attempted backend; records for other backends
            // (pre-existing pins) must persist and are untouched.
            if (gpuLayerCount > 0 && backendId != null)
            {
                ClearGpuLoadSentinel(modelPath, backendId);
            }
            throw;
        }
        catch when (gpuLayerCount > 0)
        {
            if (backendId != null)
            {
                ClearGpuLoadSentinel(modelPath, backendId);
            }
            LoggingService.Warn(
                $"LocalLlmService: {LocalLlmGpuHelper.BackendDisplayName(plan.Backend)} load failed, retrying with CPU backend");
            UnloadModel();
            var parameters = CreateParameters(modelPath, gpuLayerCount: 0);
            _weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), cancellationToken);
            _parameters = parameters;
            _activeModelPath = modelPath;
            IsUsingGpu = false;
            LoggingService.Info(BuildRuntimeStatusMessage(modelPath));
        }
    }

    /// <summary>
    /// Chooses and freezes the process-wide LLamaSharp native library on the
    /// first load. Vulkan is pinned explicitly via <see cref="NativeLibraryConfig"/>
    /// because LLamaSharp's own Vulkan detection shells out to the Vulkan SDK's
    /// vulkaninfo tool (absent on end-user machines) and silently skips the
    /// backend; CUDA and CPU keep the default selection chain, which works today.
    /// The pinned vulkan directory must be self-contained — the build copies the
    /// CPU backend (ggml-cpu.dll) next to the vulkan binaries so both the Vulkan
    /// load and the 0-layer in-process degrade can resolve every dependency.
    /// </summary>
    private static LocalLlmBackend PinProcessNativeLibraryOnce(LocalLlmBackend desiredBackend)
    {
        lock (ProcessBackendLock)
        {
            if (_processNativeBackend is { } chosen)
            {
                return chosen;
            }

            if (desiredBackend == LocalLlmBackend.Vulkan)
            {
                try
                {
                    var vulkanDir = Path.Combine(
                        AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vulkan");
                    NativeLibraryConfig.All.WithLibrary(
                        Path.Combine(vulkanDir, "llama.dll"),
                        Path.Combine(vulkanDir, "mtmd.dll"));
                    LoggingService.Info("LocalLlmService: Pinned LLamaSharp native library to the Vulkan backend");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn(
                        $"LocalLlmService: Failed to pin the Vulkan native library, keeping default backend selection: {ex.Message}");
                    desiredBackend = LocalLlmBackend.None;
                }
            }

            _processNativeBackend = desiredBackend;
            return desiredBackend;
        }
    }

    private static LocalLlmGpuHelper.RuntimePlan GetRuntimePlanSafely(
        string? modelPath, LocalLlmBackend? restrictToBackend)
    {
        try
        {
            return LocalLlmGpuHelper.GetRuntimePlan(modelPath, restrictToBackend);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: GPU detection failed: {ex.Message}");
            return LocalLlmGpuHelper.CpuPlan(null);
        }
    }

    // The GPU-load crash store lives in LocalLlmGpuLoadCrashStore. These three
    // names are reached from outside this class (LocalLlmGpuHelper, App.OnExit,
    // the smoke suite), so they stay callable on LocalLlmService.
    internal static bool CrashEntryPinsBackend(string? entryBackend, string plannedBackend)
        => LocalLlmGpuLoadCrashStore.CrashEntryPinsBackend(entryBackend, plannedBackend);

    internal static bool GpuLoadCrashedPreviously(string modelPath, string gpuIdentity, string backend)
        => LocalLlmGpuLoadCrashStore.GpuLoadCrashedPreviously(modelPath, gpuIdentity, backend);

    public static void ClearInFlightGpuLoads() => LocalLlmGpuLoadCrashStore.ClearInFlightGpuLoads();

    private static ModelParams CreateParameters(string modelPath, int gpuLayerCount)
    {
        return new ModelParams(modelPath)
        {
            ContextSize = ContextSize,
            GpuLayerCount = gpuLayerCount
        };
    }

    private void EnsurePromptFitsLocalContext(string systemPrompt, string userMessage)
    {
        // Use the active model's tokenizer rather than a character approximation. If
        // the complete source cannot fit alongside the reserved output budget, fail the
        // local post-processing attempt so the caller preserves the raw transcript.
        // Truncating the input here would make a cleanly-terminated response look valid
        // even though part of the recording had already been discarded.
        var systemTokens = _weights!.Tokenize(systemPrompt, true, true, Encoding.UTF8).Length;
        var userTokens = _weights.Tokenize(userMessage, false, true, Encoding.UTF8).Length;
        EnsureTokenBudgetFits(systemTokens, userTokens);
    }

    internal static void EnsureTokenBudgetFits(int systemTokens, int userTokens)
    {
        var promptBudget = ContextSize - MaxTokens - ChatTemplateTokenReserve;
        var promptTokens = checked(systemTokens + userTokens);
        if (promptTokens <= promptBudget)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Local LLM prompt requires {promptTokens:N0} tokens, but only {promptBudget:N0} are available while preserving the {MaxTokens:N0}-token output budget.");
    }

    private string BuildRuntimeStatusMessage(string modelPath)
    {
        var modelName = Path.GetFileName(modelPath);
        if (!IsUsingGpu)
        {
            return $"LocalLlmService: Loaded {modelName} (CPU fallback)";
        }

        var gpu = GpuInfoService.GetBestGpu();
        var gpuSummary = gpu == null
            ? "GPU"
            : $"{gpu.Name}, {gpu.VramDisplay} VRAM";

        return $"LocalLlmService: Loaded {modelName} ({LocalLlmGpuHelper.BackendDisplayName(_activeBackend)}, {gpuSummary})";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Signal any in-flight load/generation to bail so it releases _inferenceLock promptly,
        // then wait only briefly. Without the bounded wait, Dispose() (often the WPF UI thread on
        // shutdown) would block synchronously for up to InferenceTimeout while inference finishes.
        _shutdownCts.Cancel();

        if (_inferenceLock.Wait(DisposeTimeout))
        {
            // Acquired the lock: no generation is in flight, so it is safe to unload the model
            // and dispose the synchronization primitives.
            try
            {
                UnloadModel();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"LocalLlmService: Dispose model unload failed: {ex.Message}");
            }
            finally
            {
                RunCleanupStep("Release inference lock", () => _inferenceLock.Release());
                RunCleanupStep("Dispose inference lock", () => _inferenceLock.Dispose());
                RunCleanupStep("Dispose shutdown cancellation token source", () => _shutdownCts.Dispose());
            }
        }
        else
        {
            // A generation is still holding the lock despite the cancel signal. Leave the
            // semaphore alive for the worker's pending Release(), then unload once it exits.
            LoggingService.Warn(
                $"LocalLlmService: Dispose timed out after {DisposeTimeout.TotalSeconds:F0}s waiting for in-flight inference; scheduling deferred unload");
            // ast-grep-ignore: no-discarded-task-run -- Dispose() is synchronous and cannot await, so the deferred unload must outlive it
            _ = Task.Run(CleanupAfterDisposeTimeout);
        }
    }

    private void CleanupAfterDisposeTimeout()
    {
        var lockHeld = false;
        try
        {
            _inferenceLock.Wait();
            lockHeld = true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Deferred acquire inference lock failed: {ex.Message}");
        }

        if (lockHeld)
        {
            try
            {
                UnloadModel();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"LocalLlmService: Deferred unload after dispose timeout failed: {ex.Message}");
            }
        }

        if (lockHeld)
        {
            RunCleanupStep("Release inference lock after deferred unload", () => _inferenceLock.Release());
        }
        RunCleanupStep("Dispose inference lock after deferred unload", () => _inferenceLock.Dispose());
        RunCleanupStep("Dispose shutdown cancellation token source after deferred unload", () => _shutdownCts.Dispose());
    }

    private static void RunCleanupStep(string operation, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: {operation} failed: {ex.Message}");
        }
    }
}
