using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Linux;

internal sealed class LinuxDesktopServices : IDisposable
{
    private bool _disposed;

    public LinuxDesktopServices()
    {
        Paths = new LinuxAppPaths();
        PrivateFiles = new LinuxPrivateFileService();
        GlobalShortcuts = new LinuxGlobalShortcutService();
        AudioDevices = new PulseAudioInputDeviceService();
        AudioRecorder = new PulseAudioRecorder(Paths);
        AudioTranscriber = new LinuxLocalWhisperTranscriber(Paths.ModelsDirectory);
        TextInjection = new LinuxTextInjectionService();
    }

    public IAppPaths Paths { get; }
    public IPrivateFileService PrivateFiles { get; }
    public IGlobalShortcutService GlobalShortcuts { get; }
    public IAudioInputDeviceService AudioDevices { get; }
    public IAudioRecorder AudioRecorder { get; }
    public LinuxLocalWhisperTranscriber AudioTranscriber { get; }
    public ITextInjectionService TextInjection { get; }

    public bool ProbeSharedCore() =>
        !SharedCoreBridge.ContainsCjk("HyperWhisper")
        && SharedCoreBridge.ContainsCjk("音声")
        && string.Equals(SharedCoreBridge.NormalizeAppType("Terminal"), "terminal", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GlobalShortcuts.Dispose();
        AudioRecorder.Dispose();
        AudioDevices.Dispose();
        AudioTranscriber.Dispose();
        TextInjection.Dispose();
    }
}
