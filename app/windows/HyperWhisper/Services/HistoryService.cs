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

    /// <summary>
    /// Rows the database refused to insert (#1589), kept outside it until the next
    /// successful write or the next launch, plus the write-ahead copy of each insert
    /// still running (hidden from readers).
    /// </summary>
    private PendingHistoryStore? _pendingStore;

    /// <summary>
    /// The store is thread-safe on its own lock, so the write-ahead and the statistics
    /// read reach it without <see cref="_lock"/> (which an insert can hold for the 30 s
    /// busy wait). Everything else still uses it under <see cref="_lock"/>.
    /// </summary>
    private PendingHistoryStore Pending
    {
        get
        {
            var store = Volatile.Read(ref _pendingStore);
            if (store != null) return store;
            Interlocked.CompareExchange(ref _pendingStore, new PendingHistoryStore(PendingHistoryStore.DefaultPath), null);
            return Volatile.Read(ref _pendingStore)!;
        }
    }

    /// <summary>
    /// What a row left Processing by a dead process says. Shared by the startup recovery
    /// and the pending store, which applies it to records it reads back from disk.
    /// </summary>
    internal const string InterruptedReason = "Interrupted — app was restarted while processing";

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
            var rows = context.Transcripts
                .OrderByDescending(t => t.Date)
                .ToList();

            // A row the database refused to insert (#1589) is still a History row.
            if (Pending.Count == 0) return rows;
            var known = rows.Select(t => t.Id).ToHashSet();
            rows.AddRange(Pending.GetAll().Where(t => !known.Contains(t.Id)));
            return rows.OrderByDescending(t => t.Date).ToList();
        }
    }

    /// <summary>
    /// The pending rows (#1589) that pass <paramref name="match"/> and are not database
    /// rows already (a stale record a flush has not dropped yet: the database wins).
    /// Every reader merges these with its database rows, so a row the database refused
    /// is in every list, count and sweep until it is saved. Caller holds <see cref="_lock"/>.
    /// </summary>
    private List<Transcript> PendingRowsNotInDatabase(HyperWhisperDbContext context, Func<Transcript, bool> match)
    {
        if (Pending.Count == 0) return new List<Transcript>();
        var rows = Pending.GetAll().Where(match).ToList();
        if (rows.Count == 0) return rows;
        var ids = rows.Select(r => r.Id).ToList();
        var inDatabase = context.Transcripts.AsNoTracking()
            .Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToHashSet();
        return inDatabase.Count == 0 ? rows : rows.Where(r => !inDatabase.Contains(r.Id)).ToList();
    }

    /// <summary>
    /// The pending rows (#1589) for <see cref="WindowsStatisticsTranscriptProvider"/>. Read
    /// from the thread-safe store without <see cref="_lock"/>, so the statistics never wait
    /// on an insert. Read it BEFORE the database rows and drop the ids the database
    /// returned: a row flushed in between is then counted once, never zero times.
    /// </summary>
    internal List<Transcript> GetPendingTranscriptsSnapshot() => Pending.GetAll();

    /// <summary>
    /// One Local API <c>/recordings</c> page, filtered, counted and limited in SQL
    /// rather than over the whole table (issue #1123). The match rule is the one the
    /// endpoint always had: <c>Contains(q, OrdinalIgnoreCase)</c> over Text,
    /// PostProcessedText and TranscribedText. SQLite LIKE folds ASCII case only, so a
    /// non-ASCII rune becomes <c>_</c> (a superset) and the old check narrows it in
    /// memory. A term holding NUL skips LIKE (SQLite cuts the pattern there, #1198).
    /// </summary>
    public (List<Transcript> Page, int Total) QueryPage(string? q, DateTime? since, DateTime? until, int limit)
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();
            var rows = context.Transcripts.AsNoTracking();
            if (since is { } from) rows = rows.Where(t => t.Date >= from);
            if (until is { } to) rows = rows.Where(t => t.Date <= to);
            var hasTerm = !string.IsNullOrEmpty(q);
            var exactInSql = !hasTerm || !q!.Contains('\0');
            if (hasTerm && exactInSql)
            {
                var pattern = new System.Text.StringBuilder("%");
                foreach (var rune in q!.EnumerateRunes())
                {
                    if (!rune.IsAscii) { exactInSql = false; pattern.Append('_'); continue; }
                    if (rune.Value is '%' or '_' or '\\') pattern.Append('\\');
                    pattern.Append((char)rune.Value);
                }
                var like = pattern.Append('%').ToString();
                rows = rows.Where(t => EF.Functions.Like(t.Text, like, "\\")
                    || EF.Functions.Like(t.PostProcessedText, like, "\\")
                    || EF.Functions.Like(t.TranscribedText, like, "\\"));
            }
            // With a term, order by an expression IX_Transcripts_Date cannot serve, so
            // SQLite scans with a top-N sort instead of a slower index walk (as #1195).
            rows = hasTerm
                ? rows.OrderByDescending(t => EF.Functions.Collate(t.Date, "RTRIM"))
                : rows.OrderByDescending(t => t.Date);
            // Rows the database refused to insert (#1589), filtered by the same rule in
            // memory, then merged into the page and the total.
            var pending = PendingRowsNotInDatabase(context, t =>
                (since is not { } f || t.Date >= f) &&
                (until is not { } u || t.Date <= u) &&
                (!hasTerm || MatchesTerm(t, q!)));
            if (exactInSql)
            {
                var page = rows.Take(limit).ToList();
                var total = page.Count < limit ? page.Count : rows.Count();
                return WithPending(page, total, pending, limit);
            }
            var matches = rows.AsEnumerable().Where(t => MatchesTerm(t, q!)).ToList();
            return WithPending(matches.Take(limit).ToList(), matches.Count, pending, limit);
        }
    }

    /// <summary>The <see cref="QueryPage"/> match rule, in memory.</summary>
    private static bool MatchesTerm(Transcript t, string q) =>
        (t.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (t.PostProcessedText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (t.TranscribedText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Merges matching pending rows into a newest-first database page. The page holds the
    /// database's top <paramref name="limit"/>, so the top of the union is in it or in
    /// <paramref name="pending"/>; the total counts both.
    /// </summary>
    private static (List<Transcript> Page, int Total) WithPending(
        List<Transcript> page, int total, List<Transcript> pending, int limit)
    {
        if (pending.Count == 0) return (page, total);
        var merged = page.Concat(pending).OrderByDescending(t => t.Date).Take(limit).ToList();
        return (merged, total + pending.Count);
    }

    /// <summary>
    /// Gets a transcript by ID.
    /// </summary>
    public Transcript? GetTranscript(Guid id)
    {
        lock (_lock)
        {
            // A pending id is never also a row (#1589), so this answers without the database.
            if (Pending.Get(id) is { } pending) return pending;

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
                var rows = context.Transcripts
                    .Where(t => t.Date < cutoffDate)
                    .OrderBy(t => t.Date)  // Oldest first
                    .ToList();

                // A pending row (#1589) past the cutoff is swept too; DeleteTranscripts drops it.
                var pending = PendingRowsNotInDatabase(context, t => t.Date < cutoffDate);
                return pending.Count == 0 ? rows : rows.Concat(pending).OrderBy(t => t.Date).ToList();
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
            string? lowerQuery = null;
            if (!string.IsNullOrWhiteSpace(query))
            {
                lowerQuery = query.ToLowerInvariant();
                queryable = queryable.Where(t => t.Text.ToLower().Contains(lowerQuery));
            }

            // Apply date filter (UTC comparisons); DateFilter.All has no bounds
            var (start, end) = DateFilterBounds(filter, DateTime.UtcNow);
            if (start is { } from && end is { } to)
            {
                queryable = queryable.Where(t => t.Date >= from && t.Date < to);
            }

            var rows = queryable
                .OrderByDescending(t => t.Date)
                .ToList();

            // Rows the database refused to insert (#1589), by the same filters.
            var pending = PendingRowsNotInDatabase(context, t =>
                (lowerQuery == null || (t.Text ?? "").ToLowerInvariant().Contains(lowerQuery)) &&
                (start is not { } s || (t.Date >= s && t.Date < end)));
            return pending.Count == 0 ? rows : rows.Concat(pending).OrderByDescending(t => t.Date).ToList();
        }
    }

    /// <summary>The [start, end) UTC window of a <see cref="DateFilter"/>; nulls for All.</summary>
    private static (DateTime? Start, DateTime? End) DateFilterBounds(DateFilter filter, DateTime now)
    {
        switch (filter)
        {
            case DateFilter.Today:
                var todayStart = now.Date;
                return (todayStart, todayStart.AddDays(1));

            case DateFilter.ThisWeek:
                var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);
                return (weekStart, weekStart.AddDays(7));

            case DateFilter.ThisMonth:
                var monthStart = new DateTime(now.Year, now.Month, 1);
                return (monthStart, monthStart.AddMonths(1));

            default:
                return (null, null);
        }
    }

    // =========================================================================
    // PUBLIC METHODS - CREATE
    // =========================================================================

    /// <summary>
    /// Creates a new transcript in Processing status.
    /// Called immediately when recording stops, before transcription begins.
    /// This allows the transcript to appear in the history view right away.
    ///
    /// Never throws for a refused write: when the database will not take the row (another
    /// process holds its write lock past the 30 s busy timeout, #1589) the row is kept in
    /// the pending store and written on the next successful write or the next launch.
    /// The UI thread must use <see cref="CreateProcessingTranscriptAsync"/>, which waits
    /// for the lock on the thread pool.
    /// </summary>
    /// <param name="duration">Recording duration in seconds</param>
    /// <param name="modeName">Name of the mode used</param>
    /// <param name="audioFilePath">Path to the audio file, or null for streaming sessions without saved audio</param>
    /// <returns>The created transcript</returns>
    /// <remarks>
    /// Internal (#1589): it waits for the lock on the calling thread, so no UI caller may
    /// use it. HyperWhisper.SmokeTests seeds rows with it.
    /// </remarks>
    internal Transcript CreateProcessingTranscript(double duration, string? modeName, string? audioFilePath) =>
        InsertProcessingTranscript(NewProcessingTranscript(duration, modeName, audioFilePath)).Transcript;

    /// <summary>
    /// <see cref="CreateProcessingTranscript"/> off the calling thread (#1589). The insert
    /// can wait the whole SQLite busy timeout, so the four stop paths await this instead of
    /// blocking the dispatcher. The row is built here, on the caller's thread, so its
    /// placeholder text uses the caller's UI culture.
    /// </summary>
    public Task<HistoryInsertResult> CreateProcessingTranscriptAsync(double duration, string? modeName, string? audioFilePath)
    {
        var transcript = NewProcessingTranscript(duration, modeName, audioFilePath);
        return Task.Run(() => InsertProcessingTranscript(transcript));
    }

    private static Transcript NewProcessingTranscript(double duration, string? modeName, string? audioFilePath) => new()
    {
        Id = Guid.NewGuid(),
        Date = DateTime.UtcNow,
        Duration = duration,
        Mode = modeName,
        AudioFilePath = audioFilePath,
        Status = TranscriptStatus.Processing,
        Text = Loc.S("recording.state.processing")
    };

    internal HistoryInsertResult InsertProcessingTranscript(Transcript transcript)
    {
        var savedLater = false;
        var databaseLocked = false;

        // WRITE-AHEAD (#1589): the row reaches pending-history.json before the insert can
        // wait for the lock, and before this thread queues on _lock behind another write.
        // An app closed mid-wait (OnExit gives the flush only 5 s) leaves it on disk and
        // the next launch saves it, Failed "Interrupted". Hidden from readers until the
        // insert ends. Costs one small file write and one delete per dictation.
        if (!Pending.BeginWriteAhead(transcript))
        {
            LoggingService.Warn($"HistoryService: Could not write ahead transcript {transcript.Id}; inserting without it");
        }

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();
                context.Transcripts.Add(transcript);
                context.SaveChanges();
                LoggingService.Info($"HistoryService: Created processing transcript {transcript.Id}");
                // Committed: the record goes. If the remove never reaches the file, the
                // next flush finds the row in the database and drops it (the database wins).
                Pending.EndWriteAhead(transcript.Id, inserted: true);
                FlushPendingLocked();
            }
            catch (Exception ex) when (IsRefusedWrite(ex))
            {
                // #1589: this used to rethrow, which aborted the job ("An error occurred
                // while saving the entity changes") and left the copied audio an orphan
                // no row points to. Keep the row instead; the caller transcribes as usual.
                databaseLocked = IsDatabaseBusy(ex);
                LoggingService.Error(
                    $"HistoryService: Failed to create transcript {transcript.Id} ({(databaseLocked ? "database locked" : "write refused")}); kept it to save later", ex);
                // The written-ahead record becomes an ordinary pending row.
                if (!Pending.EndWriteAhead(transcript.Id, inserted: false))
                {
                    LoggingService.Error(
                        $"HistoryService: Transcript {transcript.Id} is held in memory only (pending-history.json could not be written); it is lost if the app exits before the database takes it");
                }
                savedLater = true;
            }
            catch
            {
                // Not a refusal: the caller aborts as before, with no row. Drop the record
                // so the next launch does not save it as an "Interrupted" row.
                Pending.EndWriteAhead(transcript.Id, inserted: true);
                throw;
            }
        }

        // Fire event outside lock to prevent deadlock. A pending row is announced too:
        // History shows it, and every later write to it lands in the pending store.
        TranscriptAdded?.Invoke(this, transcript);
        return new HistoryInsertResult(transcript, savedLater, databaseLocked);
    }

    // =========================================================================
    // PUBLIC METHODS - PENDING ROWS (#1589)
    // =========================================================================

    /// <summary>
    /// Writes the rows the database refused earlier. Called at startup, before
    /// <see cref="RecoverOrphanedProcessingTranscripts"/>; every successful write in this
    /// service also tries it. Returns how many rows landed.
    /// </summary>
    public int FlushPendingTranscripts()
    {
        lock (_lock)
        {
            return FlushPendingLocked();
        }
    }

    /// <summary>The number of rows waiting to be written. For diagnostics and tests.</summary>
    internal int PendingTranscriptCount
    {
        get
        {
            lock (_lock)
            {
                return Pending.Count;
            }
        }
    }

    /// <summary>
    /// Points the service at another pending store and returns the old one. Lets
    /// HyperWhisper.SmokeTests read a store back from disk, as the next launch does.
    /// </summary>
    internal PendingHistoryStore ReplacePendingStoreForTests(PendingHistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_lock)
        {
            var previous = Pending;
            _pendingStore = store;
            return previous;
        }
    }

    /// <summary>
    /// Inserts every pending row in one SaveChanges. Never throws: a refusal keeps the
    /// rows for the next try. Caller holds <see cref="_lock"/>.
    /// A pending id that is already a database row is dropped, the database wins: the
    /// store only ever takes rows that were never inserted, so such a record is a stale
    /// file an earlier flush committed but could not delete, and the row may since have
    /// been completed or edited. Overwriting it would lose the user's text.
    /// </summary>
    private int FlushPendingLocked()
    {
        try
        {
            if (Pending.Count == 0) return 0;

            var records = Pending.GetAll();
            var ids = records.Select(r => r.Id).ToList();
            using var context = new HyperWhisperDbContext();
            var existing = context.Transcripts.AsNoTracking()
                .Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToHashSet();
            var toAdd = records.Where(r => !existing.Contains(r.Id)).ToList();
            if (existing.Count > 0)
            {
                LoggingService.Warn($"HistoryService: Dropped {existing.Count} stale pending transcript(s) already in History; kept the database rows");
            }

            context.Transcripts.AddRange(toAdd);
            context.SaveChanges();
            Pending.Remove(ids);
            LoggingService.Info($"HistoryService: Saved {toAdd.Count} pending transcript(s) to History");
            return toAdd.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"HistoryService: Pending transcripts not saved yet ({(IsDatabaseBusy(ex) ? "database locked" : ex.GetType().Name)}); will retry on the next write");
            return 0;
        }
    }

    /// <summary>
    /// SQLITE_BUSY (5) or SQLITE_LOCKED (6) anywhere in the chain: another connection
    /// held the lock past the busy timeout. Picks the toast that names the lock.
    /// </summary>
    internal static bool IsDatabaseBusy(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 })
                return true;
        }
        return false;
    }

    /// <summary>Applies the restart-recovery failure to a row left Processing.</summary>
    internal static void MarkInterrupted(Transcript t)
    {
        t.Status = TranscriptStatus.Failed;
        t.FailedReason = InterruptedReason;
        // Replace the "Processing audio..." placeholder text so the
        // history row shows something useful instead of a stuck spinner label.
        if (string.IsNullOrWhiteSpace(t.Text) ||
            t.Text == Loc.S("recording.state.processing"))
        {
            t.Text = InterruptedReason;
        }
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

                foreach (var t in recovered)
                {
                    MarkInterrupted(t);
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
    /// <param name="keepLiveAudioPath">
    /// When the caller's audio path names a file that is gone and the row's own path names
    /// one that exists, keep the row's path and copy it onto <paramref name="transcript"/>.
    /// The terminal-status safety net passes true: its in-memory copy can still hold the
    /// WAV a background compression already deleted after re-pointing the row at the M4A
    /// (#1603). Decided under the lock, so it cannot race <see cref="UpdateAudioFilePath"/>.
    /// </param>
    public void UpdateTranscript(Transcript transcript, bool keepLiveAudioPath = false)
    {
        lock (_lock)
        {
            // The row the database refused to insert (#1589): the update lands in the
            // pending store, without touching the database (it may still be locked).
            if (Pending.Get(transcript.Id) is { } pending)
            {
                if (keepLiveAudioPath)
                    KeepLiveAudioPath(transcript, pending.AudioFilePath);

                Pending.Upsert(transcript);
                LoggingService.Info($"HistoryService: Updated pending transcript {transcript.Id} (Status: {transcript.Status})");
            }
            else
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

                    if (keepLiveAudioPath)
                        KeepLiveAudioPath(transcript, existing.AudioFilePath);

                    context.Entry(existing).CurrentValues.SetValues(transcript);
                    context.SaveChanges();
                    LoggingService.Info($"HistoryService: Updated transcript {transcript.Id} (Status: {transcript.Status})");
                    FlushPendingLocked();
                }
                catch (DbUpdateException ex)
                {
                    LoggingService.Error($"HistoryService: Failed to update transcript {transcript.Id}", ex);
                    throw;
                }
            }
        }

        // Fire event outside lock to prevent deadlock
        TranscriptUpdated?.Invoke(this, transcript);
    }

    /// <summary>
    /// The <c>keepLiveAudioPath</c> rule of <see cref="UpdateTranscript"/>: when the caller's
    /// audio file is gone and the stored row's file exists, keep the stored path (#1603).
    /// </summary>
    private static void KeepLiveAudioPath(Transcript transcript, string? storedAudioFilePath)
    {
        if (string.IsNullOrEmpty(storedAudioFilePath) ||
            storedAudioFilePath == transcript.AudioFilePath ||
            File.Exists(transcript.AudioFilePath) ||
            !File.Exists(storedAudioFilePath))
            return;

        LoggingService.Info($"HistoryService: Kept the live audio path of transcript {transcript.Id}; the caller's file is gone");
        transcript.AudioFilePath = storedAudioFilePath;
    }

    /// <summary>
    /// Points an existing row at new audio (the M4A a background compression just wrote)
    /// and writes nothing else, so a text edit made while the compression ran is kept.
    /// Safe on any thread: the event carries the freshly-read row, never a caller's live
    /// instance, and the History page applies it on the dispatcher (#1499).
    /// </summary>
    /// <returns>False when no row has that id (the user deleted it meanwhile).</returns>
    public bool UpdateAudioFilePath(Guid id, string audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
            throw new ArgumentException("An audio path is required.", nameof(audioFilePath));

        Transcript updated;
        lock (_lock)
        {
            if (Pending.Get(id) is { } pending)
            {
                // #1589: the compressed audio belongs to a row still waiting to be saved.
                pending.AudioFilePath = audioFilePath;
                Pending.Upsert(pending);
                updated = pending;
                LoggingService.Info($"HistoryService: Updated audio path of pending transcript {id}");
            }
            else
            {
                try
                {
                    using var context = new HyperWhisperDbContext();

                    var existing = context.Transcripts.Find(id);
                    if (existing == null)
                    {
                        LoggingService.Info($"HistoryService: Transcript {id} is gone; not pointing it at new audio");
                        return false;
                    }

                    existing.AudioFilePath = audioFilePath;
                    context.SaveChanges();
                    updated = existing;
                    LoggingService.Info($"HistoryService: Updated audio path of transcript {id}");
                    FlushPendingLocked();
                }
                catch (DbUpdateException ex)
                {
                    LoggingService.Error($"HistoryService: Failed to update audio path of transcript {id}", ex);
                    throw;
                }
            }
        }

        // Fire event outside lock to prevent deadlock
        TranscriptUpdated?.Invoke(this, updated);
        return true;
    }

    // =========================================================================
    // PUBLIC METHODS - DELETE
    // =========================================================================

    /// <summary>
    /// Deletes a single transcript and its associated audio file.
    /// Success(true): deleted. Success(false): no transcript has that id (not an
    /// error). Failure: SQLite refused the delete, or a pending row's file could not be
    /// rewritten (#2126), so the row and its audio are still there; the Error is the
    /// localized <c>transcripts.delete.failed</c> copy and Exception holds the cause (#974).
    /// </summary>
    public Result<bool> DeleteTranscript(Guid id)
    {
        Transcript? transcript = null;

        lock (_lock)
        {
            if (Pending.Get(id) is { } pending)
            {
                // Never written (#1589): dropping the pending record is the whole delete.
                // A pending file that could not be rewritten still holds it, and the next
                // launch would save it again (#2126): keep the record and its audio.
                if (!Pending.Delete(new[] { id }))
                {
                    LoggingService.Error($"HistoryService: Failed to delete pending transcript {id}; the pending file could not be written");
                    return Result<bool>.Failure(Loc.S("transcripts.delete.failed"),
                        new IOException($"Could not rewrite {PendingHistoryStore.FileName}"));
                }
                transcript = pending;
                LoggingService.Info($"HistoryService: Deleted pending transcript {id}");
            }
            else
            {
                try
                {
                    using var context = new HyperWhisperDbContext();

                    transcript = context.Transcripts.Find(id);
                    if (transcript == null)
                    {
                        LoggingService.Warn($"HistoryService: Transcript {id} not found for deletion");
                        return Result<bool>.Success(false);
                    }

                    context.Transcripts.Remove(transcript);
                    context.SaveChanges();
                    LoggingService.Info($"HistoryService: Deleted transcript {id}");
                }
                catch (Exception ex) when (IsRefusedWrite(ex))
                {
                    // A plain false here read as "deleted" to every caller, so a delete
                    // SQLite refused ("database is locked") was reported as a success
                    // (#974). Log the id and the exception only, never transcript text.
                    LoggingService.Error($"HistoryService: Failed to delete transcript {id}", ex);
                    return Result<bool>.Failure(Loc.S("transcripts.delete.failed"), ex);
                }
            }
        }

        // The row is gone from here on, so nothing below may turn this into a
        // failure: a caller that saw one would treat a deleted row as still there.
        // Delete audio files outside of lock (DeleteAudioFile logs its own errors).
        DeleteTranscriptAudioFiles(transcript);
        // Fire event outside lock to prevent deadlock
        RaiseTranscriptDeleted(id);
        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Deletes multiple transcripts and their associated audio files.
    /// More efficient than calling DeleteTranscript multiple times.
    /// Success(n): n rows deleted (0 when none of the ids exist). Failure: SQLite
    /// refused the delete, or the pending file could not be rewritten without the
    /// pending rows among them (#2126). Either way nothing was deleted (#974).
    /// </summary>
    public Result<int> DeleteTranscripts(IEnumerable<Guid> ids)
    {
        var idSet = ids.ToHashSet();
        List<Transcript> deletedTranscripts;

        lock (_lock)
        {
            var pending = Pending.GetAll().Where(t => idSet.Contains(t.Id)).ToList();
            try
            {
                using var context = new HyperWhisperDbContext();
                // One transaction around both parts (#2126): the database delete is staged,
                // the pending rows go only when their file is rewritten without them, and
                // only then does the database part commit. A refusal on either side
                // deletes nothing; only a failed commit has rows to put back.
                using var transaction = context.Database.BeginTransaction();

                deletedTranscripts = context.Transcripts
                    .Where(t => idSet.Contains(t.Id))
                    .ToList();

                if (deletedTranscripts.Count > 0)
                {
                    context.Transcripts.RemoveRange(deletedTranscripts);
                    context.SaveChanges();
                }

                // Rows the database never took (#1589): the old file would bring them
                // back at the next launch, after their audio was deleted.
                if (pending.Count > 0 && !Pending.Delete(pending.Select(t => t.Id)))
                {
                    LoggingService.Error($"HistoryService: Failed to delete {pending.Count} pending transcripts; the pending file could not be written");
                    return Result<int>.Failure(Loc.S("transcripts.delete.failed"),
                        new IOException($"Could not rewrite {PendingHistoryStore.FileName}"));
                }

                try
                {
                    transaction.Commit();
                }
                catch
                {
                    // The pending rows already left the file; the database rows are still
                    // there, so they come back too and the sweep deletes nothing.
                    if (pending.Count > 0 && !Pending.Restore(pending))
                        LoggingService.Error($"HistoryService: {pending.Count} pending transcripts are held in memory only; the pending file could not be written");
                    throw;
                }

                if (deletedTranscripts.Count > 0)
                    LoggingService.Info($"HistoryService: Deleted {deletedTranscripts.Count} transcripts");
            }
            catch (Exception ex) when (IsRefusedWrite(ex))
            {
                // A 0 here stamped a refused Delete Now sweep as a completed cleanup
                // and left History rows half-deleted (#974). Log the count and the
                // exception only: a sweep can hold thousands of ids, and never text.
                LoggingService.Error($"HistoryService: Failed to delete {idSet.Count} transcripts", ex);
                return Result<int>.Failure(Loc.S("transcripts.delete.failed"), ex);
            }

            if (pending.Count > 0)
            {
                deletedTranscripts.AddRange(pending);
                LoggingService.Info($"HistoryService: Deleted {pending.Count} pending transcripts");
            }

            if (deletedTranscripts.Count == 0)
            {
                return Result<int>.Success(0);
            }
        }

        // Committed: as in DeleteTranscript, nothing below may report a failure.
        foreach (var transcript in deletedTranscripts)
        {
            DeleteTranscriptAudioFiles(transcript);
            RaiseTranscriptDeleted(transcript.Id);
        }

        return Result<int>.Success(deletedTranscripts.Count);
    }

    /// <summary>
    /// The database said no: SaveChanges failed (<see cref="DbUpdateException"/>), or
    /// the lookup before it hit a locked or broken database
    /// (<see cref="Microsoft.Data.Sqlite.SqliteException"/>). Both are expected
    /// runtime failures, so they become a failed Result rather than a throw.
    /// </summary>
    private static bool IsRefusedWrite(Exception ex) =>
        ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException;

    /// <summary>
    /// Raises <see cref="TranscriptDeleted"/> to each subscriber on its own. It runs
    /// after the delete committed, so a subscriber that throws is logged and must
    /// not reach the caller: it would read a deleted row as a failed delete (the
    /// cancel paths would then re-mark the deleted row terminal).
    /// </summary>
    private void RaiseTranscriptDeleted(Guid id)
    {
        var handlers = TranscriptDeleted;
        if (handlers == null) return;

        foreach (EventHandler<Guid> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, id);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"HistoryService: TranscriptDeleted subscriber failed for transcript {id}", ex);
            }
        }
    }

    // =========================================================================
    // PUBLIC METHODS - AUDIO FILE MANAGEMENT
    // =========================================================================

    /// <summary>
    /// Saves an audio file to permanent storage.
    /// Called when recording stops to preserve the audio for history/retry.
    ///
    /// DEFENSIVE BEHAVIOR:
    /// - Retries up to 10 times with 50ms delay if file is locked
    /// - Falls back to using temp path if move ultimately fails
    /// - Never returns a path to a non-existent file
    /// </summary>
    /// <param name="tempPath">Path to the audio file</param>
    /// <param name="ownsSource">Whether the caller owns the source and permits moving/deleting it</param>
    /// <returns>Path to the audio file (permanent if move succeeded, temp if failed)</returns>
    public string SaveAudioFile(string tempPath, bool ownsSource = true)
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
                PersistAudioFile(tempPath, permanentPath, ownsSource);
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
                    if (ownsSource)
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
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

    internal static void PersistAudioFile(string sourcePath, string destinationPath, bool ownsSource)
    {
        if (ownsSource)
        {
            File.Move(sourcePath, destinationPath);
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite: false);
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

    internal static IEnumerable<string> GetTrustedAudioRoots()
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

    internal static bool IsTempRecordingFallbackFile(string fullPath)
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

/// <summary>
/// What <see cref="HistoryService.CreateProcessingTranscriptAsync"/> did (#1589).
/// <paramref name="SavedLater"/>: the database refused the insert, so the row waits in
/// the pending store; <paramref name="DatabaseLocked"/>: it refused because another
/// process held its write lock. The transcript is usable either way.
/// </summary>
public sealed record HistoryInsertResult(Transcript Transcript, bool SavedLater, bool DatabaseLocked);
