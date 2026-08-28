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
    ("Windows JSON transcription contract", JsonTranscriptionContract),
    ("Windows JSON file source is private and bounded", JsonFileSecurity),
    ("Windows post-process application context contract", PostProcessContextContract),
    ("Windows endpoint contract snapshots and overrides", EndpointContractSnapshots),
    ("path traversal rejected", Traversal),
    ("request cancellation propagates", Cancellation),
    ("health reports actual bound port", HealthReportsBoundPort),
    ("bind fallback", BindFallback),
    ("private token persistence", TokenPersistence),
    ("private token regeneration invalidates old credential", TokenRegeneration),
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
    ,("application backend resolves modes and vocabulary", ApplicationBackendModeRouting)
    ,("application backend validates mode catalogs", ApplicationBackendModeValidation)
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

static async Task EndpointContractSnapshots()
{
    await using var fixture = await Fixture.Create();

    using (var health = JsonDocument.Parse(await fixture.Client.GetStringAsync("/health")))
    {
        AssertProperties(health.RootElement,
            "ok", "app_version", "api_version", "port", "pid", "providers", "post_processing_providers", "local_models");
        Assert(health.RootElement.GetProperty("api_version").GetInt32() == 1, "health API version drifted");
    }

    fixture.Authenticate();
    using (var models = JsonDocument.Parse(await fixture.Client.GetStringAsync("/models")))
        AssertProperties(models.RootElement, "ok", "models");
    using (var modes = JsonDocument.Parse(await fixture.Client.GetStringAsync("/modes")))
        AssertProperties(modes.RootElement, "ok", "modes");

    using (var postResponse = await fixture.Client.PostAsync("/post-process", JsonContent(
        """{"text":"hello","prompt":"Be concise","provider":"groq","model":"llama"}""")))
    using (var post = JsonDocument.Parse(await postResponse.Content.ReadAsStreamAsync()))
    {
        Assert(postResponse.StatusCode == HttpStatusCode.OK, "post-process override contract failed");
        AssertProperties(post.RootElement, "ok", "text", "provider", "model", "preset", "latency_ms");
        Assert(fixture.Backend.PostProcess is { Prompt: "Be concise", Provider: "groq", Model: "llama" },
            "post-process provider/model overrides were lost");
    }

    using (var recordings = JsonDocument.Parse(await fixture.Client.GetStringAsync(
        "/recordings?q=ray&since=2026-01-01T00:00:00Z&until=2026-01-02T00:00:00Z&limit=999")))
    {
        AssertProperties(recordings.RootElement, "ok", "total", "returned", "recordings");
        Assert(fixture.Backend.RecordingQuery is { Search: "ray", Limit: 500 }
            && fixture.Backend.RecordingQuery.Since is not null
            && fixture.Backend.RecordingQuery.Until is not null,
            "recording search/date/limit overrides drifted");
    }
}

static void AssertProperties(JsonElement element, params string[] expected)
{
    var actual = element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
    Assert(actual.SequenceEqual(expected.Order(StringComparer.Ordinal)),
        $"contract properties drifted: {string.Join(',', actual)}");
}

static async Task JsonTranscriptionContract()
{
    await using var fixture = await Fixture.Create();
    fixture.Authenticate();
    const string body = """
    {
      "audio_base64":"AQIDBA==",
      "mime_type":"audio/wav",
      "engine":"whisperLocal",
      "model":"base",
      "language":"en",
      "applicationContext":{
        "processName":"code",
        "windowTitle":"Contract test",
        "category":"Code Editor",
        "browserTabTitle":null,
        "browserHost":null,
        "focusedElementType":"text",
        "focusedContent":"bounded context",
        "textFormat":"code",
        "appType":"code",
        "appTypeConfidence":"strong",
        "appTypeSource":"localApi",
        "screenOCRText":"visible words"
      }
    }
    """;
    using var response = await fixture.Client.PostAsync("/transcribe", new StringContent(body, Encoding.UTF8, "application/json"));
    Assert(response.StatusCode == HttpStatusCode.OK, "Windows JSON transcription request failed");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    var root = json.RootElement;
    string[] exact = ["ok", "text", "engine", "model", "language", "timings", "latency_ms"];
    Assert(root.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(exact.Order()), "transcription response drifted from Windows shape");
    Assert(root.GetProperty("text").GetString() == "hello" && !root.TryGetProperty("result", out _), "legacy nested response leaked into Windows contract");
    Assert(root.GetProperty("timings").GetProperty("load_ms").GetInt32() == 1, "Windows timing fields changed");
    Assert(fixture.Backend.Upload?.Content.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }) == true, "base64 audio bytes changed");
    Assert(fixture.Backend.Upload?.Engine == "whisperLocal" && fixture.Backend.Upload.Model == "base", "engine/model overrides were lost");
    var context = fixture.Backend.Upload?.ApplicationContext?.ToSnapshot();
    Assert(context?.ProcessName == "code" && context.ScreenOcrText == "visible words", "applicationContext was not propagated");

    using var timestampRequest = new StringContent(
        """{"audio_base64":"AQIDBA==","engine":"whisperLocal","model":"base","timestamp_granularities":["segment","word"]}""",
        Encoding.UTF8,
        "application/json");
    using var timestampResponse = await fixture.Client.PostAsync("/transcribe", timestampRequest);
    using var timestampJson = JsonDocument.Parse(await timestampResponse.Content.ReadAsStreamAsync());
    Assert(timestampResponse.StatusCode == HttpStatusCode.OK
        && fixture.Backend.Upload?.TimestampGranularities?.SequenceEqual(["segment", "word"]) == true,
        "timestamp granularities were not propagated to the backend");
    Assert(timestampJson.RootElement.GetProperty("raw_text").GetString() == "hello"
        && timestampJson.RootElement.GetProperty("segments")[0].GetProperty("text").GetString() == "hello"
        && timestampJson.RootElement.GetProperty("words")[0].GetProperty("word").GetString() == "hello",
        "timestamp response omitted the mac-compatible raw/segment/word shape");

    using var both = new StringContent("""{"file":"/tmp/a.wav","audio_base64":"AQ==","engine":"whisper","model":"base"}""", Encoding.UTF8, "application/json");
    Assert((await fixture.Client.PostAsync("/transcribe", both)).StatusCode == HttpStatusCode.BadRequest, "file plus base64 was accepted");
    using var missingEngine = new StringContent("""{"audio_base64":"AQ=="}""", Encoding.UTF8, "application/json");
    Assert((await fixture.Client.PostAsync("/transcribe", missingEngine)).StatusCode == HttpStatusCode.BadRequest, "request without mode_id/engine was accepted");
    using var invalid = new StringContent("""{"audio_base64":"%%%","engine":"whisper","model":"base"}""", Encoding.UTF8, "application/json");
    Assert((await fixture.Client.PostAsync("/transcribe", invalid)).StatusCode == HttpStatusCode.BadRequest, "invalid base64 was accepted");
    var tooManyBytes = Convert.ToBase64String(new byte[1025]);
    using var oversized = JsonContent($$"""{"audio_base64":{{JsonSerializer.Serialize(tooManyBytes)}},"engine":"whisper","model":"base"}""");
    Assert((await fixture.Client.PostAsync("/transcribe", oversized)).StatusCode == HttpStatusCode.RequestEntityTooLarge,
        "oversized base64 audio was accepted");
}

static async Task JsonFileSecurity()
{
    await using var fixture = await Fixture.Create(maxUpload: 4);
    fixture.Authenticate();
    var allowed = Path.Combine(fixture.AllowedRoot, "clip.wav");
    await File.WriteAllBytesAsync(allowed, [1, 2, 3, 4]);
    using var valid = JsonContent($$"""{"file":{{JsonSerializer.Serialize(allowed)}},"engine":"whisper","model":"base"}""");
    Assert((await fixture.Client.PostAsync("/transcribe", valid)).StatusCode == HttpStatusCode.OK, "allowed private file failed");
    Assert(fixture.Backend.Upload?.Content.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }) == true, "allowed file bytes changed");

    var outside = Path.Combine(Path.GetTempPath(), $"hyperwhisper-outside-{Guid.NewGuid():N}.wav");
    await File.WriteAllBytesAsync(outside, [1]);
    try
    {
        using var external = JsonContent($$"""{"file":{{JsonSerializer.Serialize(outside)}},"engine":"whisper","model":"base"}""");
        var externalResponse = await fixture.Client.PostAsync("/transcribe", external);
        Assert(externalResponse.StatusCode == HttpStatusCode.BadRequest && await HasFailureEnvelope(externalResponse), "outside file was accepted or leaked details");
        using var relative = JsonContent("""{"file":"../secret.wav","engine":"whisper","model":"base"}""");
        Assert((await fixture.Client.PostAsync("/transcribe", relative)).StatusCode == HttpStatusCode.BadRequest, "relative traversal was accepted");
        await File.WriteAllBytesAsync(allowed, [1, 2, 3, 4, 5]);
        using var large = JsonContent($$"""{"file":{{JsonSerializer.Serialize(allowed)}},"engine":"whisper","model":"base"}""");
        Assert((await fixture.Client.PostAsync("/transcribe", large)).StatusCode == HttpStatusCode.RequestEntityTooLarge, "oversized file was accepted");
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(fixture.AllowedRoot, "link.wav");
            File.CreateSymbolicLink(link, outside);
            using var symlink = JsonContent($$"""{"file":{{JsonSerializer.Serialize(link)}},"engine":"whisper","model":"base"}""");
            Assert((await fixture.Client.PostAsync("/transcribe", symlink)).StatusCode == HttpStatusCode.BadRequest, "symlink escape was accepted");
        }
    }
    finally { File.Delete(outside); }
}

static async Task PostProcessContextContract()
{
    await using var fixture = await Fixture.Create();
    fixture.Authenticate();
    const string body = """
    {"text":"raw words","preset":"hyper","provider":"openai","model":"gpt-test",
     "applicationContext":{"processName":"terminal","appType":"terminal","screenOCRText":"prompt context"}}
    """;
    using var response = await fixture.Client.PostAsync("/post-process", new StringContent(body, Encoding.UTF8, "application/json"));
    Assert(response.StatusCode == HttpStatusCode.OK, "Windows post-process request failed");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    string[] exact = ["ok", "text", "provider", "model", "preset", "latency_ms"];
    Assert(json.RootElement.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(exact.Order()), "post-process response drifted from Windows shape");
    Assert(fixture.Backend.PostProcess?.ApplicationContext?.ToSnapshot().AppType == "terminal", "post-process applicationContext was lost");

    using var conflicting = JsonContent("""{"text":"raw","preset":"hyper","prompt":"custom"}""");
    Assert((await fixture.Client.PostAsync("/post-process", conflicting)).StatusCode == HttpStatusCode.BadRequest,
        "mutually exclusive preset and prompt were accepted");
    using var missingSelector = JsonContent("""{"text":"raw","provider":"openai","model":"gpt-test"}""");
    Assert((await fixture.Client.PostAsync("/post-process", missingSelector)).StatusCode == HttpStatusCode.BadRequest,
        "post-process request without mode_id/preset/prompt was accepted");
}

static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

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

static Task TokenRegeneration()
{
    var files = new FakePrivateFiles();
    var store = new LocalApiTokenStore(files, "/config/local-api-token");
    var original = store.LoadOrCreate();
    var replacement = store.Regenerate();
    Assert(original != replacement, "token regeneration retained the old credential");
    Assert(replacement == store.LoadOrCreate(), "replacement token was not persisted");
    Assert(!LocalApiTokenStore.FixedTimeEquals(original, replacement), "old token still matched replacement");
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

static async Task ApplicationBackendModeRouting()
{
    using var paths = new TempPaths();
    var database = new ApplicationDb(paths);
    await using (var context = database.CreateContext()) await context.Database.EnsureCreatedAsync();
    var history = new HistoryRepository(database);
    var modes = new ModeRepository(database);
    var vocabulary = new VocabularyRepository(database);
    var defaultMode = new HyperWhisper.Data.Entities.Mode
    {
        Name = "Default cloud", IsDefault = true, SortOrder = 1, ProviderType = "cloud",
        CloudProvider = "groq", Language = "fr", PostProcessingMode = 1,
        PostProcessingProvider = "openai",
    };
    var selectedMode = new HyperWhisper.Data.Entities.Mode
    {
        Name = "Selected local", SortOrder = 2, ProviderType = "local", LocalEngine = "whisper",
        Model = "tiny.en", ModelType = "tiny.en", Language = "en",
    };
    await modes.UpsertAsync(defaultMode);
    await modes.UpsertAsync(selectedMode);
    await vocabulary.AddAsync(new HyperWhisper.Data.Entities.VocabularyItem { Word = " Ray ", SortOrder = 1 });
    await vocabulary.AddAsync(new HyperWhisper.Data.Entities.VocabularyItem { Word = "HyperWhisper", SortOrder = 2 });
    // Sanitized and de-duplicated by the shared core on the way out: angle
    // brackets stripped, whitespace runs collapsed, "ray" folds into " Ray ".
    await vocabulary.AddAsync(new HyperWhisper.Data.Entities.VocabularyItem { Word = "Rust<script>", SortOrder = 10 });
    await vocabulary.AddAsync(new HyperWhisper.Data.Entities.VocabularyItem { Word = "multi\n  word", SortOrder = 11 });
    await vocabulary.AddAsync(new HyperWhisper.Data.Entities.VocabularyItem { Word = "ray", SortOrder = 12 });

    var transcriber = new CapturingTranscriber();
    using var workflow = new TranscriptionWorkflow(new TestRecorder(paths), new TestDevices(), transcriber, history);
    workflow.RefreshDevices();
    var backend = new ApplicationLocalApiBackend(modes, history, workflow, new FullCatalog(), new DiskPrivateFiles(), paths, "1.0", vocabulary: vocabulary);

    var defaultResult = await backend.TranscribeAsync(new AudioUpload("default.wav", "audio/wav", new byte[] { 1 }, null, null, null, null), CancellationToken.None);
    Assert(transcriber.Request?.ModeId == defaultMode.Id && transcriber.Request.SelectedMode?.Name == "Default cloud", "omitted mode_id did not select the persisted default");
    Assert(transcriber.Request!.Language == "fr", "default mode language was not used");
    Assert(transcriber.Request.SelectedMode?.PostProcessingMode == 0, "/transcribe applied the saved mode's post-processing");
    Assert(defaultResult.Language == "fr", "response did not report the resolved default-mode language");
    Assert(defaultResult.Engine == "groq", "response did not use the Windows cloud engine label");
    Assert(transcriber.Request.Vocabulary?.SequenceEqual(["Ray", "HyperWhisper", "Rustscript", "multi word"]) == true, "global vocabulary was not normalized through the shared core on the way to transcription");

    _ = await backend.TranscribeAsync(new AudioUpload("selected.wav", "audio/wav", new byte[] { 2 }, selectedMode.Id.ToString("D"), null, null, "de"), CancellationToken.None);
    Assert(transcriber.Request?.ModeId == selectedMode.Id && transcriber.Request.SelectedMode?.Name == "Selected local", "explicit mode_id did not select the exact persisted mode");
    Assert(transcriber.Request!.Language == "de", "explicit language did not override mode language");
    var apiContext = new LocalApiApplicationContext("terminal", "Shell", null, null, null, null, null,
        "command", "terminal", "strong", "localApi", null);
    _ = await backend.TranscribeAsync(new AudioUpload(
        "transient.wav", "audio/wav", new byte[] { 2 }, null,
        "parakeet", "parakeet-v3", "ja", apiContext), CancellationToken.None);
    Assert(transcriber.Request?.SelectedMode is { ProviderType: "local", LocalEngine: "parakeet", LocalParakeetModel: "parakeet-v3" },
        "engine/model did not construct a transient Windows-compatible mode");
    Assert(transcriber.Request?.ApplicationContext?.AppType == "terminal", "transcription applicationContext did not reach the workflow");
    var stagedBeforeInvalid = Directory.EnumerateFiles(paths.RecordingsDirectory, "local-api-*").Count();
    await AssertThrowsAsync<ArgumentException>(() => backend.TranscribeAsync(new AudioUpload("bad.wav", "audio/wav", new byte[] { 3 }, Guid.NewGuid().ToString("D"), null, null, null), CancellationToken.None).AsTask());
    Assert(Directory.EnumerateFiles(paths.RecordingsDirectory, "local-api-*").Count() == stagedBeforeInvalid, "invalid mode retained an orphaned upload");
    await using (var fixture = await Fixture.Create(backend: backend))
    {
        fixture.Authenticate();
        using var invalidMode = new MultipartFormDataContent();
        invalidMode.Add(new ByteArrayContent([4]), "audio", "invalid-mode.wav");
        invalidMode.Add(new StringContent("not-a-guid"), "mode_id");
        var response = await fixture.Client.PostAsync("/transcribe", invalidMode);
        Assert(response.StatusCode == HttpStatusCode.BadRequest && await HasFailureEnvelope(response), "invalid mode_id did not return a structured API failure");
    }

    _ = await backend.ToggleRecordingAsync(CancellationToken.None);
    defaultMode.IsDefault = false;
    selectedMode.IsDefault = true;
    await modes.UpsertAsync(defaultMode);
    await modes.UpsertAsync(selectedMode);
    await vocabulary.AddAsync(new HyperWhisper.Data.Entities.VocabularyItem { Word = "late mutation", SortOrder = 3 });
    _ = await backend.ToggleRecordingAsync(CancellationToken.None);
    Assert(transcriber.Request?.ModeId == defaultMode.Id, "recording stop did not retain the mode captured at start");
    Assert(transcriber.Request!.Vocabulary?.SequenceEqual(["Ray", "HyperWhisper", "Rustscript", "multi word"]) == true, "recording stop did not retain vocabulary captured at start");
}

static async Task ApplicationBackendModeValidation()
{
    using var paths = new TempPaths();
    var database = new ApplicationDb(paths);
    await using (var context = database.CreateContext()) await context.Database.EnsureCreatedAsync();
    var modes = new ModeRepository(database);
    var history = new HistoryRepository(database);
    using var workflow = new TranscriptionWorkflow(new NoRecorder(), new NoDevices(), new UnavailableTranscriber(), history);
    var backend = new ApplicationLocalApiBackend(modes, history, workflow, new FullCatalog(), new DiskPrivateFiles(), paths, "1.0");
    // Both Gemini ids: `gemini` (multimodal) and `geminiTranscribe` (BYOK Gemini
    // 3.5 Transcribe). The camelCase spelling is what Windows persists, so the
    // allow-list must accept it as well as the lowercase one macOS writes.
    string[] providers = ["openai", "groq", "deepgram", "assemblyai", "elevenlabs", "mistral", "soniox", "hyperwhisper", "gemini", "geminiTranscribe", "geminitranscribe", "grok", "microsoftAzureSpeech", "googleSpeech"];
    // Mode names are unique case-insensitively, so index them: two spellings of
    // the same provider id are a legitimate pair of cases to accept.
    for (var index = 0; index < providers.Length; index++)
        _ = await backend.CreateModeAsync(
            Json($$"""{"name":"cloud-{{index}}-{{providers[index]}}","providerType":"cloud","cloudProvider":"{{providers[index]}}"}"""),
            CancellationToken.None);
    await AssertThrowsAsync<ArgumentException>(() => backend.CreateModeAsync(Json("""{"name":"Unknown cloud","providerType":"cloud","cloudProvider":"azure"}"""), CancellationToken.None).AsTask());
    await AssertThrowsAsync<ArgumentException>(() => backend.CreateModeAsync(Json("""{"name":"Unknown engine","providerType":"local","localEngine":"vosk","model":"base"}"""), CancellationToken.None).AsTask());
    await AssertThrowsAsync<ArgumentException>(() => backend.CreateModeAsync(Json("""{"name":"Wrong whisper","providerType":"local","localEngine":"whisper","model":"parakeet-v3"}"""), CancellationToken.None).AsTask());
    await AssertThrowsAsync<ArgumentException>(() => backend.CreateModeAsync(Json("""{"name":"Wrong parakeet","providerType":"local","localEngine":"parakeet","localParakeetModel":"base"}"""), CancellationToken.None).AsTask());
    _ = await backend.CreateModeAsync(Json("""{"name":"Valid whisper","providerType":"local","localEngine":"whisper","model":"large-v3"}"""), CancellationToken.None);
    _ = await backend.CreateModeAsync(Json("""{"name":"Valid parakeet","providerType":"local","localEngine":"parakeet","localParakeetModel":"parakeet-v3"}"""), CancellationToken.None);
    Assert((await modes.ListAsync()).Count(item => item.IsDefault) == 1, "create operations did not preserve exactly one default mode");
}

static JsonElement Json(string value)
{
    using var document = JsonDocument.Parse(value);
    return document.RootElement.Clone();
}

static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
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
    public required string AllowedRoot { get; init; }
    public void Authenticate() => Client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
    public static async Task<Fixture> Create(int maxUpload = 1024, ILocalApiBackend? backend = null)
    {
        backend ??= new FakeBackend();
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"hyperwhisper-local-api-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowedRoot);
        var options = new PortableLocalApiOptions(Token, 0, 4096, maxUpload, AllowedFileRoots: [allowedRoot]);
        var app = PortableLocalApi.Build([], options, backend, builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new() { App = app, Client = app.GetTestClient(), Backend = backend as FakeBackend ?? new FakeBackend(), AllowedRoot = allowedRoot };
    }
    public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); if (Directory.Exists(AllowedRoot)) Directory.Delete(AllowedRoot, true); }
}

sealed class FakeBackend : ILocalApiBackend
{
    public AudioUpload? Upload { get; private set; }
    public PostProcessRequest? PostProcess { get; private set; }
    public RecordingQuery? RecordingQuery { get; private set; }
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
        return upload.TimestampGranularities is { Count: > 0 }
            ? new("hello", "fake", "fake", "en", 1, 1, 2, "hello",
                [new(0, 0, 0.5, "hello")], [new("hello", 0, 0.5, 0.9)])
            : new("hello", "fake", "fake", "en", 1, 1, 2);
    }
    public ValueTask<PostProcessResult> PostProcessAsync(PostProcessRequest request, CancellationToken ct)
    { PostProcess = request; return ValueTask.FromResult(new PostProcessResult(request.Text, "fake", "fake", "hyper", 1)); }
    public ValueTask<IReadOnlyList<RecordingEntry>> GetRecordingsAsync(RecordingQuery query, CancellationToken ct)
    { RecordingQuery = query; return ValueTask.FromResult<IReadOnlyList<RecordingEntry>>([]); }
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

sealed class FullCatalog : ILocalApiCapabilityCatalog
{
    public IReadOnlyList<ModelEntry> Models { get; } =
    [
        .. new[] { "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en", "large-v3-turbo", "large-v2", "large-v3",
            "parakeet-v2", "parakeet-v3", "qwen3-asr-0.6b", "nemotron-3.5-ml-560ms" }
            .Select(id => new ModelEntry(id, "voice", "local", id, true)),
    ];
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

sealed class CapturingTranscriber : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability { get; } = new(true, "Capturing");
    public TranscriptionWorkflowRequest? Request { get; private set; }
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language, CancellationToken cancellationToken = default)
        => TranscribeAsync(audioPath, new TranscriptionWorkflowRequest(Language: language), cancellationToken);
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, TranscriptionWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        Request = request;
        return Task.FromResult(PortableTranscriptionResult.Success("captured", "Capturing"));
    }
}

sealed class TestDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices()
        => PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([new("default", "Default", true)]);
    public void Dispose() { }
}

sealed class TestRecorder(TempPaths paths) : IAudioRecorder
{
    private bool _recording;
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording => _recording;
    public TimeSpan Duration => TimeSpan.FromSeconds(1);
    public PlatformResult Start(AudioRecordingOptions options) { _recording = true; return PlatformResult.Success(); }
    public PlatformResult<string> Stop()
    {
        _recording = false;
        var path = Path.Combine(paths.RecordingsDirectory, $"recording-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, [1, 2, 3]);
        return PlatformResult<string>.Success(path);
    }
    public void Dispose() { }
}

sealed class ThrowOnStopService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("expected stop failure"));
}
