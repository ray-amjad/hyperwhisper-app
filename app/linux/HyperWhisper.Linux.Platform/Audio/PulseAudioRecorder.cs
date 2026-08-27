using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

public sealed class PulseAudioRecorder : IAudioRecorder
{
    private readonly object _gate = new();
    private readonly IPulseAudioApi _api;
    private readonly IAppPaths _paths;
    private IPulseAudioRecordSession? _session;
    private FileStream? _output;
    private Task? _captureTask;
    private volatile bool _stopRequested;
    private string? _outputPath;
    private WaveFormat? _format;
    private long _dataBytes;
    private PlatformError? _captureError;
    private bool _disposed;

    public PulseAudioRecorder(IAppPaths paths)
        : this(new PulseAudioApi(), paths)
    {
    }

    internal PulseAudioRecorder(IPulseAudioApi api, IAppPaths paths)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public event EventHandler<float>? AudioLevelChanged;

    public bool IsRecording { get; private set; }
    public TimeSpan Duration => _format is null || _format.BytesPerSecond == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)Interlocked.Read(ref _dataBytes) / _format.BytesPerSecond);

    public PulseAudioCapabilities GetCapabilities() => _api.GetCapabilities();

    public PlatformResult Start(AudioRecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_disposed)
            {
                return PlatformResult.Failure("audio_recorder_disposed", "The audio recorder is disposed.");
            }

            if (IsRecording)
            {
                return PlatformResult.Failure("audio_already_recording", "Audio recording is already active.");
            }

            if (options.SampleRate <= 0 || options.ChannelCount <= 0 || options.BitsPerSample != 16)
            {
                return PlatformResult.Failure("audio_format_unsupported", "PulseAudio recording requires positive-rate 16-bit PCM.");
            }

            if (!_api.GetCapabilities().Available)
            {
                return PlatformResult.Failure("pulse_unavailable", "PulseAudio compatibility libraries are unavailable.");
            }

            var opened = _api.OpenRecord(options);
            if (opened.IsFailure)
            {
                return PlatformResult.Failure(opened.Error!.Code, opened.Error.Message);
            }

            try
            {
                Directory.CreateDirectory(
                    _paths.RecordingsDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                _outputPath = Path.Combine(_paths.RecordingsDirectory, $"recording-{Guid.NewGuid():N}.wav");
                _output = new FileStream(_outputPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.Read,
                    Options = FileOptions.WriteThrough,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                _format = new WaveFormat(options.SampleRate, (short)options.BitsPerSample, (short)options.ChannelCount);
                // The length fields are a placeholder until CaptureLoop patches them on stop.
                // A crash in between leaves them at zero, which readers survive because
                // PcmWaveHeader recomputes the payload length from the file itself.
                WaveFile.WriteHeader(_output, _format, 0);
                _output.Position = WaveFile.HeaderSize;
                _session = opened.Value!;
                _dataBytes = 0;
                _captureError = null;
                _stopRequested = false;
                IsRecording = true;
                _captureTask = Task.Run(CaptureLoop);
                return PlatformResult.Success();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                opened.Value!.Dispose();
                SafeDispose(ref _output);
                return PlatformResult.Failure("audio_output_failed", "The recording file could not be created securely.");
            }
        }
    }

    public PlatformResult<string> Stop()
    {
        Task? task;
        lock (_gate)
        {
            if (!IsRecording && _captureTask is null)
            {
                return PlatformResult<string>.Failure("audio_not_recording", "Audio recording is not active.");
            }

            _stopRequested = true;
            task = _captureTask;
        }

        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch
        {
        }

        lock (_gate)
        {
            var path = _outputPath;
            _captureTask = null;
            IsRecording = false;
            return _captureError is not null
                ? PlatformResult<string>.Failure(_captureError.Code, _captureError.Message)
                : path is null
                    ? PlatformResult<string>.Failure("audio_output_missing", "The recording output path is unavailable.")
                    : PlatformResult<string>.Success(path);
        }
    }

    private void CaptureLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_stopRequested)
            {
                var read = _session!.Read(buffer);
                if (read.IsFailure)
                {
                    _captureError = read.Error;
                    break;
                }

                if (read.Value <= 0)
                {
                    break;
                }

                _output!.Write(buffer, 0, read.Value);
                Interlocked.Add(ref _dataBytes, read.Value);
                RaiseAudioLevel(CalculateLevel(buffer.AsSpan(0, read.Value)));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            if (!_stopRequested)
            {
                _captureError = new PlatformError("audio_capture_failed", "PulseAudio capture stopped unexpectedly.");
            }
        }
        finally
        {
            try
            {
                if (_output is not null && _format is not null)
                {
                    WaveFile.WriteHeader(_output, _format, Interlocked.Read(ref _dataBytes));
                    _output.Flush(flushToDisk: true);
                }
            }
            catch (Exception exception) when (exception is IOException or OverflowException)
            {
                _captureError ??= new PlatformError("audio_finalize_failed", "The recording file could not be finalized.");
            }
            finally
            {
                SafeDispose(ref _session);
                SafeDispose(ref _output);
                IsRecording = false;
            }
        }
    }

    private void RaiseAudioLevel(float level)
    {
        var handlers = AudioLevelChanged;
        if (handlers is null) return;
        foreach (EventHandler<float> handler in handlers.GetInvocationList())
        {
            try { handler(this, level); } catch { }
        }
    }

    private static float CalculateLevel(ReadOnlySpan<byte> pcm)
    {
        long sum = 0;
        var samples = pcm.Length / 2;
        for (var index = 0; index < samples * 2; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8);
            sum += Math.Abs((int)sample);
        }
        return samples == 0 ? 0 : Math.Clamp((float)sum / samples / short.MaxValue, 0, 1);
    }

    private static void SafeDispose<T>(ref T? value) where T : class, IDisposable
    {
        var current = Interlocked.Exchange(ref value, null);
        try { current?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (IsRecording || _captureTask is not null) Stop();
        SafeDispose(ref _session);
        SafeDispose(ref _output);
        GC.SuppressFinalize(this);
    }
}
