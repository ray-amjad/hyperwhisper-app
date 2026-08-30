using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// The persisted status of a transcript row, as the home statistics see it.
/// Mirrors the core's <c>HwTranscriptStatus</c>.
/// </summary>
public enum PortableStatsTranscriptStatus
{
    Processing,
    Completed,
    Failed,
}

/// <summary>
/// One persisted transcript, projected down to what the home statistics need
/// (issue #285).
///
/// <para><c>CreatedAtLocalEpochSeconds</c> is the row's instant ALREADY SHIFTED
/// into the calendar time zone. The host owns that conversion because the host
/// owns the time-zone database, and doing it per row is what keeps DST correct.
/// Every calendar boundary above it — Monday, the 1st, January 1st — is
/// computed in the core.</para>
///
/// <para><c>WordCount</c> is counted by the host from the full text. There is
/// no persisted word count on any of the three stores, so word counting stays
/// native.</para>
/// </summary>
public sealed record PortableStatsTranscript(
    long CreatedAtLocalEpochSeconds,
    int WordCount,
    double DurationSeconds,
    PortableStatsTranscriptStatus Status);

/// <summary>One period's totals and derived figures. Mirrors <c>HwPeriodStats</c>.</summary>
public sealed record PortablePeriodStats(
    int WordCount,
    double DurationSeconds,
    int AverageWordsPerMinute,
    double EstimatedTypingMinutes,
    double EstimatedTimeSavedMinutes);

/// <summary>
/// Everything the three home strips render, plus the periods the statistics
/// pages use. Mirrors <c>HwHomeStatsSnapshot</c>.
/// </summary>
public sealed record PortableHomeStats(
    PortablePeriodStats ThisWeek,
    PortablePeriodStats ThisMonth,
    PortablePeriodStats ThisYear,
    PortablePeriodStats AllTime,
    int TypingSpeedWordsPerMinute,
    int AverageWordsPerMinute,
    int SavedThisWeekMinutes);

public static partial class SharedCoreBridge
{
    /// <summary>
    /// The home statistics for a whole transcript history, in one call (issue
    /// #285). Weekly, monthly, yearly and all-time totals, the average speaking
    /// speed, and the clamped "saved this week" figure the home strip renders.
    ///
    /// <para>The core filters to <see cref="PortableStatsTranscriptStatus.Completed"/>
    /// itself, normalises every non-finite or negative duration to zero, starts
    /// the week on the local-time-zone Monday, rounds half away from zero, and
    /// clamps <c>SavedThisWeekMinutes</c> to
    /// <see cref="SavedThisWeekMinutesCeiling"/>. There is no error case: a
    /// typing speed of zero or less zeroes the saving figures rather than
    /// failing.</para>
    ///
    /// <para>One call per recompute, not one per row. Every head already
    /// materialises the whole row set to do this.</para>
    /// </summary>
    public static PortableHomeStats CalculateHomeStatistics(
        IReadOnlyList<PortableStatsTranscript>? transcripts,
        int typingSpeedWordsPerMinute,
        long nowLocalEpochSeconds)
    {
        var snapshot = HyperwhisperCoreMethods.StatsCalculateHome(
            ToNativeTranscripts(transcripts), typingSpeedWordsPerMinute, nowLocalEpochSeconds);
        return new PortableHomeStats(
            ToPortablePeriod(snapshot.thisWeek),
            ToPortablePeriod(snapshot.thisMonth),
            ToPortablePeriod(snapshot.thisYear),
            ToPortablePeriod(snapshot.allTime),
            snapshot.typingSpeedWordsPerMinute,
            snapshot.averageWordsPerMinute,
            snapshot.savedThisWeekMinutes);
    }

    /// <summary>
    /// The ceiling the displayed "saved this week" figure is clamped to: one
    /// week of minutes. Read it rather than restating <c>7 * 24 * 60</c>, which
    /// is exactly how the constant drifted onto two platforms and off the third.
    /// </summary>
    public static int SavedThisWeekMinutesCeiling => HyperwhisperCoreMethods.StatsSavedMinutesCeiling();

    private static List<HwStatsTranscript> ToNativeTranscripts(
        IReadOnlyList<PortableStatsTranscript>? transcripts)
        => transcripts?
            // A null row cannot cross the FFI, and a row the store never
            // completed would be filtered out on the far side anyway.
            .Where(transcript => transcript is not null)
            .Select(transcript => new HwStatsTranscript(
                transcript.CreatedAtLocalEpochSeconds,
                // A negative count is not representable across the FFI. It is
                // not reachable from a real store either — clamp rather than
                // throw, because this runs on the home view's render path.
                (uint)Math.Max(0, transcript.WordCount),
                transcript.DurationSeconds,
                transcript.Status switch
                {
                    PortableStatsTranscriptStatus.Completed => HwTranscriptStatus.Completed,
                    PortableStatsTranscriptStatus.Failed => HwTranscriptStatus.Failed,
                    PortableStatsTranscriptStatus.Processing => HwTranscriptStatus.Processing,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(transcripts), transcript.Status, null),
                }))
            .ToList() ?? [];

    private static PortablePeriodStats ToPortablePeriod(HwPeriodStats stats) =>
        new(
            // The core saturates its word total at uint.MaxValue rather than
            // trapping; saturate again on the way down to int for the same
            // reason. Both are wrong numbers, and both beat a crash on the home
            // view.
            (int)Math.Min(stats.wordCount, int.MaxValue),
            stats.durationSeconds,
            stats.averageWordsPerMinute,
            stats.estimatedTypingMinutes,
            stats.estimatedTimeSavedMinutes);
}
