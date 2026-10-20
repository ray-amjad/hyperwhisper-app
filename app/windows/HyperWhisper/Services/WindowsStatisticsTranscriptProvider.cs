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
/// </summary>
public sealed class WindowsStatisticsTranscriptProvider : IStatisticsTranscriptProvider
{
    public async ValueTask<IReadOnlyList<StatisticsTranscript>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        // Rows the database refused to insert (#1589) count too. Read them first: a row
        // flushed between the two reads is then in both and counted once (by id below).
        var pending = HistoryService.Instance.GetPendingTranscriptsSnapshot();

        await using var context = new HyperWhisperDbContext();
        var rows = await context.Transcripts.AsNoTracking()
            .Select(item => new { item.Id, item.Date, item.Text, item.Duration, item.Status })
            .ToListAsync(cancellationToken);

        var known = rows.Select(r => r.Id).ToHashSet();
        return rows.Select(r => ToStatistics(r.Date, r.Text, r.Duration, r.Status))
            .Concat(pending.Where(t => !known.Contains(t.Id))
                .Select(t => ToStatistics(t.Date, t.Text, t.Duration, t.Status)))
            .ToList();
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
