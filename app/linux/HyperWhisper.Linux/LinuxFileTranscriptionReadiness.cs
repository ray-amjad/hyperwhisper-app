using HyperWhisper.FileTranscription;
using HyperWhisper.ModelManagement;

namespace HyperWhisper.Linux;

internal sealed class LinuxFileTranscriptionReadiness(
    PortableModelManager models,
    Func<LocalTranscriptionEngine, bool> backendAvailable) : ILocalFileTranscriptionReadiness
{
    private readonly PortableModelManager _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly Func<LocalTranscriptionEngine, bool> _backendAvailable =
        backendAvailable ?? throw new ArgumentNullException(nameof(backendAvailable));

    public ValueTask<bool> IsBackendAvailableAsync(
        LocalTranscriptionEngine engine, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_backendAvailable(engine));
    }

    public ValueTask<bool> IsModelInstalledAsync(
        ManagedModel model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_models.IsInstalled(model));
    }
}
