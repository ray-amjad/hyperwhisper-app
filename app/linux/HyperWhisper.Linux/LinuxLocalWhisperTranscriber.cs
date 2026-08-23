using HyperWhisper.LocalInference;
using HyperWhisper.ModelManagement;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.Linux;

internal sealed class LinuxLocalWhisperTranscriber : IRecordedAudioTranscriber, IDisposable
{
    private readonly string _modelsDirectory;
    private readonly ILocalWhisperService _service;
    private readonly ILinuxWhisperRuntimePreferenceSource _preferences;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private LocalWhisperLoadOptions? _loadedOptions;
    private bool _disposed;

    public LinuxLocalWhisperTranscriber(string modelsDirectory)
        : this(modelsDirectory, new LocalWhisperService(), new EnvironmentWhisperRuntimePreferenceSource())
    {
    }

    internal LinuxLocalWhisperTranscriber(
        string modelsDirectory,
        ILocalWhisperService service,
        ILinuxWhisperRuntimePreferenceSource preferences)
    {
        _modelsDirectory = Path.GetFullPath(modelsDirectory ?? throw new ArgumentNullException(nameof(modelsDirectory)));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    }

    public TranscriptionBackendCapability Capability
    {
        get
        {
            var (modelPath, probeFailure) = TryResolveModelPath();
            var preference = _preferences.Resolve();
            var displayName = _service.IsLoaded
                ? DisplayNameForRuntime(_service.Runtime, preference)
                : preference.DisplayName;
            return modelPath is null
                ? new TranscriptionBackendCapability(
                    false,
                    displayName,
                    probeFailure ?? $"Local Whisper is unavailable. Place a ggml .bin model in {_modelsDirectory} or set HYPERWHISPER_MODEL_PATH.")
                : new TranscriptionBackendCapability(true, displayName);
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
        var preference = _preferences.Resolve();
        if (modelPath is null)
            return PortableTranscriptionResult.Failed(
                PortableTranscriptionErrorCode.BackendUnavailable,
                modelFailure ?? Capability.UnavailableReason!,
                Capability.DisplayName);

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadOptions = new LocalWhisperLoadOptions(
                modelPath,
                preference.Backend,
                preference.GpuDevice,
                preference.AllowCpuFallback);
            if (_loadedOptions != loadOptions)
            {
                var loaded = await _service.LoadAsync(
                    loadOptions,
                    cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                    return MapFailure(loaded, preference.DisplayName);
                if (!preference.AllowCpuFallback
                    && preference.Backend != LocalWhisperBackend.Cpu
                    && !RuntimeMatches(preference.Backend, loaded.Runtime))
                {
                    return PortableTranscriptionResult.Failed(
                        PortableTranscriptionErrorCode.BackendUnavailable,
                        "The selected Whisper GPU runtime was not activated and CPU fallback is disabled.",
                        preference.DisplayName);
                }
                _loadedOptions = loadOptions;
            }

            var result = await _service.TranscribeAsync(
                new LocalWhisperRequest(audioPath, NormalizeLanguage(language)),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? PortableTranscriptionResult.Success(result.Text!, DisplayNameForRuntime(result.Runtime, preference))
                : MapFailure(result, preference.DisplayName);
        }
        catch (OperationCanceledException)
        {
            return PortableTranscriptionResult.Failed(
                PortableTranscriptionErrorCode.Cancelled,
                "Local transcription was cancelled.",
                preference.DisplayName);
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

    private static PortableTranscriptionResult MapFailure(LocalWhisperResult result, string displayName)
    {
        var failure = result.Failure!;
        var code = failure.Code switch
        {
            LocalWhisperErrorCode.InvalidRequest => PortableTranscriptionErrorCode.InvalidRequest,
            LocalWhisperErrorCode.RuntimeUnavailable or LocalWhisperErrorCode.ModelLoadFailed => PortableTranscriptionErrorCode.BackendUnavailable,
            LocalWhisperErrorCode.Cancelled => PortableTranscriptionErrorCode.Cancelled,
            _ => PortableTranscriptionErrorCode.TranscriptionFailed,
        };
        return PortableTranscriptionResult.Failed(code, failure.Message, displayName);
    }

    private static bool RuntimeMatches(LocalWhisperBackend backend, string? runtime) => backend switch
    {
        LocalWhisperBackend.Cuda12 => runtime?.Contains("Cuda12", StringComparison.OrdinalIgnoreCase) == true,
        LocalWhisperBackend.Vulkan => runtime?.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) == true,
        _ => runtime?.Contains("Cpu", StringComparison.OrdinalIgnoreCase) == true,
    };

    private static string DisplayNameForRuntime(string? runtime, LinuxWhisperRuntimePreference preference)
    {
        if (runtime?.Contains("Cuda12", StringComparison.OrdinalIgnoreCase) == true) return "Local Whisper (CUDA 12)";
        if (runtime?.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) == true) return "Local Whisper (Vulkan)";
        if (runtime?.Contains("Cpu", StringComparison.OrdinalIgnoreCase) == true
            && preference.Backend != LocalWhisperBackend.Cpu) return "Local Whisper (CPU fallback)";
        return preference.DisplayName;
    }
}

internal sealed record LinuxWhisperRuntimePreference(
    LocalWhisperBackend Backend,
    bool AllowCpuFallback,
    int GpuDevice = 0)
{
    public string DisplayName => Backend switch
    {
        LocalWhisperBackend.Cuda12 => $"Local Whisper (CUDA 12{FallbackSuffix})",
        LocalWhisperBackend.Vulkan => $"Local Whisper (Vulkan{FallbackSuffix})",
        _ => "Local Whisper (CPU)",
    };

    private string FallbackSuffix => AllowCpuFallback ? "; CPU fallback" : string.Empty;
}

internal interface ILinuxWhisperRuntimePreferenceSource
{
    LinuxWhisperRuntimePreference Resolve();
}

internal sealed class EnvironmentWhisperRuntimePreferenceSource : ILinuxWhisperRuntimePreferenceSource
{
    public LinuxWhisperRuntimePreference Resolve() => new(
        Parse(Environment.GetEnvironmentVariable("HYPERWHISPER_WHISPER_BACKEND")),
        !string.Equals(Environment.GetEnvironmentVariable("HYPERWHISPER_WHISPER_CPU_FALLBACK"), "0", StringComparison.Ordinal));

    internal static LocalWhisperBackend Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cuda" or "cuda12" => LocalWhisperBackend.Cuda12,
        "vulkan" => LocalWhisperBackend.Vulkan,
        _ => LocalWhisperBackend.Cpu,
    };
}

internal sealed class LinuxWhisperRuntimePreferenceSource(
    IPrivateFileService files,
    IAppPaths paths,
    IGpuInfoProvider gpu) : ILinuxWhisperRuntimePreferenceSource
{
    public LinuxWhisperRuntimePreference Resolve()
    {
        var settings = new PortableSettingsService(files, paths);
        _ = settings.Load();
        var configured = Environment.GetEnvironmentVariable("HYPERWHISPER_WHISPER_BACKEND")
            ?? settings.Get("localWhisperBackend", "auto");
        var allowFallback = Environment.GetEnvironmentVariable("HYPERWHISPER_WHISPER_CPU_FALLBACK") switch
        {
            "0" => false,
            "1" => true,
            _ => settings.Get("allowLocalWhisperCpuFallback", true),
        };
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            return new(EnvironmentWhisperRuntimePreferenceSource.Parse(configured), allowFallback);
        var detected = gpu.GetBestGpu();
        var backend = detected.Value switch
        {
            { SupportsCuda: true } => LocalWhisperBackend.Cuda12,
            { SupportsVulkan: true } => LocalWhisperBackend.Vulkan,
            _ => LocalWhisperBackend.Cpu,
        };
        return new(backend, allowFallback);
    }
}
