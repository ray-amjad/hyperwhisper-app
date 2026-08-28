using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;
using HyperWhisper.SpeechOutput;
using HyperWhisper.TranscriptionRouting;
using HyperWhisper.LiveStreaming;
using HyperWhisper.ModelManagement;

var root = Path.Combine(Path.GetTempPath(), "hyperwhisper-routing-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TestRouterAsync(root);
    await TestLocalVocabularyAsync(root);
    await TestParakeetProtocolAsync(root);
    await TestParakeetCancellationAsync(root);
    await TestParakeetLiveProtocolAsync(root);
    TestCredentials();
    Console.WriteLine("HyperWhisper.TranscriptionRouting tests passed.");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static async Task TestParakeetLiveProtocolAsync(string root)
{
    var executable = Path.Combine(root, "parakeet-live-engine");
    await File.WriteAllBytesAsync(executable, [1]);
    var models = Path.Combine(root, "live-models");
    var model = PortableModelCatalog.Parakeet.Single(value => value.Id == "parakeet-v3");
    var modelDirectory = Path.Combine(models, "Parakeet", model.StorageName);
    Directory.CreateDirectory(modelDirectory);
    foreach (var artifact in model.Artifacts)
    {
        var path = Path.Combine(modelDirectory, artifact.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1]);
    }
    var process = new FakeProcess(string.Join('\n',
        "{\"status\":\"ready\",\"provider\":\"cpu\"}",
        "{\"type\":\"started\"}",
        "{\"type\":\"transcript\",\"text\":\"private partial\",\"committed\":\"stable\"}",
        "{\"type\":\"transcript\",\"text\":\"stable final\",\"committed\":\"final\",\"is_final\":true}", ""));
    var sink = new RecordingLiveSink();
    var launcher = new RecordingLauncher(process);
    var transcriber = new ParakeetDaemonLiveTranscriber(
        new StaticRuntime(executable), launcher, models, sink,
        timeouts: new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)));
    var result = await transcriber.TranscribeAsync(new(
        LiveTranscriptionProvider.ParakeetLocal, Language: "en", Model: "parakeet-v3"),
        OneChunk([1, 0, 2, 0]));
    Assert(result.IsSuccess && result.Transcript == "stable final"
        && result.AudioChunksSent == 1 && result.MessagesReceived == 3,
        "local live daemon result mapping changed");
    Assert(sink.Updates.SequenceEqual(new[]
    {
        new LiveTranscriptUpdate("stable", true),
        new LiveTranscriptUpdate("private partial", false),
        new LiveTranscriptUpdate("final", true),
    }), "local live daemon did not distinguish committed and volatile updates");
    var lines = Encoding.UTF8.GetString(process.Input.ToArray())
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert(lines.Length == 3, "local live daemon request count changed");
    using var start = System.Text.Json.JsonDocument.Parse(lines[0]);
    using var audio = System.Text.Json.JsonDocument.Parse(lines[1]);
    using var finish = System.Text.Json.JsonDocument.Parse(lines[2]);
    Assert(start.RootElement.GetProperty("command").GetString() == "start"
        && audio.RootElement.GetProperty("command").GetString() == "audio"
        && Convert.FromBase64String(audio.RootElement.GetProperty("audio").GetString()!).SequenceEqual(new byte[] { 1, 0, 2, 0 })
        && finish.RootElement.GetProperty("command").GetString() == "finish",
        "local live daemon JSON protocol changed");
    Assert(process.Terminated, "local live daemon was not terminated after graceful finish");
}

static async IAsyncEnumerable<ReadOnlyMemory<byte>> OneChunk(byte[] value)
{
    yield return value;
    await Task.CompletedTask;
}

static async Task TestRouterAsync(string root)
{
    var audio = Path.Combine(root, "router.wav");
    await File.WriteAllBytesAsync(audio, [1]);
    var whisper = new FakeTranscriber("whisper", available: false);
    var parakeet = new FakeTranscriber("parakeet", available: false);
    var cloud = new RecordingCloud();
    using var router = new ModeAwareTranscriptionRouter(whisper, parakeet, cloud);

    var localMode = new Mode { ProviderType = "local", LocalEngine = "parakeet", LocalParakeetModel = "parakeet-v3" };
    var local = await router.TranscribeAsync(audio, new TranscriptionWorkflowRequest("fr", SelectedMode: localMode));
    Assert(local.Provider == "parakeet" && parakeet.LastRequest?.SelectedMode == localMode,
        "router did not preserve the selected mode for Parakeet");

    foreach (var providerId in new[]
    {
        "openai", "groq", "elevenlabs", "mistral", "grok", "deepgram",
        "assemblyai", "soniox", "gemini", "geminiTranscribe", "geminitranscribe",
        "gemini-transcribe", "microsoftAzureSpeech",
        "googleSpeech", "hyperwhisper",
    })
    {
        var mode = new Mode
        {
            ProviderType = "cloud",
            CloudProvider = providerId,
            CloudTranscriptionModel = "selected-model",
            // Deliberately the RETIRED tier id. Catalog v8 replaced googleChirp3
            // with geminiTranscribe, and this is the .NET read path that has to
            // migrate it — the assertion below pins the routed provider/model so
            // a missing `migrateFrom` alias (which would silently fall back to
            // Deepgram) fails here instead of on a user's bill.
            CloudAccuracyTier = "googleChirp3",
            CloudTranscriptionDomain = "medical",
            GeminiCustomPrompt = "verbatim prompt",
            CustomVocabulary = ["Ray", "Ray", " Codex ", "Rust<script>", "multi\n  word", "   "],
        };
        var result = await router.TranscribeAsync(
            audio, new TranscriptionWorkflowRequest("auto", SelectedMode: mode, Vocabulary: ["Global", "Ray"]));
        var mapped = cloud.LastRequest!;
        Assert(result.IsSuccess && result.Provider!.Contains("upstream", StringComparison.Ordinal),
            $"{providerId} did not preserve provider attribution");
        Assert(mapped.Model == (providerId == "hyperwhisper" ? "" : "selected-model"),
            $"{providerId} model mapping changed");
        // Request vocabulary first, then the mode's, through the shared core:
        // trimmed, angle brackets stripped, whitespace runs collapsed, empties
        // dropped, de-duplicated case-insensitively keeping first-seen casing.
        Assert(mapped.Language is null
            && mapped.Vocabulary!.SequenceEqual(["Global", "Ray", "Codex", "Rustscript", "multi word"]),
            $"{providerId} language/vocabulary mapping changed");
        if (providerId == "gemini") Assert(mapped.Prompt == "verbatim prompt", "Gemini prompt was dropped");
        // BYOK Gemini 3.5 Transcribe. Every persisted spelling — Windows writes
        // camelCase, macOS lowercase, the catalog uses the hyphenated form — has
        // to land on its OWN provider. Failing the map is not a fallback: the
        // router returns InvalidRequest and the dictation never reaches a
        // provider at all, which is what shipped.
        if (providerId.Contains("ranscribe", StringComparison.Ordinal))
            Assert(mapped.Provider == CloudTranscriptionProvider.GeminiTranscribe,
                $"cloud provider id '{providerId}' mapped to {mapped.Provider}, not GeminiTranscribe");
        if (providerId == "hyperwhisper")
            Assert(mapped.RoutedProvider == "gemini-transcribe"
                && mapped.RoutedModel == "gemini-3.5-transcribe"
                && mapped.RoutedDomain == "medical",
                "HyperWhisper routed tier/model/domain mapping changed: the retired "
                    + $"googleChirp3 tier routed to '{mapped.RoutedProvider}'/'{mapped.RoutedModel}'");
    }

    // --- Catalog v8 migration proof: googleChirp3 -> geminiTranscribe ---------
    //
    // Chirp 3 lost its catalog entry; `geminiTranscribe` took Google's cloud-tier
    // slot and carries every Chirp spelling in its `migrateFrom` list. This route
    // is the one that turns a persisted tier into the X-STT-Provider header, and
    // `SharedCoreBridge.CloudSttProvider` returns null for an id with no entry —
    // at which point `BuildCloudRequest` falls back to the literal "deepgram".
    // So a dropped alias does not throw, it silently bills a Google user as
    // Deepgram. Pin every spelling, and pin the failure mode by name.
    foreach (var legacyTier in new[]
    {
        "googleChirp3", "googlechirp3", "GOOGLECHIRP3", "  googleChirp3  ",
        "googlespeech", "googleSpeech", "chirp", "google-chirp", "googlechirp", "chirp_3",
    })
    {
        var legacyMode = new Mode
        {
            ProviderType = "cloud",
            CloudProvider = "hyperwhisper",
            // A pre-v8 client also persisted Chirp's model id alongside the tier.
            // It is not a model of the new tier, so the router must fall back to
            // the tier default rather than forward a model the backend will 400 on.
            CloudTranscriptionModel = "chirp_3",
            CloudAccuracyTier = legacyTier,
        };
        await router.TranscribeAsync(audio, new TranscriptionWorkflowRequest(SelectedMode: legacyMode));
        var routed = cloud.LastRequest!;
        Assert(routed.RoutedProvider != "deepgram",
            $"legacy tier '{legacyTier}' fell through to Deepgram — the documented silent-failure "
                + "mode. Its `migrateFrom` alias is missing from the geminiTranscribe catalog entry.");
        Assert(routed.RoutedProvider == "gemini-transcribe",
            $"legacy tier '{legacyTier}' routed to '{routed.RoutedProvider}', not 'gemini-transcribe'.");
        Assert(routed.RoutedModel == "gemini-3.5-transcribe",
            $"legacy tier '{legacyTier}' kept model '{routed.RoutedModel}'; chirp_3 is not a model "
                + "of geminiTranscribe, so the tier default must win.");
    }

    // The same canonicalisation, one layer down, on the bridge the Mode editor
    // and the Local API both call. Asserted separately so a regression names the
    // layer that broke rather than only the route that noticed.
    foreach (var legacyTier in new[] { "googleChirp3", "chirp_3", "google-chirp", "googlechirp3" })
    {
        Assert(SharedCoreBridge.CanonicalCloudSttTier(legacyTier) == "geminiTranscribe",
            $"SharedCoreBridge.CanonicalCloudSttTier('{legacyTier}') did not resolve to geminiTranscribe.");
    }

    // And the retired id must not have come back as a catalog entry: an entry
    // would win the canonical match ahead of the alias table and strand the user
    // on a tier with no models and no credits/min.
    Assert(SharedCoreBridge.CloudSttProvider("googleChirp3") is null,
        "googleChirp3 is a catalog entry again; it would shadow the geminiTranscribe migrateFrom alias.");

    // This route's own 1000-term cap, kept out of the shared core deliberately:
    // the live-streaming router caps at 100 and neither may drift onto the other.
    var cappedMode = new Mode
    {
        ProviderType = "cloud",
        CloudProvider = "openai",
        CustomVocabulary = [.. Enumerable.Range(0, 1200).Select(index => $"term{index}")],
    };
    await router.TranscribeAsync(audio, new TranscriptionWorkflowRequest(SelectedMode: cappedMode));
    Assert(cloud.LastRequest!.Vocabulary!.Count == 1000
        && cloud.LastRequest.Vocabulary[999] == "term999",
        "the 1000-term cloud vocabulary cap changed");

    var assemblyMode = new Mode { ProviderType = "cloud", CloudProvider = "assemblyai", CloudTranscriptionModel = null };
    await router.TranscribeAsync(audio, new TranscriptionWorkflowRequest(SelectedMode: assemblyMode));
    Assert(cloud.LastRequest?.Model == "universal-3-5-pro",
        "AssemblyAI default drifted from the authoritative shared catalog");
    Assert(router.Capability.IsAvailable,
        "cloud-only routing was blocked by unavailable local models");

    var unsupported = await router.TranscribeAsync(audio, new TranscriptionWorkflowRequest(
        SelectedMode: new Mode { ProviderType = "cloud", CloudProvider = "unknown" }));
    Assert(unsupported.Failure?.Code == PortableTranscriptionErrorCode.InvalidRequest,
        "unsupported cloud provider did not fail structurally");
}

// The two on-device vocabulary passes on the router's LOCAL branch (issue
// #283). Both are new on this head: it had no phonetic matching and no
// substring pass at all before. The phonetic pass is parakeet-only, the
// substring pass runs for every local engine, and a cloud transcription sees
// neither — same split macOS and Windows have.
static async Task TestLocalVocabularyAsync(string root)
{
    var audio = Path.Combine(root, "vocabulary.wav");
    await File.WriteAllBytesAsync(audio, [1]);

    var parakeetMode = new Mode { ProviderType = "local", LocalEngine = "parakeet", LocalParakeetModel = "parakeet-v3" };
    var whisperMode = new Mode { ProviderType = "local", LocalEngine = "whisper" };

    async Task<string> Transcribe(string spoken, Mode mode, TranscriptionWorkflowRequest request)
    {
        using var router = new ModeAwareTranscriptionRouter(
            new FakeTranscriber("whisper", available: false, text: spoken),
            new FakeTranscriber("parakeet", available: false, text: spoken),
            new RecordingCloud());
        var result = await router.TranscribeAsync(audio, request with { SelectedMode = mode });
        Assert(result.IsSuccess, "the local route failed");
        return result.Text!;
    }

    var whisperWord = new TranscriptionWorkflowRequest(Vocabulary: ["Whisper"]);
    Assert(await Transcribe("hyper wisper", parakeetMode, whisperWord) == "hyper Whisper",
        "the phonetic pass did not correct a Parakeet misrecognition");
    // whisper.cpp is this head's equivalent of the macOS providers that skip
    // the phonetic pass, so a misrecognition stays as spoken.
    Assert(await Transcribe("hyper wisper", whisperMode, whisperWord) == "hyper wisper",
        "the phonetic pass ran for an engine that must not get it");

    var cafe = new TranscriptionWorkflowRequest(
        Vocabulary: ["cafe"],
        VocabularyReplacements: [new PortableVocabularyReplacement("cafe", "Coffee House")]);
    foreach (var mode in new[] { parakeetMode, whisperMode })
        Assert(await Transcribe("Zoë went to the Café today", mode, cafe) == "Zoë went to the Coffee House today",
            "the substring pass did not match through an accent");

    // A word with a replacement is the substring pass's row alone. Same word as
    // the phonetic case above, so the misrecognition stays as spoken while the
    // literal spelling is still replaced.
    var replaced = new TranscriptionWorkflowRequest(
        Vocabulary: ["Whisper"],
        VocabularyReplacements: [new PortableVocabularyReplacement("Whisper", "Dictation")]);
    Assert(await Transcribe("hyper wisper", parakeetMode, replaced) == "hyper wisper",
        "a replacement-bearing row reached the phonetic matcher");
    Assert(await Transcribe("hyper Whisper", parakeetMode, replaced) == "hyper Dictation",
        "the substring pass did not apply a replacement");

    Assert(await Transcribe("hyper wisper", parakeetMode, new TranscriptionWorkflowRequest()) == "hyper wisper",
        "an empty vocabulary changed the text");

    // The cloud branch runs neither pass on any platform.
    using var cloudRouter = new ModeAwareTranscriptionRouter(
        new FakeTranscriber("whisper", available: false),
        new FakeTranscriber("parakeet", available: false),
        new RecordingCloud());
    var cloudMode = new Mode { ProviderType = "cloud", CloudProvider = "openai", CloudTranscriptionModel = "m" };
    var cloudResult = await cloudRouter.TranscribeAsync(
        audio, new TranscriptionWorkflowRequest(Vocabulary: ["Cloud"], SelectedMode: cloudMode));
    Assert(cloudResult.Text == "cloud words", "a cloud transcription reached the local vocabulary passes");
}

static async Task TestParakeetProtocolAsync(string root)
{
    var executable = Path.Combine(root, "parakeet-engine");
    await File.WriteAllBytesAsync(executable, [1]);
    var models = Path.Combine(root, "models");
    Directory.CreateDirectory(Path.Combine(models, "Parakeet", "parakeet-v2"));
    var incompleteLauncher = new RecordingLauncher(new FakeProcess(""));
    using (var incomplete = new ParakeetDaemonTranscriber(
        new StaticRuntime(executable), incompleteLauncher, models))
    {
        var unavailable = await incomplete.TranscribeAsync(executable, new TranscriptionWorkflowRequest(
            SelectedMode: new Mode { LocalEngine = "parakeet", LocalParakeetModel = "parakeet-v2" }));
        Assert(unavailable.Failure?.Code == PortableTranscriptionErrorCode.BackendUnavailable
            && incompleteLauncher.Request is null,
            "an incomplete Parakeet model directory was treated as installed");
    }
    var modelDirectory = Path.Combine(models, "Parakeet", "parakeet-v3");
    Directory.CreateDirectory(modelDirectory);
    foreach (var file in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
        await File.WriteAllBytesAsync(Path.Combine(modelDirectory, file), [1]);
    var audio = Path.Combine(root, "audio ' quoted.wav");
    await File.WriteAllBytesAsync(audio, [1]);
    var process = new FakeProcess(
        "{\"status\":\"ready\",\"provider\":\"cpu\"}\n{\"text\":\"daemon words\",\"duration_ms\":2}\n");
    var launcher = new RecordingLauncher(process);
    using (var service = new ParakeetDaemonTranscriber(
        new StaticRuntime(executable), launcher, models,
        timeouts: new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20))))
    {
        var mode = new Mode { LocalEngine = "parakeet", LocalParakeetModel = "parakeet-v3", Language = "ja" };
        var result = await service.TranscribeAsync(audio, new TranscriptionWorkflowRequest(SelectedMode: mode));
        Assert(result.IsSuccess && result.Text == "daemon words" && result.Provider == "Parakeet parakeet-v3 (cpu)",
            "Parakeet success/provenance mapping changed");
        Assert(launcher.Request!.Arguments.SequenceEqual(new[]
        {
            "--model", Path.Combine(models, "Parakeet", "parakeet-v3"),
            "--language", "ja",
            "--engine", "nemo_transducer",
        }), "Parakeet daemon arguments changed or became shell-composed");
    }
    var lines = Encoding.UTF8.GetString(process.Input.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert(lines.Length == 2 && lines[1] == "{\"command\":\"quit\"}", "Parakeet quit protocol changed");
    using var request = System.Text.Json.JsonDocument.Parse(lines[0]);
    Assert(request.RootElement.GetProperty("audio_path").GetString() == Path.GetFullPath(audio),
        "Parakeet audio path was not JSON encoded exactly");
}

static async Task TestParakeetCancellationAsync(string root)
{
    var executable = Path.Combine(root, "parakeet-cancel-engine");
    await File.WriteAllBytesAsync(executable, [1]);
    var models = Path.Combine(root, "cancel-models");
    var qwenDirectory = Path.Combine(models, "Parakeet", "qwen3-asr-0.6b");
    Directory.CreateDirectory(Path.Combine(qwenDirectory, "tokenizer"));
    foreach (var file in new[] { "conv_frontend.int8.onnx", "encoder.int8.onnx", "decoder.int8.onnx" })
        await File.WriteAllBytesAsync(Path.Combine(qwenDirectory, file), [1]);
    await File.WriteAllBytesAsync(Path.Combine(qwenDirectory, "tokenizer", "tokenizer.json"), [1]);
    var audio = Path.Combine(root, "cancel.wav");
    await File.WriteAllBytesAsync(audio, [1]);
    var process = new FakeProcess("{\"status\":\"ready\",\"provider\":\"cpu\"}\n", blockAfterContent: true);
    using var service = new ParakeetDaemonTranscriber(
        new StaticRuntime(executable), new RecordingLauncher(process), models,
        timeouts: new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20)));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    var result = await service.TranscribeAsync(audio, new TranscriptionWorkflowRequest(
        SelectedMode: new Mode { LocalEngine = "parakeet", LocalParakeetModel = "qwen3-asr-0.6b" }), cancellation.Token);
    Assert(result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled && process.Terminated,
        "Parakeet cancellation did not terminate the daemon process tree structurally");
}

static void TestCredentials()
{
    var store = new MemoryCredentials();
    store.Values[("HyperWhisper", "OpenAIApiKey")] = Encoding.UTF8.GetBytes("secret-value");
    store.Values[("HyperWhisper", "LicenseKey")] = Encoding.UTF8.GetBytes("account-value");
    var source = new CredentialStoreCloudCredentialSource(store, new StaticIdentity());
    var api = source.GetCredentialAsync(CloudTranscriptionProvider.OpenAi, CancellationToken.None).Result;
    var routed = source.GetCredentialAsync(CloudTranscriptionProvider.HyperWhisperCloud, CancellationToken.None).Result;
    Assert(api?.ApiKey == "secret-value" && api.DeviceId == "device-id", "API credential mapping changed");
    Assert(routed?.LicenseKey == "account-value" && routed.ApiKey is null, "account credential mapping changed");
    Assert(store.ReadAccounts.SequenceEqual(["OpenAIApiKey", "LicenseKey"]), "credential account names changed");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeTranscriber(string provider, bool available = true, string? text = null)
    : IRecordedAudioTranscriber
{
    public TranscriptionWorkflowRequest? LastRequest { get; private set; }
    public TranscriptionBackendCapability Capability => new(available, provider);
    public Task<PortableTranscriptionResult> TranscribeAsync(string path, string? language, CancellationToken token = default) =>
        Task.FromResult(PortableTranscriptionResult.Success(text ?? provider, provider));
    public Task<PortableTranscriptionResult> TranscribeAsync(string path, TranscriptionWorkflowRequest request, CancellationToken token = default)
    { LastRequest = request; return Task.FromResult(PortableTranscriptionResult.Success(text ?? provider, provider)); }
}

sealed class RecordingCloud : IBatchCloudTranscriptionClient
{
    public CloudTranscriptionRequest? LastRequest { get; private set; }
    public Task<CloudTranscriptionResult> TranscribeAsync(CloudTranscriptionRequest request, CancellationToken token = default)
    {
        LastRequest = request;
        return Task.FromResult(CloudTranscriptionResult.Success(new("cloud words", null, null, "upstream"), 1));
    }
}

sealed class StaticRuntime(string executable) : INativeRuntimeLocator
{
    public NativeRuntimeCapabilities Capabilities => new("linux-x64", "x64", true, true, true, new HashSet<NativeComputeBackend>());
    public PlatformResult<string> FindExecutable(string component) => PlatformResult<string>.Success(executable);
    public PlatformResult<string> FindLibrary(string component, NativeComputeBackend backend) => PlatformResult<string>.Failure("missing", "missing");
}

sealed class RecordingLauncher(FakeProcess process) : IChildProcessLauncher
{
    public ChildProcessStartRequest? Request { get; private set; }
    public PlatformResult<IChildProcess> Start(ChildProcessStartRequest request)
    { Request = request; return PlatformResult<IChildProcess>.Success(process); }
}

sealed class FakeProcess(string stdout, bool blockAfterContent = false) : IChildProcess
{
    public MemoryStream Input { get; } = new();
    public bool Terminated { get; private set; }
    private readonly Stream _output = blockAfterContent
        ? new PrefixThenBlockStream(Encoding.UTF8.GetBytes(stdout))
        : new MemoryStream(Encoding.UTF8.GetBytes(stdout));
    public int Id => 42;
    public bool HasExited { get; private set; }
    public int? ExitCode => HasExited ? 0 : null;
    public Stream StandardInput => Input;
    public Stream StandardOutput => _output;
    public Stream StandardError => Stream.Null;
    public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    { HasExited = true; return ValueTask.FromResult(0); }
    public ValueTask TerminateAsync(CancellationToken cancellationToken = default)
    { Terminated = true; HasExited = true; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { _output.Dispose(); return ValueTask.CompletedTask; }
}

sealed class PrefixThenBlockStream(byte[] prefix) : Stream
{
    private readonly MemoryStream _prefix = new(prefix);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _prefix.ReadAsync(buffer, cancellationToken);
        if (read > 0) return read;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class MemoryCredentials : ICredentialStore
{
    public Dictionary<(string, string), byte[]> Values { get; } = [];
    public List<string> ReadAccounts { get; } = [];
    public PlatformResult<byte[]?> Read(string resource, string account)
    {
        ReadAccounts.Add(account);
        return PlatformResult<byte[]?>.Success(Values.TryGetValue((resource, account), out var value) ? value.ToArray() : null);
    }
    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value) => PlatformResult.Success();
    public PlatformResult Delete(string resource, string account) => PlatformResult.Success();
}

sealed class StaticIdentity : IDeviceIdentityProvider
{
    public PlatformResult<DeviceIdentity> GetDeviceIdentity() =>
        PlatformResult<DeviceIdentity>.Success(new("device-id", DeviceIdentitySource.PlatformMachineId));
}

sealed class RecordingLiveSink : ILiveTranscriptSink
{
    public List<LiveTranscriptUpdate> Updates { get; } = [];
    public void OnTranscript(LiveTranscriptUpdate update) => Updates.Add(update);
}
