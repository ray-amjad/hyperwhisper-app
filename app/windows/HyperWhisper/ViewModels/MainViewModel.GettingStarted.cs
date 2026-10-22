using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.ViewModels;

/// <summary>
/// What the Home Getting Started rows read their ticks from (#638). Each flag is
/// a fact about the user's real data, never a record of a click.
/// </summary>
internal readonly record struct GettingStartedSignals(
    bool HasCompletedTranscript,
    bool HasCustomShortcut,
    bool HasUserMode,
    bool HasVocabularyWord);

public partial class MainViewModel
{
    /// <summary>Bumped per refresh, so a slow read cannot overwrite a newer one.</summary>
    private int _gettingStartedRefreshGeneration;

    /// <summary>
    /// Coalesces bursts of History / vocabulary events (a bulk delete raises one
    /// TranscriptDeleted per row) into one refresh.
    /// </summary>
    private System.Timers.Timer? _gettingStartedDebounceTimer;

    /// <summary>True from a Start Recording row click until its start attempt returns.</summary>
    private bool _gettingStartedRecordingStartInFlight;

    private bool _gettingStartedEventsAttached;

    private async Task InitializeGettingStartedAsync()
    {
        GettingStartedItems = new ObservableCollection<GettingStartedItem>
        {
            new() { Id = "recording", Icon = "\U0001F3A4", IconColor = System.Windows.Media.Color.FromRgb(0, 122, 255), Title = Loc.S("home.gettingStarted.recording.title"), Description = Loc.S("home.gettingStarted.recording.description") },
            new() { Id = "shortcuts", Icon = "⌨️", IconColor = System.Windows.Media.Color.FromRgb(175, 82, 222), Title = Loc.S("home.gettingStarted.shortcuts.title"), Description = Loc.S("home.gettingStarted.shortcuts.description") },
            new() { Id = "mode", Icon = "\U0001F3AF", IconColor = System.Windows.Media.Color.FromRgb(52, 199, 89), Title = Loc.S("home.gettingStarted.mode.title"), Description = Loc.S("home.gettingStarted.mode.description") },
            new() { Id = "vocabulary", Icon = "\U0001F4DA", IconColor = System.Windows.Media.Color.FromRgb(255, 149, 0), Title = Loc.S("home.gettingStarted.vocabulary.title"), Description = Loc.S("home.gettingStarted.vocabulary.description") },
        };

        // Seeded through the same method that keeps them current, so the initial
        // badge and the refreshed badge cannot disagree about which row shows what.
        RefreshGettingStartedShortcuts();

        // The old click-written GettingStartedCompletedSteps setting is no longer
        // read (#638): it recorded clicks, not work. The property stays in
        // SettingsService so backup export/import keep their shape.
        await RefreshGettingStartedProgressAsync();

        if (!_gettingStartedEventsAttached)
        {
            HistoryService.Instance.TranscriptAdded += OnGettingStartedTranscriptChanged;
            HistoryService.Instance.TranscriptUpdated += OnGettingStartedTranscriptChanged;
            HistoryService.Instance.TranscriptDeleted += OnGettingStartedTranscriptDeleted;
            VocabularyService.Instance.VocabularyChanged += OnGettingStartedVocabularyChanged;
            _gettingStartedEventsAttached = true;
        }
    }

    private void DetachGettingStartedEvents()
    {
        if (_gettingStartedEventsAttached)
        {
            HistoryService.Instance.TranscriptAdded -= OnGettingStartedTranscriptChanged;
            HistoryService.Instance.TranscriptUpdated -= OnGettingStartedTranscriptChanged;
            HistoryService.Instance.TranscriptDeleted -= OnGettingStartedTranscriptDeleted;
            VocabularyService.Instance.VocabularyChanged -= OnGettingStartedVocabularyChanged;
            _gettingStartedEventsAttached = false;
        }

        _gettingStartedDebounceTimer?.Stop();
        _gettingStartedDebounceTimer?.Dispose();
        _gettingStartedDebounceTimer = null;
    }

    private void OnGettingStartedTranscriptChanged(object? sender, Transcript e) => ScheduleGettingStartedRefresh();
    private void OnGettingStartedTranscriptDeleted(object? sender, Guid e) => ScheduleGettingStartedRefresh();
    private void OnGettingStartedVocabularyChanged(object? sender, EventArgs e) => ScheduleGettingStartedRefresh();

    /// <summary>
    /// Debounced (~250 ms) refresh for events that can arrive in bursts and from
    /// any thread. The refresh itself always runs on the UI thread.
    /// </summary>
    private void ScheduleGettingStartedRefresh()
    {
        _gettingStartedDebounceTimer?.Stop();
        _gettingStartedDebounceTimer?.Dispose();

        var timer = new System.Timers.Timer(250) { AutoReset = false };
        timer.Elapsed += (_, _) => RefreshGettingStartedProgress();
        _gettingStartedDebounceTimer = timer;
        timer.Start();
    }

    /// <summary>
    /// Recomputes every row's tick from real app state. Safe from any thread:
    /// it hops onto the UI dispatcher, and does nothing before Home is built (or
    /// after cleanup): until then there are no rows to tick.
    /// </summary>
    private void RefreshGettingStartedProgress()
    {
        if (!_gettingStartedEventsAttached) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshGettingStartedProgress);
            return;
        }

        _ = RefreshGettingStartedProgressAsync();
    }

    /// <summary>Call on the UI thread. Never throws.</summary>
    private async Task RefreshGettingStartedProgressAsync()
    {
        if (GettingStartedItems == null || GettingStartedItems.Count == 0) return;

        var generation = ++_gettingStartedRefreshGeneration;

        try
        {
            // Read on the UI thread: settings and the Modes list live here.
            var ptt = _settingsService.PushToTalk;
            var hasCustomShortcut = HasCustomShortcut(
                _settingsService.ToggleShortcut,
                _settingsService.CancelShortcut,
                _settingsService.ChangeModeShortcut,
                _settingsService.StreamingShortcut,
                ptt.Mode);
            var hasUserMode = HasUserMode(Modes);

            // The two store reads go off the UI thread, like the Home stats (#1486).
            var (hasTranscript, hasVocabulary) = await Task.Run(ReadGettingStartedStoreSignals);

            if (generation != _gettingStartedRefreshGeneration) return;

            ShowGettingStarted = ApplyGettingStartedProgress(
                GettingStartedItems,
                new GettingStartedSignals(hasTranscript, hasCustomShortcut, hasUserMode, hasVocabulary));
        }
        catch (Exception ex)
        {
            // Leave the ticks as they were; a failed read must not hide or tick a row.
            LoggingService.Warn($"MainViewModel: Getting Started refresh failed (exception_type={ex.GetType().FullName})");
        }
    }

    /// <summary>
    /// (at least one completed transcript, at least one vocabulary word), read from
    /// the database plus the History rows still waiting for it (#1589).
    /// </summary>
    private static (bool HasCompletedTranscript, bool HasVocabularyWord) ReadGettingStartedStoreSignals()
    {
        var pendingCompleted = HistoryService.Instance.GetPendingTranscriptsSnapshot()
            .Any(t => t.Status == TranscriptStatus.Completed);

        using var context = new HyperWhisperDbContext();
        var hasTranscript = pendingCompleted
            || context.Transcripts.AsNoTracking().Any(t => t.Status == TranscriptStatus.Completed);
        var hasVocabulary = context.VocabularyItems.AsNoTracking().Any(v => v.Word != "");
        return (hasTranscript, hasVocabulary);
    }

    /// <summary>
    /// Ticks each row from <paramref name="signals"/> and returns whether the
    /// section should still show: true until all four steps are really done.
    /// </summary>
    /// <remarks>
    /// static and internal so the smoke suite can assert the mapping without a
    /// MainViewModel and the services behind it (as ApplyShortcutsToGettingStarted).
    /// </remarks>
    internal static bool ApplyGettingStartedProgress(
        IEnumerable<GettingStartedItem> items, GettingStartedSignals signals)
    {
        var all = items.ToList();
        foreach (var item in all)
        {
            item.IsCompleted = IsGettingStartedStepDone(item.Id, signals);
        }

        return !all.All(i => i.IsCompleted);
    }

    /// <summary>The real-state signal behind one Getting Started row (#638).</summary>
    internal static bool IsGettingStartedStepDone(string stepId, GettingStartedSignals signals) => stepId switch
    {
        "recording" => signals.HasCompletedTranscript,
        "shortcuts" => signals.HasCustomShortcut,
        "mode" => signals.HasUserMode,
        "vocabulary" => signals.HasVocabularyWord,
        _ => false,
    };

    /// <summary>
    /// True when any app shortcut differs from its default, or Push to Talk is on.
    /// Ray's reading for macOS #652 / PR #2171, 2026-10-20, applied here: a cleared
    /// shortcut and a set Quick Capture count too. Windows has no Quick Capture, and
    /// SettingsService reads an empty stored shortcut back as its default, so here
    /// only a changed chord or Push to Talk can tick the row.
    /// </summary>
    internal static bool HasCustomShortcut(
        KeyboardShortcut toggle,
        KeyboardShortcut cancel,
        KeyboardShortcut changeMode,
        KeyboardShortcut streaming,
        PushToTalkMode pushToTalk) =>
        pushToTalk != PushToTalkMode.Disabled
        || toggle != SettingsService.DefaultActionShortcut("Toggle")
        || cancel != SettingsService.DefaultActionShortcut("Cancel")
        || changeMode != SettingsService.DefaultActionShortcut("ChangeMode")
        || streaming != SettingsService.DefaultActionShortcut("Streaming");

    /// <summary>
    /// True when at least one Mode is one the user made: not system-provided and
    /// not the seeded Hyper mode's well-known id (a backup restore can bring that
    /// row back without its system flag).
    /// </summary>
    internal static bool HasUserMode(IEnumerable<Mode>? modes) =>
        modes != null && modes.Any(m => !m.IsSystemProvided && m.Id != ModeDefaults.DefaultModeId);

    private void RefreshGettingStartedShortcuts() =>
        ApplyShortcutsToGettingStarted(
            GettingStartedItems,
            HotkeyText,
            _settingsService.ChangeModeShortcut.ToDisplayString());

    /// <summary>
    /// Puts the current hotkeys onto the Getting Started rows that teach them.
    /// </summary>
    /// <remarks>
    /// Two rows carry a badge, and both were stale after a shortcut change: the rows
    /// are built once, by InitializeGettingStartedAsync, whose only caller is
    /// OnNavigatedToAsync behind an "if (IsInitialized) return;". So Home kept telling
    /// a new user to press a chord that no longer did anything while the status bar
    /// beside it was already right, and only a restart fixed it.
    ///
    /// static and internal so the smoke suite can assert the mapping without standing
    /// up a MainViewModel and the dozen services behind it.
    /// </remarks>
    internal static void ApplyShortcutsToGettingStarted(
        IEnumerable<GettingStartedItem> items, string toggleText, string changeModeText)
    {
        foreach (var item in items)
        {
            switch (item.Id)
            {
                case "recording":
                    item.ShortcutText = toggleText;
                    break;
                case "mode":
                    item.ShortcutText = changeModeText;
                    break;
            }
        }
    }

    /// <summary>
    /// A Getting Started row click (#638). It only ACTS: it never ticks or un-ticks
    /// a row, and it acts on every click, completed or not. Ticks come from
    /// <see cref="RefreshGettingStartedProgress"/> alone.
    /// </summary>
    /// <remarks>
    /// Concurrent executions are allowed so a Start Recording attempt that is still
    /// awaiting does not disable the other rows' navigation.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenGettingStartedStep(string stepId)
    {
        switch (stepId)
        {
            case "recording":
                await StartRecordingFromGettingStartedAsync();
                break;
            // The row promises the Shortcuts section, not Settings' first page (#637).
            case "shortcuts": OpenSettingsSection("Shortcuts"); break;
            case "mode": CurrentPage = NavigationPage.Modes; break;
            case "vocabulary": CurrentPage = NavigationPage.Vocabulary; break;
        }
    }

    /// <summary>
    /// Starts a recording through <see cref="StartRecordingAsync"/>, the entry point
    /// the toggle hotkey and the tray use. Does nothing while a dictation is already
    /// recording, stopping, transcribing, loading or streaming, or while a previous
    /// row click's start is still running: StartRecordingAsync itself does not guard
    /// against a second start.
    /// </summary>
    private async Task StartRecordingFromGettingStartedAsync()
    {
        if (_gettingStartedRecordingStartInFlight
            || IsRecording || IsTranscribing || IsModelLoading
            || _isStoppingRecording || _hotkeyBlocked || IsStreamingActive())
        {
            LoggingService.Info("MainViewModel: Getting Started Start Recording ignored - a dictation is already in flight");
            return;
        }

        _gettingStartedRecordingStartInFlight = true;
        try
        {
            await StartRecordingAsync();
        }
        catch (Exception ex) when (OptionalAssemblyGuard.IsLoadFailure(ex))
        {
            // Same backstop as OnShortcutPressed: never ex.Message (installed path).
            OptionalAssemblyGuard.MarkUnavailable(
                OptionalAssemblyGuard.AppClassificationAssembly, ex, "getting_started_recording");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.recordingStartFailed"),
                showSettingsButton: false));
        }
        finally
        {
            _gettingStartedRecordingStartInFlight = false;
        }
    }

    partial void OnCurrentPageChanged(NavigationPage value)
    {
        // Back on Home: the user may have made a Mode, a word or a shortcut meanwhile.
        if (value == NavigationPage.Home)
        {
            RefreshGettingStartedProgress();
        }
    }
}
