using System.IO;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

/// <summary>
/// The file-handling half of a single-file model download, shared by
/// <see cref="WhisperModelService"/> (GGML) and <see cref="LocalLlmModelService"/> (GGUF).
/// The HTTP, progress and install-size rules stay with each service: they differ on
/// purpose (Whisper demands the exact catalog size, #1598).
/// </summary>
internal static class ModelDownloader
{
    internal const int MoveMaxAttempts = 6;

    internal static void EnsureDirectory(string directory, string ownerName)
    {
        if (!Directory.Exists(directory))
        {
            LoggingService.Info($"{ownerName}: Creating models directory: {directory}");
            Directory.CreateDirectory(directory);
        }
    }

    internal static void CleanupFile(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Refuses a download that cannot fit: the drive holding <paramref name="directory"/>
    /// needs 110% of <paramref name="modelBytes"/> free. A failed check of the drive itself
    /// lets the download go ahead.
    /// </summary>
    internal static Result EnsureDiskSpace(string directory, long modelBytes, string displayName, string ownerName)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            if (string.IsNullOrEmpty(root))
            {
                return Result.Success();
            }

            var driveInfo = new DriveInfo(root);
            var requiredBytes = (long)(modelBytes * 1.1);
            if (driveInfo.AvailableFreeSpace >= requiredBytes)
            {
                return Result.Success();
            }

            var needed = ByteSizeFormatter.FormatCompact(requiredBytes);
            var available = ByteSizeFormatter.FormatCompact(driveInfo.AvailableFreeSpace);
            return Result.Failure(Loc.S("settings.models.localLlm.diskSpace", displayName, needed, available));
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"{ownerName}: Disk space check failed: {ex.Message}");
            return Result.Success();
        }
    }

    /// <summary>
    /// Moves a finished download into place, replacing any older file. A virus scanner
    /// or indexer can hold the fresh temp file for a moment, so an <see cref="IOException"/>
    /// or <see cref="UnauthorizedAccessException"/> is retried with a growing delay
    /// (250 ms, 500 ms, ...) before the last attempt's exception is let through.
    /// </summary>
    internal static async Task MoveDownloadedFileAsync(
        string tempPath,
        string finalPath,
        string ownerName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Move(tempPath, finalPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                attempt < MoveMaxAttempts &&
                ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Warn($"{ownerName}: Model finalization attempt {attempt} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }
    }

    internal static bool HasMagicHeader(string path, ReadOnlySpan<byte> magic)
    {
        Span<byte> header = stackalloc byte[magic.Length];
        using var stream = File.OpenRead(path);
        var bytesRead = stream.Read(header);
        return bytesRead == magic.Length && header.SequenceEqual(magic);
    }
}
