using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.Security;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.LiveStreaming;
using HyperWhisper.Telemetry;

namespace HyperWhisper.Linux;

internal sealed class LinuxDesktopServices : IDisposable
{
    private bool _disposed;
    private readonly bool _ownsTelemetry;
    private readonly IRecordedAudioTranscriber _localWhisper;
    private readonly IRecordedAudioTranscriber _localParakeet;

    public LinuxDesktopServices(LinuxSentryService? telemetry = null)
    {
        Telemetry = telemetry ?? new LinuxSentryService();
        _ownsTelemetry = telemetry is null;
        Paths = new LinuxAppPaths();
        PrivateFiles = new LinuxPrivateFileService();
        CredentialStore = new LinuxCredentialStore();
        DeviceIdentity = new LinuxDeviceIdentityProvider();
        GlobalShortcuts = new LinuxGlobalShortcutService();
        PushToTalk = new LinuxPushToTalkMonitor();
        AudioDevices = new PulseAudioInputDeviceService();
        AudioRecorder = new PulseAudioRecorder(Paths);
        AudioTranscriber = LinuxModeAwareTranscriptionFactory.Create(
            Paths, CredentialStore, DeviceIdentity, out _localWhisper, out _localParakeet);
        AudioPlayback = new PulseAudioPlaybackService();
        TextInjection = new LinuxTextInjectionService();
        InsertionContext = new AtSpiInsertionContextProvider();
        ApplicationContext = new LinuxApplicationContextProvider();
        ScreenOcr = new LinuxScreenOcrService();
        Tray = new LinuxStatusNotifierItemService();
        Autostart = new LinuxAutostartService();
        SingleInstance = new LinuxSingleInstanceCoordinator(Paths);
        MicrophoneVolume = new LinuxMicrophoneVolumeService();
        MicrophoneKeepWarm = new LinuxMicrophoneKeepWarmService();
        SoundEffects = new LinuxSoundEffectsService();
        AudioEnvironment = new LinuxAudioEnvironmentService();
        LiveStreaming = new LiveStreamingSessionController(
            new PulseStreamingAudioCapture(),
            new SharedCoreLiveCloudTranscriber(new LiveCloudTranscriptionService()));
    }

    public IAppPaths Paths { get; }
    public IPrivateFileService PrivateFiles { get; }
    public ICredentialStore CredentialStore { get; }
    public IGlobalShortcutService GlobalShortcuts { get; }
    public IPushToTalkMonitor PushToTalk { get; }
    public IAudioInputDeviceService AudioDevices { get; }
    public IAudioRecorder AudioRecorder { get; }
    public IRecordedAudioTranscriber AudioTranscriber { get; }
    public IAudioPlaybackService AudioPlayback { get; }
    public ITextInjectionService TextInjection { get; }
    public IInsertionContextProvider InsertionContext { get; }
    public IApplicationContextProvider ApplicationContext { get; }
    public IScreenOcrService ScreenOcr { get; }
    public LinuxStatusNotifierItemService Tray { get; }
    public IAutostartService Autostart { get; }
    public ISingleInstanceCoordinator SingleInstance { get; }
    public IDeviceIdentityProvider DeviceIdentity { get; }
    public IMicrophoneVolumeService MicrophoneVolume { get; }
    public IMicrophoneKeepWarmService MicrophoneKeepWarm { get; }
    public ISoundEffectsService SoundEffects { get; }
    public IAudioEnvironmentService AudioEnvironment { get; }
    public LiveStreamingSessionController LiveStreaming { get; }
    public LinuxSentryService Telemetry { get; }
    public TranscriptionBackendCapability LocalWhisperCapability => _localWhisper.Capability;
    public TranscriptionBackendCapability LocalParakeetCapability => _localParakeet.Capability;

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
        PushToTalk.Dispose();
        AudioRecorder.Dispose();
        AudioDevices.Dispose();
        if (AudioTranscriber is IDisposable transcriber) transcriber.Dispose();
        AudioPlayback.Dispose();
        TextInjection.Dispose();
        ApplicationContext.Dispose();
        Tray.Dispose();
        SingleInstance.Dispose();
        MicrophoneKeepWarm.Dispose();
        SoundEffects.Dispose();
        LiveStreaming.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_ownsTelemetry) Telemetry.Dispose();
    }
}
