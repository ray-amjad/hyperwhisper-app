using HyperWhisper.LinuxSpike.ActiveApp;
using HyperWhisper.LinuxSpike.Audio;
using HyperWhisper.LinuxSpike.Injection;

var environment = new ProcessEnvironmentReader();
var nativeLibraries = new NativeLibraryProbe();
var backendProbe = new EnvironmentBackedActiveAppProbe(environment, nativeLibraries);
var activeApp = new ActiveAppCapabilityReporter(environment, backendProbe).GetCapability();
var pulseAvailable = nativeLibraries.CanLoad(PulseAudioCaptureService.PulseLibrary);
var uinputAvailable = new UInputCapabilityProbe(new FilePathAccessProbe()).IsAvailable();

Console.WriteLine("HyperWhisper Linux integration spike capability report");
Console.WriteLine($"session={environment.Get("XDG_SESSION_TYPE") ?? "unknown"}");
Console.WriteLine($"desktop={environment.Get("XDG_CURRENT_DESKTOP") ?? "unknown"}");
Console.WriteLine($"pulse={(pulseAvailable ? "available" : "unavailable")}");
Console.WriteLine($"uinput={(uinputAvailable ? "writable" : "unavailable")}");
Console.WriteLine($"active-app={activeApp.Level}:{activeApp.Backend}:{activeApp.Detail}");

internal sealed class EnvironmentBackedActiveAppProbe(
    IEnvironmentReader environment,
    INativeLibraryProbe libraries) : IActiveAppBackendProbe
{
    public bool X11Available =>
        !string.IsNullOrWhiteSpace(environment.Get("DISPLAY")) && libraries.CanLoad("libX11.so.6");

    // The real D-Bus probes are deliberately separate adapters for the next
    // spike step. Environment flags let desktop VM tests exercise routing now.
    public bool KdeDbusAvailable => environment.Get("HYPERWHISPER_SPIKE_KDE_DBUS") == "1";

    public bool GnomeExtensionAvailable => environment.Get("HYPERWHISPER_SPIKE_GNOME_EXTENSION") == "1";
}
