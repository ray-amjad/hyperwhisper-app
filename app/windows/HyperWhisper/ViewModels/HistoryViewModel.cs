using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Services.Transcription;
using HyperWhisper.ViewModels.Base;

namespace HyperWhisper.ViewModels;

/// <summary>
/// HISTORY VIEW MODEL
///
/// Main view model for the History page. Manages:
/// - Transcript list with search and filtering
/// - Grouping by date (Today, Yesterday, specific dates)
/// - Single and multi-selection
/// - Detail view state (selected transcript, audio playback)
/// - Commands for copy, delete, retry operations
///
/// DATA FLOW:
/// 1. LoadTranscripts() fetches from HistoryService on page load, populating
///    the master Transcripts ObservableCollection once.
/// 2. TranscriptsView is an ICollectionView over Transcripts that owns the
///    sort (Date desc), grouping (by GroupHeader), and filter predicate. The
///    XAML ListBox binds directly to it.
/// 3. Search/date filter updates re-evaluate the Filter predicate via a single
///    view Refresh — no Clear/Add churn on the underlying collection.
/// 4. HistoryService events update Transcripts in real-time; the view re-runs
///    Filter on changed items automatically.
/// </summary>
public partial class HistoryViewModel : ViewModelBase
{
    // =========================================================================
    // SERVICES
    // =========================================================================

    private readonly HistoryService _historyService;
    private readonly AudioPlaybackService _playbackService;
    private readonly TranscriptionRetryHandler _retryHandler;

    // =========================================================================
    // COLLECTIONS
    // =========================================================================

    /// <summary>All transcript view models (unfiltered, for lookup).</summary>
    private readonly Dictionary<Guid, TranscriptViewModel> _transcriptLookup = new();

    /// <summary>
    /// Master collection of all transcripts. Populated once at load time and
    /// mutated only when transcripts are added/updated/deleted. Filtering and
    /// sorting happen view-side via <see cref="TranscriptsView"/>.
    /// </summary>
    [ObservableProperty]
    private BulkObservableCollection<TranscriptViewModel> _transcripts = new();

    /// <summary>
    /// Filtered, sorted, grouped view over <see cref="Transcripts"/>. The XAML
    /// ListBox binds to this. Filter predicate, sort order, and group descriptions
    /// are configured in the constructor.
    /// </summary>
    public ICollectionView TranscriptsView { get; }

    // =========================================================================
    // SELECTION STATE
    // =========================================================================

    /// <summary>Currently selected transcript for detail view.</summary>
    [ObservableProperty]
    private TranscriptViewModel? _selectedTranscript;

    /// <summary>All selected transcripts (for multi-selection).</summary>
    [ObservableProperty]
    private ObservableCollection<TranscriptViewModel> _selectedTranscripts = new();

    /// <summary>Whether multiple items are selected.</summary>
    public bool HasMultipleSelection => SelectedTranscripts.Count > 1;

    /// <summary>Whether any item is selected.</summary>
    public bool HasSelection => SelectedTranscript != null || SelectedTranscripts.Count > 0;

    /// <summary>Count of selected items for display.</summary>
    public int SelectionCount => SelectedTranscripts.Count;

    // =========================================================================
    // FILTERING STATE
    // =========================================================================

    /// <summary>Search text for filtering transcripts.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Date filter for filtering transcripts.</summary>
    [ObservableProperty]
    private DateFilter _dateFilter = DateFilter.All;

    // =========================================================================
    // PLAYBACK STATE
    // =========================================================================

    /// <summary>Whether audio is currently playing.</summary>
    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>Current playback position.</summary>
    [ObservableProperty]
    private TimeSpan _playbackPosition;

    /// <summary>Total duration of loaded audio.</summary>
    [ObservableProperty]
    private TimeSpan _playbackDuration;

    /// <summary>Whether the selected transcript has an audio file that exists but failed to load.</summary>
    [ObservableProperty]
    private bool _playbackLoadFailed;

    /// <summary>Formatted current position (e.g., "1:23").</summary>
    public string FormattedPosition => FormatTimeSpan(PlaybackPosition);

    /// <summary>Formatted total duration (e.g., "2:45").</summary>
    public string FormattedDuration => FormatTimeSpan(PlaybackDuration);

    /// <summary>Whether the selected transcript's audio is loaded and ready for playback controls.</summary>
    public bool IsPlaybackAvailable => SelectedTranscript?.HasAudio == true && !PlaybackLoadFailed;

    /// <summary>Whether the audio unavailable message should be shown for the selected transcript.</summary>
    public bool ShowAudioUnavailableMessage => SelectedTranscript != null && !IsPlaybackAvailable;

    /// <summary>Message shown when the selected transcript cannot be played.</summary>
    public string AudioUnavailableMessage => PlaybackLoadFailed
        ? Loc.S("history.audio.loadFailed")
        : Loc.S("history.audio.notAvailable");

    /// <summary>
    /// Set by OnPlaybackPositionChanged while propagating a player-driven update
    /// so the two-way binding's write-back to PlaybackPositionSeconds does not
    /// trigger a redundant (and jittery) Seek during playback.
    /// </summary>
    private bool _suppressSeekFromBinding;

    /// <summary>
    /// Current playback position as seconds, for two-way binding to the seek Slider.
    /// Setting this value seeks the underlying playback service, unless the
    /// update originated from the player itself.
    /// </summary>
    public double PlaybackPositionSeconds
    {
        get => PlaybackPosition.TotalSeconds;
        set
        {
            var clamped = value;
            if (clamped < 0) clamped = 0;
            var max = PlaybackDuration.TotalSeconds;
            if (max > 0 && clamped > max) clamped = max;

            var target = TimeSpan.FromSeconds(clamped);
            if (target == PlaybackPosition) return;

            if (_suppressSeekFromBinding)
            {
                // Player-driven update; just mirror it without re-seeking.
                PlaybackPosition = target;
                return;
            }

            // User-driven update (slider drag / click). Seek and update state.
            _playbackService.Seek(target);
            PlaybackPosition = target;
        }
    }

    /// <summary>Total duration as seconds, for binding to the seek Slider's Maximum.</summary>
    public double PlaybackDurationSeconds => PlaybackDuration.TotalSeconds;

    // =========================================================================
    // DETAIL VIEW STATE
    // =========================================================================

    /// <summary>Whether to show raw text instead of processed text.</summary>
    [ObservableProperty]
    private bool _showRawText;

    /// <summary>
    /// True for a short window (1.5s) after a successful copy. Drives the
    /// Copy button's "Copied!" toast feedback in the History page.
    /// </summary>
    [ObservableProperty]
    private bool _isCopiedRecently;

    /// <summary>Timer that resets <see cref="IsCopiedRecently"/> after the feedback window.</summary>
    private System.Windows.Threading.DispatcherTimer? _copyFeedbackTimer;

    /// <summary>Text currently displayed in detail view.</summary>
    public string CurrentDisplayText
    {
        get
        {
            if (SelectedTranscript == null) return "";
            return ShowRawText
                ? SelectedTranscript.RawDisplayText
                : SelectedTranscript.ProcessedDisplayText;
        }
    }

    // =========================================================================
    // EMPTY STATE
    // =========================================================================

    /// <summary>Whether there are no transcripts at all.</summary>
    public bool IsEmpty => _transcriptLookup.Count == 0;

    /// <summary>
    /// Whether the filtered view has no items, but the underlying collection
    /// has some — i.e. the search/date filter excluded everything. Uses the
    /// view (not Transcripts.Count) so it reflects the active filter.
    /// </summary>
    public bool IsFilteredEmpty => !IsEmpty && !TranscriptsView.Cast<object>().Any();

    /// <summary>Dynamic hotkey instruction using the user's configured shortcut.</summary>
    public string HotkeyInstruction => Loc.S("status.ready.withHotkey",
        SettingsService.Instance.ToggleShortcut.ToDisplayString());

    // =========================================================================
    // MODES (FOR RETRY WITH MODE)
    // =========================================================================

    /// <summary>Available modes for "Retry with..." menu.</summary>
    [ObservableProperty]
    private List<Mode> _availableModes = new();

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public HistoryViewModel()
    {
        _historyService = HistoryService.Instance;
        _playbackService = new AudioPlaybackService();
        _retryHandler = new TranscriptionRetryHandler();

        // Configure the filtered/sorted/grouped view over Transcripts. CVS.GetDefaultView
        // returns the per-collection default ICollectionView — the same instance any
        // ItemsControl bound to Transcripts would use. Setting sort/group/filter here
        // keeps the view layer as the single source of truth.
        TranscriptsView = CollectionViewSource.GetDefaultView(Transcripts);
        using (TranscriptsView.DeferRefresh())
        {
            TranscriptsView.SortDescriptions.Add(
                new SortDescription(nameof(TranscriptViewModel.Date), ListSortDirection.Descending));
            TranscriptsView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(TranscriptViewModel.GroupHeader)));
            TranscriptsView.Filter = FilterPredicate;
        }

        // 250ms debounce coalesces per-keystroke search updates into a single
        // Refresh after the user stops typing. Pattern from app/windows/CLAUDE.md
        // §6 "Debouncing — Coalesce Rapid Events".
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Tick += OnSearchDebounceTick;

        // Schedule a tick at local midnight so "Today" / "Yesterday" group headers
        // stay accurate without restarting the app.
        _midnightTimer = new DispatcherTimer();
        _midnightTimer.Tick += OnMidnightTick;
        ScheduleNextMidnightTick();

        // Subscribe to history service events
        _historyService.TranscriptAdded += OnTranscriptAdded;
        _historyService.TranscriptUpdated += OnTranscriptUpdated;
        _historyService.TranscriptDeleted += OnTranscriptDeleted;

        // Subscribe to playback events
        _playbackService.PositionChanged += HandlePlaybackPositionChanged;
        _playbackService.PlaybackEnded += HandlePlaybackEnded;
        _playbackService.DurationReady += HandleDurationReady;
        _playbackService.PlaybackFailed += HandlePlaybackFailed;

        // Load modes for retry menu
        _availableModes = ModeService.Instance.GetAllModes();
        ModeService.Instance.ModeChanged += (s, e) =>
        {
            AvailableModes = ModeService.Instance.GetAllModes();
        };
    }

    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly DispatcherTimer _midnightTimer;

    // =========================================================================
    // INITIALIZATION
    // =========================================================================

    public override async Task OnNavigatedToAsync()
    {
        if (IsInitialized)
        {
            // App may have been suspended across midnight — refresh group headers
            // so "Today" / "Yesterday" pick up the new local date.
            RefreshDateGrouping();
            return;
        }
        IsLoading = true;

        try
        {
            await Task.Run(() => LoadTranscripts());
            IsInitialized = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadTranscripts()
    {
        var transcripts = _historyService.GetAllTranscripts();
        var transcriptViewModels = transcripts
            .Select(transcript => new TranscriptViewModel(transcript))
            .ToList();

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            _transcriptLookup.Clear();
            foreach (var vm in transcriptViewModels)
            {
                _transcriptLookup[vm.Id] = vm;
            }

            Transcripts.ReplaceRange(transcriptViewModels);
            NotifyEmptyStateChanged();
        });

        LoggingService.Info($"HistoryViewModel: Loaded {transcripts.Count} transcripts");
    }

    // =========================================================================
    // FILTERING
    // =========================================================================

    partial void OnSearchTextChanged(string value)
    {
        // Coalesce per-keystroke updates: restart the timer; ApplyFilters fires
        // 250ms after the last edit. Empty-string clears still pay the same
        // debounce, which is fine — the user perceives "Refresh after I stop".
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        ApplyFilters();
    }

    partial void OnDateFilterChanged(DateFilter value)
    {
        // Date filter is a discrete pill click, not typed input — no debounce.
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        TranscriptsView.Refresh();
        NotifyEmptyStateChanged();
    }

    /// <summary>
    /// Filter predicate evaluated by <see cref="TranscriptsView"/> against each
    /// <see cref="TranscriptViewModel"/>. Returns true when the item matches both
    /// the current search text (case-insensitive Contains over the raw and
    /// processed text) and the current date filter window. Pure in-memory —
    /// no SQLite round-trip per keystroke.
    /// </summary>
    private bool FilterPredicate(object item)
    {
        if (item is not TranscriptViewModel vm) return false;

        var search = SearchText;
        if (!string.IsNullOrWhiteSpace(search))
        {
            bool matches =
                (vm.TranscribedText?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (vm.PostProcessedText?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (vm.Text?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!matches) return false;
        }

        if (DateFilter == DateFilter.All) return true;

        // Date filtering uses local time to match the group headers the user sees
        // ("Today" / "Yesterday" are computed in local time on the VM).
        var localDate = vm.Date.ToLocalTime();
        var now = DateTime.Now;
        return DateFilter switch
        {
            DateFilter.Today => localDate.Date == now.Date,
            DateFilter.ThisWeek => localDate >= now.Date.AddDays(-(int)now.DayOfWeek)
                                && localDate < now.Date.AddDays(7 - (int)now.DayOfWeek),
            DateFilter.ThisMonth => localDate.Year == now.Year && localDate.Month == now.Month,
            _ => true,
        };
    }

    // =========================================================================
    // MIDNIGHT REFRESH
    // =========================================================================

    private void ScheduleNextMidnightTick()
    {
        var now = DateTime.Now;
        var nextMidnight = now.Date.AddDays(1);
        var delta = nextMidnight - now;
        // Guard against pathological zero/negative intervals from clock skew.
        if (delta <= TimeSpan.Zero) delta = TimeSpan.FromMinutes(1);
        _midnightTimer.Interval = delta;
        _midnightTimer.Start();
    }

    private void OnMidnightTick(object? sender, EventArgs e)
    {
        _midnightTimer.Stop();
        RefreshDateGrouping();
        ScheduleNextMidnightTick();
    }

    /// <summary>
    /// Re-raises <see cref="TranscriptViewModel.GroupHeader"/> changes for any
    /// transcript whose local date sits in the window where the label could
    /// have rolled over (today, yesterday, day-before-yesterday), then refreshes
    /// the view so groups re-bucket. Avoids touching the entire collection.
    /// </summary>
    private void RefreshDateGrouping()
    {
        var threshold = DateTime.Now.Date.AddDays(-2);
        foreach (var vm in Transcripts)
        {
            if (vm.Date.ToLocalTime().Date >= threshold)
            {
                vm.RefreshGroupHeader();
            }
        }
        TranscriptsView.Refresh();
    }

    // =========================================================================
    // SELECTION
    // =========================================================================

    partial void OnSelectedTranscriptChanged(TranscriptViewModel? value)
    {
        // Drop any pending Copy feedback — selection change makes it stale.
        _copyFeedbackTimer?.Stop();
        IsCopiedRecently = false;

        // Stop playback when selection changes
        StopPlayback();
        ShowRawText = false;

        // Reset playback state before loading the next file
        PlaybackPosition = TimeSpan.Zero;
        PlaybackDuration = TimeSpan.Zero;
        PlaybackLoadFailed = false;

        // Load audio for new selection
        if (value?.HasAudio == true)
        {
            // Eagerly seed PlaybackDuration from the stored entity value so the
            // denominator is correct instantly. NAudio's AudioFileReader reports
            // TotalTime synchronously in Load(), so DurationReady will fire and
            // overwrite this with the authoritative value moments later.
            if (value.Duration > 0)
            {
                PlaybackDuration = TimeSpan.FromSeconds(value.Duration);
            }

            var loaded = _playbackService.Load(value.AudioFilePath!);
            if (!loaded)
            {
                PlaybackPosition = TimeSpan.Zero;
                PlaybackDuration = TimeSpan.Zero;
                PlaybackLoadFailed = true;
            }
            else if (PlaybackDuration == TimeSpan.Zero)
            {
                // Fallback: if Load somehow didn't fire DurationReady, read
                // TotalDuration directly. This also covers the case where the
                // entity had no duration stored.
                PlaybackDuration = _playbackService.TotalDuration;
            }
        }

        OnPropertyChanged(nameof(CurrentDisplayText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsPlaybackAvailable));
        OnPropertyChanged(nameof(ShowAudioUnavailableMessage));
        OnPropertyChanged(nameof(AudioUnavailableMessage));
    }

    partial void OnPlaybackLoadFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlaybackAvailable));
        OnPropertyChanged(nameof(ShowAudioUnavailableMessage));
        OnPropertyChanged(nameof(AudioUnavailableMessage));
    }

    /// <summary>
    /// Triggered by ObservableProperty codegen whenever PlaybackDuration changes.
    /// Keeps the computed seek-slider max and formatted string in sync.
    /// </summary>
    partial void OnPlaybackDurationChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(FormattedDuration));
        OnPropertyChanged(nameof(PlaybackDurationSeconds));
    }

    /// <summary>
    /// Triggered by ObservableProperty codegen whenever PlaybackPosition changes.
    /// Keeps the slider's two-way binding and formatted string in sync.
    /// </summary>
    partial void OnPlaybackPositionChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(FormattedPosition));

        // Suppress the write-back loop: the Slider's two-way binding will call
        // our PlaybackPositionSeconds setter in response to this notification,
        // and we don't want that to trigger another Seek.
        _suppressSeekFromBinding = true;
        try
        {
            OnPropertyChanged(nameof(PlaybackPositionSeconds));
        }
        finally
        {
            _suppressSeekFromBinding = false;
        }
    }

    partial void OnShowRawTextChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentDisplayText));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedTranscript = null;
        SelectedTranscripts.Clear();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(SelectionCount));
    }

    /// <summary>
    /// Updates the selection state. Called from the view when selection changes.
    /// </summary>
    public void UpdateSelection(IEnumerable<TranscriptViewModel> selectedItems)
    {
        SelectedTranscripts.Clear();
        foreach (var item in selectedItems)
        {
            SelectedTranscripts.Add(item);
        }

        // Update single selection for detail view
        SelectedTranscript = SelectedTranscripts.Count == 1 ? SelectedTranscripts[0] : null;

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(SelectionCount));
    }

    // =========================================================================
    // COMMANDS - COPY
    // =========================================================================

    [RelayCommand]
    private void CopyText()
    {
        if (SelectedTranscript == null) return;

        var text = ShowRawText
            ? SelectedTranscript.RawDisplayText
            : SelectedTranscript.ProcessedDisplayText;

        try
        {
            WpfClipboard.SetText(text);
            LoggingService.Info("HistoryViewModel: Copied text to clipboard");
            ShowCopyFeedback();
        }
        catch (Exception ex)
        {
            LoggingService.Error($"HistoryViewModel: Failed to copy text: {ex.Message}");
        }
    }

    /// <summary>
    /// Flips <see cref="IsCopiedRecently"/> on and schedules it to flip off
    /// after 1.5 seconds. Idempotent — repeated invocations restart the timer
    /// so the toast stays visible while the user keeps clicking Copy.
    /// </summary>
    private void ShowCopyFeedback()
    {
        IsCopiedRecently = true;

        // Stop any pending reset so rapid clicks restart the window.
        if (_copyFeedbackTimer != null)
        {
            _copyFeedbackTimer.Stop();
        }
        else
        {
            _copyFeedbackTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500),
            };
            _copyFeedbackTimer.Tick += OnCopyFeedbackTimerTick;
        }

        _copyFeedbackTimer.Start();
    }

    private void OnCopyFeedbackTimerTick(object? sender, EventArgs e)
    {
        _copyFeedbackTimer?.Stop();
        IsCopiedRecently = false;
    }

    // =========================================================================
    // COMMANDS - DELETE
    // =========================================================================

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedTranscripts.Count == 0) return;

        var count = SelectedTranscripts.Count;
        var message = count == 1
            ? Loc.S("transcripts.delete.single.message")
            : Loc.S("transcripts.delete.multiple.message", count);

        var title = count == 1
            ? Loc.S("transcripts.delete.single.title")
            : Loc.S("transcripts.delete.multiple.title", count);

        var result = WpfMessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        // Mark as deleting
        foreach (var vm in SelectedTranscripts)
        {
            vm.IsDeleting = true;
        }

        var ids = SelectedTranscripts.Select(t => t.Id).ToList();

        await Task.Run(() =>
        {
            _historyService.DeleteTranscripts(ids);
        });

        ClearSelection();
    }

    [RelayCommand]
    private async Task DeleteTranscriptAsync(TranscriptViewModel? transcript)
    {
        if (transcript == null) return;

        var result = WpfMessageBox.Show(
            Loc.S("transcripts.delete.single.message"),
            Loc.S("transcripts.delete.single.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        transcript.IsDeleting = true;

        await Task.Run(() =>
        {
            _historyService.DeleteTranscript(transcript.Id);
        });

        if (SelectedTranscript?.Id == transcript.Id)
        {
            ClearSelection();
        }
    }

    // =========================================================================
    // COMMANDS - RETRY
    // =========================================================================

    [RelayCommand]
    private async Task RetryTranscriptAsync()
    {
        if (SelectedTranscript == null || !SelectedTranscript.CanRetry) return;

        var transcript = SelectedTranscript;
        var mode = ModeService.Instance.GetAllModes()
            .FirstOrDefault(m => m.Name == transcript.Mode)
            ?? ModeService.Instance.GetDefaultMode();

        if (mode == null)
        {
            WpfMessageBox.Show(Loc.S("errors.noModeForRetry"), Loc.S("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await RetryWithModeAsync(mode);
    }

    [RelayCommand]
    private async Task RetryWithModeAsync(Mode? mode)
    {
        if (SelectedTranscript == null || mode == null) return;
        if (!_retryHandler.CanRetry(SelectedTranscript)) return;

        try
        {
            await _retryHandler.RetryTranscriptionAsync(SelectedTranscript, mode);
            LoggingService.Info($"HistoryViewModel: Retry successful for transcript {SelectedTranscript.Id}");
        }
        catch (Exception ex)
        {
            LoggingService.Error($"HistoryViewModel: Retry failed: {ex.Message}");
            WpfMessageBox.Show(Loc.S("errors.retryFailed", ex.Message), Loc.S("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =========================================================================
    // COMMANDS - PLAYBACK
    // =========================================================================

    [RelayCommand]
    private void TogglePlayback()
    {
        if (!_playbackService.IsLoaded) return;

        _playbackService.TogglePlayPause();
        IsPlaying = _playbackService.IsPlaying;
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _playbackService.Stop();
        IsPlaying = false;
        PlaybackPosition = TimeSpan.Zero;
    }

    private void HandlePlaybackPositionChanged(TimeSpan position)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            PlaybackPosition = position;
        });
    }

    /// <summary>
    /// Seeks playback to the given position (in seconds). Bound from the
    /// progress slider's Thumb.DragCompleted event in HistoryPage.xaml.cs.
    /// </summary>
    [RelayCommand]
    private void Seek(double positionSeconds)
    {
        if (!_playbackService.IsLoaded) return;

        PlaybackPositionSeconds = positionSeconds;
    }

    private void HandleDurationReady(TimeSpan duration)
    {
        // Fires on NAudio / service caller thread; marshal to UI thread.
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        dispatcher.BeginInvoke(() =>
        {
            PlaybackDuration = duration;
        });
    }

    private void HandlePlaybackEnded()
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            IsPlaying = false;
            PlaybackPosition = TimeSpan.Zero;
        });
    }

    private void HandlePlaybackFailed(Exception exception)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        dispatcher.BeginInvoke(() =>
        {
            LoggingService.Warn($"HistoryViewModel: Playback failed - {exception.Message}");
            IsPlaying = false;
            PlaybackPosition = TimeSpan.Zero;
            PlaybackDuration = TimeSpan.Zero;
            PlaybackLoadFailed = true;
        });
    }

    // =========================================================================
    // COMMANDS - RAW/PROCESSED TOGGLE
    // =========================================================================

    [RelayCommand]
    private void ToggleRawText()
    {
        ShowRawText = !ShowRawText;
    }

    // =========================================================================
    // COMMANDS - DATE FILTER
    // =========================================================================

    [RelayCommand]
    private void SetDateFilter(DateFilter filter)
    {
        DateFilter = filter;
    }

    // =========================================================================
    // EVENT HANDLERS - HISTORY SERVICE
    // =========================================================================

    private void OnTranscriptAdded(object? sender, Transcript transcript)
    {
        WpfApplication.Current.Dispatcher.BeginInvoke(() =>
        {
            var vm = new TranscriptViewModel(transcript);
            _transcriptLookup[transcript.Id] = vm;

            // Order doesn't matter: TranscriptsView sorts by Date desc.
            Transcripts.Add(vm);
            NotifyEmptyStateChanged();
        });
    }

    private void OnTranscriptUpdated(object? sender, Transcript transcript)
    {
        WpfApplication.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_transcriptLookup.TryGetValue(transcript.Id, out var vm))
            {
                // Update the source and refresh
                var entity = vm.ToEntity();
                entity.Text = transcript.Text;
                entity.TranscribedText = transcript.TranscribedText;
                entity.PostProcessedText = transcript.PostProcessedText;
                entity.Status = transcript.Status;
                entity.FailedReason = transcript.FailedReason;
                entity.TranscriptionProvider = transcript.TranscriptionProvider;
                entity.PostProcessingProvider = transcript.PostProcessingProvider;
                entity.RetryCount = transcript.RetryCount;
                entity.LastRetryDate = transcript.LastRetryDate;

                vm.Refresh();

                // If this is the selected transcript, update detail view
                if (SelectedTranscript?.Id == transcript.Id)
                {
                    OnPropertyChanged(nameof(CurrentDisplayText));
                }
            }
        });
    }

    private void OnTranscriptDeleted(object? sender, Guid id)
    {
        WpfApplication.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_transcriptLookup.TryGetValue(id, out var vm))
            {
                _transcriptLookup.Remove(id);
                Transcripts.Remove(vm);
                SelectedTranscripts.Remove(vm);

                if (SelectedTranscript?.Id == id)
                {
                    SelectedTranscript = null;
                }

                NotifyEmptyStateChanged();
            }
        });
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    private static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return span.ToString(@"h\:mm\:ss");
        }
        return span.ToString(@"m\:ss");
    }

    // =========================================================================
    // CLEANUP
    // =========================================================================

    public void Cleanup()
    {
        _historyService.TranscriptAdded -= OnTranscriptAdded;
        _historyService.TranscriptUpdated -= OnTranscriptUpdated;
        _historyService.TranscriptDeleted -= OnTranscriptDeleted;

        _playbackService.PositionChanged -= HandlePlaybackPositionChanged;
        _playbackService.PlaybackEnded -= HandlePlaybackEnded;
        _playbackService.DurationReady -= HandleDurationReady;
        _playbackService.PlaybackFailed -= HandlePlaybackFailed;

        _playbackService.Dispose();
        _retryHandler.Dispose();

        // Stop and release the copy feedback timer so we don't leak the tick
        // handler reference across VM lifetimes.
        if (_copyFeedbackTimer != null)
        {
            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer.Tick -= OnCopyFeedbackTimerTick;
            _copyFeedbackTimer = null;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= OnSearchDebounceTick;

        _midnightTimer.Stop();
        _midnightTimer.Tick -= OnMidnightTick;
    }
}
