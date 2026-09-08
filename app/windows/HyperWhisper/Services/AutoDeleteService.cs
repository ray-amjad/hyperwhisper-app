using System;
using System.IO;
using System.Linq;

namespace HyperWhisper.Services;

/// <summary>
/// AUTO-DELETE CLEANUP SERVICE
///
/// Automatically deletes old transcripts based on user-configured age threshold.
/// Follows the same pattern as macOS AutoDeleteCleanupService.
///
/// CLEANUP FLOW:
/// 1. Check if auto-delete is enabled
/// 2. Calculate cutoff date (Now - DaysOld)
/// 3. Query transcripts older than cutoff
/// 4. Delete audio files and database records
/// 5. Track and log statistics
///
/// SCHEDULING:
/// - Runs once per hour via System.Timers.Timer
/// - First cleanup runs immediately on startup
/// - Timer interval: 1 hour (adequate for daily granularity)
///
/// THREAD SAFETY:
/// - Uses existing HistoryService locking
/// - Timer callbacks run on thread pool
/// - Disposal via SafeDispose pattern
/// </summary>
public sealed class AutoDeleteService : IDisposable
{
    // =========================================================================
    // SINGLETON PATTERN
    // =========================================================================

    private static readonly Lazy<AutoDeleteService> _instance = new(() => new AutoDeleteService());
    public static AutoDeleteService Instance => _instance.Value;

    private AutoDeleteService() { }

    // =========================================================================
    // STATE
    // =========================================================================

    private System.Timers.Timer? _cleanupTimer;
    private bool _isInitialized;
    private bool _disposed;
    private bool _isCleanupInProgress;

    // Statistics. The timestamp and the transcript count live in settings.json, via
    // SettingsService, because the Storage page's "last cleanup" line is a fact about
    // the PROFILE: while they were fields here, a sweep was forgotten at shutdown and
    // the line reverted to "No cleanup has run yet" on the next launch (issue #514).
    // The audio-file count stays process-local because nothing renders it.
    private int _lastCleanupFilesDeleted;

    // Services
    private SettingsService Settings => SettingsService.Instance;
    private HistoryService History => HistoryService.Instance;

    // Timer interval: Check once per hour
    private const int CleanupIntervalMs = 60 * 60 * 1000;  // 1 hour

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Initialize the auto-delete service and start periodic cleanup.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized)
        {
            LoggingService.Debug("AutoDeleteService: Already initialized, skipping");
            return;
        }

        try
        {
            LoggingService.Info("AutoDeleteService: Initializing");

            // Run cleanup once immediately on startup
            PerformCleanup();

            // Start hourly timer
            _cleanupTimer = new System.Timers.Timer(CleanupIntervalMs);
            _cleanupTimer.AutoReset = true;
            _cleanupTimer.Elapsed += OnTimerElapsed;
            _cleanupTimer.Start();

            _isInitialized = true;
            LoggingService.Info("AutoDeleteService: Initialization complete");
        }
        catch (Exception ex)
        {
            LoggingService.Error("AutoDeleteService: Failed to initialize", ex);
            // Don't throw - auto-delete failure shouldn't block app startup
        }
    }

    /// <summary>
    /// Manually trigger cleanup (called from UI "Delete Now" button).
    /// Returns count of deleted transcripts.
    /// </summary>
    public int PerformManualCleanup()
    {
        LoggingService.Info("AutoDeleteService: Manual cleanup requested by user");
        return PerformCleanup(throwOnFailure: true);
    }

    /// <summary>
    /// Shutdown the service and cleanup resources.
    /// </summary>
    public void Shutdown()
    {
        if (!_isInitialized) return;

        try
        {
            LoggingService.Debug("AutoDeleteService: Shutting down");
            SafeDispose(ref _cleanupTimer);
            _isInitialized = false;
            LoggingService.Debug("AutoDeleteService: Shutdown complete");
        }
        catch (Exception ex)
        {
            LoggingService.Error("AutoDeleteService: Error during shutdown", ex);
        }
    }

    // =========================================================================
    // CLEANUP LOGIC
    // =========================================================================

    private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        PerformCleanup();
    }

    /// <summary>
    /// Execute cleanup: find old transcripts and delete them.
    /// Returns count of deleted transcripts.
    /// </summary>
    private int PerformCleanup(bool throwOnFailure = false)
    {
        // GUARD CLAUSE: Check if enabled
        if (!Settings.AutoDeleteEnabled)
        {
            LoggingService.Debug("AutoDeleteService: Auto-delete disabled, skipping cleanup");
            return 0;
        }

        // GUARD CLAUSE: Prevent concurrent cleanup
        if (_isCleanupInProgress)
        {
            LoggingService.Warn("AutoDeleteService: Cleanup already in progress, skipping");
            return 0;
        }

        _isCleanupInProgress = true;

        try
        {
            int daysOld = Settings.AutoDeleteDaysOld;
            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);

            LoggingService.Info($"AutoDeleteService: Starting cleanup. Cutoff date: {cutoffDate:yyyy-MM-dd HH:mm:ss} (older than {daysOld} days)");

            // Get transcripts older than cutoff
            var transcriptsToDelete = History.GetTranscriptsOlderThan(cutoffDate);

            if (transcriptsToDelete.Count == 0)
            {
                LoggingService.Debug("AutoDeleteService: No transcripts to delete");

                // A sweep that deleted nothing still RAN, and the line under the days box
                // reports when a sweep last ran. Returning here without recording it left
                // the page saying "No cleanup has run yet" straight after the app had
                // reported "Cleanup Complete" (issue #514) — and "never run" is the
                // reading that makes a user press Delete Now again.
                _lastCleanupFilesDeleted = 0;
                Settings.RecordAutoDeleteCleanup(DateTime.UtcNow, 0);
                return 0;
            }

            LoggingService.Info($"AutoDeleteService: Found {transcriptsToDelete.Count} transcripts to delete");

            // Count existing audio files before deletion. Transcripts may have
            // both an original and a VAD-trimmed audio file.
            int filesDeleted = transcriptsToDelete
                .SelectMany(t => new[] { t.AudioFilePath, t.TrimmedAudioFilePath })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(HistoryService.IsDeletableAudioPath)
                .Count(File.Exists);

            // Delete transcripts (HistoryService handles audio file deletion)
            var ids = transcriptsToDelete.Select(t => t.Id).ToList();
            int deletedCount = History.DeleteTranscripts(ids);

            // Update statistics
            _lastCleanupFilesDeleted = filesDeleted;
            Settings.RecordAutoDeleteCleanup(DateTime.UtcNow, deletedCount);

            LoggingService.Info($"AutoDeleteService: Cleanup complete. Deleted {deletedCount} transcripts and {filesDeleted} audio files");

            // Report to Sentry for diagnostics
            if (Settings.EnableErrorLogging && deletedCount > 0)
            {
                SentryService.AddBreadcrumb(
                    "Auto-delete cleanup completed",
                    "auto-delete",
                    Sentry.BreadcrumbLevel.Info,
                    new Dictionary<string, string>
                    {
                        ["transcriptsDeleted"] = deletedCount.ToString(),
                        ["filesDeleted"] = filesDeleted.ToString(),
                        ["daysOld"] = daysOld.ToString()
                    });
            }

            return deletedCount;
        }
        catch (Exception ex)
        {
            LoggingService.Error("AutoDeleteService: Cleanup failed", ex);
            if (Settings.EnableErrorLogging)
            {
                SentryService.Capture(ex, "Auto-delete cleanup failed");
            }

            if (throwOnFailure)
            {
                throw new InvalidOperationException("Auto-delete cleanup failed.", ex);
            }

            return 0;
        }
        finally
        {
            _isCleanupInProgress = false;
        }
    }

    // =========================================================================
    // STATISTICS (for UI display)
    // =========================================================================

    public int LastCleanupTranscriptsDeleted => Settings.AutoDeleteLastCleanupDeleted;
    public int LastCleanupFilesDeleted => _lastCleanupFilesDeleted;

    /// <summary>UTC. The Storage page converts it for display.</summary>
    public DateTime? LastCleanupTime => Settings.AutoDeleteLastCleanupUtc;

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SafeDispose(ref _cleanupTimer);
        GC.SuppressFinalize(this);
    }

    private static void SafeDispose<T>(ref T? resource) where T : class, IDisposable
    {
        var temp = resource;
        resource = null;

        try
        {
            temp?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AutoDeleteService: Dispose failed for {typeof(T).Name}: {ex.Message}");
        }
    }
}
