using System.Net;
using System.Text;
using System.Text.Json;
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
    // The CJK segment-join policy (issue #286). The parakeet daemon and the
    // Linux live-delivery path both pick their separator from this instead of
    // keeping a private ja|zh|ko|yue table.
    ("the no-space join policy stays in the shared core", () =>
    {
        foreach (var code in new[] { "ja", "zh", "ko", "yue", "th", "zh-Hant" })
        {
            Assert.True(SharedCoreBridge.IsNoSpaceLanguage(code));
        }
        // Case-insensitive, whitespace-tolerant, two-character prefix fallback.
        Assert.True(SharedCoreBridge.IsNoSpaceLanguage("JA"));
        Assert.True(SharedCoreBridge.IsNoSpaceLanguage("zh-CN"));
        Assert.True(SharedCoreBridge.IsNoSpaceLanguage("  ja  "));
        // "No language declared" is not a no-space language — text-based
        // detection is ContainsCjk's job, not this one's.
        foreach (var code in new[] { "en", "de", "en-US", "auto", "" })
        {
            Assert.False(SharedCoreBridge.IsNoSpaceLanguage(code));
        }
        Assert.False(SharedCoreBridge.IsNoSpaceLanguage(null));
        return Task.CompletedTask;
    }),
    // The auto-language hole. "auto" is the DEFAULT streaming language and what
    // the hosts send the daemon for a mode with no language, so a policy read
    // from the language alone would leave #286 unfixed for almost every user.
    ("the segment separator falls back to the text when no language is declared", () =>
    {
        // A declared language decides on its own, whatever the text looks like.
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("ja", "hello", "world"));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("ZH-CN", "hello", "world"));
        Assert.Equal(" ", SharedCoreBridge.SegmentSeparator("en", "こんにちは", "世界"));

        // With nothing declared, the text decides — this is the case the fix
        // exists for: default settings, Japanese dictation.
        foreach (var automatic in new string?[] { null, "", "   ", "auto", "AUTO", " Auto " })
        {
            Assert.True(SharedCoreBridge.IsAutomaticLanguage(automatic));
            Assert.Equal("", SharedCoreBridge.SegmentSeparator(automatic, "こんにちは", "世界"));
            Assert.Equal(" ", SharedCoreBridge.SegmentSeparator(automatic, "hello", "world"));
        }
        Assert.False(SharedCoreBridge.IsAutomaticLanguage("en"));
        Assert.False(SharedCoreBridge.IsAutomaticLanguage("ja"));

        // EITHER side of the boundary being continuous-script joins without a
        // space. The accumulated text is the primary signal, because a single
        // segment often carries no script evidence at all: these four all used to
        // wedge a space into the middle of a Japanese dictation.
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "こんにちは", ""));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "こんにちは", null));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "日本語", "。"));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "これは", "2024年"));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "これは", "OK"));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "これはOK", "です"));
        // The first boundary of a stream has no accumulated text, so the incoming
        // segment still has to be able to decide it.
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "", "世界"));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", null, "世界"));
        // Nothing on either side is not evidence of a no-space language.
        Assert.Equal(" ", SharedCoreBridge.SegmentSeparator("auto", "", ""));
        Assert.Equal(" ", SharedCoreBridge.SegmentSeparator("auto", null, null));

        // Thai is a no-space LANGUAGE but is not CJK, so the auto fallback has to
        // detect it too or `th` and `auto` disagree on the same audio.
        Assert.True(SharedCoreBridge.IsNoSpaceLanguage("th"));
        Assert.True(SharedCoreBridge.IsContinuousScript("สวัสดี"));
        Assert.False(SharedCoreBridge.ContainsCjk("สวัสดี"));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("th", "สวัสดี", "ครับ"));
        Assert.Equal("", SharedCoreBridge.SegmentSeparator("auto", "สวัสดี", "ครับ"));

        // End to end, through the production join: the three-segment Japanese
        // dictation from issue #286 on the default "auto" language. Both sinks
        // build from these separators, so asserting the join asserts the typed
        // text and the saved text at once.
        Assert.Equal("こんにちは世界です", SharedCoreBridge.JoinSegments("auto", ["こんにちは", "世界", "です"]));
        Assert.Equal("hello there world", SharedCoreBridge.JoinSegments("auto", ["hello", "there", "world"]));
        // A segment that decoded to nothing is skipped, not separated.
        Assert.Equal("こんにちは世界", SharedCoreBridge.JoinSegments("auto", ["こんにちは", "", "世界"]));
        Assert.Equal("hello world", SharedCoreBridge.JoinSegments("auto", ["hello", "   ", "world"]));
        Assert.Equal("สวัสดีครับ", SharedCoreBridge.JoinSegments("auto", ["สวัสดี", "ครับ"]));
        // Where the boundaries fell must not change the result — that is what
        // keeps the daemon and the host in agreement when their VAD differs.
        Assert.Equal("これはtestです", SharedCoreBridge.JoinSegments("auto", ["これはtestです"]));
        Assert.Equal("これはtestです", SharedCoreBridge.JoinSegments("auto", ["これは", "test", "です"]));
        Assert.Equal("これはtestです", SharedCoreBridge.JoinSegments("auto", ["これ", "はtest", "です"]));
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
        Assert.Equal(13, providers.Count);
        Assert.Equal(13, providers.Select(value => value.Provider).Distinct().Count());
        Assert.True(providers.All(value => value.SupportsBatch));
        Assert.Equal(3, providers.Count(value => value.IsMultiStep));
        return Task.CompletedTask;
    }),
    ("single-shot providers use Rust request and response contracts", TestSingleShotProvidersAsync),
    ("multi-step providers execute upload poll parse and cleanup flows", TestMultiStepProvidersAsync),
    ("observer diagnostics redact credentials and request bodies", TestObserverRedactionAsync),
    ("retry policy retries transient responses deterministically", TestRetryAsync),
    ("the retry wall-clock budget stops a hard-down provider early", TestRetryBudgetAsync),
    ("unauthorized responses are classified without leaking provider bodies", TestUnauthorizedAsync),
    ("cancellation stops in-flight HTTP and returns structured cancellation", TestCancellationAsync),
    ("live strategies construct and parse all six provider protocols", TestLiveProvidersAsync),
    ("gemini live setup frame pins the input_audio_transcription position", TestGeminiLiveSetupFrameAsync),
    ("a mid-session provider complete is a turn boundary not the end", TestMidSessionTurnBoundaryAsync),
    ("the audio pump waits for the provider setup handshake", TestAudioPumpWaitsForStartAsync),
    ("hyperwhisper cloud live route is derived from the selected tier", TestCloudLiveRouteAsync),
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
    ("inline base64 audio bodies assemble prefix + base64(file) + suffix", TestInlineBase64BodyAsync),
    ("live terminal-error policy comes from the shared core", () =>
    {
        // The policy this head never had (issue #281). The macOS suite
        // `StreamingProviderErrorPolicyTests` is the full conformance proof;
        // these are the cases that decide whether THIS head retries.
        Assert.Equal(
            PortableLiveErrorOutcome.Terminal,
            SharedCoreBridge.ClassifyLiveErrorMessage("Credit balance exhausted"));
        Assert.Equal(
            PortableLiveErrorOutcome.Terminal,
            SharedCoreBridge.ClassifyLiveErrorMessage("You exceeded your current quota, check billing."));
        // A rate limit clears on its own; a request id that merely contains
        // "401" is not an auth failure. Both must keep their reconnect.
        Assert.Equal(
            PortableLiveErrorOutcome.Transient,
            SharedCoreBridge.ClassifyLiveErrorMessage("Rate limit reached for requests. Try again in 20s."));
        Assert.Equal(
            PortableLiveErrorOutcome.Transient,
            SharedCoreBridge.ClassifyLiveErrorMessage("Stream interrupted (request_id: req_4013f2c8)."));
        Assert.Equal(
            PortableLiveErrorOutcome.Transient,
            SharedCoreBridge.ClassifyLiveErrorMessage(string.Empty));

        Assert.Equal(PortableLiveUpgradeRefusal.InsufficientCredits, SharedCoreBridge.LiveUpgradeRefusal(402));
        Assert.Equal(PortableLiveUpgradeRefusal.Unauthorized, SharedCoreBridge.LiveUpgradeRefusal(401));
        Assert.Equal(PortableLiveUpgradeRefusal.Unauthorized, SharedCoreBridge.LiveUpgradeRefusal(403));
        foreach (var status in new[] { 101, 0, 200, 400, 429, 500, 503, -1, 70000 })
        {
            Assert.True(SharedCoreBridge.LiveUpgradeRefusal(status) is null, $"HTTP {status} must keep its retry");
        }

        foreach (var code in new[] { 1002, 1003, 1007, 1008, 1009, 1011 })
        {
            Assert.True(SharedCoreBridge.IsTerminalLiveCloseCode(code), $"close {code} must be terminal");
        }
        foreach (var code in new[] { 1000, 1001, 1006, 1012, 1013, 4001, -1, 70000 })
        {
            Assert.True(!SharedCoreBridge.IsTerminalLiveCloseCode(code), $"close {code} must keep its retry");
        }
        return Task.CompletedTask;
    }),
    ("live capabilities and language normalization come from the shared core", () =>
    {
        Assert.Equal(24000, SharedCoreBridge.LiveRequiredSampleRate(LiveTranscriptionProvider.OpenAi));
        Assert.Equal(24000, LiveCloudTranscriptionService.GetRequiredSampleRate(LiveTranscriptionProvider.OpenAi));
        foreach (var provider in new[]
                 {
                     LiveTranscriptionProvider.Deepgram,
                     LiveTranscriptionProvider.ElevenLabs,
                     LiveTranscriptionProvider.Grok,
                     LiveTranscriptionProvider.HyperWhisperCloud,
                 })
        {
            Assert.Equal(16000, SharedCoreBridge.LiveRequiredSampleRate(provider));
            Assert.Equal(16000, LiveCloudTranscriptionService.GetRequiredSampleRate(provider));
        }
        // The two local engines are not WebSocket protocols; the service keeps
        // its own literal for them and the core has no arm at all.
        Assert.Equal(16000, LiveCloudTranscriptionService.GetRequiredSampleRate(LiveTranscriptionProvider.ParakeetLocal));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SharedCoreBridge.LiveRequiredSampleRate(LiveTranscriptionProvider.ParakeetLocal));

        Assert.True(SharedCoreBridge.LiveSupportsVocabulary(LiveTranscriptionProvider.Deepgram));
        Assert.True(SharedCoreBridge.LiveSupportsVocabulary(LiveTranscriptionProvider.Grok));
        Assert.True(SharedCoreBridge.LiveSupportsVocabulary(LiveTranscriptionProvider.HyperWhisperCloud));
        Assert.True(!SharedCoreBridge.LiveSupportsVocabulary(LiveTranscriptionProvider.ElevenLabs));
        Assert.True(!SharedCoreBridge.LiveSupportsVocabulary(LiveTranscriptionProvider.OpenAi));

        Assert.Equal("Deepgram (Streaming)", SharedCoreBridge.LiveProviderLabel(LiveTranscriptionProvider.Deepgram));
        Assert.Equal("ElevenLabs (Streaming)", SharedCoreBridge.LiveProviderLabel(LiveTranscriptionProvider.ElevenLabs));
        Assert.Equal("OpenAI (Streaming)", SharedCoreBridge.LiveProviderLabel(LiveTranscriptionProvider.OpenAi));
        Assert.Equal("xAI (Streaming)", SharedCoreBridge.LiveProviderLabel(LiveTranscriptionProvider.Grok));
        Assert.Equal(
            "HyperWhisper Cloud (Streaming)",
            SharedCoreBridge.LiveProviderLabel(LiveTranscriptionProvider.HyperWhisperCloud));

        Assert.True(SharedCoreBridge.NormalizeLiveLanguage(null) is null);
        Assert.True(SharedCoreBridge.NormalizeLiveLanguage("  ") is null);
        Assert.True(SharedCoreBridge.NormalizeLiveLanguage("AUTO") is null);
        Assert.Equal("en", SharedCoreBridge.NormalizeLiveLanguage(" EN-US "));
        Assert.Equal("zh", SharedCoreBridge.NormalizeLiveLanguage("zh-Hans"));
        return Task.CompletedTask;
    }),
    ("a terminal provider error frame is marked terminal, a transient one is not", TestLiveTerminalErrorFrameAsync),
    ("the Rust live protocol answers the connect descriptor for all five providers", TestRustLiveConnectAsync),
    ("the Rust live protocol frames audio without the samples crossing the FFI", TestRustLiveFramingAsync),
    ("the Rust live protocol parses every provider's literal frames", TestRustLiveParseAsync),
    ("the Rust live protocol answers an ordered stop sequence per provider", TestRustLiveStopSequenceAsync),
    ("OpenAI's commit gate is the server's 100 ms rule, driven by the caller's clock", TestRustLiveCommitGateAsync),
    ("the ordered stop sequence reaches the socket in order, with its gap", TestLiveStopSequenceOnTheWireAsync),
    ("a final transcript that lands after the stop sequence is still counted", TestLiveLateFinalTranscriptAsync),
    ("a failure the receive loop recorded survives a throwing close", TestLiveRecordedFailureSurvivesCloseAsync),
    ("a failure that lands after our own close cannot destroy the transcript", TestLivePostCloseFailureCannotDestroyTranscriptAsync),
    ("a cancel inside the post-close drain abandons the session", TestLiveCancelDuringDrainAsync),
    ("the live protocol owns a Rust handle and is disposed", TestRustLiveDisposalAsync),
    // The no-speech diagnostic (issue #291). Windows and macOS both delegate
    // measurement, classification and Sentry grouping here, and neither head's
    // test suite runs on Linux — this is the only gate that executes the shared
    // classifier itself rather than compiling against it.
    ("the no-speech thresholds and dBFS maths stay in the shared core", () =>
    {
        Assert.Equal(0.01f, PortableNoSpeechDiagnostics.SilenceThreshold);
        Assert.Equal(-120.0, PortableNoSpeechDiagnostics.MinimumDbfs);
        Assert.Equal(-50.0, PortableNoSpeechDiagnostics.ConfirmedSilencePeakDbfs);
        Assert.Equal(-38.0, PortableNoSpeechDiagnostics.LowSignalRmsDbfs);
        Assert.Equal(0.06, PortableNoSpeechDiagnostics.LowSignalNonSilentRatio);

        // Digital silence and a negative amplitude report the floor, never
        // -infinity or NaN, which would poison the Sentry tag.
        Assert.Equal(-120.0, PortableNoSpeechDiagnostics.ToDbfs(0));
        Assert.Equal(-120.0, PortableNoSpeechDiagnostics.ToDbfs(-1));
        Assert.Equal(0.0, PortableNoSpeechDiagnostics.ToDbfs(1.0));

        // Floors, does NOT truncate: a negative buckets downward. Truncation
        // would put -38.2 in "-35dbfs" and shift every bucket in the facet.
        Assert.Equal("-40dbfs", PortableNoSpeechDiagnostics.BucketDbfs(-38.2));
        Assert.Equal("silent", PortableNoSpeechDiagnostics.BucketDbfs(-120.0));
        return Task.CompletedTask;
    }),
    ("summarizing an empty accumulation floors instead of dividing by zero", () =>
    {
        var empty = PortableNoSpeechDiagnostics.Summarize(new PortableSignalAccumulation(0, 0, 0, 0));
        Assert.Equal(-120.0, empty.PeakDbfs);
        Assert.Equal(-120.0, empty.RmsDbfs);
        Assert.Equal(0.0, empty.NonSilentRatio);

        var fullScale = PortableNoSpeechDiagnostics.Summarize(new PortableSignalAccumulation(4, 4, 4.0, 1.0));
        Assert.Equal(0.0, fullScale.PeakDbfs);
        Assert.Equal(0.0, fullScale.RmsDbfs);
        Assert.Equal(1.0, fullScale.NonSilentRatio);
        return Task.CompletedTask;
    }),
    ("the five no-speech arms are evaluated in the Windows order", () =>
    {
        static PortableNoSpeechOutcome Classify(
            bool analysisSucceeded,
            long? decodedSampleCount,
            bool emptyTranscriptWithoutFlag,
            bool backendNoSpeechDetected,
            double peakDbfs,
            double rmsDbfs,
            double nonSilentRatio)
            => PortableNoSpeechDiagnostics.Classify(new PortableNoSpeechInput(
                analysisSucceeded,
                decodedSampleCount,
                emptyTranscriptWithoutFlag,
                backendNoSpeechDetected,
                peakDbfs,
                rmsDbfs,
                nonSilentRatio));

        // 1. A failed analysis must stay ahead of everything: a zero sample
        //    count means nothing when no decode loop ran.
        Assert.Equal(
            PortableNoSpeechOutcome.NoSpeech,
            Classify(false, 0, false, true, -120, -120, 0));

        // 2. A zero-sample recording is a recorder failure with its own
        //    identity, even when the provider ALSO returned an empty transcript
        //    without its flag.
        Assert.Equal(
            PortableNoSpeechOutcome.EmptyRecording,
            Classify(true, 0, true, false, -120, -120, 0));

        // An unknown count (no read loop) is deliberately NOT empty.
        Assert.Equal(
            PortableNoSpeechOutcome.Skip,
            Classify(true, null, false, true, -120, -120, 0));

        // 3. An empty transcript with no flag is a provider anomaly whatever
        //    the signal looks like - it beats both skip arms below.
        Assert.Equal(
            PortableNoSpeechOutcome.NoSpeech,
            Classify(true, 48000, true, true, -95, -100, 0));

        // 4. Confirmed dead silence, which does not consult the backend flag.
        Assert.Equal(
            PortableNoSpeechOutcome.Skip,
            Classify(true, 48000, false, false, -80, -90, 0));
        Assert.Equal(
            PortableNoSpeechOutcome.NoSpeech,
            Classify(true, 48000, false, false, -40, -90, 0));

        // 5. The real HYPERWHISPER-PA/-QB/-VY sample the thresholds were tuned
        //    against, and the inclusive boundary.
        Assert.Equal(
            PortableNoSpeechOutcome.Skip,
            Classify(true, 48000, false, true, -30, -39.64, 0.046));
        Assert.Equal(
            PortableNoSpeechOutcome.Skip,
            Classify(true, 48000, false, true, -30, -38.0, 0.06));

        // BOTH low-signal conditions must hold - an OR would let one quiet
        // reading suppress a genuine backend-disagreement anomaly.
        Assert.Equal(
            PortableNoSpeechOutcome.NoSpeech,
            Classify(true, 48000, false, true, -30, -39.64, 0.5));
        Assert.Equal(
            PortableNoSpeechOutcome.NoSpeech,
            Classify(true, 48000, false, true, -30, -10.0, 0.046));

        // The cohort the diagnostic exists to catch: healthy speech energy and
        // no transcript.
        Assert.Equal(
            PortableNoSpeechOutcome.NoSpeech,
            Classify(true, 48000, false, true, -18.47, -22.0, 0.35));

        // A negative count cannot come from a decode loop; it takes the
        // "unknown" answer rather than wrapping into an enormous one.
        Assert.Equal(
            PortableNoSpeechOutcome.Skip,
            Classify(true, -5, false, true, -120, -120, 0));
        return Task.CompletedTask;
    }),
    ("the no-speech fingerprint keeps Windows' element order and each head's root", () =>
    {
        static string Fingerprint(string root, PortableModeIdentity? mode) => string.Join(
            "|",
            PortableNoSpeechDiagnostics.BuildFingerprint(root, "live_recording", "provider_no_speech", mode));

        // Byte-identical to what Windows emitted before #291 - its live Sentry
        // groups have to survive the move into the core.
        Assert.Equal(
            "transcription-no-speech|live_recording|provider_no_speech|local|whisper",
            Fingerprint("transcription-no-speech", new PortableModeIdentity("local", "groq", "whisper")));
        Assert.Equal(
            "transcription-no-speech|live_recording|provider_no_speech|cloud|groq",
            Fingerprint("transcription-no-speech", new PortableModeIdentity("cloud", "groq", "whisper")));

        // The root is the caller's and is NOT unified: sharing it would merge
        // macOS events into Windows' live issues.
        Assert.Equal(
            "macos-transcription-no-speech|live_recording|provider_no_speech|local|parakeet",
            Fingerprint("macos-transcription-no-speech", new PortableModeIdentity("local", "groq", "parakeet")));

        // The production regression: two local modes with different leftover
        // cloud vendors are ONE condition and must be one group.
        Assert.Equal(
            Fingerprint("transcription-no-speech", new PortableModeIdentity("local", "groq", "parakeet")),
            Fingerprint("transcription-no-speech", new PortableModeIdentity("local", "gemini", "parakeet")));

        // ...and a null or non-canonical provider type routes local just like
        // the dispatch sites do, so it must not re-split the same cohort.
        Assert.Equal(
            Fingerprint("transcription-no-speech", new PortableModeIdentity(null, "groq", "whisper")),
            Fingerprint("transcription-no-speech", new PortableModeIdentity("", "gemini", "whisper")));

        // "No mode at all" is a different fact from "a mode with nothing
        // written on it" - the nullable argument is what keeps them apart.
        var absent = Fingerprint("transcription-no-speech", null);
        var blank = Fingerprint("transcription-no-speech", new PortableModeIdentity(null, null, null));
        Assert.True(absent.EndsWith("|unknown|none", StringComparison.Ordinal), absent);
        Assert.True(blank.EndsWith("|local|none", StringComparison.Ordinal), blank);
        Assert.Equal(5, PortableNoSpeechDiagnostics.BuildFingerprint("r", "s", "d", null).Length);
        return Task.CompletedTask;
    }),
    ("the no-speech tags mask a local mode's stale cloud vendor", () =>
    {
        var staleLocal = new PortableModeIdentity("local", "groq", "whisper");
        Assert.Equal("none", PortableNoSpeechDiagnostics.CloudProviderTag(staleLocal));
        Assert.Equal("whisper", PortableNoSpeechDiagnostics.LocalEngineTag(staleLocal));

        Assert.Equal("groq", PortableNoSpeechDiagnostics.CloudProviderTag(
            new PortableModeIdentity("cloud", "groq", "whisper")));

        Assert.Equal("none", PortableNoSpeechDiagnostics.CloudProviderTag(null));
        Assert.Equal("none", PortableNoSpeechDiagnostics.LocalEngineTag(null));
        // Blank is not an engine.
        Assert.Equal("none", PortableNoSpeechDiagnostics.LocalEngineTag(
            new PortableModeIdentity("local", null, "  ")));
        return Task.CompletedTask;
    }),
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
        new ProviderCase(CloudTranscriptionProvider.GeminiTranscribe, "gemini-3.5-transcribe", "{\"steps\":[{\"content\":[{\"text\":\"gemini transcribe text\"}]}]}", "gemini transcribe text"),
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

/// <summary>
/// The <c>Body.JsonWithBase64File</c> transport: the platform, not Rust, splices
/// the base64 of the audio between the two JSON fragments Rust produced. It is
/// the only body variant where the audio bytes are assembled on this side, and
/// <c>RustHttpTransport.BuildRequestMessage</c>'s switch is not exhaustive — a
/// missing arm would send a body-less request rather than fail to compile. This
/// test is the guard for that.
/// </summary>
static async Task TestInlineBase64BodyAsync()
{
    // Deliberately 7 bytes: not a multiple of 3, so a wrong encoder that drops
    // the final partial group or forgets padding fails here.
    const string audioBytes = "abcdefg";
    var audio = TempAudio(audioBytes);
    try
    {
        var handler = new RecordingHandler((_, _) =>
            Json("{\"steps\":[{\"content\":[{\"text\":\"inline text\"}]}]}"));
        using var service = new CloudTranscriptionService(handler, new StaticCredentials(), Sharing);
        var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
            CloudTranscriptionProvider.GeminiTranscribe,
            audio,
            "gemini-3.5-transcribe",
            Language: "en-US",
            Vocabulary: ["HyperWhisper"]));

        Assert.True(result.IsSuccess);
        Assert.Equal("inline text", result.Transcript!.Text);
        Assert.NotNull(handler.LastBody);

        var body = Encoding.UTF8.GetString(handler.LastBody!);
        // Valid JSON, with the audio inline where Rust left the placeholder.
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var input = document.RootElement.GetProperty("input")[0];
        Assert.Equal("audio", input.GetProperty("type").GetString());
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(audioBytes)),
            input.GetProperty("data").GetString());
        Assert.Equal(
            "gemini-3.5-transcribe",
            document.RootElement.GetProperty("model").GetString());

        // Content-Length must match what was actually written, or HttpClient
        // would have thrown before we got here — assert it explicitly anyway.
        Assert.Equal(body.Length, handler.LastBodyLength);

        // The vendor rejects custom_vocabulary sent with either of these.
        var config = document.RootElement
            .GetProperty("generation_config")
            .GetProperty("transcription_config");
        Assert.True(config.TryGetProperty("custom_vocabulary", out _));
        Assert.False(config.TryGetProperty("diarization_mode", out _));
        Assert.False(config.TryGetProperty("timestamp_granularities", out _));
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

// Issue #379: an always-503 provider used to burn all 8 core attempts, which is
// 1+2+4+8+16+32+64 = 127s of sleep and a ~150s user-visible hang. The wall-clock
// budget cuts the sequence at the attempt whose NEXT sleep would land past it.
static async Task TestRetryBudgetAsync()
{
    var audio = TempAudio();
    try
    {
        // A 5s budget: the 1s, 2s and 4s sleeps all fit, the 8s fourth does not,
        // so exactly 4 requests go out and 3 delays are taken.
        var budgeted = new ImmediateDelay();
        var budgetedSends = 0;
        var budgetedHandler = new RecordingHandler((_, _) =>
        {
            budgetedSends++;
            return Json("{\"error\":\"provider down\"}", HttpStatusCode.ServiceUnavailable);
        });
        using (var service = new CloudTranscriptionService(
            budgetedHandler, new StaticCredentials(), Sharing, budgeted, observer: null, retryBudgetMs: 5_000))
        {
            var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
                CloudTranscriptionProvider.Groq, audio, "whisper-large-v3"));
            Assert.False(result.IsSuccess);
            Assert.Equal(4, budgetedSends);
            Assert.Equal(4, result.Attempts);
            Assert.Equal(3, budgeted.Delays.Count);
        }

        // budgetMs 0 is unbounded — the pre-#379 behaviour, all 8 core attempts.
        // This is the proof the parameter reaches the core rather than being
        // clamped somewhere on the way.
        var unbounded = new ImmediateDelay();
        var unboundedSends = 0;
        var unboundedHandler = new RecordingHandler((_, _) =>
        {
            unboundedSends++;
            return Json("{\"error\":\"provider down\"}", HttpStatusCode.ServiceUnavailable);
        });
        using (var service = new CloudTranscriptionService(
            unboundedHandler, new StaticCredentials(), Sharing, unbounded, observer: null, retryBudgetMs: 0))
        {
            var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
                CloudTranscriptionProvider.Groq, audio, "whisper-large-v3"));
            Assert.False(result.IsSuccess);
            Assert.Equal((int)uniffi.hyperwhisper_core.HyperwhisperCoreMethods.RetryMaxAttempts(), unboundedSends);
            Assert.Equal(7, unbounded.Delays.Count);
        }

        // The clock starts BEFORE the loop and counts the failed requests' own
        // time, not just the sleeps. With a 2.1s budget and a free clock the
        // sequence would run 3 attempts (1s + 2s fit, 4s does not); a handler
        // that burns ~120ms per attempt pushes attempt 2's 2s sleep past the
        // budget, so it must stop at 2. This case fails if the Stopwatch is
        // started inside the loop, or restarted by the delay.
        var slow = new ImmediateDelay();
        var slowSends = 0;
        var slowHandler = new RecordingHandler(async (_, token) =>
        {
            slowSends++;
            await Task.Delay(TimeSpan.FromMilliseconds(120), token);
            return Json("{\"error\":\"provider down\"}", HttpStatusCode.ServiceUnavailable);
        });
        using (var service = new CloudTranscriptionService(
            slowHandler, new StaticCredentials(), Sharing, slow, observer: null, retryBudgetMs: 2_100))
        {
            var result = await service.TranscribeAsync(new CloudTranscriptionRequest(
                CloudTranscriptionProvider.Groq, audio, "whisper-large-v3"));
            Assert.False(result.IsSuccess);
            Assert.Equal(2, slowSends);
        }
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
        // `usageMetadata` is a real unmodelled frame Google interleaves; it must be
        // ignored rather than treated as an error. Multi-turn accumulation (the
        // no-prefix-diffing contract) is asserted in TestGeminiLiveSetupFrameAsync.
        new LiveCase(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret", Language: "en-US", Vocabulary: ["Codex"]),
            "generativelanguage.googleapis.com",
            [
                TextFrame("{\"setupComplete\":{}}"),
                TextFrame("{\"usageMetadata\":{\"totalTokenCount\":3}}"),
                TextFrame("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"gemini par\"}}}"),
                TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"gemini final\"}}}"),
                TextFrame("{\"serverContent\":{\"generationComplete\":true}}"),
                CloseFrame(),
            ],
            "gemini final"),
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
        // The provider ANSWERS audio; it does not pre-empt it. Frame 0 of every
        // case is the session-started frame, so it is released on connect; the
        // rest wait for the first PCM chunk. That ordering matters now that
        // Gemini holds its audio back until `setupComplete`
        // (GeminiTranscribeLiveProtocol.StartTimeout) — a socket that hands over
        // its whole script the instant the client reads would end the session
        // before the pump had run, and `AudioChunksSent` below would be 0.
        var socket = new PacedStreamingWebSocket(
            value.Frames.Select((frame, index) => new PacedFrame(frame, index > 0 ? 1 : 0)));
        var sink = new LiveSink();
        var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket), sink);
        var result = await service.TranscribeAsync(value.Config, socket.PacedAudio(1, value.AudioBytes));
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
        Audio(320, socket));
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

    // ElevenLabs is the one provider whose error frames carry a machine-readable
    // KIND and no wording of their own, and it keeps its three distinct failure
    // codes. The four providers that do send wording arrive as
    // ProviderUnavailable with the reconnect decision carried by IsTerminal, but
    // ElevenLabs cannot join them: the three sentences the core answers with are
    // the core's own prose, so classifying them would be the core grading itself,
    // and "rate limit reached" matches no terminal marker at all. So the kind
    // rides across the FFI (HwLiveEvent.Error.kind) and is mapped back — see
    // LiveTranscriptionProtocols.Event. Unauthorized is not in
    // LiveStreamingSessionController.CanReconnect's allowed set, which is what
    // actually refuses the reconnect here.
    var providerError = new FakeStreamingWebSocket(
    [
        TextFrame("{\"message_type\":\"auth_error\"}"),
        CloseFrame(),
    ]);
    var errorService = new LiveCloudTranscriptionService(new FakeWebSocketFactory(providerError));
    var error = await errorService.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "bad"),
        Audio(320, providerError));
    Assert.Equal(LiveTranscriptionFailureCode.Unauthorized, error.Failure!.Code);
    Assert.True(error.Failure.IsTerminal, "a bad ElevenLabs key must not earn a reconnect");

    // THE CODE WHOSE VERDICT ACTUALLY FLIPS. `auth_error` is rescued by
    // IsTerminal either way — its sentence carries "authentication failed",
    // which is a terminal marker — so testing only that frame cannot tell a
    // per-kind mapping apart from a collapsed one. `rate_limited` can: the
    // sentence the core answers with, "ElevenLabs rate limit reached. Please try
    // again in a moment.", matches NONE of the twenty markers, so IsTerminal is
    // false and the CODE is the whole verdict. Collapsed to ProviderUnavailable
    // it earned two reconnects at 250 ms and 500 ms straight back into the same
    // concurrent-session limit; as RateLimited it earns none, which is what this
    // head shipped before issue #281.
    var rateLimited = new FakeStreamingWebSocket(
    [
        TextFrame("{\"message_type\":\"rate_limited\"}"),
        CloseFrame(),
    ]);
    var limited = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(rateLimited))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "busy"),
            Audio(320, rateLimited));
    Assert.Equal(LiveTranscriptionFailureCode.RateLimited, limited.Failure!.Code);
    Assert.True(
        !limited.Failure.IsTerminal,
        "a rate limit clears on its own - the wording is deliberately not terminal");
    // LiveStreamingSessionController.CanReconnect, restated: it lives in
    // HyperWhisper.LiveStreaming, which this suite does not reference, and it is
    // the consumer that reads Code. Only these three earn a fresh socket.
    Assert.True(
        limited.Failure.Code is not (LiveTranscriptionFailureCode.Network
            or LiveTranscriptionFailureCode.Timeout
            or LiveTranscriptionFailureCode.ProviderUnavailable),
        "a rate-limited ElevenLabs key must not earn a reconnect into the same limit");

    // The third kind. Its sentence carries "billing" and "quota exceeded", so
    // IsTerminal would have refused the reconnect too - but the code is the one
    // this head shipped and the one the mapping must still produce.
    var quota = new FakeStreamingWebSocket(
    [
        TextFrame("{\"message_type\":\"quota_exceeded\"}"),
        CloseFrame(),
    ]);
    var exhausted = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(quota))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "spent"),
            Audio(320, quota));
    Assert.Equal(LiveTranscriptionFailureCode.QuotaExceeded, exhausted.Failure!.Code);
    Assert.True(exhausted.Failure.IsTerminal, "an exhausted ElevenLabs quota is terminal");

    // The four providers that send real wording keep the collapsed code, and
    // their reconnect decision stays with IsTerminal.
    var cloudError = new FakeStreamingWebSocket(
    [
        TextFrame("{\"type\":\"error\",\"message\":\"Credit balance exhausted\"}"),
        CloseFrame(),
    ]);
    var cloud = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(cloudError))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.HyperWhisperCloud, LicenseKey: "hw"),
            Audio(320, cloudError));
    Assert.Equal(LiveTranscriptionFailureCode.ProviderUnavailable, cloud.Failure!.Code);
    Assert.True(cloud.Failure.IsTerminal, "the flagship terminal wording");

    // A missing credential still fails before a socket is opened, and still with
    // Unauthorized: that one comes from the core's MissingCredential error, not
    // from a frame.
    var missing = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(new FakeStreamingWebSocket([])))
        .TranscribeAsync(new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs), Audio(320));
    Assert.Equal(LiveTranscriptionFailureCode.Unauthorized, missing.Failure!.Code);
    Assert.Equal(0, missing.AudioChunksSent);

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
        Audio(320, inboundOversized));
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
        Audio(320, terminalClose));
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

/// <summary>
/// TRAP 3, pinned. Gemini takes the same transcription config object at two
/// different paths depending on the model: the pre-recorded model wants
/// `setup.generation_config.transcription_config`, the LIVE model wants
/// `setup.input_audio_transcription`. Sending the pre-recorded shape to the live
/// socket closes it with code 1007, which is terminal, not retryable — so this
/// asserts the exact frame rather than "some setup was sent". The expectations
/// are copied from shared-conformance/live-frame-vectors.json.
///
/// Also pins the two contracts a reader is most likely to get wrong from the
/// docs: vocabulary is NOT gated on an explicit language (that is a Deepgram
/// constraint; Gemini accepts custom_vocabulary in auto-detect, verified live),
/// and consecutive `inputTranscription` frames are per-turn deltas that must be
/// APPENDED, never prefix-diffed the way GrokLiveProtocol diffs its
/// session-cumulative text.
/// </summary>
static async Task TestGeminiLiveSetupFrameAsync()
{
    static async Task<JsonElement> Setup(LiveTranscriptionConfig config)
    {
        var socket = new FakeStreamingWebSocket([CloseFrame()]);
        var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket));
        await service.TranscribeAsync(config, Audio(320));
        Assert.True(socket.Sent.Count > 0);
        return JsonDocument.Parse(socket.Sent[0].Data).RootElement.Clone();
    }

    var full = await Setup(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret",
        Language: "en-US", Vocabulary: ["HyperWhisper"]));
    // Asserted by PATH, not as raw text. The frame is now built by the shared
    // core (issue #281 moved live frame building into hw_net::live), and
    // serde_json emits object keys in sorted order where the hand-written C#
    // builder emitted them in declaration order. That reorders the bytes and
    // changes nothing a parser can see — Google's protobuf-JSON reader is
    // order-insensitive — so pinning the literal string would be pinning the
    // serializer, not the contract. The contract is WHERE each value sits.
    var setup = full.GetProperty("setup");
    Assert.Equal("models/gemini-3.5-transcribe-live", setup.GetProperty("model").GetString());
    var transcription = setup.GetProperty("input_audio_transcription");
    Assert.Equal("en-US", transcription.GetProperty("language_codes")[0].GetString());
    Assert.Equal("HyperWhisper", transcription.GetProperty("custom_vocabulary")[0].GetString());
    // TRAP 3: the wrong-but-plausible position must be absent, not merely
    // unused. `setup.generation_config.transcription_config` is correct for the
    // pre-recorded POST and closes the live socket with 1007.
    Assert.False(setup.TryGetProperty("generation_config", out _));

    // Region is PRESERVED. Every other live protocol here flattens "en-GB" to
    // "en"; Gemini takes the qualified form and throwing the region away would
    // silently ignore a deliberate user choice.
    var region = await Setup(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret", Language: "en-GB"));
    Assert.Equal(
        "[\"en-GB\"]",
        region.GetProperty("setup").GetProperty("input_audio_transcription")
            .GetProperty("language_codes").GetRawText());

    // Auto-detect: language_codes omitted entirely, custom_vocabulary still sent.
    var auto = await Setup(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret",
        Language: "auto", Vocabulary: ["Kalamazoo"]));
    var autoTranscription = auto.GetProperty("setup").GetProperty("input_audio_transcription");
    Assert.False(autoTranscription.TryGetProperty("language_codes", out _));
    Assert.Equal("[\"Kalamazoo\"]", autoTranscription.GetProperty("custom_vocabulary").GetRawText());

    // A bare model id is prefixed; an already-prefixed one is left alone.
    var prefixed = await Setup(new LiveTranscriptionConfig(
        LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret",
        Model: "models/gemini-3.5-transcribe-live"));
    Assert.Equal("models/gemini-3.5-transcribe-live", prefixed.GetProperty("setup").GetProperty("model").GetString());

    // Audio and stop frames, byte-for-byte against the vectors. This socket
    // deliberately never sends a close or a generationComplete, because Google
    // does not either — measured 54 s of silence after audio_stream_end. So this
    // also proves the client stops on its own DrainTimeout instead of hanging on
    // an upstream close that never comes.
    var audioSocket = new FakeStreamingWebSocket([TextFrame("{\"setupComplete\":{}}")]);
    var audioService = new LiveCloudTranscriptionService(new FakeWebSocketFactory(audioSocket));
    var started = System.Diagnostics.Stopwatch.StartNew();
    var drained = await audioService.TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret"),
        Audio(320));
    started.Stop();
    // Silence in, NoSpeech out — the point is that it RETURNS, bounded by the
    // drain budget, rather than blocking on a close Google never sends.
    Assert.Equal(LiveTranscriptionFailureCode.NoSpeech, drained.Failure!.Code);
    Assert.True(started.Elapsed < TimeSpan.FromSeconds(20));
    var frames = audioSocket.Sent.Select(frame => Encoding.UTF8.GetString(frame.Data)).ToArray();
    Assert.True(frames.Any(frame =>
        frame.Contains("\"mime_type\":\"audio/pcm;rate=16000\"", StringComparison.Ordinal) &&
        frame.Contains("\"realtime_input\"", StringComparison.Ordinal)));
    Assert.Equal("{\"realtime_input\":{\"audio_stream_end\":true}}", frames[^1]);

    // Two turns: appended, not diffed. "second turn." shares no prefix rule with
    // "first turn." — under Grok's Delta() the second final would be emitted whole
    // only by accident, so the case that actually discriminates is a turn whose
    // text repeats the previous one.
    var turns = new FakeStreamingWebSocket(
    [
        TextFrame("{\"setupComplete\":{}}"),
        TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"again.\"}}}"),
        TextFrame("{\"serverContent\":{\"interimInputTranscription\":{\"text\":\"ag\"}}}"),
        TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"again.\"}}}"),
        TextFrame("{\"serverContent\":{\"generationComplete\":true}}"),
        CloseFrame(),
    ]);
    var turnsResult = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(turns))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret"),
            Audio(320));
    Assert.True(turnsResult.IsSuccess);
    Assert.Equal("again. again.", turnsResult.Transcript);
}

/// <summary>
/// `generationComplete` is a TURN boundary, not the end of the session: Google
/// emits one at every pause in speech and then keeps transcribing.
///
/// The case above places it AFTER both turns, so it cannot tell a turn boundary
/// from a session end — which is exactly why treating any Complete as terminal
/// shipped. Here it lands BETWEEN two utterances, four chunks into a 24-chunk
/// stream, which is what a speaker pausing mid-dictation actually produces. The
/// old behaviour returned IsSuccess with only the first utterance, stopped
/// pumping audio, and never sent `audio_stream_end` — the user kept dictating
/// into a dead session and was told it worked.
///
/// The rule is the backend's (`ws-streaming-shared.ts`: a 'complete' upstream
/// event closes the session only once `stopRequested`), so this also pins that
/// the SAME signal after the stop frame IS terminal, and that the vendors whose
/// complete really does mean end-of-session keep the old behaviour.
/// </summary>
static async Task TestMidSessionTurnBoundaryAsync()
{
    var config = new LiveTranscriptionConfig(
        LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret");

    var midSession = new PacedStreamingWebSocket(
    [
        new PacedFrame(TextFrame("{\"setupComplete\":{}}")),
        new PacedFrame(TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"first utterance.\"}}}"), 2),
        // The pause. Under the shipped reading the session ends right here.
        new PacedFrame(TextFrame("{\"serverContent\":{\"generationComplete\":true}}"), 4),
        new PacedFrame(TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"second utterance.\"}}}"), 12),
        // Google's post-stop complete. Released only once `audio_stream_end` has
        // gone out, so this asserts the terminal reading without racing it.
        new PacedFrame(TextFrame("{\"serverContent\":{\"generationComplete\":true}}"), AfterStop: true),
    ]);
    var result = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(midSession))
        .TranscribeAsync(config, midSession.PacedAudio(24));

    Assert.True(result.IsSuccess);
    Assert.Equal("first utterance. second utterance.", result.Transcript);
    Assert.Equal(24, midSession.AudioFramesSent);
    Assert.Equal(24, result.AudioChunksSent);
    Assert.Equal(
        "{\"realtime_input\":{\"audio_stream_end\":true}}",
        Encoding.UTF8.GetString(midSession.Sent[^1].Data));

    // Without a post-stop complete Google simply goes quiet (measured 54 s), so
    // the session must still terminate on the drain budget with everything it
    // heard, not hang and not lose the second turn.
    var silentAfterStop = new PacedStreamingWebSocket(
    [
        new PacedFrame(TextFrame("{\"setupComplete\":{}}")),
        new PacedFrame(TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"first utterance.\"}}}"), 2),
        new PacedFrame(TextFrame("{\"serverContent\":{\"generationComplete\":true}}"), 4),
        new PacedFrame(TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"second utterance.\"}}}"), 12),
    ]);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var drained = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(silentAfterStop))
        .TranscribeAsync(config, silentAfterStop.PacedAudio(24));
    clock.Stop();
    Assert.True(drained.IsSuccess);
    Assert.Equal("first utterance. second utterance.", drained.Transcript);
    Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));

    // HyperWhisper Cloud is the other vendor whose complete reaches this code.
    // Its `session_complete` genuinely IS the end of the session — the backend
    // only forwards one once the upstream closed — so it must stay terminal even
    // before a stop, and the audio pump must stop with it. Deepgram, ElevenLabs
    // and OpenAI map nothing to Complete at all and cannot be reached by this.
    var cloudComplete = new PacedStreamingWebSocket(
    [
        new PacedFrame(TextFrame("{\"type\":\"ready\",\"sessionId\":\"s1\"}")),
        new PacedFrame(TextFrame("{\"type\":\"transcript\",\"text\":\"cloud final\",\"is_final\":true}"), 2),
        new PacedFrame(TextFrame("{\"type\":\"session_complete\"}"), 4),
    ], StopMarker: "\"type\":\"stop\"");
    var cloudResult = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(cloudComplete))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.HyperWhisperCloud, LicenseKey: "hw"),
            cloudComplete.PacedAudio(24));
    Assert.True(cloudResult.IsSuccess);
    Assert.Equal("cloud final", cloudResult.Transcript);
    Assert.True(cloudComplete.AudioFramesSent < 24);
}

/// <summary>
/// Gemini discards audio that arrives before `setupComplete`, so pumping the
/// stream the moment the socket opens loses the opening words — the requirement
/// is stated in `hw-net/src/providers/gemini_transcribe.rs` ("Audio sent before
/// this arrives must be buffered") and mirrored on the Cloud route as
/// `readyOnOpen: false`. Windows and macOS both gate on session-started; this
/// pins the .NET/Linux path to the same contract.
///
/// The gate is per-protocol, NOT global: Deepgram accepts audio from the moment
/// the socket opens, and its `Metadata` frame is not a readiness signal, so
/// gating it would stall every Deepgram dictation. That is asserted too.
/// </summary>
static async Task TestAudioPumpWaitsForStartAsync()
{
    var config = new LiveTranscriptionConfig(
        LiveTranscriptionProvider.GeminiTranscribe, ApiKey: "AIza-secret");

    // The handshake takes a real, measurable moment. If the pump does not wait,
    // all 24 chunks are gone before `setupComplete` is even delivered.
    var gated = new PacedStreamingWebSocket(
    [
        new PacedFrame(TextFrame("{\"setupComplete\":{}}"), DelayMilliseconds: 150),
        new PacedFrame(TextFrame("{\"serverContent\":{\"inputTranscription\":{\"text\":\"opening words.\"}}}"), 2),
        new PacedFrame(TextFrame("{\"serverContent\":{\"generationComplete\":true}}"), AfterStop: true),
    ]);
    var result = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(gated))
        .TranscribeAsync(config, gated.PacedAudio(24));
    Assert.True(result.IsSuccess);
    Assert.Equal("opening words.", result.Transcript);
    Assert.Equal(0, gated.AudioFramesAtFirstRelease);
    Assert.Equal(24, gated.AudioFramesSent);
    // The setup frame still goes out first — the gate is on audio, not on the
    // handshake itself, or the provider would never have anything to answer.
    Assert.True(Encoding.UTF8.GetString(gated.Sent[0].Data)
        .Contains("\"setup\"", StringComparison.Ordinal));

    // A provider that never acknowledges must fail cleanly on a bounded wait,
    // not hang the dictation, and must not spray audio at a socket that is not
    // listening.
    var never = new PacedStreamingWebSocket([]);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var timedOut = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(never))
        .TranscribeAsync(config, never.PacedAudio(24));
    clock.Stop();
    Assert.Equal(LiveTranscriptionFailureCode.Timeout, timedOut.Failure!.Code);
    Assert.Equal(0, never.AudioFramesSent);
    Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));

    // Deepgram: no readiness frame ever arrives, and every chunk must still be
    // sent. This is the regression guard on scoping the gate per protocol.
    var deepgram = new PacedStreamingWebSocket([], StopMarker: "CloseStream");
    var deepgramResult = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(deepgram))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "dg"),
            deepgram.PacedAudio(24));
    Assert.Equal(24, deepgram.AudioFramesSent);
    Assert.Equal(LiveTranscriptionFailureCode.NoSpeech, deepgramResult.Failure!.Code);
}

/// <summary>
/// The HyperWhisper Cloud live route is DERIVED from the selected cloud tier
/// (`/ws/streaming-{sttProvider}`), so a new live vendor is a catalog change.
/// The two things this guards: `deepgramNova3` and a null/garbage tier must both
/// still produce the byte-identical legacy path (installed clients send no tier
/// at all), and the Deepgram-only "no vocabulary without an explicit language"
/// rule must NOT be applied to the Gemini tier.
/// </summary>
static async Task TestCloudLiveRouteAsync()
{
    static async Task<Uri> Route(string? tier, string? language, IReadOnlyList<string>? vocabulary = null)
    {
        var socket = new FakeStreamingWebSocket([CloseFrame()]);
        var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket));
        await service.TranscribeAsync(
            new LiveTranscriptionConfig(
                LiveTranscriptionProvider.HyperWhisperCloud, LicenseKey: "hw",
                Language: language, Vocabulary: vocabulary, CloudTier: tier),
            Audio(320));
        Assert.NotNull(socket.Options);
        return socket.Options!.Uri;
    }

    Assert.Equal("/ws/streaming-deepgram", (await Route(null, "en")).AbsolutePath);
    Assert.Equal("/ws/streaming-deepgram", (await Route("deepgramNova3", "en")).AbsolutePath);
    Assert.Equal("/ws/streaming-deepgram", (await Route("  ", "en")).AbsolutePath);
    // An id the catalog does not know must not produce /ws/streaming-nonsense.
    Assert.Equal("/ws/streaming-deepgram", (await Route("notATier", "en")).AbsolutePath);
    Assert.Equal("/ws/streaming-gemini-transcribe", (await Route("geminiTranscribe", "en")).AbsolutePath);

    // Deepgram Nova-3 silently drops keyterms in auto-detect, so we withhold them.
    var deepgramAuto = await Route("deepgramNova3", "auto", ["Kalamazoo"]);
    Assert.False(deepgramAuto.Query.Contains("vocabulary=", StringComparison.Ordinal));
    Assert.False(deepgramAuto.Query.Contains("language=", StringComparison.Ordinal));

    // Gemini accepts custom_vocabulary in auto-detect, and vocabulary is the whole
    // reason to pick that tier — withholding it would delete the headline feature
    // for every auto-detect user.
    var geminiAuto = await Route("geminiTranscribe", "auto", ["Kalamazoo"]);
    Assert.True(geminiAuto.Query.Contains("vocabulary=Kalamazoo", StringComparison.Ordinal));
    Assert.False(geminiAuto.Query.Contains("language=", StringComparison.Ordinal));

    // With an explicit language both tiers send it.
    Assert.True((await Route("deepgramNova3", "en", ["Kalamazoo"])).Query
        .Contains("vocabulary=Kalamazoo", StringComparison.Ordinal));

    // The region is sent VERBATIM on this route, for every tier.
    //
    // This assertion used to say the opposite for the Deepgram tier — that
    // `en-GB` was flattened to `en`. Issue #281 (PR #320) deliberately reversed
    // that while this branch was being built, and its reasoning wins:
    // `zh-TW`, `zh-Hans`, `en-GB` and `pt-BR` are DISTINCT Deepgram codes, so
    // truncating `zh-TW` to `zh` asks Deepgram for Simplified Chinese when the
    // user chose Traditional. Windows and macOS always sent the tag whole; this
    // head was the only one that normalized, and it is the head with no users.
    // See hw_net::live::language for the per-provider table.
    Assert.True((await Route("deepgramNova3", "en-GB")).Query
        .Contains("language=en-GB", StringComparison.Ordinal));
    Assert.True((await Route("geminiTranscribe", "en-GB")).Query
        .Contains("language=en-GB", StringComparison.Ordinal));
    // "auto" still means auto-detect on both, not a literal language.
    Assert.False((await Route("geminiTranscribe", "auto")).Query
        .Contains("language=", StringComparison.Ordinal));
}

static async Task<string> ConnectQuery(LiveTranscriptionConfig config)
{
    var socket = new FakeStreamingWebSocket([CloseFrame()]);
    var service = new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket));
    await service.TranscribeAsync(config, Audio(320, socket));
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

/// <summary>
/// One chunk of PCM, then the microphone stays open until <paramref name="socket"/>
/// has handed every scripted frame to the receive loop.
///
/// That second half is not padding. The service ends the session when the AUDIO
/// ends, and since issue #281 the stop path is the protocol's own ordered step
/// list — ElevenLabs' is a bare <c>Close</c>, with no wait at all, because
/// <c>commit_strategy=vad</c> means the server has already committed everything
/// it intends to. A fake socket answers in microseconds, so a test that stopped
/// the instant the first chunk was sent would be racing the receive loop's very
/// first scheduling rather than testing anything. A real recording runs for
/// seconds with the loop already draining; this reproduces that.
///
/// <c>Pending == 0</c> is a race-free "everything before the last frame has been
/// handled": the loop dequeues frame N+1 only after it has processed frame N.
/// </summary>
static async IAsyncEnumerable<ReadOnlyMemory<byte>> Audio(int byteCount, FakeStreamingWebSocket? socket = null)
{
    await Task.CompletedTask;
    yield return new byte[byteCount];
    for (var attempt = 0; socket is { Pending: > 0 } && attempt < 1000; attempt++)
    {
        await Task.Delay(2);
    }
}

static async IAsyncEnumerable<ReadOnlyMemory<byte>> BlockingAudio(
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    yield break;
}

static async Task TestLiveTerminalErrorFrameAsync()
{
    // THE flagship case: the DEFAULT provider's credit-exhaustion frame. Before
    // issue #281 the wording was thrown away at parse time, so this reached the
    // caller as a bare ProviderUnavailable and LiveStreamingSessionController
    // retried it twice more into the same exhausted balance.
    var exhausted = await RunLiveErrorFrameAsync(
        LiveTranscriptionProvider.HyperWhisperCloud,
        "{\"type\":\"error\",\"message\":\"Credit balance exhausted\"}");
    Assert.Equal(LiveTranscriptionFailureCode.ProviderUnavailable, exhausted.Code);
    Assert.True(exhausted.IsTerminal);

    // The expensive direction: a service-side blip must keep its reconnect.
    var busy = await RunLiveErrorFrameAsync(
        LiveTranscriptionProvider.HyperWhisperCloud,
        "{\"type\":\"error\",\"message\":\"Transcription service busy, audio dropped\"}");
    Assert.Equal(LiveTranscriptionFailureCode.ProviderUnavailable, busy.Code);
    Assert.False(busy.IsTerminal);

    // OpenAI Realtime nests the wording under `error.message` instead.
    var quota = await RunLiveErrorFrameAsync(
        LiveTranscriptionProvider.OpenAi,
        "{\"type\":\"error\",\"error\":{\"message\":\"You exceeded your current quota.\"}}");
    Assert.True(quota.IsTerminal);

    // An error frame with no wording at all stays transient — unknown must
    // never mean "stop retrying".
    var wordless = await RunLiveErrorFrameAsync(
        LiveTranscriptionProvider.OpenAi,
        "{\"type\":\"error\"}");
    Assert.False(wordless.IsTerminal);
}

static async Task<LiveTranscriptionFailure> RunLiveErrorFrameAsync(
    LiveTranscriptionProvider provider,
    string errorFrame)
{
    var socket = new FakeStreamingWebSocket([TextFrame(errorFrame), CloseFrame()]);
    var config = provider == LiveTranscriptionProvider.HyperWhisperCloud
        ? new LiveTranscriptionConfig(provider, LicenseKey: "hw-secret")
        : new LiveTranscriptionConfig(provider, ApiKey: "provider-secret");
    var result = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket))
        .TranscribeAsync(config, Audio(320, socket));
    Assert.NotNull(result.Failure);
    return result.Failure!;
}

// ---------------------------------------------------------------------------
// The re-homed protocol suite (issue #281).
//
// These are the assertions the five deleted C# protocol classes used to earn
// through a socket transcript, made directly against the Rust-backed
// `RustLiveProtocol`. They are this head's proof that the shared `HwLiveSession`
// object is right, and they are the reason phase 4 can re-point Windows at it:
// the connect descriptor, the framing rule, the parsers and the ordered stop
// sequences are all pinned here, on Linux, where they actually run.
// ---------------------------------------------------------------------------

static ILiveTranscriptionProtocol Protocol(LiveTranscriptionProvider provider, string? language = null) =>
    LiveTranscriptionProtocolFactory.Create(provider switch
    {
        LiveTranscriptionProvider.HyperWhisperCloud =>
            new LiveTranscriptionConfig(provider, LicenseKey: "hw-secret", Language: language),
        _ => new LiveTranscriptionConfig(provider, ApiKey: $"{provider}-secret", Language: language),
    });

static Task TestRustLiveConnectAsync()
{
    // Deepgram: the API key travels as the SECOND subprotocol, never a header,
    // and the thirteen .NET query parameters won over macOS's ten.
    using (var deepgram = Protocol(LiveTranscriptionProvider.Deepgram, "en-GB"))
    {
        Assert.Equal(16000, deepgram.SampleRate);
        Assert.Equal("api.deepgram.com", deepgram.ConnectOptions.Uri.Host);
        Assert.Equal("/v1/listen", deepgram.ConnectOptions.Uri.AbsolutePath);
        Assert.Equal(0, deepgram.ConnectOptions.Headers.Count);
        Assert.Equal("token|Deepgram-secret", string.Join('|', deepgram.ConnectOptions.SubProtocols));
        Assert.Equal(0, deepgram.StartFrames.Count);
        var query = deepgram.ConnectOptions.Uri.Query;
        // The model alias resolves; a bare `model=` can no longer be emitted.
        Assert.True(query.Contains("model=nova-3-general&", StringComparison.Ordinal), query);
        foreach (var expected in new[]
                 {
                     "encoding=linear16", "sample_rate=16000", "channels=1", "smart_format=true",
                     "punctuate=true", "filler_words=true", "no_delay=false", "endpointing=300",
                     "utterance_end_ms=1500", "interim_results=true", "vad_events=true",
                     "mip_opt_out=true", "language=en",
                 })
        {
            Assert.True(query.Contains(expected, StringComparison.Ordinal), $"missing {expected}");
        }
    }

    // Auto-detect is spelled with a parameter, not by omitting one.
    using (var auto = Protocol(LiveTranscriptionProvider.Deepgram))
    {
        Assert.True(auto.ConnectOptions.Uri.Query.Contains("detect_language=true", StringComparison.Ordinal));
        Assert.False(auto.ConnectOptions.Uri.Query.Contains("&language=", StringComparison.Ordinal));
        Assert.False(auto.ConnectOptions.Uri.Query.Contains("keyterm=", StringComparison.Ordinal));
    }

    using (var elevenLabs = Protocol(LiveTranscriptionProvider.ElevenLabs, "EN-US"))
    {
        Assert.Equal(16000, elevenLabs.SampleRate);
        Assert.Equal("api.elevenlabs.io", elevenLabs.ConnectOptions.Uri.Host);
        Assert.Equal("ElevenLabs-secret", elevenLabs.ConnectOptions.Headers["xi-api-key"]);
        Assert.True(elevenLabs.ConnectOptions.Uri.Query.Contains("language_code=en", StringComparison.Ordinal));
        Assert.True(elevenLabs.ConnectOptions.Uri.Query.Contains("commit_strategy=vad", StringComparison.Ordinal));
    }

    using (var openAi = Protocol(LiveTranscriptionProvider.OpenAi, "en"))
    {
        Assert.Equal(24000, openAi.SampleRate);
        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", openAi.ConnectOptions.Uri.ToString());
        Assert.Equal("Bearer OpenAi-secret", openAi.ConnectOptions.Headers["Authorization"]);
        Assert.Equal(1, openAi.StartFrames.Count);
        // The session update byte for byte, `turn_detection: null` included —
        // that null is what disables server-side VAD and makes the commit gate
        // this client's to get right.
        Assert.Equal(
            "{\"type\":\"session.update\",\"session\":{\"type\":\"transcription\",\"audio\":{\"input\":"
            + "{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000},\"transcription\":"
            + "{\"model\":\"gpt-realtime-whisper\",\"language\":\"en\"},\"turn_detection\":null}}}}",
            Encoding.UTF8.GetString(openAi.StartFrames[0].Data));
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Text, openAi.StartFrames[0].Type);
    }

    using (var grok = Protocol(LiveTranscriptionProvider.Grok, "en"))
    {
        Assert.Equal(16000, grok.SampleRate);
        Assert.Equal("api.x.ai", grok.ConnectOptions.Uri.Host);
        Assert.Equal("Bearer Grok-secret", grok.ConnectOptions.Headers["Authorization"]);
    }

    using (var cloud = Protocol(LiveTranscriptionProvider.HyperWhisperCloud, "en"))
    {
        Assert.Equal(16000, cloud.SampleRate);
        Assert.Equal(
            "wss://transcribe-prod-v2.hyperwhisper.com/ws/streaming-deepgram?license_key=hw-secret&language=en",
            cloud.ConnectOptions.Uri.ToString());
        // The client-identity headers stay the platform's to add.
        Assert.Equal(0, cloud.ConnectOptions.Headers.Count);
    }

    // A credential the core cannot use fails before a socket exists, for every
    // provider — including HyperWhisper Cloud, which accepts either of two.
    foreach (var provider in new[]
             {
                 LiveTranscriptionProvider.Deepgram, LiveTranscriptionProvider.ElevenLabs,
                 LiveTranscriptionProvider.OpenAi, LiveTranscriptionProvider.Grok,
                 LiveTranscriptionProvider.HyperWhisperCloud,
             })
    {
        Assert.Throws<LiveProtocolException>(
            () => LiveTranscriptionProtocolFactory.Create(new LiveTranscriptionConfig(provider, ApiKey: "  ")));
    }
    using (var trial = LiveTranscriptionProtocolFactory.Create(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.HyperWhisperCloud, DeviceId: "device-1")))
    {
        Assert.True(trial.ConnectOptions.Uri.Query.Contains("device_id=device-1", StringComparison.Ordinal));
    }
    return Task.CompletedTask;
}

static Task TestRustLiveFramingAsync()
{
    // AUDIO NEVER CROSSES THE FFI. The core answers a framing rule once; the
    // base64 and the concatenation happen here. `+` is in the base64 alphabet
    // and is emitted literally, where System.Text.Json used to escape it.
    var pcm = new byte[] { 0xFB, 0xEF, 0xBE, 0x01, 0x02, 0x03 };
    var base64 = Convert.ToBase64String(pcm);
    Assert.True(base64.Contains('+'), "the fixture must exercise the plus sign");

    foreach (var provider in new[]
             {
                 LiveTranscriptionProvider.Deepgram, LiveTranscriptionProvider.Grok,
                 LiveTranscriptionProvider.HyperWhisperCloud,
             })
    {
        using var binary = Protocol(provider);
        var frame = binary.EncodeAudio(pcm);
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Binary, frame.Type);
        Assert.True(frame.Data.SequenceEqual(pcm), $"{provider} must send PCM untouched");
    }

    using (var elevenLabs = Protocol(LiveTranscriptionProvider.ElevenLabs))
    {
        var frame = elevenLabs.EncodeAudio(pcm);
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Text, frame.Type);
        Assert.Equal(
            "{\"message_type\":\"input_audio_chunk\",\"audio_base_64\":\"" + base64
            + "\",\"commit\":false,\"sample_rate\":16000}",
            Encoding.UTF8.GetString(frame.Data));
    }

    using (var openAi = Protocol(LiveTranscriptionProvider.OpenAi))
    {
        Assert.Equal(
            "{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + base64 + "\"}",
            Encoding.UTF8.GetString(openAi.EncodeAudio(pcm).Data));
    }
    return Task.CompletedTask;
}

static Task TestRustLiveParseAsync()
{
    using (var deepgram = Protocol(LiveTranscriptionProvider.Deepgram))
    {
        // `Metadata` is the session acknowledgement and `request_id` is what
        // Deepgram support asks for; it is now carried instead of discarded.
        var started = deepgram.Parse(Utf8("{\"type\":\"Metadata\",\"request_id\":\"r1\"}"));
        Assert.Equal(LiveProtocolEventKind.Started, started.Kind);
        Assert.Equal("r1", started.SessionId);
        Assert.Equal(
            LiveProtocolEventKind.Partial,
            deepgram.Parse(Utf8("{\"type\":\"Results\",\"is_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"p\"}]}}")).Kind);
        Assert.Equal(
            LiveProtocolEventKind.Final,
            deepgram.Parse(Utf8("{\"type\":\"Results\",\"is_final\":true,\"channel\":{\"alternatives\":[{\"transcript\":\"f\"}]}}")).Kind);
        // `channel` is an ARRAY on the voice-activity frames. A strict decode of
        // the object shape throws on these and takes the session with it.
        Assert.Equal(
            LiveProtocolEventKind.Metadata,
            deepgram.Parse(Utf8("{\"type\":\"UtteranceEnd\",\"channel\":[0,1]}")).Kind);
        Assert.Equal(
            LiveProtocolEventKind.Metadata,
            deepgram.Parse(Utf8("{\"type\":\"SpeechStarted\",\"channel\":[0,1]}")).Kind);
        // Not JSON at all. This head used to end the session on a Protocol
        // failure; a provider adding a frame shape must not stop a recording.
        Assert.Equal(LiveProtocolEventKind.Ignore, deepgram.Parse(Utf8("<html>502</html>")).Kind);
        Assert.Equal(LiveProtocolEventKind.Ignore, deepgram.Parse(Utf8("[1,2,3]")).Kind);
    }

    using (var elevenLabs = Protocol(LiveTranscriptionProvider.ElevenLabs))
    {
        Assert.Equal(LiveProtocolEventKind.Started, elevenLabs.Parse(Utf8("{\"message_type\":\"session_started\"}")).Kind);
        // The one provider whose error frames carry no wording of their own: the
        // core supplies the sentence, and the classifier reads it.
        var auth = elevenLabs.Parse(Utf8("{\"message_type\":\"auth_error\"}"));
        Assert.Equal(LiveProtocolEventKind.Error, auth.Kind);
        Assert.Equal(
            PortableLiveErrorOutcome.Terminal,
            SharedCoreBridge.ClassifyLiveErrorMessage(auth.Text!));
        Assert.Equal(
            PortableLiveErrorOutcome.Terminal,
            SharedCoreBridge.ClassifyLiveErrorMessage(
                elevenLabs.Parse(Utf8("{\"message_type\":\"quota_exceeded\"}")).Text!));
        // The rate-limit asymmetry is deliberate: it clears on its own.
        Assert.Equal(
            PortableLiveErrorOutcome.Transient,
            SharedCoreBridge.ClassifyLiveErrorMessage(
                elevenLabs.Parse(Utf8("{\"message_type\":\"rate_limited\"}")).Text!));
    }

    using (var openAi = Protocol(LiveTranscriptionProvider.OpenAi))
    {
        // Deltas accumulate per item_id — a partial is the whole interim
        // utterance, not the fragment that just arrived.
        Assert.Equal(
            "one",
            openAi.Parse(Utf8("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i1\",\"delta\":\"one\"}")).Text);
        Assert.Equal(
            "one two",
            openAi.Parse(Utf8("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i1\",\"delta\":\" two\"}")).Text);
        // A completion for the same item emits only what is new.
        Assert.Equal(
            "one two",
            openAi.Parse(Utf8("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"one two\"}")).Text);
        Assert.Equal(
            "three",
            openAi.Parse(Utf8("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"one two three\"}")).Text);
        Assert.Equal("s1", openAi.Parse(Utf8("{\"type\":\"session.updated\",\"session\":{\"id\":\"s1\"}}")).SessionId);
        // OpenAI Realtime nests the wording under error.message.
        Assert.Equal(
            "You exceeded your current quota.",
            openAi.Parse(Utf8("{\"type\":\"error\",\"error\":{\"message\":\"You exceeded your current quota.\"}}")).Text);
    }

    using (var grok = Protocol(LiveTranscriptionProvider.Grok))
    {
        Assert.Equal(LiveProtocolEventKind.Started, grok.Parse(Utf8("{\"type\":\"transcript.created\"}")).Kind);
        Assert.Equal(
            "hello",
            grok.Parse(Utf8("{\"type\":\"transcript.partial\",\"text\":\"hello\",\"is_final\":true}")).Text);
        // xAI re-sends the whole transcript, so a final is the delta.
        Assert.Equal(
            "world",
            grok.Parse(Utf8("{\"type\":\"transcript.partial\",\"text\":\"hello world\",\"is_final\":true}")).Text);
        // `transcript.done` is BOTH the last words and the end of the session.
        // Splitting it would drop the tail.
        var done = grok.Parse(Utf8("{\"type\":\"transcript.done\",\"text\":\"hello world again\",\"duration\":4.5}"));
        Assert.Equal(LiveProtocolEventKind.Complete, done.Kind);
        Assert.Equal("again", done.Text);
        Assert.Equal(4.5, done.DurationSeconds);
        Assert.Equal(0d, done.CreditsUsed);
    }

    using (var cloud = Protocol(LiveTranscriptionProvider.HyperWhisperCloud))
    {
        Assert.Equal("s1", cloud.Parse(Utf8("{\"type\":\"ready\",\"sessionId\":\"s1\"}")).SessionId);
        // BILLING DATA. It arrives once, after the stop frame, which is the
        // whole reason the stop path waits on this event.
        var complete = cloud.Parse(Utf8("{\"type\":\"session_complete\",\"duration_seconds\":12.5,\"credits_used\":3.25}"));
        Assert.Equal(LiveProtocolEventKind.Complete, complete.Kind);
        Assert.Equal(12.5, complete.DurationSeconds);
        Assert.Equal(3.25, complete.CreditsUsed);
        // The only provider that warns, and a warning is not a failure.
        var warning = cloud.Parse(Utf8("{\"type\":\"warning\",\"message\":\"Low balance\"}"));
        Assert.Equal(LiveProtocolEventKind.Warning, warning.Kind);
        Assert.Equal("Low balance", warning.Text);
        Assert.Equal(
            "Credit balance exhausted",
            cloud.Parse(Utf8("{\"type\":\"error\",\"message\":\"Credit balance exhausted\"}")).Text);
    }
    return Task.CompletedTask;
}

static Task TestRustLiveStopSequenceAsync()
{
    // Deepgram: Finalize -> WAIT 500 ms -> CloseStream -> Close. This head used
    // to send both frames back to back and then drain 2 s, which lets the close
    // be processed before the flush and loses the finalized tail.
    using (var deepgram = Protocol(LiveTranscriptionProvider.Deepgram))
    {
        Assert.Equal(
            "SendMessage:{\"type\":\"Finalize\"}|Wait:500|SendMessage:{\"type\":\"CloseStream\"}|Close",
            Rendered(deepgram.StopSequence(0)));
    }

    // ElevenLabs: close, and nothing else. `commit_strategy=vad` means the
    // server has already committed everything; the old 1 s drain waited for a
    // frame that was never coming.
    using (var elevenLabs = Protocol(LiveTranscriptionProvider.ElevenLabs))
    {
        Assert.Equal("Close", Rendered(elevenLabs.StopSequence(0)));
    }

    // xAI and HyperWhisper Cloud wait on the session-complete EVENT — the one
    // that carries credits_used — not on a duration.
    using (var grok = Protocol(LiveTranscriptionProvider.Grok))
    {
        Assert.Equal(
            "SendMessage:{\"type\":\"audio.done\"}|WaitForSessionComplete:10000|Close",
            Rendered(grok.StopSequence(0)));
    }
    using (var cloud = Protocol(LiveTranscriptionProvider.HyperWhisperCloud))
    {
        Assert.Equal(
            "SendMessage:{\"type\":\"stop\"}|WaitForSessionComplete:10000|Close",
            Rendered(cloud.StopSequence(0)));
    }

    // Every sequence ends with exactly one Close, and nothing follows it.
    foreach (var provider in new[]
             {
                 LiveTranscriptionProvider.Deepgram, LiveTranscriptionProvider.ElevenLabs,
                 LiveTranscriptionProvider.OpenAi, LiveTranscriptionProvider.Grok,
                 LiveTranscriptionProvider.HyperWhisperCloud,
             })
    {
        using var protocol = Protocol(provider);
        var steps = protocol.StopSequence(0);
        Assert.Equal(1, steps.Count(step => step.Action == LiveStopAction.Close));
        Assert.Equal(LiveStopAction.Close, steps[^1].Action);
    }
    return Task.CompletedTask;
}

static Task TestRustLiveCommitGateAsync()
{
    // The server's rule EXACTLY: 100 ms x 24 kHz x 2 bytes = 4800, no margin.
    // Windows captures in 100 ms buffers, so every chunk lands on the line.
    using (var short_ = Protocol(LiveTranscriptionProvider.OpenAi))
    {
        short_.EncodeAudio(new byte[4799]);
        Assert.Equal("Wait:1000|Close", Rendered(short_.StopSequence(5_000)));
    }
    using (var exact = Protocol(LiveTranscriptionProvider.OpenAi))
    {
        exact.EncodeAudio(new byte[4800]);
        Assert.Equal(
            "SendMessage:{\"type\":\"input_audio_buffer.commit\"}|Wait:1000|Close",
            Rendered(exact.StopSequence(5_000)));
    }

    // The periodic commit needs BOTH gates, and now_ms is a parameter — the
    // 1.2 s interval is exercised without sleeping.
    using (var periodic = Protocol(LiveTranscriptionProvider.OpenAi))
    {
        // The first opportunity only seeds the clock mark.
        Assert.Equal(0, periodic.AudioOpportunityFrames(0).Count);
        periodic.EncodeAudio(new byte[4800]);
        Assert.Equal(0, periodic.AudioOpportunityFrames(1_199).Count);
        var commit = periodic.AudioOpportunityFrames(1_200);
        Assert.Equal(1, commit.Count);
        Assert.Equal("{\"type\":\"input_audio_buffer.commit\"}", Encoding.UTF8.GetString(commit[0].Data));
        // The bytes were claimed, so the stop path cannot commit them twice.
        Assert.Equal("Wait:1000|Close", Rendered(periodic.StopSequence(2_000)));
    }

    // Deepgram's keepalive is the same shape: silence measured off the caller's
    // clock, no keepalive until 3 s have actually passed.
    using (var deepgram = Protocol(LiveTranscriptionProvider.Deepgram))
    {
        Assert.Equal(0, deepgram.AudioOpportunityFrames(0).Count);
        Assert.Equal(0, deepgram.AudioOpportunityFrames(3_000).Count);
        var keepAlive = deepgram.AudioOpportunityFrames(6_001);
        Assert.Equal(1, keepAlive.Count);
        Assert.Equal("{\"type\":\"KeepAlive\"}", Encoding.UTF8.GetString(keepAlive[0].Data));
    }

    // The four providers with no control frames answer an empty list, always.
    foreach (var provider in new[]
             {
                 LiveTranscriptionProvider.ElevenLabs, LiveTranscriptionProvider.Grok,
                 LiveTranscriptionProvider.HyperWhisperCloud,
             })
    {
        using var protocol = Protocol(provider);
        protocol.EncodeAudio(new byte[9600]);
        Assert.Equal(0, protocol.AudioOpportunityFrames(60_000).Count);
    }
    return Task.CompletedTask;
}

/// <summary>
/// The step list is only worth having if the service runs it in order. Deepgram
/// is the case that proves it: the 500 ms gap has to be BETWEEN the two frames,
/// not before them or after them.
/// </summary>
static async Task TestLiveStopSequenceOnTheWireAsync()
{
    var socket = new FakeStreamingWebSocket(
    [
        TextFrame("{\"type\":\"Results\",\"is_final\":true,\"channel\":{\"alternatives\":[{\"transcript\":\"deep final\"}]}}"),
    ]);
    var result = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(socket)).TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.Deepgram, ApiKey: "dg"),
        Audio(320, socket));
    Assert.Equal("deep final", result.Transcript);

    var text = socket.Sent
        .Where(value => value.Type == System.Net.WebSockets.WebSocketMessageType.Text)
        .ToList();
    Assert.Equal(2, text.Count);
    Assert.Equal("{\"type\":\"Finalize\"}", Encoding.UTF8.GetString(text[0].Data));
    Assert.Equal("{\"type\":\"CloseStream\"}", Encoding.UTF8.GetString(text[1].Data));
    Assert.True(
        text[1].AtMs - text[0].AtMs >= 400,
        $"CloseStream followed Finalize after only {text[1].AtMs - text[0].AtMs} ms");
    Assert.Equal(1, socket.Closes);

    // HyperWhisper Cloud — the DEFAULT provider — waits on the session-complete
    // EVENT for up to ten seconds, because that frame carries credits_used and
    // does not arrive until after `stop`. This socket reproduces that ordering:
    // it withholds the completion until the stop frame is actually on the wire.
    // A flat drain would either close before it (losing billing data) or add ten
    // seconds to every stop; the event wait does neither.
    var cloudSocket = new FakeStreamingWebSocket(
        [TextFrame("{\"type\":\"session_complete\",\"duration_seconds\":1.5,\"credits_used\":0.25}")],
        release: value => value.Sent.Any(sent =>
            sent.Type == System.Net.WebSockets.WebSocketMessageType.Text
            && Encoding.UTF8.GetString(sent.Data) == "{\"type\":\"stop\"}"));
    var started = System.Diagnostics.Stopwatch.StartNew();
    var cloud = await new LiveCloudTranscriptionService(new FakeWebSocketFactory(cloudSocket)).TranscribeAsync(
        new LiveTranscriptionConfig(LiveTranscriptionProvider.HyperWhisperCloud, LicenseKey: "hw"),
        Audio(320));
    started.Stop();
    // No transcript frame was scripted, so the session ends with no speech —
    // what is under test is the stop ordering, not the text.
    Assert.Equal(LiveTranscriptionFailureCode.NoSpeech, cloud.Failure!.Code);
    Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"the stop path took {started.Elapsed}");
    Assert.Equal("{\"type\":\"stop\"}", Encoding.UTF8.GetString(cloudSocket.Sent[^1].Data));
    Assert.Equal(1, cloudSocket.Closes);
}

/// <summary>
/// The phase seam against Windows (see
/// <c>LiveCloudTranscriptionService.DrainAfterCloseAsync</c>). ElevenLabs' stop
/// sequence is a bare <c>Close</c>, so once the ordered stop path arrived this
/// head had a zero-millisecond budget for anything still in flight, while
/// Windows kept draining through its blocking close handshake.
///
/// One sentence with <c>commit_strategy=vad</c> is exactly that case: the
/// <c>committed_transcript</c> lands shortly AFTER the last audio chunk, and
/// without the post-close drain it came back as <c>NoSpeech</c>.
///
/// 300 ms is deliberately far from both ends — well outside zero, so the
/// pre-fix behaviour cannot pass by luck, and well inside the 2 s
/// <c>CloseTimeout</c> budget, so a loaded CI box cannot fail it.
/// </summary>
static async Task TestLiveLateFinalTranscriptAsync()
{
    var socket = new LateFrameStreamingWebSocket(
        [TextFrame("{\"message_type\":\"committed_transcript\",\"text\":\"hello\"}"), CloseFrame()],
        TimeSpan.FromMilliseconds(300));
    var result = await new LiveCloudTranscriptionService(new DirectWebSocketFactory(socket))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "eleven"),
            LateAudio(320, socket));
    Assert.True(result.Failure is null, $"expected a transcript, got {result.Failure?.Code}");
    Assert.Equal("hello", result.Transcript);
    Assert.Equal(1, socket.Closes);
}

/// <summary>
/// The verdict the receive loop recorded must outlive the unwind.
///
/// HyperWhisper Cloud — the default provider — sends
/// <c>{"type":"error","message":"Credit balance exhausted"}</c>. The loop marks
/// it <c>ProviderUnavailable, IsTerminal: true</c> and returns, the audio loop
/// breaks, the stop sequence is skipped, and the bounded close then throws on
/// the dead socket. That catch arm used to build a fresh
/// <c>Network</c> / <c>IsTerminal: false</c> failure over the top, which
/// <c>LiveStreamingSessionController.CanReconnect</c> reads as permission for two
/// more doomed reconnects into the same exhausted balance.
///
/// The throwing close is why this needs its own fake: the shared
/// <c>FakeStreamingWebSocket</c> closes cleanly, and teaching it to throw would
/// change every test that already uses it.
/// </summary>
static async Task TestLiveRecordedFailureSurvivesCloseAsync()
{
    var socket = new LateFrameStreamingWebSocket(
        [TextFrame("{\"type\":\"error\",\"message\":\"Credit balance exhausted\"}")],
        TimeSpan.Zero,
        throwOnClose: true);
    var result = await new LiveCloudTranscriptionService(new DirectWebSocketFactory(socket))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.HyperWhisperCloud, LicenseKey: "hw"),
            LateAudio(320, socket, awaitFirstFrame: true));
    Assert.NotNull(result.Failure);
    Assert.Equal(LiveTranscriptionFailureCode.ProviderUnavailable, result.Failure!.Code);
    Assert.True(result.Failure.IsTerminal, "the recorded terminal verdict was overwritten by the close failure");
}

/// <summary>
/// The other half of the post-close drain: it may ADD a transcript and may never
/// ADD a failure.
///
/// The drain holds the receive loop open through this head's own
/// <c>CloseOutputAsync</c>, so for the first time frames the peer sends in ANSWER
/// to that half-close are read and classified. Every one of them lands on
/// <c>state.Failure</c>, and the session's exit preferred a failure over a
/// transcript unconditionally — so a session that already had "hello" came back
/// as a bare failure with the text destroyed. Measured against a real
/// <c>ClientWebSocket</c> and a real RFC 6455 server: a peer that drops the TCP
/// connection instead of echoing the close returned
/// <c>Transcript=null, Failure=Network</c>, and a 1011 close echo returned
/// <c>Protocol</c>, where the same server against a service with no drain
/// returned <c>"hello"</c>.
///
/// The close echo is the case pinned here, because it is deterministic: 1008
/// (<c>PolicyViolation</c>) is a terminal close code, so the receive loop records
/// <c>Protocol</c> — the exact shape that used to win.
/// </summary>
static async Task TestLivePostCloseFailureCannotDestroyTranscriptAsync()
{
    var socket = new LateFrameStreamingWebSocket(
        [
            TextFrame("{\"message_type\":\"committed_transcript\",\"text\":\"hello\"}"),
            new StreamingWebSocketFrame(
                [],
                System.Net.WebSockets.WebSocketMessageType.Close,
                CloseStatus: System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation),
        ],
        TimeSpan.FromMilliseconds(300));
    var result = await new LiveCloudTranscriptionService(new DirectWebSocketFactory(socket))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "eleven"),
            LateAudio(320, socket));
    Assert.True(
        result.Failure is null,
        $"a verdict reached after our own close destroyed a finished transcript: {result.Failure?.Code}");
    Assert.Equal("hello", result.Transcript);

    // The rule is one-directional. A verdict recorded BEFORE the close still
    // outranks everything, which is what TestLiveRecordedFailureSurvivesCloseAsync
    // pins from the other side.
}

/// <summary>
/// A cancel that lands inside the post-close drain must abandon the session, not
/// finalize it.
///
/// The drain waits on a token linked over the CALLER's token as well as
/// <c>CloseTimeout</c>, and <see cref="Task.WhenAny(Task[])"/> returns normally
/// when a linked token fires rather than throwing. Nothing downstream tested the
/// token again, so a user who hit cancel 250 ms into the drain got
/// <c>Transcript="hello", IsSuccess=true</c> — measured against a real
/// <c>ClientWebSocket</c> — and the in-flight stop then injected that text while
/// the cancel path was deleting the history row and reporting "Recording
/// cancelled".
///
/// The cancel is armed BY the close rather than by a wall clock, so the drain is
/// provably the thing running when it fires.
/// </summary>
static async Task TestLiveCancelDuringDrainAsync()
{
    using var cancellation = new CancellationTokenSource();
    var socket = new LateFrameStreamingWebSocket(
        [TextFrame("{\"message_type\":\"committed_transcript\",\"text\":\"hello\"}")],
        TimeSpan.Zero,
        onClose: () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(100)));
    var result = await new LiveCloudTranscriptionService(new DirectWebSocketFactory(socket))
        .TranscribeAsync(
            new LiveTranscriptionConfig(LiveTranscriptionProvider.ElevenLabs, ApiKey: "eleven"),
            LateAudio(320, socket, awaitFirstFrame: true),
            cancellation.Token);
    Assert.True(
        result.Failure is not null,
        $"the cancel was swallowed by the drain and the session finalized \"{result.Transcript}\"");
    Assert.Equal(LiveTranscriptionFailureCode.Cancelled, result.Failure!.Code);
    Assert.True(
        result.Transcript is null,
        "a cancelled session must not hand back text the caller's cancel path is deleting");
}

/// <summary>
/// One chunk, then a resume that means "the last audio byte is on the wire" —
/// the socket withholds its script until that point, so the delay under test is
/// measured from the stop and not from a scheduling accident.
///
/// <paramref name="awaitFirstFrame"/> must stay FALSE for the late-transcript
/// test. Waiting here would let the frame land while the audio loop is still
/// running, which is a path that never needed the drain at all — the test would
/// then pass without the fix and prove nothing. The failure test sets it, because
/// its point is the opposite: the error frame has to be handled BEFORE the audio
/// loop ends, so the session takes the skip-the-stop-sequence path.
/// </summary>
static async IAsyncEnumerable<ReadOnlyMemory<byte>> LateAudio(
    int byteCount,
    LateFrameStreamingWebSocket socket,
    bool awaitFirstFrame = false)
{
    await Task.CompletedTask;
    yield return new byte[byteCount];
    socket.MarkAudioDone();
    for (var attempt = 0; awaitFirstFrame && socket.Served == 0 && attempt < 1000; attempt++)
    {
        await Task.Delay(2);
    }
}

static Task TestRustLiveDisposalAsync()
{
    // The generated HwLiveSession is a handle onto a Rust Arc. Disposal is not
    // optional, and it has to be idempotent — the service disposes through a
    // `using`, and the test suite above disposes the same objects by hand.
    var protocol = Protocol(LiveTranscriptionProvider.Deepgram);
    protocol.Dispose();
    protocol.Dispose();
    return Task.CompletedTask;
}

static string Rendered(IReadOnlyList<LiveStopStep> steps) =>
    string.Join('|', steps.Select(step => step.Action switch
    {
        LiveStopAction.SendMessage => $"SendMessage:{Encoding.UTF8.GetString(step.Payload!)}",
        LiveStopAction.Wait => $"Wait:{(int)(step.WaitAfter ?? TimeSpan.Zero).TotalMilliseconds}",
        LiveStopAction.WaitForSessionComplete =>
            $"WaitForSessionComplete:{(int)(step.WaitAfter ?? TimeSpan.Zero).TotalMilliseconds}",
        _ => "Close",
    }));

static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

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
    public byte[]? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            LastRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        LastBodyLength = LastBody?.Length ?? 0;
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

sealed class FakeWebSocketFactory(IStreamingWebSocket socket) : IStreamingWebSocketFactory
{
    public IStreamingWebSocket Create() => socket;
}

sealed class FakeStreamingWebSocket(
    IEnumerable<StreamingWebSocketFrame> frames,
    Func<FakeStreamingWebSocket, bool>? release = null) : IStreamingWebSocket
{
    private readonly Queue<StreamingWebSocketFrame> _frames = new(frames);

    /// <summary>Scripted frames not yet handed to the receive loop.</summary>
    public int Pending => _frames.Count;

    /// <summary>
    /// Started at construction so a send carries WHEN it happened. The ordered
    /// stop sequence is the reason: Deepgram's 500 ms gap has to sit between two
    /// frames, and a transcript with no clock on it cannot show that.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public StreamingWebSocketConnectOptions? Options { get; private set; }
    public List<(byte[] Data, System.Net.WebSockets.WebSocketMessageType Type, long AtMs)> Sent { get; } = [];
    public int Closes { get; private set; }

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
        Sent.Add((data.ToArray(), messageType, _clock.ElapsedMilliseconds));
        return Task.CompletedTask;
    }

    public async Task<StreamingWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_frames.Count > 0)
        {
            // A provider does not answer before it is asked. `release` withholds
            // the scripted frames until the client has put something specific on
            // the wire, which is how a stop-then-complete exchange is reproduced
            // without a sleep.
            while (release is not null && !release(this))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(2, cancellationToken);
            }
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
        Closes++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// One scripted inbound frame plus the condition that releases it.
/// </summary>
/// <param name="Frame">The frame to deliver.</param>
/// <param name="AfterAudioFrames">Hold it until the client has SENT this many of
/// <see cref="PacedStreamingWebSocket.PacedAudio"/>'s PCM chunks, so "mid-session"
/// is a fact rather than a race. Counted at the source rather than by sniffing the
/// wire, because the six protocols encode audio six different ways — but only once
/// the pump has come back for the next chunk, which is what makes it "sent".</param>
/// <param name="AfterStop">Hold it until the client's stop frame has gone out.</param>
/// <param name="DelayMilliseconds">Make the provider take a measurable moment to
/// answer, so a client that does not wait is caught deterministically.</param>
sealed record PacedFrame(
    StreamingWebSocketFrame Frame,
    int AfterAudioFrames = 0,
    bool AfterStop = false,
    int DelayMilliseconds = 0);

/// <summary>
/// A fake socket that releases each scripted frame only when the client has
/// reached a given point in the session. <see cref="FakeStreamingWebSocket"/>
/// hands over everything it holds as fast as the client reads, which is why the
/// suite could only ever place a frame before or after the whole audio stream.
/// </summary>
sealed class PacedStreamingWebSocket(
    IEnumerable<PacedFrame> script,
    string StopMarker = "audio_stream_end") : IStreamingWebSocket
{
    private readonly Queue<PacedFrame> _script = new(script);
    private readonly object _gate = new();
    private int _audioFrames;
    private int _audioYielded;
    private int _receiveEntries;
    private int _dequeued;
    private volatile bool _stopSent;

    public StreamingWebSocketConnectOptions? Options { get; private set; }
    public List<(byte[] Data, System.Net.WebSockets.WebSocketMessageType Type)> Sent { get; } = [];
    public int AudioFramesSent => Volatile.Read(ref _audioFrames);

    /// <summary>
    /// How many audio frames the client had already sent when the FIRST inbound
    /// frame was delivered. Zero proves the pump waited for the handshake.
    /// </summary>
    public int AudioFramesAtFirstRelease { get; private set; } = -1;

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
        var bytes = data.ToArray();
        Sent.Add((bytes, messageType));
        var text = messageType == System.Net.WebSockets.WebSocketMessageType.Text
            ? Encoding.UTF8.GetString(bytes)
            : null;
        if (text is not null && text.Contains(StopMarker, StringComparison.Ordinal))
        {
            _stopSent = true;
        }
        else if (text is null || text.Contains("\"mime_type\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _audioFrames);
        }
        return Task.CompletedTask;
    }

    public async Task<StreamingWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _receiveEntries);
        // Never complete synchronously: the receive loop would otherwise run the
        // whole script to its end before the audio pump has been scheduled once.
        await Task.Yield();
        while (Next() is { } next)
        {
            if (Volatile.Read(ref _audioYielded) < next.AfterAudioFrames || (next.AfterStop && !_stopSent))
            {
                await Task.Delay(1, cancellationToken);
                continue;
            }
            if (next.DelayMilliseconds > 0)
            {
                await Task.Delay(next.DelayMilliseconds, cancellationToken);
            }
            if (AudioFramesAtFirstRelease < 0)
            {
                AudioFramesAtFirstRelease = AudioFramesSent;
            }
            lock (_gate)
            {
                _dequeued++;
                return _script.Dequeue().Frame;
            }
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }

    private PacedFrame? Next()
    {
        lock (_gate) return _script.Count > 0 ? _script.Peek() : null;
    }

    /// <summary>
    /// A PCM stream in lock step with the script: the next chunk is held back
    /// until every frame due at the chunks already sent has been delivered AND
    /// the client has come back for more (which means it finished handling it).
    ///
    /// Without that the pump — 24 chunks of `await Task.Yield()` — finishes in
    /// microseconds and sends its stop frame before the receive loop has read the
    /// second frame, so "the provider said this MID-session" silently becomes
    /// "after the client stopped" and the test proves nothing.
    ///
    /// The wait is bounded: once the receive loop has ended (a terminal frame) it
    /// never comes back for more, and a test that stops mid-stream would
    /// otherwise deadlock here rather than fail.
    /// </summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> PacedAudio(int count, int chunkBytes = 320)
    {
        for (var index = 0; index < count; index++)
        {
            var deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline)
            {
                var next = Next();
                var pendingDue = next is { AfterStop: false }
                    && next.AfterAudioFrames <= Volatile.Read(ref _audioYielded);
                var handled = Volatile.Read(ref _receiveEntries) > Volatile.Read(ref _dequeued);
                if (!pendingDue && handled) break;
                await Task.Delay(1);
            }
            yield return new byte[chunkBytes];
            // Count the chunk only once the pump has COME BACK for the next one,
            // which in `await foreach` means its body already ran: the frame is
            // on the wire and `AudioChunksSent` is incremented. Counting at the
            // yield instead let every frame gated on chunk 1 release while the
            // pump was still suspended, so the receive loop could run the script
            // to its terminal frame first; the pump then saw `Completed` at the
            // top of its loop and broke with 0 chunks sent. That lost the race
            // roughly one run in ten under coverage instrumentation, which is
            // slow enough to open the window. The wait above is unaffected —
            // during iteration i the count reads i either way.
            Interlocked.Increment(ref _audioYielded);
        }
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

sealed class DirectWebSocketFactory(IStreamingWebSocket socket) : IStreamingWebSocketFactory
{
    public IStreamingWebSocket Create() => socket;
}

/// <summary>
/// A socket for the two stop-path regressions, kept separate from
/// <see cref="FakeStreamingWebSocket"/> because both of its powers would change
/// the behaviour every existing test depends on: it withholds its whole script
/// until the caller says the last audio chunk is on the wire and then holds it
/// back a further <paramref name="lateBy"/>, and it can fail the close.
///
/// <paramref name="onClose"/> runs when the close frame is written, which is the
/// instant the post-close drain begins. A test that has to act DURING the drain
/// hangs its trigger there rather than on a wall clock started somewhere earlier.
/// </summary>
sealed class LateFrameStreamingWebSocket(
    IEnumerable<StreamingWebSocketFrame> frames,
    TimeSpan lateBy,
    bool throwOnClose = false,
    Action? onClose = null) : IStreamingWebSocket
{
    private readonly Queue<StreamingWebSocketFrame> _frames = new(frames);
    private readonly TaskCompletionSource _audioDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _served;

    /// <summary>Scripted frames already handed to the receive loop.</summary>
    public int Served => Volatile.Read(ref _served);

    public int Closes { get; private set; }

    /// <summary>The last audio chunk is on the wire; the clock for <c>lateBy</c> starts now.</summary>
    public void MarkAudioDone() => _audioDone.TrySetResult();

    public Task ConnectAsync(StreamingWebSocketConnectOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> data,
        System.Net.WebSockets.WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<StreamingWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_frames.Count > 0)
        {
            if (Volatile.Read(ref _served) == 0)
            {
                await _audioDone.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (lateBy > TimeSpan.Zero)
                {
                    await Task.Delay(lateBy, cancellationToken).ConfigureAwait(false);
                }
            }
            Interlocked.Increment(ref _served);
            return _frames.Dequeue();
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("Unreachable.");
    }

    public Task CloseAsync(
        System.Net.WebSockets.WebSocketCloseStatus status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Closes++;
        onClose?.Invoke();
        // What a real socket does when the provider has already torn the session
        // down: the close write lands on a dead connection.
        return throwOnClose
            ? Task.FromException(new System.Net.WebSockets.WebSocketException(
                System.Net.WebSockets.WebSocketError.ConnectionClosedPrematurely))
            : Task.CompletedTask;
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

    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
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

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static void DoesNotContain(string value, string actual)
    {
        if (actual.Contains(value, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected sensitive value '{value}'.");
    }
}
