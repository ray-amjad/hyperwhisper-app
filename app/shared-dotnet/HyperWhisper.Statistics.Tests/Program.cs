using HyperWhisper.Statistics;

var tests = new (string Name, Func<Task> Run)[]
{
    ("home metrics match Windows formulas", HomeMetricsMatchWindows),
    ("UTC week and month boundaries are half open", UtcBoundariesAreHalfOpen),
    ("local calendar boundaries convert absolute timestamps", LocalCalendarBoundaries),
    ("Sunday belongs to the Monday based week", SundayBelongsToMondayWeek),
    ("failed and processing transcripts are excluded", FailedAndProcessingAreExcluded),
    ("blank completed transcripts retain dictated duration", BlankCompletedTranscriptRetainsDuration),
    ("zero and invalid durations are safe", ZeroAndInvalidDurationsAreSafe),
    ("typing speed controls estimates and weekly ceiling", TypingSpeedAndCeiling),
    ("word counting follows whitespace semantics", WordCounting),
    ("service uses immutable provider snapshot", ServiceUsesProvider),
    ("service observes cancellation", ServiceObservesCancellation),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static Task HomeMetricsMatchWindows()
{
    var now = Utc(2026, 8, 19, 12);
    var result = Calculate(now, 40,
        Completed(Utc(2026, 8, 17, 0), "one two three four", 2),
        Completed(Utc(2026, 8, 1, 0), "five six", 58),
        Completed(Utc(2026, 7, 31, 23), "seven eight nine ten", 60));

    Equal(4, result.ThisWeek.WordCount, "weekly words");
    Equal(6, result.ThisMonth.WordCount, "monthly words");
    Equal(10, result.AllTime.WordCount, "all-time words");
    Equal(120d, result.AllTime.DictatedDurationSeconds, "all-time duration");
    Equal(5, result.AverageWordsPerMinute, "all-time WPM");
    Nearly(0.25, result.AllTime.EstimatedTypingMinutes, "typing time");
    Nearly(0, result.AllTime.EstimatedTimeSavedMinutes, "clamped time saved");
    return Task.CompletedTask;
}

static Task UtcBoundariesAreHalfOpen()
{
    var now = Utc(2026, 8, 19, 12);
    var result = Calculate(now, 40,
        Completed(Utc(2026, 8, 16, 23, 59), "before week", 1),
        Completed(Utc(2026, 8, 17, 0), "week start", 1),
        Completed(Utc(2026, 8, 24, 0), "next week", 1),
        Completed(Utc(2026, 7, 31, 23, 59), "before month", 1),
        Completed(Utc(2026, 8, 1, 0), "month start", 1),
        Completed(Utc(2026, 9, 1, 0), "next month", 1));

    Equal(2, result.ThisWeek.WordCount, "half-open week");
    Equal(8, result.ThisMonth.WordCount, "half-open month");
    Equal(12, result.AllTime.WordCount, "all time retains all instants");
    return Task.CompletedTask;
}

static Task LocalCalendarBoundaries()
{
    var zone = TimeZoneInfo.CreateCustomTimeZone("UTC-07", TimeSpan.FromHours(-7), "UTC-07", "UTC-07");
    var now = Utc(2026, 8, 3, 12); // Monday 05:00 local.
    var rows = new[]
    {
        Completed(Utc(2026, 8, 3, 6, 59), "local sunday", 1),
        Completed(Utc(2026, 8, 3, 7), "local monday", 1),
        Completed(Utc(2026, 8, 1, 6, 59), "local july", 1),
        Completed(Utc(2026, 8, 1, 7), "local august", 1),
    };
    var local = HomeStatisticsCalculator.Calculate(rows, 40, now, zone);
    var utc = HomeStatisticsCalculator.Calculate(rows, 40, now, TimeZoneInfo.Utc);

    Equal(2, local.ThisWeek.WordCount, "local week");
    Equal(4, utc.ThisWeek.WordCount, "UTC week");
    Equal(6, local.ThisMonth.WordCount, "local month");
    Equal(8, utc.ThisMonth.WordCount, "UTC month");
    return Task.CompletedTask;
}

static Task SundayBelongsToMondayWeek()
{
    var result = Calculate(Utc(2026, 8, 23, 12), 40,
        Completed(Utc(2026, 8, 17, 0), "monday", 1),
        Completed(Utc(2026, 8, 23, 23, 59), "sunday", 1),
        Completed(Utc(2026, 8, 16, 23, 59), "previous", 1));
    Equal(2, result.ThisWeek.WordCount, "Monday through Sunday week");
    return Task.CompletedTask;
}

static Task FailedAndProcessingAreExcluded()
{
    var now = Utc(2026, 8, 19, 12);
    var result = Calculate(now, 40,
        Completed(now, "kept", 60),
        new(now, "failure message must not count", 600, StatisticsTranscriptStatus.Failed),
        new(now, "processing text must not count", 600, StatisticsTranscriptStatus.Processing));
    Equal(1, result.AllTime.WordCount, "completed words only");
    Equal(60d, result.AllTime.DictatedDurationSeconds, "completed duration only");
    return Task.CompletedTask;
}

static Task BlankCompletedTranscriptRetainsDuration()
{
    var now = Utc(2026, 8, 19, 12);
    var result = Calculate(now, 40,
        Completed(now, "sixty words", 30),
        Completed(now, " \r\n\t", 30));
    Equal(2, result.AllTime.WordCount, "blank row word count");
    Equal(60d, result.AllTime.DictatedDurationSeconds, "blank row duration");
    Equal(2, result.AverageWordsPerMinute, "blank row affects WPM duration");
    return Task.CompletedTask;
}

static Task ZeroAndInvalidDurationsAreSafe()
{
    var now = Utc(2026, 8, 19, 12);
    var result = Calculate(now, 40,
        Completed(now, "words with no audio", 0),
        Completed(now, "negative", -1),
        Completed(now, "not finite", double.NaN));
    Equal(0d, result.AllTime.DictatedDurationSeconds, "normalized duration");
    Equal(0, result.AverageWordsPerMinute, "zero duration WPM");
    True(double.IsFinite(result.AllTime.EstimatedTimeSavedMinutes), "time saved must be finite");
    return Task.CompletedTask;
}

static Task TypingSpeedAndCeiling()
{
    var now = Utc(2026, 8, 19, 12);
    var normal = Calculate(now, 40, Completed(now, string.Join(' ', Enumerable.Repeat("word", 400)), 60));
    Equal(9, normal.SavedThisWeekMinutes, "rounded weekly savings");
    Nearly(10, normal.ThisWeek.EstimatedTypingMinutes, "typing estimate");
    Nearly(9, normal.ThisWeek.EstimatedTimeSavedMinutes, "raw saved time");

    var ceiling = Calculate(now, 1,
        Completed(now, string.Join(' ', Enumerable.Repeat("word", 11_000)), 0));
    Equal(HomeStatisticsCalculator.SavedThisWeekMinutesCeiling, ceiling.SavedThisWeekMinutes, "weekly ceiling");

    var invalid = Calculate(now, 0, Completed(now, "one two", 0));
    Equal(0, invalid.SavedThisWeekMinutes, "invalid speed display");
    Equal(0d, invalid.AllTime.EstimatedTypingMinutes, "invalid speed estimate");
    return Task.CompletedTask;
}

static Task WordCounting()
{
    Equal(0, HomeStatisticsCalculator.CountWords(null), "null");
    Equal(0, HomeStatisticsCalculator.CountWords(" \r\n\t"), "whitespace");
    Equal(4, HomeStatisticsCalculator.CountWords(" one\ttwo\r\nthree\u00a0four "), "Unicode whitespace");
    return Task.CompletedTask;
}

static async Task ServiceUsesProvider()
{
    var now = Utc(2026, 8, 19, 12);
    var provider = new FakeProvider([Completed(now, "one two", 60)]);
    var service = new HomeStatisticsService(provider);
    var result = await service.GetAsync(40, now, TimeZoneInfo.Utc);
    Equal(1, provider.ReadCount, "provider read count");
    Equal(2, result.AllTime.WordCount, "provider result");
}

static async Task ServiceObservesCancellation()
{
    var provider = new FakeProvider([]);
    var service = new HomeStatisticsService(provider);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        _ = await service.GetAsync(40, Utc(2026, 8, 19, 12), TimeZoneInfo.Utc, cancellation.Token);
        throw new InvalidOperationException("Expected cancellation.");
    }
    catch (OperationCanceledException)
    {
    }
}

static HomeStatisticsSnapshot Calculate(DateTimeOffset now, int typingSpeed, params StatisticsTranscript[] rows) =>
    HomeStatisticsCalculator.Calculate(rows, typingSpeed, now, TimeZoneInfo.Utc);

static StatisticsTranscript Completed(DateTimeOffset at, string? text, double duration) =>
    new(at, text, duration, StatisticsTranscriptStatus.Completed);

static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
    new(year, month, day, hour, minute, 0, TimeSpan.Zero);

static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
{
    if (!expected.Equals(actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void Nearly(double expected, double actual, string message)
{
    if (Math.Abs(expected - actual) > 0.000_001)
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

sealed class FakeProvider(IReadOnlyList<StatisticsTranscript> rows) : IStatisticsTranscriptProvider
{
    public int ReadCount { get; private set; }

    public ValueTask<IReadOnlyList<StatisticsTranscript>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ValueTask.FromResult(rows);
    }
}
