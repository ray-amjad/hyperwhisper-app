using System.Globalization;
using System.Text;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

public sealed class LinuxMicrophoneVolumeService : IMicrophoneVolumeService
{
    private readonly IDesktopCommandRunner _runner; private readonly string? _pactl;
    private string? _device; private IReadOnlyList<int>? _prior;
    public LinuxMicrophoneVolumeService() : this(new DesktopCommandRunner(), CommandClipboardBackend.FindExecutable("pactl")) { }
    internal LinuxMicrophoneVolumeService(IDesktopCommandRunner runner, string? pactl) { _runner = runner; _pactl = pactl; }
    public PlatformResult<float?> ReadLevel(string deviceId)
    {
        if (_pactl is null) return PlatformResult<float?>.Failure("microphone_volume.unsupported", "pactl is unavailable.");
        try { var result = Run(["get-source-volume", Normalize(deviceId)]); var values = result.ExitCode == 0 ? Percentages(result.Output) : [];
            return values.Count == 0 ? PlatformResult<float?>.Failure("microphone_volume.read_failed", "The microphone volume could not be read.")
                : PlatformResult<float?>.Success((float)values.Average() / 100f); }
        catch { return PlatformResult<float?>.Failure("microphone_volume.read_failed", "The microphone volume could not be read."); }
    }
    public PlatformResult BoostIfNeeded(string deviceId)
    {
        if (_pactl is null) return PlatformResult.Failure("microphone_volume.unsupported", "pactl is unavailable.");
        try
        {
            var device = Normalize(deviceId); var read = Run(["get-source-volume", device]); var values = read.ExitCode == 0 ? Percentages(read.Output) : [];
            if (values.Count == 0) return PlatformResult.Failure("microphone_volume.read_failed", "The microphone volume could not be read.");
            if (_prior is not null && _device != device) return PlatformResult.Failure("microphone_volume.session_active", "A different microphone volume restore is still pending.");
            if (_prior is null) { _prior = values; _device = device; }
            if (values.All(value => value >= 100)) return PlatformResult.Success();
            return Run(["set-source-volume", device, "100%"]).ExitCode == 0 ? PlatformResult.Success()
                : PlatformResult.Failure("microphone_volume.boost_failed", "The microphone volume could not be adjusted.");
        }
        catch { return PlatformResult.Failure("microphone_volume.boost_failed", "The microphone volume could not be adjusted."); }
    }
    public PlatformResult Restore()
    {
        if (_prior is null || _device is null) return PlatformResult.Success();
        try { var args = new List<string> { "set-source-volume", _device }; args.AddRange(_prior.Select(value => $"{value}%"));
            var result = Run(args); if (result.ExitCode != 0) return PlatformResult.Failure("microphone_volume.restore_failed", "The microphone volume could not be restored.");
            _prior = null; _device = null; return PlatformResult.Success(); }
        catch { return PlatformResult.Failure("microphone_volume.restore_failed", "The microphone volume could not be restored."); }
    }
    private ExternalProcessResult Run(IReadOnlyList<string> args) => _runner.RunAsync(_pactl!, args, null, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value)
        || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase) ? "@DEFAULT_SOURCE@" : value;
    internal static IReadOnlyList<int> Percentages(byte[] output) => Encoding.UTF8.GetString(output).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Where(value => value.EndsWith('%')).Select(value => int.TryParse(value.AsSpan(0, value.Length - 1), out var parsed) ? parsed : -1).Where(value => value >= 0).ToArray();
}

public sealed class LinuxMicrophoneKeepWarmService : IMicrophoneKeepWarmService
{
    private readonly IStreamingAudioSourceFactory _factory; private IStreamingAudioSource? _source;
    private CancellationTokenSource? _cancellation; private Task? _drain; private bool _enabled; private bool _suspended; private string? _device; private bool _disposed;
    public LinuxMicrophoneKeepWarmService() : this(new ChildProcessStreamingAudioSourceFactory()) { }
    internal LinuxMicrophoneKeepWarmService(IStreamingAudioSourceFactory factory) => _factory = factory;
    public PulseAudioCapabilities GetCapabilities() => new(_factory.IsAvailable, _factory.Backend,
        _factory.IsAvailable ? "cancellable-child-capture" : "capture-helper-unavailable");
    public void Configure(bool enabled, string? deviceId) { if (_disposed) return; Stop(); _enabled = enabled; _device = deviceId; if (enabled && !_suspended) Start(); }
    public void SuspendForRecording() { if (_disposed) return; _suspended = true; Stop(); }
    public void ResumeAfterRecording(string? deviceId) { if (_disposed) return; _device = deviceId ?? _device; _suspended = false; if (_enabled) Start(); }
    private void Start()
    {
        var opened = _factory.Open(new AudioRecordingOptions(_device ?? "default")); if (opened.IsFailure) return;
        var source = opened.Value!; var cancellation = new CancellationTokenSource();
        _source = source; _cancellation = cancellation; _drain = Task.Run(async () =>
        { try { await source.Output.CopyToAsync(Stream.Null, cancellation.Token); } catch { } });
    }
    private void Stop()
    {
        var source = _source; _cancellation?.Cancel();
        if (source is not null) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); try { source.TerminateAsync(deadline.Token).AsTask().GetAwaiter().GetResult(); } catch { } }
        try { _drain?.Wait(TimeSpan.FromSeconds(3)); } catch { } try { source?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _source = null; _drain = null; _cancellation?.Dispose(); _cancellation = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
}

public sealed class LinuxSoundEffectsService : ISoundEffectsService
{
    private readonly IDesktopCommandRunner _runner; private readonly string? _player; private readonly string _assets; private bool _disposed; private double _volume = 1;
    public LinuxSoundEffectsService() : this(new DesktopCommandRunner(), CommandClipboardBackend.FindExecutable("paplay") ?? CommandClipboardBackend.FindExecutable("pw-play"), Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds")) { }
    internal LinuxSoundEffectsService(IDesktopCommandRunner runner, string? player, string assets) { _runner = runner; _player = player; _assets = assets; }
    public PlatformResult Play(SoundEffect effect)
    {
        if (_disposed) return PlatformResult.Failure("sound_effects.disposed", "Sound effects are disposed.");
        var path = Path.Combine(_assets, effect == SoundEffect.RecordingStarted ? "start1.wav" : "stop1.wav");
        if (_player is null || !File.Exists(path)) return PlatformResult.Failure("sound_effects.unsupported", "A packaged sound effect or supported player is unavailable.");
        try { var result = _runner.RunAsync(_player, PlaybackArguments(_player, path, _volume), null, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return result.ExitCode == 0 ? PlatformResult.Success() : PlatformResult.Failure("sound_effects.play_failed", "The sound effect could not be played."); }
        catch { return PlatformResult.Failure("sound_effects.play_failed", "The sound effect could not be played."); }
    }
    public PlatformResult ConfigureVolume(double volume)
    {
        if (_disposed) return PlatformResult.Failure("sound_effects.disposed", "Sound effects are disposed.");
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
            return PlatformResult.Failure("sound_effects.invalid_volume", "Sound effect volume must be between zero and one.");
        _volume = volume;
        return PlatformResult.Success();
    }
    internal static IReadOnlyList<string> PlaybackArguments(string player, string path, double volume)
    {
        var executable = Path.GetFileName(player);
        return string.Equals(executable, "paplay", StringComparison.Ordinal)
            ? [$"--volume={(int)Math.Round(volume * 65_536, MidpointRounding.AwayFromZero)}", path]
            : [$"--volume={volume.ToString("0.###", CultureInfo.InvariantCulture)}", path];
    }
    public void Dispose() => _disposed = true;
}

public sealed class LinuxAudioEnvironmentService : IAudioEnvironmentService
{
    private readonly IDesktopCommandRunner _runner; private readonly string? _pactl;
    public LinuxAudioEnvironmentService() : this(new DesktopCommandRunner(), CommandClipboardBackend.FindExecutable("pactl")) { }
    internal LinuxAudioEnvironmentService(IDesktopCommandRunner runner, string? pactl) { _runner = runner; _pactl = pactl; }
    public PlatformResult<IAudioEnvironmentSession> PrepareForRecording(AudioEnvironmentPolicy policy, TimeSpan restoreDelay)
    {
        if (restoreDelay < TimeSpan.Zero) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.invalid_delay", "The restore delay cannot be negative.");
        if (policy == AudioEnvironmentPolicy.Unchanged) return PlatformResult<IAudioEnvironmentSession>.Success(new LinuxAudioEnvironmentSession(null, [], restoreDelay));
        if (_pactl is null) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.unsupported", "pactl is unavailable.");
        try
        {
            var query = policy == AudioEnvironmentPolicy.MuteOtherAudio ? new[] { "get-sink-mute", "@DEFAULT_SINK@" } : ["get-sink-volume", "@DEFAULT_SINK@"];
            var prior = _runner.RunAsync(_pactl, query, null, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (prior.ExitCode != 0) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.read_failed", "The output audio state could not be read.");
            IReadOnlyList<string> restore; IReadOnlyList<string> mutate;
            if (policy == AudioEnvironmentPolicy.MuteOtherAudio)
            { var muted = Encoding.UTF8.GetString(prior.Output).Contains("yes", StringComparison.OrdinalIgnoreCase); restore = ["set-sink-mute", "@DEFAULT_SINK@", muted ? "1" : "0"]; mutate = ["set-sink-mute", "@DEFAULT_SINK@", "1"]; }
            else
            { var volumes = LinuxMicrophoneVolumeService.Percentages(prior.Output); if (volumes.Count == 0) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.read_failed", "The output volume could not be read.");
                restore = ["set-sink-volume", "@DEFAULT_SINK@", .. volumes.Select(value => $"{value}%")];
                mutate = ["set-sink-volume", "@DEFAULT_SINK@", .. volumes.Select(value => $"{Math.Min(value, 35)}%")]; }
            var changed = _runner.RunAsync(_pactl, mutate, null, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return changed.ExitCode == 0 ? PlatformResult<IAudioEnvironmentSession>.Success(new LinuxAudioEnvironmentSession(_runner, [_pactl, .. restore], restoreDelay))
                : PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.change_failed", "The output audio state could not be changed.");
        }
        catch { return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.unavailable", "Output audio policy is unavailable."); }
    }
}

internal sealed class LinuxAudioEnvironmentSession(IDesktopCommandRunner? runner, IReadOnlyList<string> command, TimeSpan delay) : IAudioEnvironmentSession
{
    private int _restored;
    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _restored, 1, 0) != 0 || runner is null || command.Count == 0) return;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var result = await runner.RunAsync(command[0], command.Skip(1).ToArray(), null, cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException("The output audio state could not be restored.");
        }
        catch { Volatile.Write(ref _restored, 0); throw; }
    }
    public async ValueTask DisposeAsync() => await RestoreAsync().ConfigureAwait(false);
}
