using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public enum DesktopShellCapabilityState { Available, Unsupported, Unavailable }
public sealed record DesktopShellCapability(DesktopShellCapabilityState State, string Backend, string Detail);

/// <summary>
/// Content-free commands emitted by the bundled StatusNotifierItem helper.
/// Values are deliberately closed: tray input can never carry transcript text,
/// credentials, paths, device names, or other user-controlled payloads.
/// <see cref="SelectMode"/> is raised only with a mode id taken from the snapshot
/// this process published (<see cref="LinuxStatusNotifierItemService.PublishModeMenu"/>):
/// the helper sends back two bounded integers, never a name or an id.
/// </summary>
public enum StatusNotifierAction
{
    StartRecording,
    StopRecording,
    SelectDefaultMicrophone,
    SelectPreviousMicrophone,
    SelectNextMicrophone,
    SelectMode,
    OpenModes,
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

public sealed class StatusNotifierActionEventArgs(StatusNotifierAction action, Guid? modeId = null) : EventArgs
{
    public StatusNotifierAction Action { get; } = action;

    /// <summary>The mode a <see cref="StatusNotifierAction.SelectMode"/> click resolved to; null otherwise.</summary>
    public Guid? ModeId { get; } = modeId;
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
    SelectMode,
    OpenModes,
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

    // ACTION|mode-select|<generation>|<index>: canonical decimal only, so there is
    // exactly one spelling of each click and nothing else fits in the token.
    private static readonly Regex ModeSelectToken = new(
        @"\AACTION\|mode-select\|(0|[1-9][0-9]{0,9})\|(0|[1-9][0-9]?)\z",
        RegexOptions.CultureInvariant);

    private readonly string? _python;
    private readonly string? _script;
    private readonly object _modeGate = new();
    private Process? _process;
    private CancellationTokenSource? _readerCancellation;
    private TrayModeMenu? _modeMenu;
    private int _modeGeneration;
    private string? _pendingModeLine;
    private bool _modeWriterRunning;
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
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };
            _process = Process.Start(start);
            if (_process is null) return PlatformResult.Failure("tray_unavailable", "The StatusNotifierItem helper could not start.");
            // A restarted helper starts with no mode list: hand it the current one.
            // The pipe buffers it until the helper's main loop starts reading.
            QueueModeMenu(advanceGeneration: false);
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
                else if (message == StatusNotifierMessage.SelectMode)
                {
                    if (TryResolveModeSelection(line, out var modeId)) Dispatch(message, modeId);
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

    internal void Dispatch(StatusNotifierMessage message, Guid? modeId = null)
    {
        if (!TryGetAction(message, out var action)) return;
        // A mode click is only ever raised with an id resolved from our own snapshot.
        if ((action == StatusNotifierAction.SelectMode) != modeId.HasValue) return;
        Raise(ActionRequested, new StatusNotifierActionEventArgs(action, modeId));
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
            "ACTION|modes" => StatusNotifierMessage.OpenModes,
            _ when ModeSelectToken.IsMatch(line) => StatusNotifierMessage.SelectMode,
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
            StatusNotifierMessage.SelectMode => StatusNotifierAction.SelectMode,
            StatusNotifierMessage.OpenModes => StatusNotifierAction.OpenModes,
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

    /// <summary>
    /// Publishes the tray's Select Mode list. A changed list is a new generation; a click
    /// the helper reports for an older generation, or for an index this snapshot does
    /// not have, is dropped. A list equal to the last one is not sent at all, so an
    /// unrelated republish never re-ids the rows of a menu the user has open. Safe to
    /// call before <see cref="StartAsync"/> and from any thread: the latest snapshot is
    /// kept and re-sent whenever the helper (re)starts.
    /// </summary>
    public void PublishModeMenu(TrayModeMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        lock (_modeGate)
        {
            if (_disposed) return;
            if (_modeMenu is { } last && last.HasSameContent(menu)) return;
            _modeMenu = menu;
        }
        QueueModeMenu(advanceGeneration: true);
    }

    /// <summary>
    /// Resolves an <c>ACTION|mode-select|generation|index</c> token against the last
    /// published snapshot. Only the two integers cross from the helper; the id comes
    /// from our own list.
    /// </summary>
    internal bool TryResolveModeSelection(string? line, out Guid modeId)
    {
        modeId = default;
        if (line is null || line.Length > MaximumProtocolLineLength) return false;
        var match = ModeSelectToken.Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var generation)
            || !int.TryParse(match.Groups[2].ValueSpan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index))
            return false;
        lock (_modeGate)
        {
            if (_modeMenu is not { } menu || !menu.Enabled || generation != _modeGeneration
                || index < 0 || index >= menu.Rows.Count)
                return false;
            modeId = menu.Rows[index].Id;
            return true;
        }
    }

    /// <summary>The stdin line for one snapshot: a single JSON object. The default
    /// encoder escapes every non-ASCII and control character, so the line is pure
    /// ASCII and no mode name can contain the newline that frames it.</summary>
    internal static string SerializeModeMenu(TrayModeMenu menu, int generation)
    {
        var payload = new Dictionary<string, object>
        {
            ["v"] = 1,
            ["generation"] = generation,
            ["active"] = menu.ActiveIndex,
            ["enabled"] = menu.Enabled,
            ["more"] = menu.HasMore,
            ["modes"] = menu.Rows.Select(row => row.Label).ToArray()
        };
        return JsonSerializer.Serialize(payload);
    }

    internal int ModeGeneration { get { lock (_modeGate) return _modeGeneration; } }

    private void QueueModeMenu(bool advanceGeneration)
    {
        lock (_modeGate)
        {
            if (_disposed || _modeMenu is null) return;
            if (advanceGeneration) _modeGeneration = _modeGeneration == int.MaxValue ? 1 : _modeGeneration + 1;
            _pendingModeLine = SerializeModeMenu(_modeMenu, _modeGeneration);
            if (_modeWriterRunning || _process is null) return;
            _modeWriterRunning = true;
        }
        // ast-grep-ignore: no-discarded-task-run -- a publish runs on the UI thread and must not block on the helper's stdin pipe; DrainModeMenuAsync catches its own write failures and the next helper start re-sends the snapshot.
        _ = Task.Run(DrainModeMenuAsync);
    }

    // One writer at a time, always sending the newest snapshot: a burst of changes
    // (a mode list reload adds rows one by one) collapses to the last line.
    private async Task DrainModeMenuAsync()
    {
        while (true)
        {
            string? line;
            StreamWriter? input;
            lock (_modeGate)
            {
                line = _pendingModeLine;
                _pendingModeLine = null;
                input = _process is { } process ? TryGetInput(process) : null;
                if (line is null || input is null)
                {
                    _modeWriterRunning = false;
                    return;
                }
            }
            try
            {
                await input.WriteAsync(line + "\n").ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
            }
            catch { /* The helper went away; the next start re-sends the snapshot. */ }
        }
    }

    private static StreamWriter? TryGetInput(Process process)
    {
        try { return process.StandardInput; }
        catch (InvalidOperationException) { return null; }
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
        lock (_modeGate) { _pendingModeLine = null; }
        if (_process is { HasExited: false })
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(2000); } catch { }
        lock (_modeGate)
        {
            _process?.Dispose();
            _process = null;
        }
        _readerCancellation?.Dispose();
        _readerCancellation = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_modeGate) { _disposed = true; }
        StopHelper();
        ActionRequested = null;
        ShowRequested = null;
        HideRequested = null;
        QuitRequested = null;
        Unavailable = null;
    }
}
