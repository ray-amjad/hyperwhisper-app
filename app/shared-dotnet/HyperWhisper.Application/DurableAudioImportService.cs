using System.Security.Cryptography;
using HyperWhisper.AudioNormalization;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed class DurableAudioImportService
{
    private const int CopyBufferBytes = 128 * 1024;
    private readonly IPrivateFileService _privateFiles;
    private readonly string _recordingsDirectory;
    private readonly IAudioNormalizationService _normalizer;

    public DurableAudioImportService(
        IPrivateFileService privateFiles,
        IAppPaths paths,
        long maximumBytes = 1_073_741_824,
        IAudioNormalizationService? normalizer = null)
    {
        _privateFiles = privateFiles ?? throw new ArgumentNullException(nameof(privateFiles));
        _recordingsDirectory = Path.GetFullPath(
            (paths ?? throw new ArgumentNullException(nameof(paths))).RecordingsDirectory);
        _normalizer = normalizer ?? new FfmpegAudioNormalizationService(new FfmpegAudioNormalizationOptions
        {
            MaximumInputBytes = maximumBytes,
            MaximumOutputBytes = maximumBytes
        });
    }

    public async Task<PlatformResult<string>> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
        => await ImportAsync(sourcePath, progress: null, cancellationToken);

    public async Task<PlatformResult<string>> ImportAsync(
        string sourcePath,
        IProgress<AudioNormalizationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        string? normalizedPath = null;
        try
        {
            var normalized = await _normalizer.NormalizeAsync(sourcePath, _recordingsDirectory, progress, cancellationToken);
            if (normalized.IsFailure) return normalized;
            var candidate = Path.GetFullPath(normalized.Value!);
            var relative = Path.GetRelativePath(_recordingsDirectory, candidate);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.IsPathFullyQualified(relative))
                return PlatformResult<string>.Failure("audio_import.invalid_output_path", "The normalized audio destination was invalid.");
            normalizedPath = candidate;
            if (!File.Exists(normalizedPath) || !string.Equals(Path.GetExtension(normalizedPath), ".wav", StringComparison.OrdinalIgnoreCase))
                return PlatformResult<string>.Failure("audio_import.invalid_output", "The normalized audio file was invalid.");
            var restricted = _privateFiles.IsRestrictedToCurrentUser(normalizedPath);
            if (restricted.IsFailure || restricted.Value != true)
            {
                return PlatformResult<string>.Failure("audio_import.permissions", "The normalized audio could not be made private.");
            }
            var completedPath = normalizedPath;
            normalizedPath = null;
            return PlatformResult<string>.Success(completedPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PlatformResult<string>.Failure("audio_import.failed", "The audio file could not be imported.");
        }
        finally
        {
            if (normalizedPath is not null)
            {
                try { File.Delete(normalizedPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Copies a cloud import byte-for-byte into owner-only app storage without
    /// decoding or changing its provider-native container. The source remains
    /// caller-owned and is never deleted.
    /// </summary>
    public async Task<PlatformResult<string>> ImportOriginalAsync(
        string sourcePath,
        long maximumBytes,
        IProgress<AudioNormalizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0)
            return PlatformResult<string>.Failure("audio_import.invalid_limit", "The provider upload limit is invalid.");
        string? destination = null;
        byte[]? buffer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new FileInfo(Path.GetFullPath(sourcePath));
            if (!source.Exists)
                return PlatformResult<string>.Failure("audio_import.file_not_found", "The selected audio file no longer exists.");
            if (source.Length <= 0 || source.Length > maximumBytes)
                return PlatformResult<string>.Failure("audio_import.invalid_size", "The audio file is empty or exceeds the provider upload limit.");
            Directory.CreateDirectory(_recordingsDirectory);
            var extension = Path.GetExtension(source.FullName).ToLowerInvariant();
            destination = Path.Combine(_recordingsDirectory, $"import-{Guid.NewGuid():N}{extension}");
            var destinationOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = CopyBufferBytes,
            };
            if (!OperatingSystem.IsWindows())
                destinationOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != source.Length || input.Length > maximumBytes)
                return PlatformResult<string>.Failure("audio_import.source_changed", "The selected audio file changed before import.");
            await using (var output = new FileStream(destination, destinationOptions))
            {
                buffer = new byte[CopyBufferBytes];
                long copied = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    copied = checked(copied + read);
                    if (copied > maximumBytes)
                        return PlatformResult<string>.Failure("audio_import.invalid_size", "The audio file exceeds the provider upload limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new AudioNormalizationProgress(
                        "staging", copied, source.Length, Math.Min(0.99, (double)copied / source.Length)));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (copied != source.Length || input.Length != source.Length)
                    return PlatformResult<string>.Failure("audio_import.source_changed", "The selected audio file changed during import.");
            }
            var restricted = _privateFiles.IsRestrictedToCurrentUser(destination);
            if (restricted.IsFailure || restricted.Value != true)
                return PlatformResult<string>.Failure("audio_import.permissions", "The imported audio could not be made private.");
            progress?.Report(new AudioNormalizationProgress("complete", source.Length, source.Length, 1));
            var completed = destination;
            destination = null;
            return PlatformResult<string>.Success(completed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or OverflowException or ArgumentException or NotSupportedException)
        {
            return PlatformResult<string>.Failure("audio_import.failed", "The audio file could not be imported.");
        }
        finally
        {
            if (buffer is not null)
                CryptographicOperations.ZeroMemory(buffer);
            if (destination is not null)
            {
                try { File.Delete(destination); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
