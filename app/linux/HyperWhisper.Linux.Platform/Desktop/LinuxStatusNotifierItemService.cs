using System.Diagnostics;
using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public enum DesktopShellCapabilityState { Available, Unsupported, Unavailable }
public sealed record DesktopShellCapability(DesktopShellCapabilityState State, string Backend, string Detail);

/// <summary>
/// Content-free commands emitted by the bundled StatusNotifierItem helper.
/// Values are deliberately closed: tray input can never carry transcript text,
/// credentials, paths, device names, or other user-controlled payloads.
/// </summary>
public enum StatusNotifierAction
{
    StartRecording,
    StopRecording,
    SelectDefaultMicrophone,
    SelectPreviousMicrophone,
    SelectNextMicrophone,
    CycleMode,
    TranscribeFile,
    OpenHistory,
    OpenSettings,
    OpenHelp,
    OpenSupport,
    SendFeedback,
    Show,
    Hide,
    Quit
}

public sealed class StatusNotifierActionEventArgs(StatusNotifierAction action) : EventArgs
{
    public StatusNotifierAction Action { get; } = action;
}

internal enum StatusNotifierMessage
{
    Unknown,
    Available,
    Unsupported,
    StartRecording,
    StopRecording,
    SelectDefaultMicrophone,
    SelectPreviousMicrophone,
    SelectNextMicrophone,
    CycleMode,
    TranscribeFile,
    OpenHistory,
    OpenSettings,
    OpenHelp,
    OpenSupport,
    SendFeedback,
    Show,
    Hide,
    Quit
}

/// <summary>
/// Hosts a StatusNotifierItem through a bundled, GI-backed session-D-Bus helper.
/// If no watcher is present (notably stock GNOME), callers receive an honest
/// unsupported state and keep the normal application-window fallback.
/// </summary>
public sealed class LinuxStatusNotifierItemService : IDisposable
{
    internal const int MaximumProtocolLineLength = 64;

    private readonly string? _python;
    private readonly string? _script;
    private Process? _process;
    private CancellationTokenSource? _readerCancellation;
    private bool _disposed;

    public LinuxStatusNotifierItemService() : this(
        Injection.CommandClipboardBackend.FindExecutable("python3"), ResolveScript()) { }

    internal LinuxStatusNotifierItemService(string? python, string? script)
    { _python = python; _script = script; }

    /// <summary>Raised for every validated tray command.</summary>
    public event EventHandler<StatusNotifierActionEventArgs>? ActionRequested;

    // Retained for source compatibility with the existing window integration.
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
        StopHelper();
        try
        {
            var start = new ProcessStartInfo(_python!, _script!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopHelper();
            throw;
        }
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
                var line = await ReadBoundedLineAsync(process.StandardOutput, token).ConfigureAwait(false);
                if (line is null) break;
                var message = ParseMessage(line);
                if (message == StatusNotifierMessage.Available)
                {
                    becameAvailable = true;
                    ready.TrySetResult(PlatformResult.Success());
                }
                else if (message == StatusNotifierMessage.Unsupported)
                {
                    ready.TrySetResult(PlatformResult.Failure("tray_unsupported", "No StatusNotifierItem watcher is available."));
                }
                else
                {
                    Dispatch(message);
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

    internal void Dispatch(StatusNotifierMessage message)
    {
        if (!TryGetAction(message, out var action)) return;
        Raise(ActionRequested, new StatusNotifierActionEventArgs(action));
        switch (action)
        {
            case StatusNotifierAction.Show: Raise(ShowRequested); break;
            case StatusNotifierAction.Hide: Raise(HideRequested); break;
            case StatusNotifierAction.Quit: Raise(QuitRequested); break;
        }
    }

    internal static StatusNotifierMessage ParseMessage(string? line)
    {
        if (line is null || line.Length is 0 or > MaximumProtocolLineLength ||
            line.Any(character => char.IsControl(character)))
            return StatusNotifierMessage.Unknown;

        return line switch
        {
            "CAPABILITY|available" => StatusNotifierMessage.Available,
            "CAPABILITY|unsupported" or "CAPABILITY|unavailable" => StatusNotifierMessage.Unsupported,
            "ACTION|record-start" => StatusNotifierMessage.StartRecording,
            "ACTION|record-stop" => StatusNotifierMessage.StopRecording,
            "ACTION|microphone-default" => StatusNotifierMessage.SelectDefaultMicrophone,
            "ACTION|microphone-previous" => StatusNotifierMessage.SelectPreviousMicrophone,
            "ACTION|microphone-next" => StatusNotifierMessage.SelectNextMicrophone,
            "ACTION|mode-cycle" => StatusNotifierMessage.CycleMode,
            "ACTION|transcribe-file" => StatusNotifierMessage.TranscribeFile,
            "ACTION|history" => StatusNotifierMessage.OpenHistory,
            "ACTION|settings" => StatusNotifierMessage.OpenSettings,
            "ACTION|help" => StatusNotifierMessage.OpenHelp,
            "ACTION|support" => StatusNotifierMessage.OpenSupport,
            "ACTION|feedback" => StatusNotifierMessage.SendFeedback,
            "ACTION|show" => StatusNotifierMessage.Show,
            "ACTION|hide" => StatusNotifierMessage.Hide,
            "ACTION|quit" => StatusNotifierMessage.Quit,
            _ when line.StartsWith("CAPABILITY|", StringComparison.Ordinal) => StatusNotifierMessage.Unsupported,
            _ => StatusNotifierMessage.Unknown
        };
    }

    internal static async ValueTask<string?> ReadBoundedLineAsync(TextReader reader, CancellationToken token = default)
    {
        var value = new StringBuilder(MaximumProtocolLineLength);
        var exceeded = false;
        var buffer = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return value.Length == 0 && !exceeded ? null : exceeded ? string.Empty : value.ToString();
            if (buffer[0] == '\n') return exceeded ? string.Empty : value.ToString();
            if (buffer[0] == '\r') continue;
            if (value.Length < MaximumProtocolLineLength) value.Append(buffer[0]);
            else exceeded = true;
        }
    }

    private static bool TryGetAction(StatusNotifierMessage message, out StatusNotifierAction action)
    {
        action = message switch
        {
            StatusNotifierMessage.StartRecording => StatusNotifierAction.StartRecording,
            StatusNotifierMessage.StopRecording => StatusNotifierAction.StopRecording,
            StatusNotifierMessage.SelectDefaultMicrophone => StatusNotifierAction.SelectDefaultMicrophone,
            StatusNotifierMessage.SelectPreviousMicrophone => StatusNotifierAction.SelectPreviousMicrophone,
            StatusNotifierMessage.SelectNextMicrophone => StatusNotifierAction.SelectNextMicrophone,
            StatusNotifierMessage.CycleMode => StatusNotifierAction.CycleMode,
            StatusNotifierMessage.TranscribeFile => StatusNotifierAction.TranscribeFile,
            StatusNotifierMessage.OpenHistory => StatusNotifierAction.OpenHistory,
            StatusNotifierMessage.OpenSettings => StatusNotifierAction.OpenSettings,
            StatusNotifierMessage.OpenHelp => StatusNotifierAction.OpenHelp,
            StatusNotifierMessage.OpenSupport => StatusNotifierAction.OpenSupport,
            StatusNotifierMessage.SendFeedback => StatusNotifierAction.SendFeedback,
            StatusNotifierMessage.Show => StatusNotifierAction.Show,
            StatusNotifierMessage.Hide => StatusNotifierAction.Hide,
            StatusNotifierMessage.Quit => StatusNotifierAction.Quit,
            _ => default
        };
        return message is >= StatusNotifierMessage.StartRecording and <= StatusNotifierMessage.Quit;
    }

    private void Raise(EventHandler? handlers)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch { }
    }

    private void Raise(EventHandler<StatusNotifierActionEventArgs>? handlers, StatusNotifierActionEventArgs args)
    {
        if (handlers is null) return;
        foreach (EventHandler<StatusNotifierActionEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, args); } catch { }
    }

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
        if (_process is { HasExited: false })
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(2000); } catch { }
        _process?.Dispose();
        _process = null;
        _readerCancellation?.Dispose();
        _readerCancellation = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHelper();
        ActionRequested = null;
        ShowRequested = null;
        HideRequested = null;
        QuitRequested = null;
        Unavailable = null;
    }
}
