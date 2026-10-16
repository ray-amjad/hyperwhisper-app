using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.Platform.Abstractions.Audio;

// Muffle Audio against a simulated PulseAudio server: the pactl command sequence, which streams move and come back,
// stale-sink recovery, the duck fallback, retry, cancel and hand-over. The real-server check lives outside the repo.
static class MuffleTests
{
    private const int OwnPid = 4242;

    private static (FakePulse Pulse, LinuxAudioEnvironmentService Service) Create(bool tools = true)
    {
        var pulse = new FakePulse();
        pulse.AddSink(0, "speakers", 1, "float32le 2ch 48000Hz"); pulse.AddSink(1, "headphones", 2, "s16le 2ch 44100Hz"); pulse.Default = "speakers";
        pulse.Inputs.Add(new(10, 0, 500, false));   // music player on the default sink
        pulse.Inputs.Add(new(11, 0, OwnPid, false)); // our own in-process playback
        pulse.Inputs.Add(new(12, 0, 900, false));   // our chime: paplay is our child
        pulse.Inputs.Add(new(13, 1, 501, false));   // another app on another sink
        pulse.Inputs.Add(new(14, 0, null, true));   // marked as ours
        pulse.Inputs.Add(new(17, 0, 503, false));   // closes before the restore
        var service = new LinuxAudioEnvironmentService(pulse, "/usr/bin/pactl", pulse, tools ? "/usr/bin/parec" : null, tools ? "/usr/bin/pacat" : null,
            OwnPid, pid => pid == 900 ? OwnPid : 1);
        service.Muffler.NudgeInterval = TimeSpan.FromMilliseconds(20); service.Muffler.ActivationTimeout = TimeSpan.FromMilliseconds(400);
        return (pulse, service);
    }

    // Prepares, then waits for the run to see audio (the fake sink starts on the first suspend/resume, as a real null sink does).
    private static async Task<IAudioEnvironmentSession> Muffled(LinuxAudioEnvironmentService service, TimeSpan delay)
    {
        var session = Prepare(service, delay);
        Assert.True(await (await RunOf(session)).Activated.WaitAsync(TimeSpan.FromSeconds(5)), "the muffle run did not activate");
        return session;
    }

    // PrepareForRecording returns at once; the sink is built on the service's muffle queue.
    private static LinuxMuffleSession Prepare(LinuxAudioEnvironmentService service, TimeSpan delay)
    {
        var prepared = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, delay);
        Assert.True(prepared.IsSuccess, prepared.Error?.Code);
        return (LinuxMuffleSession)prepared.Value!;
    }

    private static async Task<PulseMuffleRun> RunOf(LinuxMuffleSession session) =>
        (await session.Ready.WaitAsync(TimeSpan.FromSeconds(5))).Run ?? throw new InvalidOperationException("the muffle did not start");

    private static Task Ready(IAudioEnvironmentSession session) => ((LinuxMuffleSession)session).Ready.WaitAsync(TimeSpan.FromSeconds(5));

    public static async Task MovesOtherStreamsAndRestoresOnlyThose()
    {
        var (pulse, service) = Create();
        var session = await Muffled(service, TimeSpan.Zero);
        var load = pulse.Calls.Single(call => call[0] == "load-module");
        Assert.Equal("load-module,module-null-sink,sink_name=hw_muffle,rate=48000,channels=2,sink_properties='device.description=\"HyperWhisper Muffle\"'", string.Join(',', load));
        var parec = pulse.Started.Single(request => request.ExecutablePath == "/usr/bin/parec");
        Assert.True(parec.Arguments.Contains("--device=hw_muffle.monitor") && parec.Arguments.Contains("--format=float32le") && parec.Arguments.Contains("--rate=48000")
            && parec.Arguments.Contains("--channels=2") && parec.Arguments.Contains("--latency-msec=20"), string.Join(' ', parec.Arguments));
        var pacat = pulse.Started.Single(request => request.ExecutablePath == "/usr/bin/pacat");
        Assert.True(pacat.Arguments.Contains("--device=speakers") && pacat.Arguments.Contains("--property=hyperwhisper.muffle=1"), string.Join(' ', pacat.Arguments));
        // pactl subscribe runs under a shell that ends it when our end of its stdin closes (a kill -9 of the app).
        var subscribe = pulse.Started.Single(request => request.ExecutablePath == "/bin/sh");
        Assert.True(subscribe.Arguments.Skip(3).SequenceEqual(["/usr/bin/pactl", "subscribe"]) && subscribe.RedirectStandardInput
            && subscribe.Environment["LC_ALL"] == "C", "pactl subscribe is not locale-pinned or not tied to the app: " + string.Join(' ', subscribe.Arguments));
        Assert.Equal("10,17", string.Join(',', pulse.MovedTo("hw_muffle")));
        // Nothing moves into the new sink before it carries audio: a fresh null sink is silent for about 2 s until nudged.
        var nudge = pulse.Calls.FindIndex(call => string.Join(' ', call) == "suspend-sink hw_muffle 0");
        Assert.True(nudge > 0 && nudge < pulse.Calls.FindIndex(call => call[0] == "move-sink-input"), "streams moved before the sink carried audio");
        Assert.Equal(MuffleFilter.MuffledCutoffHz, ((LinuxMuffleSession)session).Run!.Filter.TargetCutoffHz);
        Assert.True(pulse.Calls.All(call => call[0] != "set-default-sink"), "the default sink was touched");

        // A stream that starts mid-recording is moved; a new stream of ours is not.
        pulse.Inputs.Add(new(15, 0, 502, false)); pulse.Inputs.Add(new(16, 0, 901, true)); pulse.Inputs.Add(new(18, 0, 504, false));
        pulse.Subscription!.Emit("Event 'new' on client #40\nEvent 'new' on sink-input #15\nEvent 'new' on sink-input #16\nEvent 'change' on sink-input #10\nEvent 'new' on sink-input #18\n");
        await pulse.WaitUntil(() => pulse.SinkOf(15) == pulse.MuffleIndex && pulse.SinkOf(18) == pulse.MuffleIndex, "the new streams were not moved");
        Assert.Equal(0, pulse.SinkOf(16));

        pulse.Inputs.RemoveAll(input => input.Index == 17);         // the app closed
        pulse.Move(18, "headphones");                               // the user moved it themselves
        var calls = pulse.Calls.Count;
        await session.RestoreAsync();
        var restore = pulse.Calls.Skip(calls).Where(call => call[0] is "move-sink-input" or "unload-module").Select(call => string.Join(' ', call)).ToArray();
        Assert.Equal("move-sink-input 10 speakers|move-sink-input 15 speakers|unload-module 3", string.Join('|', restore));
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0 && pulse.SinkOf(13) == 1 && pulse.SinkOf(18) == 1, "restore left the wrong routing");
        Assert.True(pulse.Children.All(child => child.Terminated), "a helper process was left running");
        calls = pulse.Calls.Count; await session.RestoreAsync(); await session.DisposeAsync();
        Assert.Equal(calls, pulse.Calls.Count);
    }

    public static async Task PumpFiltersTheMonitorIntoTheRealSink()
    {
        var (pulse, service) = Create();
        var session = await Muffled(service, TimeSpan.Zero);
        const int rate = 48_000; var samples = new float[rate * 2];
        for (var f = 0; f < rate; f++) samples[f * 2] = samples[f * 2 + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 100 * f / rate) + 0.3 * Math.Sin(2 * Math.PI * 3_000 * f / rate));
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        // Odd-sized writes split frames across reads; the pump must re-align them.
        for (var offset = 0; offset < bytes.Length; offset += 1_237) pulse.Capture!.Write(bytes.AsSpan(offset, Math.Min(1_237, bytes.Length - offset)));
        // The silent frame the fake sink sent on its nudge comes first; zeros leave the filter state at zero.
        await pulse.WaitUntil(() => pulse.Playback!.Received >= bytes.Length + 8, "pacat did not receive the filtered audio");
        var output = MemoryMarshal.Cast<byte, float>(pulse.Playback!.Bytes()).ToArray()[2..];
        Assert.True(output.Length == samples.Length, $"pacat got {output.Length} samples");
        double Level(float[] buffer, double hz)
        {
            double re = 0, im = 0; var start = rate / 2; var count = rate / 2;
            for (var i = 0; i < count; i++) { var phase = 2 * Math.PI * hz * (start + i) / rate; re += buffer[(start + i) * 2] * Math.Cos(phase); im += buffer[(start + i) * 2] * Math.Sin(phase); }
            return 2 * Math.Sqrt(re * re + im * im) / count;
        }
        var high = 20 * Math.Log10(Level(output, 3_000) / Level(samples, 3_000)); var low = 20 * Math.Log10(Level(output, 100) / Level(samples, 100));
        Assert.True(high < -40 && Math.Abs(low) < 1, $"3 kHz {high:0.0} dB, 100 Hz {low:0.00} dB");
        await session.RestoreAsync();
    }

    public static async Task StaleSinkIsRecovered()
    {
        var (pulse, service) = Create();
        pulse.AddSink(5, "hw_muffle", 7, "float32le 2ch 48000Hz"); pulse.Default = "hw_muffle";
        pulse.Inputs.Add(new(20, 5, 600, false));
        Assert.Equal(1, await service.RecoverStaleMuffleAsync());
        var steps = pulse.Calls.Where(call => call[0] is "move-sink-input" or "unload-module").Select(call => string.Join(' ', call));
        // The default had fallen to the stray sink, so its streams go to the first real one.
        Assert.Equal("move-sink-input 20 speakers|unload-module 7", string.Join('|', steps));
        Assert.Equal(0, await service.RecoverStaleMuffleAsync());
        Assert.Equal(0, pulse.SinkOf(20));

        // PrepareForRecording recovers too, before it loads its own sink.
        (pulse, service) = Create(); pulse.AddSink(5, "hw_muffle", 7, "float32le 2ch 48000Hz");
        var prepared = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, TimeSpan.Zero);
        Assert.True(prepared.IsSuccess, prepared.Error?.Code);
        await Ready(prepared.Value!);
        var unload = pulse.Calls.FindIndex(call => call[0] == "unload-module"); var load = pulse.Calls.FindIndex(call => call[0] == "load-module");
        Assert.True(unload >= 0 && unload < load && pulse.Sinks.Count(sink => sink.Name == "hw_muffle") == 1, "the stale sink was not removed first");
        await prepared.Value!.RestoreAsync();
        Assert.Equal(0, await new LinuxAudioEnvironmentService(new FakeDesktopCommandRunner(), null).RecoverStaleMuffleAsync());
    }

    public static async Task FallsBackToDuck()
    {
        var (pulse, service) = Create(); pulse.FailLoad = true; var reasons = new List<string>(); service.MuffleFellBack += (_, code) => reasons.Add(code);
        var prepared = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, TimeSpan.Zero);
        Assert.True(prepared.IsSuccess, prepared.Error?.Code);
        await Ready(prepared.Value!);
        Assert.Equal("audio_environment.muffle_load_failed", string.Join(',', reasons));
        Assert.True(pulse.Calls.Any(call => string.Join(' ', call) == "set-sink-volume @DEFAULT_SINK@ 35% 35%") && pulse.Started.Count == 0, "did not duck");
        await prepared.Value!.RestoreAsync();
        Assert.Equal("set-sink-volume @DEFAULT_SINK@ 80% 80%", string.Join(' ', pulse.Calls[^1]));

        (pulse, service) = Create(tools: false); reasons.Clear(); service.MuffleFellBack += (_, code) => reasons.Add(code);
        prepared = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, TimeSpan.Zero);
        await Ready(prepared.Value!);
        Assert.True(prepared.IsSuccess && reasons.SequenceEqual(["audio_environment.muffle_unavailable"]) && pulse.Calls.All(call => call[0] != "load-module"), "missing parec/pacat did not fall back");

        // A helper that cannot start unloads the sink it just made.
        (pulse, service) = Create(); pulse.FailStart = "/usr/bin/pacat"; reasons.Clear(); service.MuffleFellBack += (_, code) => reasons.Add(code);
        prepared = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, TimeSpan.Zero);
        await Ready(prepared.Value!);
        Assert.True(prepared.IsSuccess && reasons.SequenceEqual(["audio_environment.muffle_start_failed"]), string.Join(',', reasons));
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.MovedTo("hw_muffle").Count == 0 && pulse.Children.All(child => child.Terminated), "a failed start left state behind");
    }

    public static async Task RestoreRetriesAndCancels()
    {
        var (pulse, service) = Create();
        var session = await Muffled(service, TimeSpan.FromMilliseconds(50));
        using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => session.RestoreAsync(cancelled.Token).AsTask()); }
        Assert.True(pulse.Sinks.Any(sink => sink.Name == "hw_muffle") && pulse.SinkOf(10) == pulse.MuffleIndex, "a cancelled restore tore down");
        pulse.FailUnload = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RestoreAsync().AsTask());
        Assert.Equal(0, pulse.SinkOf(10));
        var moves = pulse.Calls.Count(call => call[0] == "move-sink-input");
        await session.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle"), "the retry did not unload");
        Assert.Equal(moves, pulse.Calls.Count(call => call[0] == "move-sink-input"));
    }

    public static async Task NewRecordingTakesOverDuringRestore()
    {
        var (pulse, service) = Create();
        var first = await Muffled(service, TimeSpan.FromMilliseconds(300));
        var restoring = first.RestoreAsync().AsTask();
        var second = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, TimeSpan.Zero);
        Assert.True(second.IsSuccess, second.Error?.Code);
        await Ready(second.Value!);
        await restoring;
        Assert.True(pulse.Calls.Count(call => call[0] == "load-module") == 1 && pulse.Sinks.Any(sink => sink.Name == "hw_muffle") && pulse.SinkOf(10) == pulse.MuffleIndex,
            "the earlier session tore down the sink the new recording uses");
        await second.Value!.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0, "the new recording did not restore");
        var third = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, TimeSpan.Zero);
        await Ready(third.Value!);
        Assert.True(third.IsSuccess && pulse.Calls.Count(call => call[0] == "load-module") == 2, "a recording after teardown did not build a new sink");
        await third.Value!.RestoreAsync();
    }

    public static async Task HelperDeathPutsStreamsBack()
    {
        var (pulse, service) = Create();
        var session = await Muffled(service, TimeSpan.Zero);
        Assert.True(pulse.SinkOf(10) == pulse.MuffleIndex, "the stream was not moved");
        pulse.Capture!.Close(); // parec died
        await pulse.WaitUntil(() => pulse.SinkOf(10) == 0 && pulse.SinkOf(17) == 0, "streams stayed on a dead muffle sink");
        await session.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle"), "the sink was not unloaded");
    }

    public static async Task SilentSinkMovesNothing()
    {
        var (pulse, service) = Create(); pulse.Silent = true; var reasons = new List<string>(); service.MuffleFellBack += (_, code) => reasons.Add(code);
        var prepared = Prepare(service, TimeSpan.Zero);
        Assert.True(!await (await RunOf(prepared)).Activated.WaitAsync(TimeSpan.FromSeconds(5)), "a silent sink activated");
        await pulse.WaitUntil(() => reasons.Count == 1, "the silent sink was not reported");
        // The watcher is up from the start, but a run that never activated moves nothing it reports either.
        pulse.Subscription!.Emit("Event 'new' on sink-input #10\n"); await Task.Delay(100);
        Assert.True(reasons[0] == "audio_environment.muffle_no_audio" && pulse.MovedTo("hw_muffle").Count == 0, "a silent sink took streams");
        // The next recording does not inherit the dead run: it builds a new sink.
        pulse.Silent = false;
        var next = await Muffled(service, TimeSpan.Zero);
        Assert.True(pulse.Calls.Count(call => call[0] == "load-module") == 2 && pulse.SinkOf(10) == pulse.MuffleIndex, "the dead run was reused");
        await prepared.RestoreAsync(); await next.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0, "restore left the wrong routing");
    }

    public static async Task RestoreBeforeAudioArrives()
    {
        // Stopped before the sink ever carried audio: nothing moved, nothing reported, the sink is gone.
        var (pulse, service) = Create(); pulse.Silent = true; var reasons = new List<string>(); service.MuffleFellBack += (_, code) => reasons.Add(code);
        service.Muffler.ActivationTimeout = TimeSpan.FromSeconds(30);
        var prepared = service.PrepareForRecording(AudioEnvironmentPolicy.MuffleOtherAudio, TimeSpan.Zero);
        await prepared.Value!.RestoreAsync();
        Assert.True(pulse.MovedTo("hw_muffle").Count == 0 && pulse.Sinks.All(sink => sink.Name != "hw_muffle") && reasons.Count == 0, "an early restore left state behind");

        // Audio that arrives after the stop began moves the streams but never engages the filter again.
        (pulse, service) = Create(); pulse.Silent = true; service.Muffler.ActivationTimeout = TimeSpan.FromSeconds(30);
        var session = Prepare(service, TimeSpan.Zero); var run = await RunOf(session);
        Assert.True(run.TryRelease(run.Generation), "the current generation could not release"); pulse.Capture!.Write(new byte[8]);
        Assert.True(await run.Activated.WaitAsync(TimeSpan.FromSeconds(5)), "the run did not activate");
        Assert.Equal(run.Filter.MaxCutoffHz, run.Filter.TargetCutoffHz);
        await session.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0, "restore left the wrong routing");
    }

    // Review round 1, #1: a quit while a stop's restore waits out its 500 ms delay (or glides) must remove the sink now.
    public static async Task QuitFlushesAPendingRestore()
    {
        var (pulse, service) = Create();
        var session = await Muffled(service, TimeSpan.FromSeconds(30));
        var restoring = session.RestoreAsync().AsTask();   // the stop took the session: the recording holds nothing now
        Assert.True(service.HasPendingRestore && !restoring.IsCompleted, "the delayed restore is not pending");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await service.FlushRestoresAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the quit waited {clock.ElapsedMilliseconds} ms");
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0 && pulse.SinkOf(17) == 0, "the quit left streams on hw_muffle");
        await restoring.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(!service.HasPendingRestore && pulse.Children.All(child => child.Terminated), "the flushed restore left state behind");

        // In the glide (past the delay, before the teardown), and a recording still live, are flushed the same way.
        (pulse, service) = Create();
        session = await Muffled(service, TimeSpan.Zero);
        restoring = session.RestoreAsync().AsTask(); await Task.Delay(50);
        await service.FlushRestoresAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0, "the quit during the glide left the sink");
        await restoring.WaitAsync(TimeSpan.FromSeconds(2));
        (pulse, service) = Create();
        await Muffled(service, TimeSpan.FromSeconds(30));
        await service.FlushRestoresAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && !service.HasPendingRestore, "the quit left a live recording's sink");

        // Duck waits out the same delay; the quit puts the volume back at once.
        (pulse, service) = Create();
        var duck = service.PrepareForRecording(AudioEnvironmentPolicy.DuckOtherAudio, TimeSpan.FromSeconds(30)).Value!;
        restoring = duck.RestoreAsync().AsTask();
        await service.FlushRestoresAsync();
        Assert.Equal("set-sink-volume @DEFAULT_SINK@ 80% 80%", string.Join(' ', pulse.Calls[^1]));
        await restoring.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(!service.HasPendingRestore, "the duck restore stayed pending");
    }

    // Review round 1, #2: a run whose parec/pacat died is not claimed by the next recording; that one builds a fresh sink.
    public static async Task DeadRunIsNotClaimed()
    {
        var (pulse, service) = Create(); var reasons = new List<string>(); service.MuffleFellBack += (_, code) => reasons.Add(code);
        var first = await Muffled(service, TimeSpan.FromMilliseconds(300));
        var dead = ((LinuxMuffleSession)first).Run!;
        pulse.Capture!.Close(); // parec died
        await pulse.WaitUntil(() => pulse.SinkOf(10) == 0, "streams stayed on a dead muffle sink");
        Assert.Equal("audio_environment.muffle_helper_exited", string.Join(',', reasons));
        var restoring = first.RestoreAsync().AsTask();
        var second = Prepare(service, TimeSpan.Zero); var run = await RunOf(second);
        Assert.True(!ReferenceEquals(run, dead) && pulse.Calls.Count(call => call[0] == "load-module") == 2, "the new recording claimed the dead run");
        Assert.True(await run.Activated.WaitAsync(TimeSpan.FromSeconds(5)) && pulse.SinkOf(10) == pulse.MuffleIndex, "the new recording muffled nothing");
        await restoring;
        Assert.True(pulse.SinkOf(10) == pulse.MuffleIndex && pulse.Sinks.Count(sink => sink.Name == "hw_muffle") == 1, "the dead run's restore undid the new sink");
        await second.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0, "restore left the wrong routing");
    }

    // Review round 1, #3: PrepareForRecording never waits for pactl; a restore issued before the sink exists waits for it.
    public static async Task PrepareDoesNotBlockTheCaller()
    {
        var (pulse, service) = Create(); using var hold = new ManualResetEventSlim(false); pulse.HoldLoad = hold;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var session = Prepare(service, TimeSpan.Zero);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200) && !session.Ready.IsCompleted, $"prepare blocked for {clock.ElapsedMilliseconds} ms");
        var restoring = session.RestoreAsync().AsTask();
        await Task.Delay(50);
        Assert.True(!restoring.IsCompleted, "the restore did not wait for the prepare");
        hold.Set();
        await restoring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pulse.Calls.Count(call => call[0] == "load-module") == 1 && pulse.Sinks.All(sink => sink.Name != "hw_muffle")
            && pulse.SinkOf(10) == 0 && pulse.Children.All(child => child.Terminated) && !service.HasPendingRestore, "an early restore left the sink behind");

        // Two recordings in a row are prepared in order: the second claims the first's sink.
        (pulse, service) = Create(); using var again = new ManualResetEventSlim(false); pulse.HoldLoad = again;
        var one = Prepare(service, TimeSpan.FromMilliseconds(300)); var two = Prepare(service, TimeSpan.Zero);
        again.Set();
        Assert.True(ReferenceEquals(await RunOf(one), await RunOf(two)), "the queued prepares built two sinks");
        await one.RestoreAsync(); await two.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle"), "the sink stayed");
    }

    // Review round 1, #4: a stream that starts right after the activation's scan is still moved (the subscription is up first).
    public static async Task StreamBetweenScanAndSubscribeIsMoved()
    {
        var (pulse, service) = Create();
        pulse.AfterListInputs = () => pulse.AddInput(new(19, 0, 505, false));
        var session = await Muffled(service, TimeSpan.Zero);
        await pulse.WaitUntil(() => pulse.SinkOf(19) == pulse.MuffleIndex, "a stream that started after the scan stayed unmuffled");
        await session.RestoreAsync();
        Assert.Equal(0, pulse.SinkOf(19));
    }

    // Review round 1, #5: with the real shell, closing our end of its stdin (what a kill -9 of the app does) ends the child.
    public static async Task TiedChildDiesWithTheApp()
    {
        if (!File.Exists(PulseAudioMuffler.Shell) || !File.Exists("/bin/sleep")) return;
        var started = new LinuxChildProcessLauncher().Start(new ChildProcessStartRequest { ExecutablePath = PulseAudioMuffler.Shell,
            Arguments = PulseAudioMuffler.TiedArguments("/bin/sleep", ["600"]), RedirectStandardInput = true, RedirectStandardOutput = true });
        Assert.True(started.IsSuccess, started.Error?.Code);
        await using var shell = started.Value!;
        int? sleeper = null;
        for (var i = 0; i < 200 && sleeper is null; i++)
        {
            sleeper = ChildrenOf(shell.Id).FirstOrDefault(pid => File.ReadAllText($"/proc/{pid}/comm").Trim() == "sleep") is var pid && pid > 0 ? pid : null;
            if (sleeper is null) await Task.Delay(10);
        }
        Assert.True(sleeper is not null, "the tied child did not start");
        shell.StandardInput!.Dispose();
        for (var i = 0; i < 200 && Alive(sleeper!.Value); i++) await Task.Delay(10);
        Assert.True(!Alive(sleeper!.Value), "the tied child outlived the closed pipe");
        // Only the child holds the output pipe, so its death ends the watcher's read.
        Assert.Equal(0, await shell.StandardOutput!.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        // Only /proc/[pid]/task/[tid]/children: a recursive walk follows each task's root symlink into the whole filesystem.
        static IEnumerable<int> ChildrenOf(int pid) => Directory.EnumerateDirectories($"/proc/{pid}/task").Select(task => Path.Combine(task, "children"))
            .Where(File.Exists).SelectMany(file => File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(int.Parse);
        static bool Alive(int pid) => File.Exists($"/proc/{pid}/stat") && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal);
    }

    // Review round 2, #1: a restore whose generation was overtaken by a new claim never opens the new recording's filter,
    // and does not keep a later activation from engaging it.
    public static async Task StaleRestoreDoesNotReleaseANewClaim()
    {
        var (pulse, service) = Create();
        var first = Prepare(service, TimeSpan.Zero); var run = await RunOf(first); var old = (await first.Ready).Generation;
        var second = Prepare(service, TimeSpan.Zero);
        Assert.True(ReferenceEquals(await RunOf(second), run) && run.Generation != old, "the second recording did not claim the run");
        Assert.True(!run.TryRelease(old), "a stale generation released the run");
        Assert.True(await run.Activated.WaitAsync(TimeSpan.FromSeconds(5)), "the run did not activate");
        Assert.Equal(MuffleFilter.MuffledCutoffHz, run.Filter.TargetCutoffHz);
        // The stale restore returns at once and leaves the sink to the recording that holds it.
        await first.RestoreAsync();
        Assert.True(pulse.Sinks.Any(sink => sink.Name == "hw_muffle") && pulse.SinkOf(10) == pulse.MuffleIndex && run.Filter.TargetCutoffHz == MuffleFilter.MuffledCutoffHz,
            "the stale restore undid the new recording's muffle");
        await second.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(10) == 0, "restore left the wrong routing");
    }

    // Review round 2, #2: a helper that dies while a stream is being moved in must not strand that stream on hw_muffle,
    // even though the pump's move-back took its copy of the moved streams before the move was recorded.
    public static async Task HelperDeathDuringAMoveStrandsNothing()
    {
        var (pulse, service) = Create();
        var session = await Muffled(service, TimeSpan.Zero);
        pulse.BeforeMoveToMuffle = index =>
        {
            var before = pulse.CallCount;
            pulse.Capture!.Close(); // parec died while the watcher's move of this stream is in flight
            bool Listed() => pulse.CalledSince(before, call => call.Length > 2 && call[2] == "sink-inputs");
            for (var i = 0; i < 300 && !Listed(); i++) Thread.Sleep(10);
            Thread.Sleep(100); // the pump's move-back has its snapshot and is done with it
        };
        pulse.AddInput(new(15, 0, 502, false));
        await pulse.WaitUntil(() => pulse.SinkOf(10) == 0 && pulse.SinkOf(17) == 0, "streams stayed on a dead muffle sink");
        await pulse.WaitUntil(() => pulse.MovedTo("hw_muffle").Contains(15) && pulse.SinkOf(15) == 0, "a stream moved during the helper's death stayed on hw_muffle");
        await session.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle") && pulse.SinkOf(15) == 0, "restore left the wrong routing");
    }

    // Review round 2, #3: a restore that joins a running one fails when that one fails, and its own token ends only its wait.
    public static async Task JoinedRestoreSharesTheOutcome()
    {
        var restore = new GatedRestore();
        var owning = restore.RestoreAsync().AsTask(); var joined = restore.RestoreAsync().AsTask();
        restore.Finish(new InvalidOperationException("unload failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joined);
        Assert.Equal(1, restore.Runs);

        // A joiner whose token is cancelled stops waiting at once; the owner runs on and succeeds; later calls are no-ops.
        owning = restore.RestoreAsync().AsTask();
        using (var cancelled = new CancellationTokenSource())
        {
            var waiting = restore.RestoreAsync(cancelled.Token).AsTask(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        Assert.True(!owning.IsCompleted, "the joiner's cancel ended the owner");
        restore.Finish(null);
        await owning.WaitAsync(TimeSpan.FromSeconds(2));
        await restore.RestoreAsync(); Assert.Equal(2, restore.Runs);

        // An owner cancelled by its own caller hands the restore to a joiner that was not cancelled.
        restore = new GatedRestore();
        using (var cancelled = new CancellationTokenSource())
        {
            owning = restore.RestoreAsync(cancelled.Token).AsTask(); joined = restore.RestoreAsync().AsTask();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => owning.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        await restore.WaitForRuns(2);
        restore.Finish(null);
        await joined.WaitAsync(TimeSpan.FromSeconds(2));

        // End to end: the stop's restore fails to unload the sink, and a DisposeAsync that joined it fails as well.
        var (pulse, service) = Create();
        var session = await Muffled(service, TimeSpan.FromMilliseconds(100));
        pulse.FailUnload = 1;
        var stopping = session.RestoreAsync().AsTask(); var disposing = session.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopping);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disposing);
        Assert.True(pulse.Sinks.Any(sink => sink.Name == "hw_muffle"), "the sink is gone, so the failure was not simulated");
        await session.RestoreAsync();
        Assert.True(pulse.Sinks.All(sink => sink.Name != "hw_muffle"), "the retry did not unload");
    }

    /// <summary>A restore whose core waits until the test finishes it, with an error or without one.</summary>
    private sealed class GatedRestore() : DeferredAudioRestore(null)
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously); private int _runs;
        public int Runs => Volatile.Read(ref _runs);
        public void Finish(Exception? error) { var gate = _gate; _gate = new(TaskCreationOptions.RunContinuationsAsynchronously); if (error is null) gate.TrySetResult(); else gate.TrySetException(error); }
        public async Task WaitForRuns(int count) { for (var i = 0; i < 200 && Runs < count; i++) await Task.Delay(10); Assert.Equal(count, Runs); }
        protected override async Task RestoreCoreAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static Task ParsesPactlJson()
    {
        var sinks = PulseAudioMuffler.ParseSinks(new(0, """[{"index":3,"name":"alsa_output.pci","owner_module":"12","sample_specification":"s32le 6ch 96000Hz"}]"""u8.ToArray()));
        Assert.Equal(new PulseSink(3, "alsa_output.pci", 12, 96_000, 6), sinks.Single());
        var inputs = PulseAudioMuffler.ParseSinkInputs(new(0, """[{"index":7,"sink":3,"properties":{"application.process.id":"321"}},{"index":8,"sink":3,"properties":{"hyperwhisper.muffle":"1"}},{"index":9,"sink":3}]"""u8.ToArray()));
        Assert.Equal("7:3:321:False|8:3::True|9:3::False", string.Join('|', inputs.Select(input => $"{input.Index}:{input.Sink}:{input.ProcessId}:{input.OwnMarker}")));
        Assert.True(PulseAudioMuffler.ParentProcessId(Environment.ProcessId) is > 0, "the parent pid of this process was not read");
        Assert.Equal(null, PulseAudioMuffler.ParentProcessId(int.MaxValue));
        return Task.CompletedTask;
    }
}

/// <summary>A tiny in-memory PulseAudio: sinks, sink inputs and modules, driven through the pactl commands Muffle Audio uses.</summary>
sealed class FakePulse : IDesktopCommandRunner, IChildProcessLauncher
{
    private readonly object _gate = new(); private int _nextModule = 3; private int _nextSink = 20;
    public List<(int Index, string Name, int Module, string Spec)> Sinks { get; } = [];
    public List<FakeInput> Inputs { get; } = [];
    public string Default { get; set; } = "";
    public bool FailLoad { get; set; }
    /// <summary>The muffle sink never sends its monitor any audio, even when nudged.</summary>
    public bool Silent { get; set; }
    public int FailUnload { get; set; }
    public string? FailStart { get; set; }
    public ManualResetEventSlim? HoldLoad { get; set; }
    /// <summary>Runs once, right after the next sink-input listing was taken (a stream that starts just after a scan).</summary>
    public Action? AfterListInputs { get; set; }
    /// <summary>Runs once, on the caller's thread and before the server takes the call, for the next move into hw_muffle.</summary>
    public Action<int>? BeforeMoveToMuffle { get => _beforeMove; set => _beforeMove = value; }
    private Action<int>? _beforeMove;
    /// <summary>The server notifies a subscriber of every new sink input, as a connected `pactl subscribe` prints it.</summary>
    public void AddInput(FakeInput input) { lock (_gate) { Inputs.Add(input); Subscription?.Emit($"Event 'new' on sink-input #{input.Index}\n"); } }
    public List<string[]> Calls { get; } = [];
    public int CallCount { get { lock (_gate) return Calls.Count; } }
    public bool CalledSince(int start, Func<string[], bool> match) { lock (_gate) return Calls.Skip(start).Any(match); }
    public List<ChildProcessStartRequest> Started { get; } = [];
    public List<FakePulseChild> Children { get; } = [];
    public FakePulseChild? Capture { get; private set; }
    public FakePulseChild? Playback { get; private set; }
    public FakePulseChild? Subscription { get; private set; }
    private readonly List<(int Input, string Sink)> _moves = [];
    private int _volume = 80;
    public int MuffleIndex { get { lock (_gate) return Sinks.FirstOrDefault(sink => sink.Name == "hw_muffle").Index; } }
    public void AddSink(int index, string name, int module, string spec) => Sinks.Add((index, name, module, spec));
    public int SinkOf(int input) { lock (_gate) return Inputs.FirstOrDefault(item => item.Index == input)?.Sink ?? -1; }
    public IReadOnlyList<int> MovedTo(string sink) { lock (_gate) return _moves.Where(move => move.Sink == sink).Select(move => move.Input).ToArray(); }
    public void Move(int input, string sink) { lock (_gate) Inputs.Single(item => item.Index == input).Sink = Sinks.Single(item => item.Name == sink).Index; }
    public async Task WaitUntil(Func<bool> condition, string message)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), message);
    }

    public Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input, CancellationToken cancellationToken, TimeSpan timeout)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A slow server: load-module blocks its caller until the test lets it go.
        if (arguments[0] == "load-module") HoldLoad?.Wait(TimeSpan.FromSeconds(10));
        if (arguments[0] == "move-sink-input" && arguments[2] == "hw_muffle" && Interlocked.Exchange(ref _beforeMove, null) is { } before)
            before(int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture));
        lock (_gate)
        {
            Calls.Add([.. arguments]);
            ExternalProcessResult Ok(string text = "") => new(0, Encoding.UTF8.GetBytes(text)); var fail = new ExternalProcessResult(1, []);
            switch (arguments[0])
            {
                case "get-default-sink": return Task.FromResult(Ok(Default + "\n"));
                case "set-default-sink": Default = arguments[1]; return Task.FromResult(Ok());
                case "get-sink-volume": return Task.FromResult(Ok($"Volume: front-left: 52428 /  {_volume}% / -5.81 dB,   front-right: 52428 /  {_volume}% / -5.81 dB\n"));
                case "set-sink-volume": return Task.FromResult(Ok());
                case "suspend-sink":
                    // A real fresh null sink starts its clock on a resume; here that sends one silent stereo frame.
                    if (arguments[1] == "hw_muffle" && arguments[2] == "0" && !Silent) Capture?.Write(new byte[8]);
                    return Task.FromResult(Ok());
                case "--format=json" when arguments[2] == "sinks":
                    return Task.FromResult(Ok(JsonSerializer.Serialize(Sinks.Select(sink => new Dictionary<string, object> { ["index"] = sink.Index, ["name"] = sink.Name,
                        ["owner_module"] = sink.Module.ToString(System.Globalization.CultureInfo.InvariantCulture), ["sample_specification"] = sink.Spec, ["monitor_source"] = sink.Name + ".monitor" }))));
                case "--format=json" when arguments[2] == "sink-inputs":
                    var listed = Ok(JsonSerializer.Serialize(Inputs.Select(item =>
                    {
                        var properties = new Dictionary<string, string> { ["application.name"] = "app" };
                        if (item.Pid is { } pid) properties["application.process.id"] = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (item.Own) properties["hyperwhisper.muffle"] = "1";
                        return new Dictionary<string, object> { ["index"] = item.Index, ["sink"] = item.Sink, ["properties"] = properties };
                    })));
                    var after = AfterListInputs; AfterListInputs = null; after?.Invoke();
                    return Task.FromResult(listed);
                case "load-module":
                    if (FailLoad) return Task.FromResult(fail);
                    var name = arguments.First(argument => argument.StartsWith("sink_name=", StringComparison.Ordinal))[10..];
                    var rate = arguments.First(argument => argument.StartsWith("rate=", StringComparison.Ordinal))[5..];
                    var channels = arguments.First(argument => argument.StartsWith("channels=", StringComparison.Ordinal))[9..];
                    var module = _nextModule++; Sinks.Add((_nextSink++, name, module, $"float32le {channels}ch {rate}Hz")); return Task.FromResult(Ok($"{module}\n"));
                case "unload-module":
                    if (FailUnload > 0) { FailUnload--; return Task.FromResult(fail); }
                    var unloaded = int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture);
                    var removed = Sinks.Where(sink => sink.Module == unloaded).Select(sink => sink.Index).ToArray();
                    if (removed.Length == 0) return Task.FromResult(fail);
                    Sinks.RemoveAll(sink => sink.Module == unloaded);
                    var rescue = Sinks.FirstOrDefault(sink => sink.Name == Default); if (rescue.Name is null) rescue = Sinks[0];
                    foreach (var item in Inputs.Where(item => removed.Contains(item.Sink))) item.Sink = rescue.Index;
                    return Task.FromResult(Ok());
                case "move-sink-input":
                    var target = Sinks.FirstOrDefault(sink => sink.Name == arguments[2]); var moving = Inputs.FirstOrDefault(item => item.Index == int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture));
                    if (target.Name is null || moving is null) return Task.FromResult(fail);
                    moving.Sink = target.Index; _moves.Add((moving.Index, target.Name)); return Task.FromResult(Ok());
                default: return Task.FromResult(fail);
            }
        }
    }

    public PlatformResult<IChildProcess> Start(ChildProcessStartRequest request)
    {
        lock (_gate)
        {
            Started.Add(request);
            if (request.ExecutablePath == FailStart) return PlatformResult<IChildProcess>.Failure("process.start_failed", "simulated");
            var child = new FakePulseChild(request.RedirectStandardOutput, request.RedirectStandardInput); Children.Add(child);
            if (request.ExecutablePath.EndsWith("parec", StringComparison.Ordinal)) Capture = child;
            else if (request.ExecutablePath.EndsWith("pacat", StringComparison.Ordinal)) Playback = child;
            else Subscription = child;
            // The helpers' own streams are events a connected subscriber sees.
            if (child != Subscription) Subscription?.Emit($"Event 'new' on source-output #{Children.Count}\n");
            return PlatformResult<IChildProcess>.Success(child);
        }
    }
}

sealed class FakeInput(int index, int sink, int? pid, bool own)
{
    public int Index { get; } = index;
    public int Sink { get; set; } = sink;
    public int? Pid { get; } = pid;
    public bool Own { get; } = own;
}

/// <summary>A child whose stdout is a real pipe the test writes into and whose stdin is captured.</summary>
sealed class FakePulseChild : IChildProcess
{
    private readonly AnonymousPipeServerStream? _writer; private readonly CapturedStream? _input;
    public FakePulseChild(bool output, bool input)
    {
        if (output) { _writer = new(PipeDirection.Out); StandardOutput = new AnonymousPipeClientStream(PipeDirection.In, _writer.ClientSafePipeHandle); }
        if (input) StandardInput = _input = new CapturedStream();
    }
    public int Id => 77;
    public bool Terminated { get; private set; }
    public bool HasExited => Terminated;
    public int? ExitCode => Terminated ? 0 : null;
    public Stream? StandardInput { get; }
    public Stream? StandardOutput { get; }
    public Stream? StandardError => null;
    public long Received => _input?.Length ?? 0;
    public byte[] Bytes() => _input!.ToArray();
    public void Write(ReadOnlySpan<byte> bytes) { _writer!.Write(bytes); _writer.Flush(); }
    public void Emit(string text) => Write(Encoding.UTF8.GetBytes(text));
    public void Close() { try { _writer?.Dispose(); } catch { } }
    public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    public ValueTask TerminateAsync(CancellationToken cancellationToken = default) { Terminated = true; Close(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Terminated = true; Close(); return ValueTask.CompletedTask; }
}

sealed class CapturedStream : Stream
{
    private readonly MemoryStream _bytes = new(); private readonly object _gate = new();
    public override void Write(byte[] buffer, int offset, int count) { lock (_gate) _bytes.Write(buffer, offset, count); }
    public byte[] ToArray() { lock (_gate) return _bytes.ToArray(); }
    public override long Length { get { lock (_gate) return _bytes.Length; } }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
