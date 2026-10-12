using HyperWhisper.LocalInference;
using Whisper.net.LibraryLoader;

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
await CheckAsync("Whisper non-speech markers never become a transcript (issue #1742)", () =>
{
    // The silent file Whisper answers with only markers strips to empty, so the
    // workflow reports no speech instead of pasting "[BLANK_AUDIO]".
    Assert(WhisperNonSpeechMarkers.Strip(" [BLANK_AUDIO] [BLANK_AUDIO]") == "",
        "a transcript of only [BLANK_AUDIO] markers must strip to empty");
    Assert(WhisperNonSpeechMarkers.Strip("[BLANK_AUDIO] ") == "", "a trailing space kept the marker");
    Assert(WhisperNonSpeechMarkers.Strip("[ Silence ] (music) [no speech] (inaudible) [blank audio]") == "",
        "Whisper's other non-speech annotations must strip too");
    Assert(WhisperNonSpeechMarkers.Strip(null) == "", "null must strip to empty");

    // Real speech around a marker survives, with one space where it was.
    var mixed = WhisperNonSpeechMarkers.Strip(" Hello there. [BLANK_AUDIO] See you soon.");
    Assert(mixed == "Hello there. See you soon.", $"speech around a marker was damaged: '{mixed}'");

    // A bracket the speaker dictated is not a marker.
    var dictated = WhisperNonSpeechMarkers.Strip(" Call the function [user id] (optional). ");
    Assert(dictated == "Call the function [user id] (optional).", $"dictated brackets were removed: '{dictated}'");
    return Task.CompletedTask;
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
