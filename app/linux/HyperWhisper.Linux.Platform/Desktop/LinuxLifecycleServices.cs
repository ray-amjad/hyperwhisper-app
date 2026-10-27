using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

// One instance per database (#1334). The guard is an exclusive flock on a file beside
// hyperwhisper.db: the kernel grants it to one process at a time and drops it when that process
// dies, so there is no stale state to race over and no XDG_RUNTIME_DIR split. The socket only
// carries the activation signal. Its name is per database, only the lock's holder replaces it,
// and the holder writes its path into the lock file so a launch with another runtime dir finds it.
public sealed class LinuxSingleInstanceCoordinator : ISingleInstanceCoordinator
{
    // Builds before #1334 take no lock and listen here.
    private readonly string _legacySocketPath;
    private readonly string _lockPath;
    private SafeFileHandle? _lock;
    private Socket? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _listenTask;
    private bool _disposed;
    public LinuxSingleInstanceCoordinator(IAppPaths paths)
    {
        var state = Path.GetFullPath(paths.StateDirectory);
        // 8 hex digits tell one user's profiles apart and keep the path well inside sun_path's 108 bytes.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))).ToLowerInvariant()[..8];
        SocketPath = Path.Combine(paths.RuntimeDirectory, $"instance-{key}.sock");
        _legacySocketPath = Path.Combine(paths.RuntimeDirectory, "instance.sock");
        _lockPath = Path.Combine(state, "instance.lock");
    }
    public LinuxSingleInstanceCoordinator() : this(new LinuxAppPaths()) { }
    internal string SocketPath { get; }
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(3);
    public event EventHandler? ActivationRequested;
    public PlatformResult<bool> TryAcquire()
    {
        if (_disposed) return PlatformResult<bool>.Failure("single_instance.disposed", "The coordinator is disposed.");
        if (_listener is not null) return PlatformResult<bool>.Success(true);
        SafeFileHandle? profileLock = null;
        Socket? listener = null;
        try
        {
            const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!, privateDirectory);
            var locked = TryLockProfile(_lockPath, out profileLock);
            if (!locked) return PlatformResult<bool>.Success(false);
            // A socket that answers at the old name belongs to a build from before #1334, which
            // takes no lock. Defer to it, as that build would have deferred to us.
            if (CanConnect(_legacySocketPath)) { profileLock?.Dispose(); return PlatformResult<bool>.Success(false); }
            Directory.CreateDirectory(Path.GetDirectoryName(SocketPath)!, privateDirectory);
            // Holding the lock, anything at our per-database path is a dead socket. With no lock
            // (no flock support) only a socket nobody answers is ours to replace.
            if (profileLock is null && CanConnect(SocketPath)) return PlatformResult<bool>.Success(false);
            File.Delete(SocketPath);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            if (profileLock is not null) RandomAccess.Write(profileLock, Encoding.UTF8.GetBytes(SocketPath + "\n"), 0);
            _lock = profileLock; _listener = listener; _cancellation = new(); _listenTask = Task.Run(() => ListenAsync(_cancellation.Token));
            return PlatformResult<bool>.Success(true);
        }
        catch
        {
            listener?.Dispose();
            profileLock?.Dispose();
            return PlatformResult<bool>.Failure("single_instance.acquire_failed", "The instance socket could not be acquired.");
        }
    }
    // False when another process holds the lock. Raw open + flock, because a FileStream takes its
    // own flock (LOCK_SH unless FileShare.None) and two shared holders can never both upgrade. A
    // filesystem with no flock (an NFS mount with nolock) keeps the pre-#1334 socket-only guard
    // rather than refusing to start: the handle comes back null and the caller still binds.
    private static bool TryLockProfile(string path, out SafeFileHandle? handle)
    {
        handle = null;
        var fd = OpenFile(path, OpenReadWrite | OpenCreate | OpenCloseOnExec, 0b110_000_000);
        if (fd < 0) throw new IOException($"open failed: errno {Marshal.GetLastPInvokeError()}");
        var opened = new SafeFileHandle(fd, ownsHandle: true);
        while (true)
        {
            if (FileLock(fd, LockExclusive | LockNonBlocking) == 0) { handle = opened; return true; }
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ErrorInterrupted) continue;
            opened.Dispose();
            return errno != ErrorWouldBlock;
        }
    }
    // The holder's socket path, from the lock file; null when there is none yet.
    private string? ReadOwnerSocketPath()
    {
        var fd = OpenFile(_lockPath, OpenReadOnly | OpenCloseOnExec, 0);
        if (fd < 0) return null;
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        var buffer = new byte[4096];
        var read = RandomAccess.Read(handle, buffer, 0);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var end = text.IndexOf('\n');
        var path = end < 0 ? null : text[..end];
        return path is not null && Path.IsPathFullyQualified(path) && path.EndsWith(".sock", StringComparison.Ordinal) ? path : null;
    }
    // Linux values, the same on x86-64 and arm64.
    private const int OpenReadOnly = 0x0, OpenReadWrite = 0x2, OpenCreate = 0x40, OpenCloseOnExec = 0x80000;
    private const int LockExclusive = 2, LockNonBlocking = 4, ErrorInterrupted = 4, ErrorWouldBlock = 11;
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int FileLock(int fd, int operation);
    // The owner binds its socket a moment after it takes the lock, so a launch that lost the lock
    // retries for a few seconds instead of failing the handoff in that window.
    public PlatformResult SignalExistingInstance()
    {
        if (_disposed) return PlatformResult.Failure("single_instance.disposed", "The coordinator is disposed.");
        var deadline = DateTime.UtcNow + SignalTimeout;
        while (true)
        {
            string?[] candidates = [ReadOwnerSocketPath(), SocketPath, _legacySocketPath];
            foreach (var path in candidates.Distinct())
                if (path is not null && TrySignal(path)) return PlatformResult.Success();
            if (DateTime.UtcNow >= deadline) return PlatformResult.Failure("single_instance.signal_failed", "The running instance could not be signalled.");
            Thread.Sleep(50);
        }
    }
    private static bool TrySignal(string path) => TryConnect(path, signal: true);
    private static bool CanConnect(string path) => TryConnect(path, signal: false);
    private static bool TryConnect(string path, bool signal)
    {
        try { using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); socket.Connect(new UnixDomainSocketEndPoint(path)); if (signal) socket.Send([1]); return true; }
        catch { return false; }
    }
    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener!.AcceptAsync(token).ConfigureAwait(false);
                var byteValue = new byte[1];
                if (await client.ReceiveAsync(byteValue, SocketFlags.None, token).ConfigureAwait(false) > 0) RaiseActivation();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { if (!token.IsCancellationRequested) continue; }
        }
    }
    private void RaiseActivation()
    { var handlers = ActivationRequested; if (handlers is null) return; foreach (EventHandler handler in handlers.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch { } }
    public void Release()
    {
        var owned = _listener is not null;
        _cancellation?.Cancel(); try { _listener?.Dispose(); } catch { } try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _listener = null; _listenTask = null; _cancellation?.Dispose(); _cancellation = null;
        if (owned) try { File.Delete(SocketPath); } catch { }
        // Unlock only after the socket is gone, so the next holder never loses its fresh socket to
        // this delete. The lock file itself stays: unlinking it would let two processes lock two inodes.
        _lock?.Dispose(); _lock = null;
    }
    public void Dispose() { if (_disposed) return; Release(); _disposed = true; ActivationRequested = null; }
}

public sealed class LinuxAutostartService : IAutostartService
{
    private const string OwnedMarker = "X-HyperWhisper-Autostart=true";
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly string _executable;
    private readonly IPrivateFileService _files;
    public LinuxAutostartService() : this(new LinuxAppPaths(), LaunchPath(Environment.ProcessPath ?? string.Empty, File.Exists), new LinuxPrivateFileService()) { }
    private LinuxAutostartService(LinuxAppPaths paths, string executable, IPrivateFileService files)
        : this(paths.XdgConfigHome, paths.ConfigDirectory, executable, files) { }

    // The .deb's /usr/bin/hyperwhisper sets MALLOC_ARENA_MAX before the runtime starts (#1088),
    // and ProcessPath is always the real binary, so a login start would skip it.
    internal static string LaunchPath(string processPath, Func<string, bool> exists) =>
        processPath == "/usr/lib/hyperwhisper/HyperWhisper" && exists("/usr/bin/hyperwhisper")
            ? "/usr/bin/hyperwhisper"
            : processPath;
    // The XDG Autostart spec reads only $XDG_CONFIG_HOME/autostart/. Builds before #2023 wrote the
    // entry under the app's own config folder, where no session looks, so Enable and Disable also
    // remove that stray copy.
    internal LinuxAutostartService(string xdgConfigHome, string appConfigDirectory, string executable, IPrivateFileService files)
    {
        _path = Path.Combine(xdgConfigHome, "autostart", "hyperwhisper.desktop");
        _legacyPath = Path.Combine(appConfigDirectory, "autostart", "hyperwhisper.desktop");
        _executable = executable;
        _files = files;
    }
    public PlatformResult<bool> IsEnabled()
    {
        var read = _files.ReadAllText(_path);
        if (read.IsFailure) return PlatformResult<bool>.Failure(read.Error!.Code, read.Error.Message);
        return PlatformResult<bool>.Success(read.Value is not null && read.Value.Contains("X-GNOME-Autostart-enabled=true", StringComparison.Ordinal));
    }
    public PlatformResult Enable()
    {
        if (!Path.IsPathFullyQualified(_executable) || !File.Exists(_executable)) return PlatformResult.Failure("autostart.executable_missing", "The application executable is unavailable.");
        var quoted = _executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal);
        var content = $"[Desktop Entry]\nType=Application\nName=HyperWhisper\nExec=\"{quoted}\"\nTerminal=false\nX-GNOME-Autostart-enabled=true\n{OwnedMarker}\n";
        var written = _files.WriteAllTextAtomically(_path, content);
        RemoveLegacyEntry();
        return written;
    }
    // Startup calls Disable on every launch while the switch is off. GNOME Tweaks and KDE's
    // Autostart page copy the .deb's launcher to this same path, so delete only an entry this
    // service wrote. Its own entry is owner-only and reads back; a world-readable copy fails the
    // private read with private_file_insecure, and any failed read counts as not ours.
    public PlatformResult Disable()
    {
        RemoveLegacyEntry();
        var read = _files.ReadAllText(_path);
        return read.IsSuccess && read.Value is not null && read.Value.Contains(OwnedMarker, StringComparison.Ordinal)
            ? _files.Delete(_path)
            : PlatformResult.Success();
    }
    // Best effort: no session reads the stray copy, so failing to delete it must not turn a
    // working switch into a reported failure.
    private void RemoveLegacyEntry() => _files.Delete(_legacyPath);
}
