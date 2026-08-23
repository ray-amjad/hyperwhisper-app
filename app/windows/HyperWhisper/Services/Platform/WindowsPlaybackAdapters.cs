using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

public sealed class WindowsAudioPlaybackService : PlatformContracts.IAudioPlaybackService
{
    private readonly AudioPlaybackService _inner = new();
    private bool _disposed;

    public WindowsAudioPlaybackService()
    {
        _inner.PlaybackEnded += OnPlaybackEnded;
        _inner.PositionChanged += OnPositionChanged;
        _inner.DurationReady += OnDurationReady;
        _inner.PlaybackFailed += OnPlaybackFailed;
    }

    public event EventHandler? PlaybackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<TimeSpan>? DurationReady;
    public event EventHandler<PlatformContracts.PlatformError>? PlaybackFailed;
    public bool IsPlaying => _inner.IsPlaying;
    public bool IsLoaded => _inner.IsLoaded;
    public TimeSpan TotalDuration => _inner.TotalDuration;
    public string? LoadedFilePath => _inner.LoadedFilePath;

    public PlatformContracts.PlatformResult Load(string audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath))
            throw new ArgumentException("An audio path is required.", nameof(audioPath));
        if (_disposed)
            return PlatformContracts.PlatformResult.Failure("audio_playback.disposed", "The Windows audio playback service has been disposed.");
        return _inner.Load(audioPath)
            ? PlatformContracts.PlatformResult.Success()
            : PlatformContracts.PlatformResult.Failure("audio_playback.load_failed", "Windows could not load the audio file.");
    }

    public void Play() { if (!_disposed) _inner.Play(); }
    public void Pause() { if (!_disposed) _inner.Pause(); }
    public void Stop() { if (!_disposed) _inner.Stop(); }
    public void Seek(TimeSpan position) { if (!_disposed) _inner.Seek(position); }

    private void OnPlaybackEnded() => Raise(PlaybackEnded, EventArgs.Empty, "playback-ended");
    private void OnPositionChanged(TimeSpan value) => Raise(PositionChanged, value, "position-changed");
    private void OnDurationReady(TimeSpan value) => Raise(DurationReady, value, "duration-ready");
    private void OnPlaybackFailed(Exception exception) => Raise(
        PlaybackFailed,
        new PlatformContracts.PlatformError("audio_playback.failed", "Windows audio playback failed."),
        "playback-failed");

    private void Raise(EventHandler? handlers, EventArgs value, string eventName)
    {
        if (handlers == null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, value); }
            catch (Exception ex) { LoggingService.Error($"WindowsAudioPlaybackService: {eventName} handler failed", ex); }
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T value, string eventName)
    {
        if (handlers == null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); }
            catch (Exception ex) { LoggingService.Error($"WindowsAudioPlaybackService: {eventName} handler failed", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _inner.PlaybackEnded -= OnPlaybackEnded;
            _inner.PositionChanged -= OnPositionChanged;
            _inner.DurationReady -= OnDurationReady;
            _inner.PlaybackFailed -= OnPlaybackFailed;
        }
        finally { _inner.Dispose(); }
        PlaybackEnded = null;
        PositionChanged = null;
        DurationReady = null;
        PlaybackFailed = null;
    }
}

public sealed class WindowsSoundEffectsService : PlatformContracts.ISoundEffectsService
{
    private readonly SoundEffectsService _inner = SoundEffectsService.Instance;
    private bool _disposed;

    public PlatformContracts.PlatformResult Play(PlatformContracts.SoundEffect effect)
    {
        if (_disposed)
            return PlatformContracts.PlatformResult.Failure("sound_effects.disposed", "The Windows sound-effects service has been disposed.");
        switch (effect)
        {
            case PlatformContracts.SoundEffect.RecordingStarted: _inner.PlayStartSound(); break;
            case PlatformContracts.SoundEffect.RecordingStopped: _inner.PlayStopSound(); break;
            default: throw new ArgumentOutOfRangeException(nameof(effect), effect, null);
        }
        return PlatformContracts.PlatformResult.Success();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }
}
