using System.Text;
using System.Text.Json;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

public sealed class PulseAudioInputDeviceService : IAudioInputDeviceService
{
    private readonly IDesktopCommandRunner _runner;
    private readonly string? _pactl;
    private string? _lastDeviceKey;
    public PulseAudioInputDeviceService() : this(new DesktopCommandRunner(), CommandClipboardBackend.FindExecutable("pactl")) { }
    internal PulseAudioInputDeviceService(IDesktopCommandRunner runner, string? pactl) { _runner = runner; _pactl = pactl; }
    public event EventHandler? DevicesChanged;
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices()
    {
        if (_pactl is null) return PlatformResult<IReadOnlyList<AudioInputDevice>>.Failure("pulse_devices_unavailable", "pactl is unavailable.");
        try
        {
            var sources = _runner.RunAsync(_pactl, ["--format=json", "list", "sources"], null, CancellationToken.None,
                TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var fallback = sources.ExitCode == 0 ? sources : _runner.RunAsync(_pactl, ["-f", "json", "list", "sources"], null,
                CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (fallback.ExitCode != 0) return PlatformResult<IReadOnlyList<AudioInputDevice>>.Failure("pulse_devices_failed", "PulseAudio device enumeration failed.");
            var defaultResult = _runner.RunAsync(_pactl, ["get-default-source"], null, CancellationToken.None,
                TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var defaultId = defaultResult.ExitCode == 0 ? Encoding.UTF8.GetString(defaultResult.Output).Trim() : string.Empty;
            using var document = JsonDocument.Parse(fallback.Output);
            var values = new List<AudioInputDevice>();
            foreach (var source in document.RootElement.EnumerateArray())
            {
                if (source.TryGetProperty("monitor_of_sink", out var monitor) && monitor.ValueKind != JsonValueKind.Null) continue;
                var id = source.TryGetProperty("name", out var name) ? name.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var description = source.TryGetProperty("description", out var label) ? label.GetString() : null;
                values.Add(new AudioInputDevice(id, string.IsNullOrWhiteSpace(description) ? id : description, id == defaultId));
            }
            var key = string.Join('\n', values.Select(value => $"{value.Id}:{value.IsDefault}"));
            if (_lastDeviceKey is not null && _lastDeviceKey != key) RaiseDevicesChanged();
            _lastDeviceKey = key;
            return PlatformResult<IReadOnlyList<AudioInputDevice>>.Success(values);
        }
        catch { return PlatformResult<IReadOnlyList<AudioInputDevice>>.Failure("pulse_devices_failed", "PulseAudio device enumeration failed."); }
    }
    private void RaiseDevicesChanged()
    { var handlers = DevicesChanged; if (handlers is null) return; foreach (EventHandler handler in handlers.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch { } }
    public void Dispose() { DevicesChanged = null; }
}

internal interface IStreamingAudioSource : IAsyncDisposable
{
    Stream Output { get; }
    ValueTask TerminateAsync(CancellationToken cancellationToken);
}

internal interface IStreamingAudioSourceFactory
{
    bool IsAvailable { get; }
    string Backend { get; }
    PlatformResult<IStreamingAudioSource> Open(AudioRecordingOptions options);
}

internal sealed class ChildProcessStreamingAudioSourceFactory : IStreamingAudioSourceFactory
{
    private readonly IChildProcessLauncher _launcher;
    private readonly string? _parec;
    private readonly string? _pwRecord;
    public ChildProcessStreamingAudioSourceFactory() : this(new LinuxChildProcessLauncher(),
        CommandClipboardBackend.FindExecutable("parec"), CommandClipboardBackend.FindExecutable("pw-record")) { }
    internal ChildProcessStreamingAudioSourceFactory(IChildProcessLauncher launcher, string? parec, string? pwRecord)
    { _launcher = launcher; _parec = parec; _pwRecord = pwRecord; }
    public bool IsAvailable => _parec is not null || _pwRecord is not null;
    public string Backend => _parec is not null ? "parec" : _pwRecord is not null ? "pw-record" : "none";
    public PlatformResult<IStreamingAudioSource> Open(AudioRecordingOptions options)
    {
        var executable = _parec ?? _pwRecord;
        if (executable is null) return PlatformResult<IStreamingAudioSource>.Failure("audio_streaming_unavailable", "Neither parec nor pw-record is installed.");
        var explicitDevice = !string.IsNullOrWhiteSpace(options.DeviceId)
            && !string.Equals(options.DeviceId, "default", StringComparison.OrdinalIgnoreCase);
        var arguments = _parec is not null
            ? new List<string> { "--raw", "--format=s16le", $"--rate={options.SampleRate}", $"--channels={options.ChannelCount}" }
            : ["--raw", "--format", "s16", "--rate", options.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--channels", options.ChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (explicitDevice) arguments.Add(_parec is not null ? $"--device={options.DeviceId}" : $"--target={options.DeviceId}");
        if (_parec is null) arguments.Add("-");
        var started = _launcher.Start(new ChildProcessStartRequest
        { ExecutablePath = executable, Arguments = arguments, RedirectStandardOutput = true });
        return started.IsFailure ? PlatformResult<IStreamingAudioSource>.Failure(started.Error!.Code, started.Error.Message)
            : PlatformResult<IStreamingAudioSource>.Success(new ChildProcessStreamingAudioSource(started.Value!));
    }
}

internal sealed class ChildProcessStreamingAudioSource(IChildProcess child) : IStreamingAudioSource
{
    public Stream Output => child.StandardOutput ?? throw new InvalidOperationException("The audio process output is unavailable.");
    public ValueTask TerminateAsync(CancellationToken cancellationToken) => child.TerminateAsync(cancellationToken);
    public ValueTask DisposeAsync() => child.DisposeAsync();
}

public sealed class PulseStreamingAudioCapture : IStreamingAudioCapture
{
    private readonly object _gate = new();
    private readonly IStreamingAudioSourceFactory _factory;
    private IStreamingAudioSource? _source;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private WaveFormat? _format;
    private long _bytes;
    private bool _disposed;
    public PulseStreamingAudioCapture() : this(new ChildProcessStreamingAudioSourceFactory()) { }
    internal PulseStreamingAudioCapture(IStreamingAudioSourceFactory factory) => _factory = factory;
    public event EventHandler<ReadOnlyMemory<byte>>? AudioChunkAvailable;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler<PlatformError?>? CaptureStopped;
    public bool IsCapturing { get; private set; }
    public TimeSpan Duration => _format is null ? TimeSpan.Zero : TimeSpan.FromSeconds((double)Interlocked.Read(ref _bytes) / _format.BytesPerSecond);
    public PlatformResult Start(AudioRecordingOptions options)
    {
        lock (_gate)
        {
            if (_disposed) return PlatformResult.Failure("stream_capture_disposed", "Streaming capture is disposed.");
            if (IsCapturing) return PlatformResult.Failure("audio_already_recording", "Streaming capture is already active.");
            if (options.BitsPerSample != 16 || options.SampleRate <= 0 || options.ChannelCount <= 0)
                return PlatformResult.Failure("audio_format_unsupported", "Streaming capture requires positive-rate 16-bit PCM.");
            var opened = _factory.Open(options);
            if (opened.IsFailure) return PlatformResult.Failure(opened.Error!.Code, opened.Error.Message);
            var source = opened.Value!;
            var cancellation = new CancellationTokenSource();
            _source = source; _cancellation = cancellation;
            _format = new(options.SampleRate, (short)options.BitsPerSample, (short)options.ChannelCount);
            _bytes = 0; IsCapturing = true; _task = Task.Run(() => CaptureLoopAsync(source, cancellation.Token)); return PlatformResult.Success();
        }
    }
    private async Task CaptureLoopAsync(IStreamingAudioSource source, CancellationToken token)
    {
        PlatformError? error = null;
        try
        {
            var buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                var read = await source.Output.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;
                var chunk = buffer.AsMemory(0, read).ToArray();
                Interlocked.Add(ref _bytes, read);
                Raise(AudioChunkAvailable, (ReadOnlyMemory<byte>)chunk);
                Raise(AudioLevelChanged, Level(chunk));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (!token.IsCancellationRequested) error = new("audio_capture_failed", "Streaming audio capture stopped unexpectedly."); }
        finally
        {
            try { await source.DisposeAsync().ConfigureAwait(false); } catch { }
            lock (_gate)
            {
                if (ReferenceEquals(_source, source))
                { _source = null; _task = null; _cancellation?.Dispose(); _cancellation = null; IsCapturing = false; }
            }
            Raise(CaptureStopped, error);
        }
    }
    public void Stop()
    {
        Task? task; IStreamingAudioSource? source; CancellationTokenSource? cancellation;
        lock (_gate) { task = _task; source = _source; cancellation = _cancellation; }
        cancellation?.Cancel();
        if (source is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { source.TerminateAsync(deadline.Token).AsTask().GetAwaiter().GetResult(); } catch { }
        }
        try { task?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        lock (_gate)
        {
            if (task?.IsCompleted != false) { _task = null; _cancellation?.Dispose(); _cancellation = null; IsCapturing = false; }
        }
    }
    private void Raise<T>(EventHandler<T>? handlers, T value)
    { if (handlers is null) return; foreach (EventHandler<T> handler in handlers.GetInvocationList()) try { handler(this, value); } catch { } }
    private static float Level(byte[] pcm)
    { long sum = 0; var samples = pcm.Length / 2; for (var i = 0; i < samples * 2; i += 2) sum += Math.Abs((short)(pcm[i] | pcm[i + 1] << 8)); return samples == 0 ? 0 : Math.Clamp((float)sum / samples / short.MaxValue, 0, 1); }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); AudioChunkAvailable = null; AudioLevelChanged = null; CaptureStopped = null; }
}
