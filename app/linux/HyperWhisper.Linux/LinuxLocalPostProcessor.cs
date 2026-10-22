using HyperWhisper.Data.Entities;
using HyperWhisper.LocalPostProcessing;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;
using HyperWhisper.Linux.Platform.SystemIntegration;

namespace HyperWhisper.Linux;

internal sealed class LinuxLocalPostProcessor : ITranscriptionPostProcessor, IDisposable
{
    private readonly string _modelsDirectory;
    private readonly PortableSettingsService _settings;
    private readonly VocabularyRepository _vocabulary;
    private readonly LocalPostProcessingService _service;
    private bool _disposed;

    public LinuxLocalPostProcessor(
        string modelsDirectory,
        PortableSettingsService settings,
        ApplicationDb database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = Path.GetFullPath(Path.Combine(modelsDirectory, "LLM"));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _vocabulary = new VocabularyRepository(database ?? throw new ArgumentNullException(nameof(database)));
        _service = new LocalPostProcessingService(new LLamaSharpLocalLlmEngine());
    }

    // Read once: LLamaSharp pins its native backend for the whole process, so a
    // hyperwhisper-cuda installed while the app runs takes effect on restart.
    private static readonly Lazy<INativeRuntimeLocator> Runtime = new(() => new LinuxNativeRuntimeLocator());

    public static string RuntimeStatus => string.Join(" · ", new[]
    {
        $"CPU: {Availability(LocalLlmBackend.Cpu, "ready")}",
        $"Vulkan runtime: {Availability(LocalLlmBackend.Vulkan, "packaged; GPU device checked during model load")}",
        CudaPackageInstalled
            ? $"CUDA: {Availability(LocalLlmBackend.Cuda, "driver and runtime ready")}"
            : "CUDA: not installed (install the hyperwhisper-cuda package)",
    });

    private static bool CudaPackageInstalled =>
        Runtime.Value.FindLibrary("llama", NativeComputeBackend.Cuda).IsSuccess;

    public async Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        ApplicationContextSnapshot? applicationContext,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mode);
        if (string.IsNullOrWhiteSpace(transcript))
            return PortablePostProcessingResult.Skipped(
                transcript, "postprocessing.empty_transcript", "The transcript is empty.");

        var fileName = mode.LocalPostProcessingModel;
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName != Path.GetFileName(fileName)
            || fileName.Contains('\\')
            || !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return PortablePostProcessingResult.Skipped(
                transcript, "postprocessing.model_invalid", "Choose a local GGUF model filename.");
        }

        // Shared core rule: sanitize, drop empties, dedupe case-insensitively.
        // Terms now get trimmed first, so " API" and "API" collapse to one.
        var vocabulary = SharedCoreBridge.NormalizeVocabularyTerms(
            [
                .. (await _vocabulary.ListAsync(cancellationToken).ConfigureAwait(false)).Select(item => item.Word),
                .. (mode.CustomVocabulary ?? []),
            ],
            100);
        var prompt = LinuxPostProcessingPromptFactory.Build(mode, applicationContext, vocabulary);

        var allowCpuFallback = _settings.Get("allowLocalLlmCpuFallback", true);
        var backend = ResolveBackend(
            ParseBackend(_settings.Get("localLlmBackend", "cpu")), allowCpuFallback, out var cudaMissing);
        if (backend is null)
        {
            return PortablePostProcessingResult.Skipped(
                transcript, "postprocessing.runtimeunavailable", cudaMissing!);
        }
        var result = await _service.ProcessAsync(new LocalPostProcessingRequest(
            Path.Combine(_modelsDirectory, fileName),
            backend.Value,
            prompt.SystemPrompt,
            prompt.SystemInfo,
            transcript,
            allowCpuFallback), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return PortablePostProcessingResult.Skipped(
                transcript,
                $"postprocessing.{result.Failure?.Code.ToString().ToLowerInvariant() ?? "failed"}",
                result.Failure?.Message ?? "Local post-processing failed.");
        }

        // The GGUF filename is this head's model identity, and it is the one that
        // ran: it was validated above and passed straight to the engine. Report
        // it rather than letting the endpoint fall back to `mode.LanguageModel`,
        // which on a local run holds a leftover CLOUD model id (issue #314).
        return PortablePostProcessingResult.Applied(
            result.Text,
            $"Local LLM · {fileName} · {result.Runtime}",
            fileName);
    }

    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(transcript, mode, null, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static string Availability(LocalLlmBackend backend, string availableText) =>
        PackagedLlamaRuntime.IsAvailable(backend) ? availableText : "unavailable";

    /// <summary>
    /// CUDA 12 ships in hyperwhisper-cuda (#1002). Without it a CUDA choice runs on
    /// Vulkan, then CPU when CPU fallback is allowed; with neither it returns null
    /// and the install hint in <paramref name="failure"/>.
    /// </summary>
    private static LocalLlmBackend? ResolveBackend(LocalLlmBackend requested, bool allowCpuFallback, out string? failure)
    {
        failure = null;
        if (requested != LocalLlmBackend.Cuda) return requested;
        var choice = LinuxCudaPackage.Choose(Runtime.Value, "llama", NativeComputeBackend.Cuda, allowCpuFallback,
            vulkanUsable: PackagedLlamaRuntime.IsAvailable(LocalLlmBackend.Vulkan));
        if (choice.Unavailable)
        {
            failure = LinuxCudaPackage.MissingFailure;
            return null;
        }
        return choice.Backend switch
        {
            NativeComputeBackend.Vulkan => LocalLlmBackend.Vulkan,
            NativeComputeBackend.Cpu => LocalLlmBackend.Cpu,
            _ => LocalLlmBackend.Cuda,
        };
    }

    private static LocalLlmBackend ParseBackend(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vulkan" => LocalLlmBackend.Vulkan,
        "cuda" => LocalLlmBackend.Cuda,
        _ => LocalLlmBackend.Cpu,
    };

}
