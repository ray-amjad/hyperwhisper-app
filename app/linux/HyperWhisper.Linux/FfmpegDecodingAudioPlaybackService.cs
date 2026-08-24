using System.Diagnostics;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux;

/// <summary>Decodes non-WAV history audio to a private temporary WAV before Pulse playback.</summary>
internal sealed class FfmpegDecodingAudioPlaybackService : IAudioPlaybackService
{
    private readonly IAudioPlaybackService _inner;
    private readonly string _temporaryDirectory;
    private readonly string _ffmpeg;
    private string? _decodedPath;
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
    public bool IsPlaying => _inner.IsPlaying;
    public bool IsLoaded => _inner.IsLoaded;
    public TimeSpan TotalDuration => _inner.TotalDuration;
    public string? LoadedFilePath { get; private set; }

    public PlatformResult Load(string audioPath)
    {
        if (_disposed) return PlatformResult.Failure("audio_playback_disposed", "The playback service is disposed.");
        _inner.Stop();
        CleanupDecoded();
        LoadedFilePath = null;
        var input = audioPath;
        if (!string.Equals(Path.GetExtension(audioPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Decode(audioPath);
            if (decoded.IsFailure) return PlatformResult.Failure(decoded.Error!.Code, decoded.Error.Message);
            input = _decodedPath = decoded.Value!;
        }
        var loaded = _inner.Load(input);
        if (loaded.IsFailure) { CleanupDecoded(); return loaded; }
        LoadedFilePath = audioPath;
        return PlatformResult.Success();
    }

    public void Play() => _inner.Play();
    public void Pause() => _inner.Pause();
    public void Stop() => _inner.Stop();
    public void Seek(TimeSpan position) => _inner.Seek(position);

    private PlatformResult<string> Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input) || !File.Exists(input))
            return PlatformResult<string>.Failure("audio_path_invalid", "A readable, fully qualified audio path is required.");
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
            foreach (var argument in new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", input,
                "-vn", "-acodec", "pcm_s16le", "-f", "wav", output,
            }) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return Failure();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
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
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            return PlatformResult<string>.Failure("audio_decode_failed", "FFmpeg could not decode this recording for playback.");
        }
    }

    private void CleanupDecoded()
    {
        var path = Interlocked.Exchange(ref _decodedPath, null);
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
    }

    private void ForwardEnded(object? sender, EventArgs args) => PlaybackEnded?.Invoke(this, args);
    private void ForwardPosition(object? sender, TimeSpan value) => PositionChanged?.Invoke(this, value);
    private void ForwardDuration(object? sender, TimeSpan value) => DurationReady?.Invoke(this, value);
    private void ForwardFailure(object? sender, PlatformError value) => PlaybackFailed?.Invoke(this, value);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.PlaybackEnded -= ForwardEnded;
        _inner.PositionChanged -= ForwardPosition;
        _inner.DurationReady -= ForwardDuration;
        _inner.PlaybackFailed -= ForwardFailure;
        _inner.Dispose();
        CleanupDecoded();
    }
}
