using System.Net;
using System.Text;
using System.Text.Json;
using HyperWhisper.CloudPostProcessing;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Windows-parity model registry", TestModelRegistry),
    ("OpenAI request and shared evaluation", TestOpenAi),
    ("Anthropic request and shared evaluation", TestAnthropic),
    ("OpenAI-compatible provider routes", TestOpenAiCompatibleRoutes),
    ("missing credentials fail closed", TestMissingCredential),
    ("custom endpoint validation", TestCustomValidation),
    ("custom Groq request policy", TestCustomGroq),
    ("HyperWhisper Cloud catalog route", TestHyperWhisperCloud),
    ("HyperWhisper Cloud device identity", TestHyperWhisperCloudDevice),
    ("HyperWhisper Cloud base URL is switchable", TestHyperWhisperCloudBaseUrl),
    ("HyperWhisper Cloud sends the transcript once", TestHyperWhisperCloudTranscriptOnce),
    ("saved endpoints are repaired, not dropped", TestExistingEndpointRepair),
    ("endpoint copy names increment", TestCopyName),
    ("truncated completion is rejected", TestTruncatedCompletion),
    ("HTTP failure is redacted", TestHttpFailureRedaction),
    ("cancellation is graceful", TestCancellation),
    ("credential decoding is strict and cleared", TestCredentialSecurity),
    ("oversized response is rejected", TestOversizedResponse),
    ("stale model falls back deterministically", TestModelFallback),
    ("redirect does not replay credentials", TestRedirectFailure),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static Task TestModelRegistry()
{
    var expected = new Dictionary<CloudPostProcessingProvider, int>
    {
        [CloudPostProcessingProvider.OpenAi] = 11,
        [CloudPostProcessingProvider.Anthropic] = 4,
        [CloudPostProcessingProvider.Groq] = 3,
        [CloudPostProcessingProvider.Grok] = 3,
        [CloudPostProcessingProvider.Gemini] = 10,
        [CloudPostProcessingProvider.Cerebras] = 2,
        [CloudPostProcessingProvider.Mistral] = 2,
    };
    foreach (var item in expected)
        Assert(PostProcessingModelCatalog.ForProvider(item.Key).Count == item.Value, $"{item.Key} model parity mismatch");
    return Task.CompletedTask;
}

static async Task TestOpenAi()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, OpenAiResponse("<<CLEANED>>Hello Ray<<END>>"));
    });
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.OpenAi, "sk-test-secret"));
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.OpenAi, "gpt-4.1-mini"));
    var snapshot = seen ?? throw new InvalidOperationException("OpenAI request was not captured");
    Assert(result.WasApplied && result.Text == "Hello Ray", "OpenAI response was not accepted");
    Assert(snapshot.Uri == "https://api.openai.com/v1/chat/completions", "OpenAI endpoint mismatch");
    Assert(snapshot.Authorization == "Bearer sk-test-secret", "OpenAI auth mismatch");
    Assert(snapshot.Body.Contains("--TRANSCRIPT--\\nraw words\\n--ENDTRANSCRIPT--", StringComparison.Ordinal), "transcript was not wrapped");
}

static async Task TestAnthropic()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, "{\"content\":[{\"type\":\"text\",\"text\":\"<<CLEANED>>Claude result<<END>>\"}],\"stop_reason\":\"end_turn\"}");
    });
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.Anthropic, "sk-ant-secret"));
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.Anthropic, "claude-haiku-4-5"));
    var snapshot = seen ?? throw new InvalidOperationException("Anthropic request was not captured");
    Assert(result.WasApplied && result.Text == "Claude result", "Anthropic response was not accepted");
    Assert(snapshot.Headers["x-api-key"] == "sk-ant-secret", "Anthropic API key header mismatch");
    Assert(snapshot.Headers["anthropic-version"] == "2023-06-01", "Anthropic version missing");
    Assert(snapshot.Body.Contains("\"max_tokens\":8192", StringComparison.Ordinal), "Anthropic max token cap missing");
    Assert(snapshot.Body.Contains("\"cache_control\":{\"type\":\"ephemeral\"}", StringComparison.Ordinal), "Anthropic prompt caching missing");
}

static async Task TestOpenAiCompatibleRoutes()
{
    var expected = new Dictionary<CloudPostProcessingProvider, string>
    {
        [CloudPostProcessingProvider.Groq] = "https://api.groq.com/openai/v1/chat/completions",
        [CloudPostProcessingProvider.Grok] = "https://api.x.ai/v1/chat/completions",
        [CloudPostProcessingProvider.Gemini] = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        [CloudPostProcessingProvider.Cerebras] = "https://api.cerebras.ai/v1/chat/completions",
        [CloudPostProcessingProvider.Mistral] = "https://api.mistral.ai/v1/chat/completions",
    };
    foreach (var route in expected)
    {
        RequestSnapshot? seen = null;
        var handler = new StubHandler(async request =>
        {
            seen = await RequestSnapshot.FromAsync(request);
            return Json(HttpStatusCode.OK, OpenAiResponse("done"));
        });
        using var service = Service(handler, ApiKey(route.Key, "provider-secret"));
        var result = await service.ProcessAsync(Request(route.Key));
        Assert(result.WasApplied, $"{route.Key} response was not accepted");
        Assert(seen?.Uri == route.Value, $"{route.Key} endpoint mismatch");
    }
}

static async Task TestMissingCredential()
{
    var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP should not run"));
    using var service = Service(handler, new MemoryCredentialSource());
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.Cerebras));
    Assert(result.Failure?.Code == CloudPostProcessingFailureCode.MissingCredential, "missing credential was not rejected");
    Assert(handler.CallCount == 0, "request ran without a credential");
}

static async Task TestCustomValidation()
{
    var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP should not run"));
    using var service = Service(handler, new MemoryCredentialSource());
    foreach (var url in new[] { "file:///etc/passwd", "https://user:pass@example.test/v1", "https://example.test/v1#secret" })
    {
        var request = Request(CloudPostProcessingProvider.Custom) with
        {
            CustomEndpoint = new(Guid.NewGuid(), url, "model"),
        };
        var result = await service.ProcessAsync(request);
        Assert(result.Failure?.Code == CloudPostProcessingFailureCode.InvalidRequest, $"unsafe URL accepted: {url}");
    }
}

static async Task TestCustomGroq()
{
    RequestSnapshot? seen = null;
    var id = Guid.NewGuid();
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, OpenAiResponse("custom result"));
    });
    var credentials = new MemoryCredentialSource();
    credentials.Set(CloudPostProcessingProvider.Custom, "custom-secret", id);
    using var service = Service(handler, credentials);
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.Custom) with
    {
        CustomEndpoint = new(id, "https://api.groq.com/openai/v1/chat/completions", "private-model"),
    });
    var snapshot = seen ?? throw new InvalidOperationException("custom request was not captured");
    Assert(result.WasApplied && result.Text == "custom result", "custom result failed");
    Assert(snapshot.Body.Contains("\"max_completion_tokens\":4096", StringComparison.Ordinal), "Groq cap missing on custom route");
    Assert(snapshot.Authorization == "Bearer custom-secret", "custom auth mismatch");
}

static async Task TestHyperWhisperCloud()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, "{\"corrected\":\"cloud result\"}");
    });
    var credentials = new MemoryCredentialSource();
    credentials.SetLicense("license-secret", "device-safe");
    using var service = Service(handler, credentials);
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.HyperWhisperCloud) with
    {
        HyperWhisperCloudModel = "groq:openai/gpt-oss-120b",
    });
    var snapshot = seen ?? throw new InvalidOperationException("cloud request was not captured");
    Assert(result.WasApplied && result.Text == "cloud result", "cloud response failed");
    Assert(snapshot.Headers["X-LLM-Provider"] == "groq", "cloud provider header mismatch");
    Assert(snapshot.Headers["X-LLM-Model"] == "openai/gpt-oss-120b", "cloud model header mismatch");
    Assert(snapshot.Body.Contains("\"license_key\":\"license-secret\"", StringComparison.Ordinal), "license auth missing");
    Assert(!snapshot.Body.Contains("device-safe", StringComparison.Ordinal), "device id sent with license");
}

static async Task TestHyperWhisperCloudDevice()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, "{\"corrected\":\"device result\"}");
    });
    var credentials = new MemoryCredentialSource();
    credentials.SetLicense(null, "device-safe");
    using var service = Service(handler, credentials);
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.HyperWhisperCloud));
    Assert(result.WasApplied, "device-auth cloud request failed");
    Assert(seen?.Body.Contains("\"device_id\":\"device-safe\"", StringComparison.Ordinal) == true, "device auth missing");
    Assert(seen?.Headers["X-LLM-Provider"] == "grok", "legacy empty-model fallback changed");
}

static async Task TestHyperWhisperCloudBaseUrl()
{
    // Before #282 the host was hardcoded to production with no switch, so every
    // dev run on this head billed production credits.
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, "{\"corrected\":\"staged\"}");
    });
    var credentials = new MemoryCredentialSource();
    credentials.SetLicense("license-secret", null);
    using var service = new CloudPostProcessingService(
        credentials,
        new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) },
        "https://transcribe-staging-v2.hyperwhisper.com");
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.HyperWhisperCloud));
    Assert(result.WasApplied, "staging cloud request failed");
    Assert(seen?.Uri == "https://transcribe-staging-v2.hyperwhisper.com/post-process",
        $"cloud base URL was not honoured: {seen?.Uri}");
}

static async Task TestHyperWhisperCloudTranscriptOnce()
{
    // macOS sent the transcript once, this head sent it twice — different input
    // tokens, different credit cost, different prompt for one recording.
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, "{\"corrected\":\"once\"}");
    });
    var credentials = new MemoryCredentialSource();
    credentials.SetLicense("license-secret", null);
    using var service = Service(handler, credentials);
    await service.ProcessAsync(Request(CloudPostProcessingProvider.HyperWhisperCloud));
    var body = seen?.Body ?? throw new InvalidOperationException("cloud request was not captured");
    var occurrences = body.Split("raw words").Length - 1;
    Assert(occurrences == 1, $"transcript appears {occurrences} times, expected 1");
    Assert(!body.Contains("--TRANSCRIPT--", StringComparison.Ordinal),
        "hosted route must not receive the wrapper markers");
}

static Task TestExistingEndpointRepair()
{
    // Tightening validation must never delete a saved endpoint or silently stop
    // a user's post-processing — it surfaces a repair prompt instead.
    var verdict = LlmPostProcessing.ValidateExistingCustomEndpoint(
        "llm.example.test/v1/chat/completions", "llama3");
    Assert(verdict.Status == PortableEndpointStatus.NeedsRepair, "schemeless endpoint was not flagged");
    Assert(verdict.IsUsable, "saved endpoint stopped working");
    Assert(verdict.Suggestion == "https://llm.example.test/v1/chat/completions", "no repair suggested");

    // Strict mode, used when saving, still refuses it.
    var strict = LlmPostProcessing.NormalizeCustomEndpoint(
        "llm.example.test/v1/chat/completions", "llama3");
    Assert(strict.Status == PortableEndpointStatus.Invalid, "strict mode accepted a schemeless URL");
    Assert(!strict.IsUsable, "strict mode handed back a callable URL");
    return Task.CompletedTask;
}

static Task TestCopyName()
{
    Assert(LlmPostProcessing.NextCopyName("Ollama") == "Ollama (copy)", "first copy name wrong");
    Assert(LlmPostProcessing.NextCopyName("Ollama (copy)") == "Ollama (copy 2)", "second copy name wrong");
    Assert(LlmPostProcessing.NextCopyName("Ollama (copy 2)") == "Ollama (copy 3)", "third copy name wrong");
    return Task.CompletedTask;
}

static async Task TestTruncatedCompletion()
{
    var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK,
        OpenAiResponse("partial", "length"))));
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.OpenAi, "secret"));
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.OpenAi));
    Assert(!result.WasApplied && result.Text == "raw words", "truncated output replaced transcript");
    Assert(result.Failure?.Code == CloudPostProcessingFailureCode.RejectedResponse, "wrong truncation failure");
}

static async Task TestHttpFailureRedaction()
{
    const string secret = "secret-that-must-never-escape";
    var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.Unauthorized,
        "{\"error\":\"credential secret-that-must-never-escape rejected\"}")));
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.OpenAi, secret));
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.OpenAi));
    Assert(result.Failure?.Code == CloudPostProcessingFailureCode.RequestFailed, "HTTP failure code mismatch");
    Assert(!JsonSerializer.Serialize(result).Contains(secret, StringComparison.Ordinal), "failure leaked credential");
}

static async Task TestCancellation()
{
    var handler = new StubHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Json(HttpStatusCode.OK, OpenAiResponse("never"));
    });
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.OpenAi, "secret"));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.OpenAi), cancellation.Token);
    Assert(result.Failure?.Code == CloudPostProcessingFailureCode.Cancelled, "cancellation did not degrade gracefully");
}

static async Task TestCredentialSecurity()
{
    var validBytes = Encoding.UTF8.GetBytes("  sk-valid  ");
    var store = new ByteStore(validBytes);
    var source = new CredentialStorePostProcessingCredentialSource(store, new DeviceIdentity());
    var credential = await source.GetCredentialAsync(CloudPostProcessingProvider.OpenAi, null);
    Assert(credential?.ApiKey == "sk-valid", "credential was not trimmed");
    Assert(validBytes.All(value => value == 0), "credential bytes were not cleared");

    var invalidBytes = new byte[] { 0xc3, 0x28 };
    store.Bytes = invalidBytes;
    credential = await source.GetCredentialAsync(CloudPostProcessingProvider.OpenAi, null);
    Assert(credential?.ApiKey is null, "invalid UTF-8 credential was accepted");
    Assert(invalidBytes.All(value => value == 0), "invalid credential bytes were not cleared");
}

static async Task TestOversizedResponse()
{
    var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]),
    }));
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.OpenAi, "secret"));
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.OpenAi));
    Assert(result.Failure?.Code == CloudPostProcessingFailureCode.RejectedResponse, "oversized response was not rejected");
}

static async Task TestModelFallback()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async request =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, OpenAiResponse("fallback"));
    });
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.Cerebras, "secret"));
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.Cerebras, "retired-model"));
    Assert(result.WasApplied, "fallback request failed");
    Assert(seen?.Body.Contains("\"model\":\"gpt-oss-120b\"", StringComparison.Ordinal) == true, "default model was not selected");
}

static async Task TestRedirectFailure()
{
    var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri("https://attacker.invalid/steal") },
    }));
    using var service = Service(handler, ApiKey(CloudPostProcessingProvider.OpenAi, "secret"));
    var result = await service.ProcessAsync(Request(CloudPostProcessingProvider.OpenAi));
    Assert(result.Failure?.Code == CloudPostProcessingFailureCode.RequestFailed, "redirect was accepted");
    Assert(handler.CallCount == 1, "credential-bearing request was replayed");
}

static CloudPostProcessingRequest Request(CloudPostProcessingProvider provider, string? model = null) =>
    new("raw words", "system prompt", "system info", provider, model);

static MemoryCredentialSource ApiKey(CloudPostProcessingProvider provider, string key)
{
    var source = new MemoryCredentialSource();
    source.Set(provider, key);
    return source;
}

static CloudPostProcessingService Service(StubHandler handler, IPostProcessingCredentialSource credentials) =>
    new(credentials, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json"),
};

static string OpenAiResponse(string content, string reason = "stop") =>
    JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = reason } } });

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handle;
    public int CallCount { get; private set; }

    public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : this((request, _) => handle(request)) { }
    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) => _handle = handle;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return _handle(request, cancellationToken);
    }
}

sealed record RequestSnapshot(string Uri, string? Authorization, Dictionary<string, string> Headers, string Body)
{
    public static async Task<RequestSnapshot> FromAsync(HttpRequestMessage request)
    {
        var headers = request.Headers.ToDictionary(item => item.Key, item => string.Join(",", item.Value), StringComparer.OrdinalIgnoreCase);
        return new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), headers,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync());
    }
}

sealed class MemoryCredentialSource : IPostProcessingCredentialSource
{
    private readonly Dictionary<(CloudPostProcessingProvider, Guid?), PostProcessingCredential> _items = [];
    public void Set(CloudPostProcessingProvider provider, string key, Guid? id = null) => _items[(provider, id)] = new(ApiKey: key);
    public void SetLicense(string? license, string? device) => _items[(CloudPostProcessingProvider.HyperWhisperCloud, null)] = new(LicenseKey: license, DeviceId: device);
    public ValueTask<PostProcessingCredential?> GetCredentialAsync(CloudPostProcessingProvider provider, Guid? customEndpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.GetValueOrDefault((provider, customEndpointId)));
    }
}

sealed class ByteStore(byte[] bytes) : ICredentialStore
{
    public byte[] Bytes { get; set; } = bytes;
    public PlatformResult<byte[]?> Read(string resource, string account) => PlatformResult<byte[]?>.Success(Bytes);
    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value) => PlatformResult.Success();
    public PlatformResult Delete(string resource, string account) => PlatformResult.Success();
}

sealed class DeviceIdentity : IDeviceIdentityProvider
{
    public PlatformResult<HyperWhisper.Platform.Abstractions.DeviceIdentity> GetDeviceIdentity() =>
        PlatformResult<HyperWhisper.Platform.Abstractions.DeviceIdentity>.Success(new("device-id", DeviceIdentitySource.StoredFallback));
}
