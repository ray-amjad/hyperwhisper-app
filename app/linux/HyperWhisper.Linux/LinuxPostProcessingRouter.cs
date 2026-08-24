using System.Globalization;
using HyperWhisper.CloudPostProcessing;
using HyperWhisper.Data.Entities;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Linux;

internal sealed class LinuxPostProcessingRouter : ITranscriptionPostProcessor, IDisposable
{
    private readonly LinuxLocalPostProcessor _local;
    private readonly CloudPostProcessingService _cloud;
    private readonly PortableSettingsService _settings;
    private readonly VocabularyRepository _vocabulary;
    private bool _disposed;

    public LinuxPostProcessingRouter(
        LinuxLocalPostProcessor local,
        CloudPostProcessingService cloud,
        PortableSettingsService settings,
        ApplicationDb database)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _vocabulary = new(database ?? throw new ArgumentNullException(nameof(database)));
    }

    public async Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        ApplicationContextSnapshot? applicationContext,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.PostProcessingMode == 2
            && string.Equals(mode.PostProcessingProvider, "local_llm", StringComparison.OrdinalIgnoreCase))
            return await _local.ProcessAsync(transcript, mode, applicationContext, cancellationToken).ConfigureAwait(false);
        if (mode.PostProcessingMode != 1 || !TryResolveProvider(mode.PostProcessingProvider, out var provider, out var endpointId))
            return PortablePostProcessingResult.Skipped(transcript);

        // Shared core rule: sanitize, drop empties, dedupe case-insensitively.
        // Terms now get trimmed first, so " API" and "API" collapse to one.
        var vocabulary = SharedCoreBridge.NormalizeVocabularyTerms(
            [
                .. (await _vocabulary.ListAsync(cancellationToken).ConfigureAwait(false)).Select(item => item.Word),
                .. (mode.CustomVocabulary ?? []),
            ],
            100);
        var prompt = LinuxPostProcessingPromptFactory.Build(mode, applicationContext, vocabulary);
        CustomPostProcessingEndpoint? custom = null;
        if (provider == CloudPostProcessingProvider.Custom)
        {
            var configured = (_settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? [])
                .FirstOrDefault(item => item.Id == endpointId);
            if (configured is null)
                return PortablePostProcessingResult.Skipped(transcript, "postprocessing.custom_endpoint_missing", "The custom endpoint is unavailable.");
            custom = new(configured.Id, configured.EndpointUrl, configured.ModelName);
        }

        var result = await _cloud.ProcessAsync(new(
            transcript,
            prompt.SystemPrompt,
            prompt.SystemInfo,
            provider,
            Model: provider is not (CloudPostProcessingProvider.HyperWhisperCloud or CloudPostProcessingProvider.Custom)
                ? mode.LanguageModel : null,
            HyperWhisperCloudModel: provider == CloudPostProcessingProvider.HyperWhisperCloud
                ? mode.CloudPostProcessingModel : null,
            CustomEndpoint: custom), cancellationToken).ConfigureAwait(false);
        return result.WasApplied && !string.IsNullOrWhiteSpace(result.Provider)
            ? PortablePostProcessingResult.Applied(result.Text, result.Provider)
            : PortablePostProcessingResult.Skipped(
                transcript,
                $"postprocessing.{result.Failure?.Code.ToString().ToLowerInvariant() ?? "failed"}",
                result.Failure?.Message ?? "Cloud post-processing failed.");
    }

    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript, Mode mode, CancellationToken cancellationToken = default) =>
        ProcessAsync(transcript, mode, null, cancellationToken);

    internal static bool TryResolveProvider(
        string? persisted, out CloudPostProcessingProvider provider, out Guid? endpointId)
    {
        endpointId = null;
        if (persisted?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) == true
            && Guid.TryParse(persisted[7..], out var parsed))
        {
            provider = CloudPostProcessingProvider.Custom;
            endpointId = parsed;
            return true;
        }
        provider = persisted?.Trim().ToLowerInvariant() switch
        {
            "openai" => CloudPostProcessingProvider.OpenAi,
            "anthropic" => CloudPostProcessingProvider.Anthropic,
            "groq" => CloudPostProcessingProvider.Groq,
            "grok" => CloudPostProcessingProvider.Grok,
            "gemini" => CloudPostProcessingProvider.Gemini,
            "cerebras" => CloudPostProcessingProvider.Cerebras,
            "mistral" => CloudPostProcessingProvider.Mistral,
            "hyperwhisper" or "hyperwhisper_cloud" or "hyperwhispercloud" => CloudPostProcessingProvider.HyperWhisperCloud,
            _ => default,
        };
        return persisted?.Trim().ToLowerInvariant() is
            "openai" or "anthropic" or "groq" or "grok" or "gemini" or "cerebras" or "mistral"
            or "hyperwhisper" or "hyperwhisper_cloud" or "hyperwhispercloud";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cloud.Dispose();
    }
}

internal static class LinuxPostProcessingPromptFactory
{
    public static PortablePostProcessingPrompt Build(
        Mode mode, ApplicationContextSnapshot? context, IReadOnlyList<string> vocabulary) =>
        SharedCoreBridge.BuildPostProcessingPrompt(new PortablePromptContext(
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
            AppType: context?.AppType ?? "other",
            AppName: context?.ProcessName ?? string.Empty,
            Category: context?.Category ?? string.Empty,
            Description: context?.WindowTitle ?? string.Empty,
            TextFormat: context?.TextFormat ?? string.Empty,
            BrowserHost: context?.BrowserHost ?? string.Empty,
            BrowserTabTitle: context?.BrowserTabTitle ?? string.Empty,
            FocusedElement: context?.FocusedElementType ?? string.Empty,
            FocusedContent: context?.FocusedContent ?? string.Empty,
            ScreenOcrText: context?.ScreenOcrText ?? string.Empty,
            AppTypeConfidence: context?.AppTypeConfidence ?? "unknown",
            AppTypeSource: context?.AppTypeSource ?? "default",
            HasApplicationContext: context is not null));

    private static string ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto") return string.Empty;
        try { return CultureInfo.GetCultureInfo(language).DisplayName; }
        catch (CultureNotFoundException) { return language; }
    }
}
