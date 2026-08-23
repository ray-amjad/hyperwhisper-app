using HyperWhisper.ModelManagement;
using HyperWhisper.ModelReadiness;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.ViewModels;

var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-model-library-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var paths = new TestPaths(root);
    var manager = new PortableModelManager(paths, new HttpClient(new RejectNetworkHandler()));
    var credentials = new TestCredentials();
    var probe = new CountingProbe();
    var local = new CountingLocalSource();
    var readiness = new ModelReadinessService(credentials, probe, local);
    using (var bundled = new ModelLibraryViewModel(manager))
        Assert(bundled.Items.Count > PortableModelCatalog.All.Count
            && bundled.Items.Any(item => item.Deployment == nameof(ModelDeployment.Cloud)),
            "default construction did not load the bundled unified catalog");
    var capabilities = new ModelCapability[]
    {
        new("local/localWhisper/base", "Whisper Base", "localWhisper", "base",
            ModelDeployment.Local, ModelWorkload.Voice, ModelSurface.BatchTranscription,
            true, true, [], false, Runtime: "whisper.cpp", ApproximateSizeBytes: 147_000_000,
            RequiresCredential: false),
        new("cloud/stt/groq/whisper", "Groq Whisper", "groq", "base",
            ModelDeployment.Cloud, ModelWorkload.Voice, ModelSurface.BatchTranscription,
            true, true, [], false, ByokEligible: true, CredentialAccount: "GroqApiKey"),
        new("cloud/streaming/hyperwhisper", "HyperWhisper Live", "hyperwhisper", "default",
            ModelDeployment.Cloud, ModelWorkload.Voice, ModelSurface.StreamingTranscription,
            true, true, [], true, CloudTierEligible: true, CredentialAccount: "LicenseKey"),
    };

    using var viewModel = new ModelLibraryViewModel(manager, readiness, capabilities);
    Assert(probe.CheckCount == 0 && local.CheckCount == 0,
        "constructing the model library performed an implicit readiness or network probe");
    Assert(viewModel.Items.Count == 3, "unified local/cloud rows were not exposed");

    var localRow = viewModel.Items.Single(item => item.Id == "local/localWhisper/base");
    Assert(localRow.RuntimeBadge == "Local · whisper.cpp"
        && localRow.Readiness == ReadinessState.Downloadable
        && localRow.Size.Contains("147", StringComparison.Ordinal),
        "local runtime, readiness, or size metadata was not exposed");

    var cloudRow = viewModel.Items.Single(item => item.ProviderId == "groq");
    Assert(cloudRow.CredentialNavigationActionId == "navigate.credentials:GroqApiKey"
        && cloudRow.AccountNavigationActionId is null && cloudRow.Model is null,
        "BYOK credential navigation action was not provider-scoped");
    var accountRow = viewModel.Items.Single(item => item.ProviderId == "hyperwhisper");
    Assert(accountRow.CredentialNavigationActionId == "navigate.account"
        && accountRow.AccountNavigationActionId == "navigate.account",
        "cloud-tier account navigation action was not exposed");

    viewModel.SearchText = "groq";
    Assert(viewModel.Items.Count == 1 && viewModel.Items[0] == cloudRow,
        "provider search did not filter unified rows");
    viewModel.SearchText = string.Empty;
    viewModel.DeploymentFilter = ModelDeployment.Cloud;
    viewModel.SurfaceFilter = ModelSurface.StreamingTranscription;
    Assert(viewModel.Items.Count == 1 && viewModel.Items[0] == accountRow,
        "deployment and surface filters were not composed");
    viewModel.DeploymentFilter = null;
    viewModel.SurfaceFilter = null;
    viewModel.Sort = ModelLibrarySort.Name;
    Assert(viewModel.Items.Select(item => item.DisplayName).SequenceEqual(
        viewModel.Items.Select(item => item.DisplayName).Order(StringComparer.OrdinalIgnoreCase)),
        "name sorting was not deterministic");

    viewModel.Selected = cloudRow;
    await viewModel.RefreshSelectedReadinessAsync();
    Assert(probe.CheckCount == 0 && cloudRow.Readiness == ReadinessState.MissingCredential,
        "missing credential readiness invoked a provider or produced the wrong state");

    credentials.Credential = new ProviderCredential("test-value-not-logged");
    await viewModel.RefreshSelectedReadinessAsync();
    Assert(probe.CheckCount == 1 && cloudRow.Readiness == ReadinessState.Healthy,
        "explicit provider readiness refresh did not update the selected row");
    readiness.NotifyCredentialChanged("GroqApiKey");
    Assert(probe.CheckCount == 1 && cloudRow.Readiness == ReadinessState.Unknown
        && cloudRow.Status.Contains("refresh", StringComparison.OrdinalIgnoreCase),
        "credential invalidation did not refresh row state without an implicit network probe");

    viewModel.Selected = localRow;
    await viewModel.RefreshSelectedReadinessAsync();
    Assert(local.CheckCount == 1 && probe.CheckCount == 1
        && localRow.Readiness == ReadinessState.Installed,
        "local readiness did not use the local adapter independently of provider health");

    var concurrencyRoot = Path.Combine(root, "concurrency");
    var concurrencyHandler = new BlockingNetworkHandler();
    var concurrencyManager = new PortableModelManager(
        new TestPaths(concurrencyRoot), new HttpClient(concurrencyHandler));
    using (var concurrencyViewModel = new ModelLibraryViewModel(concurrencyManager))
    {
        var downloadTarget = concurrencyViewModel.Selected!;
        var download = concurrencyViewModel.DownloadAsync();
        await concurrencyHandler.Started.Task;
        var otherRow = concurrencyViewModel.Items.First(item => item.Id != downloadTarget.Id);
        Assert(otherRow.Model is not null && otherRow.Status == "Not installed",
            "recommended ordering did not preserve local download rows ahead of cloud metadata rows");
        concurrencyViewModel.Selected = otherRow;
        concurrencyViewModel.Dispose();
        await download;
        Assert(downloadTarget.Status.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            && otherRow.Status == "Not installed" && !otherRow.Installed,
            "selection change redirected an in-flight download result to another unified row");
    }

    Console.WriteLine("Model library application tests passed (13/13).");
}
finally
{
    Directory.Delete(root, true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestCredentials : IProviderCredentialSource
{
    public ProviderCredential? Credential { get; set; }
    public ValueTask<ProviderCredential?> GetCredentialAsync(string account, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Credential);
}

sealed class CountingProbe : IProviderHealthProbe
{
    public int CheckCount { get; private set; }
    public ValueTask<ProviderHealthResponse> CheckAsync(ProviderHealthRequest request, CancellationToken cancellationToken = default)
    {
        CheckCount++;
        return ValueTask.FromResult(new ProviderHealthResponse(ProviderHealthOutcome.Healthy));
    }
}

sealed class CountingLocalSource : ILocalModelReadinessSource
{
    public int CheckCount { get; private set; }
    public ValueTask<bool> IsInstalledAsync(ModelCapability model, CancellationToken cancellationToken = default)
    {
        CheckCount++;
        return ValueTask.FromResult(true);
    }
}

sealed class RejectNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Unexpected network access.");
}

sealed class BlockingNetworkHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable blocking model response.");
    }
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
