using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HyperWhisper.LocalInference;
using Whisper.net.LibraryLoader;

// Child-process probe for the runtime-fault split: Whisper.net caches a failed
// native load for the whole process, so it cannot run beside the other checks.
if (args is ["--probe-runtime-fault", var probeModel])
{
    await using var probe = new LocalWhisperService();
    var probed = await probe.LoadAsync(new LocalWhisperLoadOptions(
        probeModel,
        LocalWhisperBackend.Cpu,
        RuntimeOrder: [RuntimeLibrary.Cuda],
        VerifyModelOnLoad: true));
    Console.WriteLine($"PROBE {probed.Failure?.Code.ToString() ?? "Success"}");
    return 0;
}

var failures = 0;
var checks = 0;
await CheckAsync("missing model returns a structured failure", async () =>
{
    await using var service = new LocalWhisperService();
    var result = await service.LoadAsync(new LocalWhisperLoadOptions(
        Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin"),
        LocalWhisperBackend.Cpu));
    Assert(!result.IsSuccess, "missing model unexpectedly loaded");
    Assert(result.Failure?.Code == LocalWhisperErrorCode.InvalidRequest, "wrong missing-model failure");
});
await CheckAsync("transcription requires a loaded model", async () =>
{
    var audio = Path.GetTempFileName();
    try
    {
        await using var service = new LocalWhisperService();
        var result = await service.TranscribeAsync(new LocalWhisperRequest(audio));
        Assert(result.Failure?.Code == LocalWhisperErrorCode.RuntimeUnavailable, "wrong unloaded failure");
    }
    finally
    {
        File.Delete(audio);
    }
});
await CheckAsync("pre-cancelled requests return structured cancellation", async () =>
{
    var model = Path.GetTempFileName();
    try
    {
        await using var service = new LocalWhisperService();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await service.LoadAsync(
            new LocalWhisperLoadOptions(model, LocalWhisperBackend.Cpu),
            cancelled.Token);
        Assert(result.Failure?.Code == LocalWhisperErrorCode.Cancelled, "cancellation escaped the result contract");
    }
    finally
    {
        File.Delete(model);
    }
});
await CheckAsync("runtime order preserves GPU fallback policy", () =>
{
    Assert(LocalWhisperService.RuntimeOrderFor(LocalWhisperBackend.Cpu, true).SequenceEqual([RuntimeLibrary.Cpu]),
        "CPU runtime order changed");
    Assert(LocalWhisperService.RuntimeOrderFor(LocalWhisperBackend.Vulkan, false).SequenceEqual([RuntimeLibrary.Vulkan]),
        "strict Vulkan runtime order admitted fallback");
    Assert(LocalWhisperService.RuntimeOrderFor(LocalWhisperBackend.Vulkan, true).SequenceEqual([RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]),
        "Vulkan CPU fallback order changed");
    Assert(LocalWhisperService.RuntimeOrderFor(LocalWhisperBackend.Cuda12, false).SequenceEqual([RuntimeLibrary.Cuda12]),
        "strict CUDA 12 runtime order admitted fallback");
    Assert(LocalWhisperService.RuntimeOrderFor(LocalWhisperBackend.Cuda12, true).SequenceEqual([RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu]),
        "CUDA 12 CPU fallback order changed");
    return Task.CompletedTask;
});
await CheckAsync("Whisper non-speech markers never become a transcript (issues #1742, #1811)", () =>
{
    // The cases are the cross-platform contract: Rust, Windows and macOS run the
    // same file, so a silent file Whisper answers with only markers strips to
    // empty here exactly as it does on the other heads.
    var vectorsPath = Path.Combine(AppContext.BaseDirectory, "whisper-marker-vectors.json");
    Assert(File.Exists(vectorsPath), $"whisper-marker-vectors.json not found at {vectorsPath}");
    using var vectors = JsonDocument.Parse(File.ReadAllText(vectorsPath));
    var cases = vectors.RootElement.GetProperty("cases");
    Assert(cases.GetArrayLength() >= 20, "whisper-marker-vectors.json looks truncated");
    foreach (var vector in cases.EnumerateArray())
    {
        var name = vector.GetProperty("name").GetString()!;
        var input = vector.GetProperty("input").GetString()!;
        var expected = vector.GetProperty("expected").GetString()!;
        var actual = WhisperNonSpeechMarkers.Strip(input);
        Assert(actual == expected, $"{name}: got '{actual}', expected '{expected}'");
    }
    Assert(WhisperNonSpeechMarkers.Strip(null) == "", "null must strip to empty");
    return Task.CompletedTask;
});

await CheckAsync("Windows-only load and request options default off (issue #1194)", () =>
{
    var load = new LocalWhisperLoadOptions("model.bin", LocalWhisperBackend.Vulkan);
    Assert(load.RuntimeOrder is null, "a runtime order is set by default");
    Assert(load.UseFlashAttention is null, "flash attention is forced by default");
    Assert(!load.VerifyModelOnLoad, "the model is verified on load by default");
    var request = new LocalWhisperRequest("audio.wav");
    Assert(!request.MaxLastTextTokensZero, "WithMaxLastTextTokens(0) is on by default");
    Assert(!request.CreateFactoryOnRequestThread, "the ARM64 thread rule is on by default");
    // The Linux transcriber compares load options by value to skip a reload.
    Assert(load == new LocalWhisperLoadOptions("model.bin", LocalWhisperBackend.Vulkan),
        "default load options no longer compare equal");
    return Task.CompletedTask;
});
await CheckAsync("an explicit runtime order replaces the backend-derived one (issue #1194)", () =>
{
    var windows = new LocalWhisperLoadOptions(
        "model.bin",
        LocalWhisperBackend.Vulkan,
        RuntimeOrder: [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]);
    Assert(LocalWhisperService.RuntimeOrderFor(windows)
            .SequenceEqual([RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]),
        "the Windows runtime order was not used");
    Assert(LocalWhisperService.RuntimeOrderFor(new LocalWhisperLoadOptions("m", LocalWhisperBackend.Vulkan))
            .SequenceEqual([RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]),
        "no explicit order must keep the backend's order");
    Assert(LocalWhisperService.RuntimeOrderFor(new LocalWhisperLoadOptions("m", LocalWhisperBackend.Cuda12, RuntimeOrder: []))
            .SequenceEqual([RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu]),
        "an empty order must keep the backend's order");
    return Task.CompletedTask;
});
await CheckAsync("factory options keep the backend rule unless flash attention is set", () =>
{
    var cpu = LocalWhisperService.FactoryOptionsFor(new LocalWhisperLoadOptions("m", LocalWhisperBackend.Cpu));
    Assert(!cpu.UseGpu && !cpu.UseFlashAttention, "CPU options changed");
    var vulkan = LocalWhisperService.FactoryOptionsFor(new LocalWhisperLoadOptions("m", LocalWhisperBackend.Vulkan, GpuDevice: 2));
    Assert(vulkan.UseGpu && vulkan.UseFlashAttention && vulkan.GpuDevice == 2, "Vulkan options changed");
    var noFlash = LocalWhisperService.FactoryOptionsFor(
        new LocalWhisperLoadOptions("m", LocalWhisperBackend.Vulkan, GpuDevice: -3, UseFlashAttention: false));
    Assert(noFlash.UseGpu && !noFlash.UseFlashAttention && noFlash.GpuDevice == 0, "flash attention override ignored");
    return Task.CompletedTask;
});
await CheckAsync("a native runtime fault is not a model-file fault (issues #1598, #1194)", () =>
{
    Assert(LocalWhisperService.ClassifyLoadFault(new InvalidOperationException("bad model"), runtimeLoaded: true)
            == LocalWhisperErrorCode.ModelLoadFailed,
        "a plain load failure after the runtime loaded must be the model's");
    Assert(LocalWhisperService.ClassifyLoadFault(new InvalidOperationException("no library"), runtimeLoaded: false)
            == LocalWhisperErrorCode.RuntimeUnavailable,
        "a failure before the runtime loaded must be the runtime's");
    foreach (var inner in new Exception[]
    {
        new DllNotFoundException("whisper.dll"),
        new BadImageFormatException("arch"),
        new EntryPointNotFoundException("whisper_init"),
    })
    {
        Assert(LocalWhisperService.ClassifyLoadFault(
                new InvalidOperationException("load", new InvalidOperationException("wrap", inner)), runtimeLoaded: true)
                == LocalWhisperErrorCode.RuntimeUnavailable,
            $"a nested {inner.GetType().Name} must be the runtime's");
        Assert(LocalWhisperService.IsNativeRuntimeFault(inner), $"a bare {inner.GetType().Name} must be the runtime's");
    }
    Assert(!LocalWhisperService.IsNativeRuntimeFault(new FileNotFoundException("x")), "an unrelated exception matched");
    return Task.CompletedTask;
});
await CheckAsync("a broken model file fails the load only when verification is on", async () =>
{
    var model = Path.GetTempFileName();
    var audio = Path.GetTempFileName();
    try
    {
        await File.WriteAllTextAsync(model, "this is not a ggml model");
        await using (var verifying = new LocalWhisperService())
        {
            var verified = await verifying.LoadAsync(
                new LocalWhisperLoadOptions(model, LocalWhisperBackend.Cpu, VerifyModelOnLoad: true));
            Assert(verified.Failure?.Code == LocalWhisperErrorCode.ModelLoadFailed,
                $"expected ModelLoadFailed, got {verified.Failure?.Code.ToString() ?? "success"}");
            Assert(!verifying.IsLoaded, "a failed load left a factory behind");
        }
        // Linux keeps today's behaviour: the load succeeds and the first
        // transcription reports the broken file.
        await using var lazy = new LocalWhisperService();
        var loaded = await lazy.LoadAsync(new LocalWhisperLoadOptions(model, LocalWhisperBackend.Cpu));
        Assert(loaded.IsSuccess, loaded.Failure?.Message ?? "unverified load failed");
        var transcribed = await lazy.TranscribeAsync(new LocalWhisperRequest(audio, "en"));
        Assert(transcribed.Failure?.Code == LocalWhisperErrorCode.TranscriptionFailed,
            $"expected TranscriptionFailed, got {transcribed.Failure?.Code.ToString() ?? "success"}");
        var perRequest = await lazy.TranscribeAsync(new LocalWhisperRequest(audio, "en", CreateFactoryOnRequestThread: true));
        Assert(perRequest.Failure?.Code == LocalWhisperErrorCode.ModelLoadFailed,
            $"the per-request factory should report ModelLoadFailed, got {perRequest.Failure?.Code.ToString() ?? "success"}");
    }
    finally
    {
        File.Delete(model);
        File.Delete(audio);
    }
});
await CheckAsync("the ARM64 rule's factory is built once on its own thread across requests", async () =>
{
    var built = 0;
    var disposed = 0;
    int? createdOn = null;
    var usedOn = new List<int>();
    var thread = new FactoryThread<FakeFactory>(
        () =>
        {
            built++;
            createdOn = Environment.CurrentManagedThreadId;
            return new FakeFactory(() => disposed++);
        },
        "test factory thread");
    try
    {
        var first = await thread.RunAsync(factory => { usedOn.Add(Environment.CurrentManagedThreadId); return factory; });
        for (var chunk = 0; chunk < 4; chunk++)
        {
            var same = await thread.RunAsync(factory => { usedOn.Add(Environment.CurrentManagedThreadId); return factory; });
            Assert(ReferenceEquals(same, first), $"request {chunk + 2} got a new factory");
        }
        Assert(built == 1 && thread.FactoriesBuilt == 1, $"5 requests built {built} factories, expected 1");
        Assert(createdOn == thread.ThreadId, "the factory was not made on the factory thread");
        Assert(usedOn.All(id => id == createdOn), "a builder ran on another thread than the factory's");
        Assert(disposed == 0, "the kept factory was disposed between requests");

        // A failed request drops the factory; the next one builds a fresh one.
        var failed = false;
        try
        {
            await thread.RunAsync<bool>(_ => throw new InvalidOperationException("builder failed"));
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        Assert(failed, "the callback's exception did not reach the caller");
        Assert(disposed == 1, "a failed request kept its factory");
        await thread.RunAsync(_ => true);
        Assert(built == 2, $"expected a fresh factory after a failure, built {built}");

        await thread.ResetAsync();
        Assert(disposed == 2, "reset did not dispose the factory");
        await thread.ResetAsync();
        Assert(built == 2 && disposed == 2, "a reset with no factory built or disposed one");
    }
    finally
    {
        thread.Dispose();
    }
    Assert(built == 2 && disposed == 2, "dispose touched a factory it no longer held");

    var held = new FactoryThread<FakeFactory>(() => new FakeFactory(() => disposed++), "test factory thread");
    await held.RunAsync(_ => true);
    held.Dispose();
    Assert(disposed == 3, "dispose did not dispose the kept factory");
});
await CheckAsync("a runtime that cannot load reports RuntimeUnavailable (issue #1194)", async () =>
{
    var model = Path.GetTempFileName();
    try
    {
        await File.WriteAllTextAsync(model, "this is not a ggml model");
        var output = await RunSelfAsync("--probe-runtime-fault", model);
        Assert(output.Contains("PROBE RuntimeUnavailable", StringComparison.Ordinal),
            $"expected RuntimeUnavailable, the probe said: {output.Trim()}");
    }
    finally
    {
        File.Delete(model);
    }
});

if (args.Length is 2 or 3)
{
    await CheckAsync("real Linux Whisper model loads and transcribes", async () =>
    {
        await using var service = new LocalWhisperService();
        var loaded = await service.LoadAsync(new LocalWhisperLoadOptions(
            args[0],
            ParseBackend(Environment.GetEnvironmentVariable("HW_WHISPER_TEST_BACKEND")),
            AllowCpuFallback: Environment.GetEnvironmentVariable("HW_WHISPER_ALLOW_CPU_FALLBACK") != "0"));
        Assert(loaded.IsSuccess, loaded.Failure?.Message ?? "model load failed");
        var result = await service.TranscribeAsync(new LocalWhisperRequest(args[1], "en"));
        Assert(result.IsSuccess, result.Failure?.Message ?? "transcription failed");
        Assert(!string.IsNullOrWhiteSpace(result.Text), "transcript was empty");
        var expectedRuntime = Environment.GetEnvironmentVariable("HW_WHISPER_EXPECT_RUNTIME");
        if (!string.IsNullOrWhiteSpace(expectedRuntime))
            Assert(result.Runtime?.Contains(expectedRuntime, StringComparison.OrdinalIgnoreCase) == true,
                $"expected runtime {expectedRuntime}, got {result.Runtime ?? "unknown"}");
        Console.WriteLine($"RUNTIME {result.Runtime}");
        Console.WriteLine($"TRANSCRIPT {result.Text}");
    });
}

if (args.Length is 2 or 3)
{
    await CheckAsync("real model: the ARM64 rule reads the model once across chunk requests", async () =>
    {
        await using var service = new LocalWhisperService();
        var options = new LocalWhisperLoadOptions(args[0], LocalWhisperBackend.Cpu);
        var loaded = await service.LoadAsync(options);
        Assert(loaded.IsSuccess, loaded.Failure?.Message ?? "model load failed");
        for (var chunk = 0; chunk < 3; chunk++)
        {
            var result = await service.TranscribeAsync(new LocalWhisperRequest(args[1], "en", CreateFactoryOnRequestThread: true));
            Assert(result.IsSuccess, result.Failure?.Message ?? "transcription failed");
            Assert(!string.IsNullOrWhiteSpace(result.Text), $"chunk {chunk} transcript was empty");
        }
        Assert(service.RequestFactoriesBuilt == 1, $"3 requests built {service.RequestFactoriesBuilt} factories, expected 1");
        var plain = await service.TranscribeAsync(new LocalWhisperRequest(args[1], "en"));
        Assert(plain.IsSuccess && service.RequestFactoriesBuilt == 1, "the default path touched the ARM64 factory");
        Assert((await service.LoadAsync(options)).IsSuccess, "reload failed");
        var after = await service.TranscribeAsync(new LocalWhisperRequest(args[1], "en", CreateFactoryOnRequestThread: true));
        Assert(after.IsSuccess && service.RequestFactoriesBuilt == 2, "a reload did not drop the kept factory");
    });
}

if (args.Length == 3)
{
    // Optional third argument: a silent WAV, e.g.
    // ffmpeg -f lavfi -i anullsrc=r=16000:cl=mono -t 60 -c:a pcm_s16le silence60.wav
    await CheckAsync("real Linux Whisper model returns no text for silence (issue #1742)", async () =>
    {
        await using var service = new LocalWhisperService();
        var loaded = await service.LoadAsync(new LocalWhisperLoadOptions(args[0], LocalWhisperBackend.Cpu));
        Assert(loaded.IsSuccess, loaded.Failure?.Message ?? "model load failed");
        var result = await service.TranscribeAsync(new LocalWhisperRequest(args[2], "en", IncludeWordTimestamps: true));
        Assert(result.IsSuccess, result.Failure?.Message ?? "transcription failed");
        Assert(result.Text == "", $"silence came back as a transcript: '{result.Text}'");
        Assert(result.Timestamps is null, "silence came back with timestamps");
    });
}

Console.WriteLine($"{checks - failures}/{checks} tests passed");
return failures == 0 ? 0 : 1;

async Task CheckAsync(string name, Func<Task> run)
{
    checks++;
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static async Task<string> RunSelfAsync(params string[] arguments)
{
    var host = Environment.ProcessPath!;
    var start = new ProcessStartInfo(host)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    // Run as `dotnet <dll>` (dotnet run, the coverage collector) or as the apphost.
    if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    }
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    return await stdout + await stderr;
}

static LocalWhisperBackend ParseBackend(string? value) =>
    value?.Trim().ToLowerInvariant() switch
    {
        "cuda" or "cuda12" => LocalWhisperBackend.Cuda12,
        "vulkan" => LocalWhisperBackend.Vulkan,
        _ => LocalWhisperBackend.Cpu,
    };

static void Assert(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeFactory(Action onDispose) : IDisposable
{
    public void Dispose() => onDispose();
}
