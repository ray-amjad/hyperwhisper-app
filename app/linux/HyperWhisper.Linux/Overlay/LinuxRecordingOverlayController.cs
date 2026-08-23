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
            _streamingConnection = null;
            _audioLevel = 0;
        }
        Apply(RecordingSnapshotLocked());
    }

    public void ShowStreaming(LinuxOverlayModeLabel mode)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingMode = mode;
            _recordingStarted = _clock();
            _streamingConnection = LinuxStreamingOverlayConnectionState.Connecting;
            _audioLevel = 0;
        }
        Apply(RecordingSnapshotLocked());
    }

    public void UpdateStreamingConnection(LinuxStreamingOverlayConnectionState state)
    {
        LinuxRecordingOverlaySnapshot? snapshot = null;
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null || _streamingConnection is null) return;
            _streamingConnection = state;
            snapshot = RecordingSnapshotLocked();
        }
        Apply(snapshot);
    }

    public void UpdateAudioLevel(float level)
    {
        LinuxRecordingOverlaySnapshot? snapshot = null;
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null) return;
            _audioLevel = Math.Clamp(double.IsFinite(level) ? level * 3.25 : 0, 0, 1);
            snapshot = RecordingSnapshotLocked();
        }
        Apply(snapshot);
    }

    public void ShowTranscribing()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
        }
        Apply(new(LinuxRecordingOverlayState.Transcribing, true, _text("recording.state.transcribing"), string.Empty,
            ViewModel.DurationText));
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
        }
        Apply(new(LinuxRecordingOverlayState.Error, true, message, string.Empty, ViewModel.DurationText));
        StartTransient(ErrorDuration, Hide);
    }

    public void ShowModeChanged(LinuxOverlayModeLabel mode)
    {
        LinuxRecordingOverlaySnapshot resume;
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingMode = mode;
            resume = _recordingStarted is not null
                ? RecordingSnapshotLocked()
                : LinuxRecordingOverlayViewModel.HiddenSnapshot;
        }
        Apply(new(LinuxRecordingOverlayState.ModeChanged, true, _text("linux.overlay.mode_changed"), mode.Value,
            resume.DurationText));
        StartTransient(ModeToastDuration, () => Apply(resume));
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
        }
        Apply(new(LinuxRecordingOverlayState.Cancelled, true, _text("status.recordingCancelled"), string.Empty,
            ViewModel.DurationText));
        StartTransient(CancelledDuration, Hide);
    }

    public void ShowCancelConfirmation()
    {
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null || _streamingConnection is not null) return;
            CancelTransientLocked();
        }
        Apply(new(LinuxRecordingOverlayState.CancelConfirmation, true,
            _text("recording.cancel.prompt"), string.Empty, ViewModel.DurationText));
    }

    public void DismissCancelConfirmation()
    {
        LinuxRecordingOverlaySnapshot? snapshot = null;
        lock (_gate)
        {
            if (_disposed || _recordingStarted is null || ViewModel.State != LinuxRecordingOverlayState.CancelConfirmation) return;
            snapshot = RecordingSnapshotLocked();
        }
        Apply(snapshot);
    }

    public void ShowCompletion(LinuxRecordingOverlayCompletion completion)
    {
        var state = completion switch
        {
            LinuxRecordingOverlayCompletion.Pasted => LinuxRecordingOverlayState.Pasted,
            LinuxRecordingOverlayCompletion.Copied => LinuxRecordingOverlayState.Copied,
            _ => LinuxRecordingOverlayState.SecureField,
        };
        var text = completion switch
        {
            LinuxRecordingOverlayCompletion.Pasted => _text("recording.success.pasted"),
            LinuxRecordingOverlayCompletion.Copied => _text("recording.copy.copied"),
            _ => _text("linux.overlay.secure_field"),
        };
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
            _audioLevel = 0;
        }
        Apply(new(state, true, text, string.Empty, ViewModel.DurationText));
        StartTransient(CompletionDuration, Hide);
    }

    public void Hide()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            _recordingStarted = null;
        }
        Apply(LinuxRecordingOverlayViewModel.HiddenSnapshot);
    }

    internal void TickDuration()
    {
        LinuxRecordingOverlaySnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_disposed && _recordingStarted is not null
                && ViewModel.State is LinuxRecordingOverlayState.Recording or LinuxRecordingOverlayState.Streaming)
                snapshot = RecordingSnapshotLocked();
        }
        if (snapshot is not null) Apply(snapshot);
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

    private void StartTransient(TimeSpan duration, Action completion)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            CancelTransientLocked();
            cancellation = _transient = new CancellationTokenSource();
        }
        _ = CompleteTransientAsync(duration, completion, cancellation);
    }

    private async Task CompleteTransientAsync(TimeSpan duration, Action completion,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _delay.WaitAsync(duration, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || _transient != cancellation) return;
                _transient = null;
            }
            completion();
        }
        catch (OperationCanceledException) { }
        catch { /* Overlay timing cannot fail transcription. */ }
        finally { cancellation.Dispose(); }
    }

    private void Apply(LinuxRecordingOverlaySnapshot snapshot)
    {
        try
        {
            _dispatcher.Post(() =>
            {
                try
                {
                    if (_disposed) return;
                    ViewModel.Apply(snapshot);
                    if (snapshot.IsVisible) _surface.ShowBestEffort();
                    else _surface.HideBestEffort();
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
        }
        _durationTimer?.Dispose();
        try { _dispatcher.Post(() => { try { _surface.HideBestEffort(); _surface.Dispose(); } catch { } }); }
        catch { try { _surface.Dispose(); } catch { } }
    }
}
