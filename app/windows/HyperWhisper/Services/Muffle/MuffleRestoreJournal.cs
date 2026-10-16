// MUFFLE RESTORE JOURNAL
// Windows persists every app's session volume, so a crash while muffling would leave, say, a music
// player at 0.01% for good. Each volume the muffler changes is written here BEFORE it is changed and
// removed once it is restored; at startup the leftovers are put back, and an entry whose session is
// not open is kept and retried whenever a session is created.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HyperWhisper.Services.Muffle;

public sealed record MuffleRestoreEntry(
    [property: JsonPropertyName("sessionIdentifier")] string SessionIdentifier,
    [property: JsonPropertyName("sessionInstanceIdentifier")] string SessionInstanceIdentifier,
    [property: JsonPropertyName("processId")] uint ProcessId,
    [property: JsonPropertyName("priorVolume")] float PriorVolume,
    [property: JsonPropertyName("setVolume")] float SetVolume,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    // True while the volume is being ramped between PriorVolume and SetVolume (either way), so a
    // crash mid-ramp leaves a value between the two that is still ours. Absent in older files.
    [property: JsonPropertyName("ramping")] bool Ramping = false);

public sealed class MuffleRestoreJournal : IDisposable
{
    /// <summary>An entry nobody could restore for this long is dropped (the app is long gone).</summary>
    internal static readonly TimeSpan StaleEntryLifetime = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<MuffleRestoreEntry> _live = new();
    private readonly List<MuffleRestoreEntry> _stale = new();
    private bool _loaded;
    private bool _disposed;
    private MMDeviceEnumerator? _watchEnumerator;
    private List<(MMDevice Device, AudioSessionManager.SessionCreatedDelegate Handler)>? _watched;

    public MuffleRestoreJournal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A journal path is required.", nameof(path));
        }

        FilePath = path;
    }

    public string FilePath { get; }

    public bool HasStaleEntries
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _stale.Count > 0;
            }
        }
    }

    /// <summary>Records a volume the muffler is about to set. Fails, and the caller must not change the volume, if the file cannot be written.</summary>
    public Result Add(MuffleRestoreEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _live.Add(entry);
            var saved = Save();
            if (saved.IsFailure)
            {
                _live.Remove(entry);
            }

            return saved;
        }
    }

    /// <summary>
    /// Replaces live entries in one write (a ramp starting or settling). The new entries are kept
    /// in memory even if the write fails; the next write puts them on disk.
    /// </summary>
    public Result Update(IReadOnlyList<(MuffleRestoreEntry Old, MuffleRestoreEntry New)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_gate)
        {
            EnsureLoaded();
            var changed = false;
            foreach (var (old, replacement) in changes)
            {
                var index = _live.IndexOf(old);
                if (index >= 0)
                {
                    _live[index] = replacement;
                    changed = true;
                }
            }

            return changed ? Save() : Result.Success();
        }
    }

    /// <summary>Forgets an entry once its volume is back (or deliberately left to the user).</summary>
    public void Remove(MuffleRestoreEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_live.Remove(entry))
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Hands an entry the muffler could not restore (its session is gone) to the leftovers, so it is
    /// retried when the app opens a session again, or at the next start.
    /// </summary>
    public void MarkStale(MuffleRestoreEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_live.Remove(entry))
            {
                _stale.Add(entry);
                Save();
            }
        }
    }

    /// <summary>
    /// Restores every leftover entry whose session is open on any active render endpoint.
    /// Returns how many volumes were put back.
    /// </summary>
    public int RecoverStale()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_stale.Count == 0)
            {
                return 0;
            }
        }

        var restored = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var deviceCount = devices.Count;
            for (var d = 0; d < deviceCount; d++)
            {
                MMDevice? device = null;
                try
                {
                    device = devices[d];
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    var sessionCount = sessions.Count;
                    for (var s = 0; s < sessionCount; s++)
                    {
                        try
                        {
                            if (TryRecoverStale(sessions[s]))
                            {
                                restored++;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Debug($"MuffleRestoreJournal: Skipped a session during recovery: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"MuffleRestoreJournal: Skipped an endpoint during recovery: {ex.Message}");
                }
                finally
                {
                    device?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MuffleRestoreJournal: Recovery enumeration failed: {ex.Message}");
        }

        lock (_gate)
        {
            if (DropExpiredStale(DateTimeOffset.UtcNow) > 0)
            {
                Save();
            }

            LoggingService.Info($"MuffleRestoreJournal: Startup recovery restored {restored} volume(s); {_stale.Count} entr(ies) wait for their app");
        }

        return restored;
    }

    /// <summary>
    /// Restores <paramref name="session"/> if a leftover entry matches it (instance identifier first,
    /// then session identifier for a re-opened app). Returns true when a volume was put back. The
    /// muffler calls this before it scales a session, so a crash leftover is never scaled twice.
    /// </summary>
    public bool TryRecoverStale(AudioSessionControl session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            EnsureLoaded();
            if (_stale.Count == 0)
            {
                return false;
            }

            string instanceId;
            string sessionId;
            try
            {
                instanceId = session.GetSessionInstanceIdentifier ?? string.Empty;
                sessionId = session.GetSessionIdentifier ?? string.Empty;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"MuffleRestoreJournal: Could not read a session's identifiers: {ex.Message}");
                return false;
            }

            // A session this process is muffling right now belongs to the live run, not the leftovers.
            if (!string.IsNullOrEmpty(instanceId)
                && _live.Any(e => string.Equals(e.SessionInstanceIdentifier, instanceId, StringComparison.Ordinal)))
            {
                return false;
            }

            var match = FindMatch(_stale, instanceId, sessionId);
            if (match == null)
            {
                return false;
            }

            float current;
            try
            {
                if (session.State == AudioSessionState.AudioSessionStateExpired)
                {
                    // A write to a closed session need not reach the app's next one.
                    return false;
                }

                current = session.SimpleAudioVolume.Volume;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"MuffleRestoreJournal: Could not read a leftover session's volume; will retry: {ex.Message}");
                return false;
            }

            var restored = false;
            if (HoldsOurVolume(match, current))
            {
                try
                {
                    session.SimpleAudioVolume.Volume = match.PriorVolume;
                    var expired = session.State == AudioSessionState.AudioSessionStateExpired;
                    var readBack = session.SimpleAudioVolume.Volume;
                    if (!RestoreLanded(expired, readBack, match.PriorVolume))
                    {
                        LoggingService.Info($"MuffleRestoreJournal: A leftover restore did not hold (expired {expired}, reads {readBack:G4}); will retry");
                        return false;
                    }

                    restored = true;
                    LoggingService.Info($"MuffleRestoreJournal: Restored a volume left by an earlier run (pid {match.ProcessId} -> {session.GetProcessID}, {current:G4} -> {match.PriorVolume:G4})");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"MuffleRestoreJournal: Could not restore a leftover volume; will retry: {ex.Message}");
                    return false;
                }
            }
            else
            {
                LoggingService.Info($"MuffleRestoreJournal: Leftover volume was changed since (now {current:G4}, set {match.SetVolume:G4}, ramping {match.Ramping}); leaving it");
            }

            _stale.Remove(match);
            Save();
            return restored;
        }
    }

    /// <summary>While leftovers remain, retries them whenever an app opens a session on any active endpoint.</summary>
    public void WatchForStaleSessions()
    {
        lock (_gate)
        {
            if (_disposed || _watched != null || !HasStaleEntriesLocked())
            {
                return;
            }

            _watched = new List<(MMDevice, AudioSessionManager.SessionCreatedDelegate)>();
            try
            {
                _watchEnumerator = new MMDeviceEnumerator();
                var devices = _watchEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                var deviceCount = devices.Count;
                for (var d = 0; d < deviceCount; d++)
                {
                    try
                    {
                        var device = devices[d];
                        AudioSessionManager.SessionCreatedDelegate handler = (_, newSession) => OnWatchedSessionCreated(newSession);
                        device.AudioSessionManager.OnSessionCreated += handler;
                        _watched.Add((device, handler));
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"MuffleRestoreJournal: Could not watch an endpoint: {ex.Message}");
                    }
                }

                LoggingService.Debug($"MuffleRestoreJournal: Watching {_watched.Count} endpoint(s) for apps with leftover volumes");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"MuffleRestoreJournal: Could not watch for leftover sessions: {ex.Message}");
            }
        }
    }

    private void OnWatchedSessionCreated(IAudioSessionControl newSession)
    {
        // The notification thread must return at once; the restore runs on the pool.
        var control = new AudioSessionControl(newSession);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                TryRecoverStale(control);
                if (!HasStaleEntries)
                {
                    StopWatching();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"MuffleRestoreJournal: Leftover retry failed: {ex.Message}");
            }
        });
    }

    private void StopWatching()
    {
        List<(MMDevice Device, AudioSessionManager.SessionCreatedDelegate Handler)>? watched;
        MMDeviceEnumerator? enumerator;
        lock (_gate)
        {
            watched = _watched;
            enumerator = _watchEnumerator;
            _watched = null;
            _watchEnumerator = null;
        }

        if (watched == null)
        {
            return;
        }

        foreach (var (device, handler) in watched)
        {
            try
            {
                device.AudioSessionManager.OnSessionCreated -= handler;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"MuffleRestoreJournal: Unsubscribe failed: {ex.Message}");
            }
            finally
            {
                try { device.Dispose(); } catch (Exception ex) { LoggingService.Debug($"MuffleRestoreJournal: Device dispose failed: {ex.Message}"); }
            }
        }

        try { enumerator?.Dispose(); } catch (Exception ex) { LoggingService.Debug($"MuffleRestoreJournal: Enumerator dispose failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopWatching();
    }

    internal static MuffleRestoreEntry? FindMatch(IReadOnlyList<MuffleRestoreEntry> entries, string instanceId, string sessionId)
    {
        if (!string.IsNullOrEmpty(instanceId))
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.SessionInstanceIdentifier, instanceId, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.SessionIdentifier, sessionId, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="current"/> is a volume the muffler left: its set volume, or, for an
    /// entry journaled mid-ramp, anything between the prior and the set volume. Anything else was
    /// changed by the user and is left alone.
    /// </summary>
    internal static bool HoldsOurVolume(MuffleRestoreEntry entry, float current)
    {
        if (StillAtSetVolume(current, entry.SetVolume))
        {
            return true;
        }

        if (!entry.Ramping)
        {
            return false;
        }

        var low = Math.Min(entry.PriorVolume, entry.SetVolume);
        var high = Math.Max(entry.PriorVolume, entry.SetVolume);
        return current >= low - Tolerance(low) && current <= high + Tolerance(high);
    }

    /// <summary>
    /// True when a restore write can be trusted: the session was still open after the write and
    /// reads back the prior volume. A session that expires mid-restore can take a write without
    /// keeping it for the app's next session, so otherwise the entry is kept for a retry.
    /// </summary>
    internal static bool RestoreLanded(bool expiredAfterWrite, float readBack, float prior) =>
        !expiredAfterWrite && StillAtSetVolume(readBack, prior);

    /// <summary>True when FindMatch can ever match the entry (it has an instance or a session identifier).</summary>
    internal static bool IsMatchable(MuffleRestoreEntry entry) =>
        !string.IsNullOrEmpty(entry.SessionInstanceIdentifier) || !string.IsNullOrEmpty(entry.SessionIdentifier);

    /// <summary>True while a session still holds (within float noise) the volume the muffler set.</summary>
    internal static bool StillAtSetVolume(float current, float set) =>
        Math.Abs(current - set) <= Tolerance(set);

    private static float Tolerance(float volume) => Math.Max(1e-7f, Math.Abs(volume) * 1e-3f);

    internal static List<MuffleRestoreEntry> Parse(string json) =>
        TryParse(json, out var entries, out _) ? entries : new List<MuffleRestoreEntry>();

    /// <summary>
    /// False when <paramref name="json"/> is not a journal (empty, torn or garbled): the file must
    /// then be kept aside, not overwritten, since it may be the only record of lowered volumes.
    /// </summary>
    internal static bool TryParse(string json, out List<MuffleRestoreEntry> entries, out string? error)
    {
        entries = new List<MuffleRestoreEntry>();
        error = null;
        try
        {
            var file = JsonSerializer.Deserialize<JournalFile>(json, JsonOptions);
            if (file == null)
            {
                error = "the file holds no journal";
                return false;
            }

            // FindMatch can use either identifier, so an entry is kept while it has at least one.
            entries = file.Entries?.Where(e => e != null && IsMatchable(e)).ToList() ?? entries;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Where an unreadable journal is moved, so a later write cannot destroy it.</summary>
    internal static string CorruptCopyPath(string path, DateTimeOffset now) =>
        $"{path}.corrupt-{now.UtcDateTime:yyyyMMdd-HHmmss-fff}";

    internal static string Serialize(IEnumerable<MuffleRestoreEntry> entries) =>
        JsonSerializer.Serialize(new JournalFile { Version = 1, Entries = entries.ToList() }, JsonOptions);

    private bool HasStaleEntriesLocked()
    {
        EnsureLoaded();
        return _stale.Count > 0;
    }

    private int DropExpiredStale(DateTimeOffset now) =>
        _stale.RemoveAll(e => now - e.CreatedUtc > StaleEntryLifetime);

    // Everything on disk when this process first touches the journal is a leftover of an earlier
    // run (the app is single-instance); it is loaded before the first write so nothing is lost.
    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                if (!TryParse(File.ReadAllText(FilePath), out var entries, out var error))
                {
                    PreserveCorruptFile(error);
                    return;
                }

                _stale.AddRange(entries);
                if (_stale.Count > 0)
                {
                    LoggingService.Info($"MuffleRestoreJournal: Found {_stale.Count} volume(s) an earlier run did not restore");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MuffleRestoreJournal: Could not read {Path.GetFileName(FilePath)}: {ex.Message}");
        }
    }

    // An unreadable journal may be the only record of volumes left at -80 dB, so it is moved aside
    // (for a person or a later fix to read) instead of being treated as empty and overwritten.
    private void PreserveCorruptFile(string? error)
    {
        var target = CorruptCopyPath(FilePath, DateTimeOffset.UtcNow);
        try
        {
            File.Move(FilePath, target);
            LoggingService.Error($"MuffleRestoreJournal: {Path.GetFileName(FilePath)} is unreadable ({error}); kept it as {Path.GetFileName(target)}. App volumes it recorded are not restored automatically");
        }
        catch (Exception moveEx)
        {
            try
            {
                File.Copy(FilePath, target, overwrite: false);
                LoggingService.Error($"MuffleRestoreJournal: {Path.GetFileName(FilePath)} is unreadable ({error}); could not move it ({moveEx.Message}), copied it to {Path.GetFileName(target)}");
            }
            catch (Exception copyEx)
            {
                LoggingService.Error($"MuffleRestoreJournal: {Path.GetFileName(FilePath)} is unreadable ({error}) and could not be kept aside: {copyEx.Message}");
            }
        }
    }

    private Result Save()
    {
        try
        {
            if (_live.Count == 0 && _stale.Count == 0)
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                return Result.Success();
            }

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // The bytes reach the disk before the rename, so a power cut leaves the old journal or
            // the new one, never a torn or empty file in its place.
            var temp = FilePath + ".tmp";
            var bytes = System.Text.Encoding.UTF8.GetBytes(Serialize(_stale.Concat(_live)));
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, FilePath, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MuffleRestoreJournal: Could not write {Path.GetFileName(FilePath)}: {ex.Message}");
            return Result.Failure("Could not write the muffle restore journal", ex);
        }
    }

    private sealed class JournalFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("entries")]
        public List<MuffleRestoreEntry>? Entries { get; set; }
    }
}
