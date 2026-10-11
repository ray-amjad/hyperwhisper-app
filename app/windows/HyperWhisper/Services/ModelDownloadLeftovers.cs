using System.IO;

namespace HyperWhisper.Services;

/// <summary>
/// The partial data a model download writes before it finishes:
/// <c>Models\ggml-*.bin.download</c> (Whisper), <c>Models\Parakeet\&lt;id&gt;.temp\</c>
/// (Parakeet / Qwen3 folder downloads) and <c>Models\LLM\*.download</c> (local LLMs).
/// <para>
/// A Cancel or a failed download deletes its own partial, but a kill, a crash or
/// a power loss mid-download never reaches that code, so the partial stayed on
/// disk for good unless the user downloaded the same model again (#1599). Quit
/// cancels running downloads (<see cref="ModelDownloadService.CancelAllAsync"/>),
/// and <see cref="SweepLeftovers(DateTime)"/> runs at start-up for whatever a
/// hard exit left behind.
/// </para>
/// <para>
/// Only the three names the download services write are touched, and only at the
/// top of each app-owned models folder. A download that started at or after the
/// sweep's cutoff is skipped, and so is a file a running download still holds open
/// (the delete fails).
/// </para>
/// </summary>
internal static class ModelDownloadLeftovers
{
    internal const string WhisperPattern = "ggml-*.bin.download";
    internal const string ParakeetPattern = "*.temp";
    internal const string LocalLlmPattern = "*.download";

    /// <summary>Remove the partial downloads an earlier run left under the models folder.</summary>
    internal static int SweepLeftovers(DateTime writtenBeforeUtc) =>
        SweepLeftovers(
            WhisperModelService.ModelsDirectory,
            ParakeetModelService.ModelsDirectory,
            LocalLlmModelService.ModelsDirectory,
            writtenBeforeUtc);

    /// <summary>
    /// Remove the partial downloads an earlier run left in the three model folders.
    /// Returns the number of partials (files or folders) deleted.
    /// </summary>
    internal static int SweepLeftovers(
        string whisperDirectory,
        string parakeetDirectory,
        string localLlmDirectory,
        DateTime writtenBeforeUtc)
    {
        var deleted = SweepFiles(whisperDirectory, WhisperPattern, writtenBeforeUtc)
            + SweepDirectories(parakeetDirectory, ParakeetPattern, writtenBeforeUtc)
            + SweepFiles(localLlmDirectory, LocalLlmPattern, writtenBeforeUtc);
        if (deleted > 0)
        {
            LoggingService.Info($"ModelDownloadLeftovers: removed {deleted} partial model download(s) an earlier run left behind");
        }

        return deleted;
    }

    private static int SweepFiles(string directory, string pattern, DateTime writtenBeforeUtc)
    {
        var deleted = 0;
        foreach (var path in List(directory, pattern, directories: false))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) >= writtenBeforeUtc)
                {
                    continue;
                }

                File.Delete(path);
                if (!File.Exists(path))
                {
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Debug($"ModelDownloadLeftovers: could not delete {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return deleted;
    }

    private static int SweepDirectories(string directory, string pattern, DateTime writtenBeforeUtc)
    {
        var deleted = 0;
        foreach (var path in List(directory, pattern, directories: true))
        {
            try
            {
                // A download that started after the cutoff recreated this folder,
                // or is writing a file in it right now.
                if (Directory.GetCreationTimeUtc(path) >= writtenBeforeUtc
                    || Directory.GetLastWriteTimeUtc(path) >= writtenBeforeUtc)
                {
                    continue;
                }

                Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path))
                {
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Debug($"ModelDownloadLeftovers: could not delete {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return deleted;
    }

    private static string[] List(string directory, string pattern, bool directories)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return [];
            }

            return directories
                ? Directory.GetDirectories(directory, pattern, SearchOption.TopDirectoryOnly)
                : Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Debug($"ModelDownloadLeftovers: could not list {directory}: {ex.Message}");
            return [];
        }
    }
}
