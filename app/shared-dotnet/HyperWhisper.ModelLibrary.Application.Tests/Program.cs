using HyperWhisper.ModelManagement;
using HyperWhisper.ModelReadiness;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
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
    {
        Assert(bundled.Items.Count > PortableModelCatalog.All.Count
            && bundled.Items.Any(item => item.Deployment == nameof(ModelDeployment.Cloud)),
            "default construction did not load the bundled unified catalog");
        // The streaming capabilities are still loaded and still selectable, but they are NOT rows
        // in the table: Windows builds that table from four blocks and none has a streaming
        // surface, so a streaming row there read as a duplicate of the batch row for the same
        // model (Parakeet v2, Parakeet v3 and Nemotron 3.5 Streaming each appeared twice).
        Assert(!bundled.Items.Any(item => item.Capability.Surface == ModelSurface.StreamingTranscription),
            "a streaming capability leaked into the library table");
        var localLive = bundled.StreamingItems.Where(item => item.Capability.Deployment == ModelDeployment.Local
            && item.Capability.Surface == ModelSurface.StreamingTranscription).ToArray();
        Assert(localLive.Length == 3
            && localLive.Count(item => item.ProviderId == "parakeetLocal") == 2
            && localLive.Single(item => item.ProviderId == "nemotronLocal").Capability.SupportedLanguages.Count == 32,
            "local live model rows or Nemotron production locales were not exposed");
        // No local model may appear twice. Identity is the model id, not the display name:
        // Windows shows "Medium" twice on purpose, for whisper medium and medium.en, and tells
        // them apart with the EN tag. What it never does is list one model id twice, which is
        // what the batch + streaming capability pair did for the three streaming-capable local
        // models. Windows draws exactly 20 offline rows, one per local catalog model.
        Assert(bundled.Items.Count(item => item.IsLocal) == PortableModelCatalog.All.Count,
            "the offline row count no longer matches the local catalog, one row per model");
        var duplicateIds = bundled.Items.Where(item => item.IsLocal).GroupBy(item => item.ModelId)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert(duplicateIds.Length == 0,
            "the library table repeats a local model id: " + string.Join(", ", duplicateIds));
    }
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
    // Two of the three fixture capabilities are table rows. The third has a streaming surface,
    // and Windows draws no streaming row at all, so it is reachable through StreamingItems
    // instead of the table.
    Assert(viewModel.Items.Count == 2, "unified local/cloud rows were not exposed");
    Assert(viewModel.StreamingItems.Count == 1,
        "the streaming capability was dropped instead of moved off the table");

    var localRow = viewModel.Items.Single(item => item.Id == "local/localWhisper/base");
    Assert(localRow.RuntimeBadge == "Local · whisper.cpp"
        && localRow.Readiness == ReadinessState.Downloadable
        && localRow.Size.Contains("147", StringComparison.Ordinal),
        "local runtime, readiness, or size metadata was not exposed");

    var cloudRow = viewModel.Items.Single(item => item.ProviderId == "groq");
    Assert(cloudRow.CredentialNavigationActionId == "navigate.credentials:GroqApiKey"
        && cloudRow.AccountNavigationActionId is null && cloudRow.Model is null,
        "BYOK credential navigation action was not provider-scoped");
    var accountRow = viewModel.StreamingItems.Single(item => item.ProviderId == "hyperwhisper");
    Assert(accountRow.CredentialNavigationActionId == "navigate.account"
        && accountRow.AccountNavigationActionId == "navigate.account",
        "cloud-tier account navigation action was not exposed");

    viewModel.SearchText = "groq";
    Assert(viewModel.Items.Count == 1 && viewModel.Items[0] == cloudRow,
        "provider search did not filter unified rows");
    viewModel.SearchText = string.Empty;
    viewModel.DeploymentFilter = ModelDeployment.Cloud;
    viewModel.SurfaceFilter = ModelSurface.BatchTranscription;
    Assert(viewModel.Items.Count == 1 && viewModel.Items[0] == cloudRow,
        "deployment and surface filters were not composed");
    // A surface filter cannot conjure a streaming row back into the table.
    viewModel.SurfaceFilter = ModelSurface.StreamingTranscription;
    Assert(viewModel.Items.Count == 0,
        "filtering by the streaming surface put a streaming row back in the table");
    viewModel.DeploymentFilter = null;
    viewModel.SurfaceFilter = null;
    viewModel.Sort = ModelLibrarySort.Name;
    Assert(viewModel.Items.Select(item => item.DisplayName).SequenceEqual(
        viewModel.Items.Select(item => item.DisplayName).Order(StringComparer.OrdinalIgnoreCase)),
        "name sorting was not deterministic");

    var liveCapability = new ModelCapability(
        "local/streaming/parakeetLocal/base", "Whisper Base Live Fixture", "parakeetLocal", "base",
        ModelDeployment.Local, ModelWorkload.Voice, ModelSurface.StreamingTranscription,
        false, false, ["en"], true, Runtime: "test", RequiresCredential: false);
    var privateFiles = new MemoryPrivateFiles();
    var settingsPath = Path.Combine(root, "settings.json");
    var settings = new SettingsViewModel(new PortableSettingsService(privateFiles, settingsPath))
        { StreamingLanguage = "fr" };
    using (var liveViewModel = new ModelLibraryViewModel(manager, capabilities: [liveCapability], streamingSettings: settings))
    {
        // The library no longer pre-selects a row, the way Windows does not, so a test that
        // exercises the selected-row action picks its row first.
        // Streaming capabilities are not rows in the Windows-shaped table (Windows draws none),
        // so the live action picks its row from StreamingItems rather than Items.
        Assert(liveViewModel.Items.Count == 0, "a streaming capability leaked into the library table");
        liveViewModel.Selected = liveViewModel.StreamingItems[0];
        liveViewModel.Selected!.Installed = true;
        await liveViewModel.UseForLiveStreamingAsync();
        Assert(settings.StreamingEnabled && settings.StreamingProvider == "parakeetLocal"
            && settings.StreamingModel == "base" && settings.StreamingLanguage == "auto",
            "local live model action did not configure provider, model, or safe language fallback");
        var persisted = new SettingsViewModel(new PortableSettingsService(privateFiles, settingsPath));
        persisted.Load();
        Assert(persisted.StreamingEnabled && persisted.StreamingProvider == "parakeetLocal"
            && persisted.StreamingModel == "base" && persisted.StreamingLanguage == "auto",
            "local live model action was not durable across settings reload");
    }

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
        // Not Items[0]: the recommended order ties cloud rows ahead of local ones, the way
        // Windows falls back to its own cloud-first input order, so the first row is a cloud
        // row with no downloadable model behind it. This test needs a row that can download.
        concurrencyViewModel.Selected ??= concurrencyViewModel.Items.First(item => item.CanDownload);
        var downloadTarget = concurrencyViewModel.Selected!;
        var download = concurrencyViewModel.DownloadAsync();
        await concurrencyHandler.Started.Task;
        // Same reason as above: the next row in recommended order is a cloud metadata row, and
        // this check is about a SECOND downloadable row, not about whatever sorts next.
        var otherRow = concurrencyViewModel.Items.First(
            item => item.Id != downloadTarget.Id && item.Model is not null);
        Assert(otherRow.Status == "Not installed",
            "a second local download row was not available in the recommended order");
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

sealed class MemoryPrivateFiles : IPrivateFileService
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    { _files[path] = contents.ToArray(); return PlatformResult.Success(); }
    public PlatformResult WriteAllTextAtomically(string path, string contents)
        => WriteAllBytesAtomically(path, System.Text.Encoding.UTF8.GetBytes(contents));
    public PlatformResult<byte[]?> ReadAllBytes(string path)
        => PlatformResult<byte[]?>.Success(_files.TryGetValue(path, out var bytes) ? bytes.ToArray() : null);
    public PlatformResult<string?> ReadAllText(string path)
        => PlatformResult<string?>.Success(_files.TryGetValue(path, out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes) : null);
    public PlatformResult Delete(string path) { _files.Remove(path); return PlatformResult.Success(); }
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => PlatformResult<bool>.Success(true);
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
