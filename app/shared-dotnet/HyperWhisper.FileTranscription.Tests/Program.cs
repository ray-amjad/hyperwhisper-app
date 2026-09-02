using System.Buffers.Binary;
using System.Diagnostics;
using HyperWhisper.FileTranscription;
using HyperWhisper.ModelManagement;
using HyperWhisper.SharedCore;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Windows cloud byte limits are exact", CloudByteLimits),
    ("invalid duration is rejected before downstream work", InvalidDurationGate),
    ("cloud credentials and models fail before metadata", CloudReadinessPrecedesMetadata),
    ("HyperWhisper guest device readiness and model fallback match routing", HyperWhisperGuestReadiness),
    ("Meta Muse accepts canonical WAV without conversion", MetaMuseCanonicalWave),
    ("Meta Muse converts portable non-WAV and incompatible WAV inputs", MetaMuseNormalization),
    ("Meta Muse enforces limits on the normalized WAV", MetaMuseNormalizedLimits),
    ("non-Muse cloud tiers keep the existing portable import policy", NonMuseCloudTierLimits),
    ("local backend model and install state fail before metadata", LocalReadinessPrecedesMetadata),
    ("local import has no provider byte or duration cap", LocalHasNoProviderLimit),
    ("unsupported format and missing file are stable", FileValidation),
    ("failures do not expose path model or credentials", FailurePrivacy),
    ("WAV metadata is streamed from a sparse file", SparseWaveMetadata),
    ("RIFF truncation and oversized chunks are bounded", MalformedWaveMetadata),
    ("cancellation is stable and content-free", Cancellation),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length}/{tests.Length} portable file preflight tests passed");

static async Task CloudByteLimits()
{
    var cases = new[]
    {
        new ProviderLimit(CloudTranscriptionProvider.OpenAi, "whisper-1", 25L * ByteSizes.MiB, false),
        new ProviderLimit(CloudTranscriptionProvider.Groq, "whisper-large-v3-turbo", 25L * ByteSizes.MiB, false),
        new ProviderLimit(CloudTranscriptionProvider.Deepgram, "nova-3-general", 2L * ByteSizes.GiB, false),
        new ProviderLimit(CloudTranscriptionProvider.AssemblyAi, "universal-3-5-pro", 5L * ByteSizes.GiB, false),
        new ProviderLimit(CloudTranscriptionProvider.ElevenLabs, "scribe_v2", 3L * ByteSizes.GiB, false),
        new ProviderLimit(CloudTranscriptionProvider.Mistral, "voxtral-mini-latest", 100L * ByteSizes.MiB, false),
        new ProviderLimit(CloudTranscriptionProvider.Soniox, "stt-async-v5", 1L * ByteSizes.GiB, false),
        new ProviderLimit(CloudTranscriptionProvider.Gemini, "gemini-2.5-flash", 2L * ByteSizes.GiB, false),
        new ProviderLimit(CloudTranscriptionProvider.Grok, "", 500L * ByteSizes.MiB, false),
        new ProviderLimit(CloudTranscriptionProvider.AzureMai, "mai-transcribe-1.5", 300L * ByteSizes.MiB, true),
        new ProviderLimit(CloudTranscriptionProvider.GoogleChirp, "chirp_3", 9_500_000L, true),
        new ProviderLimit(CloudTranscriptionProvider.GeminiTranscribe, "gemini-3.5-transcribe", 14L * ByteSizes.MiB, false),
        new ProviderLimit(CloudTranscriptionProvider.HyperWhisperCloud, "nova-3-general", 2L * ByteSizes.GiB, true, "deepgramNova3"),
        new ProviderLimit(CloudTranscriptionProvider.Meta, "muse-voice-transcribe-1.0", 32L * ByteSizes.MiB, false),
    };
    foreach (var item in cases)
    {
        var metadata = new FakeMetadata
        {
            Value = item.Provider == CloudTranscriptionProvider.Meta
                ? new(item.Bytes, TimeSpan.FromMinutes(1), 1, 1, 16_000, 16)
                : new(item.Bytes, TimeSpan.FromMinutes(1)),
        };
        var result = await Service(metadata, account: item.Account).ValidateAsync(
            "recording.wav", new(FileTranscriptionRoute.Cloud, item.Model, CloudProvider: item.Provider, CloudCatalogTier: item.Tier));
        Assert(result.IsSuccess && result.Constraints?.MaximumBytes == item.Bytes,
            $"{item.Provider} exact byte cap failed");
        result = await Service(metadata, account: item.Account).ValidateAsync(
            "recording.wav", new(FileTranscriptionRoute.Cloud, "", CloudProvider: item.Provider, CloudCatalogTier: item.Tier));
        Assert(result.IsSuccess && result.ResolvedModel == item.Model,
            $"{item.Provider} default model mapping failed");
        var invalidMetadata = new FakeMetadata { Value = metadata.Value };
        var invalid = await Service(invalidMetadata, account: item.Account).ValidateAsync(
            "recording.wav", new(FileTranscriptionRoute.Cloud, "unsupported-model", CloudProvider: item.Provider, CloudCatalogTier: item.Tier));
        if (item.Provider == CloudTranscriptionProvider.HyperWhisperCloud)
            Assert(invalid.IsSuccess && invalid.ResolvedModel == item.Model,
                "HyperWhisper invalid explicit model did not fall back to the tier default");
        else
        {
            AssertCode(invalid, "file_preflight.model_unsupported");
            Assert(invalidMetadata.Calls == 0, $"{item.Provider} invalid model reached file metadata");
        }
        var missingCredential = new PortableFileTranscriptionPreflight(
            metadata, new FakeLocalReadiness(), new FakeCredentials(false, false));
        AssertCode(await missingCredential.ValidateAsync("recording.wav",
            new(FileTranscriptionRoute.Cloud, item.Model, CloudProvider: item.Provider, CloudCatalogTier: item.Tier)),
            "file_preflight.credential_missing");
        metadata.Value = item.Provider == CloudTranscriptionProvider.Meta
            ? new(item.Bytes + 1, TimeSpan.FromMinutes(1), 1, 1, 16_000, 16)
            : new(item.Bytes + 1, TimeSpan.FromMinutes(1));
        result = await Service(metadata, account: item.Account).ValidateAsync(
            "recording.wav", new(FileTranscriptionRoute.Cloud, item.Model, CloudProvider: item.Provider, CloudCatalogTier: item.Tier));
        AssertCode(result, "file_preflight.file_too_large");
    }
}

static async Task HyperWhisperGuestReadiness()
{
    var metadata = new FakeMetadata { Value = new(1024, TimeSpan.FromSeconds(1)) };
    var guest = new PortableFileTranscriptionPreflight(
        metadata, new FakeLocalReadiness(), new FakeCredentials(false, false, device: true));
    var result = await guest.ValidateAsync("recording.wav", new(
        FileTranscriptionRoute.Cloud, "not-in-tier", CloudProvider: CloudTranscriptionProvider.HyperWhisperCloud,
        CloudCatalogTier: "deepgramNova3"));
    Assert(result.IsSuccess && result.ResolvedModel == "nova-3-general",
        "device-only HyperWhisper guest routing was rejected or failed model fallback");
}

static async Task MetaMuseCanonicalWave()
{
    foreach (var target in MetaMuseTargets())
    foreach (var sampleRate in new uint[] { 16_000, 24_000 })
    {
        var metadata = new FakeMetadata
        {
            Value = new(1024, TimeSpan.FromMinutes(1), 1, 1, sampleRate, 16),
        };
        var result = await Service(metadata,
            account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud).ValidateAsync(
            "recording.wav", target);
        Assert(result.IsSuccess && !result.RequiresNormalization
            && result.ResolvedModel == "muse-voice-transcribe-1.0"
            && result.Constraints is
            {
                MaximumBytes: 32L * ByteSizes.MiB,
                MaximumDuration: var duration,
                RequiresMuseWave: true,
                MaximumSourceBytes: 64L * ByteSizes.MiB,
            }
            && duration == TimeSpan.FromMinutes(10),
            $"Meta Muse rejected canonical {sampleRate} Hz PCM WAV");
    }
}

static async Task MetaMuseNormalization()
{
    foreach (var target in MetaMuseTargets())
    foreach (var file in new[] { "recording.mp3", "recording.m4a" })
    {
        var result = await Service(new FakeMetadata
        {
            // The source may exceed the upload cap. The normalized artifact is
            // authoritative because conversion can reduce its byte size.
            Value = new(40L * ByteSizes.MiB, null),
        }, account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud).ValidateAsync(file, target);
        Assert(result.IsSuccess && result.RequiresNormalization,
            $"Meta Muse did not request conversion for {Path.GetExtension(file)}");
    }

    foreach (var target in MetaMuseTargets())
    {
    var sourceAtLimit = await Service(new FakeMetadata
    {
        Value = new(64L * ByteSizes.MiB, null),
    }, account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud)
        .ValidateAsync("source-at-limit.mp3", target);
    Assert(sourceAtLimit.IsSuccess && sourceAtLimit.RequiresNormalization,
        "Meta Muse rejected a source at the 64 MiB normalization bound");

    var sourceOverLimit = await Service(new FakeMetadata
    {
        Value = new(64L * ByteSizes.MiB + 1, null),
    }, account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud)
        .ValidateAsync("source-over-limit.mp3", target);
    AssertCode(sourceOverLimit, "file_preflight.file_too_large");

    var overlength = await Service(new FakeMetadata
    {
        Value = new(40L * ByteSizes.MiB, TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1)),
    }, account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud).ValidateAsync("recording.mp3", target);
    AssertCode(overlength, "file_preflight.duration_too_long");

    foreach (var value in new[]
    {
        new FileAudioMetadata(1024, null, 1, 1, 16_000, 16),
        new FileAudioMetadata(1024, TimeSpan.FromMinutes(1), 1, 2, 16_000, 16),
        new FileAudioMetadata(1024, TimeSpan.FromMinutes(1), 1, 1, 48_000, 16),
        new FileAudioMetadata(1024, TimeSpan.FromMinutes(1), 3, 1, 16_000, 32),
    })
    {
        var result = await Service(new FakeMetadata { Value = value }, account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud)
            .ValidateAsync("recording.wav", target);
        Assert(result.IsSuccess && result.RequiresNormalization,
            "Meta Muse did not normalize an incompatible WAV");
    }
    }
}

static async Task NonMuseCloudTierLimits()
{
    var metadata = new FakeMetadata { Value = new(30L * ByteSizes.MiB, TimeSpan.FromMinutes(20)) };
    var result = await Service(metadata, account: true).ValidateAsync(
        "recording.m4a",
        new(FileTranscriptionRoute.Cloud, "", CloudProvider: CloudTranscriptionProvider.HyperWhisperCloud,
            CloudCatalogTier: "gemini"));
    Assert(result.IsSuccess && result.Constraints is
        { MaximumBytes: 2L * ByteSizes.GiB, MaximumDuration: null, RequiresMuseWave: false },
        "a non-Muse cloud tier inherited catalog file limits");
}

static async Task MetaMuseNormalizedLimits()
{
    foreach (var target in MetaMuseTargets())
    {
    var metadata = new FakeMetadata
    {
        Value = new(32L * ByteSizes.MiB + 1, TimeSpan.FromMinutes(1), 1, 1, 16_000, 16),
    };
    AssertCode(await Service(metadata,
        account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud).ValidateAsync(
        "normalized.wav", target), "file_preflight.file_too_large");

    metadata.Value = new(1024, TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1),
        1, 1, 16_000, 16);
    AssertCode(await Service(metadata,
        account: target.CloudProvider == CloudTranscriptionProvider.HyperWhisperCloud).ValidateAsync(
        "normalized.wav", target), "file_preflight.duration_too_long");
    }
}

static async Task InvalidDurationGate()
{
    var metadata = new FakeMetadata { Value = new(1024, TimeSpan.Zero) };
    var downstreamCalls = 0;
    var result = await Service(metadata).ValidateAsync("recording.wav",
        new(FileTranscriptionRoute.Cloud, "nova-3-general", CloudProvider: CloudTranscriptionProvider.Deepgram));
    if (result.IsSuccess) downstreamCalls++;
    AssertCode(result, "file_preflight.duration_invalid");
    Assert(downstreamCalls == 0, "normalization/upload seam ran after duration rejection");
}

static async Task CloudReadinessPrecedesMetadata()
{
    var metadata = new FakeMetadata { Value = new(1024, TimeSpan.FromSeconds(1)) };
    var missing = new PortableFileTranscriptionPreflight(metadata, new FakeLocalReadiness(), new FakeCredentials(false, false));
    var result = await missing.ValidateAsync("secret-filename.wav",
        new(FileTranscriptionRoute.Cloud, "whisper-1", CloudProvider: CloudTranscriptionProvider.OpenAi));
    AssertCode(result, "file_preflight.credential_missing");
    Assert(metadata.Calls == 0, "metadata was read before credential readiness");

    result = await Service(metadata).ValidateAsync("secret-filename.wav",
        new(FileTranscriptionRoute.Cloud, "not-a-real-model", CloudProvider: CloudTranscriptionProvider.OpenAi));
    AssertCode(result, "file_preflight.model_unsupported");
    Assert(metadata.Calls == 0, "metadata was read before model validation");
}

static async Task LocalReadinessPrecedesMetadata()
{
    var metadata = new FakeMetadata { Value = new(1024, TimeSpan.FromSeconds(1)) };
    var readiness = new FakeLocalReadiness { BackendAvailable = false };
    var service = new PortableFileTranscriptionPreflight(metadata, readiness, new FakeCredentials(true, true));
    var target = new FileTranscriptionTarget(FileTranscriptionRoute.Local, "base", LocalTranscriptionEngine.Whisper);
    AssertCode(await service.ValidateAsync("recording.wav", target), "file_preflight.backend_unavailable");
    Assert(metadata.Calls == 0, "metadata was read before backend readiness");

    readiness.BackendAvailable = true;
    readiness.ModelInstalled = false;
    AssertCode(await service.ValidateAsync("recording.wav", target), "file_preflight.model_not_installed");
    Assert(metadata.Calls == 0, "metadata was read before model installation readiness");
}

static async Task LocalHasNoProviderLimit()
{
    var metadata = new FakeMetadata { Value = new(long.MaxValue, TimeSpan.FromDays(10)) };
    var result = await Service(metadata).ValidateAsync("recording.wav",
        new(FileTranscriptionRoute.Local, "base", LocalTranscriptionEngine.Whisper));
    Assert(result.IsSuccess && result.Constraints is { MaximumBytes: null, MaximumDuration: null },
        "local route inherited a cloud provider limit");
}

static async Task FileValidation()
{
    var metadata = new FakeMetadata { Value = new(1024, TimeSpan.FromSeconds(1)) };
    var service = Service(metadata);
    AssertCode(await service.ValidateAsync("recording.aac",
        new(FileTranscriptionRoute.Local, "base", LocalTranscriptionEngine.Whisper)),
        "file_preflight.format_unsupported");
    Assert(metadata.Calls == 0, "unsupported format reached metadata reader");
    var upperCase = await service.ValidateAsync("recording.FLAC",
        new(FileTranscriptionRoute.Local, "base", LocalTranscriptionEngine.Whisper));
    Assert(upperCase.IsSuccess, "case-insensitive portable format was rejected");
    metadata.Value = null;
    AssertCode(await service.ValidateAsync("recording.wav",
        new(FileTranscriptionRoute.Local, "base", LocalTranscriptionEngine.Whisper)),
        "file_preflight.file_not_found");
}

static async Task FailurePrivacy()
{
    const string sensitivePath = "/private/Ray client meeting.wav";
    const string sensitiveModel = "private-model-name";
    var result = await Service(new FakeMetadata()).ValidateAsync(sensitivePath,
        new(FileTranscriptionRoute.Cloud, sensitiveModel, CloudProvider: CloudTranscriptionProvider.OpenAi));
    var failure = result.Failure ?? throw new InvalidOperationException("privacy case unexpectedly passed");
    var rendered = $"{failure.Code} {failure.Message}";
    Assert(!rendered.Contains(sensitivePath, StringComparison.Ordinal)
        && !rendered.Contains(sensitiveModel, StringComparison.Ordinal)
        && !rendered.Contains("credential-value", StringComparison.Ordinal),
        "failure exposed local content");
}

static async Task SparseWaveMetadata()
{
    var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-preflight-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "sparse.wav");
    try
    {
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var header = WaveHeader(dataBytes: 32_000, bytesPerSecond: 32_000);
            await stream.WriteAsync(header);
            stream.SetLength(1L * ByteSizes.GiB);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var metadata = await new StreamingFileAudioMetadataSource().ReadAsync(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(metadata is { LengthBytes: 1_073_741_824 } && metadata.Duration == TimeSpan.FromSeconds(1),
            "sparse WAV metadata was incorrect");
        Assert(allocated < 1_000_000 && timer.Elapsed < TimeSpan.FromSeconds(2),
            "metadata reader appears to buffer or scan the audio payload");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task MalformedWaveMetadata()
{
    var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-preflight-malformed-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var truncated = Path.Combine(root, "truncated.wav");
        await File.WriteAllBytesAsync(truncated, "RIFF"u8.ToArray());
        var value = await new StreamingFileAudioMetadataSource().ReadAsync(truncated);
        Assert(value is { LengthBytes: 4, Duration: null }, "truncated RIFF was misread");

        var oversized = Path.Combine(root, "oversized.wav");
        var bytes = new byte[20];
        "RIFF"u8.CopyTo(bytes); "WAVEdata"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), uint.MaxValue);
        await File.WriteAllBytesAsync(oversized, bytes);
        value = await new StreamingFileAudioMetadataSource().ReadAsync(oversized);
        Assert(value is { LengthBytes: 20, Duration: null }, "oversized RIFF chunk overflowed or was accepted");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task Cancellation()
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var result = await Service(new FakeMetadata { Value = new(1024, TimeSpan.FromSeconds(1)) }).ValidateAsync(
        "/private/Ray cancellation.wav",
        new(FileTranscriptionRoute.Local, "base", LocalTranscriptionEngine.Whisper), cancelled.Token);
    AssertCode(result, "file_preflight.cancelled");
    Assert(!result.Failure!.Message.Contains("Ray", StringComparison.Ordinal), "cancellation exposed a file name");
}

static byte[] WaveHeader(uint dataBytes, uint bytesPerSecond)
{
    var value = new byte[44];
    "RIFF"u8.CopyTo(value); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), dataBytes + 36);
    "WAVEfmt "u8.CopyTo(value.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(20), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(22), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(24), 16_000);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(28), bytesPerSecond);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(32), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(34), 16);
    "data"u8.CopyTo(value.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(40), dataBytes);
    return value;
}

static PortableFileTranscriptionPreflight Service(FakeMetadata metadata, bool account = false) =>
    new(metadata, new FakeLocalReadiness(), new FakeCredentials(api: !account, account: account));

static FileTranscriptionTarget MetaMuseTarget() => new(
    FileTranscriptionRoute.Cloud,
    "muse-voice-transcribe-1.0",
    CloudProvider: CloudTranscriptionProvider.HyperWhisperCloud,
    CloudCatalogTier: "metaMuse");

static IReadOnlyList<FileTranscriptionTarget> MetaMuseTargets() =>
[
    MetaMuseTarget(),
    new(FileTranscriptionRoute.Cloud, "muse-voice-transcribe-1.0",
        CloudProvider: CloudTranscriptionProvider.Meta),
];

static void AssertCode(FileTranscriptionPreflightResult result, string code) =>
    Assert(result.Failure?.Code == code, $"expected {code}, got {result.Failure?.Code ?? "success"}");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record ProviderLimit(
    CloudTranscriptionProvider Provider, string Model, long Bytes, bool Account, string? Tier = null);

static class ByteSizes
{
    public const long MiB = 1024L * 1024;
    public const long GiB = 1024L * 1024 * 1024;
}

sealed class FakeMetadata : IFileAudioMetadataSource
{
    public FileAudioMetadata? Value { get; set; }
    public int Calls { get; private set; }
    public ValueTask<FileAudioMetadata?> ReadAsync(string path, CancellationToken cancellationToken = default)
    { Calls++; cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Value); }
}

sealed class FakeLocalReadiness : ILocalFileTranscriptionReadiness
{
    public bool BackendAvailable { get; set; } = true;
    public bool ModelInstalled { get; set; } = true;
    public ValueTask<bool> IsBackendAvailableAsync(LocalTranscriptionEngine engine, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(BackendAvailable); }
    public ValueTask<bool> IsModelInstalledAsync(ManagedModel model, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(ModelInstalled); }
}

sealed class FakeCredentials(bool api, bool account, bool device = false) : ICloudCredentialSource
{
    public ValueTask<CloudCredential?> GetCredentialAsync(
        CloudTranscriptionProvider provider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CloudCredential?>(new(
            ApiKey: api ? "credential-value" : null,
            LicenseKey: account ? "credential-value" : null,
            DeviceId: account || device ? "device" : null));
    }
}
