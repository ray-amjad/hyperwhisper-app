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
    ("post-processing prompts use shared Rust presets and dynamic context", () =>
    {
        var prompt = SharedCoreBridge.BuildPostProcessingPrompt(new PortablePromptContext(
            "hyper", "", "british", "English", "Keep names exact.", ["HyperWhisper"],
            true, true, false, "12:00", "UTC", "en-GB", "test-host"));
        Assert.True(prompt.SystemPrompt.Contains("<USER_SYSTEM_PROMPT>", StringComparison.Ordinal));
        Assert.True(prompt.SystemPrompt.Contains("Keep names exact.", StringComparison.Ordinal));
        Assert.True(prompt.SystemInfo.Contains("HyperWhisper", StringComparison.Ordinal));
        Assert.True(prompt.SystemInfo.Contains("test-host", StringComparison.Ordinal));
        return Task.CompletedTask;
    }),
    ("application and OCR context reaches the shared Rust prompt", () =>
    {
        var withoutContext = Prompt();
        var withContext = Prompt(
            AppType: "browser",
            AppName: "Firefox",
            Category: "browser",
            Description: "Compose message",
            TextFormat: "plain text",
            BrowserHost: "example.test",
            BrowserTabTitle: "Inbox",
            FocusedElement: "text area",
            FocusedContent: "Hello team",
            ScreenOcrText: "Visible screen text",
            AppTypeConfidence: "high",
            AppTypeSource: "desktop",
            HasApplicationContext: true);

        Assert.Equal(withoutContext.SystemPrompt, withContext.SystemPrompt);
        Assert.False(withoutContext.SystemInfo.Contains("<APPLICATION_CONTEXT>", StringComparison.Ordinal));
        Assert.True(withContext.SystemInfo.Contains("<APPLICATION_CONTEXT>", StringComparison.Ordinal));
        foreach (var expected in new[] { "Firefox", "browser", "Compose message", "plain text", "example.test", "Inbox", "text area", "Visible screen text", "high", "desktop" })
            Assert.True(withContext.SystemInfo.Contains(expected, StringComparison.Ordinal));
        return Task.CompletedTask;
    }),
    ("application prompt privacy bounds match desktop capture limits", () =>
    {
        var focused = new string('F', 100) + "FOCUSED_SECRET";
        var ocr = new string('O', 2000) + "OCR_SECRET";
        var boundedFocus = Prompt(
            AppType: "editor",
            AppName: "Editor",
            FocusedContent: focused,
            HasApplicationContext: true);
        var boundedOcr = Prompt(
            AppType: "editor",
            AppName: "Editor",
            ScreenOcrText: ocr,
            HasApplicationContext: true);

        Assert.DoesNotContain("FOCUSED_SECRET", boundedFocus.SystemInfo);
        Assert.True(boundedFocus.SystemInfo.Contains(new string('F', 100) + "...", StringComparison.Ordinal));
        Assert.DoesNotContain("OCR_SECRET", boundedOcr.SystemInfo);
        Assert.True(boundedOcr.SystemInfo.Contains(new string('O', 2000), StringComparison.Ordinal));
        return Task.CompletedTask;
    }),
    ("context fields are inert when context is absent", () =>
    {
        var baseline = Prompt();
        var disabled = Prompt(
            AppType: "browser",
            AppName: "must-not-appear",
            FocusedContent: "must-not-appear",
            ScreenOcrText: "must-not-appear",
            HasApplicationContext: false);
        Assert.Equal(baseline.SystemPrompt, disabled.SystemPrompt);
        Assert.Equal(baseline.SystemInfo, disabled.SystemInfo);
        return Task.CompletedTask;
    }),
    ("OCR-only snapshots produce application context without an app name", () =>
    {
        var prompt = Prompt(
            ScreenOcrText: "OCR-only visible text",
            HasApplicationContext: true);
        Assert.True(prompt.SystemInfo.Contains("<APPLICATION_CONTEXT>", StringComparison.Ordinal));
        Assert.True(prompt.SystemInfo.Contains("OCR-only visible text", StringComparison.Ordinal));
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
    ("live strategies construct and parse all five provider protocols", TestLiveProvidersAsync),
    ("live diagnostics redact credentials audio and transcript text", TestLiveDiagnosticsRedactionAsync),
    ("live provider failures and buffer limits are structured", TestLiveFailureAndBoundsAsync),
    ("live cancellation stops transport deterministically", TestLiveCancellationAsync),
    ("vocabulary egress normalizes through the shared core", () =>
    {
        // The canonical rule: sanitize (strip <>, collapse whitespace runs, cap
        // the term at the core's 80 chars), drop empties, dedupe
        // case-insensitively keeping first-seen order and casing, then cap.
        string[] words = ["  API  ", "", "api", "Rust<script>", "multi\n  word", "   "];
        Assert.Equal(
            "API|Rustscript|multi word",
            string.Join('|', SharedCoreBridge.NormalizeVocabularyTerms(words, null)));
        Assert.Equal(
            "API|Rustscript",
            string.Join('|', SharedCoreBridge.NormalizeVocabularyTerms(words, 2)));
        // limit 0 is .Take(0), NOT "uncapped".
        Assert.Equal(0, SharedCoreBridge.NormalizeVocabularyTerms(words, 0).Count);
        Assert.Equal(0, SharedCoreBridge.NormalizeVocabularyTerms(null, null).Count);
        // Sanitization truncates at 80 characters.
        Assert.Equal(80, SharedCoreBridge.NormalizeVocabularyTerms(new[] { new string('x', 150) }, null)[0].Length);
        return Task.CompletedTask;
    }),
    ("live protocol vocabulary keeps its own length drop and cap", TestLiveVocabularyAsync),
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

static PortablePostProcessingPrompt Prompt(
    string AppType = "other",
    string AppName = "",
    string Category = "",
    string Description = "",
    string TextFormat = "",
    string BrowserHost = "",
    string BrowserTabTitle = "",
    string FocusedElement = "",
    string FocusedContent = "",
    string ScreenOcrText = "",
    string AppTypeConfidence = "unknown",
    string AppTypeSource = "default",
    bool HasApplicationContext = false) =>
    SharedCoreBridge.BuildPostProcessingPrompt(new PortablePromptContext(
        "hyper", "", "british", "English", "", ["HyperWhisper"],
        true, true, false, "12:00", "UTC", "en-GB", "test-host",
        AppType, AppName, Category, Description, TextFormat, BrowserHost,
        BrowserTabTitle, FocusedElement, FocusedContent, ScreenOcrText,
        AppTypeConfidence, AppTypeSource, HasApplicationContext));

// The privacy flag is a required constructor argument — there is no default to
// fall back on. These cases assert wire shape, not the opt-out, so they pass the
// default-install answer: sharing on, which sends no header at all.
// The Linux composition suite covers `false` end to end.
static bool Sharing() => true;

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
            using var service = new CloudTranscriptionService(handler, new StaticCredentials(), Sharing);
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
            Sharing,
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
                Sharing,
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
        using var service = new CloudTranscriptionService(handler, new StaticCredentials(), Sharing, delay);
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
        using var service = new CloudTranscriptionService(handler, new StaticCredentials(), Sharing);
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
        using var service = new CloudTranscriptionService(handler, new StaticCredentials(), Sharing);
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

static async Task TestLiveProvidersAsync()
{
    var cases = new[]
    {
        new LiveCase(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "dg-secret", Language: "en", Vocabulary: ["Codex"]),
            "api.deepgram.com",
            [
                TextFrame("{\"type\":\"Metadata\",\"request_id\":\"r1\"}"),
                TextFrame("{\"type\":\"Results\",\"is_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"deep partial\"}]}}"),
                TextFrame("{\"type\":\"Results\",\"is_final\":true,\"channel\":{\"alternatives\":[{\"transcript\":\"deep final\"}]}}"),
                CloseFrame(),
            ],
            "deep final"),
        new LiveCase(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "el-secret", Language: "en-US"),
            "api.elevenlabs.io",
            [
                TextFrame("{\"message_type\":\"session_started\"}"),
                TextFrame("{\"message_type\":\"partial_transcript\",\"text\":\"eleven partial\"}"),
                TextFrame("{\"message_type\":\"committed_transcript\",\"text\":\"eleven final\"}"),
                CloseFrame(),
            ],
            "eleven final"),
        new LiveCase(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.OpenAi, ApiKey: "oa-secret", Language: "en"),
            "api.openai.com",
            [
                TextFrame("{\"type\":\"session.updated\",\"session\":{\"id\":\"s1\"}}"),
                TextFrame("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i1\",\"delta\":\"open partial\"}"),
                TextFrame("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"open final\"}"),
                CloseFrame(),
            ],
            "open final",
            4800),
        new LiveCase(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.Grok, ApiKey: "xai-secret", Language: "en", Vocabulary: ["Codex"]),
            "api.x.ai",
            [
                TextFrame("{\"type\":\"transcript.created\"}"),
                TextFrame("{\"type\":\"transcript.partial\",\"text\":\"grok partial\",\"is_final\":false}"),
                TextFrame("{\"type\":\"transcript.partial\",\"text\":\"grok final\",\"is_final\":true}"),
                TextFrame("{\"type\":\"transcript.done\",\"text\":\"grok final\"}"),
                CloseFrame(),
            ],
            "grok final"),
        new LiveCase(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.HyperWhisperCloud, LicenseKey: "hw-secret", Language: "en", Vocabulary: ["Codex"]),
            "transcribe-prod-v2.hyperwhisper.com",
            [
                TextFrame("{\"type\":\"ready\",\"sessionId\":\"s1\"}"),
                TextFrame("{\"type\":\"transcript\",\"text\":\"cloud partial\",\"is_final\":false}"),
                TextFrame("{\"type\":\"transcript\",\"text\":\"cloud final\",\"is_final\":true}"),
                TextFrame("{\"type\":\"session_complete\"}"),
                CloseFrame(),
            ],
            "cloud final"),
    };

    foreach (var value in cases)
    {
        var socket = new FakeStreamingWebSocket(value.Frames);
        var sink = new LiveSink();
        var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket), sink);
        var result = await service.TranscribeAsync(value.Config, Audio(value.AudioBytes));
        Assert.True(result.IsSuccess);
        Assert.Equal(value.Expected, result.Transcript);
        Assert.Equal(1, result.AudioChunksSent);
        Assert.Equal(value.Host, socket.Options!.Uri.Host);
        Assert.True(socket.Sent.Count > 0);
        Assert.True(sink.Updates.Any(update => update.IsFinal && update.Text == value.Expected));
    }
}

static async Task TestLiveDiagnosticsRedactionAsync()
{
    const string secret = "live-super-secret";
    const string transcript = "private dictated phrase";
    var socket = new FakeStreamingWebSocket(
    [
        TextFrame("{\"message_type\":\"session_started\"}"),
        TextFrame($"{{\"message_type\":\"committed_transcript\",\"text\":\"{transcript}\"}}"),
        CloseFrame(),
    ]);
    var diagnostics = new LiveDiagnostics();
    var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket), diagnostics: diagnostics);
    var result = await service.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: secret),
        Audio(320));
    Assert.True(result.IsSuccess);
    var rendered = string.Join("\n", diagnostics.Values.Select(value => value.ToString()));
    Assert.DoesNotContain(secret, rendered);
    Assert.DoesNotContain(transcript, rendered);
    Assert.DoesNotContain(Convert.ToBase64String(new byte[320]), rendered);
    Assert.DoesNotContain(secret, new LiveTranscriptionConfig(
        LiveTranscriptionProvider.ElevenLabs,
        ApiKey: secret).ToString());
    Assert.DoesNotContain(secret, socket.Options!.ToString());
}

static async Task TestLiveFailureAndBoundsAsync()
{
    Assert.Equal(24000, LiveCloudTranscriptionService.GetRequiredSampleRate(LiveTranscriptionProvider.OpenAi));
    Assert.Equal(16000, LiveCloudTranscriptionService.GetRequiredSampleRate(LiveTranscriptionProvider.Deepgram));

    var providerError = new FakeStreamingWebSocket(
    [
        TextFrame("{\"message_type\":\"auth_error\"}"),
        CloseFrame(),
    ]);
    var errorService = new LiveCloudTranscriptionService(new FakeWebSocketFactory(providerError));
    var error = await errorService.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "bad"),
        Audio(320));
    Assert.Equal(LiveTranscriptionFailureCode.Unauthorized, error.Failure!.Code);

    var oversized = new FakeStreamingWebSocket([]);
    var boundService = new LiveCloudTranscriptionService(new FakeWebSocketFactory(oversized));
    var bound = await boundService.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "key"),
        Audio(256 * 1024 + 1));
    Assert.Equal(LiveTranscriptionFailureCode.BufferLimit, bound.Failure!.Code);
    Assert.Equal(0, bound.AudioChunksSent);

    var oddPcm = new FakeStreamingWebSocket([]);
    var oddPcmService = new LiveCloudTranscriptionService(new FakeWebSocketFactory(oddPcm));
    var odd = await oddPcmService.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "key"),
        Audio(3));
    Assert.Equal(LiveTranscriptionFailureCode.InvalidRequest, odd.Failure!.Code);
    Assert.Equal(0, odd.AudioChunksSent);

    var inboundOversized = new FakeStreamingWebSocket(
    [
        new StreamingWebSocketFrame(
            new byte[1024 * 1024 + 1],
            System.Net.WebSockets.WebSocketMessageType.Text),
    ]);
    var inboundService = new LiveCloudTranscriptionService(new FakeWebSocketFactory(inboundOversized));
    var inbound = await inboundService.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "key"),
        Audio(320));
    Assert.Equal(LiveTranscriptionFailureCode.BufferLimit, inbound.Failure!.Code);

    var terminalClose = new FakeStreamingWebSocket(
    [
        new StreamingWebSocketFrame(
            [],
            System.Net.WebSockets.WebSocketMessageType.Close,
            CloseStatus: System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation),
    ]);
    var terminalService = new LiveCloudTranscriptionService(new FakeWebSocketFactory(terminalClose));
    var terminal = await terminalService.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "key"),
        Audio(320));
    Assert.Equal(LiveTranscriptionFailureCode.Protocol, terminal.Failure!.Code);
    Assert.Equal(1008, terminal.Failure.CloseStatus);
}

static async Task TestLiveCancellationAsync()
{
    var socket = new FakeStreamingWebSocket([]);
    var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket));
    using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    var result = await service.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "key"),
        BlockingAudio(source.Token),
        source.Token);
    Assert.Equal(LiveTranscriptionFailureCode.Cancelled, result.Failure!.Code);
}

/// <summary>
/// Pins <c>LiveTranscriptionProtocolFactory.Vocabulary</c> after it was routed
/// through the shared core: the core sanitizes/de-duplicates, each protocol
/// keeps its own length drop and term cap, and the length drop still runs
/// BEFORE the cap.
/// </summary>
static async Task TestLiveVocabularyAsync()
{
    var eightyFive = new string('a', 85);
    var oneFifty = new string('b', 150);
    string[] words = ["  API  ", "api", "Rust<script>", "multi\n  word", "", eightyFive, oneFifty];

    // Deepgram: chars = 100. Both over-long terms arrive truncated to the core's
    // 80-character term limit, so both now fit under 100 — the 150-char term used
    // to be dropped outright. Each term is its own `keyterm=` parameter.
    var deepgram = await ConnectQuery(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.Deepgram, ApiKey: "dg", Language: "en", Vocabulary: words));
    Assert.True(deepgram.Contains("keyterm=API&", StringComparison.Ordinal));
    Assert.True(deepgram.Contains("keyterm=Rustscript&", StringComparison.Ordinal));
    Assert.True(deepgram.Contains("keyterm=multi%20word&", StringComparison.Ordinal));
    Assert.Equal(1, CountOccurrences(deepgram, "keyterm=API"));
    Assert.Equal(5, CountOccurrences(deepgram, "keyterm="));
    Assert.True(deepgram.Contains($"keyterm={new string('a', 80)}", StringComparison.Ordinal));
    Assert.True(deepgram.Contains($"keyterm={new string('b', 80)}", StringComparison.Ordinal));

    // Grok: chars = 50. Truncation alone does not rescue an over-long term —
    // 80 is still over 50 — so both of these stay dropped.
    var grok = await ConnectQuery(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.Grok, ApiKey: "xai", Language: "en", Vocabulary: words));
    Assert.Equal(3, CountOccurrences(grok, "keyterm="));
    Assert.False(grok.Contains("aaaa", StringComparison.Ordinal));
    Assert.False(grok.Contains("bbbb", StringComparison.Ordinal));

    // ...but the OTHER two sanitizer steps do, and that IS a wire change even at
    // chars = 50. The length filter now measures the sanitized term, so a term
    // that bracket-stripping or whitespace-collapsing shrinks under the limit is
    // now sent where the old raw trim-only filter dropped it. Pinned here so the
    // next reader is not told "nothing changes at 50".
    var shrunk = await ConnectQuery(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.Grok, ApiKey: "xai", Language: "en", Vocabulary:
        [
            // 51 raw characters, 50 after `<` is dropped.
            "<" + new string('a', 50),
            // 68 raw characters, 49 after the 20-space run collapses to one.
            new string('c', 40) + new string(' ', 20) + new string('d', 8),
        ]));
    Assert.Equal(2, CountOccurrences(shrunk, "keyterm="));
    Assert.True(shrunk.Contains($"keyterm={new string('a', 50)}&", StringComparison.Ordinal));
    Assert.True(shrunk.Contains(
        $"keyterm={new string('c', 40)}%20{new string('d', 8)}&", StringComparison.Ordinal));

    // HyperWhisper Cloud: chars = 100, joined with ", " into one parameter.
    var cloud = await ConnectQuery(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.HyperWhisperCloud, LicenseKey: "hw", Language: "en",
        Vocabulary: ["  API  ", "api", "Rust<script>"]));
    Assert.True(cloud.Contains("vocabulary=API%2C%20Rustscript", StringComparison.Ordinal));

    // The per-protocol cap runs after the length drop, so the shorter terms
    // still fill the budget rather than being spent on dropped ones.
    var capped = await ConnectQuery(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.Grok, ApiKey: "xai", Language: "en",
        Vocabulary: [.. Enumerable.Range(0, 260).Select(index =>
            index % 2 == 0 ? new string('z', 60) + index : $"term{index}")]));
    Assert.Equal(100, CountOccurrences(capped, "keyterm="));
    Assert.False(capped.Contains("zzzz", StringComparison.Ordinal));
}

static async Task<string> ConnectQuery(LiveTranscriptionConfig config)
{
    var socket = new FakeStreamingWebSocket([CloseFrame()]);
    var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket));
    await service.TranscribeAsync(config, Audio(320));
    Assert.NotNull(socket.Options);
    return socket.Options!.Uri.Query + "&";
}

static int CountOccurrences(string haystack, string needle)
{
    var count = 0;
    for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
         index >= 0;
         index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
    {
        count++;
    }
    return count;
}

static async IAsyncEnumerable<ReadOnlyMemory<byte>> Audio(int byteCount)
{
    await Task.CompletedTask;
    yield return new byte[byteCount];
}

static async IAsyncEnumerable<ReadOnlyMemory<byte>> BlockingAudio(
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    yield break;
}

static StreamingWebSocketFrame TextFrame(string value) =>
    new(Encoding.UTF8.GetBytes(value), System.Net.WebSockets.WebSocketMessageType.Text);

static StreamingWebSocketFrame CloseFrame() =>
    new([], System.Net.WebSockets.WebSocketMessageType.Close, CloseStatus: System.Net.WebSockets.WebSocketCloseStatus.NormalClosure);

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

sealed record LiveCase(
    LiveTranscriptionConfig Config,
    string Host,
    IReadOnlyList<StreamingWebSocketFrame> Frames,
    string Expected,
    int AudioBytes = 320);

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

sealed class FakeWebSocketFactory(FakeStreamingWebSocket socket) : IStreamingWebSocketFactory
{
    public IStreamingWebSocket Create() => socket;
}

sealed class FakeStreamingWebSocket(IEnumerable<StreamingWebSocketFrame> frames) : IStreamingWebSocket
{
    private readonly Queue<StreamingWebSocketFrame> _frames = new(frames);
    public StreamingWebSocketConnectOptions? Options { get; private set; }
    public List<(byte[] Data, System.Net.WebSockets.WebSocketMessageType Type)> Sent { get; } = [];

    public Task ConnectAsync(StreamingWebSocketConnectOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Options = options;
        return Task.CompletedTask;
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> data,
        System.Net.WebSockets.WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Add((data.ToArray(), messageType));
        return Task.CompletedTask;
    }

    public async Task<StreamingWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_frames.Count > 0)
        {
            await Task.Yield();
            return _frames.Dequeue();
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }

    public Task CloseAsync(
        System.Net.WebSockets.WebSocketCloseStatus status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class LiveSink : ILiveTranscriptSink
{
    public List<LiveTranscriptUpdate> Updates { get; } = [];
    public void OnTranscript(LiveTranscriptUpdate update) => Updates.Add(update);
}

sealed class LiveDiagnostics : ILiveTranscriptionDiagnostics
{
    public List<LiveTranscriptionDiagnostic> Values { get; } = [];
    public void OnDiagnostic(LiveTranscriptionDiagnostic diagnostic) => Values.Add(diagnostic);
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
