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
/// NOT THREAD-SAFE on its own: every call is made under HistoryService's lock.
/// The file is only a durability copy; the in-memory list is authoritative for this
/// process, and an id held here is never also a row in the database.
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
    private List<Transcript>? _records;

    /// <summary>
    /// The last file write failed, so the file does not match the list. Every later
    /// Upsert/Remove rewrites the whole list, which retries it.
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

    public int Count => Records.Count;

    /// <summary>A copy of the pending record, or null when the id is not pending.</summary>
    public Transcript? Get(Guid id)
    {
        var record = Records.FirstOrDefault(r => r.Id == id);
        return record == null ? null : Clone(record);
    }

    /// <summary>Copies of every pending record, oldest first.</summary>
    public List<Transcript> GetAll() => Records.Select(Clone).ToList();

    /// <summary>
    /// Adds the record, or replaces the one with the same id, and saves the file.
    /// Returns false when the file could not be written: the record is then held in
    /// memory only, and a crash or exit before the database takes it loses it.
    /// </summary>
    public bool Upsert(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var records = Records;
        var index = records.FindIndex(r => r.Id == transcript.Id);
        var copy = Clone(transcript);
        if (index >= 0)
            records[index] = copy;
        else
            records.Add(copy);
        return Save();
    }

    /// <summary>Removes the records with these ids and saves the file. Returns how many went.</summary>
    public int Remove(IEnumerable<Guid> ids)
    {
        var idSet = ids.ToHashSet();
        var removed = Records.RemoveAll(r => idSet.Contains(r.Id));
        if (removed > 0 || _saveFailed) Save();
        return removed;
    }

    public bool Remove(Guid id) => Remove(new[] { id }) > 0;

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

    /// <summary>Writes the list (or deletes the file when it is empty). Never throws.</summary>
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
