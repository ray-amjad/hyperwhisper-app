using HyperWhisper.Services.Streaming;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

public sealed class WindowsAudioInputDeviceService : PlatformContracts.IAudioInputDeviceService
{
    private readonly AudioDeviceService _inner = new();
    private bool _disposed;

    public WindowsAudioInputDeviceService() => _inner.DevicesChanged += OnDevicesChanged;

    public event EventHandler? DevicesChanged;

    public PlatformContracts.PlatformResult<IReadOnlyList<PlatformContracts.AudioInputDevice>> GetAvailableDevices()
    {
        if (_disposed)
            return PlatformContracts.PlatformResult<IReadOnlyList<PlatformContracts.AudioInputDevice>>.Failure(
                "audio_devices.disposed", "The Windows audio-device service has been disposed.");

        var result = _inner.GetAvailableDevices();
        if (result.IsFailure)
            return PlatformContracts.PlatformResult<IReadOnlyList<PlatformContracts.AudioInputDevice>>.Failure(
                "audio_devices.enumeration_failed", result.Error ?? "Windows could not enumerate audio input devices.");

        IReadOnlyList<PlatformContracts.AudioInputDevice> devices = result.Value!
            .Select(device => new PlatformContracts.AudioInputDevice(
                device.DeviceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                device.Name))
            .ToArray();
        return PlatformContracts.PlatformResult<IReadOnlyList<PlatformContracts.AudioInputDevice>>.Success(devices);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _inner.DevicesChanged -= OnDevicesChanged; }
        finally { _inner.Dispose(); }
        DevicesChanged = null;
    }

    private void OnDevicesChanged(object? sender, EventArgs args)
    {
        if (DevicesChanged == null) return;
        foreach (EventHandler handler in DevicesChanged.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex) { LoggingService.Error("WindowsAudioInputDeviceService: devices-changed handler failed", ex); }
        }
    }
}

public sealed class WindowsAudioRecorder : PlatformContracts.IAudioRecorder
{
    private readonly AudioRecorderService _inner = new();
    private bool _disposed;

    public WindowsAudioRecorder() => _inner.AudioLevelChanged += OnAudioLevelChanged;

    public event EventHandler<float>? AudioLevelChanged;
    public bool IsRecording => _inner.IsRecording;
    public TimeSpan Duration => _inner.Duration;

    public PlatformContracts.PlatformResult Start(PlatformContracts.AudioRecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_disposed)
            return PlatformContracts.PlatformResult.Failure("audio_recorder.disposed", "The Windows audio recorder has been disposed.");
        if (!TryGetDeviceNumber(options.DeviceId, out var deviceNumber))
            return PlatformContracts.PlatformResult.Failure("audio_recorder.invalid_device", "The Windows audio device identifier is invalid.");
        if (options.SampleRate != 16000 || options.BitsPerSample != 16 || options.ChannelCount != 1)
            return PlatformContracts.PlatformResult.Failure("audio_recorder.unsupported_format", "Windows file recording currently supports 16 kHz, 16-bit mono PCM only.");

        try
        {
            _inner.StartRecording(deviceNumber);
            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsAudioRecorder: start failed", ex);
            return PlatformContracts.PlatformResult.Failure("audio_recorder.start_failed", "Windows could not start audio recording.");
        }
    }

    public PlatformContracts.PlatformResult<string> Stop()
    {
        if (_disposed)
            return PlatformContracts.PlatformResult<string>.Failure("audio_recorder.disposed", "The Windows audio recorder has been disposed.");

        var result = _inner.StopRecording();
        return result.IsSuccess
            ? PlatformContracts.PlatformResult<string>.Success(result.Value!)
            : PlatformContracts.PlatformResult<string>.Failure("audio_recorder.stop_failed", result.Error ?? "Windows could not stop audio recording.");
    }

    private void OnAudioLevelChanged(float level)
    {
        if (AudioLevelChanged == null) return;
        foreach (EventHandler<float> handler in AudioLevelChanged.GetInvocationList())
        {
            try { handler(this, level); }
            catch (Exception ex) { LoggingService.Error("WindowsAudioRecorder: audio-level handler failed", ex); }
        }
    }

    internal static bool TryGetDeviceNumber(string deviceId, out int deviceNumber)
        => int.TryParse(deviceId, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out deviceNumber);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _inner.AudioLevelChanged -= OnAudioLevelChanged; }
        finally { _inner.Dispose(); }
        AudioLevelChanged = null;
    }
}

public sealed class WindowsStreamingAudioCapture : PlatformContracts.IStreamingAudioCapture
{
    private readonly StreamingAudioCapture _inner = new();
    private bool _disposed;

    public WindowsStreamingAudioCapture()
    {
        _inner.AudioChunkAvailable += OnAudioChunkAvailable;
        _inner.AudioLevelChanged += OnAudioLevelChanged;
        _inner.CaptureStopped += OnCaptureStopped;
    }

    public event EventHandler<ReadOnlyMemory<byte>>? AudioChunkAvailable;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler<PlatformContracts.PlatformError?>? CaptureStopped;
    public bool IsCapturing => _inner.IsCapturing;
    public TimeSpan Duration => _inner.Duration;

    public PlatformContracts.PlatformResult Start(PlatformContracts.AudioRecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_disposed)
            return PlatformContracts.PlatformResult.Failure("streaming_capture.disposed", "The Windows streaming capture has been disposed.");
        if (!WindowsAudioRecorder.TryGetDeviceNumber(options.DeviceId, out var deviceNumber))
            return PlatformContracts.PlatformResult.Failure("streaming_capture.invalid_device", "The Windows audio device identifier is invalid.");
        if (options.BitsPerSample != 16 || options.ChannelCount != 1)
            return PlatformContracts.PlatformResult.Failure("streaming_capture.unsupported_format", "Windows streaming capture emits 16-bit mono PCM only.");
        try
        {
            _inner.Start(deviceNumber, options.SampleRate);
            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsStreamingAudioCapture: start failed", ex);
            return PlatformContracts.PlatformResult.Failure("streaming_capture.start_failed", "Windows could not start streaming audio capture.");
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        _inner.Stop();
    }

    private void OnAudioChunkAvailable(byte[] chunk) => Raise(AudioChunkAvailable, new ReadOnlyMemory<byte>(chunk), "audio-chunk");
    private void OnAudioLevelChanged(float level) => Raise(AudioLevelChanged, level, "audio-level");
    private void OnCaptureStopped(Exception? exception) => Raise(
        CaptureStopped,
        exception == null ? null : new PlatformContracts.PlatformError("streaming_capture.stopped", "Windows audio capture stopped unexpectedly."),
        "capture-stopped");

    private void Raise<T>(EventHandler<T>? handlers, T value, string eventName)
    {
        if (handlers == null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); }
            catch (Exception ex) { LoggingService.Error($"WindowsStreamingAudioCapture: {eventName} handler failed", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _inner.AudioChunkAvailable -= OnAudioChunkAvailable;
            _inner.AudioLevelChanged -= OnAudioLevelChanged;
            _inner.CaptureStopped -= OnCaptureStopped;
        }
        finally { _inner.Dispose(); }
        AudioChunkAvailable = null;
        AudioLevelChanged = null;
        CaptureStopped = null;
    }
}

public sealed class WindowsMicrophoneKeepWarmService : PlatformContracts.IMicrophoneKeepWarmService
{
    private readonly MicrophoneKeepWarmService _inner = MicrophoneKeepWarmService.Instance;
    private bool _disposed;

    public void Configure(bool enabled, string? deviceId)
        => _inner.Configure(enabled, ParseOptionalDevice(deviceId));

    public void SuspendForRecording()
    {
        if (!_disposed) _inner.SuspendForRecording();
    }

    public void ResumeAfterRecording(string? deviceId)
    {
        if (!_disposed) _inner.ResumeAfterRecording(ParseOptionalDevice(deviceId));
    }

    private int? ParseOptionalDevice(string? deviceId)
        => !_disposed && deviceId != null && WindowsAudioRecorder.TryGetDeviceNumber(deviceId, out var number) ? number : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }
}
