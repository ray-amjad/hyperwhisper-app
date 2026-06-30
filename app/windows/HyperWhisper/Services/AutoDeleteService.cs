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

    // Statistics
    private int _lastCleanupTranscriptsDeleted;
    private int _lastCleanupFilesDeleted;
    private DateTime? _lastCleanupTime;

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
            _lastCleanupTranscriptsDeleted = deletedCount;
            _lastCleanupFilesDeleted = filesDeleted;
            _lastCleanupTime = DateTime.UtcNow;

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

    public int LastCleanupTranscriptsDeleted => _lastCleanupTranscriptsDeleted;
    public int LastCleanupFilesDeleted => _lastCleanupFilesDeleted;
    public DateTime? LastCleanupTime => _lastCleanupTime;

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
