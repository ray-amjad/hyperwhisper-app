using System.Net;
using System.Text;
using HyperWhisper.SharedCore;

var tests = new (string Name, Func<Task> Run)[]
{
    ("CJK detection crosses the Linux UniFFI boundary", () =>
    {
        Assert.False(SharedCoreBridge.ContainsCjk("HyperWhisper"));
        Assert.True(SharedCoreBridge.ContainsCjk("音声"));
        return Task.CompletedTask;
    }),
    ("application types normalize through the shared catalog", () =>
    {
        Assert.Equal("terminal", SharedCoreBridge.NormalizeAppType("Terminal"));
        return Task.CompletedTask;
    }),
    ("language-aware spacing stays in the shared core", () =>
    {
        Assert.Equal("hello ", SharedCoreBridge.AppendTrailingSpace("hello", "en"));
        return Task.CompletedTask;
    }),
    ("backup validation returns structured failures", () =>
    {
        Assert.True(SharedCoreBridge.ValidateBackup("{}").Count > 0);
        return Task.CompletedTask;
    }),
    ("cloud catalog enumerates every batch provider", () =>
    {
        var providers = CloudTranscriptionService.Providers;
        Assert.Equal(12, providers.Count);
        Assert.Equal(12, providers.Select(value => value.Provider).Distinct().Count());
        Assert.True(providers.All(value => value.SupportsBatch));
        Assert.Equal(3, providers.Count(value => value.IsMultiStep));
        return Task.CompletedTask;
    }),
    ("single-shot providers use Rust request and response contracts", TestSingleShotProvidersAsync),
    ("multi-step providers execute upload poll parse and cleanup flows", TestMultiStepProvidersAsync),
    ("observer diagnostics redact credentials and request bodies", TestObserverRedactionAsync),
    ("retry policy retries transient responses deterministically", TestRetryAsync),
    ("unauthorized responses are classified without leaking provider bodies", TestUnauthorizedAsync),
    ("cancellation stops in-flight HTTP and returns structured cancellation", TestCancellationAsync),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static async Task TestSingleShotProvidersAsync()
{
    var cases = new[]
    {
        new ProviderCase(CloudTranscriptionProvider.OpenAi, "whisper-1", "{\"text\":\"openai text\"}", "openai text"),
        new ProviderCase(CloudTranscriptionProvider.Groq, "whisper-large-v3", "{\"text\":\"groq text\"}", "groq text"),
        new ProviderCase(CloudTranscriptionProvider.ElevenLabs, "scribe_v2", "{\"text\":\"eleven text\"}", "eleven text"),
        new ProviderCase(CloudTranscriptionProvider.Mistral, "voxtral-mini-latest", "{\"text\":\"mistral text\"}", "mistral text"),
        new ProviderCase(CloudTranscriptionProvider.Grok, "grok-2-audio", "{\"text\":\"grok text\"}", "grok text"),
        new ProviderCase(CloudTranscriptionProvider.Deepgram, "nova-3", "{\"results\":{\"channels\":[{\"alternatives\":[{\"transcript\":\"deepgram text\"}]}]}}", "deepgram text"),
        new ProviderCase(CloudTranscriptionProvider.AzureMai, "mai-1.5", "{\"text\":\"azure text\",\"cost\":{\"credits\":1.0}}", "azure text", true),
        new ProviderCase(CloudTranscriptionProvider.GoogleChirp, "chirp_3", "{\"text\":\"chirp text\",\"cost\":{\"credits\":1.0}}", "chirp text", true),
        new ProviderCase(CloudTranscriptionProvider.HyperWhisperCloud, "", "{\"text\":\"cloud text\",\"credits_remaining\":99}", "cloud text", true),
    };

    var audio = TempAudio();
    try
    {
        foreach (var value in cases)
        {
            var handler = new RecordingHandler((_, _) => Json(value.Response));
            using var service = new CloudTranscriptionService(handler, new StaticCredentials());
            var request = new CloudTranscriptionRequest(
                value.Provider,
                audio,
                value.Model,
                Language: "en",
                Vocabulary: ["UniFFI"],
                BaseUrl: value.UsesLicense ? "https://routing.test" : null,
                RoutedProvider: value.Provider switch
                {
                    CloudTranscriptionProvider.AzureMai => "azure-mai",
                    CloudTranscriptionProvider.GoogleChirp => "google-chirp",
                    _ => null,
                });
            var result = await service.TranscribeAsync(request);
            Assert.True(result.IsSuccess);
            Assert.Equal(value.Expected, result.Transcript!.Text);
            Assert.Equal(1, result.Attempts);
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
            Assert.True(handler.LastBodyLength > 0);
        }
    }
    finally
    {
        File.Delete(audio);
    }
}

static async Task TestObserverRedactionAsync()
{
    const string secret = "super-secret-api-key";
    const string audioMarker = "private-audio-body";
    var audio = TempAudio(audioMarker);
    try
    {
        var observer = new RecordingObserver();
        var handler = new RecordingHandler((_, _) => Json("{\"text\":\"safe\"}"));
        using var service = new CloudTranscriptionService(
            handler,
            new StaticCredentials(secret),
            new ImmediateDelay(),
            observer);
        var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
            CloudTranscriptionProvider.OpenAi,
            audio,
            "whisper-1"));
        Assert.True(result.IsSuccess);
        var diagnostics = string.Join("\n", observer.Events.Select(value => value.ToString()));
        Assert.DoesNotContain(secret, diagnostics);
        Assert.DoesNotContain(audioMarker, diagnostics);
        Assert.DoesNotContain("Authorization", diagnostics);
    }
    finally
    {
        File.Delete(audio);
    }
}

static async Task TestMultiStepProvidersAsync()
{
    var audio = TempAudio();
    try
    {
        var cases = new[]
        {
            new MultiStepCase(
                CloudTranscriptionProvider.AssemblyAi,
                "universal-2",
                [
                    Json("{\"upload_url\":\"https://upload.test/audio\"}"),
                    Json("{\"id\":\"transcript_abc\",\"status\":\"queued\"}"),
                    Json("{\"id\":\"transcript_abc\",\"status\":\"completed\",\"text\":\"assembly text\"}"),
                ],
                "assembly text",
                3),
            new MultiStepCase(
                CloudTranscriptionProvider.Soniox,
                "stt-async-v5",
                [
                    Json("{\"id\":\"file_abc\"}"),
                    Json("{\"id\":\"tx_123\"}"),
                    Json("{\"id\":\"tx_123\",\"status\":\"completed\"}"),
                    Json("{\"id\":\"tx_123\",\"text\":\"soniox text\"}"),
                    Json("{}"),
                    Json("{}"),
                ],
                "soniox text",
                4),
            new MultiStepCase(
                CloudTranscriptionProvider.Gemini,
                "gemini-2.5-flash",
                [
                    JsonWithHeader("", "X-Goog-Upload-URL", "https://upload.test/gemini"),
                    Json("{\"file\":{\"name\":\"files/abc\",\"uri\":\"https://gen.test/files/abc\",\"state\":\"ACTIVE\",\"mimeType\":\"audio/wav\"}}"),
                    Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"gemini text\"}]}}]}"),
                    Json("{}"),
                ],
                "gemini text",
                3),
        };

        foreach (var value in cases)
        {
            var handler = new QueueHandler(value.Responses);
            using var service = new CloudTranscriptionService(
                handler,
                new StaticCredentials(),
                new ImmediateDelay());
            var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
                value.Provider,
                audio,
                value.Model));
            Assert.True(result.IsSuccess);
            Assert.Equal(value.Expected, result.Transcript!.Text);
            Assert.Equal(value.ExpectedAttempts, result.Attempts);
            Assert.Equal(0, handler.Remaining);
        }
    }
    finally
    {
        File.Delete(audio);
    }
}

static async Task TestRetryAsync()
{
    var count = 0;
    var audio = TempAudio();
    try
    {
        var handler = new RecordingHandler((_, _) =>
        {
            count++;
            return count == 1
                ? Json("{\"error\":\"temporary\"}", HttpStatusCode.ServiceUnavailable)
                : Json("{\"text\":\"after retry\"}");
        });
        var delay = new ImmediateDelay();
        using var service = new CloudTranscriptionService(handler, new StaticCredentials(), delay);
        var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
            CloudTranscriptionProvider.Groq,
            audio,
            "whisper-large-v3"));
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(1, delay.Delays.Count);
        Assert.True(delay.Delays[0] > TimeSpan.Zero);
    }
    finally
    {
        File.Delete(audio);
    }
}

static async Task TestUnauthorizedAsync()
{
    const string hostileBody = "credential=must-not-surface";
    var audio = TempAudio();
    try
    {
        var handler = new RecordingHandler((_, _) => Json(hostileBody, HttpStatusCode.Unauthorized));
        using var service = new CloudTranscriptionService(handler, new StaticCredentials());
        var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
            CloudTranscriptionProvider.OpenAi,
            audio,
            "whisper-1"));
        Assert.False(result.IsSuccess);
        Assert.Equal(CloudTranscriptionErrorCode.Unauthorized, result.Failure!.Code);
        Assert.Equal(401, result.Failure.HttpStatus);
        Assert.DoesNotContain(hostileBody, result.Failure.Message);
    }
    finally
    {
        File.Delete(audio);
    }
}

static async Task TestCancellationAsync()
{
    var audio = TempAudio();
    try
    {
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{\"text\":\"never\"}");
        });
        using var service = new CloudTranscriptionService(handler, new StaticCredentials());
        using var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMilliseconds(20));
        var result = await service.TranscribeAsync(
            new CloudTranscriptionRequest(CloudTranscriptionProvider.OpenAi, audio, "whisper-1"),
            source.Token);
        Assert.Equal(CloudTranscriptionErrorCode.Cancelled, result.Failure!.Code);
        Assert.Equal(1, result.Attempts);
    }
    finally
    {
        File.Delete(audio);
    }
}

static string TempAudio(string content = "RIFF-test-audio")
{
    var path = Path.Combine(Path.GetTempPath(), $"hyperwhisper-shared-{Guid.NewGuid():N}.wav");
    File.WriteAllText(path, content);
    return path;
}

static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json"),
};

static HttpResponseMessage JsonWithHeader(string body, string name, string value)
{
    var response = Json(body);
    response.Headers.TryAddWithoutValidation(name, value);
    return response;
}

sealed record ProviderCase(
    CloudTranscriptionProvider Provider,
    string Model,
    string Response,
    string Expected,
    bool UsesLicense = false);

sealed record MultiStepCase(
    CloudTranscriptionProvider Provider,
    string Model,
    IReadOnlyList<HttpResponseMessage> Responses,
    string Expected,
    int ExpectedAttempts);

sealed class StaticCredentials(string apiKey = "test-api-key") : ICloudCredentialSource
{
    public ValueTask<CloudCredential?> GetCredentialAsync(
        CloudTranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CloudCredential?>(new CloudCredential(apiKey, "test-license-key", "test-device"));
    }
}

sealed class ImmediateDelay : ICloudTranscriptionDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

sealed class RecordingObserver : ICloudTranscriptionObserver
{
    public List<CloudTranscriptionEvent> Events { get; } = [];
    public void OnEvent(CloudTranscriptionEvent value) => Events.Add(value);
}

sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : this((request, token) => Task.FromResult(response(request, token)))
    {
    }

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
    {
        _response = response;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public int LastBodyLength { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            LastRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        LastBodyLength = request.Content is null
            ? 0
            : (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Length;
        return await _response(request, cancellationToken);
    }
}

sealed class QueueHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);
    public int Remaining => _responses.Count;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected request: {request.Method}");
        }
        return Task.FromResult(_responses.Dequeue());
    }
}

static class Assert
{
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value) => True(!value);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    public static void NotNull(object? value)
    {
        if (value is null) throw new InvalidOperationException("Expected non-null value.");
    }

    public static void DoesNotContain(string value, string actual)
    {
        if (actual.Contains(value, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected sensitive value '{value}'.");
    }
}
