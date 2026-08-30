using HyperWhisper.SharedCore;

namespace HyperWhisper.Statistics;

public enum StatisticsTranscriptStatus
{
    Processing,
    Completed,
    Failed,
}

/// <summary>
/// Portable projection of the persisted transcript fields used by home statistics.
/// Timestamps are absolute instants; calendar boundaries are selected separately.
/// </summary>
public sealed record StatisticsTranscript(
    DateTimeOffset CreatedAt,
    string? Text,
    double DictatedDurationSeconds,
    StatisticsTranscriptStatus Status);

public sealed record PeriodStatistics(
    int WordCount,
    double DictatedDurationSeconds,
    int AverageWordsPerMinute,
    double EstimatedTypingMinutes,
    double EstimatedTimeSavedMinutes);

public sealed record HomeStatisticsSnapshot(
    PeriodStatistics ThisWeek,
    PeriodStatistics ThisMonth,
    PeriodStatistics ThisYear,
    PeriodStatistics AllTime,
    int TypingSpeedWordsPerMinute,
    int SavedThisWeekMinutes)
{
    public int AverageWordsPerMinute => AllTime.AverageWordsPerMinute;
}

public interface IStatisticsTranscriptProvider
{
    ValueTask<IReadOnlyList<StatisticsTranscript>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads a stable transcript projection and calculates the home statistics in one pass.
/// </summary>
public sealed class HomeStatisticsService(IStatisticsTranscriptProvider provider)
{
    private readonly IStatisticsTranscriptProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    public async ValueTask<HomeStatisticsSnapshot> GetAsync(
        int typingSpeedWordsPerMinute,
        DateTimeOffset now,
        TimeZoneInfo calendarTimeZone,
        CancellationToken cancellationToken = default)
    {
        var transcripts = await _provider.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return HomeStatisticsCalculator.Calculate(
            transcripts, typingSpeedWordsPerMinute, now, calendarTimeZone);
    }
}

/// <summary>
/// The .NET seam onto the shared home-statistics core (issue #285).
///
/// <para>Every formula lives in the <c>hw-stats</c> crate now — the calendar
/// boundaries, the finite-and-positive duration guard, half-away-from-zero
/// rounding, the saved-minutes ceiling and the completed-only filter. This class
/// keeps the two jobs the host owns: counting words from the full text, and
/// converting each instant into the calendar time zone. Both stay here because
/// both need something the core deliberately does not carry — the persisted text
/// on one side, the time-zone database on the other.</para>
///
/// <para><c>shared-conformance/stats-vectors.json</c> is the cross-platform
/// record of what changed and why. It is run against this binding by
/// <c>HyperWhisper.StatsConformance.Tests</c>.</para>
/// </summary>
public static class HomeStatisticsCalculator
{
    /// <summary>
    /// At most one week of displayed saved minutes. Read from the core so the
    /// three heads cannot drift apart on it again.
    /// </summary>
    public static int SavedThisWeekMinutesCeiling => SharedCoreBridge.SavedThisWeekMinutesCeiling;

    public static HomeStatisticsSnapshot Calculate(
        IEnumerable<StatisticsTranscript> transcripts,
        int typingSpeedWordsPerMinute,
        DateTimeOffset now,
        TimeZoneInfo calendarTimeZone)
    {
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(calendarTimeZone);

        var rows = transcripts
            .Select(transcript => new PortableStatsTranscript(
                LocalEpochSeconds(transcript.CreatedAt, calendarTimeZone),
                CountWords(transcript.Text),
                transcript.DictatedDurationSeconds,
                transcript.Status switch
                {
                    StatisticsTranscriptStatus.Completed => PortableStatsTranscriptStatus.Completed,
                    StatisticsTranscriptStatus.Failed => PortableStatsTranscriptStatus.Failed,
                    _ => PortableStatsTranscriptStatus.Processing,
                }))
            .ToList();

        var snapshot = SharedCoreBridge.CalculateHomeStatistics(
            rows, typingSpeedWordsPerMinute, LocalEpochSeconds(now, calendarTimeZone));

        return new(
            ToPeriod(snapshot.ThisWeek),
            ToPeriod(snapshot.ThisMonth),
            ToPeriod(snapshot.ThisYear),
            ToPeriod(snapshot.AllTime),
            snapshot.TypingSpeedWordsPerMinute,
            snapshot.SavedThisWeekMinutes);
    }

    /// <summary>
    /// The displayed "saved this week" figure for totals a caller already holds,
    /// without re-reading the store.
    ///
    /// <para>This is the home strip's typing-speed menu: the user picks a new
    /// speed and the number has to move immediately, but the week's words and
    /// spoken seconds have not changed. Rather than restate the formula — which
    /// is how the ceiling and the rounding rule drifted in the first place — it
    /// asks the core the same question with a single synthetic row carrying
    /// those totals.</para>
    /// </summary>
    public static int DisplayedSavedMinutes(
        int weekWordCount,
        double weekDurationSeconds,
        int typingSpeedWordsPerMinute)
    {
        var snapshot = SharedCoreBridge.CalculateHomeStatistics(
            [
                new PortableStatsTranscript(
                    0, weekWordCount, weekDurationSeconds, PortableStatsTranscriptStatus.Completed),
            ],
            typingSpeedWordsPerMinute,
            // The row and "now" share an instant, so the row is always inside
            // the current week whatever day that instant lands on.
            0);
        return snapshot.SavedThisWeekMinutes;
    }

    /// <summary>
    /// Word counting stays native: there is no persisted count on any of the
    /// three stores, and the Swift and C# implementations already agree.
    /// </summary>
    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// An instant read as local wall-clock time, in seconds since the epoch.
    /// This is the one thing the core cannot do for itself — it carries no
    /// time-zone database — and doing it per row is what keeps DST correct.
    /// </summary>
    private static long LocalEpochSeconds(DateTimeOffset instant, TimeZoneInfo calendarTimeZone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, calendarTimeZone);
        return (long)Math.Floor((local.DateTime - DateTime.UnixEpoch).TotalSeconds);
    }

    private static PeriodStatistics ToPeriod(PortablePeriodStats stats) =>
        new(
            stats.WordCount,
            stats.DurationSeconds,
            stats.AverageWordsPerMinute,
            stats.EstimatedTypingMinutes,
            stats.EstimatedTimeSavedMinutes);
}
