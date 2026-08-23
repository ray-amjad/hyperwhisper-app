namespace HyperWhisper.Linux.Overlay;

internal interface ILinuxRecordingOverlayFeedback : IDisposable
{
    void RecordingStarted(LinuxOverlayModeLabel mode);
    void Transcribing();
    void Completed();
    void Cancelled();
    void Failed(LinuxRecordingOverlayError error);
    void ModeChanged(LinuxOverlayModeLabel mode);
}

/// <summary>Creates and owns the Avalonia surface lazily on the UI dispatcher.</summary>
internal sealed class LazyLinuxRecordingOverlayFeedback : ILinuxRecordingOverlayFeedback
{
    private readonly ILinuxOverlayDispatcher _dispatcher;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string, string> _text;
    private LinuxRecordingOverlayController? _controller;
    private bool _disposed;

    public LazyLinuxRecordingOverlayFeedback(
        ILinuxOverlayDispatcher dispatcher,
        Func<bool>? isEnabled,
        Func<string, string> text)
    {
        _dispatcher = dispatcher;
        _isEnabled = isEnabled ?? (() => true);
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public void RecordingStarted(LinuxOverlayModeLabel mode) => Post(controller => controller.ShowRecording(mode));
    public void Transcribing() => Post(controller => controller.ShowTranscribing());
    public void Completed() => Post(controller => controller.Hide());
    public void Cancelled() => Post(controller => controller.Cancel());
    public void Failed(LinuxRecordingOverlayError error) => Post(controller => controller.ShowError(error));
    public void ModeChanged(LinuxOverlayModeLabel mode) => Post(controller => controller.ShowModeChanged(mode));
    internal LinuxRecordingOverlaySnapshot? Snapshot => _controller?.ViewModel.Snapshot;

    private void Post(Action<LinuxRecordingOverlayController> action)
    {
        if (!_isEnabled()) return;
        try
        {
            _dispatcher.Post(() =>
            {
                try
                {
                    if (_disposed) return;
                    _controller ??= LinuxRecordingOverlayFactory.Create(_text);
                    action(_controller);
                }
                catch { /* Overlay creation/rendering cannot block speech. */ }
            });
        }
        catch { }
    }

    public void ApplyPreference()
    {
        if (_isEnabled()) return;
        try { _dispatcher.Post(() => { try { _controller?.Hide(); } catch { } }); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _dispatcher.Post(() => { try { _controller?.Dispose(); } catch { } _controller = null; }); }
        catch { try { _controller?.Dispose(); } catch { } _controller = null; }
    }
}

internal static class LinuxRecordingOverlayErrorMapper
{
    public static LinuxRecordingOverlayError FromCode(string? code, bool transcription)
    {
        var normalized = code?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("permission", StringComparison.Ordinal)
            || normalized.Contains("denied", StringComparison.Ordinal))
            return LinuxRecordingOverlayError.PermissionDenied;
        if (normalized.Contains("microphone", StringComparison.Ordinal)
            || normalized.Contains("audio_device", StringComparison.Ordinal)
            || normalized.Contains("capture", StringComparison.Ordinal))
            return LinuxRecordingOverlayError.MicrophoneUnavailable;
        if (normalized.Contains("provider", StringComparison.Ordinal)
            || normalized.Contains("backend_unavailable", StringComparison.Ordinal))
            return LinuxRecordingOverlayError.ProviderUnavailable;
        if (normalized.Contains("no_speech", StringComparison.Ordinal))
            return LinuxRecordingOverlayError.NoSpeechDetected;
        return transcription
            ? LinuxRecordingOverlayError.TranscriptionFailed
            : LinuxRecordingOverlayError.RecordingFailed;
    }
}
