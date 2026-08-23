using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HyperWhisper.LocalApi;
using HyperWhisper.Platform.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

var tests = new (string Name, Func<Task> Run)[]
{
    ("route parity", RouteParity),
    ("bearer auth and public health", Auth),
    ("bounded multipart transcription", Multipart),
    ("path traversal rejected", Traversal),
    ("request cancellation propagates", Cancellation),
    ("health reports actual bound port", HealthReportsBoundPort),
    ("bind fallback", BindFallback),
    ("private token persistence", TokenPersistence)
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
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
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
    public static async Task<Fixture> Create(int maxUpload = 1024, FakeBackend? backend = null)
    {
        backend ??= new();
        var options = new PortableLocalApiOptions(Token, 0, 4096, maxUpload);
        var app = PortableLocalApi.Build([], options, backend, builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new() { App = app, Client = app.GetTestClient(), Backend = backend };
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
