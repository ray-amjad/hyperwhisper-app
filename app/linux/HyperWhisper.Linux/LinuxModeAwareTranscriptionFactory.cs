using HyperWhisper.Linux.Platform.Security;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using HyperWhisper.TranscriptionRouting;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.Linux;

/// <summary>
/// Production construction seam for the mode-aware router. The active desktop
/// composition opts into this in a separate UI phase.
/// </summary>
internal static class LinuxModeAwareTranscriptionFactory
{
    public static ModeAwareTranscriptionRouter Create(
        IAppPaths paths,
        IPrivateFileService privateFiles,
        ICredentialStore credentialStore,
        IDeviceIdentityProvider deviceIdentity,
        out IRecordedAudioTranscriber whisper,
        out IRecordedAudioTranscriber parakeet)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var credentials = new CredentialStoreCloudCredentialSource(
            credentialStore ?? throw new ArgumentNullException(nameof(credentialStore)),
            deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity)));
        // TRUE means SHARE (the app-wide default), so an unreadable settings file
        // keeps sharing on rather than silently opting the user out. The core
        // turns a FALSE into `X-Latency-Opt-Out: 1`, and only on the HyperWhisper
        // Cloud / routed builders — the header can no longer reach a direct
        // vendor, which is what the old hostname-gated DelegatingHandler was for.
        // That handler also dropped the choice on any non-production base URL;
        // reading the setting here fixes that.
        bool ShareAnonymousSpeedData()
        {
            var settings = new PortableSettingsService(privateFiles, paths);
            return settings.Load().IsFailure || settings.Get("general.shareAnonymousSpeedData", true);
        }
        var cloud = new SharedCoreBatchCloudClient(new CloudTranscriptionService(
            new HttpClientHandler(),
            credentials,
            shareAnonymousSpeedData: ShareAnonymousSpeedData));
        parakeet = new ParakeetDaemonTranscriber(
            new LinuxNativeRuntimeLocator(),
            new LinuxChildProcessLauncher(),
            paths.ModelsDirectory);
        whisper = new LinuxLocalWhisperTranscriber(
                paths.ModelsDirectory,
                new HyperWhisper.LocalInference.LocalWhisperService(),
                new LinuxWhisperRuntimePreferenceSource(
                    new HyperWhisper.Linux.Platform.Files.LinuxPrivateFileService(),
                    paths,
                    new LinuxGpuInfoProvider()));
        return new ModeAwareTranscriptionRouter(
            whisper,
            parakeet,
            cloud,
            ownsDependencies: true);
    }
}
