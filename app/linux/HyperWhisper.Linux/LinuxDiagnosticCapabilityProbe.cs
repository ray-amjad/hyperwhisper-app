using HyperWhisper.Diagnostics;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.Input;

namespace HyperWhisper.Linux;

internal sealed record LinuxDiagnosticCapabilitySnapshot(
    bool AudioCapture,
    bool Clipboard,
    bool UInput,
    bool CapturedTargetFocus,
    bool GlobalShortcuts,
    bool ScreenCapture,
    bool UsesDesktopPortal,
    bool LocalInference,
    bool Cuda);

internal static class LinuxDiagnosticCapabilityProbe
{
    internal static DiagnosticCapabilities Detect(LinuxDesktopServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var audio = services.AudioRecorder is PulseAudioRecorder recorder
            && recorder.GetCapabilities().Available;
        var injection = services.TextInjection is LinuxTextInjectionService textInjection
            ? textInjection.GetCapabilities()
            : new LinuxTextInjectionCapabilities(false, "none", false, false, false, false);
        var shortcuts = services.GlobalShortcuts is LinuxGlobalShortcutService globalShortcuts
            && globalShortcuts.GetCapabilities().Available;
        var screen = services.ScreenOcr is LinuxScreenOcrService screenOcr
            ? screenOcr.GetCapabilities()
            : new LinuxScreenOcrCapabilities(false, "none", false, false);
        var localInference = services.LocalWhisperCapability.IsAvailable
            || services.LocalParakeetCapability.IsAvailable;
        var cuda = services.LocalWhisperCapability.IsAvailable
            && services.LocalWhisperCapability.DisplayName.Contains("CUDA", StringComparison.OrdinalIgnoreCase);

        return Create(new(
            audio,
            injection.ClipboardAvailable,
            injection.UInputAvailable,
            injection.CapturedTargetFocusAvailable,
            shortcuts,
            screen.CaptureAvailable,
            screen.UsesDesktopPortal,
            localInference,
            cuda));
    }

    internal static DiagnosticCapabilities Create(LinuxDiagnosticCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            AudioCapture: snapshot.AudioCapture,
            Clipboard: snapshot.Clipboard,
            GlobalShortcuts: snapshot.GlobalShortcuts,
            TextInjection: snapshot.Clipboard && snapshot.UInput && snapshot.CapturedTargetFocus,
            PortalScreenCapture: snapshot.ScreenCapture && snapshot.UsesDesktopPortal,
            LocalInference: snapshot.LocalInference,
            Cuda: snapshot.LocalInference && snapshot.Cuda);
    }
}
