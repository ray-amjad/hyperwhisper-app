using HyperWhisper.LocalInference;
using HyperWhisper.ModelManagement;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.Linux;

internal sealed class LinuxLocalWhisperTranscriber : IRecordedAudioTranscriber, IDisposable
{
    private readonly string _modelsDirectory;
    private readonly LocalWhisperService _service = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private string? _loadedModelPath;
    private bool _disposed;

    public LinuxLocalWhisperTranscriber(string modelsDirectory)
    {
        _modelsDirectory = Path.GetFullPath(modelsDirectory ?? throw new ArgumentNullException(nameof(modelsDirectory)));
    }

    public TranscriptionBackendCapability Capability
    {
        get
        {
            var (modelPath, probeFailure) = TryResolveModelPath();
            return modelPath is null
                ? new TranscriptionBackendCapability(
                    false,
                    "Local Whisper (CPU)",
                    probeFailure ?? $"Local Whisper is unavailable. Place a ggml .bin model in {_modelsDirectory} or set HYPERWHISPER_MODEL_PATH.")
                : new TranscriptionBackendCapability(true, "Local Whisper (CPU)");
        }
    }

    public async Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? language,
        CancellationToken cancellationToken = default)
        => await TranscribeSelectedAsync(audioPath, language, null, cancellationToken).ConfigureAwait(false);

    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default) =>
        TranscribeSelectedAsync(
            audioPath,
            request.Language,
            request.SelectedMode?.ModelType ?? request.SelectedMode?.Model,
            cancellationToken);

    private async Task<PortableTranscriptionResult> TranscribeSelectedAsync(
        string audioPath,
        string? language,
        string? modelId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (modelPath, modelFailure) = TryResolveModelPath(modelId);
        if (modelPath is null)
            return PortableTranscriptionResult.Failed(
                PortableTranscriptionErrorCode.BackendUnavailable,
                modelFailure ?? Capability.UnavailableReason!,
                Capability.DisplayName);

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_loadedModelPath, modelPath, StringComparison.Ordinal))
            {
                var loaded = await _service.LoadAsync(
                    new LocalWhisperLoadOptions(modelPath, LocalWhisperBackend.Cpu),
                    cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                    return MapFailure(loaded);
                _loadedModelPath = modelPath;
            }

            var result = await _service.TranscribeAsync(
                new LocalWhisperRequest(audioPath, NormalizeLanguage(language)),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? PortableTranscriptionResult.Success(result.Text!, "Local Whisper (CPU)")
                : MapFailure(result);
        }
        catch (OperationCanceledException)
        {
            return PortableTranscriptionResult.Failed(
                PortableTranscriptionErrorCode.Cancelled,
                "Local transcription was cancelled.",
                "Local Whisper (CPU)");
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _loadGate.Dispose();
    }

    private (string? Path, string? Failure) TryResolveModelPath(string? modelId = null)
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("HYPERWHISPER_MODEL_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var fullPath = Path.GetFullPath(configured);
                return File.Exists(fullPath)
                    ? (fullPath, null)
                    : (null, "The configured local Whisper model does not exist or is inaccessible.");
            }
            if (!Directory.Exists(_modelsDirectory)) return (null, null);
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var selected = PortableModelCatalog.Whisper.FirstOrDefault(model =>
                    string.Equals(model.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                    return (null, "The selected local Whisper model is not supported.");
                var selectedPath = Path.Combine(_modelsDirectory, selected.StorageName);
                return File.Exists(selectedPath)
                    ? (selectedPath, null)
                    : (null, "The selected local Whisper model is not downloaded.");
            }
            return (Directory.EnumerateFiles(_modelsDirectory, "ggml*.bin", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(), null);
        }
        catch (Exception)
        {
            return (null, "The local Whisper model location could not be inspected safely.");
        }
    }

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    private static PortableTranscriptionResult MapFailure(LocalWhisperResult result)
    {
        var failure = result.Failure!;
        var code = failure.Code switch
        {
            LocalWhisperErrorCode.InvalidRequest => PortableTranscriptionErrorCode.InvalidRequest,
            LocalWhisperErrorCode.RuntimeUnavailable or LocalWhisperErrorCode.ModelLoadFailed => PortableTranscriptionErrorCode.BackendUnavailable,
            LocalWhisperErrorCode.Cancelled => PortableTranscriptionErrorCode.Cancelled,
            _ => PortableTranscriptionErrorCode.TranscriptionFailed,
        };
        return PortableTranscriptionResult.Failed(code, failure.Message, "Local Whisper (CPU)");
    }
}
