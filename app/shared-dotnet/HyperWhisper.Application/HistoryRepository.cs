using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public interface ITranscriptionHistoryStore
{
    Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum HistoryRetryStartStatus
{
    Started,
    NotFound,
    NotFailed,
    ConcurrentlyChanged,
}

public sealed record HistoryRetryStartResult(HistoryRetryStartStatus Status, Transcript? Transcript = null)
{
    public bool IsStarted => Status == HistoryRetryStartStatus.Started && Transcript is not null;
}

public interface ITranscriptionRetryStore
{
    Task<HistoryRetryStartResult> TryBeginRetryAsync(
        Guid id,
        DateTime retryDateUtc,
        CancellationToken cancellationToken = default);
}

public sealed class HistoryRepository : ITranscriptionHistoryStore, ITranscriptionRetryStore
{
    private readonly ApplicationDb _database;
    private readonly string? _recordingsRoot;

    public HistoryRepository(ApplicationDb database, IAppPaths? paths = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _recordingsRoot = paths is null ? null : Path.GetFullPath(paths.RecordingsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public async Task<IReadOnlyList<Transcript>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.Transcripts.AsNoTracking()
            .OrderByDescending(item => item.Date)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// One Local API <c>/recordings</c> page, filtered, counted and limited in
    /// SQL rather than over the whole table (issue #1087). The match rule is
    /// the one the endpoint always had: <c>Contains(term, OrdinalIgnoreCase)</c>
    /// over <c>Text</c> and <c>TranscribedText</c>, term not trimmed.
    /// </summary>
    /// <remarks>
    /// SQLite LIKE folds ASCII case only, and under OrdinalIgnoreCase no
    /// non-ASCII character folds onto an ASCII one, so for an ASCII term LIKE
    /// is exact. A non-ASCII rune becomes <c>_</c> (any one character): a
    /// superset that the in-memory check then narrows to the same rows. A term
    /// holding NUL skips LIKE: SQLite cuts the pattern and the value there, so
    /// <c>%\0%</c> matched every row (#1198); the in-memory check alone decides.
    /// </remarks>
    public async Task<(IReadOnlyList<Transcript> Page, int Total)> QueryPageAsync(
        string? search,
        DateTime? since,
        DateTime? until,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var rows = context.Transcripts.AsNoTracking();
        if (since is { } from) rows = rows.Where(item => item.Date >= from);
        if (until is { } to) rows = rows.Where(item => item.Date <= to);
        var hasTerm = !string.IsNullOrWhiteSpace(search);
        var exactInSql = !hasTerm || !search!.Contains('\0');
        if (hasTerm && exactInSql)
        {
            var pattern = new System.Text.StringBuilder("%");
            foreach (var rune in search!.EnumerateRunes())
            {
                if (!rune.IsAscii) { exactInSql = false; pattern.Append('_'); continue; }
                if (rune.Value is '%' or '_' or '\\') pattern.Append('\\');
                pattern.Append((char)rune.Value);
            }
            var like = pattern.Append('%').ToString();
            rows = rows.Where(item => EF.Functions.Like(item.Text, like, "\\")
                || (item.TranscribedText != null && EF.Functions.Like(item.TranscribedText, like, "\\")));
        }
        // With a term, order by an expression IX_Transcripts_Date cannot serve.
        // SQLite otherwise walks that index and looks up every row, about 3x
        // slower for a rare term than a scan with a top-N sort. RTRIM orders a
        // stored date (never a trailing space) exactly as BINARY does.
        rows = string.IsNullOrWhiteSpace(search)
            ? rows.OrderByDescending(item => item.Date)
            : rows.OrderByDescending(item => EF.Functions.Collate(item.Date, "RTRIM"));
        if (exactInSql)
        {
            // A page short of the limit already holds every match, so the
            // second scan (COUNT) only runs when there may be more.
            var page = await rows.Take(limit).ToListAsync(cancellationToken);
            return (page, page.Count < limit ? page.Count : await rows.CountAsync(cancellationToken));
        }

        var matches = (await rows.ToListAsync(cancellationToken))
            .Where(item => item.Text.Contains(search!, StringComparison.OrdinalIgnoreCase)
                || (item.TranscribedText?.Contains(search!, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        return (matches.Take(limit).ToList(), matches.Count);
    }

    /// <summary>True when any history row points at <paramref name="path"/> (ordinal, as SQLite's BINARY collation).</summary>
    public async Task<bool> AnyWithAudioFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.Transcripts.AnyAsync(item => item.AudioFilePath == path, cancellationToken);
    }

    public async Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.Transcripts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        await using var context = _database.CreateContext();
        context.Transcripts.Add(transcript);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        await using var context = _database.CreateContext();
        if (!await context.Transcripts.AnyAsync(item => item.Id == transcript.Id, cancellationToken))
            return false;
        context.Transcripts.Update(transcript);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<HistoryRetryStartResult> TryBeginRetryAsync(
        Guid id,
        DateTime retryDateUtc,
        CancellationToken cancellationToken = default)
    {
        if (retryDateUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Retry timestamp must be UTC.", nameof(retryDateUtc));

        await using var context = _database.CreateContext();
        var candidate = await context.Transcripts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (candidate is null) return new(HistoryRetryStartStatus.NotFound);
        if (candidate.Status != TranscriptStatus.Failed) return new(HistoryRetryStartStatus.NotFailed);

        // The retry counter is also the optimistic concurrency token. This
        // prevents two app processes from claiming the same failed row.
        var affected = await context.Transcripts
            .Where(item => item.Id == id
                && item.Status == TranscriptStatus.Failed
                && item.RetryCount == candidate.RetryCount)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, TranscriptStatus.Processing)
                .SetProperty(item => item.RetryCount, item => item.RetryCount + 1)
                .SetProperty(item => item.LastRetryDate, retryDateUtc), cancellationToken);
        if (affected != 1) return new(HistoryRetryStartStatus.ConcurrentlyChanged);

        var claimed = await context.Transcripts.AsNoTracking()
            .SingleAsync(item => item.Id == id, cancellationToken);
        return new(HistoryRetryStartStatus.Started, claimed);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var transcript = await context.Transcripts.FindAsync(new object[] { id }, cancellationToken);
        if (transcript == null) return false;
        context.Transcripts.Remove(transcript);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<IReadOnlyList<Transcript>> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, null, null, cancellationToken);

    public async Task<IReadOnlyList<Transcript>> SearchAsync(
        string? query,
        DateTime? fromUtc,
        DateTime? toUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var rows = context.Transcripts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            rows = rows.Where(item => item.Text.Contains(term)
                || (item.TranscribedText != null && item.TranscribedText.Contains(term))
                || (item.PostProcessedText != null && item.PostProcessedText.Contains(term)));
        }
        if (fromUtc is { } from) rows = rows.Where(item => item.Date >= from);
        if (toUtcExclusive is { } to) rows = rows.Where(item => item.Date < to);
        return await rows.OrderByDescending(item => item.Date).ToListAsync(cancellationToken);
    }

    public async Task<HistoryDeletionResult> DeleteAsync(Guid id, bool deleteAudio, CancellationToken cancellationToken = default)
    {
        var result = await DeleteManyAsync([id], deleteAudio, cancellationToken);
        return new(
            result.TranscriptsDeleted == 1,
            result.AudioFilesDeleted > 0 && result.AudioFilesRetained == 0,
            result.Warnings.FirstOrDefault());
    }

    public async Task<HistoryBulkDeletionResult> DeleteManyAsync(
        IReadOnlyCollection<Guid> ids,
        bool deleteAudio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinctIds = ids.Distinct().ToArray();
        if (distinctIds.Length == 0) return new(0, 0, 0, []);

        await using var context = _database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var transcripts = await context.Transcripts
            .Where(item => distinctIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var paths = transcripts
            .SelectMany(item => new[] { item.AudioFilePath, item.TrimmedAudioFilePath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        context.Transcripts.RemoveRange(transcripts);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (!deleteAudio) return new(transcripts.Count, 0, paths.Length, []);

        var audioDeleted = 0;
        var retained = 0;
        var warnings = new List<string>();
        foreach (var path in paths)
        {
            if (!IsContainedRecording(path) || HasSymbolicLinkComponent(path))
            {
                retained++;
                continue;
            }

            try
            {
                File.Delete(path);
                audioDeleted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                retained++;
                warnings.Add("An app-owned audio file could not be deleted.");
            }
        }

        if (retained > 0 && warnings.Count == 0)
            warnings.Add("External or symbolic-link audio was retained for safety.");
        return new(transcripts.Count, audioDeleted, retained, warnings);
    }

    private bool IsContainedRecording(string path)
    {
        try { return Path.GetFullPath(path).StartsWith(_recordingsRoot!, StringComparison.Ordinal); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }

    private bool HasSymbolicLinkComponent(string path)
    {
        if (_recordingsRoot is null) return true;
        try
        {
            var root = _recordingsRoot.TrimEnd(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(_recordingsRoot, StringComparison.Ordinal)) return true;
            var relative = Path.GetRelativePath(root, fullPath);
            var current = root;
            foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (!Path.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public async Task<int> FailOrphanedProcessingAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var orphaned = await context.Transcripts
            .Where(item => item.Status == TranscriptStatus.Processing)
            .ToListAsync(cancellationToken);
        foreach (var transcript in orphaned)
        {
            const string reason = "Transcription did not finish";
            transcript.Status = TranscriptStatus.Failed;
            transcript.FailedReason = string.IsNullOrWhiteSpace(transcript.FailedReason)
                ? reason
                : transcript.FailedReason;
            transcript.Text = string.IsNullOrWhiteSpace(transcript.Text) ? reason : transcript.Text;
        }
        if (orphaned.Count > 0) await context.SaveChangesAsync(cancellationToken);
        return orphaned.Count;
    }
}

public sealed record HistoryDeletionResult(bool TranscriptDeleted, bool AudioDeleted, string? Warning);

public sealed record HistoryBulkDeletionResult(
    int TranscriptsDeleted,
    int AudioFilesDeleted,
    int AudioFilesRetained,
    IReadOnlyList<string> Warnings);
