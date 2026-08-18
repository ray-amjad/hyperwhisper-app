// SMOKE TESTS
//
// Fast, dependency-free checks run in release CI (windows-release.yml) after the
// Rust core DLL is built and before installers are produced:
//   1. FFI touch — the FIRST call into hyperwhisper_core.dll trips the UniFFI
//      contract-version + API-checksum verification (uniffiCheckApiChecksums),
//      catching generated-binding ↔ DLL drift.
//   2. Unit-style checks over the pieces the 1.8.0 release review flagged as
//      highest-risk: RustRetry transport/timeout caps + per-attempt client
//      resolution + cancellation ordering, Grok's size-scaled timeout, the
//      hardened vocabulary replacement, phonetic matching, and the Parakeet
//      reload predicates.
//   3. The original WPF initialization smoke (BackupExportSettingsPage).
//
// Requires InternalsVisibleTo("HyperWhisper.SmokeTests") in HyperWhisper.csproj.
// Coverage is x64-only on the CI runner; ARM64 is exercised manually.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.Streaming;
using HyperWhisper.Services.Transcription;
using HyperWhisper.Utilities;
using HyperWhisper.ViewModels;
using HyperWhisper.Views.Pages.Settings;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.SmokeTests;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HyperWhisper.SmokeTests",
            Guid.NewGuid().ToString("N"));

        Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, tempRoot);

        try
        {
            Directory.CreateDirectory(tempRoot);

            // FFI touch FIRST: the first call into the native DLL runs the UniFFI
            // contract-version + checksum handshake — binding↔DLL drift fails
            // here, before anything else can produce a confusing error.
            Run("ffi checksum touch (CloudSttCreditsPerMinute)", () =>
            {
                var credits = HyperwhisperCoreMethods.CloudSttCreditsPerMinute("balanced");
                Assert(credits >= 0, $"expected credits/min >= 0, got {credits}");
            });

            Run("ApplyHardenedReplacement keeps $-tokens literal", () =>
            {
                var result = HyperwhisperCoreMethods.ApplyHardenedReplacement(
                    "use claude code", "claude code", "X $0 $& $$");
                Assert(result == "use X $0 $& $$", $"got '{result}'");
            });

            Run("PhoneticEncode returns codes", () =>
            {
                var codes = HyperwhisperCoreMethods.PhoneticEncode("whisper");
                Assert(codes.Count > 0, "expected non-empty phonetic codes");
            });

            Run("PhoneticVocabularyMatcher corrects a misrecognition", () =>
            {
                var matcher = new PhoneticVocabularyMatcher(new[]
                {
                    new VocabularyItem { Word = "Whisper", Replacement = null }
                });
                var corrected = matcher.Apply("hyper wisper");
                Assert(corrected == "hyper Whisper", $"got '{corrected}'");
            });

            Run("IsNoSpaceLanguage / NormalizeLanguage truth tables", () =>
            {
                foreach (var code in new[] { "ja", "zh", "ko", "yue" })
                    Assert(ParakeetTranscriptionService.IsNoSpaceLanguage(code), $"{code} should be no-space");
                foreach (var code in new[] { "en", "fr", "auto" })
                    Assert(!ParakeetTranscriptionService.IsNoSpaceLanguage(code), $"{code} should be spaced");

                Assert(ParakeetTranscriptionService.NormalizeLanguage(null) == "auto", "null → auto");
                Assert(ParakeetTranscriptionService.NormalizeLanguage("  ") == "auto", "whitespace → auto");
                Assert(ParakeetTranscriptionService.NormalizeLanguage("AUTO") == "auto", "AUTO → auto");
                Assert(ParakeetTranscriptionService.NormalizeLanguage(" EN ") == "en", "' EN ' → en");
            });

            Run("Grok GetRequestTimeout scales with file size", () =>
            {
                Assert(GrokSttService.GetRequestTimeout(0) == TimeSpan.FromMinutes(5), "0 bytes → 5min base");
                Assert(GrokSttService.GetRequestTimeout(100L * 1024 * 1024) == TimeSpan.FromMinutes(10),
                    "100MB → 5min + 300s");
                Assert(GrokSttService.GetRequestTimeout(600L * 1024 * 1024) == TimeSpan.FromMinutes(30),
                    "600MB → capped at 30min");
            });

            Run("XaiFormattingLanguages shared between Grok batch and streaming", () =>
            {
                Assert(XaiFormattingLanguages.TryGetSupportedCode("en", out var en) && en == "en", "en supported");
                Assert(XaiFormattingLanguages.TryGetSupportedCode("EN-US", out var enUs) && enUs == "en",
                    "EN-US → en");
                Assert(XaiFormattingLanguages.TryGetSupportedCode("tl", out var tl) && tl == "fil",
                    "tl aliases to fil");
                Assert(!XaiFormattingLanguages.TryGetSupportedCode("auto", out _), "auto unsupported");
                Assert(!XaiFormattingLanguages.TryGetSupportedCode(null, out _), "null unsupported");
                Assert(!XaiFormattingLanguages.TryGetSupportedCode("zz", out _), "unknown code unsupported");
            });

            Run("OpenAI post-processing omits an output-token cap", () =>
            {
                var requestJson = PostProcessingService.BuildOpenAIRequestJson(
                    OpenAICompatibleProvider.OpenAI, "gpt-4.1-nano", "system", "user");
                using var request = JsonDocument.Parse(requestJson);

                Assert(!request.RootElement.TryGetProperty("max_tokens", out _),
                    "OpenAI request should not contain max_tokens");
                Assert(!request.RootElement.TryGetProperty("max_completion_tokens", out _),
                    "OpenAI request should not contain max_completion_tokens");
            });

            Run("Groq post-processing sends an explicit output-token cap", () =>
            {
                var requestJson = PostProcessingService.BuildOpenAIRequestJson(
                    OpenAICompatibleProvider.Groq, "openai/gpt-oss-20b", "system", "user");
                using var request = JsonDocument.Parse(requestJson);

                Assert(request.RootElement.GetProperty("max_completion_tokens").GetInt32()
                        == PostProcessingService.GroqMaxCompletionTokens,
                    "Groq request should cap completions at GroqMaxCompletionTokens");
                Assert(!request.RootElement.TryGetProperty("max_tokens", out _),
                    "Groq request should use max_completion_tokens, not max_tokens");
            });

            Run("Custom endpoint pointed at Groq's API is recognized", () =>
            {
                Assert(PostProcessingService.IsGroqEndpoint("https://api.groq.com/openai/v1/chat/completions"),
                    "api.groq.com should be recognized as a Groq endpoint");
                Assert(PostProcessingService.IsGroqEndpoint("https://API.GROQ.COM/openai/v1/chat/completions"),
                    "host match should be case-insensitive");
                Assert(!PostProcessingService.IsGroqEndpoint("http://localhost:1234/v1/chat/completions"),
                    "a local/self-hosted endpoint should not be recognized as Groq");
                Assert(!PostProcessingService.IsGroqEndpoint("not a url"),
                    "an unparsable URL should not be recognized as Groq");
            });

            Run("Deepgram parses every message shape of its \"channel\" field", () =>
            {
                var strategy = new DeepgramStreamingStrategy();

                // "channel":[0,1] — the array form used by the endpointing frames.
                Assert(strategy.ParseMessage("""{"type":"SpeechStarted","channel":[0,1],"timestamp":1.2}""")
                        is StreamingProviderEvent.Metadata,
                    "SpeechStarted should parse to a Metadata event");
                Assert(strategy.ParseMessage("""{"type":"UtteranceEnd","channel":[0,1],"last_word_end":2.5}""")
                        is StreamingProviderEvent.Metadata,
                    "UtteranceEnd should parse to a Metadata event");

                // "channel":{...} — the transcript form, which must keep working.
                Assert(strategy.ParseMessage(
                        """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"hello"}]}}""")
                        is StreamingProviderEvent.FinalTranscript { Text: "hello" },
                    "Results should still yield its transcript");
            });

            // These checks call straight into the generated FFI surface
            // (HyperwhisperCoreMethods.EvaluateLlmResponseJson / EvaluateCompletion /
            // NormalizeTermination), which doubles as the uniffi API-checksum drift
            // gate for the completion-policy functions on the real Windows DLL.

            Run("OpenAI wire: complete + wrapped content is accepted", () =>
            {
                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>>clean transcript<<END>>"},"finish_reason":"stop"}]}""",
                    raw);

                Assert(evaluation.accepted, $"rejected as {evaluation.failure}");
                Assert(evaluation.text == "clean transcript", $"got '{evaluation.text}'");
            });

            Run("OpenAI wire: finish_reason=length is rejected and returns original", () =>
            {
                const string raw = "complete raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>>partial output"},"finish_reason":"length"}]}""",
                    raw);

                Assert(!evaluation.accepted, "length response should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
                Assert(evaluation.failure == CompletionFailure.OutputLimit, $"failure {evaluation.failure}");
            });

            Run("OpenAI wire: missing finish_reason proceeds (Unspecified)", () =>
            {
                Assert(
                    HyperwhisperCoreMethods.NormalizeTermination(WireProtocol.OpenAiChat, null) == CompletionState.Unspecified,
                    "missing finish_reason should normalize to Unspecified");

                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>>clean<<END>>"}}]}""",
                    raw);

                Assert(evaluation.accepted, $"missing finish_reason should still proceed to lenient evaluation, got {evaluation.failure}");
                Assert(evaluation.text == "clean", $"got '{evaluation.text}'");
            });

            Run("OpenAI wire: markerless content is accepted (lenient variant matching)", () =>
            {
                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"clean transcript, no markers"},"finish_reason":"stop"}]}""",
                    raw);

                Assert(evaluation.accepted, $"markerless complete content should be accepted leniently, got {evaluation.failure}");
                Assert(evaluation.text == "clean transcript, no markers", $"got '{evaluation.text}'");
            });

            Run("OpenAI wire: malformed JSON is rejected and returns original", () =>
            {
                const string raw = "complete raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    "not json",
                    raw);

                Assert(!evaluation.accepted, "malformed body should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
            });

            Run("wrapped prompt leakage is rejected", () =>
            {
                const string raw = "raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.OpenAiChat,
                    """{"choices":[{"message":{"content":"<<CLEANED>><APPLICATION_CONTEXT>\nApp: Mail\n</APPLICATION_CONTEXT><<END>>"},"finish_reason":"stop"}]}""",
                    raw);

                Assert(!evaluation.accepted, "leaked application-context content should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
                Assert(evaluation.failure == CompletionFailure.PromptLeakage, $"failure {evaluation.failure}");
            });

            Run("OpenAI-compatible providers retain their endpoints", () =>
            {
                Assert(OpenAICompatibleProvider.OpenAI.Endpoint() == "https://api.openai.com/v1/chat/completions", "OpenAI endpoint");
                Assert(OpenAICompatibleProvider.Groq.Endpoint() == "https://api.groq.com/openai/v1/chat/completions", "Groq endpoint");
                Assert(OpenAICompatibleProvider.Grok.Endpoint() == "https://api.x.ai/v1/chat/completions", "Grok endpoint");
                Assert(OpenAICompatibleProvider.Gemini.Endpoint() == "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "Gemini endpoint");
                Assert(OpenAICompatibleProvider.Cerebras.Endpoint() == "https://api.cerebras.ai/v1/chat/completions", "Cerebras endpoint");
                Assert(OpenAICompatibleProvider.Mistral.Endpoint() == "https://api.mistral.ai/v1/chat/completions", "Mistral endpoint");
            });

            Run("Anthropic keeps its required 8192 output limit", () =>
            {
                var requestJson = PostProcessingService.BuildAnthropicRequestJson("model", "system", "user");
                using var request = JsonDocument.Parse(requestJson);
                Assert(request.RootElement.GetProperty("max_tokens").GetInt32() == 8192, "Anthropic max_tokens should be 8192");
            });

            Run("Anthropic wire: max_tokens stop is rejected", () =>
            {
                const string raw = "complete raw transcript";
                var evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                    WireProtocol.AnthropicMessages,
                    """{"content":[{"type":"text","text":"<<CLEANED>>partial<<END>>"}],"stop_reason":"max_tokens"}""",
                    raw);

                Assert(!evaluation.accepted, "max_tokens response should be rejected");
                Assert(evaluation.text == raw, "raw transcript was not preserved");
                Assert(evaluation.failure == CompletionFailure.OutputLimit, $"failure {evaluation.failure}");

                Assert(HyperwhisperCoreMethods.NormalizeTermination(WireProtocol.AnthropicMessages, "end_turn") == CompletionState.Complete,
                    "end_turn should be complete");
                Assert(HyperwhisperCoreMethods.NormalizeTermination(WireProtocol.AnthropicMessages, "max_tokens") == CompletionState.OutputLimit,
                    "max_tokens should be output-limited");
            });

            Run("Local/in-process completions (Unspecified state) require lenient acceptance", () =>
            {
                const string raw = "complete raw transcript";
                var rejected = HyperwhisperCoreMethods.EvaluateCompletion(raw, "", CompletionState.Unspecified);
                var accepted = HyperwhisperCoreMethods.EvaluateCompletion(raw, "<<CLEANED>>clean<<END>>", CompletionState.Unspecified);
                var acceptedMarkerless = HyperwhisperCoreMethods.EvaluateCompletion(raw, "clean, no markers", CompletionState.Unspecified);

                Assert(!rejected.accepted && rejected.text == raw, "empty unspecified response should preserve raw text");
                Assert(accepted.accepted && accepted.text == "clean", "wrapped unspecified response should be accepted");
                Assert(acceptedMarkerless.accepted && acceptedMarkerless.text == "clean, no markers",
                    "markerless unspecified response should be accepted leniently");
            });

            Run("Local LLM reserves an 8192-token output without context shifting", () =>
            {
                Assert(LocalLlmService.MaxTokens == 8_192, "local output ceiling should be 8192");
                Assert(LocalLlmService.ContextSize >= 16_384, "local context should be at least 16384");

                var promptBudget = LocalLlmService.ContextSize
                    - LocalLlmService.MaxTokens
                    - LocalLlmService.ChatTemplateTokenReserve;
                LocalLlmService.EnsureTokenBudgetFits(500, promptBudget - 500);

                Expect<InvalidOperationException>(() =>
                    LocalLlmService.EnsureTokenBudgetFits(500, promptBudget - 499));
            });

            RunAsync("RustRetry caps transport failures at 4 and resolves the client per attempt", async () =>
            {
                var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
                using var client = new HttpClient(handler);
                var providerCalls = 0;

                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    () => { providerCalls++; return client; },
                    BuildDummyRequest,
                    _ => new TranscriptionException(TranscriptionErrorCode.Unknown, "unexpected"),
                    CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.NetworkError, $"code {ex.Code}");
                Assert(handler.Sends == RustRetry.MaxTransportAttempts, $"sends {handler.Sends}");
                Assert(providerCalls == RustRetry.MaxTransportAttempts, $"provider calls {providerCalls}");
            });

            RunAsync("RustRetry caps per-attempt timeouts at 2", async () =>
            {
                var handler = new StubHandler(async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("unreachable");
                });
                using var client = new HttpClient(handler);

                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    client,
                    BuildDummyRequest,
                    _ => new TranscriptionException(TranscriptionErrorCode.Unknown, "unexpected"),
                    CancellationToken.None,
                    perAttemptTimeout: TimeSpan.FromMilliseconds(100)));

                Assert(ex.Code == TranscriptionErrorCode.NetworkError, $"code {ex.Code}");
                Assert(handler.Sends == RustRetry.MaxTimeoutAttempts, $"sends {handler.Sends}");
            });

            RunAsync("RustRetry never retries caller cancellation", async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                var handler = new StubHandler(async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("unreachable");
                });
                using var client = new HttpClient(handler);

                await ExpectAsync<OperationCanceledException>(() => RustRetry.PerformAsync(
                    client,
                    BuildDummyRequest,
                    _ => new TranscriptionException(TranscriptionErrorCode.Unknown, "unexpected"),
                    cts.Token,
                    perAttemptTimeout: TimeSpan.FromSeconds(30)));

                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            RunAsync("RustRetry gives up immediately on a non-retryable status via parseError", async () =>
            {
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"bad\"}")
                }));
                using var client = new HttpClient(handler);

                var ex = await ExpectAsync<TranscriptionException>(() => RustRetry.PerformAsync(
                    client,
                    BuildDummyRequest,
                    resp => new TranscriptionException(TranscriptionErrorCode.InvalidRequest, "mapped", null, (int)resp.@status),
                    CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.InvalidRequest, $"code {ex.Code}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            // RustSingleShot now solely owns build-error mapping, the retry
            // give-up mapping, the post-retry cancellation check, parse-error
            // mapping and the completion log for the services that call it. Its
            // build and parse arguments are caller-supplied delegates, so the
            // whole sequence is exercisable here without an FFI call or a
            // network hop.
            //
            // Each case below asserts WHICH path produced its outcome, not just
            // that something happened, via one shared mechanism: the test's own
            // build/parse delegates append to a `steps` list, and the case asserts
            // the exact ordered join. "Threw" alone is not evidence — the runner
            // hands the same parseResponse delegate to RustRetry as its give-up
            // mapper, so several distinct paths produce indistinguishable
            // exceptions unless something pins the path down.

            RunAsync("RustSingleShot maps a build-fn core error with the provider tag", async () =>
            {
                var handler = new StubHandler(_ => throw new InvalidOperationException("must not send"));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "Groq",
                    buildRequest: () => { steps.Add("build"); throw new HwTranscriptionException.Unauthorized(); },
                    parseResponse: _ => { steps.Add("parse"); throw new InvalidOperationException("must not parse"); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.Unauthorized, $"code {ex.Code}");
                Assert(ex.ProviderName == "Groq", $"provider {ex.ProviderName}");
                Assert(ex.Message == "Invalid Groq API key", $"message {ex.Message}");
                // The build fn threw before the executor was reached, and nothing
                // downstream ran.
                Assert(string.Join(",", steps) == "build", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 0, $"sends {handler.Sends}");
            });

            RunAsync("RustSingleShot maps a parse-fn core error from the POST-RETRY parse", async () =>
            {
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\":\"\"}")
                }));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "Deepgram",
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ => { steps.Add("parse"); throw new HwTranscriptionException.NoSpeech(); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.NoSpeechDetected, $"code {ex.Code}");
                Assert(ex.ProviderName == "Deepgram", $"provider {ex.ProviderName}");
                Assert(string.Join(",", steps) == "build,parse", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
                // THE discriminator. The give-up mapper runs the very same
                // parseResponse delegate and produces the same code, provider and
                // send count, so none of the above can tell the two apart. It maps
                // with the response status; the post-retry parse maps without one.
                // A null status is therefore proof this came from the post-retry
                // parse — and matches what the six services emitted on main. The
                // next case is that other path, asserting 401 where this one
                // asserts null, so the premise is executed rather than assumed.
                Assert(ex.HttpStatusCode == null, $"http status {ex.HttpStatusCode}");
            });

            RunAsync("RustSingleShot maps a non-2xx give-up with the provider tag and the status", async () =>
            {
                // The give-up mapper is the only symbol this refactor relocated,
                // and nothing else in the suite reaches it: it runs solely from
                // RustRetry's GiveUp branch, which needs a non-2xx response.
                // 401 classifies Unauthorized in the core, which is terminal, so
                // the wrapper gives up on attempt 1 — one send, no backoff sleep.
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"bad key\"}")
                }));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "ElevenLabs",
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ => { steps.Add("parse"); throw new HwTranscriptionException.Unauthorized(); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.Unauthorized, $"code {ex.Code}");
                Assert(ex.ProviderName == "ElevenLabs", $"provider {ex.ProviderName}");
                // The point of the mapper. Drop the status it passes, or hand it
                // the wrong provider, and every BYOK 401/429/413 still surfaces —
                // untagged and status-less — to the UI and to
                // TranscriptionDiagnosticsService.
                Assert(ex.HttpStatusCode == 401, $"http status {ex.HttpStatusCode}");
                // parse ran once, against the non-2xx, from inside the give-up
                // branch; the runner's own post-retry parse is never reached.
                Assert(string.Join(",", steps) == "build,parse", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            RunAsync("RustSingleShot fails a non-2xx the core parser accepts", async () =>
            {
                // The give-up mapper's other branch. A parser that returns a
                // transcript for an error response must not turn that body into a
                // successful transcription.
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"text\":\"not an error body\"}")
                }));
                using var client = new HttpClient(handler);

                var ex = await ExpectAsync<TranscriptionException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "Mistral",
                    buildRequest: BuildDummyRequest,
                    parseResponse: _ => new HwTranscript("must not be returned", null, null, null),
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None));

                Assert(ex.Code == TranscriptionErrorCode.Unknown, $"code {ex.Code}");
                Assert(ex.Message == "Unexpected non-error response", $"message {ex.Message}");
                Assert(ex.ProviderName == "Mistral", $"provider {ex.ProviderName}");
                Assert(ex.HttpStatusCode == 401, $"http status {ex.HttpStatusCode}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");
            });

            RunAsync("RustSingleShot honours cancellation after the retry loop returns", async () =>
            {
                using var cts = new CancellationTokenSource();
                var steps = new List<string>();

                // Cancel from the response's Dispose, which the executor runs
                // strictly AFTER SendAsync returned and after the body was read.
                // Nothing else cancels this token, so neither HttpClient nor the
                // executor's content read can be the source of the throw: if the
                // post-retry ThrowIfCancellationRequested were gone, the token
                // would still be cancelled but nothing would observe it, parse
                // would run, and ExpectAsync would fail. (The previous shape
                // cancelled inside the handler and relied on BCL buffering detail
                // to decide which of two paths threw — under dotnet-quality
                // "preview" that floats between runs.)
                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new SignalOnDisposeContent("{\"text\":\"ignored\"}", cts.Cancel)
                }));
                using var client = new HttpClient(handler);

                await ExpectAsync<OperationCanceledException>(() => RustSingleShot.TranscribeAsync(
                    client,
                    "OpenAI",
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ =>
                    {
                        steps.Add("parse");
                        return new HwTranscript("must not be returned", null, null, null);
                    },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: cts.Token));

                Assert(handler.Sends == 1, $"sends {handler.Sends}");
                // A cancelled sequence must never parse the body or hand back a
                // transcript.
                Assert(string.Join(",", steps) == "build", $"steps {string.Join(",", steps)}");
            });

            RunAsync("RustSingleShot returns the transcript and logs the banner derived from provider", async () =>
            {
                // The PR's headline claim is byte-identical logs. main hard-coded
                // one completion banner per service; the runner derives it from
                // `provider`. This is Groq's literal, verbatim from main.
                //
                // ONE provider, not the six. The other five differ from this one
                // only by what ToUpperInvariant returns, so running them proves
                // the BCL uppercases and nothing about the services — their
                // "Groq"/"OpenAI"/… arguments are inline literals at six call
                // sites that no test reaches, before this PR or after it. Five
                // more iterations would buy that non-coverage at 15 extra writes
                // through a sink that swallows its own IO errors.
                const string provider = "Groq";
                const string banner = "========== GROQ TRANSCRIPTION COMPLETE ==========";

                var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\":\"body is parsed by the stub below\"}")
                }));
                using var client = new HttpClient(handler);
                var steps = new List<string>();

                // Read back what LoggingService actually wrote. Main() points
                // AppPaths at a disposable temp root, so this is a private log
                // directory and no production seam is added to observe it.
                // Offsets are per FILE across that directory, not one captured
                // path: CurrentLogPath is DateTime.Now-derived and re-evaluated
                // on every write, so a run crossing local midnight appends these
                // lines to a file that did not exist when this case started.
                var offsets = SnapshotLogOffsets();

                var text = await RustSingleShot.TranscribeAsync(
                    client,
                    provider,
                    buildRequest: () => { steps.Add("build"); return BuildDummyRequest(); },
                    parseResponse: _ => { steps.Add("parse"); return new HwTranscript("hello world", null, null, null); },
                    totalSw: System.Diagnostics.Stopwatch.StartNew(),
                    cancellationToken: CancellationToken.None);

                Assert(text == "hello world", $"text {text}");
                Assert(string.Join(",", steps) == "build,parse", $"steps {string.Join(",", steps)}");
                Assert(handler.Sends == 1, $"sends {handler.Sends}");

                // Every assertion below carries the captured window verbatim.
                // LoggingService.WriteLog swallows its own IOException, so a red
                // here has two possible causes — the banner changed, or the write
                // was dropped — and only the captured text tells them apart.
                var emitted = ReadLogSince(offsets);
                var lines = SplitLogLines(emitted);
                var context = $"log dir '{LoggingService.LogDirectory}', {lines.Length} line(s) captured: <<<{emitted}>>>";

                // Positional, not Contains: the two detail lines must be THIS
                // banner's own next two lines. A Contains pair would be satisfied
                // by any banner's detail lines anywhere in the window.
                var bannerLine = Array.FindIndex(lines, l => l.EndsWith(banner, StringComparison.Ordinal));
                Assert(bannerLine >= 0, $"banner line not emitted — {context}");
                Assert(bannerLine + 2 < lines.Length, $"fewer than two lines follow the banner — {context}");
                Assert(lines[bannerLine + 1].EndsWith("  Characters: 11", StringComparison.Ordinal),
                    $"line after banner is '{lines[bannerLine + 1]}' — {context}");
                // Anchored digits+"ms", because "  Total time: " on its own is a
                // prefix of every unit a future edit could switch to.
                var totalTime = lines[bannerLine + 2];
                Assert(
                    System.Text.RegularExpressions.Regex.IsMatch(totalTime, @"  Total time: [0-9]+ms$"),
                    $"second line after banner is '{totalTime}' — {context}");
            });

            Run("AssemblyAI MapError classifies via the step's own parser", () =>
            {
                var unauthorized = new uniffi.hyperwhisper_core.HttpResponse(
                    401, new List<Header>(), System.Text.Encoding.UTF8.GetBytes("{\"error\":\"bad key\"}"));
                var ex = AssemblyAIService.MapError(
                    unauthorized, "upload", r => HyperwhisperCoreMethods.AssemblyaiParseUploadResponse(r));
                Assert(ex.Code == TranscriptionErrorCode.Unauthorized, $"code {ex.Code}");
                Assert(ex.HttpStatusCode == 401, $"status {ex.HttpStatusCode}");
            });

            Run("AssemblyAI IsSyncEligible gates on exact duration vs the core's sync cap, and excludes medical models", () =>
            {
                var cap = HyperwhisperCoreMethods.AssemblyaiSyncMaxDurationSecs();
                Assert(cap > 0, $"expected a positive sync cap from the core, got {cap}");

                Assert(
                    AssemblyAIService.IsSyncEligible(Result<double>.Success(cap - 1), cap, isMedicalModel: false),
                    "a duration just under the cap should be sync-eligible");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Success(cap), cap, isMedicalModel: false),
                    "a duration AT the cap should NOT be sync-eligible (falls back to async)");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Success(cap + 1), cap, isMedicalModel: false),
                    "a duration over the cap should NOT be sync-eligible");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Failure("duration probe failed"), cap, isMedicalModel: false),
                    "an unknown (failed) duration probe should NOT be sync-eligible — fail closed to async");
                Assert(
                    !AssemblyAIService.IsSyncEligible(Result<double>.Success(cap - 1), cap, isMedicalModel: true),
                    "a medical model should NOT be sync-eligible even with an otherwise-eligible duration — sync has no medical/domain concept");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech skips confirmed dead silence", () =>
            {
                // NonSilentRatio == 0 with a very low peak is the "nothing was recorded
                // at all" case - always benign, must always be skipped. Regression guard
                // for the confirmed-dead-silence early-out.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 3.0,
                    FileSizeBytes: 1024,
                    PeakDbfs: -80.0,
                    RmsDbfs: -90.0,
                    NonSilentRatio: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "confirmed dead silence should be skipped");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech skips the real HYPERWHISPER-PA no-speech sample (fix for the widened low-signal thresholds)", () =>
            {
                // The actual values from the HYPERWHISPER-PA/-QB/-VY Sentry sample: quiet
                // room tone the backend correctly called "no speech", but the old -50dBFS /
                // 0.02 thresholds were too strict to catch it, so it was captured as a full
                // Sentry issue on every occurrence. This is the fix.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -30.0,
                    RmsDbfs: -39.64,
                    NonSilentRatio: 0.046);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "the real HYPERWHISPER-PA no-speech sample should now be skipped");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech skips exactly at the low-signal threshold boundary (inclusive <=)", () =>
            {
                // The gate's comparisons are inclusive (<=), so a reading sitting exactly on
                // both thresholds must still be treated as low-signal and skipped, not
                // captured. Boundary-exact regression guard, same shape as the
                // AssemblyAI IsSyncEligible "AT the cap" boundary test above.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -30.0,
                    RmsDbfs: -38.0,
                    NonSilentRatio: 0.06);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a reading exactly at the low-signal thresholds should be skipped (inclusive boundary)");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech still captures a loud backend-disagreement anomaly", () =>
            {
                // Anomaly-detection guard: loud audio (high RMS, high non-silent ratio) that
                // the backend nonetheless flags as no-speech is exactly the genuine
                // backend-disagreement case this diagnostic exists to catch - must not
                // regress to being skipped just because the low-signal thresholds widened.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 5.0,
                    FileSizeBytes: 131072,
                    PeakDbfs: -5.0,
                    RmsDbfs: -18.0,
                    NonSilentRatio: 0.35);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a loud backend-disagreement anomaly should still be captured");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech always captures EmptyTranscriptWithoutFlag, unaffected by the threshold change", () =>
            {
                // A real bug class (backend/local mismatch) that must keep firing
                // regardless of RMS/ratio values.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -30.0,
                    RmsDbfs: -39.64,
                    NonSilentRatio: 0.046);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true, EmptyTranscriptWithoutFlag: true);

                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "EmptyTranscriptWithoutFlag should always be captured");
            });

            Run("TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech always captures a failed audio analysis, unaffected by the threshold change", () =>
            {
                // A real bug class (backend/local mismatch) that must keep firing
                // regardless of RMS/ratio values.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: false,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -80.0,
                    RmsDbfs: -90.0,
                    NonSilentRatio: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a failed audio analysis should always be captured");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic reclassifies a zero-frame recording as EmptyRecording, not no-speech", () =>
            {
                // A header-only / zero-frame WAV means the recorder captured nothing at
                // all, so calling it "no speech" is wrong - it is a recorder failure.
                // 4 of the 6 events on 1.11.0 in the HYPERWHISPER-PA/-QB/-RM/-XB/-XR
                // cluster were this. It must leave the no-speech group but must still be
                // reported (under its own name/fingerprint) rather than dropped.
                //
                // DurationSeconds is 5.0 on purpose: the live-recording call site passes
                // RecordingDuration.TotalSeconds as the fallback, so a header-only file
                // from a 5-second recording reports a full 5 seconds. Keying the rule on
                // duration missed exactly this case and let it fall into the
                // dead-silence skip below (NonSilentRatio 0 + PeakDbfs -120), i.e.
                // reported nothing at all for the recorder failure we most want to see.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 5.0,
                    FileSizeBytes: 44,
                    PeakDbfs: -120.0,
                    RmsDbfs: -120.0,
                    NonSilentRatio: 0,
                    DecodedSampleCount: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == TranscriptionDiagnosticsService.NoSpeechDiagnosticOutcome.EmptyRecording,
                    "a zero-frame recording should classify as EmptyRecording");
                Assert(!TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "an empty recording must no longer be reported as a no-speech diagnostic");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic keeps capturing the production zero-frame cohort (audio_rms_dbfs_bucket=silent on 1.11.0)", () =>
            {
                // The events this PR exists to reclassify: analysis succeeded, the file
                // has real bytes and the container reports no duration, and the signal
                // reads as silent. Under the old duration rule they were captured (as
                // no-speech); they must keep being captured after the change, as
                // EmptyRecording - not fall into the dead-silence skip.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 0,
                    FileSizeBytes: 44,
                    PeakDbfs: -120.0,
                    RmsDbfs: -120.0,
                    NonSilentRatio: 0,
                    DecodedSampleCount: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        != TranscriptionDiagnosticsService.NoSpeechDiagnosticOutcome.Skip,
                    "the 1.11.0 zero-frame cohort must still be reported, never skipped");
                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == TranscriptionDiagnosticsService.NoSpeechDiagnosticOutcome.EmptyRecording,
                    "the 1.11.0 zero-frame cohort should now report as EmptyRecording");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic does not call a decodable file with no container duration an EmptyRecording", () =>
            {
                // The other direction. The file-transcription call site passes a fallback
                // duration that is 0 when nothing probed it, so a perfectly decodable file
                // whose container reports no duration used to be labelled a
                // microphone-capture failure on a path where no recorder ever ran. The
                // decoder produced frames, so this is a no-speech result, not an empty
                // recording.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 0,
                    FileSizeBytes: 65536,
                    PeakDbfs: -5.0,
                    RmsDbfs: -18.0,
                    NonSilentRatio: 0.35,
                    DecodedSampleCount: 67200);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == TranscriptionDiagnosticsService.NoSpeechDiagnosticOutcome.NoSpeech,
                    "a decodable file with no container duration must stay a no-speech diagnostic");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic keeps the failed-analysis check ahead of the empty-recording check", () =>
            {
                // Precedence guard. A failed analysis decodes nothing, so if the
                // empty-recording rule ever moved above it every analysis failure would
                // silently be relabelled a recorder failure. !AnalysisSucceeded must stay
                // the first check.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: false,
                    DurationSeconds: 0,
                    FileSizeBytes: 0,
                    AnalysisError: "synthetic analysis failure",
                    DecodedSampleCount: 0);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == TranscriptionDiagnosticsService.NoSpeechDiagnosticOutcome.NoSpeech,
                    "a failed analysis must classify as NoSpeech even with zero duration/size");
                Assert(TranscriptionDiagnosticsService.ShouldCaptureAsNoSpeech(audio, provider),
                    "a failed analysis should still be captured");
            });

            Run("TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic never calls a normal clip an EmptyRecording", () =>
            {
                // The reclassification must not leak into the genuine anomaly case: real
                // audio with real signal stays a no-speech diagnostic.
                var audio = new TranscriptionDiagnosticsService.AudioAnalysisDiagnostics(
                    AnalysisSucceeded: true,
                    DurationSeconds: 4.2,
                    FileSizeBytes: 65536,
                    PeakDbfs: -5.0,
                    RmsDbfs: -18.0,
                    NonSilentRatio: 0.35,
                    DecodedSampleCount: 67200);
                var provider = new TranscriptionProviderDiagnostics(
                    ProviderDisplayName: "test", BackendNoSpeechDetected: true);

                Assert(
                    TranscriptionDiagnosticsService.ClassifyNoSpeechDiagnostic(audio, provider)
                        == TranscriptionDiagnosticsService.NoSpeechDiagnosticOutcome.NoSpeech,
                    "a normal 4.2s clip should stay a no-speech diagnostic");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint ignores a stale CloudProvider on a local mode", () =>
            {
                // Mode.CloudProvider and Mode.ProviderType are independent persisted
                // fields, so a mode switched from cloud to local keeps its old vendor
                // forever. Grouping on it regardless of provider type is what split one
                // local-mode condition across HYPERWHISPER-QB/-RM/-XB/-XR.
                var mode = new Mode { ProviderType = "local", CloudProvider = "groq", LocalEngine = "whisper" };

                var fingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", mode);

                Assert(fingerprint.Length == 5, $"expected 5 fingerprint elements, got {fingerprint.Length}");
                Assert(fingerprint[3] == "local", $"expected provider type 'local', got '{fingerprint[3]}'");
                Assert(fingerprint[4] == "whisper",
                    $"a local mode should group on its local engine, got '{fingerprint[4]}'");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(mode) == "none",
                    "the cloud_provider tag must not report a stale vendor for a local mode");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint groups two local modes with different stale vendors together", () =>
            {
                // The actual production regression: same local engine, same condition,
                // different leftover CloudProvider values - one Sentry group, not four.
                var staleGroq = new Mode { ProviderType = "local", CloudProvider = "groq", LocalEngine = "parakeet" };
                var staleGemini = new Mode { ProviderType = "local", CloudProvider = "gemini", LocalEngine = "parakeet" };

                var first = string.Join("|", TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", staleGroq));
                var second = string.Join("|", TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", staleGemini));

                Assert(first == second, $"expected identical fingerprints, got '{first}' vs '{second}'");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint still separates cloud vendors", () =>
            {
                // The fix must not collapse every provider into one group: for a cloud
                // mode the vendor is live data and two vendors are two conditions.
                var groq = new Mode { ProviderType = "cloud", CloudProvider = "groq" };
                var openai = new Mode { ProviderType = "cloud", CloudProvider = "openai" };

                var groqFingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", groq);
                var openaiFingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-no-speech", "live_recording", "provider_no_speech", openai);

                Assert(groqFingerprint[4] == "groq", $"expected 'groq', got '{groqFingerprint[4]}'");
                Assert(openaiFingerprint[4] == "openai", $"expected 'openai', got '{openaiFingerprint[4]}'");
                Assert(string.Join("|", groqFingerprint) != string.Join("|", openaiFingerprint),
                    "two different cloud vendors must keep grouping separately");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(groq) == "groq",
                    "a cloud mode's cloud_provider tag must keep reporting its vendor");
            });

            Run("TranscriptionDiagnosticsService.BuildDiagnosticFingerprint handles a null mode without throwing", () =>
            {
                // The diagnostic runs on failure paths where the mode can be gone.
                var fingerprint = TranscriptionDiagnosticsService.BuildDiagnosticFingerprint(
                    "transcription-empty-recording", "file_transcription", "provider_no_speech", null);

                Assert(fingerprint.Length == 5, $"expected 5 fingerprint elements, got {fingerprint.Length}");
                Assert(fingerprint[0] == "transcription-empty-recording",
                    $"expected the empty-recording root, got '{fingerprint[0]}'");
                Assert(fingerprint[3] == "unknown", $"expected 'unknown', got '{fingerprint[3]}'");
                Assert(fingerprint[4] == "none", $"expected 'none', got '{fingerprint[4]}'");
                Assert(TranscriptionDiagnosticsService.ResolveCloudProviderTag(null) == "none",
                    "a null mode's cloud_provider tag should be 'none'");
            });

            Run("DeepgramStreamingStrategy.SessionStartsOnWebSocketOpen is true (regression for #100)", () =>
            {
                // Deepgram never sends its only session-shaped message (Metadata) until
                // after audio is sent, so startup must not block waiting for it — the
                // client should treat the WebSocket handshake itself as session-start.
                // A regression to false here reintroduces a guaranteed 10s connect
                // timeout on every Windows Deepgram live session.
                var strategy = new DeepgramStreamingStrategy();
                Assert(strategy.SessionStartsOnWebSocketOpen,
                    "Deepgram must start streaming on WebSocket open, not wait for a Metadata message");
            });

            Run("DeepgramStreamingStrategy.ParseMessage still classifies a late Metadata message as SessionStarted", () =>
            {
                // Even though startup no longer blocks on Metadata, a Metadata message
                // can still legitimately arrive later (after audio starts flowing) and
                // must keep parsing correctly.
                var strategy = new DeepgramStreamingStrategy();
                var evt = strategy.ParseMessage("{\"type\":\"Metadata\",\"request_id\":\"abc-123\"}");

                Assert(evt is StreamingProviderEvent.SessionStarted, $"expected SessionStarted, got {evt}");
                var sessionStarted = (StreamingProviderEvent.SessionStarted)evt!;
                Assert(sessionStarted.SessionId == "abc-123", $"expected request id 'abc-123', got '{sessionStarted.SessionId}'");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats an abnormal provider close (1008) as terminal", () =>
            {
                // Deepgram's real DATA-xxxx payload errors close with 1008 - before the fix,
                // HandleCloseResult only recognized HyperWhisper's own 4001/4002 codes and let
                // everything else fall through to ~3s of doomed reconnect churn before finally
                // surfacing a generic message instead of the provider's own close description.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                // HandleCloseResult's own shutdown guard no-ops on a freshly constructed
                // (Idle) client - drive it into a realistic in-session state first.
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1008, "DATA-0000: invalid audio codec"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for an abnormal close code");
                Assert(capturedMessage!.Contains("DATA-0000"), $"expected the provider's close description to surface, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats an abnormal provider close (1011) as terminal", () =>
            {
                // Deepgram's NET-xxxx errors (timeout / no audio) close with 1011.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1011, "NET-0000: timeout"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for an abnormal close code");
                Assert(capturedMessage!.Contains("NET-0000"), $"expected the provider's close description to surface, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult still recognizes HyperWhisper's own 4001 (credits exhausted)", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)4001, "credits exhausted"));

                Assert(capturedMessage == "Streaming stopped because credits are exhausted.", $"got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult still recognizes HyperWhisper's own 4002 (max session duration)", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)4002, "max duration reached"));

                Assert(capturedMessage == "Streaming stopped because the maximum session duration was reached.", $"got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult does not treat a normal closure (1000) as terminal", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, ""));

                Assert(capturedMessage == null, $"expected no ErrorReceived for a normal closure, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Streaming, $"expected State to remain Streaming, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult does not treat a null close status as terminal", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, null, null));

                Assert(capturedMessage == null, $"expected no ErrorReceived when CloseStatus is null (ambiguous), got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Streaming, $"expected State to remain Streaming, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats a transient close (1006 abnormal) as recoverable", () =>
            {
                // 1006 (no close frame) is a textbook-transient WebSocket close code - it must
                // keep falling through to the existing reconnect/backoff path, not be treated
                // as terminal (the earlier blanket "any non-1000 code is terminal" diff broke
                // this).
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1006, "abnormal closure"));

                Assert(capturedMessage == null, $"expected no ErrorReceived for a transient close code, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Streaming, $"expected State to remain Streaming, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult does not reclassify our own shutdown as a provider error", () =>
            {
                // Even a code that would otherwise be terminal (e.g. 1011) must not overwrite
                // an in-flight StopAsync/Dispose shutdown - HandleCloseResult can still observe
                // a close arriving concurrently with our own Disconnecting/Idle transition.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Disconnecting);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1011, "NET-0000: timeout"));

                Assert(capturedMessage == null, $"expected no ErrorReceived while Disconnecting, got '{capturedMessage}'");
                Assert(client.State == StreamingConnectionState.Disconnecting, $"expected State to remain Disconnecting, got {client.State}");
            });

            Run("IStreamingProviderStrategy.IsTerminalCloseCode default covers the standard fatal WebSocket protocol codes", () =>
            {
                // The terminal-code allowlist moved from a StreamingTranscriptionClient-private,
                // Deepgram-flavored comment into the strategy interface as a default interface
                // method - verify the default itself (not just Deepgram, which takes no override)
                // covers the standard non-recoverable codes and still excludes the transient ones.
                // Typed as the interface (not the concrete class) so this actually calls through
                // to the default interface method - C# only considers a DIM when the member is
                // accessed via the interface type.
                IStreamingProviderStrategy strategy = new DeepgramStreamingStrategy();
                foreach (var fatalCode in new[] { 1002, 1003, 1007, 1008, 1009, 1011 })
                {
                    Assert(strategy.IsTerminalCloseCode(fatalCode), $"expected close code {fatalCode} to be terminal by default");
                }
                foreach (var transientCode in new[] { 1000, 1001, 1006, 1012, 1013 })
                {
                    Assert(!strategy.IsTerminalCloseCode(transientCode), $"expected close code {transientCode} to remain recoverable");
                }
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats Message Too Big (1009) as terminal", () =>
            {
                // Confirmed non-hypothetical: hyperwhisper-cloud/src/routes/ws-streaming-deepgram.ts
                // sends 1009 for an oversized audio stream from the HyperWhisperCloud backend.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1009, "message too big"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for close code 1009");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult treats Protocol Error (1002) as terminal", () =>
            {
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Streaming);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1002, "protocol error"));

                Assert(capturedMessage != null, "expected ErrorReceived to fire for close code 1002");
                Assert(client.State == StreamingConnectionState.Error, $"expected State Error, got {client.State}");
            });

            Run("StreamingTranscriptionClient.HandleCloseResult still records terminal-close bookkeeping when State is already Error", () =>
            {
                // Regression for: an in-band provider error (HandleProviderEvent's Error case)
                // already moved State to Error before the socket's terminal close frame arrives.
                // TryChangeStateUnless(Error, ...) used to fail in that case because State already
                // equalled the target, so ErrorReceived never fired again and _receivedTerminalClose
                // never got set for this close - losing the last partial transcript in StopAsync
                // (which falls back to FinalText instead of preserving CurrentPartial). The "already
                // at target state" case must still be treated as success for this bookkeeping.
                var config = new StreamingSessionConfig(null, null, "en", null, "test-api-key", null, false, false);
                var client = new StreamingTranscriptionClient(new DeepgramStreamingStrategy(), config);
                client.SetStateForTesting(StreamingConnectionState.Error);
                string? capturedMessage = null;
                client.ErrorReceived += m => capturedMessage = m;

                client.HandleCloseResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)1011, "NET-0000: timeout"));

                Assert(capturedMessage != null, "expected ErrorReceived to still fire when State was already Error");
                Assert(client.State == StreamingConnectionState.Error, $"expected State to remain Error, got {client.State}");
            });

            Run("TranscriptViewModel.ApplyUpdate never reverts the entity it wraps", () =>
            {
                // Reproduces the History clobber: MainViewModel creates a Processing
                // transcript, HistoryViewModel wraps that exact instance, then
                // MainViewModel completes the SAME instance and hands it back through
                // HistoryService.TranscriptUpdated. Absorbing the update must not
                // revert the entity to the snapshot taken at construction — the
                // transcription flow's finally-block safety net reads that status and
                // would overwrite a completed transcript with a failure.
                var transcript = new Transcript
                {
                    Id = Guid.NewGuid(),
                    Status = TranscriptStatus.Processing,
                    Text = "Processing audio..."
                };

                var vm = new TranscriptViewModel(transcript);

                transcript.Text = "the real transcription";
                transcript.TranscribedText = "the real transcription";
                transcript.Status = TranscriptStatus.Completed;
                transcript.TranscriptionProvider = "Whisper large-v3-turbo";

                vm.ApplyUpdate(transcript);

                Assert(transcript.Status == TranscriptStatus.Completed,
                    $"entity status was reverted to {transcript.Status}");
                Assert(transcript.Text == "the real transcription",
                    $"entity text was reverted to '{transcript.Text}'");
                Assert(transcript.TranscribedText == "the real transcription",
                    "entity raw text was discarded");
                Assert(transcript.TranscriptionProvider == "Whisper large-v3-turbo",
                    "entity provider was discarded");

                Assert(vm.Status == TranscriptStatus.Completed,
                    $"view model still shows {vm.Status}");
                Assert(vm.Text == "the real transcription",
                    $"view model still shows '{vm.Text}'");

                // A distinct instance (e.g. re-read from the DB) must still land.
                var reread = new Transcript
                {
                    Id = transcript.Id,
                    Status = TranscriptStatus.Failed,
                    Text = "No speech detected",
                    FailedReason = "No speech detected",
                    RetryCount = 2
                };

                vm.ApplyUpdate(reread);

                Assert(vm.Status == TranscriptStatus.Failed, $"view model shows {vm.Status}");
                Assert(vm.Text == "No speech detected", $"view model shows '{vm.Text}'");
                Assert(vm.RetryCount == 2, $"view model shows retry count {vm.RetryCount}");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript applies filler-word removal to confirmed deltas, gated by _config.RemoveFillerWords (mirrors TranscriptionOrchestrator's batch order)", () =>
            {
                // Issue #94: "Remove filler words" stripped fillers from batch
                // transcription but was silently ignored in streaming. This pins the
                // fix's wiring in AppendFinalTranscript — the only place confirmed/
                // final streaming deltas are processed (never PartialTranscript/interim).
                //
                // RemoveFillerWords now flows through the immutable StreamingSessionConfig
                // (built once by StreamingTranscriptionSessionFactory.Create), like every
                // other per-session setting on this client — so exercising "on" vs "off"
                // is just two configs, no global-singleton save/mutate/restore needed.
                const string raw = "I uh think this is, um, correct";
                var baseConfig = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "en",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var enabledClient = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), baseConfig);
                var expectedEnabled = TranscriptionTextProcessing
                    .ProcessVoiceCommands(SmartSpacing.RemoveFillerWords(raw))
                    .Trim();
                var actualEnabled = enabledClient.AppendFinalTranscript(raw);
                Assert(actualEnabled == expectedEnabled,
                    $"expected filler words stripped ('{expectedEnabled}'), got '{actualEnabled}'");
                Assert(actualEnabled != null && !actualEnabled.Contains("uh") && !actualEnabled.Contains("um"),
                    $"filler words were not removed from '{actualEnabled}'");

                var disabledConfig = baseConfig with { RemoveFillerWords = false };
                var disabledClient = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), disabledConfig);
                var expectedDisabled = TranscriptionTextProcessing.ProcessVoiceCommands(raw).Trim();
                var actualDisabled = disabledClient.AppendFinalTranscript(raw);
                Assert(actualDisabled == expectedDisabled,
                    $"expected filler words preserved when the setting is off ('{expectedDisabled}'), got '{actualDisabled}'");
                Assert(actualDisabled != null && actualDisabled.Contains("uh") && actualDisabled.Contains("um"),
                    $"filler words should be preserved when RemoveFillerWords is disabled, got '{actualDisabled}'");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript gates filler removal on the session language (English-only regex, no language parameter)", () =>
            {
                // SmartSpacing.RemoveFillerWords is a hardcoded-English regex - unlike the
                // shared Rust remove_filler_words used on macOS/batch, it has no language
                // parameter of its own to no-op on non-English text. AppendFinalTranscript
                // must gate the call itself so a German stream doesn't have real words
                // ("er" = he, "um" = at) stripped just because the setting is on.
                const string german = "ich denke er ist groß";
                var germanConfig = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "de",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), germanConfig);
                var actual = client.AppendFinalTranscript(german);
                Assert(actual == german,
                    $"expected German real words preserved ('{german}'), got '{actual}'");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript strips a confirmed delta that is entirely a filler word", () =>
            {
                // A confirmed segment that is JUST "uh"/"um"/"er" (already trimmed, no
                // surrounding whitespace) must be stripped down to empty, same as a filler
                // appearing mid-sentence — regression coverage for the boundary case where
                // the old regex required whitespace on at least one side to match.
                var config = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "en",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);
                var actual = client.AppendFinalTranscript("uh");
                Assert(actual == null,
                    $"expected a filler-only delta to be stripped down to nothing, got '{actual}'");
            });

            Run("StreamingTranscriptionClient.AppendFinalTranscript only recapitalizes the leading word for the session's first confirmed delta", () =>
            {
                // A leading filler on the FIRST confirmed delta of a session is a real
                // sentence opener, so SmartSpacing.RemoveFillerWords correctly recapitalizes
                // the word after it. But that same recapitalization is wrong for a LATER
                // delta - "um, this works" following an earlier confirmed "I think" is
                // mid-transcript, not a new sentence, and must not become "I think This works".
                var config = new StreamingSessionConfig(
                    LicenseKey: null,
                    DeviceId: null,
                    Language: "en",
                    Vocabulary: null,
                    ApiKey: null,
                    Model: null,
                    FastFormatting: false,
                    RemoveFillerWords: true);

                var client = new StreamingTranscriptionClient(new NoOpStreamingProviderStrategy(), config);

                var firstDelta = client.AppendFinalTranscript("um, the cat sat down");
                Assert(firstDelta == "The cat sat down",
                    $"expected the session's first delta to recapitalize after its leading filler, got '{firstDelta}'");

                var secondDelta = client.AppendFinalTranscript("um, this works");
                Assert(secondDelta == "this works",
                    $"expected a later delta's leading word to stay lowercase (mid-transcript), got '{secondDelta}'");
            });

            Run("DeepgramStreamingStrategy parses Results (object channel) into a FinalTranscript", () =>
            {
                var strategy = new DeepgramStreamingStrategy();
                var evt = strategy.ParseMessage(
                    "{\"type\":\"Results\",\"channel\":{\"alternatives\":[{\"transcript\":\"hello\"}]},\"is_final\":true}");

                var final = evt as StreamingProviderEvent.FinalTranscript;
                Assert(final != null, $"expected FinalTranscript, got {evt?.GetType().Name ?? "null"}");
                Assert(final!.Text == "hello", $"expected text 'hello', got '{final.Text}'");
            });

            Run("DeepgramStreamingStrategy parses SpeechStarted (array channel) without throwing — issue #106", () =>
            {
                // Deepgram overloads "channel": an object on Results frames, an array of channel
                // indices on SpeechStarted/UtteranceEnd frames. Before the fix, deserializing the
                // array shape into the DeepgramChannel object threw and was swallowed by the
                // outer try/catch, so this event never reached the caller.
                var strategy = new DeepgramStreamingStrategy();
                var evt = strategy.ParseMessage("{\"type\":\"SpeechStarted\",\"channel\":[0,1],\"timestamp\":1.2}");

                Assert(evt is StreamingProviderEvent.Metadata,
                    $"expected Metadata, got {evt?.GetType().Name ?? "null"}");
            });

            Run("DeepgramStreamingStrategy parses UtteranceEnd (array channel) without throwing — issue #106", () =>
            {
                var strategy = new DeepgramStreamingStrategy();
                var evt = strategy.ParseMessage("{\"type\":\"UtteranceEnd\",\"channel\":[0,1],\"last_word_end\":2.5}");

                Assert(evt is StreamingProviderEvent.Metadata,
                    $"expected Metadata, got {evt?.GetType().Name ?? "null"}");
            });

            Run("InlineHtml turns release-note <b> into a bold run instead of literal markup", () =>
            {
                var runs = InlineHtml.Parse("<b>New models</b> &mdash; OpenAI gpt-transcribe and more.");

                Assert(runs.Count == 2, $"expected 2 runs, got {runs.Count}");
                Assert(runs[0] == new HtmlRun("New models", Bold: true, Italic: false),
                    $"expected a bold lead-in, got '{runs[0]}'");
                Assert(runs[1] == new HtmlRun(" — OpenAI gpt-transcribe and more.", Bold: false, Italic: false),
                    $"expected the decoded remainder, got '{runs[1]}'");
                Assert(!InlineHtml.PlainText("<b>x</b>").Contains('<'), "markup leaked into plain text");
            });

            Run("InlineHtml keeps text from tags it does not support and leaves unknown entities literal", () =>
            {
                Assert(InlineHtml.PlainText("<span class=\"x\">kept</span>") == "kept",
                    "unsupported tag should be dropped but its text kept");
                Assert(InlineHtml.PlainText("2 < 3") == "2 < 3",
                    "an unterminated tag should be treated as text");
                Assert(InlineHtml.PlainText("&bogus; stays") == "&bogus; stays",
                    "an unknown entity should stay literal");
                Assert(InlineHtml.PlainText("&lt;b&gt;escaped&lt;/b&gt;") == "<b>escaped</b>",
                    "escaped markup must not be re-parsed as a tag");
            });

            Run("InlineHtml turns <a href> into a linked run", () =>
            {
                var runs = InlineHtml.Parse("See the <a href=\"https://example.com/latency\">latency page</a> now.");

                Assert(runs.Count == 3, $"expected 3 runs, got {runs.Count}");
                Assert(runs[1].Text == "latency page" && !runs[1].Bold && !runs[1].Italic
                        && runs[1].Link?.AbsoluteUri == "https://example.com/latency",
                    $"expected a linked label, got '{runs[1]}'");
                Assert(runs[0].Link is null && runs[2].Link is null,
                    "text outside the anchor should not be linked");

                // Uri normalization: a bare authority gains the empty path's slash.
                var bold = InlineHtml.Parse("<a href=\"https://example.com\"><b>bold link</b></a>");
                Assert(bold.Count == 1 && bold[0].Bold && bold[0].Link?.AbsoluteUri == "https://example.com/",
                    "emphasis inside a link should keep both the style and the destination");

                var quoting = new[]
                {
                    "<A HREF='https://example.com/a' class=\"x\">x</A>",
                    "<a class=\"x\" href=https://example.com/a>x</a>",
                    "<a href = \"https://example.com/a\">x</a>"
                };
                foreach (var html in quoting)
                {
                    Assert(InlineHtml.Parse(html)[0].Link?.AbsoluteUri == "https://example.com/a",
                        $"href not read from '{html}'");
                }

                Assert(InlineHtml.Parse("<a href=\"https://example.com/p?a=1&amp;b=2\">x</a>")[0].Link?.AbsoluteUri
                        == "https://example.com/p?a=1&b=2",
                    "escaped query separators should survive in the destination");
            });

            Run("InlineHtml links only web and mail schemes", () =>
            {
                var hostile = new[]
                {
                    "<a href=\"javascript:alert(1)\">x</a>",
                    "<a href=\"data:text/html,<b>x</b>\">x</a>",
                    "<a href=\"file:///etc/passwd\">x</a>",
                    "<a href=\"/relative/path\">x</a>",
                    "<a data-href=\"https://example.com\">x</a>",
                    "<a>x</a>"
                };

                foreach (var html in hostile)
                {
                    Assert(InlineHtml.Parse(html).TrueForAll(run => run.Link is null),
                        $"'{html}' should not produce a link");
                    // Exactly the label, nothing else: the data: case used to leak
                    // '">x' into the visible text because the tag scan cut at the
                    // '>' inside the quoted href.
                    Assert(InlineHtml.PlainText(html) == "x",
                        $"'{html}' should render as its label alone, got '{InlineHtml.PlainText(html)}'");
                }

                Assert(InlineHtml.Parse("<a href=\"mailto:hi@example.com\">mail</a>")[0].Link?.AbsoluteUri
                        == "mailto:hi@example.com",
                    "mailto should stay clickable");

                // An unusable href must not leak onto the text that follows it.
                var after = InlineHtml.Parse("<a href=\"javascript:x\">label</a> after");
                Assert(after.TrueForAll(run => run.Link is null), "link leaked past the anchor");
            });

            Run("InlineHtml reads href only in attribute-name position", () =>
            {
                // "preceded by whitespace" is also true inside a quoted value, so a
                // title carrying "href=..." used to win over the real attribute.
                Assert(InlineHtml.Parse(
                            "<a title=\"see href=http://evil.example more\" href=\"https://real.example\">Label</a>")[0]
                        .Link?.AbsoluteUri == "https://real.example/",
                    "an href inside another attribute's value must not win");
                Assert(InlineHtml.Parse("<a title=\"use href=1\" href=\"https://real\">x</a>")[0]
                        .Link?.AbsoluteUri == "https://real/",
                    "the real href must still be found after a shadowing value");
                Assert(InlineHtml.Parse("<a data-href=\"https://evil.example\" href=\"https://real.example\">x</a>")[0]
                        .Link?.AbsoluteUri == "https://real.example/",
                    "a longer attribute name ending in 'href' must not match");
            });

            Run("InlineHtml keeps a quoted '>' inside the tag, and invents nothing on an unterminated quote", () =>
            {
                // A '>' in a query string used to truncate the tag, linking half the
                // URL and spilling the rest of the markup into the visible text.
                const string quotedAngle = "<li>Read <a href=\"https://ex.com/?q=a>b\" title=\"t\">here</a></li>";
                var runs = InlineHtml.Parse(quotedAngle);
                Assert(InlineHtml.PlainText(quotedAngle) == "Read here",
                    $"markup leaked into the text: '{InlineHtml.PlainText(quotedAngle)}'");
                Assert(runs.Count == 2 && runs[1].Text == "here",
                    $"expected the label alone in the linked run, got {runs.Count} runs");

                // An href whose quote is never closed is not a destination.
                const string unterminated = "<a href=\"https://example.com>label</a> after";
                Assert(InlineHtml.Parse(unterminated).TrueForAll(run => run.Link is null),
                    "an unterminated quote should yield no link at all");
                Assert(InlineHtml.PlainText(unterminated) == "label after",
                    $"got '{InlineHtml.PlainText(unterminated)}'");
            });

            Run("InlineHtml does not leave a self-closing or unclosed <a> on the link stack", () =>
            {
                // <a …/> pushed an entry nothing ever popped, so every remaining
                // word in the note rendered as part of the link.
                Assert(InlineHtml.Parse("Before <a href=\"https://x.example\"/> after and more")
                        .TrueForAll(run => run.Link is null),
                    "a self-closing anchor must not link the text that follows it");

                var selfClosed = InlineHtml.Parse("<a href=\"https://x.example\"/>after");
                Assert(selfClosed.Count == 1 && selfClosed[0] == new HtmlRun("after", Bold: false, Italic: false),
                    $"expected one unlinked run, got '{string.Join(", ", selfClosed)}'");

                // An <a> nobody closes is bounded by the end of the fragment.
                var unclosed = InlineHtml.Parse("<a href=\"https://x.example\">unclosed");
                Assert(unclosed.Count == 1 && unclosed[0].Text == "unclosed"
                        && unclosed[0].Link?.AbsoluteUri == "https://x.example/",
                    "an unclosed anchor should still link its own label");
            });

            Run("InlineHtml treats a '/' at the end of a bare href as part of the URL", () =>
            {
                // Deciding "self-closing" from the raw tag's last character read
                // every bare href ending in '/' — most URLs — as "<a …/>", and
                // silently dropped the link.
                var bare = InlineHtml.Parse("<a href=https://example.com/>Home</a>");
                Assert(bare.Count == 1 && bare[0].Text == "Home"
                        && bare[0].Link?.AbsoluteUri == "https://example.com/",
                    $"a bare href ending in '/' lost its link: '{string.Join(", ", bare)}'");

                var quoted = InlineHtml.Parse("<a href=\"https://example.com/\">Home</a>");
                Assert(quoted.Count == 1 && quoted[0].Text == "Home"
                        && quoted[0].Link?.AbsoluteUri == "https://example.com/",
                    $"a quoted href ending in '/' lost its link: '{string.Join(", ", quoted)}'");

                // A '/' of the tag's own still closes it, even after a bare href
                // that ends in one.
                const string closed = "<a href=https://example.com/ />Home and the rest";
                Assert(InlineHtml.Parse(closed).TrueForAll(run => run.Link is null),
                    "a genuinely self-closing anchor must link nothing");
                Assert(InlineHtml.PlainText(closed) == "Home and the rest",
                    $"got '{InlineHtml.PlainText(closed)}'");

                // A '/' that is not the last thing in the tag is not the tag's.
                Assert(InlineHtml.Parse("<a / href=https://example.com/>L</a>")[0].Link?.AbsoluteUri
                        == "https://example.com/",
                    "a '/' before a real attribute must not close the tag");

                foreach (var lineBreak in new[] { "a<br>b", "a<br/>b", "a<br />b" })
                {
                    Assert(InlineHtml.PlainText(lineBreak) == "a\nb",
                        $"'{lineBreak}' should be a line break, got '{InlineHtml.PlainText(lineBreak)}'");
                }
            });

            Run("InlineHtml gives a nested <a> the innermost destination, not the outer one", () =>
            {
                // The inner href is rejected, so its label must lose the link
                // rather than inherit the outer anchor's destination.
                var runs = InlineHtml.Parse(
                    "<a href=\"https://ok.example\">read <a href=\"javascript:x\">this</a></a>");

                Assert(runs.Count == 2, $"expected 2 runs, got {runs.Count}");
                Assert(runs[0].Text == "read " && runs[0].Link?.AbsoluteUri == "https://ok.example/",
                    $"outer label lost its link: '{runs[0]}'");
                Assert(runs[1].Text == "this" && runs[1].Link is null,
                    $"inner label inherited the outer destination: '{runs[1]}'");
            });

            Run("InlineHtml leaves the space on either side of a link outside the link", () =>
            {
                // A space inside the linked run is underlined, tinted and
                // clickable — it belongs outside the anchor, on whichever side
                // of it the space was written.
                const string opening = "<b>See</b> <a href=\"https://example.com\">here</a>";
                var runs = InlineHtml.Parse(opening);

                Assert(InlineHtml.PlainText(opening) == "See here",
                    "the space between the two runs was lost");
                Assert(runs.TrueForAll(run => run.Link is null || !run.Text.StartsWith(' ')),
                    $"the space was swallowed into the link run: '{string.Join(", ", runs)}'");

                // The closing side: the space in front of </a> used to be emitted
                // as its own run still carrying the link — an extra Hyperlink with
                // a hand cursor and a tooltip, for one blank character.
                const string closing = "See <a href=\"https://x.example\"><b>the page</b> </a>now";
                var closingRuns = InlineHtml.Parse(closing);

                Assert(InlineHtml.PlainText(closing) == "See the page now",
                    $"got '{InlineHtml.PlainText(closing)}'");
                Assert(closingRuns.TrueForAll(run => run.Link is null || run.Text.Trim().Length > 0),
                    $"a blank run still carries the link: '{string.Join(", ", closingRuns)}'");
            });

            Run("InlineHtml does not style the rest of the note after a self-closing <b/> or <i/>", () =>
            {
                // Self-closing was consulted for <a> only, so "<b/>" pushed a depth
                // nothing ever popped and emboldened everything after it.
                Assert(InlineHtml.Parse("before <b/> after").TrueForAll(run => !run.Bold),
                    $"'<b/>' bolded the rest: '{string.Join(", ", InlineHtml.Parse("before <b/> after"))}'");
                Assert(InlineHtml.Parse("before <i/> after").TrueForAll(run => !run.Italic),
                    $"'<i/>' italicised the rest: '{string.Join(", ", InlineHtml.Parse("before <i/> after"))}'");
                Assert(InlineHtml.Parse("x<strong />y").TrueForAll(run => !run.Bold),
                    "'<strong />' bolded the rest");
                Assert(InlineHtml.Parse("x<em />y").TrueForAll(run => !run.Italic),
                    "'<em />' italicised the rest");

                // The paired forms still style what they wrap.
                Assert(InlineHtml.Parse("<b>still bold</b>")[0].Bold, "a real <b> stopped working");
            });

            Run("InlineHtml reads an apostrophe in a bare value as an ordinary character", () =>
            {
                // Skipping quoted values in the tag-end scan entered quote mode on
                // any apostrophe, so the one in a bare "href=it's" paired up with
                // the next one in the text and swallowed everything between them.
                const string html = "<a href=it's>label</a> and <b>Ray's</b> note";
                Assert(InlineHtml.PlainText(html) == "label and Ray's note",
                    $"text between two apostrophes was eaten: '{InlineHtml.PlainText(html)}'");
                Assert(InlineHtml.PlainText("<b>Ray's</b> and <i>don't</i>") == "Ray's and don't",
                    $"got '{InlineHtml.PlainText("<b>Ray's</b> and <i>don't</i>")}'");

                // A quote in value position — with or without whitespace after the
                // '=' — still shields a '>' sitting inside the value.
                Assert(InlineHtml.PlainText("<a href=\"https://ex.com/?q=a>b\" title=\"t\">here</a>") == "here",
                    "a '>' inside a quoted value truncated the tag");
                Assert(InlineHtml.PlainText("<a href = 'https://ex.com/?q=a>b'>x</a>") == "x",
                    "a '>' inside a spaced-out quoted value truncated the tag");
            });

            Run("InlineHtml keeps the feed's href verbatim, decoding entities only", () =>
            {
                // The href used to be run through the whole parser to decode
                // "&amp;", which also stripped tags and comments out of it — a
                // valid, allow-listed, but different destination.
                var markup = InlineHtml.Parse("<a href=\"https://ex.com/?q=<b>x</b>\">Docs</a>");
                Assert(markup[0].Link?.AbsoluteUri != "https://ex.com/?q=x",
                    "markup was stripped out of the destination");
                Assert(Uri.UnescapeDataString(markup[0].Link?.AbsoluteUri ?? "") == "https://ex.com/?q=<b>x</b>",
                    $"destination not preserved verbatim: '{markup[0].Link?.AbsoluteUri}'");
                Assert(InlineHtml.PlainText("<a href=\"https://ex.com/?q=<b>x</b>\">Docs</a>") == "Docs",
                    "the label changed");

                var commented = InlineHtml.Parse("<a href=\"https://ex.com/<!-- c -->path\">Docs</a>");
                Assert(commented[0].Link?.AbsoluteUri != "https://ex.com/path",
                    "a comment was stripped out of the destination");

                // Entities in the href must still decode: feeds escape query
                // separators, and "?a=1&amp;b=2" is not a URL until they do.
                Assert(InlineHtml.Parse("<a href=\"https://ex.com/p?a=1&amp;b=2\">x</a>")[0].Link?.AbsoluteUri
                        == "https://ex.com/p?a=1&b=2", "&amp; stopped decoding in the href");
                Assert(InlineHtml.Parse("<a href=\"https://ex.com/p?a=1&#38;b=2\">x</a>")[0].Link?.AbsoluteUri
                        == "https://ex.com/p?a=1&b=2", "&#38; stopped decoding in the href");
            });

            Run("InlineHtml does not pair an unclosed value with a later tag's quote", () =>
            {
                // Skipping to the matching quote searched the whole fragment, so
                // a value never closed inside its own tag paired up with the
                // quote of a later one and everything between them — the label,
                // its </a>, the next tag — was swallowed as one tag body.
                const string html = "Read <a href=\"https://ex.com/latency>the page</a> for <b class=\"hl\">details</b>.";
                var runs = InlineHtml.Parse(html);

                Assert(InlineHtml.PlainText(html) == "Read the page for details.",
                    $"text was swallowed: '{InlineHtml.PlainText(html)}'");
                Assert(runs.TrueForAll(run => run.Link is null),
                    $"an unterminated quote invented a destination: '{string.Join(", ", runs)}'");
                Assert(runs.Exists(run => run.Text == "details" && run.Bold),
                    $"the <b> after it never opened: '{string.Join(", ", runs)}'");
                Assert(runs.TrueForAll(run => !run.Text.Contains('<') && !run.Text.Contains("href")),
                    $"markup leaked into the text: '{string.Join(", ", runs)}'");

                // A '>' inside a value that *is* closed still does not end the
                // tag, and an apostrophe in a bare value is still ordinary text.
                Assert(InlineHtml.PlainText("<a href=\"https://ex.com/?q=a>b\" title=\"t\">here</a>") == "here",
                    "a '>' inside a quoted value truncated the tag");
                Assert(InlineHtml.PlainText("<a href=it's>label</a> and <b>Ray's</b> note")
                        == "label and Ray's note",
                    "an apostrophe in a bare value ate the text after it");
            });

            Run("InlineHtml pops on a closing tag that also closes itself", () =>
            {
                // parseTag sets both flags for "</a/>", and the self-closing
                // guard ran first, so nothing was ever popped and the rest of
                // the note stayed linked, bold or italic.
                const string anchor =
                    "<a href=\"https://hyperwhisper.app/changelog\">changelog</a/>. Also faster startup.";
                var runs = InlineHtml.Parse(anchor);

                Assert(InlineHtml.PlainText(anchor) == "changelog. Also faster startup.",
                    $"got '{InlineHtml.PlainText(anchor)}'");
                Assert(runs.Exists(run => run.Text == "changelog" && run.Link is not null),
                    $"the anchor's own label lost its link: '{string.Join(", ", runs)}'");
                Assert(runs.TrueForAll(run => run.Link is null || run.Text == "changelog"),
                    $"'</a/>' left the rest of the note linked: '{string.Join(", ", runs)}'");

                const string bold = "<b>New:</b/> dictation is faster";
                Assert(InlineHtml.Parse(bold).TrueForAll(run => !run.Bold || run.Text == "New:"),
                    $"'</b/>' left the rest of the note bold: '{string.Join(", ", InlineHtml.Parse(bold))}'");
                Assert(InlineHtml.Parse("<i>x</i/> y").TrueForAll(run => !run.Italic || run.Text == "x"),
                    "'</i/>' left the rest of the note italic");
                Assert(InlineHtml.Parse("<a href=\"https://x.example\">x</a />after")
                        .TrueForAll(run => run.Link is null || run.Text == "x"),
                    "'</a />' left the rest of the note linked");

                // The opening self-closing forms still change no state.
                Assert(InlineHtml.Parse("before <b/> after").TrueForAll(run => !run.Bold),
                    "'<b/>' bolded the rest again");
                Assert(InlineHtml.Parse("Before <a href=\"https://x.example\"/> after")
                        .TrueForAll(run => run.Link is null),
                    "'<a …/>' linked the rest again");
                Assert(InlineHtml.PlainText("a<br/>b") == "a\nb", "'<br/>' stopped breaking the line");
            });

            Run("InlineHtml writes one space around an element that produces no text", () =>
            {
                // The pending space was committed on the opening tag and then
                // re-armed from producedText, so an empty element was spelt with
                // a space on each side of nothing.
                foreach (var html in new[]
                {
                    "Read <a href=\"https://x.example\"><img src=\"badge.png\"></a> the docs",
                    "Read <a href=\"https://x.example\"></a> the docs",
                    "Read <a href=\"https://x.example\">   </a> the docs"
                })
                {
                    Assert(InlineHtml.PlainText(html) == "Read the docs",
                        $"'{html}' rendered as '{InlineHtml.PlainText(html)}'");
                }

                // Both space cases above still hold: the space in front of <a>
                // stays outside the link, and so does the one in front of </a>.
                const string opening = "<b>See</b> <a href=\"https://example.com\">here</a>";
                Assert(InlineHtml.PlainText(opening) == "See here", "the opening-side space was lost");
                Assert(InlineHtml.Parse(opening).TrueForAll(run => run.Link is null || !run.Text.StartsWith(' ')),
                    "the opening-side space was swallowed into the link");

                const string closing = "See <a href=\"https://x.example\"><b>the page</b> </a>now";
                Assert(InlineHtml.PlainText(closing) == "See the page now", "the closing-side space was lost");
                Assert(InlineHtml.Parse(closing).TrueForAll(run => run.Link is null || run.Text.Trim().Length > 0),
                    "the closing-side space is inside the link again");
            });

            Run("InlineHtml keeps the element name when its own value is malformed", () =>
            {
                // The name was recorded after the value parse, so giving up on a
                // malformed value on the first token discarded the element too.
                Assert(InlineHtml.PlainText("line one<br = >line two") == "line one\nline two",
                    $"the line break was dropped: '{InlineHtml.PlainText("line one<br = >line two")}'");
                Assert(InlineHtml.PlainText("a<br = \"unterminated>b") == "a\nb",
                    $"got '{InlineHtml.PlainText("a<br = \"unterminated>b")}'");
            });

            Run("InlineHtml collapses a CRLF and leaves a non-breaking space alone", () =>
            {
                // macOS reads text by grapheme, where "\r\n" is one Character
                // equal to neither "\r" nor "\n". Both mirrors must agree here.
                Assert(InlineHtml.PlainText("line one\r\nline two") == "line one line two",
                    $"got '{InlineHtml.PlainText("line one\r\nline two")}'");
                Assert(InlineHtml.PlainText("a\r\n\r\n  b") == "a b",
                    "a run of CRLFs should collapse to one space");
                Assert(InlineHtml.PlainText("a&nbsp;b") == "a\u00A0b",
                    $"a non-breaking space is not collapsible: '{InlineHtml.PlainText("a&nbsp;b")}'");
            });

            Run("InlineHtmlText renders an anchor containing emphasis as one link", () =>
            {
                // Three runs, one destination: one Hyperlink, not three siblings
                // with three tab stops, three tooltips and three hyperlink nodes
                // announced to a screen reader. macOS renders this anchor as one
                // contiguous link too.
                const string html = "before <a href=\"https://x.com\">see <b>this</b> page</a> after";
                var runs = InlineHtml.Parse(html);
                Assert(runs.FindAll(run => run.Link is not null).Count == 3,
                    $"expected 3 linked runs, got '{string.Join(", ", runs)}'");

                var textBlock = new System.Windows.Controls.TextBlock();
                InlineHtmlText.Apply(textBlock, html);

                var inlines = textBlock.Inlines.ToList();
                Assert(inlines.Count == 3, $"expected 3 inlines, got {inlines.Count}");
                Assert(inlines[1] is System.Windows.Documents.Hyperlink,
                    $"expected the anchor to be one Hyperlink, got {inlines[1].GetType().Name}");

                var hyperlink = (System.Windows.Documents.Hyperlink)inlines[1];
                Assert(hyperlink.NavigateUri?.AbsoluteUri == "https://x.com/",
                    $"wrong destination: '{hyperlink.NavigateUri}'");
                Assert(hyperlink.Inlines.Count == 3,
                    $"expected the three runs inside one link, got {hyperlink.Inlines.Count}");
                Assert(hyperlink.Inlines.ToList()[1] is System.Windows.Documents.Run bolded
                        && bolded.FontWeight == FontWeights.Bold,
                    "emphasis inside the link was lost");
            });

            Run("AppcastItem.BulletPoints keeps inline emphasis and drops empty items", () =>
            {
                var item = new AppcastItem
                {
                    ReleaseNotes = "<ul><li><b>Bold lead</b> — detail.</li><li class=\"x\">  </li>"
                                 + "<li>Plain bullet.</li></ul>"
                };

                Assert(item.BulletPoints.Count == 2, $"expected 2 bullets, got {item.BulletPoints.Count}");
                Assert(InlineHtml.Parse(item.BulletPoints[0])[0].Bold,
                    "first bullet should start with a bold run");
                Assert(InlineHtml.PlainText(item.BulletPoints[1]) == "Plain bullet.",
                    $"got '{InlineHtml.PlainText(item.BulletPoints[1])}'");
            });

            Run("Every cloudTierEligible catalog id has a CloudAccuracyTier case", () =>
            {
                // shared-app-classification/AGENTS.md documents catalog edits as
                // data-only, but the Provider dropdown is built straight from the
                // catalog while persistence funnels through the CloudAccuracyTier
                // enum, whose FromString fallback is DeepgramNova3. A 12th
                // cloudTierEligible entry with no enum case would therefore be a
                // selectable row that transcribes and bills as Deepgram. Fail here
                // instead, on the PR that adds the entry.
                var entries = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared
                    .CloudTierEligibleProviders();
                Assert(entries.Count > 0, "cloud-stt-catalog.json exposed no cloudTierEligible providers");

                foreach (var entry in entries)
                {
                    var roundTripped = CloudAccuracyTierExtensions.FromString(entry.Id).ToStorageValue();
                    Assert(
                        string.Equals(roundTripped, entry.Id, StringComparison.OrdinalIgnoreCase),
                        $"catalog id '{entry.Id}' has no CloudAccuracyTier case — FromString falls back to "
                            + $"'{roundTripped}', so that Provider row would silently route and bill as Deepgram. "
                            + "Add the case to Models/CloudAccuracyTier.cs (and the macOS CloudAccuracyTier enum) "
                            + "in the same change as the catalog entry.");
                }
            });

            Run("Grok's empty model id resolves through a provider-scoped lookup", () =>
            {
                // Grok's API takes no `model` parameter, so its single registry
                // entry is stored under the empty id. The Model row is now a
                // one-item dropdown like every other provider's, so that id has
                // to resolve or the description, the price and the mode card's
                // model name all render blank.
                var grok = CloudTranscriptionModels.GetById("", CloudTranscriptionProvider.Grok);
                Assert(grok != null, "GetById(\"\", Grok) returned null — the Grok Model row would render blank");
                Assert(grok!.DisplayName == "Grok Speech-to-Text", $"got '{grok.DisplayName}'");
                Assert(!string.IsNullOrEmpty(grok.Description), "Grok entry has no description to show");

                // Unscoped, "" stays ambiguous: any provider left without a model
                // would otherwise resolve to Grok.
                Assert(CloudTranscriptionModels.GetById("") == null, "unscoped GetById(\"\") must stay null");
            });

            Run("BackupExportSettingsPage initializes under WPF", () =>
            {
                DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();

                var application = new Application();
                LoadApplicationResources(application);

                // Constructing the page exercises the exact construction-order NRE this
                // regression test covers. Export selection handlers must not run until
                // InitializeComponent has created the complete checkbox tree.
                var page = new BackupExportSettingsPage();
                if (!page.IsInitialized)
                    throw new InvalidOperationException("BackupExportSettingsPage did not finish WPF initialization.");

                // Post-construction changes prove handlers are attached and state is
                // recomputed; the button's default enabled value alone proves nothing.
                page.ExportSettingsCheckbox.IsChecked = false;
                page.ExportModesCheckbox.IsChecked = false;
                page.ExportVocabularyCheckbox.IsChecked = false;
                Assert(!page.ExportButton.IsEnabled,
                    "expected ExportButton to be disabled once all export sections are unchecked");

                page.ExportModesCheckbox.IsChecked = true;
                Assert(page.ExportButton.IsEnabled,
                    "expected ExportButton to be re-enabled after re-checking a section");

                application.Shutdown();
            });

            Run("VocabularyProcessor.ApplyReplacements trims even with no vocabulary configured — issue #92", () =>
            {
                DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();

                var processor = new VocabularyProcessor();
                var result = processor.ApplyReplacements("  hello world  ");

                Assert(result == "hello world", $"expected trimmed text with empty vocabulary, got '{result}'");
            });

            Run("EnglishSpellingRegionDefault maps a region to its spelling variant", () =>
            {
                Assert(EnglishSpellingRegionDefault.ForRegion("GB") == "british",
                    "expected GB to map to british");
                Assert(EnglishSpellingRegionDefault.ForRegion("IE") == "british",
                    "expected IE to map to british");
                Assert(EnglishSpellingRegionDefault.ForRegion("NZ") == "british",
                    "expected NZ to map to british");
                Assert(EnglishSpellingRegionDefault.ForRegion("AU") == "australian",
                    "expected AU to map to australian");
                Assert(EnglishSpellingRegionDefault.ForRegion("CA") == "canadian",
                    "expected CA to map to canadian");
                Assert(EnglishSpellingRegionDefault.ForRegion("US") == "american",
                    "expected US to map to american");

                // Case and padding come from whatever the OS reports.
                Assert(EnglishSpellingRegionDefault.ForRegion(" gb ") == "british",
                    "expected a lowercase, padded code to still map to british");

                // Anything unknown or missing keeps the historical american value.
                Assert(EnglishSpellingRegionDefault.ForRegion("JP") == "american",
                    "expected an unlisted region to fall back to american");
                Assert(EnglishSpellingRegionDefault.ForRegion(null) == "american",
                    "expected a null region to fall back to american");
                Assert(EnglishSpellingRegionDefault.ForRegion("") == "american",
                    "expected an empty region to fall back to american");
                Assert(EnglishSpellingRegionDefault.ForRegion("ZZ") == "american",
                    "expected an invalid region to fall back to american");
            });

            Run("ForCurrentRegion returns a variant the mode editor can select", () =>
            {
                var current = EnglishSpellingRegionDefault.ForCurrentRegion();
                Assert(
                    current is "american" or "british" or "australian" or "canadian",
                    $"expected a known spelling variant, got '{current}'");
            });

            Run("Seeded default modes carry the region's spelling variant", () =>
            {
                var expected = EnglishSpellingRegionDefault.ForCurrentRegion();
                var modes = ModeDefaults.GetDefaultModes();

                Assert(modes.Count > 0, "expected at least one default mode");
                foreach (var mode in modes)
                {
                    Assert(mode.EnglishSpelling == expected,
                        $"expected default mode '{mode.Name}' to seed '{expected}', got '{mode.EnglishSpelling ?? "<null>"}'");
                }
            });

            // HYPERWHISPER-SP / HYPERWHISPER-FM parity: LicenseNetworkService.LicenseVerdictReason
            // decides whether a non-200 license-validate reply is an ordinary verdict (log only) or
            // a genuine backend incident (capture to Sentry). The backend answers 400 with the SAME
            // {"valid":false,"error":"..."} shape for a lapsed license, a nonexistent key, AND a real
            // infrastructure fault, so the shape alone cannot decide this - only the server's own
            // `reason` field can. Mirrors macOS LicenseNetworkVerdictReportingTests.swift exactly.
            Run("LicenseVerdictReason recognizes reason=not_entitled at 400 as an ordinary verdict", () =>
            {
                var body = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License is revoked","reason":"not_entitled"}""");
                var reason = LicenseNetworkService.LicenseVerdictReason(400, body);
                Assert(reason == LicenseNetworkService.NotEntitledReason,
                    $"expected '{LicenseNetworkService.NotEntitledReason}', got '{reason ?? "<null>"}'");
            });

            Run("LicenseVerdictReason: reason alone (no other fields) is enough at 400", () =>
            {
                var body = System.Text.Encoding.UTF8.GetBytes("""{"reason":"not_entitled"}""");
                var reason = LicenseNetworkService.LicenseVerdictReason(400, body);
                Assert(reason == LicenseNetworkService.NotEntitledReason, $"got '{reason ?? "<null>"}'");
            });

            Run("LicenseVerdictReason: lookup_failed and bad_request are NOT an ordinary verdict", () =>
            {
                var lookupFailed = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"Failed to validate with Polar","reason":"lookup_failed"}""");
                var badRequest = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License key is required","reason":"bad_request"}""");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, lookupFailed) == "lookup_failed",
                    "expected 'lookup_failed' returned verbatim");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, badRequest) == "bad_request",
                    "expected 'bad_request' returned verbatim");
            });

            Run("LicenseVerdictReason: an unrecognised reason is returned verbatim but is not a verdict", () =>
            {
                var body = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"...","reason":"quota_exceeded"}""");
                var reason = LicenseNetworkService.LicenseVerdictReason(400, body);
                Assert(reason == "quota_exceeded", $"got '{reason ?? "<null>"}'");
                Assert(reason != LicenseNetworkService.NotEntitledReason,
                    "an unrecognised reason must never equal the not-entitled constant");
            });

            Run("LicenseVerdictReason: a body with no reason field is not a verdict (must still be captured)", () =>
            {
                var noReason = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License is revoked"}""");
                var validTrue = System.Text.Encoding.UTF8.GetBytes("""{"valid":true}""");
                var emptyObject = System.Text.Encoding.UTF8.GetBytes("{}");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, noReason) == null,
                    "expected null for a 400 body with no reason field");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, validTrue) == null,
                    "expected null for valid:true with no reason field");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, emptyObject) == null,
                    "expected null for an empty JSON object");
            });

            Run("LicenseVerdictReason: an empty body or an HTML captive-portal page is not a verdict", () =>
            {
                var html = System.Text.Encoding.UTF8.GetBytes(
                    "<!DOCTYPE html><html><body><h1>Sign in to continue</h1></body></html>");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, System.Array.Empty<byte>()) == null,
                    "expected null for an empty body");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, html) == null,
                    "expected null for an undecodable HTML body");
            });

            Run("LicenseVerdictReason: non-object JSON and a non-string reason are not a verdict", () =>
            {
                var array = System.Text.Encoding.UTF8.GetBytes("[]");
                var nonStringReason = System.Text.Encoding.UTF8.GetBytes("""{"valid":false,"reason":7}""");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, array) == null, "expected null for a JSON array");
                Assert(LicenseNetworkService.LicenseVerdictReason(400, nonStringReason) == null,
                    "expected null for a non-string reason value");
            });

            Run("LicenseVerdictReason: only status 400 is eligible - same body at 500 is not a verdict", () =>
            {
                var verdictBody = System.Text.Encoding.UTF8.GetBytes(
                    """{"valid":false,"error":"License is revoked","reason":"not_entitled"}""");
                Assert(LicenseNetworkService.LicenseVerdictReason(500, verdictBody) == null,
                    "expected null - a 5xx never reaches this predicate as a terminal result, and even if it did, only 400 is the documented verdict status");
                Assert(LicenseNetworkService.LicenseVerdictReason(401, verdictBody) == null, "expected null at 401");
                Assert(LicenseNetworkService.LicenseVerdictReason(200, verdictBody) == null, "expected null at 200");
            });

            Console.WriteLine(_failures == 0
                ? "All smoke tests passed."
                : $"{_failures} smoke test(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, null);

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup; a failed delete must not mask the smoke result.
            }
        }
    }

    // =========================================================================
    // Harness
    // =========================================================================

    private static void Run(string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine($"  ok   {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine($"  FAIL {name}");
            Console.Error.WriteLine($"       {ex}");
        }
    }

    private static void RunAsync(string name, Func<Task> check)
        => Run(name, () => check().GetAwaiter().GetResult());

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T expected)
        {
            return expected;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name} was not thrown");
    }

    private static T Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T expected)
        {
            return expected;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name} was not thrown");
    }

    /// <summary>Minimal core-shaped request; the stub handler never reads it.</summary>
    private static uniffi.hyperwhisper_core.HttpRequest BuildDummyRequest()
        => new(
            uniffi.hyperwhisper_core.HttpMethod.Get,
            "http://127.0.0.1:9/smoke",
            new List<Header>(),
            new Body.Empty());

    /// <summary>
    /// Scripted HttpMessageHandler: counts sends and delegates each attempt to
    /// the supplied responder (which may throw, hang on the token, or respond).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;
        public int Sends;

        public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return _respond(cancellationToken);
        }
    }

    /// <summary>
    /// Response body that invokes <paramref name="onDispose"/> when the response
    /// message is disposed — which <c>RustHttpExecutor</c> does strictly AFTER
    /// <c>SendAsync</c> returned and after the body was read. That lets a test
    /// change the world (cancel a token) at a point no HttpClient-internal read
    /// can observe, so only the code under test can react to the change.
    /// </summary>
    private sealed class SignalOnDisposeContent : StringContent
    {
        private readonly Action _onDispose;
        private bool _signalled;

        public SignalOnDisposeContent(string content, Action onDispose) : base(content)
            => _onDispose = onDispose;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && !_signalled)
            {
                _signalled = true;
                _onDispose();
            }
        }
    }

    /// <summary>
    /// Current length of every file in the log directory. Taken before the act
    /// step so <see cref="ReadLogSince"/> returns exactly what that step
    /// appended — including into a file that did not exist yet, which is what a
    /// run crossing local midnight produces (LoggingService derives its path
    /// from <c>DateTime.Now</c> on every single write).
    /// </summary>
    private static Dictionary<string, long> SnapshotLogOffsets()
    {
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var directory = LoggingService.LogDirectory;
        if (!Directory.Exists(directory))
        {
            return offsets;
        }

        foreach (var file in Directory.GetFiles(directory))
        {
            offsets[file] = new FileInfo(file).Length;
        }
        return offsets;
    }

    /// <summary>
    /// Everything LoggingService appended anywhere in the log directory since
    /// <paramref name="offsets"/> was taken, in filename order (the names are
    /// <c>hyperwhisper-yyyy-MM-dd.log</c>, so that is chronological). Shared
    /// read access because the writer may still hold a file; a file that is
    /// unreadable or gone contributes nothing rather than throwing, so the
    /// caller's assertion — which prints what WAS captured — reports the
    /// problem instead of this helper.
    /// </summary>
    private static string ReadLogSince(Dictionary<string, long> offsets)
    {
        var directory = LoggingService.LogDirectory;
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        var files = Directory.GetFiles(directory);
        Array.Sort(files, StringComparer.Ordinal);

        var captured = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            var from = offsets.TryGetValue(file, out var offset) ? offset : 0L;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (from >= stream.Length)
                {
                    continue;
                }
                stream.Seek(from, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                captured.Append(reader.ReadToEnd());
            }
            catch (IOException)
            {
                // Includes FileNotFoundException; see summary.
            }
        }
        return captured.ToString();
    }

    /// <summary>
    /// Log text split into non-empty lines, newline style stripped. Multi-line
    /// entries (LoggingService.Warn with an exception writes one) therefore
    /// contribute one element per physical line, which is what the positional
    /// banner assertions want.
    /// </summary>
    private static string[] SplitLogLines(string text)
    {
        var raw = text.Split('\n');
        var lines = new List<string>(raw.Length);
        foreach (var line in raw)
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }
        return lines.ToArray();
    }

    /// <summary>
    /// Minimal IStreamingProviderStrategy for exercising StreamingTranscriptionClient
    /// methods (like AppendFinalTranscript) that never touch the provider strategy.
    /// Any member a test does end up hitting should throw loudly rather than fake
    /// network behavior.
    /// </summary>
    private sealed class NoOpStreamingProviderStrategy : IStreamingProviderStrategy
    {
        public string TranscriptionProviderLabel => "test";
        public bool SupportsVocabulary => false;
        public bool SessionStartsOnWebSocketOpen => false;
        public int AudioSampleRate => 16000;

        public Uri? BuildWebSocketUri(StreamingSessionConfig config) => throw new NotSupportedException();
        public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config) => throw new NotSupportedException();
        public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData) => throw new NotSupportedException();
        public StreamingProviderEvent? ParseMessage(string text) => throw new NotSupportedException();
        public IReadOnlyList<StreamingStopStep> GetStopSequence() => throw new NotSupportedException();
        public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config) => throw new NotSupportedException();
        public Task OnAudioSendOpportunityAsync(
            Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static void LoadApplicationResources(Application application)
    {
        AddResourceDictionary(application, "Themes/LightColors.xaml");
        AddResourceDictionary(application, "Themes/Brushes.xaml");
        AddResourceDictionary(application, "Themes/Generic.xaml");
    }

    private static void AddResourceDictionary(Application application, string resourcePath)
    {
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/HyperWhisper;component/{resourcePath}", UriKind.Absolute)
        });
    }
}
