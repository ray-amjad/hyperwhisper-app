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
    private bool _hasShortcutConflicts;
    private string _shortcutConflictMessage = string.Empty;
    private bool _hasLowMicVolume;
    private string _lowMicVolumeMessage = string.Empty;
    private bool _lowMicVolumeDismissed;
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
        LoadGettingStarted();
    }

    // =====================================================================================
    // GETTING STARTED
    //
    // Windows stores the completed steps as ONE comma-separated setting and treats each card as
    // a toggle, not a one-way "done". Clicking a card that is not yet complete marks it AND
    // navigates; clicking a complete card only un-marks it, and the section comes back. The whole
    // section disappears once all four are complete.
    //
    // Linux had none of this: the four rows only navigated, so the list never shrank and never
    // showed progress.
    // =====================================================================================

    /// <summary>The Windows setting name, so an imported backup carries the state across.</summary>
    private const string GettingStartedSettingKey = "GettingStartedCompletedSteps";

    /// <summary>The four ids Windows uses, in the order the cards are drawn.</summary>
    public static IReadOnlyList<string> GettingStartedStepIds { get; } =
        ["recording", "shortcuts", "mode", "vocabulary"];

    private readonly HashSet<string> _gettingStartedCompleted = new(StringComparer.Ordinal);

    public bool ShowGettingStarted => _gettingStartedCompleted.Count < GettingStartedStepIds.Count;
    public bool IsRecordingStepComplete => _gettingStartedCompleted.Contains("recording");
    public bool IsShortcutsStepComplete => _gettingStartedCompleted.Contains("shortcuts");
    public bool IsModeStepComplete => _gettingStartedCompleted.Contains("mode");
    public bool IsVocabularyStepComplete => _gettingStartedCompleted.Contains("vocabulary");

    private void LoadGettingStarted()
    {
        var stored = _settings.Get(GettingStartedSettingKey, string.Empty) ?? string.Empty;
        foreach (var id in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (GettingStartedStepIds.Contains(id, StringComparer.Ordinal))
                _gettingStartedCompleted.Add(id);
    }

    /// <summary>
    /// Toggle one card. Returns true when the card just became COMPLETE, which is the only case
    /// in which Windows also navigates -- un-ticking a card leaves you where you are.
    /// </summary>
    public bool ToggleGettingStartedStep(string stepId)
    {
        if (!GettingStartedStepIds.Contains(stepId, StringComparer.Ordinal)) return false;

        var completed = _gettingStartedCompleted.Add(stepId);
        if (!completed) _gettingStartedCompleted.Remove(stepId);

        // Windows writes the ids sorted, so the setting has one canonical spelling per set.
        _settings.Set(GettingStartedSettingKey,
            string.Join(",", _gettingStartedCompleted.OrderBy(id => id, StringComparer.Ordinal)));
        var saved = _settings.Save();
        if (saved.IsFailure) Status.Failure(saved.Error!.Code, saved.Error.Message);

        Notify(nameof(ShowGettingStarted));
        Notify(nameof(IsRecordingStepComplete));
        Notify(nameof(IsShortcutsStepComplete));
        Notify(nameof(IsModeStepComplete));
        Notify(nameof(IsVocabularyStepComplete));
        return completed;
    }

    public UiStatus Status { get; } = new();
    public TranscriptionWorkflowViewModel? Recording { get; }
    public int HistoryCount { get => _historyCount; private set => Set(ref _historyCount, value); }
    public int VocabularyCount { get => _vocabularyCount; private set => Set(ref _vocabularyCount, value); }
    public int ModeCount { get => _modeCount; private set => Set(ref _modeCount, value); }
    /// <summary>
    /// The appcast failure message, or null when there is nothing to report. Windows shows the
    /// Recent Updates error card only while this is non-null (HomePage.xaml:217-227) and shows
    /// release cards otherwise. Nothing reads an appcast on Linux yet, so this stays null and the
    /// card stays hidden; the card used to be unconditional there, which told every Linux user on
    /// every launch that release notes had failed to load when nothing had been attempted.
    /// </summary>
    public string? ReleasesError
    {
        get => _releasesError;
        set { if (Set(ref _releasesError, value)) Notify(nameof(HasReleasesError)); }
    }
    private string? _releasesError;
    public bool HasReleasesError => !string.IsNullOrEmpty(ReleasesError);

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

    /// <summary>
    /// The two inline Home banners the Windows page carries. Both are hidden until the shell
    /// reports the fault, so the page opens on the stats bar exactly as Windows does.
    /// </summary>
    public bool HasShortcutConflicts
    {
        get => _hasShortcutConflicts;
        private set => Set(ref _hasShortcutConflicts, value);
    }

    public string ShortcutConflictMessage
    {
        get => _shortcutConflictMessage;
        private set => Set(ref _shortcutConflictMessage, value);
    }

    /// <summary>
    /// Raised by the shell when a global shortcut fails to register, which on Linux usually
    /// means another application or the desktop portal already holds the combination.
    /// </summary>
    public void ReportShortcutConflict(string? message)
    {
        ShortcutConflictMessage = message ?? string.Empty;
        HasShortcutConflicts = !string.IsNullOrWhiteSpace(ShortcutConflictMessage);
    }

    public void ClearShortcutConflict() => ReportShortcutConflict(null);

    public bool HasLowMicVolume
    {
        get => _hasLowMicVolume;
        private set => Set(ref _hasLowMicVolume, value);
    }

    public string LowMicVolumeMessage
    {
        get => _lowMicVolumeMessage;
        private set => Set(ref _lowMicVolumeMessage, value);
    }

    /// <summary>
    /// Reports the measured input level as too low. Nothing on Linux calls this yet: the
    /// platform layer exposes no input-level probe, so the banner stays hidden the way the
    /// Windows one does on a healthy machine. It is the seam a probe would report through.
    /// </summary>
    public void ReportLowMicVolume(string? message)
    {
        if (_lowMicVolumeDismissed) return;
        LowMicVolumeMessage = message ?? string.Empty;
        HasLowMicVolume = !string.IsNullOrWhiteSpace(LowMicVolumeMessage);
    }

    public void DismissLowMicVolume()
    {
        _lowMicVolumeDismissed = true;
        HasLowMicVolume = false;
    }

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
