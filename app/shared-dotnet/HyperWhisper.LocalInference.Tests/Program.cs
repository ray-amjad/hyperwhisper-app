using HyperWhisper.LocalInference;

var failures = 0;
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

if (args.Length == 2)
{
    await CheckAsync("real Linux Whisper model loads and transcribes", async () =>
    {
        await using var service = new LocalWhisperService();
        var loaded = await service.LoadAsync(new LocalWhisperLoadOptions(
            args[0],
            ParseBackend(Environment.GetEnvironmentVariable("HW_WHISPER_TEST_BACKEND"))));
        Assert(loaded.IsSuccess, loaded.Failure?.Message ?? "model load failed");
        var result = await service.TranscribeAsync(new LocalWhisperRequest(args[1], "en"));
        Assert(result.IsSuccess, result.Failure?.Message ?? "transcription failed");
        Assert(!string.IsNullOrWhiteSpace(result.Text), "transcript was empty");
        Console.WriteLine($"RUNTIME {result.Runtime}");
        Console.WriteLine($"TRANSCRIPT {result.Text}");
    });
}

Console.WriteLine($"{(args.Length == 2 ? 4 : 3) - failures}/{(args.Length == 2 ? 4 : 3)} tests passed");
return failures == 0 ? 0 : 1;

async Task CheckAsync(string name, Func<Task> run)
{
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
    string.Equals(value, "vulkan", StringComparison.OrdinalIgnoreCase)
        ? LocalWhisperBackend.Vulkan
        : LocalWhisperBackend.Cpu;

static void Assert(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}
