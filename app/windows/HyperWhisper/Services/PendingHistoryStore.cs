using System.IO;
using System.Text.Json;
using HyperWhisper.Data.Entities;

namespace HyperWhisper.Services;

/// <summary>
/// PENDING HISTORY STORE (#1589)
///
/// History rows the database refused to insert (another process held its write lock
/// past the 30 s busy timeout, or the write failed some other way). They live in a
/// small JSON file next to <c>hyperwhisper.db</c>, outside the locked database, until
/// <see cref="HistoryService"/> writes them on the next successful database write or
/// on the next launch. Updates to such a transcript (the finished text, a failed
/// status, a compressed audio path) land here instead of being lost.
///
/// WRITE-AHEAD: every new History row is written here BEFORE its insert starts
/// (<see cref="BeginWriteAhead"/>) and removed once the insert commits
/// (<see cref="EndWriteAhead"/>). An app that exits while the insert still waits for the
/// lock leaves the record on disk, and the next launch saves it (as Failed, "Interrupted").
/// Until the insert ends, the record is invisible to every in-session reader
/// (<see cref="Count"/>, <see cref="Get"/>, <see cref="GetAll"/>, <see cref="Remove(IEnumerable{Guid})"/>):
/// the caller has not got the row yet, so History, the Local API and the flush must not
/// see it, and the flush must never insert it under its own insert.
///
/// Thread-safe: every member takes the store's own lock, which never waits on the
/// database, so the write-ahead does not queue behind an insert holding HistoryService's
/// lock. The file is only a durability copy; the in-memory list is authoritative for this
/// process, and a visible id held here is never also a row in the database.
/// </summary>
internal sealed class PendingHistoryStore
{
    public const string FileName = "pending-history.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _sync = new();
    private List<Transcript>? _records;

    /// <summary>Ids whose insert is still running: on disk, hidden from every reader.</summary>
    private readonly HashSet<Guid> _writeAhead = new();

    /// <summary>
    /// The last file write failed, so the file does not match the list. Every later
    /// change rewrites the whole list, which retries it.
    /// </summary>
    private bool _saveFailed;

    public PendingHistoryStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A file path is required.", nameof(filePath));
        _filePath = Path.GetFullPath(filePath);
    }

    /// <summary>The store the app uses: <c>pending-history.json</c> in the profile root.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.AppDataRoot, FileName);

    /// <summary>The number of pending rows, write-ahead records excluded.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _writeAhead.Count == 0 ? Records.Count : Records.Count(IsVisible);
            }
        }
    }

    /// <summary>A copy of the pending record, or null when the id is not pending (or only written ahead).</summary>
    public Transcript? Get(Guid id)
    {
        lock (_sync)
        {
            var record = Records.FirstOrDefault(r => r.Id == id && IsVisible(r));
            return record == null ? null : Clone(record);
        }
    }

    /// <summary>Copies of every pending record, oldest first, write-ahead records excluded.</summary>
    public List<Transcript> GetAll()
    {
        lock (_sync)
        {
            return Records.Where(IsVisible).Select(Clone).ToList();
        }
    }

    /// <summary>
    /// Adds the record, or replaces the one with the same id, and saves the file.
    /// Returns false when the file could not be written: the record is then held in
    /// memory only, and a crash or exit before the database takes it loses it.
    /// </summary>
    public bool Upsert(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        lock (_sync)
        {
            Put(transcript);
            return Save();
        }
    }

    /// <summary>
    /// Writes a new row to the file before its insert starts, hidden from readers.
    /// Returns false when the file could not be written.
    /// </summary>
    public bool BeginWriteAhead(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        lock (_sync)
        {
            Put(transcript);
            _writeAhead.Add(transcript.Id);
            return Save();
        }
    }

    /// <summary>
    /// The insert of a written-ahead row ended. <paramref name="inserted"/>: it committed,
    /// so the record goes (a remove that never reaches the file is dropped at the next
    /// flush, the database wins). Otherwise the record becomes an ordinary pending row,
    /// visible to readers. Returns false when the file still does not hold the list.
    /// </summary>
    public bool EndWriteAhead(Guid id, bool inserted)
    {
        lock (_sync)
        {
            if (!_writeAhead.Remove(id)) return !_saveFailed;
            if (inserted)
            {
                Records.RemoveAll(r => r.Id == id);
                return Save();
            }
            return _saveFailed ? Save() : true;
        }
    }

    /// <summary>
    /// Removes the visible records with these ids and saves the file. Returns how many
    /// went. A write-ahead record is never removed here; only its insert ends it.
    /// </summary>
    public int Remove(IEnumerable<Guid> ids)
    {
        var idSet = ids.ToHashSet();
        lock (_sync)
        {
            var removed = Records.RemoveAll(r => idSet.Contains(r.Id) && IsVisible(r));
            if (removed > 0 || _saveFailed) Save();
            return removed;
        }
    }

    public bool Remove(Guid id) => Remove(new[] { id }) > 0;

    /// <summary>
    /// The user's delete of these visible records (#2126): they go only when the file is
    /// rewritten without them. Returns false, and keeps every one of them in memory as it
    /// was, when the file could not be written: the old file still holds them, so dropping
    /// them here would bring them back at the next launch, after their audio was deleted.
    /// </summary>
    public bool Delete(IEnumerable<Guid> ids)
    {
        var idSet = ids.ToHashSet();
        lock (_sync)
        {
            var before = new List<Transcript>(Records);
            if (Records.RemoveAll(r => idSet.Contains(r.Id) && IsVisible(r)) == 0) return true;
            if (Save()) return true;

            _records = before;
            return false;
        }
    }

    /// <summary>
    /// Puts back records a <see cref="Delete"/> took, when the database half of the same
    /// delete did not commit (#2126). One file write. Returns false when the file could
    /// not be written: the records are then held in memory, as after a failed Upsert.
    /// </summary>
    public bool Restore(IEnumerable<Transcript> records)
    {
        lock (_sync)
        {
            foreach (var record in records)
                Put(record);
            return Save();
        }
    }

    private bool IsVisible(Transcript record) => !_writeAhead.Contains(record.Id);

    private void Put(Transcript transcript)
    {
        var records = Records;
        var index = records.FindIndex(r => r.Id == transcript.Id);
        var copy = Clone(transcript);
        if (index >= 0)
            records[index] = copy;
        else
            records.Add(copy);
    }

    /// <summary>
    /// The records read from disk on first use. Anything on disk at that point was left
    /// by an earlier process, so a record still Processing has no live worker behind it.
    /// </summary>
    private List<Transcript> Records => _records ??= Load();

    private List<Transcript> Load()
    {
        List<Transcript> records;
        try
        {
            if (!File.Exists(_filePath))
                return new List<Transcript>();

            var json = File.ReadAllText(_filePath);
            records = (JsonSerializer.Deserialize<List<Transcript>>(json, JsonOptions) ?? new List<Transcript>())
                .Where(r => r != null && r.Id != Guid.Empty)
                .ToList();
            LoggingService.Info($"PendingHistoryStore: Loaded {records.Count} pending History row(s)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Keep the unreadable file for diagnosis rather than overwriting it on the next save.
            LoggingService.Error("PendingHistoryStore: Could not read the pending History file; starting empty", ex);
            try { File.Move(_filePath, _filePath + ".unreadable", overwrite: true); } catch { }
            return new List<Transcript>();
        }

        // Same rule as HistoryService.RecoverOrphanedProcessingTranscripts: the worker
        // that left a record Processing died with its process, so the row must land as
        // Failed (and retryable, its audio path is kept), not as a row that spins forever.
        var interrupted = 0;
        foreach (var record in records.Where(r => r.Status == TranscriptStatus.Processing))
        {
            HistoryService.MarkInterrupted(record);
            interrupted++;
        }

        if (interrupted > 0)
        {
            LoggingService.Warn($"PendingHistoryStore: {interrupted} pending row(s) were still Processing from a previous session; marked Failed");
            Save(records);
        }

        return records;
    }

    private bool Save() => Save(Records);

    /// <summary>
    /// Writes the list (or deletes the file when it is empty). Never throws. Caller holds
    /// <see cref="_sync"/>.
    /// </summary>
    private bool Save(List<Transcript> records)
    {
        try
        {
            if (records.Count == 0)
            {
                if (File.Exists(_filePath)) File.Delete(_filePath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(records, JsonOptions));
                File.Move(tempPath, _filePath, overwrite: true);
            }

            if (_saveFailed)
                LoggingService.Info($"PendingHistoryStore: Saved {_filePath} after an earlier failure");
            _saveFailed = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The in-memory record still reaches the database this session; only a crash
            // before then would lose it. The next Upsert/Remove retries. Log the path,
            // never transcript text.
            _saveFailed = true;
            LoggingService.Error($"PendingHistoryStore: Could not save {_filePath}; will retry on the next change", ex);
            return false;
        }
    }

    /// <summary>A detached copy of the scalar columns. The navigation property is dropped.</summary>
    internal static Transcript Clone(Transcript source) => new()
    {
        Id = source.Id,
        Text = source.Text,
        TranscribedText = source.TranscribedText,
        PostProcessedText = source.PostProcessedText,
        Date = source.Date,
        Duration = source.Duration,
        AudioFilePath = source.AudioFilePath,
        TrimmedAudioFilePath = source.TrimmedAudioFilePath,
        WordTimestampsJson = source.WordTimestampsJson,
        Status = source.Status,
        FailedReason = source.FailedReason,
        TranscriptionProvider = source.TranscriptionProvider,
        PostProcessingProvider = source.PostProcessingProvider,
        Mode = source.Mode,
        ModeId = source.ModeId,
        RecordingSessionId = source.RecordingSessionId,
        RetryCount = source.RetryCount,
        LastRetryDate = source.LastRetryDate,
    };
}
