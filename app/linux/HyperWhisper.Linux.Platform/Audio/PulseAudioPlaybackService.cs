using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

public sealed class PulseAudioPlaybackService : IAudioPlaybackService
{
    private readonly object _gate = new();
    private readonly IPulseAudioApi _api;
    private CancellationTokenSource? _playbackCancellation;
    private Task? _playbackTask;
    private WaveFormat? _format;
    private long _dataOffset;
    private long _dataLength;
    private long _positionBytes;
    private bool _disposed;

    public PulseAudioPlaybackService() : this(new PulseAudioApi()) { }
    internal PulseAudioPlaybackService(IPulseAudioApi api) => _api = api ?? throw new ArgumentNullException(nameof(api));

    public event EventHandler? PlaybackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<TimeSpan>? DurationReady;
    public event EventHandler<PlatformError>? PlaybackFailed;

    public bool IsPlaying { get; private set; }
    public bool IsLoaded => LoadedFilePath is not null;
    public TimeSpan TotalDuration => _format is null ? TimeSpan.Zero : TimeSpan.FromSeconds((double)_dataLength / _format.BytesPerSecond);
    public string? LoadedFilePath { get; private set; }
    public PulseAudioCapabilities GetCapabilities() => _api.GetCapabilities();

    public PlatformResult Load(string audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !Path.IsPathFullyQualified(audioPath))
            return PlatformResult.Failure("audio_path_invalid", "A fully qualified audio path is required.");
        if (_disposed) return PlatformResult.Failure("audio_playback_disposed", "The playback service is disposed.");
        Pause();
        LoadedFilePath = null;
        _format = null;
        _dataOffset = 0;
        _dataLength = 0;
        Interlocked.Exchange(ref _positionBytes, 0);
        try
        {
            using var stream = File.OpenRead(audioPath);
            var header = WaveFile.ReadHeader(stream);
            if (header.IsFailure) return PlatformResult.Failure(header.Error!.Code, header.Error.Message);
            if (header.Value.DataOffset + header.Value.DataLength > stream.Length)
                return PlatformResult.Failure("audio_file_truncated", "The WAV data length exceeds the file size.");
            (_format, _dataOffset, _dataLength) = header.Value;
            _positionBytes = 0;
            LoadedFilePath = audioPath;
            Raise(DurationReady, TotalDuration);
            return PlatformResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PlatformResult.Failure("audio_load_failed", "The audio file could not be loaded.");
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_disposed || IsPlaying || LoadedFilePath is null || _format is null) return;
            if (!_api.GetCapabilities().Available)
            {
                Raise(PlaybackFailed, new PlatformError("pulse_unavailable", "PulseAudio compatibility libraries are unavailable."));
                return;
            }
            _playbackCancellation = new CancellationTokenSource();
            IsPlaying = true;
            _playbackTask = Task.Run(() => PlaybackLoop(_playbackCancellation.Token));
        }
    }

    public void Pause()
    {
        Task? task;
        lock (_gate)
        {
            _playbackCancellation?.Cancel();
            task = _playbackTask;
        }
        try { task?.GetAwaiter().GetResult(); } catch { }
    }

    public void Stop()
    {
        Pause();
        Interlocked.Exchange(ref _positionBytes, 0);
        Raise(PositionChanged, TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        if (_format is null) return;
        var wasPlaying = IsPlaying;
        Pause();
        var bytes = (long)(Math.Clamp(position.TotalSeconds, 0, TotalDuration.TotalSeconds) * _format.BytesPerSecond);
        bytes -= bytes % _format.BlockAlign;
        Interlocked.Exchange(ref _positionBytes, bytes);
        Raise(PositionChanged, TimeSpan.FromSeconds((double)bytes / _format.BytesPerSecond));
        if (wasPlaying) Play();
    }

    private void PlaybackLoop(CancellationToken cancellationToken)
    {
        PlatformError? error = null;
        var ended = false;
        try
        {
            var opened = _api.OpenPlayback(_format!);
            if (opened.IsFailure) { error = opened.Error; return; }
            using var session = opened.Value!;
            using var stream = File.OpenRead(LoadedFilePath!);
            stream.Position = _dataOffset + Interlocked.Read(ref _positionBytes);
            var buffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested && Interlocked.Read(ref _positionBytes) < _dataLength)
            {
                var remaining = _dataLength - Interlocked.Read(ref _positionBytes);
                var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) break;
                var write = session.Write(buffer, count);
                if (write.IsFailure) { error = write.Error; break; }
                var current = Interlocked.Add(ref _positionBytes, count);
                Raise(PositionChanged, TimeSpan.FromSeconds((double)current / _format!.BytesPerSecond));
            }
            if (!cancellationToken.IsCancellationRequested && error is null)
            {
                var drain = session.Drain();
                error = drain.Error;
                ended = error is null;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            error = new PlatformError("audio_playback_failed", "Audio playback stopped unexpectedly.");
        }
        finally
        {
            lock (_gate)
            {
                IsPlaying = false;
                _playbackTask = null;
                _playbackCancellation?.Dispose();
                _playbackCancellation = null;
            }
            if (error is not null) Raise(PlaybackFailed, error);
            else if (ended)
            {
                Interlocked.Exchange(ref _positionBytes, 0);
                Raise(PositionChanged, TimeSpan.Zero);
                Raise(PlaybackEnded);
            }
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); } catch { }
        }
    }

    private void Raise(EventHandler? handlers)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
