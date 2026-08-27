using HyperWhisper.Platform.Abstractions;
using HyperWhisper.Storage;

namespace HyperWhisper.PortableApplication.Transcription;

public interface ICompletedAudioTransformer
{
    Task<PlatformResult<string>> TransformAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class CompletedAudioRetention(
    Func<bool> keepAudio,
    PortableStorageLifecycleService storage,
    ICompletedAudioTransformer? transformer = null)
{
    private readonly Func<bool> _keepAudio = keepAudio ?? throw new ArgumentNullException(nameof(keepAudio));
    private readonly PortableStorageLifecycleService _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly ICompletedAudioTransformer? _transformer = transformer;

    public bool ShouldKeepAudio => _keepAudio();

    public Task<StorageCleanupResult> DeleteAsync(string path, CancellationToken cancellationToken) =>
        _storage.EnforceKeepAudioAsync(path, keepAudio: false, cancellationToken);

    public Task<PlatformResult<string>> TransformAsync(string path, CancellationToken cancellationToken) =>
        _transformer is null
            ? Task.FromResult(PlatformResult<string>.Success(path))
            : _transformer.TransformAsync(path, cancellationToken);
}
