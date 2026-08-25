using System.Text;
using HyperWhisper.AppClassification;
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
    private readonly string? _gdbus;
    private readonly string _desktop;
    private readonly bool _wayland;
    private readonly bool _displayAvailable;

    public LinuxApplicationContextProvider() : this(new DesktopCommandRunner(),
        CommandClipboardBackend.FindExecutable("xprop"), CommandClipboardBackend.FindExecutable("python3"),
        IsWaylandSession(), !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")),
        CommandClipboardBackend.FindExecutable("gdbus"), Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")) { }

    internal LinuxApplicationContextProvider(IDesktopCommandRunner runner, string? xprop, string? python,
        bool wayland, bool displayAvailable = true, string? gdbus = null, string? desktop = null)
    {
        _runner = runner;
        _xprop = xprop;
        _python = python;
        _gdbus = gdbus;
        _desktop = desktop ?? string.Empty;
        _wayland = wayland;
        _displayAvailable = displayAvailable;
    }

    public LinuxApplicationContextCapabilities GetCapabilities()
    {
        if (_wayland)
            return _python is null && _gdbus is null
                ? new(LinuxDesktopCapabilityState.Unsupported, "wayland-none", false,
                    "No standard active-window portal exists and no supported desktop-session backend is available.")
                : new(LinuxDesktopCapabilityState.Available, WaylandBackendName(), false,
                    WaylandCapabilityDetail());
        return _xprop is null || !_displayAvailable
            ? new(LinuxDesktopCapabilityState.Unavailable, "x11-none", false, "An X11 display and xprop are required.")
            : new(LinuxDesktopCapabilityState.Available, "x11-ewmh", false, "EWMH active-window properties are available.");
    }

    public async ValueTask<PlatformResult<ApplicationContextSnapshot?>> GatherAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var gathered = _wayland
                ? await GatherWaylandAsync(cancellationToken).ConfigureAwait(false)
                : await GatherX11Async(cancellationToken).ConfigureAwait(false);
            return gathered.IsSuccess && gathered.Value is not null
                ? PlatformResult<ApplicationContextSnapshot?>.Success(Classify(gathered.Value))
                : gathered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_timeout", "Active application discovery timed out."); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_unavailable", "Active application discovery is unavailable."); }
    }

    // Linux shipped no classifier at all until issue #279, so `AppType` kept the
    // `other` default from DesktopIntegrationContracts and the two AppType gates
    // in the shared prompt builder never fired. The live one is the screen-OCR
    // gate: with OCR enabled for a mode, recognized pixels from a password
    // manager reached the post-processing prompt. Only the process name and the
    // window title are observable here — Linux has no browser host and no
    // focused-element snapshot — so those are the two signals sent.
    private static ApplicationContextSnapshot Classify(ApplicationContextSnapshot snapshot)
    {
        var classification = AppTypeClassifier.Classify(new AppClassificationRequest(
            ProcessName: snapshot.ProcessName,
            Title: snapshot.WindowTitle));
        if (classification.AppType == AppType.Other) return snapshot;

        return snapshot with
        {
            Category = string.IsNullOrWhiteSpace(snapshot.Category)
                ? classification.AppType.ToCategory()
                : snapshot.Category,
            TextFormat = string.IsNullOrWhiteSpace(snapshot.TextFormat)
                ? classification.AppType.ToTextFormat()
                : snapshot.TextFormat,
            AppType = classification.AppType.ToPromptValue(),
            AppTypeConfidence = classification.Confidence,
            AppTypeSource = classification.Source,
        };
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
        if (_gdbus is not null && IsDesktop("GNOME"))
        {
            var companion = await GatherSessionDbusAsync(GnomeBusName, GnomeObjectPath, GnomeInterface, token).ConfigureAwait(false);
            if (companion.IsSuccess) return companion;
        }
        if (_gdbus is not null && IsDesktop("KDE"))
        {
            var kwin = await GatherSessionDbusAsync(KdeBusName, KdeObjectPath, KdeInterface, token).ConfigureAwait(false);
            if (kwin.IsSuccess) return kwin;
        }
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

    // Companion services return one string containing the same privacy-bounded payload as the
    // AT-SPI adapter: CONTEXT|pid|base64(application)|base64(title). No key or document text is
    // accepted. KWin itself has no stable non-interactive active-window metadata method, so its
    // companion is deliberately optional and AT-SPI remains the explicit default-mode fallback.
    internal const string GnomeBusName = "org.gnome.Shell.Extensions.HyperWhisper";
    internal const string GnomeObjectPath = "/org/gnome/Shell/Extensions/HyperWhisper";
    internal const string GnomeInterface = "org.gnome.Shell.Extensions.HyperWhisper";
    internal const string KdeBusName = "org.kde.KWin.HyperWhisper";
    internal const string KdeObjectPath = "/HyperWhisper";
    internal const string KdeInterface = "org.kde.KWin.HyperWhisper";

    private async ValueTask<PlatformResult<ApplicationContextSnapshot?>> GatherSessionDbusAsync(
        string busName, string objectPath, string interfaceName, CancellationToken token)
    {
        var result = await _runner.RunAsync(_gdbus!,
            ["call", "--session", "--dest", busName, "--object-path", objectPath,
                "--method", $"{interfaceName}.GetActiveWindow"], null, token, CommandTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
            return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_backend_unavailable", "The desktop active-window companion is unavailable.");
        return ParseDbusContext(Encoding.UTF8.GetString(result.Output));
    }

    internal static PlatformResult<ApplicationContextSnapshot?> ParseDbusContext(string output)
    {
        var payload = ParseSingleGVariantString(output);
        if (payload is null)
            return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_invalid_response", "The desktop active-window companion returned an invalid response.");
        if (payload == "UNAVAILABLE") return PlatformResult<ApplicationContextSnapshot?>.Success(null);
        var parts = payload.Split('|');
        if (parts.Length != 4 || parts[0] != "CONTEXT")
            return PlatformResult<ApplicationContextSnapshot?>.Failure("active_app_invalid_response", "The desktop active-window companion returned an invalid response.");
        return PlatformResult<ApplicationContextSnapshot?>.Success(new ApplicationContextSnapshot
        {
            ProcessName = ResolveProcessName(parts[1]) ?? Decode(parts[2]),
            WindowTitle = Decode(parts[3]),
        });
    }

    private static string? ParseSingleGVariantString(string output)
    {
        var value = output.Trim();
        var start = value.IndexOf('\'');
        if (start < 0) return null;
        var builder = new StringBuilder();
        var escaped = false;
        for (var index = start + 1; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                builder.Append(character switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => character });
                escaped = false;
            }
            else if (character == '\\') escaped = true;
            else if (character == '\'') return builder.ToString();
            else builder.Append(character);
        }
        return null;
    }

    private bool IsDesktop(string value) => _desktop.Contains(value, StringComparison.OrdinalIgnoreCase);
    private string WaylandBackendName() => IsDesktop("GNOME") && _gdbus is not null ? "gnome-companion-dbus+atspi"
        : IsDesktop("KDE") && _gdbus is not null ? "kde-kwin-dbus+atspi" : "wayland-atspi";
    private string WaylandCapabilityDetail() => IsDesktop("GNOME") && _gdbus is not null
        ? "Uses the GNOME HyperWhisper companion when present, with an explicit AT-SPI default-mode fallback."
        : IsDesktop("KDE") && _gdbus is not null
            ? "Uses the KWin HyperWhisper companion when present, with an explicit AT-SPI fallback."
            : "AT-SPI supplies the focused application; no standard Wayland active-window portal exists.";

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
