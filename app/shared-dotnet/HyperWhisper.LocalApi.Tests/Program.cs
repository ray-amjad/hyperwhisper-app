using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HyperWhisper.LocalApi;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var tests = new (string Name, Func<Task> Run)[]
{
    ("route parity", RouteParity),
    ("bearer auth and public health", Auth),
    ("bounded multipart transcription", Multipart),
    ("path traversal rejected", Traversal),
    ("request cancellation propagates", Cancellation),
    ("health reports actual bound port", HealthReportsBoundPort),
    ("bind fallback", BindFallback),
    ("private token persistence", TokenPersistence),
    ("real loopback lifecycle", RealLoopbackLifecycle),
    ("real occupied-port fallback", RealOccupiedPortFallback),
    ("fallback failure is structured", FallbackFailure),
    ("fallback cancellation is structured", FallbackCancellation),
    ("cancelled stop cleans discovery", CancelledStopCleanup),
    ("double dispose is safe", DoubleDispose)
    ,("application backend errors are structured", ApplicationBackendErrors)
    ,("shutdown failure still cleans discovery", ShutdownFailureCleanup)
    ,("failed-start cleanup survives shutdown failure", FailedStartCleanup)
    ,("host options validate eagerly", HostOptionsValidation)
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length}/{tests.Length} portable Local API tests passed.");

static async Task RouteParity()
{
    await using var fixture = await Fixture.Create();
    var endpoints = fixture.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
        .Select(x => x.RoutePattern.RawText).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
    string[] expected = ["/health", "/models", "/modes", "/modes/{id}", "/recording/toggle", "/recording/cancel", "/transcribe", "/post-process", "/recordings", "/recordings/search", "/recordings/{id}"];
    foreach (var route in expected) Assert(endpoints.Contains(route), $"missing route {route}");
}

static async Task Auth()
{
    await using var fixture = await Fixture.Create();
    Assert((await fixture.Client.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "health must be public");
    Assert((await fixture.Client.GetAsync("/models")).StatusCode == HttpStatusCode.Unauthorized, "missing bearer accepted");
    fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong");
    Assert((await fixture.Client.GetAsync("/models")).StatusCode == HttpStatusCode.Unauthorized, "wrong bearer accepted");
    fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", Fixture.Token);
    Assert((await fixture.Client.GetAsync("/models")).StatusCode == HttpStatusCode.OK, "valid bearer rejected");
}

static async Task Multipart()
{
    await using var fixture = await Fixture.Create(maxUpload: 4);
    fixture.Authenticate();
    using var okay = new MultipartFormDataContent();
    okay.Add(new ByteArrayContent([1, 2, 3, 4]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "audio", "clip.wav");
    Assert((await fixture.Client.PostAsync("/transcribe", okay)).StatusCode == HttpStatusCode.OK, "valid upload failed");
    Assert(fixture.Backend.Upload?.Content.Length == 4, "backend did not receive bounded upload");
    using var large = new MultipartFormDataContent();
    large.Add(new ByteArrayContent([1, 2, 3, 4, 5]), "audio", "clip.wav");
    Assert((await fixture.Client.PostAsync("/transcribe", large)).StatusCode == HttpStatusCode.RequestEntityTooLarge, "large upload accepted");
}

static async Task Traversal()
{
    await using var fixture = await Fixture.Create();
    fixture.Authenticate();
    using var form = new MultipartFormDataContent();
    form.Add(new ByteArrayContent([1]), "audio", "../secret.wav");
    Assert((await fixture.Client.PostAsync("/transcribe", form)).StatusCode == HttpStatusCode.BadRequest, "traversal filename accepted");
    using var windowsForm = new MultipartFormDataContent();
    windowsForm.Add(new ByteArrayContent([1]), "audio", "directory\\secret.wav");
    Assert((await fixture.Client.PostAsync("/transcribe", windowsForm)).StatusCode == HttpStatusCode.BadRequest, "Windows path filename accepted");
}

static async Task Cancellation()
{
    var backend = new FakeBackend { BlockTranscription = true };
    await using var fixture = await Fixture.Create(backend: backend);
    fixture.Authenticate();
    using var form = new MultipartFormDataContent();
    form.Add(new ByteArrayContent([1]), "audio", "clip.wav");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try { _ = await fixture.Client.PostAsync("/transcribe", form, cts.Token); } catch (OperationCanceledException) { }
    await backend.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
}

static async Task BindFallback()
{
    var attempts = new List<int>();
    var result = await LocalApiBindFallback.BindAsync(1234, (port, _) =>
    {
        attempts.Add(port);
        return port == 1234 ? Task.FromException(new SocketException((int)SocketError.AddressAlreadyInUse)) : Task.CompletedTask;
    });
    Assert(result == 0 && attempts.SequenceEqual([1234, 0]), "did not fall back to ephemeral loopback port");
}

static async Task HealthReportsBoundPort()
{
    await using var app = PortableLocalApi.Build([], new PortableLocalApiOptions(Fixture.Token, 0), new FakeBackend());
    await app.StartAsync();
    var address = new Uri(app.Urls.Single());
    using var client = new HttpClient { BaseAddress = address };
    using var response = await client.GetAsync("/health");
    using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    Assert(address.Port != 0 && body.RootElement.GetProperty("port").GetInt32() == address.Port,
        "health returned the configured sentinel instead of the actual listener port");
}

static Task TokenPersistence()
{
    var files = new FakePrivateFiles();
    var store = new LocalApiTokenStore(files, "/config/local-api-token");
    var token = store.LoadOrCreate();
    Assert(token.Length == 43 && files.Restricted, "token was not generated into private storage");
    Assert(store.LoadOrCreate() == token, "stored token was not reused");
    files.Value = "weak";
    Assert(store.LoadOrCreate() != "weak", "malformed token was reused");
    return Task.CompletedTask;
}

static async Task RealLoopbackLifecycle()
{
    if (!OperatingSystem.IsLinux()) return;
    using var temp = new TempPaths();
    var files = new DiskPrivateFiles();
    await using var host = new PortableLocalApiHost(files, temp, new FakeBackend(), "1.2.3", preferredPort: 0);
    var state = await host.StartAsync();
    Assert(state.IsRunning && state.Port > 0, "real host did not start");
    var address = new Uri(state.BaseAddress!);
    Assert(address.Host == IPAddress.Loopback.ToString(), "listener was not IPv4 loopback-only");
    Assert(File.Exists(host.DiscoveryPath), "discovery was not written");
    Assert((File.GetUnixFileMode(host.DiscoveryPath) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "discovery is not 0600-equivalent");
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(host.DiscoveryPath));
    var root = document.RootElement;
    Assert(root.GetProperty("port").GetInt32() == state.Port, "discovery has wrong port");
    Assert(root.GetProperty("pid").GetInt32() == Environment.ProcessId, "discovery has wrong pid");
    Assert(root.GetProperty("app_version").GetString() == "1.2.3", "discovery has wrong version");
    var token = root.GetProperty("token").GetString()!;
    Assert(token.Length == 43, "discovery token is malformed");
    using var client = new HttpClient { BaseAddress = address };
    Assert((await client.GetAsync("/models")).StatusCode == HttpStatusCode.Unauthorized, "real listener accepted unauthenticated request");
    client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    Assert((await client.GetAsync("/models")).StatusCode == HttpStatusCode.OK, "real listener rejected discovery token");
    Assert((await host.StartAsync()).Port == state.Port, "start was not idempotent");
    Assert(!(await host.StopAsync()).IsRunning && !File.Exists(host.DiscoveryPath), "stop did not clean discovery");
    Assert(!(await host.StopAsync()).IsRunning, "stop was not idempotent");
}

static async Task RealOccupiedPortFallback()
{
    using var occupied = new TcpListener(IPAddress.Loopback, 0);
    occupied.Start();
    var preferred = ((IPEndPoint)occupied.LocalEndpoint).Port;
    using var temp = new TempPaths();
    await using var host = new PortableLocalApiHost(new DiskPrivateFiles(), temp, new FakeBackend(), "1.0", preferred);
    var state = await host.StartAsync();
    Assert(state.IsRunning && state.Port != preferred, "occupied preferred port did not fall back");
}

static async Task FallbackFailure()
{
    using var temp = new TempPaths();
    var calls = 0;
    await using var host = new PortableLocalApiHost(new DiskPrivateFiles(), temp, new FakeBackend(), "1.0", 1234, applicationStarter: (_, _, _) =>
    {
        calls++;
        return calls == 1
            ? Task.FromException<Microsoft.AspNetCore.Builder.WebApplication>(new SocketException((int)SocketError.AddressAlreadyInUse))
            : Task.FromException<Microsoft.AspNetCore.Builder.WebApplication>(new InvalidOperationException("fallback failed"));
    });
    var state = await host.StartAsync();
    Assert(!state.IsRunning && state.Failure?.Code == "local_api.bind" && calls == 2, "fallback failure escaped structured state");
}

static async Task FallbackCancellation()
{
    using var temp = new TempPaths();
    using var cts = new CancellationTokenSource();
    var calls = 0;
    await using var host = new PortableLocalApiHost(new DiskPrivateFiles(), temp, new FakeBackend(), "1.0", 1234, applicationStarter: (_, _, _) =>
    {
        calls++;
        if (calls == 1) return Task.FromException<Microsoft.AspNetCore.Builder.WebApplication>(new SocketException((int)SocketError.AddressAlreadyInUse));
        cts.Cancel();
        return Task.FromCanceled<Microsoft.AspNetCore.Builder.WebApplication>(cts.Token);
    });
    var state = await host.StartAsync(cts.Token);
    Assert(!state.IsRunning && state.Failure?.Code == "local_api.cancelled" && calls == 2, "fallback cancellation escaped structured state");
}

static async Task CancelledStopCleanup()
{
    using var temp = new TempPaths();
    await using var host = new PortableLocalApiHost(new DiskPrivateFiles(), temp, new FakeBackend(), "1.0", 0);
    Assert((await host.StartAsync()).IsRunning, "host did not start");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var state = await host.StopAsync(cts.Token);
    Assert(state.Failure?.Code == "local_api.cancelled" && !File.Exists(host.DiscoveryPath), "cancelled stop left discovery behind");
}

static async Task DoubleDispose()
{
    using var temp = new TempPaths();
    var host = new PortableLocalApiHost(new DiskPrivateFiles(), temp, new FakeBackend(), "1.0", 0);
    Assert((await host.StartAsync()).IsRunning, "host did not start");
    await host.DisposeAsync();
    await host.DisposeAsync();
    Assert(!File.Exists(host.DiscoveryPath), "double dispose left discovery behind");
}

static async Task ApplicationBackendErrors()
{
    using var paths = new TempPaths();
    var database = new ApplicationDb(paths);
    await using (var context = database.CreateContext()) await context.Database.EnsureCreatedAsync();
    var history = new HistoryRepository(database);
    var modes = new ModeRepository(database);
    using var workflow = new TranscriptionWorkflow(new NoRecorder(), new NoDevices(), new UnavailableTranscriber(), history);
    var backend = new ApplicationLocalApiBackend(modes, history, workflow, new EmptyCatalog(), new DiskPrivateFiles(), paths, "1.0");
    await using var fixture = await Fixture.Create(backend: backend);
    fixture.Authenticate();

    using var invalid = new StringContent("{", Encoding.UTF8, "application/json");
    var invalidResponse = await fixture.Client.PostAsync("/modes", invalid);
    Assert(invalidResponse.StatusCode == HttpStatusCode.BadRequest && await HasFailureEnvelope(invalidResponse), "invalid JSON did not return LocalApiFailure");
    using var unknownField = new StringContent("{\"name\":\"Bad\",\"notAField\":1}", Encoding.UTF8, "application/json");
    var unknownResponse = await fixture.Client.PostAsync("/modes", unknownField);
    Assert(unknownResponse.StatusCode == HttpStatusCode.BadRequest && await HasFailureEnvelope(unknownResponse), "unknown mode field did not return LocalApiFailure");

    using var first = new StringContent("{\"name\":\"Only\"}", Encoding.UTF8, "application/json");
    Assert((await fixture.Client.PostAsync("/modes", first)).StatusCode == HttpStatusCode.OK, "first mode create failed");
    using var duplicate = new StringContent("{\"name\":\"only\"}", Encoding.UTF8, "application/json");
    var duplicateResponse = await fixture.Client.PostAsync("/modes", duplicate);
    Assert(duplicateResponse.StatusCode == HttpStatusCode.BadRequest && await HasFailureEnvelope(duplicateResponse), "duplicate mode was not structured failure");
    var mode = (await modes.ListAsync()).Single();
    var deleteResponse = await fixture.Client.DeleteAsync($"/modes/{mode.Id:D}");
    Assert(deleteResponse.StatusCode == HttpStatusCode.BadRequest && await HasFailureEnvelope(deleteResponse), "last-mode delete was not structured failure");

    var toggleResponse = await fixture.Client.PostAsync("/recording/toggle", content: null);
    Assert(toggleResponse.StatusCode == HttpStatusCode.ServiceUnavailable && await HasFailureEnvelope(toggleResponse), "recording start failure was reported as success");

    using var post = new StringContent("{\"text\":\"hello\",\"preset\":\"hyper\"}", Encoding.UTF8, "application/json");
    var postResponse = await fixture.Client.PostAsync("/post-process", post);
    Assert(postResponse.StatusCode == HttpStatusCode.ServiceUnavailable && await HasFailureEnvelope(postResponse), "unavailable post-process was not structured failure");

    using var form = new MultipartFormDataContent();
    form.Add(new ByteArrayContent([1, 2, 3]), "audio", "clip.wav");
    var transcribeResponse = await fixture.Client.PostAsync("/transcribe", form);
    Assert(transcribeResponse.StatusCode == HttpStatusCode.ServiceUnavailable && await HasFailureEnvelope(transcribeResponse), "unavailable transcription was not structured failure");
    Assert(!Directory.EnumerateFiles(paths.RecordingsDirectory, "local-api-*").Any(), "failed transcription retained staged audio");

    using (var successWorkflow = new TranscriptionWorkflow(new NoRecorder(), new NoDevices(), new StaticTranscriber(success: true), history))
    {
        var successBackend = new ApplicationLocalApiBackend(modes, history, successWorkflow, new EmptyCatalog(), new DiskPrivateFiles(), paths, "1.0");
        _ = await successBackend.TranscribeAsync(new AudioUpload("success.wav", "audio/wav", new byte[] { 4, 5, 6 }, null, null, null, "en"), CancellationToken.None);
        var completed = (await history.ListAsync()).Single(item => item.Status == HyperWhisper.Data.Entities.TranscriptStatus.Completed);
        Assert(completed.AudioFilePath is not null && File.Exists(completed.AudioFilePath), "successful history points at deleted audio");
    }

    using (var failedWorkflow = new TranscriptionWorkflow(new NoRecorder(), new NoDevices(), new StaticTranscriber(success: false), history))
    {
        var failedBackend = new ApplicationLocalApiBackend(modes, history, failedWorkflow, new EmptyCatalog(), new DiskPrivateFiles(), paths, "1.0");
        try { _ = await failedBackend.TranscribeAsync(new AudioUpload("failed.wav", "audio/wav", new byte[] { 7, 8, 9 }, null, null, null, "en"), CancellationToken.None); }
        catch (InvalidOperationException) { }
        var failed = (await history.ListAsync()).Single(item => item.Status == HyperWhisper.Data.Entities.TranscriptStatus.Failed);
        Assert(failed.AudioFilePath is not null && File.Exists(failed.AudioFilePath), "retryable failed history points at deleted audio");
    }
}

static async Task ShutdownFailureCleanup()
{
    using var paths = new TempPaths();
    async Task<Microsoft.AspNetCore.Builder.WebApplication> Start(int port, string token, CancellationToken cancellationToken)
    {
        var app = PortableLocalApi.Build([], new PortableLocalApiOptions(token, port), new FakeBackend(), builder =>
            builder.Services.AddHostedService<ThrowOnStopService>());
        await app.StartAsync(cancellationToken);
        return app;
    }
    await using var host = new PortableLocalApiHost(new DiskPrivateFiles(), paths, new FakeBackend(), "1.0", 0, applicationStarter: Start);
    Assert((await host.StartAsync()).IsRunning && File.Exists(host.DiscoveryPath), "throwing host did not start");
    var stopped = await host.StopAsync();
    Assert(stopped.Failure?.Code == "local_api.shutdown" && !File.Exists(host.DiscoveryPath), "host exception escaped or left discovery behind");
}

static async Task FailedStartCleanup()
{
    using var paths = new TempPaths();
    async Task<Microsoft.AspNetCore.Builder.WebApplication> Start(int port, string token, CancellationToken cancellationToken)
    {
        var app = PortableLocalApi.Build([], new PortableLocalApiOptions(token, port), new FakeBackend(), builder =>
            builder.Services.AddHostedService<ThrowOnStopService>());
        await app.StartAsync(cancellationToken);
        return app;
    }
    await using var host = new PortableLocalApiHost(new FailDiscoveryPrivateFiles(), paths, new FakeBackend(), "1.0", 0, applicationStarter: Start);
    var state = await host.StartAsync();
    Assert(!state.IsRunning && state.Failure?.Code == "local_api.discovery", "failed discovery cleanup escaped or changed failure");
    Assert(!File.Exists(host.DiscoveryPath), "failed start left discovery behind");
}

static Task HostOptionsValidation()
{
    using var paths = new TempPaths();
    AssertThrows<ArgumentOutOfRangeException>(() => new PortableLocalApiHost(new DiskPrivateFiles(), paths, new FakeBackend(), "1.0", -1));
    AssertThrows<ArgumentOutOfRangeException>(() => new PortableLocalApiHost(new DiskPrivateFiles(), paths, new FakeBackend(), "1.0", 65_536));
    AssertThrows<ArgumentOutOfRangeException>(() => new PortableLocalApiHost(new DiskPrivateFiles(), paths, new FakeBackend(), "1.0", 0, maxRequestBytes: 0));
    AssertThrows<ArgumentOutOfRangeException>(() => new PortableLocalApiHost(new DiskPrivateFiles(), paths, new FakeBackend(), "1.0", 0, maxRequestBytes: 10, maxUploadBytes: 11));
    return Task.CompletedTask;
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task<bool> HasFailureEnvelope(HttpResponseMessage response)
{
    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    return document.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean() && document.RootElement.TryGetProperty("error", out _);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Fixture : IAsyncDisposable
{
    public const string Token = "test-only-local-token-with-enough-entropy";
    public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required FakeBackend Backend { get; init; }
    public void Authenticate() => Client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
    public static async Task<Fixture> Create(int maxUpload = 1024, ILocalApiBackend? backend = null)
    {
        backend ??= new FakeBackend();
        var options = new PortableLocalApiOptions(Token, 0, 4096, maxUpload);
        var app = PortableLocalApi.Build([], options, backend, builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new() { App = app, Client = app.GetTestClient(), Backend = backend as FakeBackend ?? new FakeBackend() };
    }
    public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
}

sealed class FakeBackend : ILocalApiBackend
{
    public AudioUpload? Upload { get; private set; }
    public bool BlockTranscription { get; init; }
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<HealthSnapshot> GetHealthAsync(CancellationToken ct) => ValueTask.FromResult(new HealthSnapshot("1.0", [], [], new { }));
    public ValueTask<IReadOnlyList<ModelEntry>> GetModelsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ModelEntry>>([]);
    public ValueTask<IReadOnlyList<JsonElement>> GetModesAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<JsonElement>>([]);
    public ValueTask<JsonElement?> GetModeAsync(string id, CancellationToken ct) => ValueTask.FromResult<JsonElement?>(null);
    public ValueTask<JsonElement> CreateModeAsync(JsonElement mode, CancellationToken ct) => ValueTask.FromResult(mode);
    public ValueTask<JsonElement?> PatchModeAsync(string id, JsonElement patch, CancellationToken ct) => ValueTask.FromResult<JsonElement?>(null);
    public ValueTask<bool> DeleteModeAsync(string id, CancellationToken ct) => ValueTask.FromResult(false);
    public ValueTask<RecordingState> ToggleRecordingAsync(CancellationToken ct) => ValueTask.FromResult(new RecordingState(true, "recording"));
    public ValueTask<RecordingState> CancelRecordingAsync(CancellationToken ct) => ValueTask.FromResult(new RecordingState(false, "idle"));
    public async ValueTask<TranscriptionResult> TranscribeAsync(AudioUpload upload, CancellationToken ct)
    {
        Upload = upload;
        if (BlockTranscription)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { CancellationObserved.TrySetResult(); throw; }
        }
        return new("hello", "fake", "fake", "en", 1, 1, 2);
    }
    public ValueTask<PostProcessResult> PostProcessAsync(PostProcessRequest request, CancellationToken ct) => ValueTask.FromResult(new PostProcessResult(request.Text, "fake", "fake", "hyper", 1));
    public ValueTask<IReadOnlyList<RecordingEntry>> GetRecordingsAsync(RecordingQuery query, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<RecordingEntry>>([]);
    public ValueTask<RecordingEntry?> GetRecordingAsync(string id, CancellationToken ct) => ValueTask.FromResult<RecordingEntry?>(null);
}

sealed class FakePrivateFiles : IPrivateFileService
{
    private string? _value;
    public string? Value { set => _value = value; }
    public bool Restricted { get; private set; }
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents) => PlatformResult.Failure("unused", "unused");
    public PlatformResult WriteAllTextAtomically(string path, string contents) { _value = contents; Restricted = true; return PlatformResult.Success(); }
    public PlatformResult<byte[]?> ReadAllBytes(string path) => PlatformResult<byte[]?>.Success(null);
    public PlatformResult<string?> ReadAllText(string path) => PlatformResult<string?>.Success(_value);
    public PlatformResult Delete(string path) { _value = null; return PlatformResult.Success(); }
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => PlatformResult<bool>.Success(Restricted);
}

sealed class TempPaths : IAppPaths, IDisposable
{
    public TempPaths()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), $"hyperwhisper-local-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataDirectory);
    }
    public string DataDirectory { get; }
    public string ConfigDirectory => DataDirectory;
    public string CacheDirectory => DataDirectory;
    public string StateDirectory => DataDirectory;
    public string LogsDirectory => DataDirectory;
    public string ModelsDirectory => DataDirectory;
    public string RecordingsDirectory => DataDirectory;
    public string RuntimeDirectory => DataDirectory;
    public string TemporaryDirectory => DataDirectory;
    public void Dispose() { if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true); }
}

class DiskPrivateFiles : IPrivateFileService
{
    public virtual PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents.ToArray());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return PlatformResult.Success();
    }
    public virtual PlatformResult WriteAllTextAtomically(string path, string contents) => WriteAllBytesAtomically(path, Encoding.UTF8.GetBytes(contents));
    public virtual PlatformResult<byte[]?> ReadAllBytes(string path) => PlatformResult<byte[]?>.Success(File.Exists(path) ? File.ReadAllBytes(path) : null);
    public virtual PlatformResult<string?> ReadAllText(string path) => PlatformResult<string?>.Success(File.Exists(path) ? File.ReadAllText(path) : null);
    public virtual PlatformResult Delete(string path) { if (File.Exists(path)) File.Delete(path); return PlatformResult.Success(); }
    public virtual PlatformResult<bool> IsRestrictedToCurrentUser(string path)
    {
        if (!File.Exists(path)) return PlatformResult<bool>.Success(false);
        if (OperatingSystem.IsWindows()) return PlatformResult<bool>.Success(true);
        var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return PlatformResult<bool>.Success((File.GetUnixFileMode(path) & forbidden) == 0);
    }
}

sealed class FailDiscoveryPrivateFiles : DiskPrivateFiles
{
    public override PlatformResult WriteAllTextAtomically(string path, string contents)
        => path.EndsWith("local-api.json", StringComparison.Ordinal)
            ? PlatformResult.Failure("expected", "expected")
            : base.WriteAllTextAtomically(path, contents);
}

sealed class EmptyCatalog : ILocalApiCapabilityCatalog
{
    public IReadOnlyList<ModelEntry> Models => [];
    public IReadOnlyList<ProviderStatus> TranscriptionProviders => [];
    public IReadOnlyList<ProviderStatus> PostProcessingProviders => [];
    public object LocalModels { get; } = new { };
}

sealed class NoRecorder : IAudioRecorder
{
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public PlatformResult Start(AudioRecordingOptions options) => PlatformResult.Failure("unavailable", "unavailable");
    public PlatformResult<string> Stop() => PlatformResult<string>.Failure("unavailable", "unavailable");
    public void Dispose() { }
}

sealed class NoDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() => PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([]);
    public void Dispose() { }
}

sealed class UnavailableTranscriber : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability { get; } = new(false, "Unavailable", "Not configured");
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language, CancellationToken cancellationToken = default)
        => Task.FromResult(PortableTranscriptionResult.Failed(PortableTranscriptionErrorCode.BackendUnavailable, "Not configured"));
}

sealed class StaticTranscriber(bool success) : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability { get; } = new(true, "Static");
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language, CancellationToken cancellationToken = default)
        => Task.FromResult(success
            ? PortableTranscriptionResult.Success("portable result", "Static")
            : PortableTranscriptionResult.Failed(PortableTranscriptionErrorCode.TranscriptionFailed, "expected failure", "Static"));
}

sealed class ThrowOnStopService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("expected stop failure"));
}
