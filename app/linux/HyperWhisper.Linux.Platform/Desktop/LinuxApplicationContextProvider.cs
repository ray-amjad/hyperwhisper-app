using System.Text;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public enum LinuxDesktopCapabilityState { Available, Unsupported, Unavailable }

public sealed record LinuxApplicationContextCapabilities(
    LinuxDesktopCapabilityState State,
    string Backend,
    bool StandardPortalAvailable,
    string Detail);

public sealed class LinuxApplicationContextProvider : IApplicationContextProvider
{
    private const string AtSpiContextScript = """
import base64,gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi
def enc(value):
    return base64.b64encode((value or '').encode('utf-8')).decode('ascii')
try:
    stack=[Atspi.get_desktop(0)]; seen=0
    while stack and seen < 10000:
        node=stack.pop(); seen+=1
        try:
            if node.get_state_set().contains(Atspi.StateType.FOCUSED):
                app=node.get_application()
                parent=node
                while parent.get_parent() is not None and parent.get_parent().get_role() != Atspi.Role.DESKTOP_FRAME:
                    parent=parent.get_parent()
                print('CONTEXT|%s|%s|%s' % (node.get_process_id(),enc(app.get_name()),enc(parent.get_name())))
                raise SystemExit(0)
            for i in range(node.get_child_count()):
                child=node.get_child_at_index(i)
                if child is not None: stack.append(child)
        except Exception:
            pass
except Exception:
    pass
print('UNAVAILABLE')
""";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private readonly IDesktopCommandRunner _runner;
    private readonly string? _xprop;
    private readonly string? _python;
    private readonly bool _wayland;
    private readonly bool _displayAvailable;

    public LinuxApplicationContextProvider() : this(new DesktopCommandRunner(),
        CommandClipboardBackend.FindExecutable("xprop"), CommandClipboardBackend.FindExecutable("python3"),
        IsWaylandSession(), !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))) { }

    internal LinuxApplicationContextProvider(IDesktopCommandRunner runner, string? xprop, string? python,
        bool wayland, bool displayAvailable = true)
    {
        _runner = runner;
        _xprop = xprop;
        _python = python;
        _wayland = wayland;
        _displayAvailable = displayAvailable;
    }

    public LinuxApplicationContextCapabilities GetCapabilities()
    {
        if (_wayland)
            return _python is null
                ? new(LinuxDesktopCapabilityState.Unsupported, "wayland-none", false,
                    "No standard active-window portal exists and the AT-SPI query runtime is unavailable.")
                : new(LinuxDesktopCapabilityState.Available, "wayland-at-spi", false,
                    "AT-SPI supplies the focused application; GNOME and KDE expose no stable cross-desktop active-window portal.");
        return _xprop is null || !_displayAvailable
            ? new(LinuxDesktopCapabilityState.Unavailable, "x11-none", false, "An X11 display and xprop are required.")
            : new(LinuxDesktopCapabilityState.Available, "x11-ewmh", false, "EWMH active-window properties are available.");
    }

    public async ValueTask<PlatformResult<ApplicationContextSnapshot?>> GatherAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return _wayland
                ? await GatherWaylandAsync(cancellationToken).ConfigureAwait(false)
                : await GatherX11Async(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_timeout", "Active application discovery timed out."); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "Active application discovery is unavailable."); }
    }

    private async ValueTask<PlatformResult<ApplicationContextSnapshot?>> GatherX11Async(CancellationToken token)
    {
        if (_xprop is null || !_displayAvailable)
            return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "X11 active-window discovery is unavailable.");
        var root = await _runner.RunAsync(_xprop, ["-root", "_NET_ACTIVE_WINDOW"], null, token, CommandTimeout).ConfigureAwait(false);
        if (root.ExitCode != 0) return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "The X11 active window is unavailable.");
        var id = ParseWindowId(Encoding.UTF8.GetString(root.Output));
        if (id is null || id == "0x0") return PlatformResult<ApplicationContextSnapshot?>.Success(null);
        var detail = await _runner.RunAsync(_xprop,
            ["-id", id, "_NET_WM_PID", "_NET_WM_NAME", "WM_NAME", "WM_CLASS"], null, token, CommandTimeout).ConfigureAwait(false);
        if (detail.ExitCode != 0) return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "The X11 window details are unavailable.");
        return PlatformResult<ApplicationContextSnapshot?>.Success(ParseX11Details(Encoding.UTF8.GetString(detail.Output)));
    }

    private async ValueTask<PlatformResult<ApplicationContextSnapshot?>> GatherWaylandAsync(CancellationToken token)
    {
        if (_python is null)
            return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unsupported", "No standard Wayland active-window portal is available.");
        var result = await _runner.RunAsync(_python, ["-c", AtSpiContextScript], null, token, CommandTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0) return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "AT-SPI active application discovery failed.");
        var value = Encoding.UTF8.GetString(result.Output).Trim();
        if (value == "UNAVAILABLE") return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "No focused AT-SPI application is available.");
        var parts = value.Split('|');
        if (parts.Length != 4 || parts[0] != "CONTEXT")
            return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "AT-SPI returned an invalid application context.");
        return PlatformResult<ApplicationContextSnapshot?>.Success(new ApplicationContextSnapshot
        {
            ProcessName = ResolveProcessName(parts[1]) ?? Decode(parts[2]),
            WindowTitle = Decode(parts[3]),
        });
    }

    private static ApplicationContextSnapshot ParseX11Details(string output)
    {
        string title = string.Empty, windowClass = string.Empty, pid = string.Empty;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("_NET_WM_PID", StringComparison.Ordinal)) pid = AfterEquals(line);
            else if (line.StartsWith("_NET_WM_NAME", StringComparison.Ordinal) && title.Length == 0) title = Quoted(line);
            else if (line.StartsWith("WM_NAME", StringComparison.Ordinal) && title.Length == 0) title = Quoted(line);
            else if (line.StartsWith("WM_CLASS", StringComparison.Ordinal)) windowClass = QuotedValues(line).LastOrDefault() ?? string.Empty;
        }
        return new ApplicationContextSnapshot { ProcessName = ResolveProcessName(pid) ?? windowClass, WindowTitle = title };
    }

    private static string? ParseWindowId(string value)
    {
        var marker = value.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            return value[marker..].Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return ulong.TryParse(AfterEquals(value), out var numeric) ? $"0x{numeric:x}" : null;
    }

    private static string AfterEquals(string line) => line[(line.IndexOf('=') + 1)..].Trim();
    private static string Quoted(string line) => QuotedValues(line).FirstOrDefault() ?? string.Empty;
    private static IEnumerable<string> QuotedValues(string line)
    {
        var index = 0;
        while ((index = line.IndexOf('"', index)) >= 0)
        {
            var end = line.IndexOf('"', index + 1);
            if (end < 0) yield break;
            yield return line[(index + 1)..end];
            index = end + 1;
        }
    }
    private static string? ResolveProcessName(string pid) =>
        int.TryParse(pid, out var value) ? ReadProcessName(value) : null;
    private static string? ReadProcessName(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); } catch { return null; }
    }
    private static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); } catch { return string.Empty; }
    }
    private static bool IsWaylandSession() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    public void Dispose() { }
}
