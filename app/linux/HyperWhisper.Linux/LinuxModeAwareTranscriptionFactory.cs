using HyperWhisper.Linux.Platform.Security;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using HyperWhisper.TranscriptionRouting;

namespace HyperWhisper.Linux;

/// <summary>
/// Production construction seam for the mode-aware router. The active desktop
/// composition opts into this in a separate UI phase.
/// </summary>
internal static class LinuxModeAwareTranscriptionFactory
{
    public static ModeAwareTranscriptionRouter Create(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var credentials = new CredentialStoreCloudCredentialSource(
            new LinuxCredentialStore(),
            new LinuxDeviceIdentityProvider());
        var cloud = new SharedCoreBatchCloudClient(
            new CloudTranscriptionService(new HttpClientHandler(), credentials));
        var parakeet = new ParakeetDaemonTranscriber(
            new LinuxNativeRuntimeLocator(),
            new LinuxChildProcessLauncher(),
            paths.ModelsDirectory);
        return new ModeAwareTranscriptionRouter(
            new LinuxLocalWhisperTranscriber(paths.ModelsDirectory),
            parakeet,
            cloud,
            ownsDependencies: true);
    }
}
