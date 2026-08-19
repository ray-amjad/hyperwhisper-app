using System;
using System.Collections.Generic;
using System.Linq;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// Builds configured realtime transcription clients from persisted Windows settings.
/// </summary>
public static class StreamingTranscriptionSessionFactory
{
    public static Result<StreamingTranscriptionClient> Create(IReadOnlyCollection<string> vocabularyWords)
    {
        var settings = SettingsService.Instance;
        var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(settings.StreamingProvider);
        var apiKeyType = GetApiKeyType(provider);
        var postProcessingApiKeyType = GetPostProcessingApiKeyType(provider);
        var apiKey = GetApiKey(provider);

        if ((apiKeyType.HasValue || postProcessingApiKeyType.HasValue) && string.IsNullOrWhiteSpace(apiKey))
        {
            return Result<StreamingTranscriptionClient>.Failure(
                $"API key not configured for {provider.DisplayName()}"
            );
        }

        if (apiKeyType.HasValue && !ApiKeyService.IsValidKeyFormat(apiKeyType.Value, apiKey))
        {
            return Result<StreamingTranscriptionClient>.Failure(
                $"Invalid API key format for {provider.DisplayName()}"
            );
        }

        if (postProcessingApiKeyType.HasValue && !ApiKeyService.IsValidKeyFormat(postProcessingApiKeyType.Value, apiKey))
        {
            return Result<StreamingTranscriptionClient>.Failure(
                $"Invalid API key format for {provider.DisplayName()}"
            );
        }

        var strategy = CreateStrategy(provider);
        var config = new StreamingSessionConfig(
            LicenseKey: LicenseManager.Instance.GetStoredLicenseKey(),
            DeviceId: DeviceIdService.Instance.GetDeviceId(),
            Language: settings.StreamingLanguage,
            Vocabulary: BuildVocabulary(strategy, vocabularyWords),
            ApiKey: apiKey,
            Model: provider == StreamingTranscriptionProvider.Deepgram ? settings.StreamingDeepgramModel : null,
            FastFormatting: settings.StreamingFastFormatting,
            RemoveFillerWords: settings.RemoveFillerWords
        );

        return Result<StreamingTranscriptionClient>.Success(new StreamingTranscriptionClient(strategy, config));
    }

    /// <summary>
    /// Whether this provider's streaming strategy consumes custom vocabulary.
    /// The strategy owns the answer; UI must ask here rather than keep its own
    /// provider list, or the two drift (see BuildVocabulary below).
    /// </summary>
    public static bool SupportsVocabulary(StreamingTranscriptionProvider provider) =>
        CreateStrategy(provider).SupportsVocabulary;

    private static IStreamingProviderStrategy CreateStrategy(StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.Deepgram => new DeepgramStreamingStrategy(),
        StreamingTranscriptionProvider.ElevenLabs => new ElevenLabsStreamingStrategy(),
        StreamingTranscriptionProvider.OpenAI => new OpenAIStreamingStrategy(),
        StreamingTranscriptionProvider.Xai => new XaiStreamingStrategy(),
        _ => new HyperWhisperCloudStreamingStrategy()
    };

    private static string? GetApiKey(StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.Deepgram =>
            ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Deepgram),
        StreamingTranscriptionProvider.ElevenLabs =>
            ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.ElevenLabs),
        StreamingTranscriptionProvider.OpenAI =>
            ApiKeyService.Instance.GetApiKey(PostProcessingProvider.OpenAI),
        StreamingTranscriptionProvider.Xai =>
            ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Grok),
        _ => null
    };

    private static TranscriptionApiKeyType? GetApiKeyType(StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.Deepgram => TranscriptionApiKeyType.Deepgram,
        StreamingTranscriptionProvider.ElevenLabs => TranscriptionApiKeyType.ElevenLabs,
        StreamingTranscriptionProvider.Xai => TranscriptionApiKeyType.Grok,
        _ => null
    };

    private static PostProcessingProvider? GetPostProcessingApiKeyType(StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.OpenAI => PostProcessingProvider.OpenAI,
        _ => null
    };

    // The strategy is the single owner of "does this provider take vocabulary".
    // A second provider list here is what made the xAI keyterm support dead on
    // arrival: the strategy said yes and this method still said no.
    private static string? BuildVocabulary(
        IStreamingProviderStrategy strategy,
        IReadOnlyCollection<string> vocabularyWords
    )
    {
        if (!strategy.SupportsVocabulary || vocabularyWords.Count == 0)
        {
            return null;
        }

        // Sanitize through the shared core, exactly like the batch path and macOS:
        // an imported backup can carry angle brackets or whitespace runs, and every
        // strategy downstream applies its own length cap AFTER this.
        var terms = vocabularyWords
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => Utilities.PromptBuilder.SanitizeVocabularyWord(term))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var vocabulary = string.Join(", ", terms);
        return string.IsNullOrWhiteSpace(vocabulary) ? null : vocabulary;
    }
}
