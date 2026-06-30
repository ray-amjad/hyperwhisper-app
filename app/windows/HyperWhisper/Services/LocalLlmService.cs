using System.IO;
using System.Text;
using System.Text.Json;
using HyperWhisper.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace HyperWhisper.Services;

/// <summary>
/// Runs local GGUF post-processing models through LLamaSharp.
/// Model weights are loaded lazily and kept warm; each request uses a fresh context
/// so prior transcripts cannot bleed into the next post-processing job.
/// </summary>
public sealed class LocalLlmService : IDisposable
{
    private const int ContextSize = 8192;
    private const int MaxTokens = 2048;
    private const int MaxPromptCharacters = 24_000;
    private static readonly TimeSpan InferenceTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    // Cancelled by Dispose() so an in-flight LoadModel/Generate that holds _inferenceLock bails out
    // promptly instead of forcing shutdown to block on a held lock for up to InferenceTimeout.
    private readonly CancellationTokenSource _shutdownCts = new();

    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private string? _activeModelPath;
    private bool _disposed;

    public bool IsModelLoaded => _weights != null;
    public string? ActiveModelPath => _activeModelPath;
    public string? ActiveModelName => string.IsNullOrEmpty(_activeModelPath) ? null : Path.GetFileName(_activeModelPath);
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

            using var context = _weights!.CreateContext(_parameters!);
            var executor = new InteractiveExecutor(context);
            var chatHistory = new ChatHistory();
            chatHistory.AddMessage(AuthorRole.System, systemPrompt);
            var boundedUserMessage = BoundPromptToLocalContext(userMessage);

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
                new ChatHistory.Message(AuthorRole.User, boundedUserMessage),
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

        var gpuLayerCount = GetGpuLayerCount();

        // A native CUDA crash during weight upload (driver mismatch, VRAM
        // exhaustion, GGUF corruption beyond the magic-byte check) surfaces as an
        // AccessViolationException. On .NET 5+ that is a corrupted-state exception
        // and is NOT delivered to managed catch handlers, so the CPU fallback
        // below cannot run — the process simply dies. An in-flight record written
        // before the GPU load and cleared on success (or on a clean process exit,
        // see ClearInFlightGpuLoads) lets the next launch detect the prior crash —
        // a record still flagged in-flight survived a hard death — and skip the GPU
        // path for that model. A clean quit mid-load no longer counts as a crash.
        //
        // The crash cause is usually durable (incompatible driver/GPU, corrupt
        // GGUF), so the record must SURVIVE this forced-CPU recovery — clearing it
        // here would let the next launch/reload retry CUDA and crash again, an
        // endless crash loop that only ever recovers its first post-crash load.
        //
        // The store keeps one entry PER (model path, GPU identity) so that:
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
        var gpuIdentity = GetGpuIdentity();
        var forcedCpuAfterCrash = false;
        if (gpuLayerCount > 0 && GpuLoadCrashedPreviously(modelPath, gpuIdentity))
        {
            LoggingService.Warn(
                "LocalLlmService: Previous GPU load of this model crashed the process; loading on CPU backend");
            ReportRecoveredGpuLoadCrash(modelPath);
            gpuLayerCount = 0;
            forcedCpuAfterCrash = true;
        }

        try
        {
            var parameters = CreateParameters(modelPath, gpuLayerCount);
            if (gpuLayerCount > 0)
            {
                WriteGpuLoadSentinel(modelPath, gpuIdentity);
            }
            _weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), cancellationToken);
            // GPU load survived: this model is safe on this GPU, so drop its crash
            // record. On the forced-CPU recovery path no record was written for
            // this load (gpuLayerCount == 0), and the pre-existing crash record for
            // this model must persist, so leave the store untouched.
            if (!forcedCpuAfterCrash)
            {
                ClearGpuLoadSentinel(modelPath);
            }
            _parameters = parameters;
            _activeModelPath = modelPath;
            IsUsingGpu = gpuLayerCount > 0;
            LoggingService.Info(BuildRuntimeStatusMessage(modelPath));
        }
        catch (OperationCanceledException)
        {
            // A managed exception means the GPU process survived the load, so the
            // crash record would be a false positive on the next launch — unless we
            // are already on the forced-CPU recovery path, where no record was
            // written for this load and the pre-existing record must persist.
            if (!forcedCpuAfterCrash)
            {
                ClearGpuLoadSentinel(modelPath);
            }
            throw;
        }
        catch when (gpuLayerCount > 0)
        {
            ClearGpuLoadSentinel(modelPath);
            LoggingService.Warn("LocalLlmService: GPU load failed, retrying with CPU backend");
            UnloadModel();
            var parameters = CreateParameters(modelPath, gpuLayerCount: 0);
            _weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), cancellationToken);
            _parameters = parameters;
            _activeModelPath = modelPath;
            IsUsingGpu = false;
            LoggingService.Info(BuildRuntimeStatusMessage(modelPath));
        }
    }

    // Legacy single-path marker from the original PR; migrated/removed on first
    // access so an upgrade does not strand a permanent CPU pin.
    private static string LegacyGpuLoadSentinelPath =>
        AppPaths.Combine("local-llm-gpu-load.sentinel");

    private static string GpuLoadSentinelPath =>
        AppPaths.Combine("local-llm-gpu-load-crashes.json");

    // A crash pin expires this many subsequent launches after it was written, so a
    // non-crash process death (force-quit, OS kill, unrelated thread crash) cannot
    // pin a model to CPU forever. Tracked via a monotonically increasing launch
    // counter rather than wall-clock time so it survives clock changes.
    private const int CrashPinExpiryLaunches = 10;

    private static readonly object GpuLoadCrashStoreLock = new();
    private static long? _cachedLaunchOrdinal;
    private static bool _cleanExitClearingGpuLoads;

    private sealed class GpuLoadCrashEntry
    {
        public string ModelPath { get; set; } = string.Empty;
        public string GpuIdentity { get; set; } = string.Empty;
        public long LaunchOrdinal { get; set; }

        /// <summary>
        /// True while a GPU load is actively in progress. A genuine native CUDA
        /// crash kills the process WITHOUT unwinding, so this flag is never cleared
        /// and survives to the next launch — that survival is the crash signal. A
        /// clean shutdown clears it first (<see cref="ClearInFlightGpuLoads"/>, wired
        /// to process exit), so an ordinary app quit mid-load no longer looks like a
        /// crash on the next launch. Only entries that are still in-flight on the
        /// next launch are treated as crashes, which keeps the forced-CPU pin and the
        /// misattributing Sentry event from firing on a clean exit.
        ///
        /// Nullable so that absent (legacy) is distinguishable from explicitly
        /// cleared. An entry deserialized from the already-shipped #563 JSON store
        /// (which had no InFlight field) yields <c>null</c> — that is a confirmed
        /// crash recorded by the shipped build, so it is treated as in-flight for
        /// backward compat (see <see cref="GpuLoadCrashedPreviously"/>). Only an
        /// explicit <c>false</c> (written by a clean exit /
        /// <see cref="ClearInFlightGpuLoads"/>) drops the entry without forcing CPU.
        /// </summary>
        public bool? InFlight { get; set; }
    }

    private sealed class GpuLoadCrashStore
    {
        public long LaunchOrdinal { get; set; }
        public List<GpuLoadCrashEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Stable identity of the GPU the model would load onto. A change here (driver
    /// update that renames the adapter, GPU swap, falling back to a different
    /// adapter) invalidates any crash pin so the GPU path is re-probed instead of
    /// staying permanently disabled.
    /// </summary>
    private static string GetGpuIdentity()
    {
        try
        {
            return GpuInfoService.GetBestGpu()?.Name ?? "unknown";
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to read GPU identity: {ex.Message}");
            return "unknown";
        }
    }

    private static GpuLoadCrashStore LoadCrashStore()
    {
        // One-time migration off the legacy single-path sentinel. Treat a leftover
        // legacy marker as a fresh crash record for that model so the protection it
        // provided is not lost on upgrade, then delete it.
        var store = new GpuLoadCrashStore();
        try
        {
            if (File.Exists(GpuLoadSentinelPath))
            {
                store = JsonSerializer.Deserialize<GpuLoadCrashStore>(
                    File.ReadAllText(GpuLoadSentinelPath)) ?? new GpuLoadCrashStore();
                store.Entries ??= new List<GpuLoadCrashEntry>();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to read GPU load crash store: {ex.Message}");
            store = new GpuLoadCrashStore();
        }

        try
        {
            if (File.Exists(LegacyGpuLoadSentinelPath))
            {
                var legacyModelPath = File.ReadAllText(LegacyGpuLoadSentinelPath).Trim();
                if (!string.IsNullOrEmpty(legacyModelPath)
                    && !store.Entries.Any(e =>
                        string.Equals(e.ModelPath, legacyModelPath, StringComparison.OrdinalIgnoreCase)))
                {
                    store.Entries.Add(new GpuLoadCrashEntry
                    {
                        ModelPath = legacyModelPath,
                        GpuIdentity = GetGpuIdentity(),
                        LaunchOrdinal = store.LaunchOrdinal,
                        // The legacy single-path marker was only ever left behind by a
                        // hard process death, so preserve it as a confirmed crash.
                        InFlight = true
                    });
                }

                File.Delete(LegacyGpuLoadSentinelPath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to migrate legacy GPU load sentinel: {ex.Message}");
        }

        return store;
    }

    private static void SaveCrashStore(GpuLoadCrashStore store)
    {
        try
        {
            File.WriteAllText(
                GpuLoadSentinelPath,
                JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to write GPU load crash store: {ex.Message}");
        }
    }

    /// <summary>
    /// Launch ordinal for this process. Read once from the store and incremented,
    /// so every entry written this run shares a single ordinal used to age out
    /// stale pins.
    /// </summary>
    private static long CurrentLaunchOrdinal(GpuLoadCrashStore store)
    {
        if (_cachedLaunchOrdinal is { } cached)
        {
            return cached;
        }

        var ordinal = store.LaunchOrdinal + 1;
        store.LaunchOrdinal = ordinal;
        _cachedLaunchOrdinal = ordinal;
        SaveCrashStore(store);
        return ordinal;
    }

    private static bool GpuLoadCrashedPreviously(string modelPath, string gpuIdentity)
    {
        try
        {
            lock (GpuLoadCrashStoreLock)
            {
                var store = LoadCrashStore();
                var now = CurrentLaunchOrdinal(store);

                var entry = store.Entries.FirstOrDefault(e =>
                    string.Equals(e.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    return false;
                }

                // The load that wrote this entry completed cleanly or the process exited
                // gracefully (ClearInFlightGpuLoads ran on exit). Only an in-flight entry
                // that survived a hard process death is treated as a crash, so a clean
                // shutdown mid-load no longer forces CPU or emits a Sentry crash event.
                //
                // Match ONLY an explicit false. A null InFlight means the entry came from
                // the already-shipped #563 JSON store (no InFlight field) where every
                // surviving entry was a confirmed native crash, so null is treated as a
                // crash (falls through to the GPU-identity / expiry checks below) rather
                // than dropped — otherwise a genuine pre-upgrade pin would be discarded
                // and the same CUDA crash would recur once on the first launch.
                if (entry.InFlight == false)
                {
                    store.Entries.Remove(entry);
                    SaveCrashStore(store);
                    return false;
                }

                // GPU environment changed since the crash: re-probe the GPU instead of
                // staying pinned to CPU. Drop the stale entry.
                if (!string.Equals(entry.GpuIdentity, gpuIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    store.Entries.Remove(entry);
                    SaveCrashStore(store);
                    return false;
                }

                // Pin has aged out: give the GPU another chance rather than pinning CPU
                // forever off a single (possibly non-crash) process death.
                if (now - entry.LaunchOrdinal >= CrashPinExpiryLaunches)
                {
                    store.Entries.Remove(entry);
                    SaveCrashStore(store);
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to read GPU load crash store: {ex.Message}");
            return false;
        }
    }

    private static void WriteGpuLoadSentinel(string modelPath, string gpuIdentity)
    {
        try
        {
            lock (GpuLoadCrashStoreLock)
            {
                if (_cleanExitClearingGpuLoads)
                {
                    return;
                }

                var store = LoadCrashStore();
                var now = CurrentLaunchOrdinal(store);

                store.Entries.RemoveAll(e =>
                    string.Equals(e.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase));
                store.Entries.Add(new GpuLoadCrashEntry
                {
                    ModelPath = modelPath,
                    GpuIdentity = gpuIdentity,
                    LaunchOrdinal = now,
                    // Marked in-flight; a clean shutdown clears it, so only a hard
                    // process death during the load leaves it set for the next launch.
                    InFlight = true
                });
                SaveCrashStore(store);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to write GPU load crash store: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes any in-flight GPU load entry started by THIS process run, so
    /// an ordinary process exit while a load was running is not mistaken for a
    /// native crash on the next launch. Wired to the WPF <c>App.OnExit</c>
    /// clean-shutdown path. Best-effort: a failure here only risks a one-off
    /// false-positive CPU pin, which the launch-ordinal expiry still self-heals.
    ///
    /// Scoped to the current run via <see cref="_cachedLaunchOrdinal"/>: only
    /// entries whose <c>LaunchOrdinal</c> equals this process's ordinal are
    /// removed. A crash entry left in-flight by a PRIOR run (a lower ordinal) is
    /// the durable crash pin that #563/#770 must preserve, so it is left intact
    /// and a clean quit after a recovery run does not re-open the GPU crash loop.
    /// </summary>
    public static void ClearInFlightGpuLoads()
    {
        try
        {
            lock (GpuLoadCrashStoreLock)
            {
                _cleanExitClearingGpuLoads = true;

                // No load was attempted this run (ordinal never assigned), so there
                // is nothing started by this process to clear. Returning early also
                // guarantees we never touch a prior run's surviving crash entry.
                if (_cachedLaunchOrdinal is not { } currentOrdinal)
                {
                    return;
                }

                var store = LoadCrashStore();
                var removed = store.Entries.RemoveAll(entry =>
                    // Only this run's still-in-flight entries (written with explicit
                    // true). Prior-run crash pins and legacy entries are preserved.
                    entry.InFlight == true && entry.LaunchOrdinal == currentOrdinal);

                if (removed > 0)
                {
                    SaveCrashStore(store);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to clear in-flight GPU loads on exit: {ex.Message}");
        }
    }

    private static void ClearGpuLoadSentinel(string modelPath)
    {
        try
        {
            lock (GpuLoadCrashStoreLock)
            {
                if (!File.Exists(GpuLoadSentinelPath) && !File.Exists(LegacyGpuLoadSentinelPath))
                {
                    return;
                }

                var store = LoadCrashStore();
                var removed = store.Entries.RemoveAll(e =>
                    string.Equals(e.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    SaveCrashStore(store);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to clear GPU load crash store: {ex.Message}");
        }
    }

    private static void ReportRecoveredGpuLoadCrash(string modelPath)
    {
        var gpu = GpuInfoService.GetBestGpu();
        SentryService.CaptureDiagnosticEvent(
            "LocalLlmService: Recovered from native GPU load crash, forced CPU backend",
            extras: new Dictionary<string, object>
            {
                ["model_file"] = Path.GetFileName(modelPath),
                ["gpu_name"] = gpu?.Name ?? "unknown",
                ["gpu_vram"] = gpu?.VramDisplay ?? "unknown"
            },
            tags: new Dictionary<string, string>
            {
                ["component"] = "local_llm",
                ["recovery"] = "gpu_to_cpu"
            },
            fingerprint: new[] { "local-llm-gpu-load-crash" },
            dedupeKey: $"local-llm-gpu-load-crash:{Path.GetFileName(modelPath)}");
    }

    private static ModelParams CreateParameters(string modelPath, int gpuLayerCount)
    {
        return new ModelParams(modelPath)
        {
            ContextSize = ContextSize,
            GpuLayerCount = gpuLayerCount
        };
    }

    private static string BoundPromptToLocalContext(string userMessage)
    {
        if (userMessage.Length <= MaxPromptCharacters)
        {
            return userMessage;
        }

        LoggingService.Warn(
            $"LocalLlmService: Prompt truncated from {userMessage.Length:N0} to {MaxPromptCharacters:N0} characters for local model context");

        const string marker = "\n\n[Transcript truncated to fit the local model context. Preserve and clean the visible content.]\n\n";
        var remainingCharacters = MaxPromptCharacters - marker.Length;
        var headLength = remainingCharacters / 2;
        var tailLength = remainingCharacters - headLength;

        return userMessage[..headLength] + marker + userMessage[^tailLength..];
    }

    private static int GetGpuLayerCount()
    {
        try
        {
            return LocalLlmGpuHelper.GetRuntimePlan().GpuLayerCount;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: GPU detection failed: {ex.Message}");
        }

        return 0;
    }

    private string BuildRuntimeStatusMessage(string modelPath)
    {
        var modelName = Path.GetFileName(modelPath);
        if (!IsUsingGpu)
        {
            return $"LocalLlmService: Loaded {modelName} (CPU fallback)";
        }

        var gpu = LocalLlmGpuHelper.GetRuntimePlan().Gpu;
        var gpuSummary = gpu == null
            ? "GPU"
            : $"{gpu.Name}, {gpu.VramDisplay} VRAM";

        return $"LocalLlmService: Loaded {modelName} (CUDA, {gpuSummary})";
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
