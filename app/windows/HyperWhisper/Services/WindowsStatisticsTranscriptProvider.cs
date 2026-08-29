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
        await using var context = new HyperWhisperDbContext();
        return await context.Transcripts.AsNoTracking()
            .Select(item => new StatisticsTranscript(
                new DateTimeOffset(DateTime.SpecifyKind(item.Date, DateTimeKind.Utc)),
                item.Text,
                item.Duration,
                item.Status == TranscriptStatus.Completed
                    ? StatisticsTranscriptStatus.Completed
                    : item.Status == TranscriptStatus.Failed
                        ? StatisticsTranscriptStatus.Failed
                        : StatisticsTranscriptStatus.Processing))
            .ToListAsync(cancellationToken);
    }
}
