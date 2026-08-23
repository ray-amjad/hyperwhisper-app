using System.Diagnostics;
using System.Globalization;
using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.AudioNormalization;

public sealed record AudioNormalizationProgress(string Phase, long BytesRead, long TotalBytes, double Fraction);

public sealed record FfmpegAudioNormalizationOptions
{
    public string FfmpegExecutable { get; init; } = "ffmpeg";
    public string FfprobeExecutable { get; init; } = "ffprobe";
    public long MaximumInputBytes { get; init; } = 1_073_741_824;
    public long MaximumOutputBytes { get; init; } = 1_073_741_824;
}

public interface IAudioNormalizationService
{
    Task<PlatformResult<string>> NormalizeAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts supported audio into the canonical speech pipeline input: mono,
/// 16 kHz, signed 16-bit PCM WAV. The source is copied to a private staging
/// file first so FFmpeg never consumes a caller-controlled path after it has
/// passed validation.
/// </summary>
public sealed class FfmpegAudioNormalizationService : IAudioNormalizationService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".webm"
    };

    private readonly FfmpegAudioNormalizationOptions _options;

    public FfmpegAudioNormalizationService(FfmpegAudioNormalizationOptions? options = null)
    {
        _options = options ?? new FfmpegAudioNormalizationOptions();
        if (_options.MaximumInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "The input limit must be positive.");
        if (_options.MaximumOutputBytes <= 44) throw new ArgumentOutOfRangeException(nameof(options), "The output limit must hold a WAV header.");
        if (string.IsNullOrWhiteSpace(_options.FfmpegExecutable)) throw new ArgumentException("An FFmpeg executable is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.FfprobeExecutable)) throw new ArgumentException("An FFprobe executable is required.", nameof(options));
    }

    public async Task<PlatformResult<string>> NormalizeAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            return Failure("audio_normalization.invalid_path", "Choose a local audio file.");
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Path.IsPathFullyQualified(destinationDirectory))
            return Failure("audio_normalization.invalid_destination", "The audio destination is invalid.");
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension))
            return Failure("audio_normalization.unsupported_format", "Use a WAV, MP3, M4A, FLAC, OGG, or WebM audio file.");

        string? stagedPath = null;
        string? outputPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationDirectory(destinationDirectory);

            var sourceInfo = new FileInfo(Path.GetFullPath(sourcePath));
            if (!sourceInfo.Exists || sourceInfo.Length <= 0 || sourceInfo.Length > _options.MaximumInputBytes)
                return Failure("audio_normalization.invalid_size", "The audio file is empty or exceeds the import limit.");

            var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            stagedPath = Path.Combine(destinationDirectory, $".normalize-{operationId}{extension.ToLowerInvariant()}.partial");
            outputPath = Path.Combine(destinationDirectory, $"import-{operationId}.wav");
            await CopyToPrivateStagingAsync(sourceInfo.FullName, stagedPath, sourceInfo.Length, progress, cancellationToken);

            var durationSeconds = await ProbeDurationAsync(stagedPath, cancellationToken);
            CreatePrivateFile(outputPath);
            var transcode = await TranscodeAsync(stagedPath, outputPath, sourceInfo.Length, durationSeconds, progress, cancellationToken);
            if (transcode.IsFailure) return transcode;

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length <= 44 || outputInfo.Length >= _options.MaximumOutputBytes)
                return Failure("audio_normalization.invalid_output", "FFmpeg produced an invalid or oversized audio file.");
            if (!await IsCanonicalWaveAsync(outputPath, cancellationToken))
                return Failure("audio_normalization.invalid_output", "FFmpeg did not produce 16 kHz mono PCM audio.");

            RestrictFile(outputPath);
            progress?.Report(new AudioNormalizationProgress("complete", sourceInfo.Length, sourceInfo.Length, 1));
            var completedPath = outputPath;
            outputPath = null;
            return PlatformResult<string>.Success(completedPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("audio_normalization.io_failed", "The audio file could not be normalized.");
        }
        finally
        {
            DeleteBestEffort(stagedPath);
            DeleteBestEffort(outputPath);
        }
    }

    private async Task CopyToPrivateStagingAsync(
        string sourcePath,
        string stagedPath,
        long totalBytes,
        IProgress<AudioNormalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != totalBytes || source.Length > _options.MaximumInputBytes)
            throw new IOException("The source changed during validation.");
        await using var destination = OpenPrivateFile(stagedPath, FileMode.CreateNew);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            copied = checked(copied + read);
            if (copied > _options.MaximumInputBytes) throw new IOException("The source exceeds the configured limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new AudioNormalizationProgress("staging", copied, totalBytes, 0.1 * copied / totalBytes));
        }
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
    }

    private async Task<double?> ProbeDurationAsync(string stagedPath, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(_options.FfprobeExecutable);
        AddArguments(startInfo, "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", stagedPath);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var stdout = ReadBoundedAsync(process.StandardOutput, 4096, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, 4096, cancellationToken);
            await WaitForExitAsync(process, cancellationToken);
            var value = (await stdout).Trim();
            _ = await stderr;
            return process.ExitCode == 0
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                && duration > 0 && double.IsFinite(duration)
                    ? duration
                    : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private async Task<PlatformResult<string>> TranscodeAsync(
        string stagedPath,
        string outputPath,
        long totalInputBytes,
        double? durationSeconds,
        IProgress<AudioNormalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(_options.FfmpegExecutable);
        AddArguments(startInfo,
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", stagedPath,
            "-map_metadata", "-1", "-vn", "-sn", "-dn",
            "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
            "-fs", _options.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture),
            "-progress", "pipe:1", outputPath);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return Failure("audio_normalization.ffmpeg_unavailable", "FFmpeg could not be started.");
            var progressTask = ReadProgressAsync(process.StandardOutput, totalInputBytes, durationSeconds, progress, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, 64 * 1024, cancellationToken);
            await WaitForExitAsync(process, cancellationToken);
            await progressTask;
            _ = await stderrTask;
            if (process.ExitCode != 0)
                return Failure("audio_normalization.ffmpeg_failed", "FFmpeg could not decode the selected audio file.");
            return PlatformResult<string>.Success(outputPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failure("audio_normalization.ffmpeg_unavailable", "FFmpeg is not installed or could not be started.");
        }
    }

    private static async Task ReadProgressAsync(
        StreamReader reader,
        long totalInputBytes,
        double? durationSeconds,
        IProgress<AudioNormalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 1024 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
            if (!long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)) continue;
            var fraction = durationSeconds is > 0
                ? Math.Clamp(0.1 + (0.89 * microseconds / (durationSeconds.Value * 1_000_000)), 0.1, 0.99)
                : 0.5;
            progress?.Report(new AudioNormalizationProgress("transcoding", totalInputBytes, totalInputBytes, fraction));
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (result.Length < maximumCharacters)
                result.Append(buffer, 0, Math.Min(read, maximumCharacters - result.Length));
        }
        return result.ToString();
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            try
            {
                var runningProcess = (Process)state!;
                if (!runningProcess.HasExited) runningProcess.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }, process);
        await process.WaitForExitAsync(cancellationToken);
    }

    private static async Task<bool> IsCanonicalWaveAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[12];
        if (await ReadExactlyOrLessAsync(stream, header, cancellationToken) != header.Length
            || Encoding.ASCII.GetString(header, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(header, 8, 4) != "WAVE") return false;
        var chunkHeader = new byte[8];
        while (stream.Position + chunkHeader.Length <= stream.Length)
        {
            if (await ReadExactlyOrLessAsync(stream, chunkHeader, cancellationToken) != chunkHeader.Length) return false;
            var chunkLength = BitConverter.ToUInt32(chunkHeader, 4);
            if (Encoding.ASCII.GetString(chunkHeader, 0, 4) == "fmt ")
            {
                if (chunkLength < 16 || chunkLength > 4096) return false;
                var format = new byte[16];
                if (await ReadExactlyOrLessAsync(stream, format, cancellationToken) != format.Length) return false;
                return BitConverter.ToUInt16(format, 0) == 1
                    && BitConverter.ToUInt16(format, 2) == 1
                    && BitConverter.ToUInt32(format, 4) == 16_000
                    && BitConverter.ToUInt16(format, 14) == 16;
            }
            var skip = checked((long)chunkLength + (chunkLength & 1));
            if (skip > stream.Length - stream.Position) return false;
            stream.Seek(skip, SeekOrigin.Current);
        }
        return false;
    }

    private static async Task<int> ReadExactlyOrLessAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static ProcessStartInfo CreateStartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    private static FileStream OpenPrivateFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static void CreatePrivateFile(string path)
    {
        using var stream = OpenPrivateFile(path, FileMode.CreateNew);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void EnsureDestinationDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void DeleteBestEffort(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static PlatformResult<string> Failure(string code, string message) => PlatformResult<string>.Failure(code, message);
}
