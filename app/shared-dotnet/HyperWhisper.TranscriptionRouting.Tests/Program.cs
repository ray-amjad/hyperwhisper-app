using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;
using HyperWhisper.TranscriptionRouting;

var root = Path.Combine(Path.GetTempPath(), "hyperwhisper-routing-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TestRouterAsync(root);
    await TestParakeetProtocolAsync(root);
    await TestParakeetCancellationAsync(root);
    TestCredentials();
    Console.WriteLine("HyperWhisper.TranscriptionRouting tests passed.");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
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
        "assemblyai", "soniox", "gemini", "microsoftAzureSpeech",
        "googleSpeech", "hyperwhisper",
    })
    {
        var mode = new Mode
        {
            ProviderType = "cloud",
            CloudProvider = providerId,
            CloudTranscriptionModel = "selected-model",
            CloudAccuracyTier = "googleChirp3",
            CloudTranscriptionDomain = "medical",
            GeminiCustomPrompt = "verbatim prompt",
            CustomVocabulary = ["Ray", "Ray", " Codex "],
        };
        var result = await router.TranscribeAsync(
            audio, new TranscriptionWorkflowRequest("auto", SelectedMode: mode, Vocabulary: ["Global", "Ray"]));
        var mapped = cloud.LastRequest!;
        Assert(result.IsSuccess && result.Provider!.Contains("upstream", StringComparison.Ordinal),
            $"{providerId} did not preserve provider attribution");
        Assert(mapped.Model == (providerId == "hyperwhisper" ? "" : "selected-model"),
            $"{providerId} model mapping changed");
        Assert(mapped.Language is null && mapped.Vocabulary!.SequenceEqual(["Global", "Ray", "Codex"]),
            $"{providerId} language/vocabulary mapping changed");
        if (providerId == "gemini") Assert(mapped.Prompt == "verbatim prompt", "Gemini prompt was dropped");
        if (providerId == "hyperwhisper")
            Assert(mapped.RoutedProvider == "google-chirp"
                && mapped.RoutedModel == "chirp_3"
                && mapped.RoutedDomain == "medical",
                "HyperWhisper routed tier/model/domain mapping changed");
    }

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

sealed class FakeTranscriber(string provider, bool available = true) : IRecordedAudioTranscriber
{
    public TranscriptionWorkflowRequest? LastRequest { get; private set; }
    public TranscriptionBackendCapability Capability => new(available, provider);
    public Task<PortableTranscriptionResult> TranscribeAsync(string path, string? language, CancellationToken token = default) =>
        Task.FromResult(PortableTranscriptionResult.Success(provider, provider));
    public Task<PortableTranscriptionResult> TranscribeAsync(string path, TranscriptionWorkflowRequest request, CancellationToken token = default)
    { LastRequest = request; return Task.FromResult(PortableTranscriptionResult.Success(provider, provider)); }
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
