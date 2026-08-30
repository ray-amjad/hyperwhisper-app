using CommunityToolkit.Mvvm.ComponentModel;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Statistics;

namespace HyperWhisper.ViewModels;

/// <summary>
/// Backs the four-column stats strip at the top of HomePage.
/// Mirrors the macOS HomeStatsBar:
///   [ avg WPM ] | [ words this week ] | [ words this month ] | [ minutes saved ⚙ ]
///
/// Every number comes from <see cref="HomeStatisticsService"/>, which calls the
/// shared Rust core (issue #285). This class used to carry its own average-WPM
/// and saved-minutes formulas over a StatisticsService whose week and month
/// boundaries were UTC. Three things changed for a Windows user:
///
///   - the week and the month now start in the user's own time zone, not UTC;
///   - 2.5 saved minutes displays as 3, not 2 (half away from zero, not
///     banker's rounding);
///   - a transcript row with a non-finite or negative duration contributes zero
///     seconds instead of poisoning the totals.
/// </summary>
public partial class HomeStatsBarViewModel : ObservableObject
{
    private readonly HomeStatisticsService _statisticsService;
    private readonly SettingsService _settingsService;

    [ObservableProperty] private int _averageWpm;
    [ObservableProperty] private int _wordsThisWeek;
    [ObservableProperty] private int _wordsThisMonth;
    [ObservableProperty] private int _savedThisWeekMinutes;
    [ObservableProperty] private int _typingSpeedWpm;

    public string SavedThisWeekDisplay => Loc.S("home.stats.minutesValue", SavedThisWeekMinutes);

    partial void OnSavedThisWeekMinutesChanged(int value) => OnPropertyChanged(nameof(SavedThisWeekDisplay));

    private System.Timers.Timer? _debounceTimer;
    private int _recomputeGeneration;
    private bool _firstRecomputeApplied;

    public HomeStatsBarViewModel(HomeStatisticsService statisticsService, SettingsService settingsService)
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

        SavedThisWeekMinutes = HomeStatisticsCalculator.DisplayedSavedMinutes(
            _lastWeekWords, _lastWeekDurationSeconds, wpm);
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

        HomeStatisticsSnapshot? snapshot = null;

        try
        {
            // One call now, not three: the shared core buckets every period in a
            // single pass over the rows.
            snapshot = await _statisticsService
                .GetAsync(TypingSpeedWpm, DateTimeOffset.UtcNow, TimeZoneInfo.Local)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"HomeStatsBarViewModel: RecomputeAsync failed: {ex.Message}");
            return;
        }

        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        var applied = snapshot;

        dispatcher.Invoke(() =>
        {
            // Drop stale snapshots — only the latest generation may write.
            if (generation != System.Threading.Volatile.Read(ref _recomputeGeneration)) return;

            _lastWeekWords = applied.ThisWeek.WordCount;
            _lastWeekDurationSeconds = applied.ThisWeek.DictatedDurationSeconds;

            AverageWpm = applied.AverageWordsPerMinute;
            WordsThisWeek = applied.ThisWeek.WordCount;
            WordsThisMonth = applied.ThisMonth.WordCount;
            SavedThisWeekMinutes = applied.SavedThisWeekMinutes;

            _firstRecomputeApplied = true;
        });
    }
}
