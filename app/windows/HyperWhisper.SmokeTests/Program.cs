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
using System.Text.Json;
using System.Windows;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.Streaming;
using HyperWhisper.Services.Transcription;
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
