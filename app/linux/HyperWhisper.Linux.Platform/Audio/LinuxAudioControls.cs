using System.Globalization;
using System.Text;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.SystemIntegration;
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
    // A second resume (the quit's cancel after the recording's own restore, #1038) must not open a
    // second capture child over the first one, which nothing would then stop.
    public void ResumeAfterRecording(string? deviceId) { if (_disposed) return; _device = deviceId ?? _device; _suspended = false; if (_enabled && _source is null) Start(); }
    private void Start()
    {
        // #627: keep the SELECTED microphone warm, never the server default. `_device` is null exactly
        // when no input device is enumerated, and the old `?? "default"` made the child omit `--device`,
        // so on a monitor-only box this held a capture stream open on a sink monitor — visible to the
        // user as HyperWhisper recording their desktop audio. With nothing selected there is nothing to
        // keep warm, so do not open a source at all.
        if (string.IsNullOrWhiteSpace(_device)) return;
        var opened = _factory.Open(new AudioRecordingOptions(_device)); if (opened.IsFailure) return;
        var source = opened.Value!; var cancellation = new CancellationTokenSource();
        _source = source; _cancellation = cancellation; _drain = Task.Run(async () =>
        { try { await source.Output.CopyToAsync(Stream.Null, cancellation.Token); } catch { } });
    }
    private void Stop()
    {
        // #1186: Configure and SuspendForRecording run on the Avalonia UI thread. Blocking it on a source
        // whose continuation posts back to that thread deadlocked the app, so detach the state here and
        // tear down off the caller's context; the worker sees only locals, so a late one never touches
        // a replacement source.
        var source = _source; var drain = _drain; var cancellation = _cancellation; _source = null; _drain = null; _cancellation = null;
        cancellation?.Cancel(); if (source is null && drain is null) { cancellation?.Dispose(); return; }
        var teardown = Task.Run(() =>
        {
            if (source is not null) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); try { source.TerminateAsync(deadline.Token).AsTask().GetAwaiter().GetResult(); } catch { } }
            try { drain?.Wait(TimeSpan.FromSeconds(3)); } catch { } try { source?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            cancellation?.Dispose();
        });
        try { teardown.Wait(TimeSpan.FromSeconds(4)); } catch { }
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
    private readonly IDesktopCommandRunner _runner; private readonly string? _pactl; private readonly PulseAudioMuffler _muffler;
    private readonly object _muffleGate = new(); private PulseMuffleRun? _muffle;
    // Muffle start and claim run here, one at a time and in prepare order, never on the caller's (UI) thread.
    private readonly object _queueGate = new(); private Task _queue = Task.CompletedTask;
    // Every session that changed something and has not finished restoring it, so a quit can finish them all at once.
    private readonly HashSet<DeferredAudioRestore> _pending = [];
    public LinuxAudioEnvironmentService() : this(new DesktopCommandRunner(), CommandClipboardBackend.FindExecutable("pactl"), new LinuxChildProcessLauncher(),
        CommandClipboardBackend.FindExecutable("parec"), CommandClipboardBackend.FindExecutable("pacat"), Environment.ProcessId, PulseAudioMuffler.ParentProcessId) { }
    internal LinuxAudioEnvironmentService(IDesktopCommandRunner runner, string? pactl) : this(runner, pactl, new LinuxChildProcessLauncher(), null, null, Environment.ProcessId, _ => null) { }
    internal LinuxAudioEnvironmentService(IDesktopCommandRunner runner, string? pactl, IChildProcessLauncher launcher, string? parec, string? pacat,
        int ownProcessId, Func<int, int?> parentProcessId)
    {
        _runner = runner; _pactl = pactl; _muffler = new(runner, launcher, pactl, parec, pacat, ownProcessId, parentProcessId);
        _muffler.ActivationFailed = FellBack;
        _muffler.Log = message => { var handlers = MuffleDiagnostic; try { handlers?.Invoke(this, message); } catch { } };
    }
    /// <summary>Raised with an error code, from a worker thread, when Muffle Audio could not start and the recording ducked
    /// instead, or when its sink never carried audio ("audio_environment.muffle_no_audio") or a helper exited
    /// ("audio_environment.muffle_helper_exited") and the other audio played on unmuffled.</summary>
    public event EventHandler<string>? MuffleFellBack;
    /// <summary>Info-level Muffle Audio diagnostics, from any thread: counts and error codes, no stream or device names.</summary>
    public event EventHandler<string>? MuffleDiagnostic;
    /// <summary>A recording's audio change has not been put back yet: it is live, waiting out its restore delay, or restoring.</summary>
    public bool HasPendingRestore { get { lock (_pending) return _pending.Count > 0; } }

    public PlatformResult<IAudioEnvironmentSession> PrepareForRecording(AudioEnvironmentPolicy policy, TimeSpan restoreDelay)
    {
        if (restoreDelay < TimeSpan.Zero) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.invalid_delay", "The restore delay cannot be negative.");
        if (policy == AudioEnvironmentPolicy.Unchanged) return PlatformResult<IAudioEnvironmentSession>.Success(new LinuxAudioEnvironmentSession(null, null, [], restoreDelay));
        if (_pactl is null) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.unsupported", "pactl is unavailable.");
        if (policy == AudioEnvironmentPolicy.MuffleOtherAudio)
        {
            var session = new LinuxMuffleSession(this, restoreDelay); Track(session);
            lock (_queueGate)
            {
                var ready = _queue.ContinueWith(_ => PrepareMuffle(), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                session.Begin(ready); _queue = ready;
            }
            return PlatformResult<IAudioEnvironmentSession>.Success(session);
        }
        var prepared = PrepareVolume(policy, restoreDelay);
        if (prepared.IsSuccess) Track((DeferredAudioRestore)prepared.Value!);
        return prepared;
    }

    private PlatformResult<IAudioEnvironmentSession> PrepareVolume(AudioEnvironmentPolicy policy, TimeSpan restoreDelay)
    {
        try
        {
            var query = policy == AudioEnvironmentPolicy.MuteOtherAudio ? new[] { "get-sink-mute", "@DEFAULT_SINK@" } : ["get-sink-volume", "@DEFAULT_SINK@"];
            var prior = _runner.RunAsync(_pactl!, query, null, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (prior.ExitCode != 0) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.read_failed", "The output audio state could not be read.");
            IReadOnlyList<string> restore; IReadOnlyList<string> mutate;
            if (policy == AudioEnvironmentPolicy.MuteOtherAudio)
            { var muted = Encoding.UTF8.GetString(prior.Output).Contains("yes", StringComparison.OrdinalIgnoreCase); restore = ["set-sink-mute", "@DEFAULT_SINK@", muted ? "1" : "0"]; mutate = ["set-sink-mute", "@DEFAULT_SINK@", "1"]; }
            else
            { var volumes = LinuxMicrophoneVolumeService.Percentages(prior.Output); if (volumes.Count == 0) return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.read_failed", "The output volume could not be read.");
                restore = ["set-sink-volume", "@DEFAULT_SINK@", .. volumes.Select(value => $"{value}%")];
                mutate = ["set-sink-volume", "@DEFAULT_SINK@", .. volumes.Select(value => $"{Math.Min(value, 35)}%")]; }
            var changed = _runner.RunAsync(_pactl!, mutate, null, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return changed.ExitCode == 0 ? PlatformResult<IAudioEnvironmentSession>.Success(new LinuxAudioEnvironmentSession(this, _runner, [_pactl!, .. restore], restoreDelay))
                : PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.change_failed", "The output audio state could not be changed.");
        }
        catch { return PlatformResult<IAudioEnvironmentSession>.Failure("audio_environment.unavailable", "Output audio policy is unavailable."); }
    }

    /// <summary>At app start: removes a muffle sink a crashed run left behind, moving its streams back to a real sink first.</summary>
    public Task<int> RecoverStaleMuffleAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_muffleGate) { if (_muffle is not null) return 0; try { return _muffler.RecoverStale(); } catch { return 0; } }
    }, cancellationToken);

    /// <summary>The quit: every pending restore runs now, skipping its delay and the glide, and waits for the muffle queue,
    /// so no null sink, duck or mute outlives the process. Bounded by pactl's own timeouts.</summary>
    public async Task FlushRestoresAsync()
    {
        DeferredAudioRestore[] pending; lock (_pending) pending = [.. _pending];
        foreach (var session in pending) await session.RestoreNowAsync().ConfigureAwait(false);
        Task queue; lock (_queueGate) queue = _queue;
        try { await queue.ConfigureAwait(false); } catch { }
        // A run no session restores any more (its restore failed twice): tear it down anyway.
        PulseMuffleRun? live; lock (_muffleGate) live = _muffle;
        if (live is null) return;
        try { await live.TeardownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        if (live.IsTornDown) Forget(live);
    }

    // On the muffle queue: claim the live sink, or replace a dying one, or build one; on failure duck instead.
    private MufflePrepared PrepareMuffle()
    {
        string code;
        lock (_muffleGate)
        {
            try
            {
                if (_muffle is { } live)
                {
                    if (live.TryClaim(out var generation)) return new(live, generation, null);
                    // The previous run is tearing down, or its helpers died: let it finish (or finish it) before building a new sink.
                    // A teardown that failed may have left its sink loaded (or, before it got that far, its helpers running):
                    // keep the reference so the next recording or FlushRestoresAsync finishes it, and duck this one instead.
                    var finished = false;
                    try { finished = live.TeardownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { finished = live.IsTornDown; }
                    if (finished) _muffle = null;
                }
                if (_muffle is not null) code = "audio_environment.muffle_busy";
                else
                {
                    var started = _muffler.Start();
                    if (started.IsSuccess) { _muffle = started.Value!; return new(_muffle, _muffle.Generation, null); }
                    code = started.Error!.Code;
                }
            }
            catch { code = "audio_environment.muffle_start_failed"; }
        }
        _muffler.Info($"fell back to duck ({code})");
        FellBack(code);
        var duck = PrepareVolume(AudioEnvironmentPolicy.DuckOtherAudio, TimeSpan.Zero);
        return new(null, 0, duck.IsSuccess ? duck.Value : null);
    }

    private void FellBack(string code) { var handlers = MuffleFellBack; try { handlers?.Invoke(this, code); } catch { } }
    private void Track(DeferredAudioRestore session) { lock (_pending) _pending.Add(session); }
    internal void Untrack(DeferredAudioRestore session) { lock (_pending) _pending.Remove(session); }
    internal PulseAudioMuffler Muffler => _muffler;
    internal void Forget(PulseMuffleRun run) { lock (_muffleGate) if (ReferenceEquals(_muffle, run)) _muffle = null; }
}

/// <summary>A restore that first waits out a delay. A second call while one runs joins it and shares its outcome: a failure
/// throws for both, and a joiner's own token cancels only its wait. When the owner was cancelled by its own token, a joiner
/// runs the restore itself. One that failed or was cancelled can be retried; <see cref="RestoreNowAsync"/> (the quit) skips
/// every remaining delay.</summary>
internal abstract class DeferredAudioRestore(LinuxAudioEnvironmentService? owner) : IAudioEnvironmentSession
{
    private readonly object _gate = new(); private readonly CancellationTokenSource _skip = new();
    private Task? _running; private bool _done;
    protected LinuxAudioEnvironmentService? Owner => owner;
    protected abstract Task RestoreCoreAsync(CancellationToken cancellationToken);

    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TaskCompletionSource? mine = null; Task running;
            lock (_gate)
            {
                if (_done) return;
                if (_running is null) { mine = new(TaskCreationOptions.RunContinuationsAsynchronously); _running = mine.Task; }
                running = _running;
            }
            if (mine is null)
            {
                try { await running.WaitAsync(cancellationToken).ConfigureAwait(false); return; }
                // The owner's caller cancelled, not this one: this caller still wants the restore, so it takes it over.
                catch (OperationCanceledException) when (running.IsCanceled && !cancellationToken.IsCancellationRequested) { continue; }
            }
            try
            {
                await RestoreCoreAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate) { _done = true; _running = null; }
                owner?.Untrack(this);
                mine.TrySetResult();
                return;
            }
            catch (Exception error)
            {
                lock (_gate) _running = null;
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) mine.TrySetCanceled(cancellationToken);
                else
                {
                    mine.TrySetException(error);
                    // With no joiner the throw below reports it; reading it keeps the task from being reported as unobserved.
                    _ = mine.Task.Exception;
                }
                throw;
            }
        }
    }

    /// <summary>Restores at once, joining a restore already waiting (its delay ends now), and retries once if that fails.</summary>
    public async Task RestoreNowAsync()
    {
        try { _skip.Cancel(); } catch { }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { await RestoreAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            lock (_gate) if (_done) return;
        }
    }

    /// <summary>The caller's token cancels (and throws); a quit's skip just ends the wait.</summary>
    protected async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero || _skip.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _skip.Token);
        try { await Task.Delay(delay, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync() => await RestoreAsync().ConfigureAwait(false);
}

internal sealed class LinuxAudioEnvironmentSession(LinuxAudioEnvironmentService? owner, IDesktopCommandRunner? runner, IReadOnlyList<string> command, TimeSpan delay)
    : DeferredAudioRestore(owner)
{
    protected override async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        if (runner is null || command.Count == 0) return;
        await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        var result = await runner.RunAsync(command[0], command.Skip(1).ToArray(), null, cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("The output audio state could not be restored.");
    }
}
