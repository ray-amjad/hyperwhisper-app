using System.IO;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// HISTORY SERVICE
///
/// Manages transcript persistence and CRUD operations using EF Core.
///
/// STORAGE LOCATIONS:
/// - Transcript data: %LOCALAPPDATA%\HyperWhisper\hyperwhisper.db (SQLite)
/// - Audio files: Configurable recordings folder (default: Documents\HyperWhisper\recordings; legacy: %LOCALAPPDATA%\HyperWhisper\Audio)
///
/// THREAD SAFETY:
/// - All operations are synchronized via lock
/// - Per-operation DbContext instances for safety
///
/// DATA FLOW:
/// 1. MainViewModel calls CreateProcessingTranscript() when recording stops
/// 2. MainViewModel calls UpdateTranscript() after transcription completes/fails
/// 3. HistoryViewModel calls Search() to display filtered results
/// 4. User actions trigger Delete/Retry operations
/// </summary>
public class HistoryService
{

    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static HistoryService? _instance;
    private static readonly object _lock = new();

    public static HistoryService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new HistoryService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly StorageService _storageService = StorageService.Instance;

    /// <summary>Event fired when a transcript is added.</summary>
    public event EventHandler<Transcript>? TranscriptAdded;

    /// <summary>Event fired when a transcript is updated.</summary>
    public event EventHandler<Transcript>? TranscriptUpdated;

    /// <summary>Event fired when a transcript is deleted.</summary>
    public event EventHandler<Guid>? TranscriptDeleted;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private HistoryService()
    {
        // EF Core database is initialized by DatabaseInitializer at app startup
        _storageService.GetRecordingsFolder(); // Ensure recordings folder exists/has fallback
    }

    // =========================================================================
    // PUBLIC METHODS - READ
    // =========================================================================

    /// <summary>
    /// Gets all transcripts sorted by date descending (newest first).
    /// </summary>
    public List<Transcript> GetAllTranscripts()
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();
            return context.Transcripts
                .OrderByDescending(t => t.Date)
                .ToList();
        }
    }

    /// <summary>
    /// Gets a transcript by ID.
    /// </summary>
    public Transcript? GetTranscript(Guid id)
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();
            return context.Transcripts.Find(id);
        }
    }

    /// <summary>
    /// Get transcripts older than the specified cutoff date.
    /// Used by AutoDeleteService to find transcripts eligible for deletion.
    /// </summary>
    public List<Transcript> GetTranscriptsOlderThan(DateTime cutoffDate)
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();
                return context.Transcripts
                    .Where(t => t.Date < cutoffDate)
                    .OrderBy(t => t.Date)  // Oldest first
                    .ToList();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"HistoryService: Failed to fetch transcripts older than {cutoffDate}", ex);
                return new List<Transcript>();
            }
        }
    }

    /// <summary>
    /// Searches transcripts with optional text query and date filter.
    /// Search is case-insensitive and matches against the Text field.
    /// </summary>
    /// <param name="query">Text to search for (null or empty returns all)</param>
    /// <param name="filter">Date filter to apply</param>
    public List<Transcript> Search(string? query, DateFilter filter)
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();

            var queryable = context.Transcripts.AsQueryable();

            // Apply text search filter (case-insensitive)
            if (!string.IsNullOrWhiteSpace(query))
            {
                var lowerQuery = query.ToLowerInvariant();
                queryable = queryable.Where(t => t.Text.ToLower().Contains(lowerQuery));
            }

            // Apply date filter (UTC comparisons)
            var now = DateTime.UtcNow;
            switch (filter)
            {
                case DateFilter.Today:
                    var todayStart = now.Date;
                    var todayEnd = todayStart.AddDays(1);
                    queryable = queryable.Where(t => t.Date >= todayStart && t.Date < todayEnd);
                    break;

                case DateFilter.ThisWeek:
                    var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);
                    var weekEnd = weekStart.AddDays(7);
                    queryable = queryable.Where(t => t.Date >= weekStart && t.Date < weekEnd);
                    break;

                case DateFilter.ThisMonth:
                    var monthStart = new DateTime(now.Year, now.Month, 1);
                    var monthEnd = monthStart.AddMonths(1);
                    queryable = queryable.Where(t => t.Date >= monthStart && t.Date < monthEnd);
                    break;

                // DateFilter.All - no filter
            }

            return queryable
                .OrderByDescending(t => t.Date)
                .ToList();
        }
    }

    // =========================================================================
    // PUBLIC METHODS - CREATE
    // =========================================================================

    /// <summary>
    /// Creates a new transcript in Processing status.
    /// Called immediately when recording stops, before transcription begins.
    /// This allows the transcript to appear in the history view right away.
    /// </summary>
    /// <param name="duration">Recording duration in seconds</param>
    /// <param name="modeName">Name of the mode used</param>
    /// <param name="audioFilePath">Path to the audio file, or null for streaming sessions without saved audio</param>
    /// <returns>The created transcript</returns>
    public Transcript CreateProcessingTranscript(double duration, string? modeName, string? audioFilePath)
    {
        var transcript = new Transcript
        {
            Id = Guid.NewGuid(),
            Date = DateTime.UtcNow,
            Duration = duration,
            Mode = modeName,
            AudioFilePath = audioFilePath,
            Status = TranscriptStatus.Processing,
            Text = Loc.S("recording.state.processing")
        };

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();
                context.Transcripts.Add(transcript);
                context.SaveChanges();
                LoggingService.Info($"HistoryService: Created processing transcript {transcript.Id}");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error("HistoryService: Failed to create transcript", ex);
                throw;
            }
        }

        // Fire event outside lock to prevent deadlock
        TranscriptAdded?.Invoke(this, transcript);
        return transcript;
    }

    // =========================================================================
    // PUBLIC METHODS - RECOVERY
    // =========================================================================

    /// <summary>
    /// Recovers orphaned transcripts left in <see cref="TranscriptStatus.Processing"/>
    /// from a previous session (e.g. app crash, kill, or OS restart mid-transcription).
    ///
    /// Called on app startup before the UI loads. Any transcript still Processing
    /// at this point cannot possibly have a live worker backing it — the only
    /// worker that set it to Processing died with the previous process. Flip
    /// each one to <see cref="TranscriptStatus.Failed"/> with a clear reason so
    /// the history row stops spinning and becomes retryable.
    /// </summary>
    /// <returns>The number of transcripts that were recovered.</returns>
    public int RecoverOrphanedProcessingTranscripts()
    {
        List<Transcript> recovered;

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                recovered = context.Transcripts
                    .Where(t => t.Status == TranscriptStatus.Processing)
                    .ToList();

                if (recovered.Count == 0)
                {
                    return 0;
                }

                const string reason = "Interrupted — app was restarted while processing";
                foreach (var t in recovered)
                {
                    t.Status = TranscriptStatus.Failed;
                    t.FailedReason = reason;
                    // Replace the "Processing audio..." placeholder text so the
                    // history row shows something useful instead of a stuck spinner label.
                    if (string.IsNullOrWhiteSpace(t.Text) ||
                        t.Text == Loc.S("recording.state.processing"))
                    {
                        t.Text = reason;
                    }
                }

                context.SaveChanges();
                LoggingService.Warn($"HistoryService: Recovered {recovered.Count} orphaned Processing transcript(s) left over from a previous session");
            }
            catch (Exception ex)
            {
                LoggingService.Error("HistoryService: Orphaned Processing transcript recovery failed", ex);
                return 0;
            }
        }

        // Fire update events outside the lock so UI can refresh rows if already bound.
        foreach (var t in recovered)
        {
            try
            {
                TranscriptUpdated?.Invoke(this, t);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"HistoryService: TranscriptUpdated handler threw during recovery: {ex.Message}");
            }
        }

        return recovered.Count;
    }

    // =========================================================================
    // PUBLIC METHODS - UPDATE
    // =========================================================================

    /// <summary>
    /// Updates an existing transcript. Used to:
    /// - Update status from Processing to Completed/Failed
    /// - Store transcription results
    /// - Record retry attempts
    /// </summary>
    public void UpdateTranscript(Transcript transcript)
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                var existing = context.Transcripts.Find(transcript.Id);
                if (existing == null)
                {
                    LoggingService.Warn($"HistoryService: Transcript {transcript.Id} not found for update");
                    return;
                }

                context.Entry(existing).CurrentValues.SetValues(transcript);
                context.SaveChanges();
                LoggingService.Info($"HistoryService: Updated transcript {transcript.Id} (Status: {transcript.Status})");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error($"HistoryService: Failed to update transcript {transcript.Id}", ex);
                throw;
            }
        }

        // Fire event outside lock to prevent deadlock
        TranscriptUpdated?.Invoke(this, transcript);
    }

    // =========================================================================
    // PUBLIC METHODS - DELETE
    // =========================================================================

    /// <summary>
    /// Deletes a single transcript and its associated audio file.
    /// </summary>
    public bool DeleteTranscript(Guid id)
    {
        Transcript? transcript = null;

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                transcript = context.Transcripts.Find(id);
                if (transcript == null)
                {
                    LoggingService.Warn($"HistoryService: Transcript {id} not found for deletion");
                    return false;
                }

                context.Transcripts.Remove(transcript);
                context.SaveChanges();
                LoggingService.Info($"HistoryService: Deleted transcript {id}");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error($"HistoryService: Failed to delete transcript {id}", ex);
                return false;
            }
        }

        // Delete audio files outside of lock
        DeleteTranscriptAudioFiles(transcript);
        // Fire event outside lock to prevent deadlock
        TranscriptDeleted?.Invoke(this, id);
        return true;
    }

    /// <summary>
    /// Deletes multiple transcripts and their associated audio files.
    /// More efficient than calling DeleteTranscript multiple times.
    /// </summary>
    public int DeleteTranscripts(IEnumerable<Guid> ids)
    {
        var idSet = ids.ToHashSet();
        List<Transcript> deletedTranscripts;

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                deletedTranscripts = context.Transcripts
                    .Where(t => idSet.Contains(t.Id))
                    .ToList();

                if (deletedTranscripts.Count == 0)
                {
                    return 0;
                }

                context.Transcripts.RemoveRange(deletedTranscripts);
                context.SaveChanges();
                LoggingService.Info($"HistoryService: Deleted {deletedTranscripts.Count} transcripts");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error("HistoryService: Failed to delete transcripts", ex);
                return 0;
            }
        }

        // Delete audio files outside of lock
        foreach (var transcript in deletedTranscripts)
        {
            DeleteTranscriptAudioFiles(transcript);
            TranscriptDeleted?.Invoke(this, transcript.Id);
        }

        return deletedTranscripts.Count;
    }

    // =========================================================================
    // PUBLIC METHODS - AUDIO FILE MANAGEMENT
    // =========================================================================

    /// <summary>
    /// Moves an audio file from a temporary location to permanent storage.
    /// Called when recording stops to preserve the audio for history/retry.
    ///
    /// DEFENSIVE BEHAVIOR:
    /// - Retries up to 10 times with 50ms delay if file is locked
    /// - Falls back to using temp path if move ultimately fails
    /// - Never returns a path to a non-existent file
    /// </summary>
    /// <param name="tempPath">Path to the temporary audio file</param>
    /// <returns>Path to the audio file (permanent if move succeeded, temp if failed)</returns>
    public string SaveAudioFile(string tempPath)
    {
        var audioFolder = _storageService.GetRecordingsFolder();
        if (!string.IsNullOrEmpty(_storageService.ValidationError))
        {
            LoggingService.Warn($"HistoryService: Using fallback recordings folder due to: {_storageService.ValidationError}");
        }

        var fileName = $"{Guid.NewGuid()}.wav";
        var permanentPath = Path.Combine(audioFolder, fileName);

        // Retry with delay in case file is still locked
        // This is a defensive measure - the primary fix is in AudioRecorderService
        const int maxRetries = 3;
        const int retryDelayMs = 10;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Move(tempPath, permanentPath);
                LoggingService.Debug($"HistoryService: Saved audio to {permanentPath}");
                return permanentPath;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                // File might still be locked, wait and retry
                LoggingService.Debug($"HistoryService: Move attempt {attempt} failed, retrying in {retryDelayMs}ms: {ex.Message}");
                Thread.Sleep(retryDelayMs);
            }
            catch (Exception ex)
            {
                // Non-IOException or final attempt - try copy as fallback
                LoggingService.Error($"HistoryService: Move failed on attempt {attempt}: {ex.Message}");

                try
                {
                    File.Copy(tempPath, permanentPath, overwrite: true);
                    LoggingService.Debug($"HistoryService: Copied audio to {permanentPath} (move failed)");
                    try { File.Delete(tempPath); } catch { }
                    return permanentPath;
                }
                catch (Exception copyEx)
                {
                    LoggingService.Error($"HistoryService: Copy also failed: {copyEx.Message}");
                    // Fall through to use temp path
                    break;
                }
            }
        }

        // All attempts failed - use the temp path as-is
        // This ensures we never return a path to a non-existent file
        LoggingService.Warn($"HistoryService: Using temp path as fallback: {tempPath}");
        return tempPath;
    }

    /// <summary>
    /// Deletes an audio file from disk if it exists.
    /// </summary>
    public void DeleteAudioFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (!IsDeletableAudioPath(path))
            {
                LoggingService.Warn($"HistoryService: Skipping audio deletion outside trusted recording roots: {path}");
                return;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
                LoggingService.Debug($"HistoryService: Deleted audio file {path}");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"HistoryService: Failed to delete audio file {path}: {ex.Message}");
        }
    }

    public static bool IsDeletableAudioPath(string? path) => IsTrustedAudioPath(path);

    /// <summary>
    /// Canonicalizes <paramref name="path"/> and reports whether it resolves to a
    /// location HyperWhisper itself owns: a configured/legacy/temp recordings root,
    /// or a temp recording-fallback file. Used both to guard audio deletion and to
    /// contain the Local API <c>/transcribe</c> <c>file</c> field so a same-user
    /// token holder cannot point the app at arbitrary readable files
    /// (confused-deputy file read / cloud exfiltration). The public deletion guard
    /// keeps the existing lexical semantics; Local API callers can pass a resolver
    /// so opened real paths still match trusted roots that are themselves reparsed.
    /// </summary>
    public static bool IsTrustedAudioPath(string? path) => IsTrustedAudioPath(path, resolveTrustedRoot: null);

    internal static bool IsTrustedAudioPath(string? path, Func<string, string>? resolveTrustedRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (IsTempRecordingFallbackFile(fullPath))
            {
                return true;
            }

            return GetTrustedAudioRoots()
                .Any(root =>
                    IsPathUnderDirectory(fullPath, root)
                    || IsPathUnderResolvedDirectory(fullPath, root, resolveTrustedRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            LoggingService.Warn($"HistoryService: Invalid audio path '{path}': {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<string> GetTrustedAudioRoots()
    {
        yield return StorageService.Instance.GetRecordingsFolder();
        yield return SettingsService.GetLegacyAudioFolder();
        yield return GetTempRecordingsRoot();
    }

    private static string GetTempRecordingsRoot()
    {
        return AppPaths.IsAppDataRootOverridden
            ? AppPaths.ProfileTempRecordingsDirectory
            : Path.Combine(Path.GetTempPath(), "HyperWhisper", "recordings");
    }

    private static bool IsTempRecordingFallbackFile(string fullPath)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath);

        return string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(tempRoot), StringComparison.OrdinalIgnoreCase)
            && fileName.StartsWith("hyperwhisper_", StringComparison.OrdinalIgnoreCase)
            && string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderDirectory(string fullPath, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        var fullDirectory = Path.GetFullPath(directory);
        var directoryWithSeparator = Path.TrimEndingDirectorySeparator(fullDirectory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderResolvedDirectory(
        string fullPath,
        string directory,
        Func<string, string>? resolveTrustedRoot)
    {
        if (resolveTrustedRoot == null || string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            var resolvedDirectory = resolveTrustedRoot(Path.GetFullPath(directory));
            return IsPathUnderDirectory(fullPath, resolvedDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void DeleteTranscriptAudioFiles(Transcript transcript)
    {
        foreach (var path in new[] { transcript.AudioFilePath, transcript.TrimmedAudioFilePath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteAudioFile(path);
        }
    }

    /// <summary>
    /// Gets the path to the audio folder.
    /// </summary>
    public static string GetAudioFolder() => StorageService.Instance.GetRecordingsFolder();

}

/// <summary>
/// DATE FILTER
///
/// Options for filtering transcripts by date range.
/// Matches the macOS app's filter options.
/// </summary>
public enum DateFilter
{
    /// <summary>Show all transcripts (no date filter).</summary>
    All,

    /// <summary>Show only transcripts from today.</summary>
    Today,

    /// <summary>Show transcripts from the current week (Sunday to Saturday).</summary>
    ThisWeek,

    /// <summary>Show transcripts from the current month.</summary>
    ThisMonth
}
