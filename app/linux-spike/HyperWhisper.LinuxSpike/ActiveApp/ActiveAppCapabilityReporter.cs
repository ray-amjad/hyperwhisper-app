namespace HyperWhisper.LinuxSpike.ActiveApp;

public enum ActiveAppCapabilityLevel
{
    Unavailable,
    DefaultModeFallback,
    Full,
}

public sealed record ActiveAppCapability(
    ActiveAppCapabilityLevel Level,
    string Backend,
    string Detail);

public interface IEnvironmentReader
{
    string? Get(string name);
}

public interface IActiveAppBackendProbe
{
    bool X11Available { get; }

    bool KdeDbusAvailable { get; }

    bool GnomeExtensionAvailable { get; }
}

public sealed class ActiveAppCapabilityReporter
{
    private readonly IEnvironmentReader _environment;
    private readonly IActiveAppBackendProbe _backends;

    public ActiveAppCapabilityReporter(
        IEnvironmentReader environment,
        IActiveAppBackendProbe backends)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
    }

    public ActiveAppCapability GetCapability()
    {
        var session = (_environment.Get("XDG_SESSION_TYPE") ?? string.Empty).ToLowerInvariant();
        var desktop = (_environment.Get("XDG_CURRENT_DESKTOP") ?? string.Empty).ToLowerInvariant();
        var hasDisplay = !string.IsNullOrWhiteSpace(_environment.Get("DISPLAY"));

        // A Wayland session usually exposes DISPLAY through XWayland. That
        // does not make X11 foreground-window detection complete, so only use
        // the DISPLAY fallback when session type itself is unavailable.
        if ((session == "x11" || (session.Length == 0 && hasDisplay))
            && _backends.X11Available)
        {
            return new ActiveAppCapability(ActiveAppCapabilityLevel.Full, "x11", "foreground-window");
        }

        if (session == "wayland" && desktop.Contains("kde", StringComparison.Ordinal)
            && _backends.KdeDbusAvailable)
        {
            return new ActiveAppCapability(ActiveAppCapabilityLevel.Full, "kde-dbus", "active-window-dbus");
        }

        if (session == "wayland" && desktop.Contains("gnome", StringComparison.Ordinal))
        {
            return _backends.GnomeExtensionAvailable
                ? new ActiveAppCapability(ActiveAppCapabilityLevel.Full, "gnome-extension", "trusted-dbus-signal")
                : new ActiveAppCapability(
                    ActiveAppCapabilityLevel.DefaultModeFallback,
                    "gnome-wayland",
                    "companion-extension-unavailable");
        }

        return new ActiveAppCapability(
            ActiveAppCapabilityLevel.Unavailable,
            "none",
            "unsupported-session");
    }
}

public sealed class ProcessEnvironmentReader : IEnvironmentReader
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}
