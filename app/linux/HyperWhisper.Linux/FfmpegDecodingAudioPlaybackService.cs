using System.Diagnostics;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux;

/// <summary>Decodes non-WAV history audio to a private temporary WAV before Pulse playback.</summary>
/// <remarks>
/// History calls <see cref="Load"/> on the UI thread every time an item is selected, and the
/// decode used to run there: ffmpeg was started and waited for in place, so a 5-minute m4a froze
/// the window for ~190 ms per selection (#1004). The decode now runs on the thread pool. While it
/// runs the service reports itself loaded, holds a Play, Pause or Seek until the decoded file is
/// in the player, and reports a decode that fails through <see cref="PlaybackFailed"/>. A second
/// Load, a Stop of the selection, or Dispose kills the ffmpeg it no longer needs.
/// </remarks>
internal sealed class FfmpegDecodingAudioPlaybackService : IAudioPlaybackService
{
    private readonly IAudioPlaybackService _inner;
    private readonly string _temporaryDirectory;
    private readonly string _ffmpeg;
    private readonly object _gate = new();
    private string? _decodedPath;
    private PendingDecode? _pending;
    private bool _disposed;

    public FfmpegDecodingAudioPlaybackService(
        IAudioPlaybackService inner,
        IAppPaths paths,
        string ffmpegExecutable = "ffmpeg")
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _temporaryDirectory = (paths ?? throw new ArgumentNullException(nameof(paths))).TemporaryDirectory;
        _ffmpeg = ffmpegExecutable;
        _inner.PlaybackEnded += ForwardEnded;
        _inner.PositionChanged += ForwardPosition;
        _inner.DurationReady += ForwardDuration;
        _inner.PlaybackFailed += ForwardFailure;
    }

    public event EventHandler? PlaybackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<TimeSpan>? DurationReady;
    public event EventHandler<PlatformError>? PlaybackFailed;

    // While a decode runs the inner player may still hold the PREVIOUS selection, so none of these
    // read through to it until the decoded file is loaded.
    public bool IsPlaying { get { lock (_gate) return _pending?.PlayRequested ?? (LoadedFilePath is not null && _inner.IsPlaying); } }
    public bool IsLoaded { get { lock (_gate) return _pending is not null || (LoadedFilePath is not null && _inner.IsLoaded); } }
    public TimeSpan TotalDuration { get { lock (_gate) return _pending is null && LoadedFilePath is not null ? _inner.TotalDuration : TimeSpan.Zero; } }
    public string? LoadedFilePath { get; private set; }

    /// <summary>Completes when the decode the last <see cref="Load"/> started has been handled. For tests.</summary>
    internal Task PendingLoad { get { lock (_gate) return _pending?.Completion ?? Task.CompletedTask; } }

    public PlatformResult Load(string audioPath)
    {
        lock (_gate)
        {
            if (_disposed) return PlatformResult.Failure("audio_playback_disposed", "The playback service is disposed.");
            CancelPending();
            _inner.Stop();
            CleanupDecoded();
            LoadedFilePath = null;
            if (!string.Equals(Path.GetExtension(audioPath), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsReadablePath(audioPath))
                    return PlatformResult.Failure("audio_path_invalid", "A readable, fully qualified audio path is required.");
                StartDecode(audioPath, fallbackFailure: null);
                LoadedFilePath = audioPath;
                return PlatformResult.Success();
            }
            var loaded = _inner.Load(audioPath);
            if (loaded.IsSuccess)
            {
                LoadedFilePath = audioPath;
                return loaded;
            }
            // A ".wav" extension does not make a file the canonical 44-byte PCM layout the inner reader
            // accepts. An imported WAV routinely carries a LIST chunk, or a wider `fmt ` chunk, before
            // its `data` chunk, so the reader rejected it and History showed "Audio file could not be
            // loaded" for a file that plays anywhere else. The decode below rewrites it to the
            // canonical layout, which is what a non-WAV import already goes through, so the extension
            // no longer decides whether the file gets a chance. When that decode fails too, the
            // reader's own error is the one reported, as before.
            if (!IsReadablePath(audioPath)) return loaded;
            StartDecode(audioPath, fallbackFailure: loaded.Error);
            LoadedFilePath = audioPath;
            return PlatformResult.Success();
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_pending is not null) _pending.PlayRequested = true;
            else if (LoadedFilePath is not null) _inner.Play();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_pending is not null) _pending.PlayRequested = false;
            else if (LoadedFilePath is not null) _inner.Pause();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_pending is not null) { _pending.PlayRequested = false; _pending.Seek = null; }
            else _inner.Stop();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_pending is not null) _pending.Seek = position;
            else if (LoadedFilePath is not null) _inner.Seek(position);
        }
    }

    private static bool IsReadablePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);

    // Called under _gate.
    private void StartDecode(string input, PlatformError? fallbackFailure)
    {
        var pending = new PendingDecode();
        _pending = pending;
        pending.Completion = Task.Run(() => DecodeAndLoadAsync(pending, input, fallbackFailure));
    }

    private async Task DecodeAndLoadAsync(PendingDecode pending, string input, PlatformError? fallbackFailure)
    {
        var decoded = await DecodeAsync(input, pending.Cancellation.Token).ConfigureAwait(false);
        PlatformError? failure = null;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_pending, pending))
            {
                // A newer Load, or Dispose, superseded this one: its output belongs to nobody.
                if (decoded.IsSuccess) TryDelete(decoded.Value!);
                return;
            }
            _pending = null;
            if (decoded.IsSuccess)
            {
                _decodedPath = decoded.Value!;
                var loaded = _inner.Load(_decodedPath);
                if (loaded.IsSuccess)
                {
                    if (pending.Seek is { } seek) _inner.Seek(seek);
                    if (pending.PlayRequested) _inner.Play();
                    return;
                }
                failure = loaded.Error;
            }
            else failure = fallbackFailure ?? decoded.Error;
            CleanupDecoded();
            LoadedFilePath = null;
        }
        PlaybackFailed?.Invoke(this, failure!);
    }

    private async Task<PlatformResult<string>> DecodeAsync(string input, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_temporaryDirectory, $"playback-{Guid.NewGuid():N}.wav");
        try
        {
            Directory.CreateDirectory(_temporaryDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(_ffmpeg)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            // Warning: keep the two bitexact flags and -map_metadata -1.
            //
            // By default ffmpeg writes a LIST/INFO chunk between `fmt ` and `data`. The player
            // reads the CANONICAL 44-byte layout (PcmWaveHeader.TryRead checks for "data" at a
            // fixed offset 36), so a default-flags decode produced a file the player then refused,
            // and every imported non-WAV recording showed "Audio file could not be loaded" with a
            // perfectly good decode sitting on disk. These flags drop the metadata chunk and leave
            // exactly `fmt ` then `data`.
            foreach (var argument in new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", input,
                "-vn", "-acodec", "pcm_s16le", "-map_metadata", "-1",
                "-fflags", "+bitexact", "-flags:a", "+bitexact", "-f", "wav", output,
            }) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return Failure();
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure();
            }
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output)) return Failure();
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return PlatformResult<string>.Success(output);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Failure();
        }

        PlatformResult<string> Failure()
        {
            TryDelete(output);
            return PlatformResult<string>.Failure("audio_decode_failed", "FFmpeg could not decode this recording for playback.");
        }
    }

    // Called under _gate.
    private void CancelPending()
    {
        var pending = _pending;
        _pending = null;
        pending?.Cancellation.Cancel();
    }

    private void CleanupDecoded()
    {
        var path = Interlocked.Exchange(ref _decodedPath, null);
        if (path is not null) TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void ForwardEnded(object? sender, EventArgs args) => PlaybackEnded?.Invoke(this, args);
    private void ForwardPosition(object? sender, TimeSpan value) => PositionChanged?.Invoke(this, value);
    private void ForwardDuration(object? sender, TimeSpan value) => DurationReady?.Invoke(this, value);
    private void ForwardFailure(object? sender, PlatformError value) => PlaybackFailed?.Invoke(this, value);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelPending();
        }
        _inner.PlaybackEnded -= ForwardEnded;
        _inner.PositionChanged -= ForwardPosition;
        _inner.DurationReady -= ForwardDuration;
        _inner.PlaybackFailed -= ForwardFailure;
        _inner.Dispose();
        CleanupDecoded();
    }

    private sealed class PendingDecode
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool PlayRequested { get; set; }
        public TimeSpan? Seek { get; set; }
    }
}
