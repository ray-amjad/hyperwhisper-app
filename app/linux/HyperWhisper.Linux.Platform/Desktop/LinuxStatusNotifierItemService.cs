using System.Diagnostics;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public enum DesktopShellCapabilityState { Available, Unsupported, Unavailable }
public sealed record DesktopShellCapability(DesktopShellCapabilityState State, string Backend, string Detail);
internal enum StatusNotifierMessage { Unknown, Available, Unsupported, Show, Hide, Quit }

/// <summary>
/// Hosts a StatusNotifierItem through a bundled, GI-backed session-D-Bus helper.
/// If no watcher is present (notably stock GNOME), callers receive an honest
/// unsupported state and keep the normal application-window fallback.
/// </summary>
public sealed class LinuxStatusNotifierItemService : IDisposable
{
    private readonly string? _python;
    private readonly string? _script;
    private Process? _process;
    private CancellationTokenSource? _readerCancellation;
    private bool _disposed;

    public LinuxStatusNotifierItemService() : this(
        Injection.CommandClipboardBackend.FindExecutable("python3"), ResolveScript()) { }

    internal LinuxStatusNotifierItemService(string? python, string? script)
    { _python = python; _script = script; }

    public event EventHandler? ShowRequested;
    public event EventHandler? HideRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? Unavailable;

    public DesktopShellCapability GetCapability()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            return new(DesktopShellCapabilityState.Unavailable, "status-notifier-none", "A session D-Bus is required.");
        if (_python is null || _script is null || !File.Exists(_script))
            return new(DesktopShellCapabilityState.Unsupported, "status-notifier-none", "The bundled StatusNotifierItem helper is unavailable.");
        return new(DesktopShellCapabilityState.Available, "status-notifier-dbus", "StatusNotifierItem will be registered when the desktop watcher is available.");
    }

    public async Task<PlatformResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return PlatformResult.Failure("tray_disposed", "The tray service is disposed.");
        var capability = GetCapability();
        if (capability.State != DesktopShellCapabilityState.Available)
            return PlatformResult.Failure("tray_unavailable", capability.Detail);
        if (_process is { HasExited: false }) return PlatformResult.Success();
        try
        {
            var start = new ProcessStartInfo(_python!, _script!)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            _process = Process.Start(start);
            if (_process is null) return PlatformResult.Failure("tray_unavailable", "The StatusNotifierItem helper could not start.");
            // The caller's token bounds startup only; a disposed startup token
            // must not silently tear down an already registered tray service.
            _readerCancellation = new CancellationTokenSource();
            var ready = new TaskCompletionSource<PlatformResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ReadActionsAsync(_process, ready, _readerCancellation.Token);
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        { StopHelper(); return PlatformResult.Failure("tray_unavailable", "The StatusNotifierItem helper is unavailable."); }
    }

    private async Task ReadActionsAsync(Process process, TaskCompletionSource<PlatformResult> ready, CancellationToken token)
    {
        var becameAvailable = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                switch (ParseMessage(line))
                {
                    case StatusNotifierMessage.Available: becameAvailable = true; ready.TrySetResult(PlatformResult.Success()); break;
                    case StatusNotifierMessage.Unsupported: ready.TrySetResult(PlatformResult.Failure("tray_unsupported", "No StatusNotifierItem watcher is available.")); break;
                    case StatusNotifierMessage.Show: Raise(ShowRequested); break;
                    case StatusNotifierMessage.Hide: Raise(HideRequested); break;
                    case StatusNotifierMessage.Quit: Raise(QuitRequested); break;
                }
            }
            ready.TrySetResult(PlatformResult.Failure("tray_unavailable", "The StatusNotifierItem helper stopped."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { ready.TrySetResult(PlatformResult.Failure("tray_unavailable", "The StatusNotifierItem helper stopped.")); }
        finally
        {
            if (becameAvailable && !token.IsCancellationRequested) Raise(Unavailable);
        }
    }

    internal static StatusNotifierMessage ParseMessage(string line) => line switch
    {
        "CAPABILITY|available" => StatusNotifierMessage.Available,
        "ACTION|show" => StatusNotifierMessage.Show,
        "ACTION|hide" => StatusNotifierMessage.Hide,
        "ACTION|quit" => StatusNotifierMessage.Quit,
        _ when line.StartsWith("CAPABILITY|", StringComparison.Ordinal) => StatusNotifierMessage.Unsupported,
        _ => StatusNotifierMessage.Unknown
    };

    private void Raise(EventHandler? handlers)
    { if (handlers is null) return; foreach (EventHandler handler in handlers.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch { } }

    private static string? ResolveScript()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DesktopCompanions", "status-notifier.py"),
            "/usr/share/hyperwhisper/companions/status-notifier.py"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void StopHelper()
    {
        _readerCancellation?.Cancel();
        if (_process is { HasExited: false }) try { _process.Kill(entireProcessTree: true); _process.WaitForExit(2000); } catch { }
        _process?.Dispose(); _process = null; _readerCancellation?.Dispose(); _readerCancellation = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; StopHelper(); ShowRequested = null; HideRequested = null; QuitRequested = null; Unavailable = null; }
}
