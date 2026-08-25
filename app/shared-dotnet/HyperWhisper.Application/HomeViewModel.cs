using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Statistics;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class HomeViewModel : ViewModelBase
{
    private readonly HistoryRepository _history;
    private readonly VocabularyRepository _vocabulary;
    private readonly ModeRepository _modes;
    private readonly HomeStatisticsService _statistics;
    private readonly PortableSettingsService _settings;
    private int _historyCount;
    private int _vocabularyCount;
    private int _modeCount;
    private int _typingSpeedWordsPerMinute = 40;
    private HomeStatisticsSnapshot _statisticsSnapshot = HomeStatisticsCalculator.Calculate(
        [], 40, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

    public HomeViewModel(
        HistoryRepository history,
        VocabularyRepository vocabulary,
        ModeRepository modes,
        HomeStatisticsService statistics,
        PortableSettingsService settings,
        TranscriptionWorkflowViewModel? recording = null)
    {
        _history = history; _vocabulary = vocabulary; _modes = modes;
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _typingSpeedWordsPerMinute = NormalizeTypingSpeed(_settings.Get("advanced.typingSpeedWPM", 40));
        Recording = recording;
        RefreshCommand = new AsyncCommand(_ => RefreshAsync());
    }

    public UiStatus Status { get; } = new();
    public TranscriptionWorkflowViewModel? Recording { get; }
    public int HistoryCount { get => _historyCount; private set => Set(ref _historyCount, value); }
    public int VocabularyCount { get => _vocabularyCount; private set => Set(ref _vocabularyCount, value); }
    public int ModeCount { get => _modeCount; private set => Set(ref _modeCount, value); }
    public IReadOnlyList<int> TypingSpeedChoices { get; } = [30, 40, 50, 60, 80, 100];
    public int TypingSpeedWordsPerMinute
    {
        get => _typingSpeedWordsPerMinute;
        set
        {
            var normalized = NormalizeTypingSpeed(value);
            if (!Set(ref _typingSpeedWordsPerMinute, normalized)) return;
            _settings.Set("advanced.typingSpeedWPM", normalized);
            var saved = _settings.Save();
            if (saved.IsFailure) Status.Failure(saved.Error!.Code, saved.Error.Message);
            else _ = RefreshAsync();
        }
    }
    public PeriodStatistics ThisWeek => _statisticsSnapshot.ThisWeek;
    public PeriodStatistics ThisMonth => _statisticsSnapshot.ThisMonth;
    public PeriodStatistics AllTime => _statisticsSnapshot.AllTime;
    public int SavedThisWeekMinutes => _statisticsSnapshot.SavedThisWeekMinutes;
    public ICommand RefreshCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Status.Busy("Refreshing library…");
        try
        {
            var persistedTypingSpeed = NormalizeTypingSpeed(_settings.Get("advanced.typingSpeedWPM", 40));
            Set(ref _typingSpeedWordsPerMinute, persistedTypingSpeed, nameof(TypingSpeedWordsPerMinute));
            HistoryCount = (await _history.ListAsync(cancellationToken)).Count;
            VocabularyCount = (await _vocabulary.ListAsync(cancellationToken)).Count;
            ModeCount = (await _modes.ListAsync(cancellationToken)).Count;
            _statisticsSnapshot = await _statistics.GetAsync(
                TypingSpeedWordsPerMinute, DateTimeOffset.UtcNow, TimeZoneInfo.Local, cancellationToken);
            Notify(nameof(ThisWeek));
            Notify(nameof(ThisMonth));
            Notify(nameof(AllTime));
            Notify(nameof(SavedThisWeekMinutes));
            Recording?.RefreshDevices();
            Status.Success("Library ready");
        }
        catch (OperationCanceledException) { Status.Failure("home.cancelled", "Refresh cancelled"); }
        catch (Exception) { Status.Failure("home.refresh_failed", "Could not load the local library."); }
    }

    private static int NormalizeTypingSpeed(int value) => Math.Clamp(value, 1, 300);
}
