using HyperWhisper.Data;
using HyperWhisper.Data.Entities;

namespace HyperWhisper.Services;

/// <summary>
/// STATISTICS SERVICE
///
/// Aggregates transcript data for the Statistics page.
/// Computes recording counts, durations, word counts, and daily usage.
///
/// THREAD SAFETY:
/// - All operations are synchronized via lock
/// - Per-operation DbContext instances for safety
/// </summary>
public class StatisticsService
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static StatisticsService? _instance;
    private static readonly object _lock = new();

    public static StatisticsService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new StatisticsService();
                }
            }
            return _instance;
        }
    }

    private StatisticsService() { }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Computes aggregated statistics for the given time period.
    /// </summary>
    public StatisticsSummary GetStatistics(TimePeriod period)
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();

            var allCompleted = context.Transcripts
                .Where(t => t.Status == TranscriptStatus.Completed)
                .ToList();

            // Filter by period in-memory (dates stored as UTC)
            var now = DateTime.UtcNow;
            var filtered = period switch
            {
                TimePeriod.Week => allCompleted.Where(t => t.Date >= GetMondayOfWeek(now)).ToList(),
                TimePeriod.Month => allCompleted.Where(t => t.Date >= new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)).ToList(),
                _ => allCompleted
            };

            if (filtered.Count == 0)
            {
                return new StatisticsSummary(0, 0, 0, 0, [], []);
            }

            var totalCount = filtered.Count;
            var totalDuration = filtered.Sum(t => t.Duration);
            var totalWords = filtered.Sum(t => CountWords(t.Text));
            var averageDuration = totalDuration / totalCount;

            // Group by mode for breakdown
            var modeBreakdowns = filtered
                .GroupBy(t => t.Mode ?? "Unknown")
                .Select(g => new ModeBreakdown(
                    g.Key,
                    g.Count(),
                    g.Sum(t => t.Duration),
                    (double)g.Count() / totalCount * 100))
                .OrderByDescending(m => m.Count)
                .ToList();

            // Group by day for chart (use local time for display)
            var dailyUsage = filtered
                .GroupBy(t => t.Date.ToLocalTime().Date)
                .Select(g => new DailyUsage(
                    g.Key,
                    g.Count(),
                    g.Sum(t => t.Duration)))
                .OrderBy(d => d.Date)
                .ToList();

            return new StatisticsSummary(
                totalCount, totalDuration, totalWords, averageDuration,
                modeBreakdowns, dailyUsage);
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static DateTime GetMondayOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

// =========================================================================
// DATA MODELS
// =========================================================================

public enum TimePeriod
{
    Week,
    Month,
    AllTime
}

public record StatisticsSummary(
    int TotalCount,
    double TotalDuration,
    int TotalWords,
    double AverageDuration,
    List<ModeBreakdown> ModeBreakdowns,
    List<DailyUsage> DailyUsage);

public record ModeBreakdown(
    string ModeName,
    int Count,
    double TotalDuration,
    double Percentage);

public record DailyUsage(
    DateTime Date,
    int Count,
    double TotalDuration);
