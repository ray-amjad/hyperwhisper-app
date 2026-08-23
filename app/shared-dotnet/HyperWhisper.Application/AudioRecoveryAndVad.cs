using System.Buffers.Binary;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.PortableApplication.Audio;

public sealed record WavePcmInfo(int SampleRate, short Channels, short BitsPerSample, long DataLength)
{
    public int BlockAlign => Channels * (BitsPerSample / 8);
    public double DurationSeconds => DataLength / (double)(SampleRate * BlockAlign);
}

public static class CanonicalPcmWave
{
    public const int HeaderSize = 44;

    public static PlatformResult<WavePcmInfo> InspectAndRepair(string path, bool repair)
    {
        try
        {
            using var stream = new FileStream(path, repair ? FileMode.Open : FileMode.Open,
                repair ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderSize)
                return PlatformResult<WavePcmInfo>.Failure("audio_recovery.truncated_header", "The WAV header is incomplete.");
            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)
                || !header[12..16].SequenceEqual("fmt "u8) || !header[36..40].SequenceEqual("data"u8)
                || BinaryPrimitives.ReadUInt16LittleEndian(header[20..]) != 1)
                return PlatformResult<WavePcmInfo>.Failure("audio_recovery.invalid_wave", "The file is not canonical PCM WAV audio.");
            var channels = checked((short)BinaryPrimitives.ReadUInt16LittleEndian(header[22..]));
            var sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..]));
            var bits = checked((short)BinaryPrimitives.ReadUInt16LittleEndian(header[34..]));
            if (channels <= 0 || sampleRate <= 0 || bits != 16)
                return PlatformResult<WavePcmInfo>.Failure("audio_recovery.unsupported_wave", "Only 16-bit PCM WAV audio can be recovered.");
            var actual = stream.Length - HeaderSize;
            var blockAlign = channels * (bits / 8);
            actual -= actual % blockAlign;
            if (actual <= 0)
                return PlatformResult<WavePcmInfo>.Failure("audio_recovery.empty_wave", "The recording contains no complete audio samples.");
            if (actual > uint.MaxValue - 36)
                return PlatformResult<WavePcmInfo>.Failure("audio_recovery.wave_too_large", "The recording is too large to recover safely.");
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
            if (declared != actual || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 36 + actual)
            {
                if (!repair)
                    return PlatformResult<WavePcmInfo>.Failure("audio_recovery.incomplete_header", "The WAV length header is incomplete.");
                stream.SetLength(HeaderSize + actual);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + actual)));
                BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)actual));
                stream.Position = 0;
                stream.Write(header);
                stream.Flush(flushToDisk: true);
            }
            return PlatformResult<WavePcmInfo>.Success(new(sampleRate, channels, bits, actual));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return PlatformResult<WavePcmInfo>.Failure("audio_recovery.read_failed", "The recording could not be inspected safely.");
        }
    }

    public static void WriteHeader(Stream stream, WavePcmInfo info, long dataLength)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + dataLength)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], checked((ushort)info.Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], checked((uint)info.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], checked((uint)(info.SampleRate * info.BlockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], checked((ushort)info.BlockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], checked((ushort)info.BitsPerSample));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)dataLength));
        stream.Position = 0;
        stream.Write(header);
    }
}

public sealed record CrashAudioRecoverySummary(int Recovered, int Quarantined, int Skipped);

public sealed class CrashAudioRecoveryService(
    IAppPaths paths,
    ITranscriptionHistoryStore history,
    Func<string?> activeRecordingPath,
    int maximumCandidates = 256)
{
    private readonly string _root = Path.GetFullPath(paths.RecordingsDirectory).TrimEnd(Path.DirectorySeparatorChar);
    private readonly ITranscriptionHistoryStore _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly Func<string?> _active = activeRecordingPath ?? throw new ArgumentNullException(nameof(activeRecordingPath));
    private readonly int _maximum = maximumCandidates is > 0 and <= 4096 ? maximumCandidates : throw new ArgumentOutOfRangeException(nameof(maximumCandidates));

    public async Task<CrashAudioRecoverySummary> RecoverAsync(CancellationToken cancellationToken = default)
    {
        CreatePrivateDirectory(_root);
        var existing = _history is HistoryRepository repository
            ? (await repository.ListAsync(cancellationToken)).SelectMany(item => new[] { item.AudioFilePath, item.TrimmedAudioFilePath })
                .Where(item => item is not null).Select(item => Path.GetFullPath(item!)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var active = NormalizeContained(_active());
        var recovered = 0;
        var quarantined = 0;
        var skipped = 0;
        foreach (var candidate in Directory.EnumerateFiles(_root, "recording-*.wav", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal).Take(_maximum))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = NormalizeContained(candidate);
            if (full is null || full == active || existing.Contains(full) || IsSymlink(full)) { skipped++; continue; }
            var inspected = CanonicalPcmWave.InspectAndRepair(full, repair: true);
            if (inspected.IsFailure)
            {
                if (Quarantine(full)) quarantined++; else skipped++;
                continue;
            }
            var transcript = new Transcript
            {
                Text = "Recovered recording — retry transcription when ready",
                Status = TranscriptStatus.Failed,
                FailedReason = "The application closed before this recording could be transcribed.",
                Date = File.GetLastWriteTimeUtc(full),
                Duration = inspected.Value!.DurationSeconds,
                AudioFilePath = full,
                TranscriptionProvider = "Recovered audio",
            };
            await _history.AddAsync(transcript, cancellationToken);
            existing.Add(full);
            recovered++;
        }
        return new(recovered, quarantined, skipped);
    }

    private string? NormalizeContained(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            return Path.GetDirectoryName(full) == _root ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return null; }
    }

    private static bool IsSymlink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private bool Quarantine(string path)
    {
        try
        {
            var directory = Path.Combine(_root, "quarantine");
            CreatePrivateDirectory(directory);
            var destination = Path.Combine(directory, $"recovery-{Guid.NewGuid():N}.wav");
            File.Move(path, destination);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

public interface IVoiceActivityDetector
{
    ValueTask ResetAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    ValueTask<PlatformResult<bool>> ContainsSpeechAsync(
        ReadOnlyMemory<float> mono16KhzPcm,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounded PCM detector fallback when the packaged Silero runtime is unavailable.</summary>
public sealed class PcmEnergyVoiceActivityDetector(float rootMeanSquareThreshold = 0.012f) : IVoiceActivityDetector
{
    private readonly float _threshold = rootMeanSquareThreshold is > 0 and < 1
        ? rootMeanSquareThreshold : throw new ArgumentOutOfRangeException(nameof(rootMeanSquareThreshold));

    public ValueTask<PlatformResult<bool>> ContainsSpeechAsync(
        ReadOnlyMemory<float> mono16KhzPcm,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mono16KhzPcm.Length is 0 or > 16000)
            return ValueTask.FromResult(PlatformResult<bool>.Failure("vad.window_invalid", "The VAD PCM window is invalid."));
        double energy = 0;
        foreach (var sample in mono16KhzPcm.Span) energy += sample * sample;
        return ValueTask.FromResult(PlatformResult<bool>.Success(
            Math.Sqrt(energy / mono16KhzPcm.Length) >= _threshold));
    }
}

public sealed record VoiceActivityTrimResult(string TranscriptionPath, string? TrimmedAudioPath, bool WasTrimmed, string Reason);

public sealed class PortableWaveVoiceActivityTrimmer(
    IVoiceActivityDetector detector,
    Func<bool>? enabled = null,
    double minimumDurationSeconds = 30,
    int maximumDurationSeconds = 7200) : IBatchAudioPreprocessor
{
    private const int WindowSamples = 512;
    private readonly IVoiceActivityDetector _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    private readonly Func<bool> _enabled = enabled ?? (() => true);

    public async Task<BatchAudioPreprocessResult> PreprocessAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!_enabled()) return new(path, null, "vad.disabled");
        var result = await TrimAsync(path, Path.GetDirectoryName(Path.GetFullPath(path))!, cancellationToken).ConfigureAwait(false);
        return new(result.TranscriptionPath, result.TrimmedAudioPath, result.Reason);
    }

    public async Task<VoiceActivityTrimResult> TrimAsync(string path, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var inspected = CanonicalPcmWave.InspectAndRepair(path, repair: false);
        if (inspected.IsFailure) return new(path, null, false, inspected.Error!.Code);
        var info = inspected.Value!;
        if (info.SampleRate != 16000 || info.Channels != 1 || info.BitsPerSample != 16)
            return new(path, null, false, "vad.unsupported_format");
        if (info.DurationSeconds < minimumDurationSeconds) return new(path, null, false, "vad.short_audio");
        if (info.DurationSeconds > maximumDurationSeconds) return new(path, null, false, "vad.audio_too_long");

        var output = Path.Combine(Path.GetFullPath(outputDirectory), $"vad-{Guid.NewGuid():N}.wav");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(outputDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var input = File.OpenRead(path);
            input.Position = CanonicalPcmWave.HeaderSize;
            var outputOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read,
                Options = FileOptions.WriteThrough,
            };
            if (OperatingSystem.IsLinux())
                outputOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var destination = new FileStream(output, outputOptions);
            destination.Position = CanonicalPcmWave.HeaderSize;
            var bytes = new byte[WindowSamples * 2];
            var samples = new float[WindowSamples];
            long written = 0;
            var speechWindows = 0;
            await _detector.ResetAsync(cancellationToken);
            while (input.Position < input.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await input.ReadAsync(bytes, cancellationToken);
                if (count <= 0) break;
                count -= count % 2;
                for (var index = 0; index < count / 2; index++)
                    samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * 2, 2)) / 32768f;
                var speech = await _detector.ContainsSpeechAsync(samples.AsMemory(0, count / 2), cancellationToken);
                if (speech.IsFailure) return Fallback(speech.Error!.Code);
                if (speech.Value != true) continue;
                await destination.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
                written += count;
                speechWindows++;
            }
            if (speechWindows == 0 || written == 0) return Fallback("vad.no_speech");
            CanonicalPcmWave.WriteHeader(destination, info, written);
            await destination.FlushAsync(cancellationToken);
            return new(output, output, true, "vad.trimmed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Fallback("vad.cancelled"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Fallback("vad.failed"); }

        VoiceActivityTrimResult Fallback(string reason)
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            return new(path, null, false, reason);
        }
    }
}
