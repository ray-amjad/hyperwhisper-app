using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;
using HyperWhisper.ModelManagement;
using HyperWhisper.Platform.Abstractions;

var failures = 0;
await CheckAsync("catalog matches authoritative Windows registries", () =>
{
    Assert(PortableModelCatalog.Whisper.Count == 11, "Whisper catalog count changed");
    Assert(PortableModelCatalog.Parakeet.Count == 4, "Parakeet catalog count changed");
    Assert(PortableModelCatalog.LocalLlm.Count == 5, "local LLM catalog count changed");
    Assert(PortableModelCatalog.All.Count == 20, "combined catalog count changed");
    Assert(PortableModelCatalog.Whisper.All(m => m.Artifacts.Single().ExactSizeBytes == m.ApproximateSizeBytes),
        "Whisper byte-exact sizes are not enforced");
    Assert(PortableModelCatalog.Parakeet.Single(m => m.Id == "parakeet-v3").SupportedLanguages.Count == 26,
        "Parakeet v3 languages changed");
    var nemotron = PortableModelCatalog.Parakeet.Single(m => m.Id == "nemotron-3.5-ml-560ms");
    Assert(nemotron.SupportsStreaming && nemotron.SupportedLanguages.Count == 32
        && nemotron.SupportedLanguages.Contains("en-US") && nemotron.SupportedLanguages.Contains("zh-CN")
        && !nemotron.SupportedLanguages.Contains("mt-MT"),
        "Nemotron production locale metadata changed");
    Assert(PortableModelCatalog.Parakeet.Count(m => m.SupportsStreaming) == 3,
        "local streaming model metadata changed");
    Assert(PortableModelCatalog.LocalLlm.Count(m => m.IsRecommended) == 1, "recommended LLM must be unique");
    Assert(PortableModelCatalog.All.SelectMany(m => m.Artifacts).All(a => a.DownloadUri.Scheme == "https"),
        "catalog contains a non-HTTPS artifact");
    return Task.CompletedTask;
});

await CheckAsync("strict containment rejects traversal and non-HTTPS URLs", async () =>
{
    using var scope = new Scope(new StaticHandler([1, 2, 3, 4]));
    var traversal = TestModel("../escaped.gguf", [1, 2, 3, 4]);
    var result = await scope.Manager.DownloadAsync(traversal);
    Assert(result.Failure?.Code == ModelManagementError.InvalidRequest, "traversal was not rejected");
    Assert(!File.Exists(Path.Combine(scope.Root, "escaped.gguf")), "traversal wrote outside model root");

    var insecure = traversal with
    {
        StorageName = "safe.gguf",
        Artifacts = [traversal.Artifacts[0] with { DownloadUri = new Uri("http://example.invalid/model") }]
    };
    result = await scope.Manager.DownloadAsync(insecure);
    Assert(result.Failure?.Code == ModelManagementError.InvalidRequest, "HTTP artifact was not rejected");

    var invalidRepository = new ManagedModel("tree", "Tree", ManagedModelKind.Parakeet,
        ManagedModelLayout.HuggingFaceTree, "tree", 100, false, [], [], "owner/repo?redirect=1");
    result = await scope.Manager.DownloadAsync(invalidRepository);
    Assert(result.Failure?.Code == ModelManagementError.InvalidRequest,
        "invalid Hugging Face repository identifier was accepted");
});

await CheckAsync("streamed checksum download finalizes privately and deletes safely", async () =>
{
    byte[] payload = [.. "GGUF"u8.ToArray(), 1, 2, 3, 4];
    using var scope = new Scope(new StaticHandler(payload));
    var model = TestModel("verified.gguf", payload);
    var updates = new List<ModelDownloadProgress>();
    var result = await scope.Manager.DownloadAsync(model, new InlineProgress<ModelDownloadProgress>(updates.Add));
    Assert(result.IsSuccess, result.Failure?.Message ?? "download failed");
    Assert(scope.Manager.IsInstalled(model), "download was not detected as installed");
    Assert(updates.Count > 0 && updates.Any(p => p.Fraction == 1), "completion progress missing");
    if (!OperatingSystem.IsWindows())
    {
        var mode = File.GetUnixFileMode(result.Value!);
        Assert(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), $"unexpected file mode {mode}");
    }
    Assert(scope.Manager.Delete(model).IsSuccess, "safe delete failed");
    Assert(!File.Exists(result.Value), "model survived deletion");
});

await CheckAsync("checksum failure is structured and removes partial files", async () =>
{
    byte[] payload = [.. "GGUF"u8.ToArray(), 9, 8, 7, 6];
    using var scope = new Scope(new StaticHandler(payload));
    var model = TestModel("bad.gguf", payload) with
    {
        Artifacts = [TestModel("bad.gguf", payload).Artifacts[0] with { Sha256 = new string('0', 64) }]
    };
    var result = await scope.Manager.DownloadAsync(model);
    Assert(result.Failure?.Code == ModelManagementError.Validation, "checksum failure was not structured");
    Assert(!Directory.EnumerateFileSystemEntries(scope.Paths.ModelsDirectory, "*.partial", SearchOption.AllDirectories).Any(),
        "partial file survived checksum failure");
});

await CheckAsync("declared length mismatch is rejected and cleaned", async () =>
{
    byte[] payload = [.. "GGUF"u8.ToArray(), 1, 2, 3, 4];
    using var scope = new Scope(new StaticHandler(payload, payload.Length + 5));
    var result = await scope.Manager.DownloadAsync(TestModel("short.gguf", payload));
    Assert(result.Failure?.Code == ModelManagementError.Validation, "short transfer was accepted");
    Assert(!Directory.EnumerateFileSystemEntries(scope.Paths.ModelsDirectory, "*.partial", SearchOption.AllDirectories).Any(),
        "partial file survived short transfer");
});

await CheckAsync("cancellation is structured and removes partial files", async () =>
{
    using var scope = new Scope(new CancellingHandler());
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    var result = await scope.Manager.DownloadAsync(TestModel("cancel.gguf", [.. "GGUF"u8.ToArray(), 1]), cancellationToken: cancellation.Token);
    Assert(result.Failure?.Code == ModelManagementError.Cancelled, "cancellation escaped result contract");
    Assert(!Directory.EnumerateFileSystemEntries(scope.Paths.ModelsDirectory, "*.partial", SearchOption.AllDirectories).Any(),
        "partial file survived cancellation");
});

await CheckAsync("fixed-file installed state requires every non-empty artifact", () =>
{
    using var scope = new Scope(new StaticHandler([1]));
    var source = PortableModelCatalog.Parakeet[0];
    var model = source with
    {
        Id = "fixture",
        StorageName = "fixture",
        ApproximateSizeBytes = 4,
        Artifacts = source.Artifacts.Select(a => a with { ExactSizeBytes = 1, Sha256 = null }).ToArray()
    };
    var directory = scope.Manager.GetInstalledPath(model);
    Directory.CreateDirectory(directory);
    foreach (var artifact in model.Artifacts) File.WriteAllBytes(Path.Combine(directory, artifact.RelativePath), [1]);
    Assert(scope.Manager.IsInstalled(model), "complete fixed model not detected");
    File.Delete(Path.Combine(directory, model.Artifacts[0].RelativePath));
    Assert(!scope.Manager.IsInstalled(model), "incomplete fixed model accepted");
    return Task.CompletedTask;
});

await CheckAsync("existing symlinks cannot redirect model writes", async () =>
{
    if (OperatingSystem.IsWindows()) return;
    byte[] payload = [.. "GGUF"u8.ToArray(), 1, 2, 3, 4];
    using var scope = new Scope(new StaticHandler(payload));
    var outside = Path.Combine(scope.Root, "outside");
    Directory.CreateDirectory(outside);
    Directory.CreateSymbolicLink(Path.Combine(scope.Paths.ModelsDirectory, "LLM"), outside);
    var result = await scope.Manager.DownloadAsync(TestModel("redirect.gguf", payload));
    Assert(result.Failure?.Code == ModelManagementError.InvalidRequest, "symlink redirect was accepted");
    Assert(!File.Exists(Path.Combine(outside, "redirect.gguf")), "download escaped through symlink");
});

await CheckAsync("tree downloads follow pagination and validate required shape", async () =>
{
    using var scope = new Scope(new TreeHandler());
    var model = new ManagedModel("tree-fixture", "Tree fixture", ManagedModelKind.Parakeet,
        ManagedModelLayout.HuggingFaceTree, "tree-fixture", 4, false, ["en"], [], "owner/repository");
    var result = await scope.Manager.DownloadAsync(model);
    Assert(result.IsSuccess, result.Failure?.Message ?? "tree download failed");
    Assert(scope.Manager.IsInstalled(model), "completed tree was not detected");
    Assert(File.Exists(Path.Combine(result.Value!, "tokenizer", "config.json")), "nested tokenizer file missing");
});

await CheckAsync("tree pagination rejects cycles", async () =>
{
    using var scope = new Scope(new InvalidTreeHandler(InvalidTreeMode.Cycle));
    var result = await scope.Manager.DownloadAsync(TreeFixture());
    Assert(result.Failure?.Code == ModelManagementError.Validation, "pagination cycle was accepted");
});

await CheckAsync("tree pagination rejects cross-host next links", async () =>
{
    using var scope = new Scope(new InvalidTreeHandler(InvalidTreeMode.CrossHost));
    var result = await scope.Manager.DownloadAsync(TreeFixture());
    Assert(result.Failure?.Code == ModelManagementError.Validation, "cross-host next link was accepted");
});

await CheckAsync("tree manifests reject oversized aggregate declarations", async () =>
{
    using var scope = new Scope(new InvalidTreeHandler(InvalidTreeMode.Oversize));
    var result = await scope.Manager.DownloadAsync(TreeFixture());
    Assert(result.Failure?.Code == ModelManagementError.Validation, "oversized tree was accepted");
});

await CheckAsync("streaming ceiling applies without Content-Length", async () =>
{
    byte[] payload = [.. "GGUF"u8.ToArray(), 1, 2];
    using var scope = new Scope(new NoLengthHandler(payload));
    var model = new ManagedModel("unbounded", "Unbounded fixture", ManagedModelKind.LocalLlm,
        ManagedModelLayout.SingleFile, "unbounded.gguf", 4, false, [],
        [new("unbounded.gguf", new Uri("https://models.example.test/model"))]);
    var result = await scope.Manager.DownloadAsync(model);
    Assert(result.Failure?.Code == ModelManagementError.Validation, "oversized lengthless stream was accepted");
});

await CheckAsync("multi-file progress is aggregate and monotonic", async () =>
{
    using var scope = new Scope(new StaticHandler([1]));
    var model = FixedFixture();
    var updates = new List<ModelDownloadProgress>();
    var result = await scope.Manager.DownloadAsync(model, new InlineProgress<ModelDownloadProgress>(updates.Add));
    Assert(result.IsSuccess, result.Failure?.Message ?? "fixed download failed");
    Assert(updates.Select(p => p.BytesReceived).SequenceEqual(updates.Select(p => p.BytesReceived).Order()),
        "progress bytes moved backwards");
    Assert(updates.Where(p => p.Fraction < 1).Select(p => p.BytesReceived).Distinct().Count() == 4,
        "progress reset between artifacts");
});

await CheckAsync("failed directory promotion restores previous install", async () =>
{
    using var scope = new Scope(new StaticHandler([1]), (_, _) => throw new IOException("injected promotion failure"));
    var model = FixedFixture();
    var final = scope.Manager.GetInstalledPath(model);
    Directory.CreateDirectory(final);
    var marker = Path.Combine(final, "previous-install.marker");
    File.WriteAllText(marker, "preserve");
    var result = await scope.Manager.DownloadAsync(model);
    Assert(result.Failure?.Code == ModelManagementError.Storage, "promotion fault was not structured");
    Assert(File.ReadAllText(marker) == "preserve", "previous install was not restored");
    Assert(!Directory.EnumerateDirectories(Path.GetDirectoryName(final)!, "*.replaced").Any(), "backup directory leaked");
});

Console.WriteLine($"{15 - failures}/15 tests passed");
return failures == 0 ? 0 : 1;

async Task CheckAsync(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}

static ManagedModel TestModel(string storageName, byte[] payload)
{
    var hash = Convert.ToHexString(SHA256.HashData(payload));
    return new(storageName, "Fixture", ManagedModelKind.LocalLlm, ManagedModelLayout.SingleFile,
        storageName, payload.Length, false, [],
        [new(storageName, new Uri("https://models.example.test/model"), payload.Length, hash)]);
}

static ManagedModel TreeFixture() => new("tree-fixture", "Tree fixture", ManagedModelKind.Parakeet,
    ManagedModelLayout.HuggingFaceTree, "tree-fixture", 4, false, ["en"], [], "owner/repository");

static ManagedModel FixedFixture()
{
    var source = PortableModelCatalog.Parakeet[0];
    return source with
    {
        Id = "fixed-fixture",
        StorageName = "fixed-fixture",
        ApproximateSizeBytes = 4,
        Artifacts = source.Artifacts.Select(a => a with { ExactSizeBytes = 1, Sha256 = null }).ToArray()
    };
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Scope : IDisposable
{
    public Scope(HttpMessageHandler handler, Action<string, string>? beforeDirectoryPromotion = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"hw-model-tests-{Guid.NewGuid():N}");
        Paths = new TestPaths(Root);
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        Manager = new PortableModelManager(Paths, Client, beforeDirectoryPromotion);
    }
    public string Root { get; }
    public TestPaths Paths { get; }
    public HttpClient Client { get; }
    public PortableModelManager Manager { get; }
    public void Dispose() { Client.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

sealed class TestPaths(string root) : IAppPaths
{
    public string DataDirectory => Path.Combine(root, "data");
    public string ConfigDirectory => Path.Combine(root, "config");
    public string CacheDirectory => Path.Combine(root, "cache");
    public string StateDirectory => Path.Combine(root, "state");
    public string LogsDirectory => Path.Combine(root, "logs");
    public string ModelsDirectory => Path.Combine(root, "models");
    public string RecordingsDirectory => Path.Combine(root, "recordings");
    public string RuntimeDirectory => Path.Combine(root, "runtime");
    public string TemporaryDirectory => Path.Combine(root, "tmp");
}

sealed class StaticHandler(byte[] payload, long? declaredLength = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentLength = declaredLength ?? payload.Length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

sealed class CancellingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new UnreachableException();
    }
}

sealed class TreeHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath.Contains("/api/models/", StringComparison.Ordinal))
        {
            var second = uri.Query.Contains("page=2", StringComparison.Ordinal);
            var json = second
                ? "[{\"type\":\"file\",\"path\":\"decoder.int8.onnx\",\"size\":1},{\"type\":\"file\",\"path\":\"tokenizer/config.json\",\"size\":1}]"
                : "[{\"type\":\"file\",\"path\":\"conv_frontend.onnx\",\"size\":1},{\"type\":\"file\",\"path\":\"encoder.int8.onnx\",\"size\":1}]";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            if (!second)
                response.Headers.TryAddWithoutValidation("Link", "<https://huggingface.co/api/models/owner/repository/tree/main?page=2>; rel=\"next\"");
            return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
    }
}

enum InvalidTreeMode { Cycle, CrossHost, Oversize }

sealed class InvalidTreeHandler(InvalidTreeMode mode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var size = mode == InvalidTreeMode.Oversize ? 6 : 1;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{{\"type\":\"file\",\"path\":\"encoder.int8.onnx\",\"size\":{size}}}]")
        };
        if (mode == InvalidTreeMode.Cycle)
            response.Headers.TryAddWithoutValidation("Link", $"<{request.RequestUri}>; rel=\"next\"");
        else if (mode == InvalidTreeMode.CrossHost)
            response.Headers.TryAddWithoutValidation("Link", "<https://example.invalid/api/models/owner/repository/tree/main?page=2>; rel=\"next\"");
        return Task.FromResult(response);
    }
}

sealed class NoLengthHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(payload)) });
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
