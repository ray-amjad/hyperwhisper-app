using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.Security;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.Linux;

internal sealed class LinuxDesktopServices : IDisposable
{
    private bool _disposed;

    public LinuxDesktopServices()
    {
        Paths = new LinuxAppPaths();
        PrivateFiles = new LinuxPrivateFileService();
        CredentialStore = new LinuxCredentialStore();
        GlobalShortcuts = new LinuxGlobalShortcutService();
        AudioDevices = new PulseAudioInputDeviceService();
        AudioRecorder = new PulseAudioRecorder(Paths);
        AudioTranscriber = LinuxModeAwareTranscriptionFactory.Create(Paths);
        AudioPlayback = new PulseAudioPlaybackService();
        TextInjection = new LinuxTextInjectionService();
    }

    public IAppPaths Paths { get; }
    public IPrivateFileService PrivateFiles { get; }
    public ICredentialStore CredentialStore { get; }
    public IGlobalShortcutService GlobalShortcuts { get; }
    public IAudioInputDeviceService AudioDevices { get; }
    public IAudioRecorder AudioRecorder { get; }
    public IRecordedAudioTranscriber AudioTranscriber { get; }
    public IAudioPlaybackService AudioPlayback { get; }
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
        if (AudioTranscriber is IDisposable transcriber) transcriber.Dispose();
        AudioPlayback.Dispose();
        TextInjection.Dispose();
    }
}
