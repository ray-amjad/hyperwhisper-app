using HyperWhisper.Linux.Platform.Security;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using HyperWhisper.TranscriptionRouting;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.Linux;

/// <summary>
/// Production construction seam for the mode-aware router. The active desktop
/// composition opts into this in a separate UI phase.
/// </summary>
internal static class LinuxModeAwareTranscriptionFactory
{
    public static ModeAwareTranscriptionRouter Create(
        IAppPaths paths,
        ICredentialStore credentialStore,
        IDeviceIdentityProvider deviceIdentity,
        out IRecordedAudioTranscriber whisper,
        out IRecordedAudioTranscriber parakeet)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var credentials = new CredentialStoreCloudCredentialSource(
            credentialStore ?? throw new ArgumentNullException(nameof(credentialStore)),
            deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity)));
        var cloud = new SharedCoreBatchCloudClient(
            new CloudTranscriptionService(new HttpClientHandler(), credentials));
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
