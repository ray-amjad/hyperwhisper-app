using CommunityToolkit.Mvvm.ComponentModel;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Services;

namespace HyperWhisper.ViewModels;

/// <summary>
/// Backs the four-column stats strip at the top of HomePage.
/// Mirrors the macOS HomeStatsBar:
///   [ avg WPM ] | [ words this week ] | [ words this month ] | [ minutes saved ⚙ ]
/// </summary>
public partial class HomeStatsBarViewModel : ObservableObject
{
    private readonly StatisticsService _statisticsService;
    private readonly SettingsService _settingsService;

    [ObservableProperty] private int _averageWpm;
    [ObservableProperty] private int _wordsThisWeek;
    [ObservableProperty] private int _wordsThisMonth;
    [ObservableProperty] private int _savedThisWeekMinutes;
    [ObservableProperty] private int _typingSpeedWpm;

    public string SavedThisWeekDisplay => Loc.S("home.stats.minutesValue", SavedThisWeekMinutes);

    partial void OnSavedThisWeekMinutesChanged(int value) => OnPropertyChanged(nameof(SavedThisWeekDisplay));

    public static int[] TypingSpeedPresets => new[] { 30, 40, 50, 60, 80, 100 };

    // Saved-minutes ceiling — one week of minutes. A row with Words>0 and
    // Duration≈0 can otherwise produce an absurd savings figure.
    private const int SavedMinutesCeiling = 7 * 24 * 60;

    private System.Timers.Timer? _debounceTimer;
    private int _recomputeGeneration;
    private bool _firstRecomputeApplied;

    public HomeStatsBarViewModel(StatisticsService statisticsService, SettingsService settingsService)
    {
        _statisticsService = statisticsService;
        _settingsService = settingsService;
        _typingSpeedWpm = _settingsService.TypingSpeedWPM;

        HistoryService.Instance.TranscriptAdded += OnTranscriptsChanged;
        HistoryService.Instance.TranscriptUpdated += OnTranscriptsChanged;
        HistoryService.Instance.TranscriptDeleted += OnTranscriptDeleted;
    }

    public void SetTypingSpeed(int wpm)
    {
        if (wpm <= 0 || wpm == TypingSpeedWpm) return;
        TypingSpeedWpm = wpm;
        _settingsService.TypingSpeedWPM = wpm;

        // If the first recompute hasn't landed yet, _lastWeek* are still 0 — don't
        // compute (it would briefly flash "0 minutes"). The debounced recompute
        // below will pick up the new typing speed via the field assignment above.
        if (!_firstRecomputeApplied)
        {
            ScheduleRecompute();
            return;
        }

        SavedThisWeekMinutes = ComputeSavedMinutes(_lastWeekWords, _lastWeekDurationSeconds, wpm);
    }

    public void Detach()
    {
        HistoryService.Instance.TranscriptAdded -= OnTranscriptsChanged;
        HistoryService.Instance.TranscriptUpdated -= OnTranscriptsChanged;
        HistoryService.Instance.TranscriptDeleted -= OnTranscriptDeleted;

        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    private int _lastWeekWords;
    private double _lastWeekDurationSeconds;

    private void OnTranscriptsChanged(object? sender, Transcript e) => ScheduleRecompute();
    private void OnTranscriptDeleted(object? sender, Guid e) => ScheduleRecompute();

    /// <summary>
    /// Coalesces a burst of HistoryService events into a single RecomputeAsync.
    /// Bulk operations like AutoDeleteService or multi-select delete fire N
    /// TranscriptDeleted events back-to-back; without debouncing each would
    /// kick off three locked DB queries.
    /// </summary>
    private void ScheduleRecompute()
    {
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();

        _debounceTimer = new System.Timers.Timer(250);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (s, e) =>
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _ = RecomputeAsync();
        };
        _debounceTimer.Start();
    }

    /// <summary>
    /// Aggregates weekly / monthly / all-time stats off the UI thread, then
    /// pushes the new values back via the dispatcher. Concurrent calls are
    /// freshness-tagged via a generation counter — stale snapshots are dropped.
    /// </summary>
    public async Task RecomputeAsync()
    {
        var generation = System.Threading.Interlocked.Increment(ref _recomputeGeneration);

        StatisticsSummary? weekly = null;
        StatisticsSummary? monthly = null;
        StatisticsSummary? allTime = null;

        await Task.Run(() =>
        {
            try
            {
                weekly = _statisticsService.GetStatistics(TimePeriod.Week);
                monthly = _statisticsService.GetStatistics(TimePeriod.Month);
                allTime = _statisticsService.GetStatistics(TimePeriod.AllTime);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"HomeStatsBarViewModel: RecomputeAsync failed: {ex.Message}");
                weekly = null;
                monthly = null;
                allTime = null;
            }
        });

        if (weekly == null || monthly == null || allTime == null) return;

        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        var weeklySnapshot = weekly;
        var monthlySnapshot = monthly;
        var allTimeSnapshot = allTime;

        dispatcher.Invoke(() =>
        {
            // Drop stale snapshots — only the latest generation may write.
            if (generation != System.Threading.Volatile.Read(ref _recomputeGeneration)) return;

            _lastWeekWords = weeklySnapshot.TotalWords;
            _lastWeekDurationSeconds = weeklySnapshot.TotalDuration;

            AverageWpm = ComputeAverageWpm(allTimeSnapshot.TotalWords, allTimeSnapshot.TotalDuration);
            WordsThisWeek = weeklySnapshot.TotalWords;
            WordsThisMonth = monthlySnapshot.TotalWords;
            SavedThisWeekMinutes = ComputeSavedMinutes(weeklySnapshot.TotalWords, weeklySnapshot.TotalDuration, TypingSpeedWpm);

            _firstRecomputeApplied = true;
        });
    }

    private static int ComputeAverageWpm(int totalWords, double totalDurationSeconds)
    {
        var minutes = totalDurationSeconds / 60.0;
        if (minutes <= 0) return 0;
        return (int)Math.Round(totalWords / minutes);
    }

    private static int ComputeSavedMinutes(int weekWords, double weekDurationSeconds, int typingWpm)
    {
        if (typingWpm <= 0) return 0;
        var typingMinutes = (double)weekWords / typingWpm;
        var spokenMinutes = weekDurationSeconds / 60.0;
        var saved = typingMinutes - spokenMinutes;
        return Math.Min(SavedMinutesCeiling, Math.Max(0, (int)Math.Round(saved)));
    }
}
