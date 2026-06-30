// LICENSE USAGE TRACKER
// Tracks daily transcription time and model downloads for trial users.
//
// TODO-verify (Windows/CI): Rust shared-core swap. UNVERIFIED / compile-only.
//
// Wave 3 swap: all usage state + limit enforcement now lives in the `hw-license`
// Rust core, persisted via RustCoreKeyValueStore under com.hyperwhisper.usage.*.
// This class is a thin shim that keeps the public surface (singleton, properties,
// UsageChanged event, Record*/Can*/GetRemaining* methods, static TrialDaily*/
// TrialModel* display fields) and delegates to HyperwhisperCoreMethods.
//
// now-INJECTION: PLAIN UTC. Native Windows reset daily usage on the UTC calendar
// day (CheckDailyReset used DateTime.UtcNow.Date), which matches the core's
// `now/86400` UTC-day bucket exactly — so no local offset is applied (unlike
// macOS). The day-boundary reset is now read-time in the core (no midnight timer).
//
// TRIAL LIMITS: defaults 300s release / 1800s debug, 3 model downloads,
// overlaid with a fresh remote override (24h TTL) via RustLicenseCore.EffectiveLimits.

using System;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Tracks usage limits for trial users (delegated to the Rust core).
/// Licensed users have unlimited access.
/// </summary>
public sealed class LicenseUsageTracker
{
    // =========================================================================
    // CONSTANTS / DISPLAY LIMITS
    // =========================================================================

    // Display-only mirrors of the active limits (read directly by Settings UI).
    // Initialized from the core's build-flavor defaults; updated by
    // UpdateTrialLimits when a remote override is applied.

    public static int TrialDailyLimitSeconds { get; private set; } =
        (int)HyperwhisperCoreMethods.LicenseLimitsDefaults(RustLicenseCore.DebugBuild).dailySeconds;

    public static int TrialModelLimit { get; private set; } =
        (int)HyperwhisperCoreMethods.LicenseLimitsDefaults(RustLicenseCore.DebugBuild).modelDownloads;

    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static LicenseUsageTracker? _instance;
    private static readonly object _lock = new();

    public static LicenseUsageTracker Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new LicenseUsageTracker();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private LicenseStatus _licenseStatus = LicenseStatus.Trial;

    /// <summary>Fired when usage statistics change.</summary>
    public event EventHandler? UsageChanged;

    private LicenseUsageTracker()
    {
        // Ensure the one-shot legacy migration has run before any usage read.
        _ = RustCoreKeyValueStore.Instance;
        var snap = Snapshot();
        LoggingService.Info(
            $"LicenseUsageTracker: Initialized (daily: {snap.dailySecondsUsed}s, models: {snap.modelsDownloaded})");
    }

    // =========================================================================
    // CORE HELPERS
    // =========================================================================

    private KeyValueStore Store => RustLicenseCore.Store;

    private UsageSnapshot Snapshot()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        return HyperwhisperCoreMethods.LicenseCheckLimits(
            Store, RustLicenseCore.ToCore(_licenseStatus), RustLicenseCore.EffectiveLimits(), RustLicenseCore.Now());
    }

    // =========================================================================
    // PUBLIC PROPERTIES
    // =========================================================================

    /// <summary>Daily transcription usage in seconds (resets at UTC day in core).</summary>
    public int DailyUsageSeconds => (int)Snapshot().dailySecondsUsed;

    /// <summary>Lifetime model-download count.</summary>
    public int ModelsDownloaded => (int)Snapshot().modelsDownloaded;

    /// <summary>Whether the daily limit has been reached (trial users only).</summary>
    public bool IsDailyLimitReached => Snapshot().dailyLimitReached;

    /// <summary>Whether the model download limit has been reached (trial users only).</summary>
    public bool IsModelLimitReached => Snapshot().modelLimitReached;

    // =========================================================================
    // LICENSE STATUS / LIMITS
    // =========================================================================

    /// <summary>
    /// Updates the display limits from a remote override and persists the override
    /// so the core applies it on every subsequent check.
    /// </summary>
    public void UpdateTrialLimits(int dailySeconds, int modelLimit)
    {
        TrialDailyLimitSeconds = dailySeconds;
        TrialModelLimit = modelLimit;

        // TODO-verify (Windows/CI): Rust shared-core swap.
        HyperwhisperCoreMethods.LicenseStoreRemoteOverride(
            Store,
            new TrialLimits(@dailySeconds: dailySeconds, @modelDownloads: modelLimit),
            RustLicenseCore.Now());

        LoggingService.Info($"LicenseUsageTracker: Trial limits updated (daily={dailySeconds}s, models={modelLimit})");
        NotifyUsageChanged();
    }

    /// <summary>Updates the license status (drives limit enforcement).</summary>
    public void UpdateLicenseStatus(LicenseStatus status)
    {
        var changed = _licenseStatus != status;
        _licenseStatus = status;

        if (changed)
        {
            LoggingService.Info($"LicenseUsageTracker: License status updated to {status}");
            NotifyUsageChanged();
        }
    }

    // =========================================================================
    // RECORDING LIMITS
    // =========================================================================

    /// <summary>Checks if user can start recording based on the daily limit.</summary>
    public bool CanStartRecording()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        return HyperwhisperCoreMethods.LicenseCanStartRecording(
            Store, RustLicenseCore.ToCore(_licenseStatus), RustLicenseCore.EffectiveLimits(), RustLicenseCore.Now());
    }

    /// <summary>Records transcription time and updates usage.</summary>
    public void RecordTranscriptionTime(int seconds)
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        HyperwhisperCoreMethods.LicenseRecordUsage(Store, seconds, RustLicenseCore.Now());
        LoggingService.Info($"LicenseUsageTracker: Recorded {seconds}s transcription");
        NotifyUsageChanged();
    }

    /// <summary>Gets remaining daily transcription time in seconds (int.MaxValue if licensed).</summary>
    public int GetRemainingDailyTime()
    {
        var remaining = Snapshot().remainingDailySeconds;
        return remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
    }

    /// <summary>Gets remaining daily time as a formatted string.</summary>
    public string GetRemainingDailyTimeFormatted()
    {
        var remaining = GetRemainingDailyTime();
        if (remaining == int.MaxValue)
            return "Unlimited";

        var minutes = remaining / 60;
        var seconds = remaining % 60;
        return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
    }

    // =========================================================================
    // MODEL DOWNLOAD LIMITS
    // =========================================================================

    /// <summary>Checks if user can download another model.</summary>
    public bool CanDownloadModel()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        return HyperwhisperCoreMethods.LicenseCanDownloadModel(
            Store, RustLicenseCore.ToCore(_licenseStatus), RustLicenseCore.EffectiveLimits());
    }

    /// <summary>Increments the lifetime model download count.</summary>
    public void IncrementModelDownloadCount()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        HyperwhisperCoreMethods.LicenseRecordModelDownload(Store);
        LoggingService.Info($"LicenseUsageTracker: Model download recorded (total: {ModelsDownloaded})");
        NotifyUsageChanged();
    }

    /// <summary>Gets remaining model downloads (int.MaxValue if licensed).</summary>
    public int GetRemainingModelDownloads()
    {
        var remaining = Snapshot().remainingModelDownloads;
        return remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
    }

    // =========================================================================
    // PRIVATE
    // =========================================================================

    private void NotifyUsageChanged()
    {
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }
}
