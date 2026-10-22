using System.IO;
using System.Text.Json;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// The local LLM's GPU-load crash sentinel store: a JSON file of in-flight load
/// records, one per (model path, GPU identity, backend), so a native GPU crash
/// during a weight upload pins the next launch off the backend that crashed.
/// Lifted out of <see cref="LocalLlmService"/> unchanged; it shares no state with it.
/// </summary>
internal static class LocalLlmGpuLoadCrashStore
{
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

        /// <summary>
        /// Which backend was being loaded when this entry was written
        /// (<see cref="LocalLlmGpuHelper.CudaBackendId"/> /
        /// <see cref="LocalLlmGpuHelper.VulkanBackendId"/>). A crash pins only
        /// its own backend, so a CUDA crash still lets the next launch try
        /// Vulkan for the same model. Nullable for the same back-compat reason
        /// as <see cref="InFlight"/>: entries deserialized from a store written
        /// before this field existed yield <c>null</c>, which is treated as
        /// pinning EVERY backend (see <see cref="CrashEntryPinsBackend"/>) —
        /// those crashes predate the Vulkan backend and forced CPU, and the
        /// launch-ordinal expiry still ages them out.
        /// </summary>
        public string? Backend { get; set; }
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
    internal static string GetGpuIdentity()
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
                        // Backend stays null: the marker predates per-backend pins,
                        // so it pins every backend (matching its original meaning).
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

    /// <summary>
    /// Whether a crash-store entry written for <paramref name="entryBackend"/>
    /// pins <paramref name="plannedBackend"/>. A null entry backend predates
    /// per-backend pins (shipped #563 store / legacy sentinel migration) and
    /// pins every backend — its crash was recorded when the app only ever
    /// forced CPU, so matching everything preserves that pre-upgrade meaning.
    /// </summary>
    internal static bool CrashEntryPinsBackend(string? entryBackend, string plannedBackend)
        => entryBackend == null
           || string.Equals(entryBackend, plannedBackend, StringComparison.OrdinalIgnoreCase);

    internal static bool GpuLoadCrashedPreviously(string modelPath, string gpuIdentity, string backend)
    {
        try
        {
            lock (GpuLoadCrashStoreLock)
            {
                var store = LoadCrashStore();
                var now = CurrentLaunchOrdinal(store);

                var matching = store.Entries.Where(e =>
                    string.Equals(e.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
                    && CrashEntryPinsBackend(e.Backend, backend)).ToList();

                if (matching.Count == 0)
                {
                    return false;
                }

                var pinned = false;
                var mutated = false;
                foreach (var entry in matching)
                {
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
                    // and the same GPU crash would recur once on the first launch.
                    if (entry.InFlight == false)
                    {
                        store.Entries.Remove(entry);
                        mutated = true;
                        continue;
                    }

                    // GPU environment changed since the crash: re-probe the GPU instead of
                    // staying pinned. Drop the stale entry.
                    if (!string.Equals(entry.GpuIdentity, gpuIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        store.Entries.Remove(entry);
                        mutated = true;
                        continue;
                    }

                    // Pin has aged out: give the GPU another chance rather than pinning
                    // forever off a single (possibly non-crash) process death.
                    if (now - entry.LaunchOrdinal >= CrashPinExpiryLaunches)
                    {
                        store.Entries.Remove(entry);
                        mutated = true;
                        continue;
                    }

                    pinned = true;
                }

                if (mutated)
                {
                    SaveCrashStore(store);
                }

                return pinned;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmService: Failed to read GPU load crash store: {ex.Message}");
            return false;
        }
    }

    internal static void WriteGpuLoadSentinel(string modelPath, string gpuIdentity, string backend)
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
                    string.Equals(e.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
                    && CrashEntryPinsBackend(e.Backend, backend));
                store.Entries.Add(new GpuLoadCrashEntry
                {
                    ModelPath = modelPath,
                    GpuIdentity = gpuIdentity,
                    LaunchOrdinal = now,
                    Backend = backend,
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

    internal static void ClearGpuLoadSentinel(string modelPath, string backend)
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
                // Scoped to the backend that just proved safe (or survived with a
                // managed failure); a crash pin recorded for a DIFFERENT backend
                // must persist so the next launch keeps skipping it.
                var removed = store.Entries.RemoveAll(e =>
                    string.Equals(e.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
                    && CrashEntryPinsBackend(e.Backend, backend));
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

    internal static void ReportRecoveredGpuLoadCrash(
        string modelPath, LocalLlmBackend crashedBackend, LocalLlmBackend fallbackBackend)
    {
        var gpu = GpuInfoService.GetBestGpu();
        var from = LocalLlmGpuHelper.BackendId(crashedBackend) ?? "gpu";
        var to = LocalLlmGpuHelper.BackendId(fallbackBackend) ?? "cpu";
        SentryService.CaptureDiagnosticEvent(
            "LocalLlmService: Recovered from native GPU load crash, downgraded backend",
            extras: new Dictionary<string, object>
            {
                ["model_file"] = Path.GetFileName(modelPath),
                ["gpu_name"] = gpu?.Name ?? "unknown",
                ["gpu_vram"] = gpu?.VramDisplay ?? "unknown",
                ["crashed_backend"] = from,
                ["fallback_backend"] = to
            },
            tags: new Dictionary<string, string>
            {
                ["component"] = "local_llm",
                ["backend"] = from,
                ["recovery"] = $"{from}_to_{to}"
            },
            fingerprint: new[] { "local-llm-gpu-load-crash" },
            dedupeKey: $"local-llm-gpu-load-crash:{Path.GetFileName(modelPath)}:{from}_to_{to}");
    }
}
