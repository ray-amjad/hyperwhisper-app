// PENDING HISTORY: AN UNREADABLE FILE IS NEVER OVERWRITTEN (issue #2630)
//
// pending-history.json holds History rows the database refused, and nothing else
// does. When another process holds it with no sharing at load (an AV scan, a
// backup, an indexer), the store starts empty and tries to move the file aside
// to .unreadable. The same lock blocks the move, which used to be swallowed by a
// bare catch, so the next save wrote the new list over the old file and its rows
// were gone with no log line. These checks drive the real PendingHistoryStore
// over a file in a temp folder held with FileShare.None.

using System.IO;
using HyperWhisper.Data.Entities;
using HyperWhisper.Services;

namespace HyperWhisper.SmokeTests;

internal static class PendingHistoryUnreadableChecks
{
    private const string MoveFailedLine = "Could not move the unreadable pending History file";

    public static void RunAll(Action<string, Action> run)
    {
        run("history: a locked pending file is kept, not overwritten, and moved aside once free — issue #2630", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"pending-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var storePath = Path.Combine(folder, PendingHistoryStore.FileName);
            var asidePath = storePath + ".unreadable";
            var r1 = NewRow("percy2630first");
            var r2 = NewRow("percy2630second");
            var r3 = NewRow("percy2630third");
            try
            {
                Assert(new PendingHistoryStore(storePath).Upsert(r1), "session 1 could not save its pending row");

                var offsets = SnapshotLogOffsets();
                var store = new PendingHistoryStore(storePath);
                using (var held = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert(store.Count == 0, $"the store read {store.Count} row(s) from a file another process holds");
                    Assert(!File.Exists(asidePath), "the held file was moved aside while it was locked");

                    // A save while the old file is still unreadable must refuse, not write over it.
                    Assert(!store.Upsert(r2), "an Upsert reported the file saved while the unread file was still locked");
                    Assert(!store.Delete(new[] { r2.Id }), "a Delete that empties the list reported the file saved over the unread file");
                    Assert(store.Get(r2.Id) != null, "the refused Delete dropped the row from memory");
                    var stillOnDisk = ReadAll(held);
                    Assert(stillOnDisk.Contains(r1.Id.ToString(), StringComparison.OrdinalIgnoreCase),
                        "the locked file lost session 1's row while it was held");
                }

                // The lock is gone: the next change moves the old file aside, then saves.
                Assert(store.Upsert(r3), "the first Upsert after the lock was released did not save");
                Assert(File.Exists(asidePath), "the old file was not moved aside to .unreadable");
                var aside = File.ReadAllText(asidePath);
                Assert(aside.Contains(r1.Id.ToString(), StringComparison.OrdinalIgnoreCase),
                    "session 1's row is not in pending-history.json.unreadable");
                var live = File.ReadAllText(storePath);
                Assert(live.Contains(r2.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                        && live.Contains(r3.Id.ToString(), StringComparison.OrdinalIgnoreCase),
                    "the new file does not hold both of this session's rows");

                var log = ReadLogSince(offsets);
                Assert(log.Contains(MoveFailedLine, StringComparison.Ordinal),
                    $"no log line names the failed move-aside; captured: <<<{log}>>>");
                Assert(!log.Contains("percy2630", StringComparison.Ordinal),
                    "the log carries a pending row's text");
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            }
        });

        run("history: an unreadable pending file that is free to move is moved aside at load — issue #2630", () =>
        {
            // The common case (a corrupt file, no lock) keeps working as before: moved at load,
            // and the first save writes a fresh file.
            var folder = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"pending-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var storePath = Path.Combine(folder, PendingHistoryStore.FileName);
            try
            {
                File.WriteAllText(storePath, "{ this is not json");
                var store = new PendingHistoryStore(storePath);
                Assert(store.Count == 0, $"a corrupt file read as {store.Count} row(s)");
                Assert(File.Exists(storePath + ".unreadable") && !File.Exists(storePath),
                    "the corrupt file was not moved aside at load");
                var row = NewRow("percy2630corrupt");
                Assert(store.Upsert(row), "the first save after a moved-aside corrupt file failed");
                Assert(File.ReadAllText(storePath).Contains(row.Id.ToString(), StringComparison.OrdinalIgnoreCase),
                    "the new pending file does not hold the row");

                // A second unreadable file must not replace the first .unreadable copy, whose
                // rows exist nowhere else.
                const string earlier = "earlier unreadable copy";
                File.WriteAllText(storePath + ".unreadable", earlier);
                File.WriteAllText(storePath, "{ corrupt again");
                Assert(new PendingHistoryStore(storePath).Count == 0, "the second corrupt file read as rows");
                Assert(File.ReadAllText(storePath + ".unreadable") == earlier,
                    "the second move-aside overwrote the earlier .unreadable copy");
                var stamped = Directory.GetFiles(folder, PendingHistoryStore.FileName + ".unreadable.*");
                Assert(stamped.Length == 1 && File.ReadAllText(stamped[0]) == "{ corrupt again",
                    $"the second unreadable file was not kept beside the first ({stamped.Length} stamped copies)");
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            }
        });
    }

    private static Transcript NewRow(string mode) => new()
    {
        Id = Guid.NewGuid(),
        Date = DateTime.UtcNow,
        Duration = 1.0,
        Mode = mode,
        Status = TranscriptStatus.Completed,
        Text = mode + " text",
    };

    private static string ReadAll(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, long> SnapshotLogOffsets()
    {
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(LoggingService.LogDirectory)) return offsets;
        foreach (var file in Directory.GetFiles(LoggingService.LogDirectory))
            offsets[file] = new FileInfo(file).Length;
        return offsets;
    }

    private static string ReadLogSince(Dictionary<string, long> offsets)
    {
        if (!Directory.Exists(LoggingService.LogDirectory)) return string.Empty;
        var files = Directory.GetFiles(LoggingService.LogDirectory);
        Array.Sort(files, StringComparer.Ordinal);
        var captured = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var from = offsets.TryGetValue(file, out var offset) ? offset : 0L;
                if (from >= stream.Length) continue;
                stream.Position = from;
                using var reader = new StreamReader(stream);
                captured.Append(reader.ReadToEnd());
            }
            catch (IOException) { }
        }
        return captured.ToString();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
