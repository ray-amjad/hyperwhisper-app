using System.Diagnostics;
using System.Threading.Channels;
using HyperWhisper.LocalInference;
using HyperWhisper.WhisperWorker;
using HyperWhisper.WhisperWorker.Tests;
using Whisper.net.LibraryLoader;
using Whisper.net.Wave;

// Console harness for the Whisper worker (issue #1194). Prints PASS/FAIL per
// check and "x/y tests passed"; exits 1 on any failure.
//
// Optional: HW_WHISPER_TEST_MODEL=<ggml model> adds a real-model check that
// transcribes the onboarding sample through the worker and in process, and
// compares the text. HW_WHISPER_WORKER_EXE overrides where the worker is.

var failures = 0;
var checks = 0;
var fast = new WhisperWorkerClientTimings
{
    IdleTimeout = TimeSpan.FromMilliseconds(300),
    ReadyTimeout = TimeSpan.FromSeconds(2),
    ShutdownTimeout = TimeSpan.FromMilliseconds(500),
    CancelTimeout = TimeSpan.FromMilliseconds(500),
};
var model = new WhisperWorkerModel("/models/ggml-base.bin", GpuDevice: 1, FlashAttention: true, GpuName: "Fake GPU");
var options = new WhisperWorkerTranscribeOptions(SingleSegment: true, MaxLastTextTokensZero: true, Threads: 7);

// =========================================================================
// PROTOCOL
// =========================================================================

await CheckAsync("protocol: every command builds and parses back", () =>
{
    WhisperWorkerCommand[] commands =
    [
        new WhisperWorkerLoadCommand(1, @"C:\Users\Zoë\models\ggml-base.bin", 2, true, "NVIDIA GeForce RTX 4060"),
        new WhisperWorkerLoadCommand(2, "/m.bin", 0, false, null),
        new WhisperWorkerTranscribeCommand(3, @"C:\Temp\a ""b"".wav", null, new WhisperWorkerTranscribeOptions(false, true, 15)),
        new WhisperWorkerTranscribeCommand(4, "/a.wav", "ja", new WhisperWorkerTranscribeOptions(true, false, null)),
        new WhisperWorkerCancelCommand(5, 4),
        new WhisperWorkerShutdownCommand(),
    ];
    foreach (var command in commands)
    {
        var line = WhisperWorkerProtocol.BuildCommand(command);
        Assert(!line.Contains('\n') && !line.Contains('\r'), $"a command line holds a newline: {line}");
        Assert(WhisperWorkerProtocol.TryParseCommand(line, out var parsed, out _, out var error), $"{line} did not parse: {error}");
        Assert(Equals(parsed, command), $"{line} parsed as {parsed}, not {command}");
    }
    return Task.CompletedTask;
});

await CheckAsync("protocol: the wire names are the spec's", () =>
{
    var load = WhisperWorkerProtocol.BuildCommand(new WhisperWorkerLoadCommand(7, "m", 1, true, null));
    Assert(load == "{\"id\":7,\"cmd\":\"load\",\"model\":\"m\",\"gpuDevice\":1,\"flashAttention\":true}", load);
    var transcribe = WhisperWorkerProtocol.BuildCommand(new WhisperWorkerTranscribeCommand(8, "w", null, new(true, true, 3)));
    Assert(transcribe == "{\"id\":8,\"cmd\":\"transcribe\",\"wav\":\"w\",\"language\":null,\"options\":{\"singleSegment\":true,\"maxLastTextTokensZero\":true,\"threads\":3}}", transcribe);
    Assert(WhisperWorkerProtocol.BuildCommand(new WhisperWorkerCancelCommand(9, 8)) == "{\"id\":9,\"cmd\":\"cancel\",\"target\":8}", "cancel");
    Assert(WhisperWorkerProtocol.BuildCommand(new WhisperWorkerShutdownCommand()) == "{\"cmd\":\"shutdown\"}", "shutdown");
    Assert(WhisperWorkerProtocol.BuildReady("vulkan", 42) == "{\"status\":\"ready\",\"runtime\":\"vulkan\",\"pid\":42}", "ready");
    return Task.CompletedTask;
});

await CheckAsync("protocol: bad commands are rejected, with the id when there is one", () =>
{
    Assert(!WhisperWorkerProtocol.TryParseCommand("nope", out _, out var id, out var error) && id is null && error is not null, "non-JSON");
    Assert(!WhisperWorkerProtocol.TryParseCommand("[1]", out _, out _, out _), "array");
    Assert(!WhisperWorkerProtocol.TryParseCommand("{\"id\":4,\"cmd\":\"explode\"}", out _, out id, out error) && id == 4
        && error!.Contains("explode"), "unknown cmd keeps the id");
    Assert(!WhisperWorkerProtocol.TryParseCommand("{\"id\":5,\"cmd\":\"load\"}", out _, out id, out _) && id == 5, "load without a model");
    Assert(!WhisperWorkerProtocol.TryParseCommand("{\"cmd\":\"transcribe\",\"wav\":\"a\"}", out _, out _, out _), "transcribe without an id");
    Assert(!WhisperWorkerProtocol.TryParseCommand("{\"id\":1,\"cmd\":\"cancel\"}", out _, out _, out _), "cancel without a target");
    Assert(!WhisperWorkerProtocol.TryParseCommand("", out _, out _, out _), "empty");
    return Task.CompletedTask;
});

await CheckAsync("protocol: worker messages parse; other stdout lines do not", () =>
{
    Assert(WhisperWorkerProtocol.TryParseMessage(WhisperWorkerProtocol.BuildReady(null, 12), out var ready)
        && ready is WhisperWorkerReady { Runtime: null, Pid: 12 }, "ready");
    Assert(WhisperWorkerProtocol.TryParseMessage(WhisperWorkerProtocol.BuildLoadOk(3, "cpu", null, "Cpu: AVX2"), out var load)
        && load is WhisperWorkerReply { Id: 3, Ok: true, Runtime: "cpu", Gpu: null, Info: "Cpu: AVX2" }, "load ok");
    var spoken = "こんにちは \"quoted\"\nnext line \\ end";
    Assert(WhisperWorkerProtocol.TryParseMessage(WhisperWorkerProtocol.BuildTranscribeOk(4, spoken.Trim(), " " + spoken, "ja", 3), out var text)
        && text is WhisperWorkerReply { Id: 4, Ok: true, Language: "ja", SegmentCount: 3 } reply
        && reply.Text == spoken.Trim() && reply.RawText == " " + spoken, "transcribe ok keeps the text exactly");
    Assert(!WhisperWorkerProtocol.BuildTranscribeOk(4, spoken, spoken, null, 1).Contains('\n'), "a reply line holds a raw newline");
    Assert(WhisperWorkerProtocol.TryParseMessage(WhisperWorkerProtocol.BuildError(5, WhisperWorkerErrorCodes.ModelFile, "bad"), out var error)
        && error is WhisperWorkerReply { Id: 5, Ok: false, Code: "model_file", Message: "bad" }, "error");
    Assert(WhisperWorkerProtocol.TryParseMessage(WhisperWorkerProtocol.BuildError(null, "protocol", "x"), out var noId)
        && noId is WhisperWorkerReply { Id: 0 }, "an error with no id answers id 0");
    foreach (var junk in new[] { "", "whisper_init_from_file: loading model", "{not json", "{\"hello\":1}", "[]", null })
    {
        Assert(!WhisperWorkerProtocol.TryParseMessage(junk, out _), $"'{junk}' parsed as a message");
    }
    return Task.CompletedTask;
});

// =========================================================================
// WAV HANDOVER
// =========================================================================

await CheckAsync("wav: Whisper.net reads back the exact samples the app prepared", async () =>
{
    // What TranscriptionService.ToWhisperInput produces: 16-bit steps over 32768,
    // including the clamp to 32767 at full scale and resampler overshoot.
    float Quantize(float sample) => (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue) / 32768f;
    var random = new Random(1194);
    var samples = new float[16000 * 3 + 17];
    for (int i = 0; i < samples.Length; i++) samples[i] = Quantize((float)(random.NextDouble() * 2.4 - 1.2));
    samples[0] = Quantize(1f);
    samples[1] = Quantize(-1f);
    samples[2] = Quantize(0f);

    using var stream = new MemoryStream();
    WhisperWav.Write(stream, samples);
    Assert(stream.Length == 44 + samples.Length * 2L, $"size {stream.Length}");
    stream.Position = 0;
    var parser = new WaveParser(stream);
    var read = await parser.GetAvgSamplesAsync();
    Assert(parser.SampleRate == 16000 && parser.Channels == 1 && parser.BitsPerSample == 16, "header");
    Assert(read.Length == samples.Length, $"{read.Length} samples read, {samples.Length} written");
    for (int i = 0; i < samples.Length; i++)
    {
        Assert(BitConverter.SingleToInt32Bits(read[i]) == BitConverter.SingleToInt32Bits(samples[i]),
            $"sample {i}: wrote {samples[i]:R}, read {read[i]:R}");
    }
});

await CheckAsync("wav: an empty chunk is still a valid file", async () =>
{
    using var stream = new MemoryStream();
    WhisperWav.Write(stream, ReadOnlySpan<float>.Empty);
    stream.Position = 0;
    var read = await new WaveParser(stream, new WaveParserOptions { AllowLessSamples = true }).GetAvgSamplesAsync();
    Assert(read.Length == 0, "samples in an empty file");
});

// =========================================================================
// CLIENT STATE MACHINE (fake worker)
// =========================================================================

await CheckAsync("client: selecting a model starts nothing (D5)", async () =>
{
    var launcher = new FakeLauncher();
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    await Task.Delay(50);
    Assert(launcher.Processes.Count == 0 && !client.IsRunning, "a worker started at selection");
    Assert(client.Model == model, "selection not recorded");
});

await CheckAsync("client: a transcribe starts the worker, loads once, then reuses it", async () =>
{
    var launcher = new FakeLauncher();
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    var first = await client.TranscribeAsync("/tmp/a.wav", null, options);
    var second = await client.TranscribeAsync("/tmp/b.wav", "en", options);
    Assert(first == new WhisperWorkerTranscript("hello", " hello", "en", 1), $"first {first}");
    Assert(second.Text == "hello", "second");
    Assert(launcher.Processes.Count == 1 && client.StartCount == 1, "more than one worker");
    var commands = launcher[0].Commands.ToArray();
    var loads = commands.OfType<WhisperWorkerLoadCommand>().ToArray();
    Assert(loads.Length == 1, $"{loads.Length} loads");
    Assert(loads[0] is { Model: "/models/ggml-base.bin", GpuDevice: 1, FlashAttention: true, GpuName: "Fake GPU" }, $"load {loads[0]}");
    var transcribes = commands.OfType<WhisperWorkerTranscribeCommand>().ToArray();
    Assert(transcribes.Length == 2 && transcribes[0].Options == options && transcribes[1].Language == "en"
        && transcribes[0].Language is null, "transcribe commands");
    Assert(client.LoadResult is { Runtime: "vulkan", Gpu: "Fake GPU" }, $"load result {client.LoadResult}");
});

await CheckAsync("client: without a selected model a request fails as not_loaded and starts nothing", async () =>
{
    var launcher = new FakeLauncher();
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    var error = await ThrowsAsync<WhisperWorkerException>(() => client.TranscribeAsync("/a.wav", null, options));
    Assert(error.Code == WhisperWorkerErrorCodes.NotLoaded, error.Code);
    Assert(launcher.Processes.Count == 0, "started a worker");
});

await CheckAsync("client: idle shutdown after the last request, then a fresh start and load", async () =>
{
    var launcher = new FakeLauncher();
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    await client.TranscribeAsync("/a.wav", null, options);
    Assert(client.IsRunning, "not running after a request");
    await WaitUntilAsync(() => !client.IsRunning, "the idle shutdown");
    Assert(launcher[0].ShutdownReceived && !launcher[0].Killed, "it was not a clean shutdown");
    Assert(client.IdleShutdownCount == 1, $"idle shutdowns {client.IdleShutdownCount}");
    Assert(client.Model == model, "the idle exit cleared the selection");

    await client.TranscribeAsync("/b.wav", null, options);
    Assert(launcher.Processes.Count == 2, "no new worker after the idle exit");
    Assert(launcher[1].Commands.OfType<WhisperWorkerLoadCommand>().Count() == 1, "the new worker did not load");
});

await CheckAsync("client: the idle timer restarts with each request", async () =>
{
    var launcher = new FakeLauncher();
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMilliseconds(400) });
    await client.SelectModelAsync(model);
    for (int i = 0; i < 4; i++)
    {
        await client.TranscribeAsync("/a.wav", null, options);
        await Task.Delay(200);
    }
    Assert(client.IsRunning && client.IdleShutdownCount == 0, "the timer fired while requests kept coming");
    await WaitUntilAsync(() => !client.IsRunning, "the idle shutdown");
});

await CheckAsync("client: a recording hold keeps the worker warm; releasing it arms the timer", async () =>
{
    var launcher = new FakeLauncher();
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    var hold = client.HoldWarm();
    var warm = await client.WarmUpAsync();
    Assert(warm.Runtime == "vulkan", "warm-up result");
    await Task.Delay(fast.IdleTimeout * 3);
    Assert(client.IsRunning, "the worker idled out under a hold");
    await client.TranscribeAsync("/a.wav", null, options);
    await Task.Delay(fast.IdleTimeout * 2);
    Assert(client.IsRunning, "the worker idled out under a hold after a request");
    hold.Dispose();
    hold.Dispose(); // a second dispose is harmless
    await WaitUntilAsync(() => !client.IsRunning, "the idle shutdown after the hold");
    Assert(launcher.Processes.Count == 1, "warm-up and the request used different workers");
});

await CheckAsync("client: a crash mid-request restarts once and retries", async () =>
{
    var launcher = new FakeLauncher(index => index == 0
        ? new FakeBehavior
        {
            Handle = (process, command) =>
            {
                if (command is WhisperWorkerTranscribeCommand) process.Exit();
                else FakeBehavior.DefaultHandle(process, command);
                return Task.CompletedTask;
            },
        }
        : new FakeBehavior());
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    var result = await client.TranscribeAsync("/a.wav", null, options);
    Assert(result.Text == "hello", "no result after the restart");
    Assert(client.StartCount == 2, $"starts {client.StartCount}");
    Assert(launcher[1].Commands.OfType<WhisperWorkerLoadCommand>().Count() == 1, "the restarted worker did not load");
    Assert(launcher[1].Commands.OfType<WhisperWorkerTranscribeCommand>().Count() == 1, "the request was not retried");
});

await CheckAsync("client: a second crash returns the error", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior
    {
        Handle = (process, command) =>
        {
            if (command is WhisperWorkerTranscribeCommand) process.Exit();
            else FakeBehavior.DefaultHandle(process, command);
            return Task.CompletedTask;
        },
    });
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    var error = await ThrowsAsync<WhisperWorkerException>(() => client.TranscribeAsync("/a.wav", null, options));
    Assert(error.Code == WhisperWorkerErrorCodes.Crashed && !error.IsModelFileFault, error.Code);
    Assert(client.StartCount == 2, $"starts {client.StartCount}");
    // The next request may start again: the failure is not sticky.
    Assert(!client.IsRunning, "a dead worker reads as running");
});

await CheckAsync("client: a worker that dies while idle is replaced on the next request", async () =>
{
    var launcher = new FakeLauncher();
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    await client.TranscribeAsync("/a.wav", null, options);
    launcher[0].Exit();
    await WaitUntilAsync(() => !client.IsRunning, "the exit to be seen");
    await client.TranscribeAsync("/a.wav", null, options);
    Assert(client.StartCount == 2, $"starts {client.StartCount}");
});

await CheckAsync("client: cancel sends cancel with the request id and keeps an answering worker", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior
    {
        Handle = (process, command) =>
        {
            if (command is not WhisperWorkerTranscribeCommand) return FakeBehavior.DefaultHandle(process, command);
            return Task.CompletedTask; // runs until cancelled
        },
        OnCancel = (process, cancel) =>
            process.Send(WhisperWorkerProtocol.BuildError(cancel.Target, WhisperWorkerErrorCodes.Cancelled, "cancelled")),
    });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    await client.WarmUpAsync();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var watch = Stopwatch.StartNew();
    await ThrowsAsync<OperationCanceledException>(() => client.TranscribeAsync("/a.wav", null, options, cts.Token));
    Assert(watch.Elapsed < TimeSpan.FromSeconds(2), $"cancel took {watch.Elapsed}");
    var transcribe = launcher[0].Commands.OfType<WhisperWorkerTranscribeCommand>().Single();
    await WaitUntilAsync(() => launcher[0].Commands.OfType<WhisperWorkerCancelCommand>().Any(), "the cancel command");
    Assert(launcher[0].Commands.OfType<WhisperWorkerCancelCommand>().Single().Target == transcribe.Id, "cancel targets another id");
    Assert(client.IsRunning && !launcher[0].Killed, "an answering worker was killed");
    Assert(client.StartCount == 1, "restarted");
});

await CheckAsync("client: a worker that ignores cancel is killed, and the next request starts fresh", async () =>
{
    var launcher = new FakeLauncher(index => new FakeBehavior
    {
        Handle = (process, command) =>
            index == 0 && command is WhisperWorkerTranscribeCommand
                ? Task.CompletedTask
                : FakeBehavior.DefaultHandle(process, command),
    });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    await ThrowsAsync<OperationCanceledException>(() => client.TranscribeAsync("/a.wav", null, options, cts.Token));
    Assert(launcher[0].Killed, "the silent worker was not killed");
    var result = await client.TranscribeAsync("/a.wav", null, options);
    Assert(result.Text == "hello" && client.StartCount == 2, "no fresh worker");
});

await CheckAsync("client: a cancelled wait for the gate leaves the running request alone", async () =>
{
    var release = new TaskCompletionSource();
    var launcher = new FakeLauncher(_ => new FakeBehavior
    {
        Handle = async (process, command) =>
        {
            if (command is WhisperWorkerTranscribeCommand) await release.Task;
            await FakeBehavior.DefaultHandle(process, command);
        },
    });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    var running = client.TranscribeAsync("/a.wav", null, options);
    await WaitUntilAsync(() => launcher.Processes.Count == 1 && launcher[0].Commands.OfType<WhisperWorkerTranscribeCommand>().Any(), "the first request");
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    await ThrowsAsync<OperationCanceledException>(() => client.TranscribeAsync("/b.wav", null, options, cts.Token));
    release.SetResult();
    Assert((await running).Text == "hello", "the running request failed");
    Assert(!launcher[0].Commands.OfType<WhisperWorkerCancelCommand>().Any(), "a cancel reached the worker");
});

await CheckAsync("client: one request at a time, even when callers race", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior
    {
        Handle = async (process, command) =>
        {
            await Task.Delay(20);
            await FakeBehavior.DefaultHandle(process, command);
        },
    });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    var tasks = Enumerable.Range(0, 12).Select(i => i % 3 == 0
        ? (Task)client.WarmUpAsync()
        : client.TranscribeAsync($"/{i}.wav", null, options)).ToArray();
    await Task.WhenAll(tasks);
    Assert(launcher.Processes.Count == 1, $"{launcher.Processes.Count} workers");
    Assert(!launcher[0].SawOverlap, "two requests ran in the worker at once");
    Assert(launcher[0].Commands.OfType<WhisperWorkerLoadCommand>().Count() == 1, "loaded more than once");
});

await CheckAsync("client: a request during an idle shutdown waits for the exit, then starts a new worker", async () =>
{
    var launcher = new FakeLauncher(index => new FakeBehavior { ShutdownDelay = index == 0 ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMilliseconds(100), ShutdownTimeout = TimeSpan.FromSeconds(2) });
    await client.SelectModelAsync(model);
    await client.TranscribeAsync("/a.wav", null, options);
    await WaitUntilAsync(() => launcher[0].ShutdownReceived, "the idle shutdown to start");
    var result = await client.TranscribeAsync("/b.wav", null, options);
    Assert(result.Text == "hello", "the request failed");
    Assert(launcher.Processes.Count == 2, $"{launcher.Processes.Count} workers");
    Assert(launcher.PeakAlive == 1, $"{launcher.PeakAlive} workers were alive at once");
    Assert(!launcher[0].Killed, "the exiting worker was killed");
});

await CheckAsync("client: a worker that does not exit on shutdown is killed after the timeout", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior { IgnoreShutdown = true });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { ShutdownTimeout = TimeSpan.FromMilliseconds(200) });
    await client.SelectModelAsync(model);
    await client.WarmUpAsync();
    var watch = Stopwatch.StartNew();
    await client.StopAsync();
    Assert(launcher[0].ShutdownReceived && launcher[0].Killed, "not shut down then killed");
    Assert(watch.Elapsed < TimeSpan.FromSeconds(2), $"stop took {watch.Elapsed}");
    Assert(!client.IsRunning && client.Model == model, "stop changed the selection");
});

await CheckAsync("client: switching models stops the old worker before the new one loads", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior { ShutdownDelay = TimeSpan.FromMilliseconds(100) });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    await client.WarmUpAsync();
    await client.SelectModelAsync(model); // same model: nothing happens
    Assert(client.IsRunning && launcher.Processes.Count == 1, "re-selecting the same model restarted");
    var other = model with { Path = "/models/ggml-small.bin" };
    await client.SelectModelAsync(other);
    Assert(!client.IsRunning && launcher[0].HasExited, "the old worker survived the switch");
    await client.TranscribeAsync("/a.wav", null, options);
    Assert(launcher.PeakAlive == 1, "two workers held a model at once");
    Assert(launcher[1].Commands.OfType<WhisperWorkerLoadCommand>().Single().Model == other.Path, "loaded the wrong model");
    await client.SelectModelAsync(null);
    Assert(!client.IsRunning && client.Model is null, "clearing the model left a worker");
});

await CheckAsync("client: model_file is a model fault; the worker stays", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior
    {
        Handle = (process, command) =>
        {
            if (command is WhisperWorkerLoadCommand load)
                process.Send(WhisperWorkerProtocol.BuildError(load.Id, WhisperWorkerErrorCodes.ModelFile, "The Whisper model could not be loaded by the selected runtime."));
            return Task.CompletedTask;
        },
    });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1) });
    await client.SelectModelAsync(model);
    var error = await ThrowsAsync<WhisperWorkerException>(() => client.TranscribeAsync("/a.wav", null, options));
    Assert(error.IsModelFileFault && error.Code == "model_file", error.Code);
    Assert(client.IsRunning && client.StartCount == 1, "a model fault restarted or stopped the worker");
});

await CheckAsync("client: runtime is not a model fault, and the poisoned worker is stopped", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior
    {
        Handle = (process, command) =>
        {
            if (command is WhisperWorkerLoadCommand load)
                process.Send(WhisperWorkerProtocol.BuildError(load.Id, WhisperWorkerErrorCodes.Runtime, "no runtime"));
            return Task.CompletedTask;
        },
    });
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    var error = await ThrowsAsync<WhisperWorkerException>(() => client.WarmUpAsync());
    Assert(!error.IsModelFileFault && error.Code == "runtime", error.Code);
    Assert(!client.IsRunning, "a worker whose runtime failed kept running");
});

await CheckAsync("client: no ready line times out, retries once, then fails as timeout", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior { SendReady = false });
    await using var client = new WhisperWorkerClient(launcher, timings: fast with { ReadyTimeout = TimeSpan.FromMilliseconds(150) });
    await client.SelectModelAsync(model);
    var error = await ThrowsAsync<WhisperWorkerException>(() => client.WarmUpAsync());
    Assert(error.Code == WhisperWorkerErrorCodes.Timeout, error.Code);
    Assert(client.StartCount == 2 && launcher.Processes.All(p => p.HasExited), "not two killed starts");
});

await CheckAsync("client: a missing worker exe fails as start_failed without a retry", async () =>
{
    var launcher = new FakeLauncher { FailStartWith = new WhisperWorkerException(WhisperWorkerErrorCodes.StartFailed, "missing") };
    await using var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    var error = await ThrowsAsync<WhisperWorkerException>(() => client.WarmUpAsync());
    Assert(error.Code == WhisperWorkerErrorCodes.StartFailed, error.Code);

    var real = new WhisperWorkerProcessLauncher(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));
    error = await ThrowsAsync<WhisperWorkerException>(() => Task.FromResult(real.Start()));
    Assert(error.Code == WhisperWorkerErrorCodes.StartFailed, error.Code);
});

await CheckAsync("client: stale replies and stray stdout lines are ignored", async () =>
{
    var launcher = new FakeLauncher(_ => new FakeBehavior
    {
        Handle = (process, command) =>
        {
            if (command is WhisperWorkerTranscribeCommand transcribe)
            {
                process.Send("ggml_vulkan: Found 1 Vulkan devices");
                process.Send(WhisperWorkerProtocol.BuildTranscribeOk(transcribe.Id - 100, "stale", "stale", null, 1));
            }
            return FakeBehavior.DefaultHandle(process, command);
        },
    });
    var logs = new List<string>();
    await using var client = new WhisperWorkerClient(launcher, info: line => { lock (logs) logs.Add(line); }, timings: fast);
    await client.SelectModelAsync(model);
    var result = await client.TranscribeAsync("/a.wav", null, options);
    Assert(result.Text == "hello", $"got '{result.Text}'");
    lock (logs)
    {
        Assert(!logs.Any(l => l.Contains("Vulkan devices")), "a stray stdout line went to the log verbatim");
    }
});

await CheckAsync("client: dispose stops the worker", async () =>
{
    var launcher = new FakeLauncher();
    var client = new WhisperWorkerClient(launcher, timings: fast);
    await client.SelectModelAsync(model);
    await client.WarmUpAsync();
    await client.DisposeAsync();
    Assert(launcher[0].HasExited && launcher[0].ShutdownReceived, "the worker outlived the client");
    await ThrowsAsync<ObjectDisposedException>(() => client.WarmUpAsync());
});

// =========================================================================
// WORKER COMMAND LOOP (fake Whisper service)
// =========================================================================

await CheckAsync("worker: ready first, then load with the Windows options", async () =>
{
    var service = new FakeWhisperService();
    await using var worker = WorkerPipe.Start(service, requestThreadFactory: false);
    var ready = await worker.ReadMessageAsync();
    Assert(ready is WhisperWorkerReady { Runtime: null }, $"first line {ready}");
    await worker.SendAsync(new WhisperWorkerLoadCommand(1, "/m.bin", 2, false, "GPU"));
    var reply = (WhisperWorkerReply)(await worker.ReadMessageAsync())!;
    Assert(reply is { Id: 1, Ok: true, Runtime: "cpu", Gpu: null }, $"reply {reply}");
    var load = service.Loads.Single();
    Assert(load.ModelPath == "/m.bin" && load.GpuDevice == 2 && load.UseFlashAttention == false
        && load.VerifyModelOnLoad && load.Backend == LocalWhisperBackend.Vulkan && load.AllowCpuFallback
        && !load.FactoryOnOwnThread, $"options {load}");
    Assert(load.RuntimeOrder!.SequenceEqual([RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]), "runtime order");
});

await CheckAsync("worker: transcribe passes the decoding switches and returns raw and stripped text", async () =>
{
    var service = new FakeWhisperService();
    await using var worker = WorkerPipe.Start(service, requestThreadFactory: true);
    await worker.ReadMessageAsync();
    await worker.SendAsync(new WhisperWorkerTranscribeCommand(1, "/a.wav", null, options));
    var early = (WhisperWorkerReply)(await worker.ReadMessageAsync())!;
    Assert(early is { Id: 1, Ok: false, Code: "not_loaded" }, $"before a load: {early}");
    await worker.SendAsync(new WhisperWorkerLoadCommand(2, "/m.bin", 0, true, null));
    await worker.ReadMessageAsync();
    await worker.SendAsync(new WhisperWorkerTranscribeCommand(3, "/a.wav", "de", new(false, true, 5)));
    var reply = (WhisperWorkerReply)(await worker.ReadMessageAsync())!;
    Assert(reply is { Id: 3, Ok: true, Text: "hello", RawText: " hello [BLANK_AUDIO]", Language: "en", SegmentCount: 2 }, $"reply {reply}");
    var request = service.Requests.Single();
    Assert(request == new LocalWhisperRequest("/a.wav", "de", 5, SingleSegment: false, IncludeWordTimestamps: false,
        MaxLastTextTokensZero: true, CreateFactoryOnRequestThread: true), $"request {request}");
    // ARM64: the load makes the one factory on the rule's thread (one model copy).
    Assert(service.Loads.Single().FactoryOnOwnThread, "the ARM64 load did not use the factory thread");
});

await CheckAsync("worker: failure codes keep the model-file / runtime split", async () =>
{
    var service = new FakeWhisperService();
    await using var worker = WorkerPipe.Start(service, requestThreadFactory: false);
    await worker.ReadMessageAsync();
    var cases = new (LocalWhisperErrorCode Code, string Wire)[]
    {
        (LocalWhisperErrorCode.ModelLoadFailed, "model_file"),
        (LocalWhisperErrorCode.RuntimeUnavailable, "runtime"),
        (LocalWhisperErrorCode.InvalidRequest, "invalid_request"),
        (LocalWhisperErrorCode.Cancelled, "cancelled"),
        (LocalWhisperErrorCode.TranscriptionFailed, "transcription"),
    };
    long id = 0;
    foreach (var (code, wire) in cases)
    {
        service.LoadResult = _ => LocalWhisperResult.Failed(code, "message");
        await worker.SendAsync(new WhisperWorkerLoadCommand(++id, "/m.bin", 0, true, null));
        var reply = (WhisperWorkerReply)(await worker.ReadMessageAsync())!;
        Assert(reply is { Ok: false } && reply.Id == id && reply.Code == wire, $"{code} became {reply.Code}");
    }
});

await CheckAsync("worker: cancel stops the running transcription; garbage gets a protocol error", async () =>
{
    var service = new FakeWhisperService
    {
        TranscribeResult = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return LocalWhisperResult.Failed(LocalWhisperErrorCode.Cancelled, "Transcription was cancelled.");
        },
    };
    await using var worker = WorkerPipe.Start(service, requestThreadFactory: false);
    await worker.ReadMessageAsync();
    await worker.SendAsync(new WhisperWorkerLoadCommand(1, "/m.bin", 0, true, null));
    await worker.ReadMessageAsync();
    await worker.SendAsync(new WhisperWorkerTranscribeCommand(2, "/a.wav", null, options));
    await WaitUntilAsync(() => service.Requests.Count == 1, "the transcription to start");
    await worker.SendLineAsync("this is not json");
    var protocol = (WhisperWorkerReply)(await worker.ReadMessageAsync())!;
    Assert(protocol is { Ok: false, Code: "protocol" }, $"garbage got {protocol}");
    await worker.SendAsync(new WhisperWorkerCancelCommand(3, 2));
    var reply = (WhisperWorkerReply)(await worker.ReadMessageAsync())!;
    Assert(reply is { Id: 2, Ok: false, Code: "cancelled" }, $"reply {reply}");
});

await CheckAsync("worker: shutdown and stdin EOF both exit 0 and dispose Whisper", async () =>
{
    foreach (var viaShutdown in new[] { true, false })
    {
        var service = new FakeWhisperService();
        var worker = WorkerPipe.Start(service, requestThreadFactory: false);
        await worker.ReadMessageAsync();
        if (viaShutdown) await worker.SendAsync(new WhisperWorkerShutdownCommand());
        else worker.CloseInput();
        var exit = await worker.Exit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(exit == 0 && service.Disposed, $"exit {exit}, disposed {service.Disposed} (shutdown={viaShutdown})");
        await worker.DisposeAsync();
    }
});

// =========================================================================
// THE REAL WORKER EXE, END TO END
// =========================================================================

var workerExe = FindWorkerExe();
if (workerExe is null)
{
    Console.WriteLine("SKIP: end-to-end checks (the worker exe was not found; set HW_WHISPER_WORKER_EXE)");
}
else
{
    await CheckAsync("e2e: the worker says ready, then reports a missing model as invalid_request", async () =>
    {
        await using var client = new WhisperWorkerClient(new WhisperWorkerProcessLauncher(workerExe, Console.Error.WriteLine), timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1), ReadyTimeout = TimeSpan.FromSeconds(30) });
        await client.SelectModelAsync(new WhisperWorkerModel(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin")));
        var error = await ThrowsAsync<WhisperWorkerException>(() => client.WarmUpAsync());
        Assert(error.Code == WhisperWorkerErrorCodes.InvalidRequest, $"{error.Code}: {error.Message}");
        Assert(client.IsRunning && client.StartCount == 1, "the worker did not come up, or restarted");
    });

    await CheckAsync("e2e: a damaged model file fails the load as model_file", async () =>
    {
        var garbage = Path.Combine(Path.GetTempPath(), $"broken-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(garbage, Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 31)).ToArray());
        try
        {
            await using var client = new WhisperWorkerClient(
                new WhisperWorkerProcessLauncher(workerExe, Console.Error.WriteLine, CpuOnly()),
                timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1), ReadyTimeout = TimeSpan.FromSeconds(30) });
            await client.SelectModelAsync(new WhisperWorkerModel(garbage, FlashAttention: false));
            var error = await ThrowsAsync<WhisperWorkerException>(() => client.WarmUpAsync());
            Assert(error.Code == WhisperWorkerErrorCodes.ModelFile && error.IsModelFileFault, $"{error.Code}: {error.Message}");
        }
        finally
        {
            File.Delete(garbage);
        }
    });

    await CheckAsync("e2e: a native runtime that cannot load fails as runtime, not model_file", async () =>
    {
        var garbage = Path.Combine(Path.GetTempPath(), $"broken-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(garbage, new byte[4096]);
        try
        {
            // The worker ships no CUDA runtime, so forcing it fails the native load.
            await using var client = new WhisperWorkerClient(
                new WhisperWorkerProcessLauncher(workerExe, Console.Error.WriteLine,
                    new Dictionary<string, string> { ["HW_WHISPER_WORKER_RUNTIMES"] = "Cuda" }),
                timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1), ReadyTimeout = TimeSpan.FromSeconds(30) });
            await client.SelectModelAsync(new WhisperWorkerModel(garbage));
            var error = await ThrowsAsync<WhisperWorkerException>(() => client.WarmUpAsync());
            Assert(error.Code == WhisperWorkerErrorCodes.Runtime && !error.IsModelFileFault, $"{error.Code}: {error.Message}");
            Assert(!client.IsRunning, "the poisoned worker kept running");
        }
        finally
        {
            File.Delete(garbage);
        }
    });

    await CheckAsync("e2e: shutdown makes the real worker exit, and idle does too", async () =>
    {
        await using var client = new WhisperWorkerClient(
            new WhisperWorkerProcessLauncher(workerExe, Console.Error.WriteLine),
            timings: fast with { IdleTimeout = TimeSpan.FromMilliseconds(500), ReadyTimeout = TimeSpan.FromSeconds(30), ShutdownTimeout = TimeSpan.FromSeconds(5) });
        await client.SelectModelAsync(new WhisperWorkerModel(Path.Combine(Path.GetTempPath(), "missing.bin")));
        await ThrowsAsync<WhisperWorkerException>(() => client.WarmUpAsync());
        var pid = client.WorkerProcessId ?? throw new InvalidOperationException("no pid");
        await WaitUntilAsync(() => !client.IsRunning, "the idle exit", TimeSpan.FromSeconds(10));
        Assert(client.IdleShutdownCount == 1, "not an idle shutdown");
        await WaitUntilAsync(() => !ProcessAlive(pid), $"pid {pid} to exit", TimeSpan.FromSeconds(10));
    });

    var realModel = Environment.GetEnvironmentVariable("HW_WHISPER_TEST_MODEL");
    if (string.IsNullOrEmpty(realModel) || !File.Exists(realModel))
    {
        Console.WriteLine("SKIP: real-model check (set HW_WHISPER_TEST_MODEL to a ggml model)");
    }
    else
    {
        await CheckAsync("e2e: a real model transcribes the same text through the worker as in process", async () =>
        {
            var sample = Path.Combine(AppContext.BaseDirectory, "onboarding-sample.wav");
            // The app's handover: decode, quantize to 16 bits, write with WhisperWav.
            float[] samples;
            await using (var file = File.OpenRead(sample)) samples = await new WaveParser(file).GetAvgSamplesAsync();
            var handover = Path.Combine(Path.GetTempPath(), $"handover-{Guid.NewGuid():N}.wav");
            WhisperWav.Write(handover, samples);
            try
            {
                await using var client = new WhisperWorkerClient(
                    new WhisperWorkerProcessLauncher(workerExe, Console.Error.WriteLine, CpuOnly()),
                    timings: fast with { IdleTimeout = TimeSpan.FromMinutes(1), ReadyTimeout = TimeSpan.FromSeconds(30) });
                await client.SelectModelAsync(new WhisperWorkerModel(realModel, FlashAttention: false));
                var load = await client.WarmUpAsync();
                Assert(load.Runtime == "cpu" && load.Gpu is null, $"load {load}");
                var viaWorker = await client.TranscribeAsync(handover, null, new(SingleSegment: false, MaxLastTextTokensZero: true, Threads: 4));
                Assert(viaWorker.Text.Length > 0 && viaWorker.SegmentCount > 0, "no text through the worker");

                await using var local = new LocalWhisperService();
                var loaded = await local.LoadAsync(new LocalWhisperLoadOptions(realModel, LocalWhisperBackend.Vulkan,
                    RuntimeOrder: [RuntimeLibrary.Cpu], UseFlashAttention: false, VerifyModelOnLoad: true));
                Assert(loaded.IsSuccess, $"in-process load: {loaded.Failure}");
                var inProcess = await local.TranscribeAsync(new LocalWhisperRequest(sample, null, 4, SingleSegment: false, MaxLastTextTokensZero: true));
                Assert(inProcess.IsSuccess, $"in-process: {inProcess.Failure}");
                Assert(inProcess.UnstrippedText == viaWorker.RawText, "the worker's text differs from the in-process text");
                Assert(inProcess.Text == viaWorker.Text && inProcess.SegmentCount == viaWorker.SegmentCount
                    && inProcess.DetectedLanguage == viaWorker.Language, "the worker's result differs from the in-process one");
                Console.WriteLine($"  ({viaWorker.SegmentCount} segments, {viaWorker.Text.Length} chars, identical)");
            }
            finally
            {
                File.Delete(handover);
            }
        });
    }
}

await CheckAsync("no fake worker loop or handler faulted", () =>
{
    foreach (var fault in FakeProcess.Faults)
    {
        Console.WriteLine($"  fake worker fault: {fault}");
    }
    return FakeProcess.Faults.IsEmpty
        ? Task.CompletedTask
        : throw new InvalidOperationException($"{FakeProcess.Faults.Count} fake worker task(s) faulted");
});

Console.WriteLine($"{checks - failures}/{checks} tests passed");
return failures == 0 ? 0 : 1;

// =========================================================================
// HELPERS
// =========================================================================

async Task CheckAsync(string name, Func<Task> body)
{
    checks++;
    try
    {
        await body().WaitAsync(TimeSpan.FromSeconds(60));
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL: {name}: {exception.GetType().Name}: {exception.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<T> ThrowsAsync<T>(Func<Task> body) where T : Exception
{
    try
    {
        await body();
    }
    catch (T expected)
    {
        return expected;
    }
    catch (Exception other)
    {
        throw new InvalidOperationException($"expected {typeof(T).Name}, got {other.GetType().Name}: {other.Message}", other);
    }
    throw new InvalidOperationException($"expected {typeof(T).Name}, nothing was thrown");
}

static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
{
    var deadline = Stopwatch.StartNew();
    while (!condition())
    {
        if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(5))) throw new TimeoutException($"timed out waiting for {what}");
        await Task.Delay(20);
    }
}

static Dictionary<string, string> CpuOnly() => new() { ["HW_WHISPER_WORKER_RUNTIMES"] = "Cpu,CpuNoAvx" };

static bool ProcessAlive(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static string? FindWorkerExe()
{
    var configured = Environment.GetEnvironmentVariable("HW_WHISPER_WORKER_EXE");
    if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;

    // This harness runs from .../HyperWhisper.WhisperWorker.Tests/bin/<Configuration>/net10.0/;
    // the worker builds to the same place under its own project folder.
    var name = OperatingSystem.IsWindows() ? "HyperWhisper.WhisperWorker.exe" : "HyperWhisper.WhisperWorker";
    var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    var tfm = baseDir.Name;
    var configuration = baseDir.Parent?.Name;
    var testsProject = baseDir.Parent?.Parent?.Parent;
    if (configuration is null || testsProject?.Parent is null) return null;
    var candidate = Path.Combine(testsProject.Parent.FullName, "HyperWhisper.WhisperWorker", "bin", configuration, tfm, name);
    return File.Exists(candidate) ? candidate : null;
}

/// <summary>Runs a WorkerHost in process over in-memory pipes.</summary>
internal sealed class WorkerPipe : IAsyncDisposable
{
    private readonly LineReader _input = new();
    private readonly LineWriter _output = new();

    private WorkerPipe(FakeWhisperService service, bool requestThreadFactory)
    {
        var host = new WorkerHost(service, _output, TextWriter.Null,
            [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            requestThreadFactory, () => service.IsLoaded ? "cpu" : null);
        Exit = Task.Run(() => host.RunAsync(_input, 4242));
    }

    public Task<int> Exit { get; }

    public static WorkerPipe Start(FakeWhisperService service, bool requestThreadFactory) => new(service, requestThreadFactory);

    public Task SendAsync(WhisperWorkerCommand command) => SendLineAsync(WhisperWorkerProtocol.BuildCommand(command));

    public Task SendLineAsync(string line) => _input.Lines.Writer.WriteAsync(line).AsTask();

    public void CloseInput() => _input.Lines.Writer.TryComplete();

    public async Task<WhisperWorkerMessage?> ReadMessageAsync()
    {
        var line = await _output.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return WhisperWorkerProtocol.TryParseMessage(line, out var message) ? message : throw new InvalidOperationException($"not a message: {line}");
    }

    public async ValueTask DisposeAsync()
    {
        CloseInput();
        try { await Exit.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    private sealed class LineReader : TextReader
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();

        public override async Task<string?> ReadLineAsync()
        {
            try { return await Lines.Reader.ReadAsync(); }
            catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class LineWriter : TextWriter
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            Lines.Writer.TryWrite(value ?? string.Empty);
            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Task.CompletedTask;
    }
}
