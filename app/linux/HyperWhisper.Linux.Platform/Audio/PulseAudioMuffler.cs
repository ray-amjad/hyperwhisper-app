using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.Platform.Abstractions.Audio;

namespace HyperWhisper.Linux.Platform.Audio;

/// <summary>
/// "Muffle Audio" on PulseAudio and PipeWire-pulse, with stock modules and no plugins. Other apps' streams on the
/// default sink move to a private null sink (<see cref="SinkName"/>); parec reads its monitor as float32, the shared
/// <see cref="MuffleFilter"/> low-passes it, and pacat plays it on the real sink. Streams that start mid-recording are
/// moved as `pactl subscribe` reports them. Restore moves back only what was moved, then unloads the sink. The default
/// sink is never changed, so a crash leaves only a stray null sink, which <see cref="RecoverStale"/> removes.
/// </summary>
internal sealed class PulseAudioMuffler(IDesktopCommandRunner runner, IChildProcessLauncher launcher, string? pactl, string? parec,
    string? pacat, int ownProcessId, Func<int, int?> parentProcessId)
{
    internal const string SinkName = "hw_muffle";
    // Marks our own parec/pacat streams so no scan ever moves them, even where pids are not visible.
    internal const string OwnStreamProperty = "hyperwhisper.muffle";
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    public bool IsAvailable => pactl is not null && parec is not null && pacat is not null;
    /// <summary>How long a new sink may stay silent before the run gives up and leaves every stream where it is.</summary>
    internal TimeSpan ActivationTimeout { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>How often a silent new sink is suspended and resumed to start its clock.</summary>
    internal TimeSpan NudgeInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    /// <summary>How long the activation waits for `pactl subscribe` to prove it is connected before its first scan.</summary>
    internal TimeSpan SubscribeReadyTimeout { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Raised from a worker thread when a started run stopped muffling on its own (its sink never received audio,
    /// or a helper exited), so the other audio plays on unmuffled.</summary>
    internal Action<string>? ActivationFailed { get; set; }
    /// <summary>Info-level diagnostics (counts and error codes only, never stream or device names).</summary>
    internal Action<string>? Log { get; set; }

    public PlatformResult<PulseMuffleRun> Start()
    {
        if (!IsAvailable) return Fail("audio_environment.muffle_unavailable", "pactl, parec or pacat is unavailable.");
        RecoverStale();
        var real = Text(Pactl(["get-default-sink"])).Trim();
        if (real.Length == 0 || real == SinkName) return Fail("audio_environment.muffle_no_sink", "There is no default output sink.");
        var sink = Sinks().FirstOrDefault(item => item.Name == real);
        if (sink is null || sink.Rate is < 8_000 or > 768_000 || sink.Channels is < 1 or > MuffleFilter.MaxChannels)
            return Fail("audio_environment.muffle_no_sink", "The default output sink format could not be read.");
        var load = Pactl(["load-module", "module-null-sink", $"sink_name={SinkName}", $"rate={sink.Rate}", $"channels={sink.Channels}",
            "sink_properties='device.description=\"HyperWhisper Muffle\"'"]);
        if (load.ExitCode != 0 || !int.TryParse(Text(load).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var module))
            return Fail("audio_environment.muffle_load_failed", "The muffle sink could not be created.");
        var run = new PulseMuffleRun(this, module, sink);
        try
        {
            var muffle = Sinks().FirstOrDefault(item => item.Name == SinkName) ?? throw new InvalidOperationException("The muffle sink did not appear.");
            run.MuffleSinkIndex = muffle.Index;
            // module-switch-on-connect may make a new sink the default; this mode must never change it.
            if (Text(Pactl(["get-default-sink"])).Trim() == SinkName) Pactl(["set-default-sink", real]);
            // Subscribe before anything else happens on the sink: the helpers' own streams then prove it is connected,
            // and no stream that starts before the activation's scan can fall between the scan and the subscription.
            Subscribe(run);
            run.StartWatching();
            var format = new[] { "--raw", "--format=float32le", $"--rate={sink.Rate}", $"--channels={sink.Channels}", "--client-name=HyperWhisper",
                "--stream-name=HyperWhisper Muffle", $"--property={OwnStreamProperty}=1" };
            run.Capture = Child(parec!, ["--record", .. format, "--latency-msec=20", $"--device={SinkName}.monitor"], output: true);
            run.Playback = Child(pacat!, ["--playback", .. format, "--latency-msec=30", $"--device={real}"], input: true);
            run.StartPump();
            // Streams move only once audio flows through the new sink (see ActivateAsync), so start never blocks on it.
            run.StartActivation();
            Info($"started ({Number(sink.Rate)} Hz, {Number(sink.Channels)} ch)");
            return PlatformResult<PulseMuffleRun>.Success(run);
        }
        catch (Exception error)
        {
            try { run.TeardownAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            return Fail("audio_environment.muffle_start_failed", error.Message);
        }
    }

    /// <summary>Removes a <see cref="SinkName"/> sink left by a crashed run: its streams go to the default (or any real) sink first.</summary>
    public int RecoverStale()
    {
        if (pactl is null) return 0;
        var sinks = Sinks(); var stale = sinks.Where(item => item.Name == SinkName).ToArray();
        if (stale.Length == 0) return 0;
        var fallback = Text(Pactl(["get-default-sink"])).Trim();
        if (fallback == SinkName || sinks.All(item => item.Name != fallback)) fallback = sinks.FirstOrDefault(item => item.Name != SinkName)?.Name ?? "";
        if (fallback.Length > 0)
            foreach (var input in SinkInputs()) if (stale.Any(item => item.Index == input.Sink)) Pactl(["move-sink-input", Number(input.Index), fallback]);
        foreach (var item in stale) Pactl(["unload-module", Number(item.OwnerModule)]);
        Info($"removed {Number(stale.Length)} stale sink(s)");
        return stale.Length;
    }

    internal bool IsOwn(PulseSinkInput input) => input.OwnMarker || input.ProcessId is { } pid && (pid == ownProcessId || parentProcessId(pid) == ownProcessId);
    internal ExternalProcessResult Pactl(IReadOnlyList<string> arguments) => PactlAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();
    internal Task<ExternalProcessResult> PactlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        runner.RunAsync(pactl!, arguments, null, cancellationToken, CommandTimeout);
    internal IReadOnlyList<PulseSink> Sinks() => ParseSinks(Pactl(["--format=json", "list", "sinks"]));
    internal IReadOnlyList<PulseSinkInput> SinkInputs() => ParseSinkInputs(Pactl(["--format=json", "list", "sink-inputs"]));
    internal async Task<IReadOnlyList<PulseSinkInput>> SinkInputsAsync(CancellationToken cancellationToken) =>
        ParseSinkInputs(await PactlAsync(["--format=json", "list", "sink-inputs"], cancellationToken).ConfigureAwait(false));
    // A crashed app (kill -9) closes this pipe, but `pactl subscribe` never writes to its parent and so would run on, orphaned,
    // holding a server connection. The shell runs it in the background and kills it once our end of its stdin closes.
    internal const string Shell = "/bin/sh";
    internal const string WatchScript = "\"$@\" </dev/null & p=$!; exec >/dev/null; read -r _ || :; kill \"$p\" 2>/dev/null";
    internal static IReadOnlyList<string> TiedArguments(string executable, IReadOnlyList<string> arguments) =>
        ["-c", WatchScript, "hyperwhisper-muffle-watch", executable, .. arguments];
    internal void Subscribe(PulseMuffleRun run) =>
        run.Subscription = Child(Shell, TiedArguments(pactl!, ["subscribe"]), output: true, input: true,
            environment: new Dictionary<string, string?> { ["LC_ALL"] = "C" });
    internal void Info(string message) { try { Log?.Invoke(message); } catch { } }
    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(ExternalProcessResult result) => result.ExitCode == 0 ? Encoding.UTF8.GetString(result.Output) : "";
    private static PlatformResult<PulseMuffleRun> Fail(string code, string message) => PlatformResult<PulseMuffleRun>.Failure(code, message);
    private IChildProcess Child(string executable, IReadOnlyList<string> arguments, bool output = false, bool input = false,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var started = launcher.Start(new ChildProcessStartRequest { ExecutablePath = executable, Arguments = arguments, RedirectStandardOutput = output,
            RedirectStandardInput = input, Environment = environment ?? new Dictionary<string, string?>() });
        return started.IsSuccess ? started.Value! : throw new InvalidOperationException($"{Path.GetFileName(executable)} did not start: {started.Error!.Code}");
    }

    private static readonly Regex SampleSpec = new(@"(\d+)ch (\d+)Hz", RegexOptions.CultureInvariant);
    internal static IReadOnlyList<PulseSink> ParseSinks(ExternalProcessResult result)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException("pactl could not list sinks.");
        using var document = JsonDocument.Parse(result.Output); var sinks = new List<PulseSink>();
        foreach (var sink in document.RootElement.EnumerateArray())
        {
            var spec = SampleSpec.Match(String(sink, "sample_specification") ?? "");
            sinks.Add(new(Int(sink, "index") ?? -1, String(sink, "name") ?? "", Int(sink, "owner_module") ?? -1,
                spec.Success ? int.Parse(spec.Groups[2].Value, CultureInfo.InvariantCulture) : 0, spec.Success ? int.Parse(spec.Groups[1].Value, CultureInfo.InvariantCulture) : 0));
        }
        return sinks;
    }
    internal static IReadOnlyList<PulseSinkInput> ParseSinkInputs(ExternalProcessResult result)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException("pactl could not list sink inputs.");
        using var document = JsonDocument.Parse(result.Output); var inputs = new List<PulseSinkInput>();
        foreach (var input in document.RootElement.EnumerateArray())
        {
            var properties = input.TryGetProperty("properties", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            var pid = properties.ValueKind == JsonValueKind.Object ? Int(properties, "application.process.id") : null;
            inputs.Add(new(Int(input, "index") ?? -1, Int(input, "sink") ?? -1, pid, properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(OwnStreamProperty, out _)));
        }
        return inputs;
    }
    // pactl prints numbers as JSON numbers in some fields and as strings in others ("owner_module": "1").
    private static int? Int(JsonElement element, string name) => !element.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number
        : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : null;
    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The parent pid from /proc/[pid]/stat, or null. Our chimes (paplay, pw-play) are direct children.</summary>
    internal static int? ParentProcessId(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat"); var end = stat.LastIndexOf(')');
            var fields = stat[(end + 2)..].Split(' ');
            return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) ? parent : null;
        }
        catch { return null; }
    }
}

internal sealed record PulseSink(int Index, string Name, int OwnerModule, int Rate, int Channels);
internal sealed record PulseSinkInput(int Index, int Sink, int? ProcessId, bool OwnMarker);

/// <summary>One live muffle sink: its children, the audio pump and the streams it moved. Sessions hand it over by generation.</summary>
internal sealed class PulseMuffleRun
{
    private static readonly Regex NewSinkInput = new(@"^Event 'new' on sink-input #(\d+)", RegexOptions.CultureInvariant);
    private readonly PulseAudioMuffler _owner; private readonly int _module; private readonly PulseSink _real;
    private readonly object _gate = new(); private readonly Dictionary<int, string> _moved = []; private readonly SemaphoreSlim _teardown = new(1, 1);
    private readonly TaskCompletionSource _audio = new(TaskCreationOptions.RunContinuationsAsynchronously); private readonly CancellationTokenSource _activation = new();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _pump; private Task? _watch; private Task? _activate; private int _generation = 1;
    // _stopping: streams are going (or went) back, because of a teardown or a dead helper; such a run is never claimed again.
    // _watching: the watcher moves new streams; set just before the activation's scan, so between them they see every stream.
    private bool _teardownStarted; private bool _stopping; private bool _unloaded; private bool _released; private bool _active; private bool _watching;
    internal PulseMuffleRun(PulseAudioMuffler owner, int module, PulseSink real) { _owner = owner; _module = module; _real = real; Filter = new(real.Rate, real.Channels); }
    public MuffleFilter Filter { get; }
    public int MuffleSinkIndex { get; set; } = -1;
    public string RealSink => _real.Name;
    public IChildProcess? Capture { get; set; }
    public IChildProcess? Playback { get; set; }
    public IChildProcess? Subscription { get; set; }
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyCollection<int> Moved { get { lock (_gate) return _moved.Keys.ToArray(); } }
    public int Generation { get { lock (_gate) return _generation; } }
    /// <summary>Completes true once the streams were moved and the filter engaged, false when the sink never carried audio.</summary>
    public Task<bool> Activated { get; private set; } = Task.FromResult(false);

    /// <summary>A new recording during the previous one's restore keeps this sink and glides back down, as macOS does.</summary>
    public bool TryClaim(out int generation)
    {
        lock (_gate)
        {
            // A run whose sink never carried audio, or whose parec/pacat died, is not worth keeping: the caller replaces it.
            generation = _teardownStarted || _stopping || Activated is { IsCompleted: true, Result: false } ? 0 : ++_generation;
            if (generation == 0) return false;
            _released = false; if (_active) Filter.Engage();
            return true;
        }
    }
    /// <summary>Glides back open, and keeps a late activation from engaging again, if <paramref name="generation"/> still holds
    /// the run. The check and the release are one step: a recording that claims the run in between keeps its filter engaged.</summary>
    public bool TryRelease(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;
            _released = true; Filter.Release(); return true;
        }
    }
    public bool TryBeginTeardown(int generation) { lock (_gate) { if (generation != _generation) return false; _teardownStarted = true; return true; } }

    public bool TryMove(int index)
    {
        lock (_gate) { if (_stopping || _moved.ContainsKey(index)) return false; }
        var moved = _owner.Pactl(["move-sink-input", PulseAudioMuffler.Number(index), PulseAudioMuffler.SinkName]).ExitCode == 0;
        // A stream that refuses to move (DONT_MOVE) just keeps playing unmuffled.
        if (!moved) return false;
        // The move ran outside the lock. Either it is recorded before _stopping is set, so MoveBackAsync (which sets
        // _stopping and then copies _moved) sees it, or a move-back already started without it and this puts it back.
        lock (_gate) { if (!_stopping) { _moved[index] = _real.Name; return true; } }
        // If that fails too, the restore's own MoveBackAsync retries it (and the unload rescues it in the end).
        if (_owner.Pactl(["move-sink-input", PulseAudioMuffler.Number(index), _real.Name]).ExitCode != 0) lock (_gate) _moved[index] = _real.Name;
        return false;
    }

    public void StartPump()
    {
        var output = Capture?.StandardOutput ?? throw new InvalidOperationException("parec has no output.");
        var input = Playback?.StandardInput ?? throw new InvalidOperationException("pacat has no input.");
        _pump = new Thread(() => Pump(output, input)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "HyperWhisper muffle" };
        _pump.Start();
    }

    // The audio path: whole float32 frames from parec, filtered in place, to pacat. Nothing here allocates.
    private void Pump(Stream output, Stream input)
    {
        var frameBytes = sizeof(float) * Filter.Channels; var buffer = new byte[frameBytes * 2048]; var carry = 0;
        try
        {
            while (true)
            {
                var read = output.Read(buffer, carry, buffer.Length - carry); if (read <= 0) break;
                _audio.TrySetResult();
                var total = carry + read; var whole = total - total % frameBytes;
                if (whole > 0)
                {
                    Filter.Process(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole)));
                    input.Write(buffer, 0, whole); input.Flush();
                }
                carry = total - whole; if (carry > 0) Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
            }
        }
        catch { }
        // A helper that died mid-recording would leave the moved streams silent: put them back at once. Marking the run as
        // stopping here, on this thread, keeps a recording that starts right now from claiming it.
        bool stopping, active; lock (_gate) { stopping = _stopping; active = _active; _stopping = true; }
        if (stopping) return;
        _owner.Info("a helper exited; moving the streams back");
        // An activation still waiting reports its own failure.
        if (active) try { _owner.ActivationFailed?.Invoke("audio_environment.muffle_helper_exited"); } catch { }
        // ast-grep-ignore: no-discarded-task-run -- the pump thread must not block on pactl; the restore's own MoveBackAsync retries what this best-effort pass misses.
        _ = Task.Run(async () => { try { await MoveBackAsync(CancellationToken.None).ConfigureAwait(false); } catch { } });
    }

    public void StartActivation()
    {
        var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); Activated = activated.Task;
        _activate = Task.Run(async () =>
        {
            var result = false;
            try { result = await ActivateAsync(_activation.Token).ConfigureAwait(false); } catch { }
            activated.TrySetResult(result);
            if (!result && !_activation.IsCancellationRequested)
            {
                _owner.Info("fell back: the sink never carried audio (audio_environment.muffle_no_audio)");
                try { _owner.ActivationFailed?.Invoke("audio_environment.muffle_no_audio"); } catch { }
            }
        });
    }

    // A freshly loaded module-null-sink sends its monitor nothing for about 2 s. A stream moved in that
    // window goes silent, so wait for the first bytes, suspending and resuming the idle sink to start it
    // (measured: data in about 250 ms instead of 2 s).
    private async Task<bool> ActivateAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _owner.ActivationTimeout;
        while (!_audio.Task.IsCompleted)
        {
            var left = deadline - DateTime.UtcNow; if (left <= TimeSpan.Zero) return false;
            try { await _audio.Task.WaitAsync(left < _owner.NudgeInterval ? left : _owner.NudgeInterval, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            if (_audio.Task.IsCompleted || DateTime.UtcNow >= deadline) continue;
            await _owner.PactlAsync(["suspend-sink", PulseAudioMuffler.SinkName, "1"], cancellationToken).ConfigureAwait(false);
            await _owner.PactlAsync(["suspend-sink", PulseAudioMuffler.SinkName, "0"], cancellationToken).ConfigureAwait(false);
        }
        // Any event line proves `pactl subscribe` is connected; the helpers' streams and the nudges make several.
        try { await _subscribed.Task.WaitAsync(_owner.SubscribeReadyTimeout, cancellationToken).ConfigureAwait(false); } catch (TimeoutException) { }
        lock (_gate) { if (_stopping) return false; _watching = true; }
        var moved = 0;
        foreach (var input in await _owner.SinkInputsAsync(cancellationToken).ConfigureAwait(false))
            if (input.Sink == _real.Index && !_owner.IsOwn(input)) { cancellationToken.ThrowIfCancellationRequested(); if (TryMove(input.Index)) moved++; }
        lock (_gate) { _active = true; if (!_released) Filter.Engage(); }
        _owner.Info($"engaged: moved {PulseAudioMuffler.Number(moved)} stream(s)");
        return true;
    }

    public void StartWatching()
    {
        var output = Subscription?.StandardOutput ?? throw new InvalidOperationException("pactl subscribe has no output.");
        _watch = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(output, Encoding.UTF8);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    _subscribed.TrySetResult();
                    var match = NewSinkInput.Match(line); if (!match.Success) continue;
                    // Before the activation's scan, that scan moves whatever exists; moving earlier would silence it.
                    lock (_gate) if (!_watching) continue;
                    var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    try
                    {
                        var input = (await _owner.SinkInputsAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(item => item.Index == index);
                        if (input is not null && input.Sink == _real.Index && !_owner.IsOwn(input) && TryMove(index)) _owner.Info("moved a new stream");
                    }
                    catch { }
                }
            }
            catch { }
        });
    }

    private async Task<int> MoveBackAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _stopping = true;
        var inputs = await _owner.SinkInputsAsync(cancellationToken).ConfigureAwait(false);
        KeyValuePair<int, string>[] moved; lock (_gate) moved = [.. _moved]; var back = 0;
        foreach (var (index, original) in moved)
        {
            var input = inputs.FirstOrDefault(item => item.Index == index);
            // Gone, or moved elsewhere by the user since: not ours to touch.
            if (input is not null && input.Sink == MuffleSinkIndex)
            {
                var result = await _owner.PactlAsync(["move-sink-input", PulseAudioMuffler.Number(index), original], cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0) continue;
                back++;
            }
            lock (_gate) _moved.Remove(index);
        }
        return back;
    }

    /// <summary>Idempotent: a step that failed is retried by the next call; one that succeeded is not repeated.</summary>
    public async Task TeardownAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { _teardownStarted = true; _stopping = true; }
        await _teardown.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await TeardownStepsAsync(cancellationToken).ConfigureAwait(false); } finally { _teardown.Release(); }
    }

    private async Task TeardownStepsAsync(CancellationToken cancellationToken)
    {
        if (_unloaded) return;
        // The activation may be moving streams or starting the watcher: let it stop before anything is undone.
        _activation.Cancel();
        try { if (_activate is not null) await _activate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); } catch (TimeoutException) { }
        await StopAsync(Subscription, cancellationToken).ConfigureAwait(false); Subscription = null;
        try { if (_watch is not null) await _watch.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); } catch (TimeoutException) { }
        // Best effort per step: a move-back that fails (a pactl timeout listing the streams) must not keep pacat on the real
        // sink or the null sink loaded. The unload below rescues whatever is still on the sink, and the failure is reported
        // after it. Only the caller's own cancel stops the remaining steps.
        var back = 0; Exception? failure = null;
        try { if (MuffleSinkIndex >= 0) back = await MoveBackAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { failure = error; }
        await StopAsync(Playback, cancellationToken).ConfigureAwait(false); Playback = null;
        await StopAsync(Capture, cancellationToken).ConfigureAwait(false); Capture = null;
        _pump?.Join(TimeSpan.FromSeconds(2));
        // Whatever is still on the sink (our moves that failed, streams stream-restore sent there) is rescued to the default by the server.
        var unload = await _owner.PactlAsync(["unload-module", PulseAudioMuffler.Number(_module)], cancellationToken).ConfigureAwait(false);
        if (unload.ExitCode != 0) throw new InvalidOperationException("The muffle sink could not be removed.", failure);
        lock (_gate) _unloaded = true;
        Completion.TrySetResult();
        if (failure is not null)
        {
            _owner.Info("restored: the streams could not all be moved back; the sink was removed and the server rescued them");
            ExceptionDispatchInfo.Throw(failure);
        }
        _owner.Info($"restored: moved {PulseAudioMuffler.Number(back)} stream(s) back, sink removed");
    }

    /// <summary>The null sink is gone and every helper stopped: nothing of this run is live any more.</summary>
    public bool IsTornDown { get { lock (_gate) return _unloaded; } }

    private static async Task StopAsync(IChildProcess? child, CancellationToken cancellationToken)
    {
        if (child is null) return;
        try { using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(2)); await child.TerminateAsync(deadline.Token).ConfigureAwait(false); }
        catch when (!cancellationToken.IsCancellationRequested) { }
        try { await child.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}

/// <summary>What the muffle queue produced for one recording: a run it holds by generation, the duck it fell back to, or nothing.</summary>
internal sealed record MufflePrepared(PulseMuffleRun? Run, int Generation, IAudioEnvironmentSession? Fallback);

/// <summary>A Muffle Audio recording. It is returned at once; the sink is built (or claimed) on the service's muffle queue, so
/// the recording never waits for pactl. Its restore waits for that work first, so restore order is the prepare order.</summary>
internal sealed class LinuxMuffleSession(LinuxAudioEnvironmentService owner, TimeSpan delay) : DeferredAudioRestore(owner)
{
    private Task<MufflePrepared> _ready = Task.FromResult(new MufflePrepared(null, 0, null));
    /// <summary>Completes once the sink was built or claimed, or the recording fell back; it never faults.</summary>
    public Task<MufflePrepared> Ready => _ready;
    public PulseMuffleRun? Run => _ready.IsCompletedSuccessfully ? _ready.Result.Run : null;
    internal void Begin(Task<MufflePrepared> ready) => _ready = ready;

    protected override async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        var prepared = await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (prepared.Fallback is { } fallback)
        {
            await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            await fallback.RestoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (prepared.Run is not { } run) return;
        var generation = prepared.Generation;
        await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        // A newer recording took the sink over during the delay; it restores it.
        if (!run.TryRelease(generation)) return;
        // A quit skips the glide as well: the sink must be gone before the process is.
        await DelayAsync(MuffleFilter.TeardownDelay, cancellationToken).ConfigureAwait(false);
        if (!run.TryBeginTeardown(generation)) return;
        // Forgotten only once nothing of it is live: a teardown that threw before the unload stays reachable for the retry or the quit.
        try { await run.TeardownAsync(cancellationToken).ConfigureAwait(false); }
        finally { if (run.IsTornDown) Owner!.Forget(run); }
    }
}
