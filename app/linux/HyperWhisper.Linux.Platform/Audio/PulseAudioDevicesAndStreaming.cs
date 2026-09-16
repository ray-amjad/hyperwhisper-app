using System.Text;
using System.Text.Json;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

public sealed class PulseAudioInputDeviceService : IAudioInputDeviceService
{
    // Holds the list just enumerated for exactly as long as this service is raising DevicesChanged, and
    // only on the thread that is raising. The slot is per INSTANCE (a field) and per THREAD (a
    // ThreadLocal), which is what makes the guard below scoped correctly by construction rather than by
    // bookkeeping: another service that enumerates from inside our handler gets its own slot, so ours
    // stays armed for the whole raise and an A -> B -> A chain terminates at A; and a genuinely
    // independent thread gets its own slot, so it stays free to enumerate and raise. Nothing is saved
    // and restored, because no frame of THIS instance on THIS thread can ever reach the assignment
    // while the slot is already set — the guard returns first — so the slot is always null beforehand.
    private readonly ThreadLocal<IReadOnlyList<AudioInputDevice>?> _publishing = new();
    private readonly IDesktopCommandRunner _runner;
    private readonly string? _pactl;
    private string? _lastDeviceKey;
    public PulseAudioInputDeviceService() : this(new DesktopCommandRunner(), CommandClipboardBackend.FindExecutable("pactl")) { }
    internal PulseAudioInputDeviceService(IDesktopCommandRunner runner, string? pactl) { _runner = runner; _pactl = pactl; }
    public event EventHandler? DevicesChanged;
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices()
    {
        // The whole method is inside the try so that a call racing Dispose() reports a failure instead of
        // throwing ObjectDisposedException out of _publishing at an unlucky shutdown.
        try
        {
            // Re-entrant call (issue #621): our own DevicesChanged handler is on the stack, because
            // TranscriptionWorkflow.OnDevicesChanged -> RefreshDevices -> GetAvailableDevices runs
            // synchronously. Hand back the list the raising frame enumerated microseconds ago instead of
            // shelling out to pactl a second time on the Avalonia UI thread. This returns before the key
            // comparison and before RaiseDevicesChanged, so the nested frame can never raise and the
            // recursion stops here whether the device set converges or flaps forever.
            // THE BOUND: a frame that raises holds its list in this instance's slot for THIS thread for
            // the entire duration of the raise, and any call that finds that slot set returns here —
            // before enumerating and before RaiseDevicesChanged — so no (instance, thread) pair can ever
            // carry two raising frames at once. Depth is therefore capped at two frames per distinct
            // service instance on the thread, which with the one instance LinuxDesktopServices builds is
            // a cap of two. It is structural: it does not need the device set to converge, or a fuse.
            if (_publishing.Value is { } publishing)
                return PlatformResult<IReadOnlyList<AudioInputDevice>>.Success(publishing);
            if (_pactl is null) return PlatformResult<IReadOnlyList<AudioInputDevice>>.Failure("pulse_devices_unavailable", "pactl is unavailable.");
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
                if (IsMonitor(source)) continue;
                var id = source.TryGetProperty("name", out var name) ? name.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var description = source.TryGetProperty("description", out var label) ? label.GetString() : null;
                values.Add(new AudioInputDevice(id, string.IsNullOrWhiteSpace(description) ? id : description, id == defaultId));
            }
            // #627: `pactl get-default-source` can name a sink monitor, which the filter above drops, so no
            // offerable device carries the flag. `IsDefault` is never rendered — its only two readers pick the
            // device to use when nothing is chosen, and each then falls back to its own first element: the
            // workflow to pactl order, the tray to Id order. Promote the first offerable source so both agree.
            // This is a no-op for the recording path, which already lands on exactly this device.
            if (values.Count > 0 && !values.Any(value => value.IsDefault)) values[0] = values[0] with { IsDefault = true };
            var key = string.Join('\n', values.Select(value => $"{value.Id}:{value.IsDefault}"));
            // Publish the key BEFORE raising, so a concurrent enumeration on another thread — which the
            // per-thread guard above deliberately does not suppress — does not raise for this same
            // transition a second time. Interlocked, not `var previous = _lastDeviceKey; _lastDeviceKey
            // = key;`, because that pair is a non-atomic read-modify-write: two threads that enumerate
            // the same new device set can both read the old key before either writes, and then both
            // raise. Claiming the property in a comment and not delivering it is worse than not
            // claiming it, because the next maintainer reads the comment and does not add the fence.
            var previous = Interlocked.Exchange(ref _lastDeviceKey, key);
            if (previous is null || previous == key)
                return PlatformResult<IReadOnlyList<AudioInputDevice>>.Success(values);
            _publishing.Value = values;
            // Disarming in finally is what keeps the guard a guard and not a latch: leave it armed and
            // every later refresh on this thread would be served the list captured at this one device
            // change, so plugging a microphone in would never update the list again.
            try { RaiseDevicesChanged(); }
            finally { _publishing.Value = null; }
            return PlatformResult<IReadOnlyList<AudioInputDevice>>.Success(values);
        }
        catch { return PlatformResult<IReadOnlyList<AudioInputDevice>>.Failure("pulse_devices_failed", "PulseAudio device enumeration failed."); }
    }
    // A sink monitor is not a microphone (#627). pactl 16.1 emits no `monitor_of_sink` on a source, so that
    // documented server field alone matched nothing: the markers that hold are `monitor_source` (the sink name
    // on a monitor, empty on a real source) and `properties["device.class"] == "monitor"` on pipewire-pulse.
    private static bool IsMonitor(JsonElement source)
    {
        if (source.TryGetProperty("monitor_of_sink", out var sink) && (sink.ValueKind == JsonValueKind.Number
            || (sink.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(sink.GetString())))) return true;
        if (source.TryGetProperty("monitor_source", out var monitor) && monitor.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(monitor.GetString())) return true;
        if (!source.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return false;
        return properties.TryGetProperty("device.class", out var deviceClass) && deviceClass.ValueKind == JsonValueKind.String
            && string.Equals(deviceClass.GetString(), "monitor", StringComparison.OrdinalIgnoreCase);
    }
    private void RaiseDevicesChanged()
    { var handlers = DevicesChanged; if (handlers is null) return; foreach (EventHandler handler in handlers.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch { } }
    public void Dispose() { DevicesChanged = null; _publishing.Dispose(); }
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
