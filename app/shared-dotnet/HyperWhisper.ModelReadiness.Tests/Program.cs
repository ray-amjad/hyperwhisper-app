using System.Reflection;
using System.Text;
using System.Text.Json;
using HyperWhisper.ModelManagement;
using HyperWhisper.ModelReadiness;

var tests = new (string Name, Func<Task> Run)[]
{
    ("local catalog maps every managed model", TestLocalCatalogAsync),
    ("cloud STT maps every provider model", TestCloudSttCoverageAsync),
    ("streaming catalog maps every supported provider", TestStreamingCoverageAsync),
    ("post-processing maps shared model catalogs", TestPostProcessingCoverageAsync),
    ("custom endpoints are isolated rows", TestCustomEndpointsAsync),
    ("missing credential never invokes probe", TestMissingCredentialAsync),
    ("health outcomes and checking state map exactly", TestHealthStatesAsync),
    ("timeout and transport failure are bounded", TestFailuresAsync),
    ("health diagnostics redact and bound secrets", TestRedactionAsync),
    ("health request cannot carry user content", TestRequestSurfaceAsync),
    ("credential lookup is provider scoped", TestCredentialScopeAsync),
    ("credential change hook is scoped", TestCredentialChangeAsync),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"Model readiness: {tests.Length}/{tests.Length} tests passed.");

static IReadOnlyList<ModelCapability> Load(CustomEndpointDefinition[]? custom = null) =>
    UnifiedModelCatalog.LoadBundled(custom);

static Task TestLocalCatalogAsync()
{
    var rows = Load().Where(x => x.Deployment == ModelDeployment.Local).ToArray();
    Equal(PortableModelCatalog.All.Count, rows.Length);
    foreach (var model in PortableModelCatalog.All)
    {
        var row = rows.Single(x => x.ModelId == model.Id);
        Equal(model.ApproximateSizeBytes, row.ApproximateSizeBytes);
        Equal(model.RecommendedVramBytes, row.RecommendedVramBytes);
        True(row.Runtime is "whisper.cpp" or "sherpa-onnx" or "llama.cpp");
        Equal(model.IsEnglishOnly, row.IsEnglishOnly);
        True(!row.RequiresCredential);
    }
    True(rows.Single(x => x.ModelId.StartsWith("nemotron-", StringComparison.Ordinal)).SupportsStreaming);
    return Task.CompletedTask;
}

static Task TestCloudSttCoverageAsync()
{
    using var json = OpenCatalog("cloud-stt-catalog.json");
    using var doc = JsonDocument.Parse(json);
    var expected = doc.RootElement.GetProperty("providers").EnumerateArray()
        .Sum(provider => provider.GetProperty("models").GetArrayLength());
    var rows = Load().Where(x => x.Surface == ModelSurface.BatchTranscription
        && x.Deployment == ModelDeployment.Cloud).ToArray();
    Equal(expected, rows.Length);
    foreach (var row in rows)
    {
        True(row.Workload == ModelWorkload.Voice && row.CredentialAccount is not null);
        True(row.CloudTierEligible || row.ByokEligible);
    }
    return Task.CompletedTask;
}

static Task TestStreamingCoverageAsync()
{
    using var json = OpenCatalog("cloud-stt-catalog.json");
    using var doc = JsonDocument.Parse(json);
    var catalogProviders = doc.RootElement.GetProperty("providers").EnumerateArray()
        .Where(provider => provider.GetProperty("features").GetProperty("streaming").GetBoolean())
        .Select(provider => provider.GetProperty("sttProvider").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var rows = Load().Where(x => x.Surface == ModelSurface.StreamingTranscription).ToArray();
    foreach (var provider in catalogProviders) True(rows.Any(x => x.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase)));
    foreach (var provider in new[] { "deepgram", "elevenlabs", "openai", "grok", "hyperwhisper" })
        True(rows.Any(x => x.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase)));
    True(rows.All(x => x.SupportsStreaming && x.Workload == ModelWorkload.Voice));
    return Task.CompletedTask;
}

static Task TestPostProcessingCoverageAsync()
{
    var rows = Load().Where(x => x.Surface == ModelSurface.PostProcessing && x.Deployment == ModelDeployment.Cloud).ToArray();
    using var modelsJson = OpenCatalog("models-catalog.json");
    using var models = JsonDocument.Parse(modelsJson);
    var expectedByok = models.RootElement.GetProperty("models").EnumerateArray()
        .Count(x => x.GetProperty("kind").GetString() == "text" && x.GetProperty("provider").GetString() != "localLLM");
    using var ppJson = OpenCatalog("cloud-pp-catalog.json");
    using var pp = JsonDocument.Parse(ppJson);
    var expectedTier = pp.RootElement.GetProperty("providers").EnumerateArray()
        .Where(p => !p.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean())
        .Sum(p => p.GetProperty("models").EnumerateArray().Count(m => !m.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean()));
    Equal(expectedByok, rows.Count(x => x.Key.StartsWith("cloud/pp-byok/", StringComparison.Ordinal)));
    Equal(expectedTier, rows.Count(x => x.Key.StartsWith("cloud/pp-tier/", StringComparison.Ordinal)));
    True(rows.All(x => x.Workload == ModelWorkload.Text));
    return Task.CompletedTask;
}

static Task TestCustomEndpointsAsync()
{
    var id = Guid.NewGuid();
    var rows = Load([new(id, "Loopback", new Uri("http://127.0.0.1:11434/v1/chat/completions"), "model", $"CustomEndpoint_{id:D}")]);
    var row = rows.Single(x => x.Surface == ModelSurface.CustomEndpoint);
    Equal(id.ToString("D"), row.Key["custom/".Length..]);
    Equal("CustomEndpoint_" + id.ToString("D"), row.CredentialAccount);
    Throws<InvalidDataException>(() => Load([new(Guid.NewGuid(), "Bad", new Uri("file:///tmp/model"), "m", "account")]));
    var noAuth = Load([new(Guid.NewGuid(), "No auth", new Uri("http://localhost:11434/v1/models"), "m", "", false)])
        .Single(x => x.Surface == ModelSurface.CustomEndpoint);
    True(!noAuth.RequiresCredential);
    return Task.CompletedTask;
}

static async Task TestMissingCredentialAsync()
{
    var probe = new FakeProbe();
    var service = Service(new FakeCredentials(), probe);
    var result = await service.CheckAsync(CloudRow());
    Equal(ReadinessState.MissingCredential, result.State);
    Equal(0, probe.Requests.Count);

    var endpoint = Load([new(Guid.NewGuid(), "No auth", new Uri("http://localhost:11434/v1/models"), "m", "", false)])
        .Single(x => x.Surface == ModelSurface.CustomEndpoint);
    result = await service.CheckAsync(endpoint);
    Equal(ReadinessState.Healthy, result.State);
    Equal(1, probe.Requests.Count);
    True(!probe.Requests[0].Credential.IsPresent);
}

static async Task TestHealthStatesAsync()
{
    foreach (var item in new[]
    {
        (ProviderHealthOutcome.Healthy, ReadinessState.Healthy),
        (ProviderHealthOutcome.Unauthorized, ReadinessState.Unauthorized),
        (ProviderHealthOutcome.Unreachable, ReadinessState.Unreachable),
        (ProviderHealthOutcome.Unsupported, ReadinessState.Unsupported),
    })
    {
        var probe = new FakeProbe { Response = new(item.Item1) };
        var service = Service(new FakeCredentials(("OpenAIApiKey", "secret")), probe);
        var states = new List<ReadinessState>();
        service.ReadinessChanged += (_, args) => states.Add(args.Readiness.State);
        var result = await service.CheckAsync(CloudRow());
        Equal(item.Item2, result.State);
        Equal(ReadinessState.Checking, states[^2]);
        Equal(item.Item2, states[^1]);
    }
    var localService = Service(new FakeCredentials(), new FakeProbe(), new FakeLocal(true));
    Equal(ReadinessState.Installed, (await localService.CheckAsync(Load().First(x => x.Deployment == ModelDeployment.Local))).State);
}

static async Task TestFailuresAsync()
{
    var credentials = new FakeCredentials(("OpenAIApiKey", "secret"));
    var slow = new FakeProbe { WaitForCancellation = true };
    var service = Service(credentials, slow, timeout: TimeSpan.FromMilliseconds(20));
    Equal(ReadinessState.Unreachable, (await service.CheckAsync(CloudRow())).State);
    var broken = new FakeProbe { Error = new HttpRequestException("host secret details") };
    var failed = await Service(credentials, broken).CheckAsync(CloudRow());
    Equal(ReadinessState.Unreachable, failed.State);
    Equal("Provider could not be reached.", failed.Detail);
}

static async Task TestRedactionAsync()
{
    const string secret = "top-secret-value";
    var probe = new FakeProbe { Response = new(ProviderHealthOutcome.Unauthorized, $"Rejected {secret}") };
    var result = await Service(new FakeCredentials(("OpenAIApiKey", secret)), probe).CheckAsync(CloudRow());
    True(result.Detail == "Rejected [redacted]" && !result.Detail.Contains(secret, StringComparison.Ordinal));
    probe.Response = new(ProviderHealthOutcome.Unreachable, new string('x', ProviderHealthResponse.MaximumDetailBytes + 1));
    result = await Service(new FakeCredentials(("OpenAIApiKey", secret)), probe).CheckAsync(CloudRow());
    Equal("Provider returned an oversized health response.", result.Detail);
    Equal("[redacted]", new ProviderCredential(secret).ToString());
}

static Task TestRequestSurfaceAsync()
{
    var names = typeof(ProviderHealthRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var forbidden in new[] { "Audio", "Transcript", "Text", "Prompt", "Vocabulary", "Credentials", "SystemInfo" })
        True(!names.Contains(forbidden));
    Equal(5, names.Count);
    return Task.CompletedTask;
}

static async Task TestCredentialScopeAsync()
{
    var credentials = new FakeCredentials(("OpenAIApiKey", "only-this-secret"), ("AnthropicApiKey", "never-read"));
    var probe = new FakeProbe();
    await Service(credentials, probe).CheckAsync(CloudRow());
    Equal(1, credentials.Requested.Count);
    Equal("OpenAIApiKey", credentials.Requested.Single());
    Equal("only-this-secret", probe.Requests.Single().Credential.Value);
}

static Task TestCredentialChangeAsync()
{
    var service = Service(new FakeCredentials(), new FakeProbe());
    string? changed = null;
    service.CredentialInvalidated += (_, account) => changed = account;
    service.NotifyCredentialChanged("GroqApiKey");
    Equal("GroqApiKey", changed);
    Throws<ArgumentException>(() => service.NotifyCredentialChanged(" "));
    return Task.CompletedTask;
}

static ModelCapability CloudRow() => Load().First(x => x.Key.StartsWith("cloud/stt/", StringComparison.Ordinal)
    && x.CredentialAccount == "OpenAIApiKey");

static ModelReadinessService Service(FakeCredentials credentials, FakeProbe probe,
    FakeLocal? local = null, TimeSpan? timeout = null) =>
    new(credentials, probe, local ?? new FakeLocal(false), timeout);

static FileStream OpenCatalog(string name) => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Catalogs", name));

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Assertion failed.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FakeCredentials(params (string Account, string Value)[] values) : IProviderCredentialSource
{
    private readonly Dictionary<string, string> _values = values.ToDictionary(x => x.Account, x => x.Value, StringComparer.Ordinal);
    public List<string> Requested { get; } = [];
    public ValueTask<ProviderCredential?> GetCredentialAsync(string account, CancellationToken cancellationToken = default)
    {
        Requested.Add(account);
        return ValueTask.FromResult(_values.TryGetValue(account, out var value) ? new ProviderCredential(value) : null);
    }
}

sealed class FakeProbe : IProviderHealthProbe
{
    public ProviderHealthResponse Response { get; set; } = new(ProviderHealthOutcome.Healthy);
    public Exception? Error { get; set; }
    public bool WaitForCancellation { get; set; }
    public List<ProviderHealthRequest> Requests { get; } = [];
    public async ValueTask<ProviderHealthResponse> CheckAsync(ProviderHealthRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Error is not null) throw Error;
        if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return Response;
    }
}

sealed class FakeLocal(bool installed) : ILocalModelReadinessSource
{
    public ValueTask<bool> IsInstalledAsync(ModelCapability model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(installed);
}
