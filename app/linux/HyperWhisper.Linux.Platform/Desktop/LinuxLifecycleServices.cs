using System.Net.Sockets;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public sealed class LinuxSingleInstanceCoordinator : ISingleInstanceCoordinator
{
    private readonly string _socketPath;
    private Socket? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _listenTask;
    private bool _disposed;
    public LinuxSingleInstanceCoordinator(IAppPaths paths) => _socketPath = Path.Combine(paths.RuntimeDirectory, "instance.sock");
    public LinuxSingleInstanceCoordinator() : this(new LinuxAppPaths()) { }
    public event EventHandler? ActivationRequested;
    public PlatformResult<bool> TryAcquire()
    {
        if (_disposed) return PlatformResult<bool>.Failure("single_instance.disposed", "The coordinator is disposed.");
        if (_listener is not null) return PlatformResult<bool>.Success(true);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { listener.Bind(new UnixDomainSocketEndPoint(_socketPath)); }
            catch (SocketException)
            {
                listener.Dispose();
                if (CanConnect()) return PlatformResult<bool>.Success(false);
                File.Delete(_socketPath);
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            }
            File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4); _listener = listener; _cancellation = new(); _listenTask = Task.Run(() => ListenAsync(_cancellation.Token));
            return PlatformResult<bool>.Success(true);
        }
        catch { return PlatformResult<bool>.Failure("single_instance.acquire_failed", "The instance socket could not be acquired."); }
    }
    public PlatformResult SignalExistingInstance()
    {
        if (_disposed) return PlatformResult.Failure("single_instance.disposed", "The coordinator is disposed.");
        try { using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); socket.Connect(new UnixDomainSocketEndPoint(_socketPath)); socket.Send([1]); return PlatformResult.Success(); }
        catch { return PlatformResult.Failure("single_instance.signal_failed", "The running instance could not be signalled."); }
    }
    private bool CanConnect()
    { try { using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); socket.Connect(new UnixDomainSocketEndPoint(_socketPath)); return true; } catch { return false; } }
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
        if (owned) try { File.Delete(_socketPath); } catch { }
    }
    public void Dispose() { if (_disposed) return; Release(); _disposed = true; ActivationRequested = null; }
}

public sealed class LinuxAutostartService : IAutostartService
{
    private readonly string _path;
    private readonly string _executable;
    private readonly IPrivateFileService _files;
    public LinuxAutostartService() : this(new LinuxAppPaths(), Environment.ProcessPath ?? string.Empty, new LinuxPrivateFileService()) { }
    internal LinuxAutostartService(IAppPaths paths, string executable, IPrivateFileService files)
    { _path = Path.Combine(paths.ConfigDirectory, "autostart", "hyperwhisper.desktop"); _executable = executable; _files = files; }
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
        var content = $"[Desktop Entry]\nType=Application\nName=HyperWhisper\nExec=\"{quoted}\"\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
        return _files.WriteAllTextAtomically(_path, content);
    }
    public PlatformResult Disable() => _files.Delete(_path);
}
