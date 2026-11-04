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
///
/// The Home page's totals are cached for the life of the process (#986): every
/// Home visit used to re-read the full text of every transcript. Only a view
/// model built with <c>cacheTotals: true</c> (HomePage) reads or fills the
/// cache, so one built over another provider never sees the Home page's
/// totals. A visit reuses them while
/// the typing speed, the time zone and the local day are the ones they were
/// worked out for (the shared core reads nothing else from "now"), and every
/// HistoryService TranscriptAdded, TranscriptUpdated and TranscriptDeleted drops
/// them. A pending row (#1589) that later reaches the database raises no event,
/// but it was already counted with the same fields, so the totals do not move.
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

    private static readonly object CachedTotalsGate = new();
    private static bool _cachedTotalsSubscribed;
    private static int _cachedTotalsVersion;
    private static CachedTotals? _cachedTotals;

    /// <summary>One computed snapshot and the inputs it depends on besides the rows.</summary>
    private sealed record CachedTotals(
        HomeStatisticsSnapshot Snapshot,
        int TypingSpeedWpm,
        string TimeZoneId,
        DateOnly LocalDay);

    private readonly bool _cacheTotals;

    public HomeStatsBarViewModel(
        HomeStatisticsService statisticsService,
        SettingsService settingsService,
        bool cacheTotals = false)
    {
        _statisticsService = statisticsService;
        _settingsService = settingsService;
        _typingSpeedWpm = _settingsService.TypingSpeedWPM;
        _cacheTotals = cacheTotals;

        // Before this instance's own handlers, so a save drops the cache before the
        // debounced recompute it schedules reads it.
        if (_cacheTotals)
            EnsureCachedTotalsSubscribed();

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
            //
            // Task.Run, not just ConfigureAwait(false): Microsoft.Data.Sqlite's
            // async calls complete synchronously, so without the hop the whole
            // read and word count ran on the dispatcher before the first await
            // yielded (#1486: 140 ms p50 per Home visit at 20k transcripts).
            var typingSpeedWpm = TypingSpeedWpm;
            var now = DateTimeOffset.UtcNow;
            var zone = TimeZoneInfo.Local;
            var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

            snapshot = _cacheTotals ? TryGetCachedTotals(typingSpeedWpm, zone.Id, localDay) : null;
            if (snapshot == null)
            {
                // Taken before the read: a save whose event lands while the read runs
                // bumps it, and this result is then shown once but never cached.
                var version = Volatile.Read(ref _cachedTotalsVersion);
                snapshot = await Task.Run(() => _statisticsService
                        .GetAsync(typingSpeedWpm, now, zone)
                        .AsTask())
                    .ConfigureAwait(false);
                if (_cacheTotals)
                    StoreCachedTotals(version, new CachedTotals(snapshot, typingSpeedWpm, zone.Id, localDay));
            }
        }
        catch (Exception ex)
        {
            // The type and the HRESULT, never the message. Windows Application
            // Control can block HyperWhisper.Statistics here, and the resulting
            // FileLoadException message carries the installed path — which holds
            // the user's Windows account name (HYPERWHISPER-YF).
            LoggingService.Warn(
                $"HomeStatsBarViewModel: RecomputeAsync failed " +
                $"(exception_type={ex.GetType().FullName}, " +
                $"hresult={OptionalAssemblyGuard.DescribeHResult(ex.HResult)})");
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

    private static HomeStatisticsSnapshot? TryGetCachedTotals(int typingSpeedWpm, string timeZoneId, DateOnly localDay)
    {
        var cached = Volatile.Read(ref _cachedTotals);
        return cached != null
            && cached.TypingSpeedWpm == typingSpeedWpm
            && cached.TimeZoneId == timeZoneId
            && cached.LocalDay == localDay
                ? cached.Snapshot
                : null;
    }

    private static void StoreCachedTotals(int version, CachedTotals totals)
    {
        lock (CachedTotalsGate)
        {
            if (_cachedTotalsVersion == version)
                _cachedTotals = totals;
        }
    }

    /// <summary>
    /// Subscribes the cache to HistoryService once per process. It lives as long as
    /// the HistoryService singleton, so it is never removed.
    /// </summary>
    private static void EnsureCachedTotalsSubscribed()
    {
        lock (CachedTotalsGate)
        {
            if (_cachedTotalsSubscribed) return;
            var history = HistoryService.Instance;
            history.TranscriptAdded += (_, _) => InvalidateCachedTotals();
            history.TranscriptUpdated += (_, _) => InvalidateCachedTotals();
            history.TranscriptDeleted += (_, _) => InvalidateCachedTotals();
            _cachedTotalsSubscribed = true;
        }
    }

    private static void InvalidateCachedTotals()
    {
        lock (CachedTotalsGate)
        {
            _cachedTotalsVersion++;
            _cachedTotals = null;
        }
    }
}
