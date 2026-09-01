using System.Net;
using System.Buffers.Binary;
using HyperWhisper.AudioNormalization;
using HyperWhisper.Data.Entities;
using HyperWhisper.FileTranscription;
using HyperWhisper.ModelManagement;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.PortableApplication.ViewModels;
using HyperWhisper.SharedCore;

var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-file-routing-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var tests = new (string Name, Func<Task> Run)[]
    {
        ("cloud import preserves private provider-native bytes", CloudPreservesOriginal),
        ("Meta Muse converts MP3 and M4A to canonical WAV", MetaMuseNormalizesPortableSources),
        ("Meta Muse preserves a compatible WAV", MetaMusePreservesCompatibleWave),
        ("Meta Muse rejects and cleans an oversized normalized WAV", MetaMuseRejectsNormalizedOverflow),
        ("Meta Muse reports a stable conversion failure", MetaMuseConversionFailure),
        ("Meta Muse rejects known overlength audio before conversion", MetaMuseRejectsOverlengthBeforeConversion),
        ("local import normalizes to canonical WAV", LocalNormalizes),
        ("import snapshots mode before asynchronous preflight", ModeSnapshot),
        ("readiness failure prevents expensive import", EarlyReadinessFailure),
        ("provider cap rejects before copy", CapRejectsBeforeCopy),
        ("malformed cloud source returns a stable failure", MalformedSourceIsStable),
        ("terminal failure deletes owned copy only", OwnedFailureCleanup),
        ("shared core rechecks actual upload artifact", SharedCoreArtifactCap),
    };
    foreach (var test in tests)
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    Console.WriteLine($"{tests.Length}/{tests.Length} file import integration tests passed");
}
finally { Directory.Delete(root, recursive: true); }

async Task CloudPreservesOriginal()
{
    var source = Source("cloud-original.MP3", [0x49, 0x44, 0x33, 1, 2, 3, 4]);
    var normalizer = new FakeNormalizer();
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("hello world", "OpenAI"));
    using var fixture = Fixture(source, CloudMode(), transcriber, normalizer);
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(normalizer.Calls == 0, "cloud import invoked WAV normalization");
    Assert(transcriber.Path is not null && !string.Equals(transcriber.Path, source, StringComparison.Ordinal)
        && string.Equals(Path.GetExtension(transcriber.Path), ".mp3", StringComparison.OrdinalIgnoreCase),
        "cloud import did not preserve the native container in owned storage");
    Assert(File.Exists(transcriber.Path!),
        $"successful cloud import lost its owned artifact ({fixture.ViewModel.ErrorCode}: {fixture.ViewModel.Message})");
    Assert(File.ReadAllBytes(transcriber.Path!).SequenceEqual(File.ReadAllBytes(source)),
        "cloud import changed provider-native bytes");
    Assert(IsOwnerOnly(transcriber.Path!), "cloud owned artifact was not private");
    Assert(File.Exists(source), "cloud import deleted the user's original");
}

async Task MetaMuseNormalizesPortableSources()
{
    foreach (var extension in new[] { "mp3", "m4a" })
    {
        var source = Source($"muse-source.{extension}", [1, 2, 3, 4]);
        var normalizer = new FakeNormalizer();
        var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("muse", "Meta"));
        using var fixture = Fixture(source, MetaMuseMode(), transcriber, normalizer);
        await fixture.ViewModel.TranscribeFileAsync();
        Assert(normalizer.Calls == 1 && transcriber.Path is not null
            && string.Equals(Path.GetExtension(transcriber.Path), ".wav", StringComparison.OrdinalIgnoreCase)
            && fixture.ViewModel.ErrorCode is null,
            $"Meta Muse did not normalize {extension} to a valid WAV");
    }
}

async Task MetaMusePreservesCompatibleWave()
{
    var source = Source("muse-compatible.wav", WaveFixture.Canonical(16_000, 32));
    var normalizer = new FakeNormalizer();
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("muse", "Meta"));
    using var fixture = Fixture(source, MetaMuseMode(), transcriber, normalizer);
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(normalizer.Calls == 0 && transcriber.Path is not null
        && File.ReadAllBytes(transcriber.Path).SequenceEqual(File.ReadAllBytes(source)),
        "Meta Muse changed a compatible 16 kHz mono PCM16 WAV");
}

async Task MetaMuseRejectsNormalizedOverflow()
{
    var source = Source("muse-overflow.mp3", [1, 2, 3]);
    var normalizer = new FakeNormalizer { OutputDataBytes = 32L * 1024 * 1024 + 1 };
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("unused", "Meta"));
    using var fixture = Fixture(source, MetaMuseMode(), transcriber, normalizer);
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(fixture.ViewModel.ErrorCode == "file_preflight.file_too_large"
        && transcriber.Path is null
        && !Directory.EnumerateFiles(fixture.Paths.RecordingsDirectory).Any(),
        "Meta Muse retained or uploaded an oversized normalized WAV");
}

async Task MetaMuseConversionFailure()
{
    var source = Source("muse-failure.mp3", [1, 2, 3]);
    var normalizer = new FakeNormalizer { Fail = true };
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("unused", "Meta"));
    using var fixture = Fixture(source, MetaMuseMode(), transcriber, normalizer);
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(fixture.ViewModel.ErrorCode == "audio_normalization.failed"
        && transcriber.Path is null && File.Exists(source),
        "Meta Muse conversion failure was not stable or deleted the source");
}

async Task MetaMuseRejectsOverlengthBeforeConversion()
{
    var source = Source("muse-overlength.mp3", [1, 2, 3]);
    var normalizer = new FakeNormalizer();
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("unused", "Meta"));
    using var fixture = Fixture(source, MetaMuseMode(), transcriber, normalizer,
        metadata: new StaticMetadata(3, TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1)));
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(fixture.ViewModel.ErrorCode == "file_preflight.duration_too_long"
        && normalizer.Calls == 0 && transcriber.Path is null,
        "Meta Muse converted or uploaded known overlength audio");
}

async Task LocalNormalizes()
{
    var source = Source("local.flac", [1, 2, 3]);
    var normalizer = new FakeNormalizer();
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("local", "Whisper"));
    using var fixture = Fixture(source, LocalMode(), transcriber, normalizer);
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(normalizer.Calls == 1 && string.Equals(Path.GetExtension(transcriber.Path), ".wav", StringComparison.OrdinalIgnoreCase),
        "local import did not use canonical WAV normalization");
    Assert(File.Exists(source), "local import deleted the user's original");
}

async Task ModeSnapshot()
{
    var source = Source("snapshot.mp3", [1, 2, 3, 4]);
    var mode = CloudMode();
    var metadata = new BlockingMetadata(new FileInfo(source).Length);
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("snapshot", "OpenAI"));
    using var fixture = Fixture(source, mode, transcriber, new FakeNormalizer(), metadata: metadata);
    var import = fixture.ViewModel.TranscribeFileAsync();
    await metadata.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    mode.CloudProvider = "groq";
    mode.CloudTranscriptionModel = "whisper-large-v3";
    mode.Name = "Mutated while importing";
    metadata.Release.TrySetResult();
    await import;
    Assert(transcriber.Request?.SelectedMode is
        { CloudProvider: "openai", CloudTranscriptionModel: "whisper-1", Name: "Cloud snapshot" },
        "in-flight import observed a later mode mutation");
}

async Task EarlyReadinessFailure()
{
    var source = Source("not-ready.wav", [1, 2, 3]);
    var normalizer = new FakeNormalizer();
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("unused", "Whisper"));
    using var fixture = Fixture(source, LocalMode(), transcriber, normalizer,
        readiness: new FakeReadiness { ModelInstalled = false });
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(fixture.ViewModel.ErrorCode == "file_preflight.model_not_installed"
        && normalizer.Calls == 0 && transcriber.Path is null,
        "local readiness failure did not stop before normalization/transcription");
    Assert(!Directory.EnumerateFiles(fixture.Paths.RecordingsDirectory).Any(),
        "readiness failure created an owned artifact");
}

async Task CapRejectsBeforeCopy()
{
    var source = Source("oversized.mp3", [1, 2, 3]);
    var metadata = new StaticMetadata(25L * 1024 * 1024 + 1);
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Success("unused", "OpenAI"));
    using var fixture = Fixture(source, CloudMode(), transcriber, new FakeNormalizer(), metadata: metadata);
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(fixture.ViewModel.ErrorCode == "file_preflight.file_too_large" && transcriber.Path is null,
        "provider cap did not reject before cloud copy/transcription");
    Assert(!Directory.EnumerateFiles(fixture.Paths.RecordingsDirectory).Any(),
        "provider cap rejection created an owned artifact");
}

async Task OwnedFailureCleanup()
{
    var source = Source("failure.ogg", [9, 8, 7, 6]);
    var transcriber = new CapturingTranscriber(PortableTranscriptionResult.Failed(
        PortableTranscriptionErrorCode.TranscriptionFailed, "expected"));
    using var fixture = Fixture(source, CloudMode(), transcriber, new FakeNormalizer());
    await fixture.ViewModel.TranscribeFileAsync();
    Assert(transcriber.Path is not null && !File.Exists(transcriber.Path),
        "terminal failure retained the app-owned cloud artifact");
    Assert(File.Exists(source), "terminal failure deleted the user's original");
}

async Task MalformedSourceIsStable()
{
    var paths = new TestPaths(Path.Combine(root, Guid.NewGuid().ToString("N")));
    var importer = new DurableAudioImportService(new UnixPrivateFiles(), paths, normalizer: new FakeNormalizer());
    var result = await importer.ImportOriginalAsync("invalid\0path.mp3", 25L * 1024 * 1024);
    Assert(result.IsFailure && result.Error?.Code == "audio_import.failed",
        "malformed cloud source escaped the stable import result boundary");
}

async Task SharedCoreArtifactCap()
{
    var path = Source("transport-cap.mp3", [1]);
    await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        stream.SetLength(25L * 1024 * 1024 + 1);
    var handler = new CountingHandler();
    // The privacy flag is a required constructor argument. This case never gets
    // as far as a request, so the default-install answer (sharing on, which
    // sends no header) is the honest value to hand it.
    using var service = new CloudTranscriptionService(handler, new FakeCredentials(), () => true);
    var result = await service.TranscribeAsync(new(
        CloudTranscriptionProvider.OpenAi, path, "whisper-1"));
    Assert(result.Failure?.Code == CloudTranscriptionErrorCode.FileTooLarge && handler.Calls == 0,
        "shared core attempted transport for an oversized actual artifact");
}

TestFixture Fixture(
    string source,
    Mode mode,
    CapturingTranscriber transcriber,
    FakeNormalizer normalizer,
    IFileAudioMetadataSource? metadata = null,
    FakeReadiness? readiness = null)
{
    var paths = new TestPaths(Path.Combine(root, Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(paths.RecordingsDirectory);
    var workflow = new TranscriptionWorkflow(
        new FakeRecorder(), new FakeDevices(), transcriber, new MemoryHistory());
    var importer = new DurableAudioImportService(new UnixPrivateFiles(), paths, normalizer: normalizer);
    var preflight = new PortableFileTranscriptionPreflight(
        metadata ?? new StreamingFileAudioMetadataSource(),
        readiness ?? new FakeReadiness(), new FakeCredentials());
    var viewModel = new TranscriptionWorkflowViewModel(
        workflow, () => new TranscriptionWorkflowRequest(SelectedMode: mode), importer, preflight)
    { FilePath = source };
    return new(viewModel, paths);
}

string Source(string name, byte[] bytes)
{
    var path = Path.Combine(root, $"{Guid.NewGuid():N}-{name}");
    File.WriteAllBytes(path, bytes);
    return path;
}

static Mode CloudMode() => new()
{
    Name = "Cloud snapshot", ProviderType = "cloud", CloudProvider = "openai",
    CloudTranscriptionModel = "whisper-1", Language = "en",
};

static Mode LocalMode() => new()
{
    Name = "Local snapshot", ProviderType = "local", LocalEngine = "whisper",
    Model = "base", ModelType = "base", Language = "en",
};

static Mode MetaMuseMode() => new()
{
    Name = "Meta Muse", ProviderType = "cloud", CloudProvider = "hyperwhisper",
    CloudAccuracyTier = "metaMuse", CloudTranscriptionModel = "muse-voice-transcribe-1.0",
    Language = "auto",
};

static bool IsOwnerOnly(string path) => OperatingSystem.IsWindows()
    || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record TestFixture(TranscriptionWorkflowViewModel ViewModel, TestPaths Paths) : IDisposable
{
    public void Dispose() => ViewModel.Dispose();
}

sealed class TestPaths(string root) : IAppPaths
{
    public string DataDirectory => root;
    public string ConfigDirectory => Path.Combine(root, "config");
    public string CacheDirectory => Path.Combine(root, "cache");
    public string StateDirectory => Path.Combine(root, "state");
    public string LogsDirectory => Path.Combine(root, "logs");
    public string ModelsDirectory => Path.Combine(root, "models");
    public string RecordingsDirectory => Path.Combine(root, "recordings");
    public string RuntimeDirectory => Path.Combine(root, "runtime");
    public string TemporaryDirectory => Path.Combine(root, "tmp");
}

sealed class FakeNormalizer : IAudioNormalizationService
{
    public int Calls { get; private set; }
    public long OutputDataBytes { get; init; } = 4;
    public bool Fail { get; init; }
    public async Task<PlatformResult<string>> NormalizeAsync(
        string sourcePath, string destinationDirectory,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Fail) return PlatformResult<string>.Failure(
            "audio_normalization.failed", "Audio conversion failed.");
        var path = Path.Combine(destinationDirectory, $"normalized-{Guid.NewGuid():N}.wav");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(WaveFixture.Canonical(16_000, checked((uint)OutputDataBytes)), cancellationToken);
            stream.SetLength(44 + OutputDataBytes);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return PlatformResult<string>.Success(path);
    }
}

sealed class StaticMetadata(long length, TimeSpan? duration = null) : IFileAudioMetadataSource
{
    public ValueTask<FileAudioMetadata?> ReadAsync(string path, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<FileAudioMetadata?>(new(length, duration)); }
}

static class WaveFixture
{
    public static byte[] Canonical(uint sampleRate, uint dataBytes)
    {
        var value = new byte[checked((int)dataBytes + 44)];
        "RIFF"u8.CopyTo(value); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), dataBytes + 36);
        "WAVEfmt "u8.CopyTo(value.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(34), 16);
        "data"u8.CopyTo(value.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(40), dataBytes);
        return value;
    }
}

sealed class BlockingMetadata(long length) : IFileAudioMetadataSource
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<FileAudioMetadata?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return new(length, null);
    }
}

sealed class FakeReadiness : ILocalFileTranscriptionReadiness
{
    public bool BackendAvailable { get; set; } = true;
    public bool ModelInstalled { get; set; } = true;
    public ValueTask<bool> IsBackendAvailableAsync(LocalTranscriptionEngine engine, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(BackendAvailable);
    public ValueTask<bool> IsModelInstalledAsync(ManagedModel model, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ModelInstalled);
}

sealed class FakeCredentials : ICloudCredentialSource
{
    public ValueTask<CloudCredential?> GetCredentialAsync(
        CloudTranscriptionProvider provider, CancellationToken cancellationToken) =>
        ValueTask.FromResult<CloudCredential?>(new(ApiKey: "test", LicenseKey: "test", DeviceId: "test"));
}

sealed class UnixPrivateFiles : IPrivateFileService
{
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents) => PlatformResult.Success();
    public PlatformResult WriteAllTextAtomically(string path, string contents) => PlatformResult.Success();
    public PlatformResult<byte[]?> ReadAllBytes(string path) => PlatformResult<byte[]?>.Success(null);
    public PlatformResult<string?> ReadAllText(string path) => PlatformResult<string?>.Success(null);
    public PlatformResult Delete(string path) { if (File.Exists(path)) File.Delete(path); return PlatformResult.Success(); }
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) =>
        PlatformResult<bool>.Success(File.Exists(path) && (OperatingSystem.IsWindows()
            || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0));
}

sealed class CapturingTranscriber(PortableTranscriptionResult result) : IRecordedAudioTranscriber
{
    public string? Path { get; private set; }
    public TranscriptionWorkflowRequest? Request { get; private set; }
    public TranscriptionBackendCapability Capability => new(true, "test");
    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath, TranscriptionWorkflowRequest request, CancellationToken cancellationToken = default)
    { Path = audioPath; Request = request; return Task.FromResult(result); }
    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath, string? language, CancellationToken cancellationToken = default)
    { Path = audioPath; return Task.FromResult(result); }
}

sealed class FakeRecorder : IAudioRecorder
{
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public PlatformResult Start(AudioRecordingOptions options) => PlatformResult.Success();
    public PlatformResult<string> Stop() => PlatformResult<string>.Failure("unused", "unused");
    public void Dispose() { }
}

sealed class FakeDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() =>
        PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([]);
    public void Dispose() { }
}

sealed class MemoryHistory : ITranscriptionHistoryStore
{
    private readonly Dictionary<Guid, Transcript> _items = [];
    public Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.GetValueOrDefault(id));
    public Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default)
    { _items[transcript.Id] = transcript; return Task.CompletedTask; }
    public Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default)
    { _items[transcript.Id] = transcript; return Task.FromResult(true); }
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.Remove(id));
}

sealed class CountingHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
}
