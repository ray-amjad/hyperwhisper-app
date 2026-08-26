using HyperWhisper.Data.Entities;
using HyperWhisper.Statistics;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed class StatisticsTranscriptProvider(ApplicationDb database) : IStatisticsTranscriptProvider
{
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<IReadOnlyList<StatisticsTranscript>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
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
