using System.Collections.Concurrent;
using System.IO;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// The scratch files <c>POST /transcribe</c> writes: the decoded
/// <c>audio_base64</c> upload (<c>%TEMP%\hyperwhisper-localapi-*</c>) and the
/// <c>file</c> snapshot (<c>%TEMP%\HyperWhisper\local-api-transcribe\snapshot-*</c>).
/// <para>
/// The request's <c>finally</c> deletes its own file, but nothing ran it when
/// the app quit or was killed mid-request, so each interrupted request left a
/// file of up to the 48 MiB upload cap in %TEMP% for good (#1476). Two backstops
/// close that: <see cref="DeleteInFlight"/> runs at Quit, after the server has
/// had its bounded chance to finish, and <see cref="SweepLeftovers()"/> runs at
/// start-up for whatever a hard kill left behind.
/// </para>
/// <para>
/// Every in-flight scratch file is held open by its request with
/// <see cref="FileShare.Read"/> and no delete share, so a sweep from another
/// HyperWhisper process sharing %TEMP% (a second profile) cannot remove a file
/// a live request is still reading: the delete fails and the sweep skips it.
/// </para>
/// </summary>
internal static class LocalApiScratchFiles
{
    internal const string UploadPrefix = "hyperwhisper-localapi-";
    internal const string SnapshotPrefix = "snapshot-";

    private static readonly ConcurrentDictionary<string, FileStream?> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    internal static string UploadDirectory(string tempRoot) => tempRoot;

    internal static string SnapshotDirectory(string tempRoot) =>
        Path.Combine(tempRoot, "HyperWhisper", "local-api-transcribe");

    /// <summary>Record a scratch file a request has just written, with the read lock it holds on it.</summary>
    internal static void Track(string path, FileStream? readLock)
    {
        InFlight[path] = readLock;
    }

    /// <summary>The request is done with <paramref name="path"/>: forget it.</summary>
    internal static void Untrack(string path)
    {
        InFlight.TryRemove(path, out _);
    }

    internal static int InFlightCount => InFlight.Count;

    /// <summary>
    /// Delete every scratch file a request still holds. Called at Quit, once the
    /// server has stopped or its stop wait has run out, so the requests that are
    /// left will never reach their own <c>finally</c>. Releases each request's read
    /// lock first. A file the engine still has open stays, and the next start-up
    /// sweep removes it. Returns the number deleted.
    /// </summary>
    internal static int DeleteInFlight()
    {
        var deleted = 0;
        foreach (var entry in InFlight.ToArray())
        {
            try { entry.Value?.Dispose(); }
            catch (Exception ex)
            {
                LoggingService.Debug($"LocalAPI scratch: failed to release lock on {entry.Key}: {ex.Message}");
            }

            if (TryDelete(entry.Key))
            {
                deleted++;
                InFlight.TryRemove(entry.Key, out _);
            }
        }

        return deleted;
    }

    /// <summary>Remove the scratch files an earlier run left in %TEMP%.</summary>
    internal static int SweepLeftovers(DateTime writtenBeforeUtc) =>
        SweepLeftovers(Path.GetTempPath(), writtenBeforeUtc);

    /// <summary>
    /// Remove the scratch files an earlier run left under <paramref name="tempRoot"/>.
    /// Only the two names this app writes are touched, and only at the top of
    /// each directory. A file a live request holds (this process or another)
    /// fails to delete and is skipped. A file last written at or after
    /// <paramref name="writtenBeforeUtc"/> is skipped too: the sweep runs in the
    /// background at start-up, and a request this run's server has just accepted
    /// writes its upload a moment before it takes the lock. Returns the number
    /// deleted.
    /// </summary>
    internal static int SweepLeftovers(string tempRoot, DateTime writtenBeforeUtc)
    {
        if (string.IsNullOrWhiteSpace(tempRoot))
        {
            return 0;
        }

        var deleted = SweepDirectory(UploadDirectory(tempRoot), UploadPrefix + "*", writtenBeforeUtc)
            + SweepDirectory(SnapshotDirectory(tempRoot), SnapshotPrefix + "*", writtenBeforeUtc);
        if (deleted > 0)
        {
            LoggingService.Info($"LocalAPI scratch: removed {deleted} leftover /transcribe scratch file(s)");
        }

        return deleted;
    }

    private static int SweepDirectory(string directory, string pattern, DateTime writtenBeforeUtc)
    {
        string[] candidates;
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            candidates = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Debug($"LocalAPI scratch: could not list {directory}: {ex.Message}");
            return 0;
        }

        var deleted = 0;
        foreach (var path in candidates)
        {
            if (InFlight.ContainsKey(path))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(path) >= writtenBeforeUtc)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (TryDelete(path))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Debug($"LocalAPI scratch: could not delete {path}: {ex.Message}");
            return false;
        }
    }
}
