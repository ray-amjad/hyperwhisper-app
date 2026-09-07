using System.Collections.ObjectModel;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// A day header in the history list. Windows groups the rows by day and draws the header from
/// <c>TranscriptViewModel.GroupHeader</c>: "Today", "Yesterday", or the formatted date. The two
/// relative headers are catalogue keys rather than text, because this assembly holds no
/// localizer; the shell resolves <see cref="LocalizationKey"/> and falls back to
/// <see cref="Text"/> for a dated header, which needs no translation.
/// </summary>
public sealed record HistoryDateGroup(string? LocalizationKey, string Text);

public sealed class HistoryViewModel : ViewModelBase, IDisposable
{
    private readonly HistoryRepository _repository;
    private readonly IAudioPlaybackService? _playback;
    private readonly Func<Transcript, CancellationToken, Task<PortableTranscriptionResult>>? _retry;
    private readonly Func<Transcript, Mode?, CancellationToken, Task<PortableTranscriptionResult>>? _retryWithMode;
    private readonly ITextInjectionService? _textInjection;
    private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
    private Transcript? _selected;
    private string _searchText = string.Empty;
    private DateTimeOffset? _startDate;
    private DateTimeOffset? _endDate;
    private bool _showRawTranscript;
    private bool _deleteAudio;
    private bool _isRetrying;
    private bool _isPlaying;
    private bool _playbackLoadFailed;
    private double _playbackPositionSeconds;
    private double _playbackDurationSeconds;
    private bool _isCopiedRecently;
    private CancellationTokenSource? _copyFeedback;
    private bool _disposed;
    private bool _lastQueryWasFiltered;
    private string _hotkeyInstruction = string.Empty;
    public HistoryViewModel(
        HistoryRepository repository,
        IAudioPlaybackService? playback = null,
        Func<Transcript, CancellationToken, Task<PortableTranscriptionResult>>? retry = null,
        Func<Transcript, Mode?, CancellationToken, Task<PortableTranscriptionResult>>? retryWithMode = null,
        IEnumerable<Mode>? retryModes = null,
        ITextInjectionService? textInjection = null)
    {
        _repository = repository;
        _playback = playback;
        _retry = retry;
        _retryWithMode = retryWithMode;
        _textInjection = textInjection;
        if (retryModes is not null)
            foreach (var mode in retryModes) AvailableRetryModes.Add(mode);
        RefreshCommand = new AsyncCommand(_ => RefreshAsync());
        DeleteCommand = new AsyncCommand(_ => DeleteSelectedAsync(), _ => SelectedItems.Count > 0);
        SearchCommand = new AsyncCommand(_ => SearchAsync());
        ClearFiltersCommand = new AsyncCommand(_ => ClearFiltersAsync());
        PlayCommand = new AsyncCommand(_ => TogglePlaybackAsync(), _ => IsPlaybackAvailable);
        StopPlaybackCommand = new AsyncCommand(_ => { StopPlayback(); return Task.CompletedTask; }, _ => _playback?.IsLoaded == true);
        CopyCommand = new AsyncCommand(_ => CopyAsync(), _ => Selected is not null && _textInjection is not null);
        RetryCommand = new AsyncCommand(_ => RetryAsync(), _ => CanRetry);
        if (_playback is not null)
        {
            _playback.PlaybackEnded += OnPlaybackEnded;
            _playback.PositionChanged += OnPlaybackPositionChanged;
            _playback.DurationReady += OnPlaybackDurationReady;
            _playback.PlaybackFailed += OnPlaybackFailed;
        }
        Items.CollectionChanged += (_, _) => RebuildGroups();
    }
    public ObservableCollection<Transcript> Items { get; } = new();
    /// <summary>
    /// <see cref="Items"/> with a <see cref="HistoryDateGroup"/> inserted before each new day, so
    /// one list control can draw the day headers Windows gets from an ICollectionView group.
    /// </summary>
    public ObservableCollection<object> GroupedItems { get; } = new();
    public ObservableCollection<Transcript> SelectedItems { get; } = new();
    public ObservableCollection<Mode> AvailableRetryModes { get; } = new();
    public Mode? SelectedRetryMode { get; set; }
    public Transcript? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ResetCopyFeedback();
            LoadSelectedPlayback();
            if (!HasRawTranscript) _showRawTranscript = false;
            Notify(nameof(ShowRawTranscript));
            NotifyDetail();
            Notify(nameof(CanRetry));
            Notify(nameof(SelectedStatusLabel));
            Notify(nameof(SelectedFailureReason));
            Notify(nameof(IsSelectedFailed));
            Notify(nameof(IsSelectedRetrying));
            Notify(nameof(HasRetryInfo));
            Notify(nameof(SelectedFormattedDate));
            Notify(nameof(ShowAudioUnavailableMessage));
            ((AsyncCommand)RetryCommand).RaiseCanExecuteChanged();
        }
    }
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public DateTimeOffset? StartDate { get => _startDate; set => Set(ref _startDate, value); }
    public DateTimeOffset? EndDate { get => _endDate; set => Set(ref _endDate, value); }
    public bool ShowRawTranscript
    {
        get => _showRawTranscript;
        set
        {
            var next = value && HasRawTranscript;
            if (Set(ref _showRawTranscript, next)) NotifyDetail();
        }
    }
    public bool HasRawTranscript => !string.IsNullOrWhiteSpace(Selected?.TranscribedText);
    public string DetailText => ShowRawTranscript && HasRawTranscript
        ? Selected!.TranscribedText!
        : !string.IsNullOrWhiteSpace(Selected?.PostProcessedText)
            ? Selected!.PostProcessedText!
            : Selected?.Text ?? string.Empty;
    public string DetailLabel => ShowRawTranscript && HasRawTranscript
        ? "Raw transcription"
        : !string.IsNullOrWhiteSpace(Selected?.PostProcessedText)
            ? "Post-processed transcript"
            : "Final transcript";
    public bool DeleteAudio { get => _deleteAudio; set => Set(ref _deleteAudio, value); }
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) Notify(nameof(PlayPauseLabel)); } }
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";
    public bool PlaybackLoadFailed { get => _playbackLoadFailed; private set { if (Set(ref _playbackLoadFailed, value)) Notify(nameof(IsPlaybackAvailable)); } }
    public bool IsPlaybackAvailable => Selected?.AudioFilePath is not null && _playback?.IsLoaded == true && !PlaybackLoadFailed;
    public double PlaybackDurationSeconds { get => _playbackDurationSeconds; private set => Set(ref _playbackDurationSeconds, value); }
    public double PlaybackPositionSeconds
    {
        get => _playbackPositionSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, PlaybackDurationSeconds);
            if (!Set(ref _playbackPositionSeconds, clamped)) return;
            _playback?.Seek(TimeSpan.FromSeconds(clamped));
        }
    }
    public string FormattedPlaybackPosition => FormatTime(PlaybackPositionSeconds);
    public string FormattedPlaybackDuration => FormatTime(PlaybackDurationSeconds);
    /// <summary>Windows draws one "position / duration" run, not two separate chips.</summary>
    public string FormattedPlayback => $"{FormattedPlaybackPosition} / {FormattedPlaybackDuration}";
    /// <summary>
    /// A transcript is selected but its audio will not play — it was never kept, or the file has
    /// gone. Windows swaps the whole playback band for a one-line warning in that case.
    /// </summary>
    public bool ShowAudioUnavailableMessage => Selected is not null && !IsPlaybackAvailable;
    /// <summary>The full date at the top of the detail pane, at 20px SemiBold on Windows.</summary>
    public string SelectedFormattedDate => Selected is null
        ? string.Empty
        : Selected.Date.ToLocalTime().ToString("f", System.Globalization.CultureInfo.CurrentCulture);
    /// <summary>
    /// The "press {shortcut} to record" line under the empty state. The shell owns the shortcut
    /// text, so it assigns the finished sentence here.
    /// </summary>
    public string HotkeyInstruction { get => _hotkeyInstruction; set => Set(ref _hotkeyInstruction, value); }
    /// <summary>No transcripts at all, with no search or date filter applied.</summary>
    public bool IsEmpty => Items.Count == 0 && !_lastQueryWasFiltered;
    /// <summary>A filter is applied and it matched nothing. Windows shows a different panel.</summary>
    public bool IsFilteredEmpty => Items.Count == 0 && _lastQueryWasFiltered;
    public bool HasSelection => SelectedItems.Count > 0;
    public bool HasMultipleSelection => SelectedItems.Count > 1;
    public int SelectionCount => SelectedItems.Count;
    public bool IsCopiedRecently { get => _isCopiedRecently; private set => Set(ref _isCopiedRecently, value); }
    public string CopyLabel => IsCopiedRecently ? "Copied!" : "Copy";
    public string SelectedStatusLabel => Selected?.Status switch
    {
        TranscriptStatus.Completed => "Completed",
        TranscriptStatus.Failed => "Failed",
        TranscriptStatus.Processing when Selected.RetryCount > 0 => "Retrying",
        TranscriptStatus.Processing => "Processing",
        _ => "Unknown",
    };
    public bool IsSelectedFailed => Selected?.Status == TranscriptStatus.Failed;
    public bool IsSelectedRetrying => Selected is { Status: TranscriptStatus.Processing, RetryCount: > 0 };
    /// <summary>
    /// True once this transcript has been retried at least once. Windows swaps the detail Retry
    /// button between history.context.retry and history.context.retryAgain on it
    /// (TranscriptViewModel.cs:331, HistoryPage.xaml:492).
    /// </summary>
    public bool HasRetryInfo => Selected is { RetryCount: > 0 };
    public string SelectedFailureReason => IsSelectedFailed ? Selected?.FailedReason ?? "Transcription failed" : string.Empty;
    public bool IsRetrying
    {
        get => _isRetrying;
        private set
        {
            if (!Set(ref _isRetrying, value)) return;
            Notify(nameof(CanRetry));
            ((AsyncCommand)RetryCommand).RaiseCanExecuteChanged();
        }
    }
    public bool CanRetry => !IsRetrying && (_retry is not null || _retryWithMode is not null) && Selected is
        { Status: TranscriptStatus.Failed, AudioFilePath: not null };
    public UiStatus Status { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand RetryCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Status.Busy("Loading history…");
        try
        {
            _lastQueryWasFiltered = false;
            Items.Clear();
            foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item);
            UpdateSelection(Items.Take(1));
            Status.Success(Items.Count == 0 ? "No transcripts yet" : $"{Items.Count} transcript(s)");
        }
        catch (OperationCanceledException) { Status.Failure("history.cancelled", "History load cancelled"); }
        catch (Exception) { Status.Failure("history.load_failed", "Could not load transcript history."); }
    }

    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        if (StartDate is { } start && EndDate is { } end && start.Date > end.Date)
        {
            Status.Failure("history.date_range_invalid", "The start date must not be after the end date.");
            return;
        }
        try
        {
            _lastQueryWasFiltered = !string.IsNullOrWhiteSpace(SearchText)
                || StartDate is not null || EndDate is not null;
            Items.Clear();
            var fromUtc = StartDate is { } from ? LocalDateStartUtc(from) : (DateTime?)null;
            var toUtcExclusive = EndDate is { } to ? LocalDateStartUtc(to, dayOffset: 1) : (DateTime?)null;
            foreach (var item in await _repository.SearchAsync(
                SearchText, fromUtc, toUtcExclusive, cancellationToken)) Items.Add(item);
            UpdateSelection(Items.Take(1));
            Status.Success($"{Items.Count} matching transcript(s)");
        }
        catch (Exception) { Status.Failure("history.search_failed", "Could not search transcript history."); }
    }

    public async Task ClearFiltersAsync(CancellationToken cancellationToken = default)
    {
        SearchText = string.Empty;
        StartDate = null;
        EndDate = null;
        await RefreshAsync(cancellationToken);
    }

    public void UpdateSelection(IEnumerable<Transcript> transcripts)
    {
        SelectedItems.Clear();
        foreach (var transcript in transcripts.DistinctBy(item => item.Id)) SelectedItems.Add(transcript);
        Selected = SelectedItems.Count == 1 ? SelectedItems[0] : null;
        Notify(nameof(HasSelection));
        Notify(nameof(HasMultipleSelection));
        Notify(nameof(SelectionCount));
        ((AsyncCommand)DeleteCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Rebuilds <see cref="GroupedItems"/> from <see cref="Items"/>, inserting one
    /// <see cref="HistoryDateGroup"/> ahead of every new local day. The repository already
    /// returns newest first, so a single pass is enough.
    /// </summary>
    private void RebuildGroups()
    {
        GroupedItems.Clear();
        var today = DateTime.Now.Date;
        var yesterday = today.AddDays(-1);
        DateTime? currentDay = null;
        foreach (var item in Items)
        {
            var day = item.Date.ToLocalTime().Date;
            if (currentDay != day)
            {
                currentDay = day;
                GroupedItems.Add(day == today
                    ? new HistoryDateGroup("history.section.today", "Today")
                    : day == yesterday
                        ? new HistoryDateGroup("history.section.yesterday", "Yesterday")
                        : new HistoryDateGroup(null, day.ToString("MMMM d, yyyy",
                            System.Globalization.CultureInfo.CurrentCulture)));
            }
            GroupedItems.Add(item);
        }
        Notify(nameof(IsEmpty));
        Notify(nameof(IsFilteredEmpty));
    }

    public void SetRetryModes(IEnumerable<Mode> modes)
    {
        var selectedId = SelectedRetryMode?.Id;
        AvailableRetryModes.Clear();
        foreach (var mode in modes.OrderBy(item => item.SortOrder)) AvailableRetryModes.Add(mode);
        SelectedRetryMode = selectedId is null
            ? null
            : AvailableRetryModes.FirstOrDefault(item => item.Id == selectedId);
        Notify(nameof(SelectedRetryMode));
    }

    public async Task DeleteAsync(Transcript? transcript, CancellationToken cancellationToken = default)
    {
        UpdateSelection(transcript is null ? [] : [transcript]);
        await DeleteSelectedAsync(cancellationToken);
    }

    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedItems.ToArray();
        if (selected.Length == 0) { Status.Failure("history.no_selection", "Select one or more transcripts to delete."); return; }
        try
        {
            StopPlayback();
            var deletion = await _repository.DeleteManyAsync(selected.Select(item => item.Id).ToArray(), DeleteAudio, cancellationToken);
            if (deletion.TranscriptsDeleted == 0)
            { Status.Failure("history.not_found", "The transcript no longer exists."); return; }
            foreach (var transcript in selected) Items.Remove(transcript);
            UpdateSelection([]);
            if (deletion.Warnings.Count == 0)
                Status.Success(DeleteAudio
                    ? $"{deletion.TranscriptsDeleted} transcript(s) and {deletion.AudioFilesDeleted} audio file(s) deleted"
                    : $"{deletion.TranscriptsDeleted} transcript(s) deleted; audio retained");
            else Status.Failure("history.audio_delete_failed", string.Join(" ", deletion.Warnings.Distinct()));
        }
        catch (OperationCanceledException) { Status.Failure("history.cancelled", "Delete cancelled"); }
        catch (Exception) { Status.Failure("history.delete_failed", "Could not delete the transcript."); }
    }

    public Task PlayAsync() => TogglePlaybackAsync();

    public Task TogglePlaybackAsync()
    {
        if (!IsPlaybackAvailable) { Status.Failure("history.audio_unavailable", "This transcript has no playable audio."); return Task.CompletedTask; }
        if (IsPlaying)
        {
            _playback!.Pause();
            IsPlaying = false;
            Status.Success("Playback paused");
        }
        else
        {
            if (PlaybackPositionSeconds >= PlaybackDurationSeconds && PlaybackDurationSeconds > 0)
                SetPlaybackPosition(0);
            _playback!.Play();
            IsPlaying = _playback.IsPlaying;
            Status.Success(IsPlaying ? "Playing recording" : "Playback could not start");
        }
        return Task.CompletedTask;
    }

    public async Task CopyAsync(CancellationToken cancellationToken = default)
    {
        if (_textInjection is null || Selected is null)
        { Status.Failure("history.copy_unavailable", "Select a transcript to copy."); return; }
        var copied = await _textInjection.CopyToClipboardAsync(DetailText, cancellationToken);
        if (copied.IsFailure) { Status.Failure(copied.Error!.Code, copied.Error.Message); return; }
        ResetCopyFeedback();
        IsCopiedRecently = true;
        Notify(nameof(CopyLabel));
        _copyFeedback = new CancellationTokenSource();
        _ = ResetCopyFeedbackAfterDelayAsync(_copyFeedback.Token);
        Status.Success("Transcript copied");
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        if ((_retry is null && _retryWithMode is null) || Selected is not { Status: TranscriptStatus.Failed } selected)
        {
            Status.Failure("history.retry_unavailable", "Only a failed transcription with retained audio can be retried.");
            return;
        }

        var selectedId = selected.Id;
        IsRetrying = true;
        try
        {
            var result = _retryWithMode is not null
                ? await _retryWithMode(selected, SelectedRetryMode, cancellationToken)
                : await _retry!(selected, cancellationToken);
            await RefreshAsync(CancellationToken.None);
            var refreshedSelection = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
            UpdateSelection(refreshedSelection is null ? [] : [refreshedSelection]);
            if (result.IsSuccess) Status.Success("Transcription retry completed");
            else if (result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled)
                Status.Failure("history.retry_cancelled", "Transcription retry cancelled");
            else Status.Failure("history.retry_failed", result.Failure?.Message ?? "The transcription retry failed.");
        }
        catch (OperationCanceledException)
        {
            await RefreshAsync(CancellationToken.None);
            var refreshedSelection = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
            UpdateSelection(refreshedSelection is null ? [] : [refreshedSelection]);
            Status.Failure("history.retry_cancelled", "Transcription retry cancelled");
        }
        catch (Exception) { Status.Failure("history.retry_failed", "The transcription retry failed."); }
        finally { IsRetrying = false; }
    }

    private void NotifyDetail()
    {
        Notify(nameof(HasRawTranscript));
        Notify(nameof(DetailText));
        Notify(nameof(DetailLabel));
    }

    private void LoadSelectedPlayback()
    {
        StopPlayback();
        PlaybackLoadFailed = false;
        SetPlaybackPosition(0);
        PlaybackDurationSeconds = 0;
        Notify(nameof(FormattedPlaybackDuration));
        Notify(nameof(FormattedPlayback));
        if (_playback is null || Selected?.AudioFilePath is not { } path) { RefreshPlaybackCommands(); return; }
        var loaded = _playback.Load(path);
        if (loaded.IsFailure)
        {
            PlaybackLoadFailed = true;
            Status.Failure(loaded.Error!.Code, loaded.Error.Message);
        }
        else
        {
            PlaybackDurationSeconds = _playback.TotalDuration.TotalSeconds;
            Notify(nameof(FormattedPlaybackDuration));
        Notify(nameof(FormattedPlayback));
        }
        RefreshPlaybackCommands();
    }

    private void StopPlayback()
    {
        _playback?.Stop();
        IsPlaying = false;
        SetPlaybackPosition(0);
        RefreshPlaybackCommands();
    }

    private void SetPlaybackPosition(double seconds)
    {
        if (Math.Abs(_playbackPositionSeconds - seconds) > 0.0001)
        {
            _playbackPositionSeconds = seconds;
            Notify(nameof(PlaybackPositionSeconds));
            Notify(nameof(FormattedPlaybackPosition));
            Notify(nameof(FormattedPlayback));
        }
    }

    private void OnPlaybackPositionChanged(object? sender, TimeSpan position) => Dispatch(() => SetPlaybackPosition(position.TotalSeconds));
    private void OnPlaybackDurationReady(object? sender, TimeSpan duration) => Dispatch(() =>
    {
        PlaybackDurationSeconds = duration.TotalSeconds;
        Notify(nameof(FormattedPlaybackDuration));
        Notify(nameof(FormattedPlayback));
    });
    private void OnPlaybackEnded(object? sender, EventArgs args) => Dispatch(() =>
    {
        IsPlaying = false;
        SetPlaybackPosition(0);
        Status.Success("Playback finished");
    });
    private void OnPlaybackFailed(object? sender, PlatformError error) => Dispatch(() =>
    {
        IsPlaying = false;
        PlaybackLoadFailed = true;
        SetPlaybackPosition(0);
        Status.Failure(error.Code, error.Message);
        RefreshPlaybackCommands();
    });
    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_synchronizationContext is null || SynchronizationContext.Current == _synchronizationContext) action();
        else _synchronizationContext.Post(_ => { if (!_disposed) action(); }, null);
    }
    private void RefreshPlaybackCommands()
    {
        Notify(nameof(IsPlaybackAvailable));
        Notify(nameof(ShowAudioUnavailableMessage));
        ((AsyncCommand)PlayCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)StopPlaybackCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CopyCommand).RaiseCanExecuteChanged();
    }
    private async Task ResetCopyFeedbackAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
            Dispatch(() => { IsCopiedRecently = false; Notify(nameof(CopyLabel)); });
        }
        catch (OperationCanceledException) { }
    }
    private void ResetCopyFeedback()
    {
        _copyFeedback?.Cancel();
        _copyFeedback?.Dispose();
        _copyFeedback = null;
        IsCopiedRecently = false;
        Notify(nameof(CopyLabel));
    }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    private static DateTime LocalDateStartUtc(DateTimeOffset value, int dayOffset = 0)
    {
        var localDate = new DateTime(value.Year, value.Month, value.Day).AddDays(dayOffset);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate)).UtcDateTime;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetCopyFeedback();
        if (_playback is not null)
        {
            _playback.PlaybackEnded -= OnPlaybackEnded;
            _playback.PositionChanged -= OnPlaybackPositionChanged;
            _playback.DurationReady -= OnPlaybackDurationReady;
            _playback.PlaybackFailed -= OnPlaybackFailed;
            _playback.Stop();
        }
    }
}
