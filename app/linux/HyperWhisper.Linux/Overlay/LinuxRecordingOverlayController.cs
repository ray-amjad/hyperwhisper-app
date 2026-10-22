namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// Content-free overlay state controller. Its public event methods accept only
/// named states, bounded mode labels, and normalized error categories.
/// Rendering is best-effort and never allowed to fail the speech workflow.
/// </summary>
public sealed class LinuxRecordingOverlayController : IDisposable
{
    private static readonly TimeSpan ModeToastDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CancelledDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CompletionDuration = TimeSpan.FromMilliseconds(900);
    private readonly object _gate = new();
    private readonly ILinuxOverlayDispatcher _dispatcher;
    private readonly ILinuxRecordingOverlaySurface _surface;
    private readonly ILinuxOverlayDelay _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, string> _text;
    private readonly Timer? _durationTimer;
    private CancellationTokenSource? _transient;
    private DateTimeOffset? _recordingStarted;
    private LinuxOverlayModeLabel _recordingMode = LinuxOverlayModeLabel.Create(null);
    private LinuxStreamingOverlayConnectionState? _streamingConnection;
    private double _audioLevel;
    // What an active recording is showing, owned here under _gate (#1245). Ticks repaint only Live.
    private RecordingFace _face;
    private long _generation;
    private bool _disposed;

    internal LinuxRecordingOverlayController(
        LinuxRecordingOverlayViewModel viewModel,
        ILinuxOverlayDispatcher dispatcher,
        ILinuxRecordingOverlaySurface surface,
        Func<string, string> text)
        : this(viewModel, dispatcher, surface, new SystemLinuxOverlayDelay(),
            () => DateTimeOffset.UtcNow, startDurationTimer: true, text) { }

    internal LinuxRecordingOverlayController(
        LinuxRecordingOverlayViewModel viewModel,
        ILinuxOverlayDispatcher dispatcher,
        ILinuxRecordingOverlaySurface surface,
        ILinuxOverlayDelay delay,
        Func<DateTimeOffset> clock,
        bool startDurationTimer,
        Func<string, string> text)
    {
        ViewModel = viewModel;
        _dispatcher = dispatcher;
        _surface = surface;
        _delay = delay;
        _clock = clock;
        _text = text ?? throw new ArgumentNullException(nameof(text));
        if (startDurationTimer)
            _durationTimer = new Timer(_ => TickDuration(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public LinuxRecordingOverlayViewModel ViewModel { get; }

    public void ShowRecording(LinuxOverlayModeLabel mode)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingMode = mode;
            _recordingStarted = _clock();
            _face = RecordingFace.Live;
            _streamingConnection = null;
            _audioLevel = 0;
            ApplyLocked(RecordingSnapshotLocked());
        }
    }

    public void ShowStreaming(LinuxOverlayModeLabel mode)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingMode = mode;
            _recordingStarted = _clock();
            _face = RecordingFace.Live;
            _streamingConnection = LinuxStreamingOverlayConnectionState.Connecting;
            _audioLevel = 0;
            ApplyLocked(RecordingSnapshotLocked());
        }
    }

    public void UpdateStreamingConnection(LinuxStreamingOverlayConnectionState state)
    {
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null || _streamingConnection is null) return;
            _streamingConnection = state;
            // A mode toast shows the new connection state when it resumes.
            if (_face == RecordingFace.Live) ApplyLocked(RecordingSnapshotLocked());
        }
    }

    public void UpdateAudioLevel(float level)
    {
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null) return;
            _audioLevel = Math.Clamp(double.IsFinite(level) ? level * 3.25 : 0, 0, 1);
            // A level tick repaints only the live pill, never a cancel confirmation or a mode toast (#1245).
            if (_face == RecordingFace.Live) ApplyLocked(RecordingSnapshotLocked());
        }
    }

    public void ShowTranscribing()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
            _face = RecordingFace.None;
            ApplyLocked(new(LinuxRecordingOverlayState.Transcribing, true, _text("recording.state.transcribing"),
                string.Empty, ViewModel.DurationText));
        }
    }

    public void ShowError(LinuxRecordingOverlayError error)
    {
        var message = error switch
        {
            LinuxRecordingOverlayError.MicrophoneUnavailable => _text("linux.overlay.error.microphone"),
            LinuxRecordingOverlayError.RecordingFailed => _text("linux.overlay.error.recording"),
            LinuxRecordingOverlayError.TranscriptionFailed => _text("linux.overlay.error.transcription"),
            LinuxRecordingOverlayError.NoSpeechDetected => _text("linux.overlay.error.no_speech"),
            LinuxRecordingOverlayError.ProviderUnavailable => _text("linux.overlay.error.provider"),
            LinuxRecordingOverlayError.PermissionDenied => _text("linux.overlay.error.permission"),
            _ => _text("linux.overlay.error.unknown"),
        };
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
            _face = RecordingFace.None;
            ApplyLocked(new(LinuxRecordingOverlayState.Error, true, message, string.Empty, ViewModel.DurationText));
            StartTransientLocked(ErrorDuration, HideLocked);
        }
    }

    public void ShowModeChanged(LinuxOverlayModeLabel mode)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _recordingMode = mode;
            // A pending cancel confirmation stays up: the toast would hide its Yes/No buttons while the
            // session still holds the prompt open. The new mode shows when the recording resumes.
            if (_face == RecordingFace.CancelConfirmation) return;
            CancelTransientLocked();
            if (_recordingStarted is not null) _face = RecordingFace.ModeToast;
            ApplyLocked(new(LinuxRecordingOverlayState.ModeChanged, true, _text("linux.overlay.mode_changed"),
                mode.Value, _recordingStarted is null ? LinuxRecordingOverlayViewModel.HiddenSnapshot.DurationText
                    : RecordingSnapshotLocked().DurationText));
            StartTransientLocked(ModeToastDuration, ResumeAfterModeToastLocked);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
            _face = RecordingFace.None;
            ApplyLocked(new(LinuxRecordingOverlayState.Cancelled, true, _text("status.recordingCancelled"),
                string.Empty, ViewModel.DurationText));
            StartTransientLocked(CancelledDuration, HideLocked);
        }
    }

    public void ShowCancelConfirmation()
    {
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null || _streamingConnection is not null) return;
            CancelTransientLocked();
            _face = RecordingFace.CancelConfirmation;
            ApplyLocked(new(LinuxRecordingOverlayState.CancelConfirmation, true, _text("recording.cancel.prompt"),
                string.Empty, RecordingSnapshotLocked().DurationText));
        }
    }

    public void DismissCancelConfirmation()
    {
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null || _face != RecordingFace.CancelConfirmation) return;
            CancelTransientLocked();
            _face = RecordingFace.Live;
            ApplyLocked(RecordingSnapshotLocked());
        }
    }

    public void ShowCompletion(LinuxRecordingOverlayCompletion completion)
    {
        var state = completion switch
        {
            LinuxRecordingOverlayCompletion.Pasted => LinuxRecordingOverlayState.Pasted,
            LinuxRecordingOverlayCompletion.Copied => LinuxRecordingOverlayState.Copied,
            LinuxRecordingOverlayCompletion.CopyFailed => LinuxRecordingOverlayState.Error,
            _ => LinuxRecordingOverlayState.SecureField,
        };
        var text = completion switch
        {
            LinuxRecordingOverlayCompletion.Pasted => _text("recording.success.pasted"),
            LinuxRecordingOverlayCompletion.Copied => _text("recording.copy.copied"),
            LinuxRecordingOverlayCompletion.CopyFailed => _text("linux.overlay.error.copy"),
            _ => _text("linux.overlay.secure_field"),
        };
        // A failed delivery stays up as long as any other error, so it is not missed (#1703).
        var duration = completion == LinuxRecordingOverlayCompletion.CopyFailed ? ErrorDuration : CompletionDuration;
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
            _face = RecordingFace.None;
            _audioLevel = 0;
            ApplyLocked(new(state, true, text, string.Empty, ViewModel.DurationText));
            StartTransientLocked(duration, HideLocked);
        }
    }

    public void Hide()
    {
        lock (_gate)
        {
            if (_disposed) return;
            HideLocked();
        }
    }

    internal void TickDuration()
    {
        lock (_gate)
        {
            // Controller state, not ViewModel.State: the view model lags behind posts still queued for the UI thread.
            if (!_disposed && _recordingStarted is not null && _face == RecordingFace.Live)
                ApplyLocked(RecordingSnapshotLocked());
        }
    }

    private void HideLocked()
    {
        CancelTransientLocked();
        _recordingStarted = null;
        _face = RecordingFace.None;
        ApplyLocked(LinuxRecordingOverlayViewModel.HiddenSnapshot);
    }

    /// <summary>Resumes from the CURRENT state when the toast expires, never from a snapshot cached at toast time.</summary>
    private void ResumeAfterModeToastLocked()
    {
        if (_recordingStarted is null)
        {
            HideLocked();
            return;
        }
        if (_face != RecordingFace.ModeToast) return;
        _face = RecordingFace.Live;
        ApplyLocked(RecordingSnapshotLocked());
    }

    private LinuxRecordingOverlaySnapshot RecordingSnapshotLocked()
    {
        var elapsed = _recordingStarted is null ? TimeSpan.Zero : _clock() - _recordingStarted.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var totalHours = Math.Min(99, (int)elapsed.TotalHours);
        var duration = totalHours > 0
            ? $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        var streaming = _streamingConnection is not null;
        var status = _streamingConnection switch
        {
            LinuxStreamingOverlayConnectionState.Connecting => _text("linux.overlay.streaming.connecting"),
            LinuxStreamingOverlayConnectionState.Reconnecting => _text("linux.overlay.streaming.reconnecting"),
            LinuxStreamingOverlayConnectionState.Error => _text("linux.overlay.streaming.error"),
            LinuxStreamingOverlayConnectionState.Connected => _text("recording.state.streaming"),
            _ => _text("linux.overlay.recording"),
        };
        return new(streaming ? LinuxRecordingOverlayState.Streaming : LinuxRecordingOverlayState.Recording,
            true, status, _recordingMode.Value, duration, _audioLevel, _streamingConnection);
    }

    private void StartTransientLocked(TimeSpan duration, Action completionLocked)
    {
        CancelTransientLocked();
        var cancellation = _transient = new CancellationTokenSource();
        _ = CompleteTransientAsync(duration, completionLocked, cancellation);
    }

    private async Task CompleteTransientAsync(TimeSpan duration, Action completionLocked,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _delay.WaitAsync(duration, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || _transient != cancellation) return;
                _transient = null;
                // Under the gate, so no state change can land between this check and the repaint.
                completionLocked();
            }
        }
        catch (OperationCanceledException) { }
        catch { /* Overlay timing cannot fail transcription. */ }
        finally { cancellation.Dispose(); }
    }

    /// <summary>
    /// Call with <c>_gate</c> held. Each paint takes a generation number; a post that reaches the UI thread after a
    /// newer one was issued is dropped, so a threadpool tick queued before a UI-thread paint cannot land on top of it.
    /// </summary>
    private void ApplyLocked(LinuxRecordingOverlaySnapshot snapshot)
    {
        var generation = ++_generation;
        try
        {
            _dispatcher.Post(() =>
            {
                try
                {
                    lock (_gate)
                    {
                        if (_disposed || generation != _generation) return;
                        ViewModel.Apply(snapshot);
                        if (snapshot.IsVisible) _surface.ShowBestEffort();
                        else _surface.HideBestEffort();
                    }
                }
                catch { /* Rendering is best-effort. */ }
            });
        }
        catch { /* Dispatch is best-effort. */ }
    }

    private void CancelTransientLocked()
    {
        var transient = _transient;
        _transient = null;
        try { transient?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelTransientLocked();
            _recordingStarted = null;
            _face = RecordingFace.None;
        }
        _durationTimer?.Dispose();
        try { _dispatcher.Post(() => { try { _surface.HideBestEffort(); _surface.Dispose(); } catch { } }); }
        catch { try { _surface.Dispose(); } catch { } }
    }

    private enum RecordingFace { None, Live, CancelConfirmation, ModeToast }
}
