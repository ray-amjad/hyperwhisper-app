using System.Security.Cryptography;
using HyperWhisper.LocalPostProcessing;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("prompt protocol matches Windows markers", PromptProtocol),
    ("completion wrappers are normalized", CompletionWrappers),
    ("prompt leakage is rejected", PromptLeakage),
    ("post-processing applies fake completion", AppliesCompletion),
    ("post-processing preserves transcript on failure", PreservesTranscript),
    ("cancellation is structured", CancellationIsStructured),
    ("prompt budget boundary", PromptBudget),
    ("model store downloads and validates atomically", ModelDownload),
    ("model checksum mismatch is rejected", ModelChecksumMismatch),
    ("model download cancellation is structured", ModelDownloadCancellation),
    ("model path traversal is rejected", ModelPathTraversal),
    ("packaged runtime resolver fails closed", RuntimeResolver),
};
if (Environment.GetEnvironmentVariable("HYPERWHISPER_TEST_GGUF") is { Length: > 0 })
{
    tests.Add(("actual GGUF inference", ActualGgufInference));
}

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
    }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed");
return failures == 0 ? 0 : 1;

static Task PromptProtocol()
{
    Equal("context\n\n--TRANSCRIPT--\nhello\n--ENDTRANSCRIPT--",
        LocalPostProcessingProtocol.BuildUserMessage("context", "hello"));
    return Task.CompletedTask;
}

static Task CompletionWrappers()
{
    True(LocalPostProcessingProtocol.TryEvaluateCompletion(
        "preamble <<CLEANED>>\nHello, Ray.\n<<END>> ignored", out var wrapped));
    Equal("Hello, Ray.", wrapped);
    True(LocalPostProcessingProtocol.TryEvaluateCompletion("Plain text<<END>>", out var plain));
    Equal("Plain text", plain);
    return Task.CompletedTask;
}

static Task PromptLeakage()
{
    False(LocalPostProcessingProtocol.TryEvaluateCompletion(
        "<<CLEANED>><SYSTEM_INFO>secret</SYSTEM_INFO><<END>>", out _));
    False(LocalPostProcessingProtocol.TryEvaluateCompletion(
        "<SCREEN_CONTEXT>private OCR text</SCREEN_CONTEXT>", out _));
    False(LocalPostProcessingProtocol.TryEvaluateCompletion("   ", out _));
    return Task.CompletedTask;
}

static async Task AppliesCompletion()
{
    var model = await TemporaryModel();
    await using var service = new LocalPostProcessingService(
        new FakeEngine(LocalLlmGenerationResult.Success(
            "<<CLEANED>>Hello, Ray.<<END>>", "fake/cpu")));
    var result = await service.ProcessAsync(new(
        model, LocalLlmBackend.Cpu, "system", "context", "hello ray"));
    True(result.IsSuccess);
    Equal("Hello, Ray.", result.Text);
    Equal("fake/cpu", result.Runtime);
    File.Delete(model);
}

static async Task PreservesTranscript()
{
    var model = await TemporaryModel();
    await using var service = new LocalPostProcessingService(
        new FakeEngine(LocalLlmGenerationResult.Failed(
            LocalPostProcessingErrorCode.ModelLoadFailed, "failed")));
    var result = await service.ProcessAsync(new(
        model, LocalLlmBackend.Vulkan, "system", "", "original"));
    False(result.IsSuccess);
    Equal("original", result.Text);
    Equal(LocalPostProcessingErrorCode.ModelLoadFailed, result.Failure!.Code);
    File.Delete(model);
}

static async Task CancellationIsStructured()
{
    var model = await TemporaryModel();
    await using var service = new LocalPostProcessingService(new CancellingEngine());
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await service.ProcessAsync(new(
        model, LocalLlmBackend.Cpu, "system", "", "original"), cancellation.Token);
    Equal(LocalPostProcessingErrorCode.Cancelled, result.Failure!.Code);
    File.Delete(model);
}

static Task PromptBudget()
{
    LLamaSharpLocalLlmEngine.EnsurePromptFits(1, 7_679);
    Throws<ArgumentOutOfRangeException>(() =>
        LLamaSharpLocalLlmEngine.EnsurePromptFits(1, 7_680));
    return Task.CompletedTask;
}

static async Task ModelDownload()
{
    var directory = Path.Combine(Path.GetTempPath(), $"hw-llm-{Guid.NewGuid():N}");
    var bytes = FakeGguf(64);
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var model = new LocalLlmModelDescriptor(
        "fake", "fake.gguf", new Uri("https://example.invalid/fake.gguf"), bytes.Length, hash);
    var store = new LocalLlmModelStore(directory, new ByteSource(bytes));
    var downloaded = await store.DownloadAsync(model);
    True(downloaded.IsSuccess);
    Equal(store.GetModelPath(model), downloaded.ModelPath);
    var validated = await store.ValidateAsync(model);
    True(validated.IsValid);
    if (!OperatingSystem.IsWindows())
    {
        Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.GetModelPath(model)));
    }
    Directory.Delete(directory, recursive: true);
}

static async Task ModelChecksumMismatch()
{
    var directory = Path.Combine(Path.GetTempPath(), $"hw-llm-{Guid.NewGuid():N}");
    var bytes = FakeGguf(64);
    var model = new LocalLlmModelDescriptor(
        "fake", "fake.gguf", new Uri("https://example.invalid/fake.gguf"), bytes.Length,
        new string('0', 64));
    var store = new LocalLlmModelStore(directory, new ByteSource(bytes));
    var result = await store.DownloadAsync(model);
    False(result.IsSuccess);
    Equal(LocalPostProcessingErrorCode.ModelInvalid, result.Failure!.Code);
    False(File.Exists(store.GetModelPath(model)));
    Directory.Delete(directory, recursive: true);
}

static async Task ModelDownloadCancellation()
{
    var directory = Path.Combine(Path.GetTempPath(), $"hw-llm-{Guid.NewGuid():N}");
    var model = new LocalLlmModelDescriptor(
        "fake", "fake.gguf", new Uri("https://example.invalid/fake.gguf"), 8);
    var store = new LocalLlmModelStore(directory, new CancelledSource());
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await store.DownloadAsync(model, cancellationToken: cancellation.Token);
    False(result.IsSuccess);
    Equal(LocalPostProcessingErrorCode.Cancelled, result.Failure!.Code);
    Directory.Delete(directory, recursive: true);
}

static Task ModelPathTraversal()
{
    var model = new LocalLlmModelDescriptor(
        "bad", "../bad.gguf", new Uri("https://example.invalid/bad"), 8);
    var store = new LocalLlmModelStore(Path.GetTempPath(), new ByteSource(FakeGguf(8)));
    Throws<ArgumentException>(() => store.GetModelPath(model));
    return Task.CompletedTask;
}

static Task RuntimeResolver()
{
    var empty = Path.Combine(Path.GetTempPath(), $"hw-runtimes-{Guid.NewGuid():N}");
    Directory.CreateDirectory(empty);
    False(PackagedLlamaRuntime.IsAvailable(LocalLlmBackend.Cpu, empty));
    False(PackagedLlamaRuntime.IsAvailable(LocalLlmBackend.Vulkan, empty));
    False(PackagedLlamaRuntime.IsAvailable(LocalLlmBackend.Cuda, empty));
    Directory.Delete(empty);
    True(PackagedLlamaRuntime.IsAvailable(LocalLlmBackend.Cpu));
    return Task.CompletedTask;
}

static async Task ActualGgufInference()
{
    var path = Environment.GetEnvironmentVariable("HYPERWHISPER_TEST_GGUF")!;
    await using var engine = new LLamaSharpLocalLlmEngine();
    var backend = Enum.TryParse<LocalLlmBackend>(
        Environment.GetEnvironmentVariable("HYPERWHISPER_TEST_BACKEND"),
        ignoreCase: true,
        out var selected) ? selected : LocalLlmBackend.Cpu;
    var result = await engine.GenerateAsync(new(
        path,
        backend,
        "Continue the story briefly.",
        "Once upon a time",
        AllowCpuFallback: true,
        Timeout: TimeSpan.FromSeconds(30),
        MaxOutputTokens: 16));
    if (!result.IsSuccess)
    {
        throw new InvalidOperationException(
            $"Actual inference failed: {result.Failure?.Code} {result.Failure?.Message}");
    }
    True(result.Completion!.Length > 0);
    True(result.Runtime!.Contains(backend.ToString(), StringComparison.OrdinalIgnoreCase));
    if (Environment.GetEnvironmentVariable("HYPERWHISPER_EXPECT_FALLBACK") == "1")
    {
        True(result.Runtime.Contains("cpu-fallback", StringComparison.Ordinal));
    }

    var incompatibleBackend = backend == LocalLlmBackend.Cpu
        ? LocalLlmBackend.Vulkan
        : LocalLlmBackend.Cpu;
    var incompatible = await engine.GenerateAsync(new(
        path, incompatibleBackend, "system", "message", true, TimeSpan.FromSeconds(1)));
    Equal(LocalPostProcessingErrorCode.RuntimeUnavailable, incompatible.Failure!.Code);
}

static async Task<string> TemporaryModel()
{
    var path = Path.Combine(Path.GetTempPath(), $"hw-fake-{Guid.NewGuid():N}.gguf");
    await File.WriteAllBytesAsync(path, FakeGguf(8));
    return path;
}

static byte[] FakeGguf(int length)
{
    var bytes = new byte[length];
    "GGUF"u8.CopyTo(bytes);
    bytes[4] = 3;
    return bytes;
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value) => True(!value);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

file sealed class FakeEngine(LocalLlmGenerationResult result) : ILocalLlmEngine
{
    public ValueTask<LocalLlmGenerationResult> GenerateAsync(
        LocalLlmGenerationRequest request,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class CancellingEngine : ILocalLlmEngine
{
    public ValueTask<LocalLlmGenerationResult> GenerateAsync(
        LocalLlmGenerationRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(LocalLlmGenerationResult.Failed(
            LocalPostProcessingErrorCode.Cancelled, "cancelled"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class ByteSource(byte[] bytes) : ILocalLlmModelSource
{
    public ValueTask<Stream> OpenReadAsync(Uri uri, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
}

file sealed class CancelledSource : ILocalLlmModelSource
{
    public ValueTask<Stream> OpenReadAsync(Uri uri, CancellationToken cancellationToken = default) =>
        ValueTask.FromCanceled<Stream>(cancellationToken);
}
