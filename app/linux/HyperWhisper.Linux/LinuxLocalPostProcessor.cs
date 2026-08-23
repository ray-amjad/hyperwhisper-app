using System.Globalization;
using HyperWhisper.Data.Entities;
using HyperWhisper.LocalPostProcessing;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;

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

    public static string RuntimeStatus => string.Join(" · ", new[]
    {
        $"CPU: {Availability(LocalLlmBackend.Cpu, "ready")}",
        $"Vulkan runtime: {Availability(LocalLlmBackend.Vulkan, "packaged; GPU device checked during model load")}",
        $"CUDA: {Availability(LocalLlmBackend.Cuda, "driver and runtime ready")}",
    });

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

        var vocabulary = (await _vocabulary.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(item => item.Word)
            .Concat(mode.CustomVocabulary ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        var prompt = SharedCoreBridge.BuildPostProcessingPrompt(new PortablePromptContext(
            mode.Preset,
            mode.CustomInstructions ?? string.Empty,
            mode.EnglishSpelling ?? string.Empty,
            ResolveLanguage(mode.Language),
            mode.UserSystemPrompt ?? string.Empty,
            vocabulary,
            mode.Punctuation,
            mode.Capitalization,
            mode.ProfanityFilter,
            DateTime.Now.ToString("t", CultureInfo.CurrentCulture),
            TimeZoneInfo.Local.StandardName,
            CultureInfo.CurrentCulture.Name,
            Environment.MachineName,
            AppType: applicationContext?.AppType ?? "other",
            AppName: applicationContext?.ProcessName ?? string.Empty,
            Category: applicationContext?.Category ?? string.Empty,
            Description: applicationContext?.WindowTitle ?? string.Empty,
            TextFormat: applicationContext?.TextFormat ?? string.Empty,
            BrowserHost: applicationContext?.BrowserHost ?? string.Empty,
            BrowserTabTitle: applicationContext?.BrowserTabTitle ?? string.Empty,
            FocusedElement: applicationContext?.FocusedElementType ?? string.Empty,
            FocusedContent: applicationContext?.FocusedContent ?? string.Empty,
            ScreenOcrText: applicationContext?.ScreenOcrText ?? string.Empty,
            AppTypeConfidence: applicationContext?.AppTypeConfidence ?? "unknown",
            AppTypeSource: applicationContext?.AppTypeSource ?? "default",
            HasApplicationContext: applicationContext is not null));

        var backend = ParseBackend(_settings.Get("localLlmBackend", "cpu"));
        var result = await _service.ProcessAsync(new LocalPostProcessingRequest(
            Path.Combine(_modelsDirectory, fileName),
            backend,
            prompt.SystemPrompt,
            prompt.SystemInfo,
            transcript,
            _settings.Get("allowLocalLlmCpuFallback", true)), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return PortablePostProcessingResult.Skipped(
                transcript,
                $"postprocessing.{result.Failure?.Code.ToString().ToLowerInvariant() ?? "failed"}",
                result.Failure?.Message ?? "Local post-processing failed.");
        }

        return PortablePostProcessingResult.Applied(
            result.Text,
            $"Local LLM · {fileName} · {result.Runtime}");
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

    private static LocalLlmBackend ParseBackend(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vulkan" => LocalLlmBackend.Vulkan,
        "cuda" => LocalLlmBackend.Cuda,
        _ => LocalLlmBackend.Cpu,
    };

    private static string ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto") return string.Empty;
        try { return CultureInfo.GetCultureInfo(language).DisplayName; }
        catch (CultureNotFoundException) { return language; }
    }
}
