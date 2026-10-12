using System.IO;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// The partial data a model download writes before it finishes:
/// <c>Models\ggml-*.bin.download</c> (Whisper), <c>Models\Parakeet\&lt;id&gt;.temp\</c>
/// (Parakeet / Qwen3 folder downloads) and <c>Models\LLM\&lt;file&gt;.download</c> (local LLMs).
/// <para>
/// A Cancel or a failed download deletes its own partial, but a kill, a crash or
/// a power loss mid-download never reaches that code, so the partial stayed on
/// disk for good unless the user downloaded the same model again (#1599). Quit
/// cancels running downloads (<see cref="ModelDownloadService.CancelAllAsync"/>),
/// and <see cref="SweepLeftovers(DateTime)"/> runs at start-up for whatever a
/// hard exit left behind.
/// </para>
/// <para>
/// Only the exact partial names the download services write for a catalog model
/// are touched (<c>ggml-&lt;type&gt;.bin.download</c>, <c>&lt;id&gt;.temp</c>,
/// <c>&lt;file&gt;.download</c>), and only at the top of each models folder. Any other
/// file or folder there, such as a user's own <c>notes.gguf.download</c>, is never
/// deleted: cleanup removes only files the app can prove are its own (#1445). A download that started at or after the
/// sweep's cutoff is skipped, and so is a file a running download still holds open
/// (the delete fails).
/// </para>
/// </summary>
internal static class ModelDownloadLeftovers
{
    /// <summary>The partial file name of each catalog Whisper model (WhisperModelService.DownloadModelAsync).</summary>
    internal static IEnumerable<string> WhisperPartialNames =>
        WhisperModelInfo.AllModels.Select(m => $"ggml-{m.Type}.bin.download");

    /// <summary>The partial folder name of each catalog Parakeet-family model (ParakeetModelService.DownloadModelAsync).</summary>
    internal static IEnumerable<string> ParakeetPartialNames =>
        ParakeetModelInfo.AllModels.Select(m => $"{m.Id}.temp");

    /// <summary>The partial file name of each catalog local LLM (LocalLlmModelService.DownloadModelAsync).</summary>
    internal static IEnumerable<string> LocalLlmPartialNames =>
        LocalLlmModelInfo.AllModels.Select(m => $"{m.FileName}.download");

    private static Task _sweep = Task.CompletedTask;

    /// <summary>
    /// Completes when the start-up sweep has finished. A download awaits it before
    /// it touches its partial path, so the sweep can never delete a partial that a
    /// download just recreated. It never faults.
    /// </summary>
    internal static Task WhenSwept => Volatile.Read(ref _sweep);

    /// <summary>
    /// Start the start-up sweep on a worker thread: a leftover can be gigabytes.
    /// Call it once, before any download can start.
    /// </summary>
    internal static Task StartSweep(DateTime writtenBeforeUtc)
    {
        var sweep = Task.Run(() =>
        {
            try { SweepLeftovers(writtenBeforeUtc); }
            catch (Exception ex) { LoggingService.Warn($"Model download leftover sweep failed: {ex.Message}"); }
        });
        Volatile.Write(ref _sweep, sweep);
        return sweep;
    }

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
        var deleted = SweepFiles(whisperDirectory, WhisperPartialNames, writtenBeforeUtc)
            + SweepDirectories(parakeetDirectory, ParakeetPartialNames, writtenBeforeUtc)
            + SweepFiles(localLlmDirectory, LocalLlmPartialNames, writtenBeforeUtc);
        if (deleted > 0)
        {
            LoggingService.Info($"ModelDownloadLeftovers: removed {deleted} partial model download(s) an earlier run left behind");
        }

        return deleted;
    }

    private static int SweepFiles(string directory, IEnumerable<string> names, DateTime writtenBeforeUtc)
    {
        var deleted = 0;
        foreach (var path in List(directory, names, directories: false))
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

    private static int SweepDirectories(string directory, IEnumerable<string> names, DateTime writtenBeforeUtc)
    {
        var deleted = 0;
        foreach (var path in List(directory, names, directories: true))
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

    private static string[] List(string directory, IEnumerable<string> names, bool directories)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return [];
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => Path.Combine(directory, name))
                .Where(path => directories ? Directory.Exists(path) : File.Exists(path))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Debug($"ModelDownloadLeftovers: could not list {directory}: {ex.Message}");
            return [];
        }
    }
}
