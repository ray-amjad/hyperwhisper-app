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

public static class HomeStatisticsCalculator
{
    // Matches HomeStatsBarViewModel: at most one week of displayed saved minutes.
    public const int SavedThisWeekMinutesCeiling = 7 * 24 * 60;

    public static HomeStatisticsSnapshot Calculate(
        IEnumerable<StatisticsTranscript> transcripts,
        int typingSpeedWordsPerMinute,
        DateTimeOffset now,
        TimeZoneInfo calendarTimeZone)
    {
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(calendarTimeZone);

        var localNow = TimeZoneInfo.ConvertTime(now, calendarTimeZone);
        var weekStartDate = StartOfWeek(localNow.Date);
        var monthStartDate = new DateTime(localNow.Year, localNow.Month, 1);
        var nextMonthStartDate = monthStartDate.AddMonths(1);
        var nextWeekStartDate = weekStartDate.AddDays(7);

        var week = new Accumulator();
        var month = new Accumulator();
        var allTime = new Accumulator();

        foreach (var transcript in transcripts)
        {
            if (transcript.Status != StatisticsTranscriptStatus.Completed) continue;

            var wordCount = CountWords(transcript.Text);
            var duration = NormalizeDuration(transcript.DictatedDurationSeconds);
            allTime.Add(wordCount, duration);

            var localDate = TimeZoneInfo.ConvertTime(transcript.CreatedAt, calendarTimeZone).Date;
            if (localDate >= weekStartDate && localDate < nextWeekStartDate)
                week.Add(wordCount, duration);
            if (localDate >= monthStartDate && localDate < nextMonthStartDate)
                month.Add(wordCount, duration);
        }

        var weekResult = week.ToStatistics(typingSpeedWordsPerMinute);
        var savedThisWeek = ComputeDisplayedSavedMinutes(
            weekResult.WordCount,
            weekResult.DictatedDurationSeconds,
            typingSpeedWordsPerMinute);

        return new(
            weekResult,
            month.ToStatistics(typingSpeedWordsPerMinute),
            allTime.ToStatistics(typingSpeedWordsPerMinute),
            typingSpeedWordsPerMinute,
            savedThisWeek);
    }

    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static DateTime StartOfWeek(DateTime date)
    {
        var daysSinceMonday = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private static double NormalizeDuration(double durationSeconds) =>
        double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;

    private static int ComputeDisplayedSavedMinutes(
        int words,
        double dictatedDurationSeconds,
        int typingSpeedWordsPerMinute)
    {
        if (typingSpeedWordsPerMinute <= 0) return 0;
        var saved = (double)words / typingSpeedWordsPerMinute - dictatedDurationSeconds / 60d;
        return Math.Min(SavedThisWeekMinutesCeiling, Math.Max(0, (int)Math.Round(saved)));
    }

    private sealed class Accumulator
    {
        private int _words;
        private double _durationSeconds;

        public void Add(int words, double durationSeconds)
        {
            _words = checked(_words + words);
            _durationSeconds += durationSeconds;
        }

        public PeriodStatistics ToStatistics(int typingSpeedWordsPerMinute)
        {
            var durationMinutes = _durationSeconds / 60d;
            var averageWordsPerMinute = durationMinutes > 0
                ? (int)Math.Round(_words / durationMinutes)
                : 0;
            var estimatedTypingMinutes = typingSpeedWordsPerMinute > 0
                ? (double)_words / typingSpeedWordsPerMinute
                : 0;
            var estimatedTimeSavedMinutes = typingSpeedWordsPerMinute > 0
                ? Math.Max(0, estimatedTypingMinutes - durationMinutes)
                : 0;
            return new(
                _words,
                _durationSeconds,
                averageWordsPerMinute,
                estimatedTypingMinutes,
                estimatedTimeSavedMinutes);
        }
    }
}
