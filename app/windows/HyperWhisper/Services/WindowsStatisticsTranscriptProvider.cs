using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Statistics;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.Services;

/// <summary>
/// Reads the WPF head's transcript rows for the shared home-statistics
/// calculator (issue #285).
///
/// The formulas used to live in <c>HomeStatsBarViewModel</c> and the calendar
/// boundaries in a <c>StatisticsService</c> that forced UTC. Both are gone: the
/// calculator in <c>HyperWhisper.Statistics</c> calls the shared Rust core, and
/// this class only supplies the rows. It is the same projection the portable
/// heads' <c>StatisticsTranscriptProvider</c> makes, against this head's own
/// <see cref="HyperWhisperDbContext"/>.
///
/// <para><c>Transcript.Date</c> is stored as UTC but SQLite round-trips it as
/// <see cref="DateTimeKind.Unspecified"/>, so the kind is restated here. The
/// conversion into the user's calendar time zone happens once, in the
/// calculator.</para>
///
/// <para>The database rows are cached for the life of the process (#986). Every
/// Home visit used to re-read the full text of every transcript (about 90 ms of
/// CPU at 20,000 rows). The cache is dropped on each <see cref="HistoryService"/>
/// TranscriptAdded, TranscriptUpdated and TranscriptDeleted, which every write to
/// the Transcripts table raises, and whenever the pending (#1589) set changes,
/// because a pending row that reaches the database raises no event. It holds rows,
/// not totals: the totals move with the clock, the time zone and the typing speed,
/// and the calculator still works them out on every visit.</para>
/// </summary>
public sealed class WindowsStatisticsTranscriptProvider : IStatisticsTranscriptProvider
{
    private static readonly object CacheGate = new();
    private static bool _subscribed;
    private static int _version;
    private static CachedRows? _cache;

    /// <summary>The database rows of one read, and the pending ids it was paired with.</summary>
    private sealed record CachedRows(
        IReadOnlyList<StatisticsTranscript> Rows,
        HashSet<Guid> Ids,
        HashSet<Guid> PendingIds);

    public async ValueTask<IReadOnlyList<StatisticsTranscript>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureSubscribed();

        // Rows the database refused to insert (#1589) count too. Read them first: a row
        // flushed between the two reads is then in both and counted once (by id below).
        var pending = HistoryService.Instance.GetPendingTranscriptsSnapshot();
        var pendingIds = pending.Select(t => t.Id).ToHashSet();

        var cached = Volatile.Read(ref _cache);
        if (cached == null || !cached.PendingIds.SetEquals(pendingIds))
        {
            cached = await ReadDatabaseAsync(pendingIds, cancellationToken).ConfigureAwait(false);
        }

        return cached.Rows
            .Concat(pending.Where(t => !cached.Ids.Contains(t.Id))
                .Select(t => ToStatistics(t.Date, t.Text, t.Duration, t.Status)))
            .ToList();
    }

    private static async Task<CachedRows> ReadDatabaseAsync(
        HashSet<Guid> pendingIds, CancellationToken cancellationToken)
    {
        // Taken before the read: a write whose event lands while the read runs bumps
        // it, and this result is then used once but never cached.
        var version = Volatile.Read(ref _version);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        await using var context = new HyperWhisperDbContext();
        var rows = await context.Transcripts.AsNoTracking()
            .Select(item => new { item.Id, item.Date, item.Text, item.Duration, item.Status })
            .ToListAsync(cancellationToken);

        var read = new CachedRows(
            rows.Select(r => ToStatistics(r.Date, r.Text, r.Duration, r.Status)).ToList(),
            rows.Select(r => r.Id).ToHashSet(),
            pendingIds);

        lock (CacheGate)
        {
            if (_version == version)
                _cache = read;
        }

        LoggingService.Info(
            $"WindowsStatisticsTranscriptProvider: read {rows.Count} transcript rows from the database " +
            $"in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
        return read;
    }

    private static void EnsureSubscribed()
    {
        lock (CacheGate)
        {
            if (_subscribed) return;
            var history = HistoryService.Instance;
            history.TranscriptAdded += (_, _) => Invalidate();
            history.TranscriptUpdated += (_, _) => Invalidate();
            history.TranscriptDeleted += (_, _) => Invalidate();
            _subscribed = true;
        }
    }

    private static void Invalidate()
    {
        lock (CacheGate)
        {
            _version++;
            _cache = null;
        }
    }

    private static StatisticsTranscript ToStatistics(DateTime date, string? text, double duration, TranscriptStatus status) => new(
        new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
        text,
        duration,
        status == TranscriptStatus.Completed
            ? StatisticsTranscriptStatus.Completed
            : status == TranscriptStatus.Failed
                ? StatisticsTranscriptStatus.Failed
                : StatisticsTranscriptStatus.Processing);
}
