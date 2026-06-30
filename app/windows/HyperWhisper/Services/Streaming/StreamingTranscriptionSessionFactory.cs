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
            Vocabulary: BuildVocabulary(provider, vocabularyWords),
            ApiKey: apiKey,
            Model: provider == StreamingTranscriptionProvider.Deepgram ? settings.StreamingDeepgramModel : null,
            FastFormatting: settings.StreamingFastFormatting
        );

        return Result<StreamingTranscriptionClient>.Success(new StreamingTranscriptionClient(strategy, config));
    }

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

    private static string? BuildVocabulary(
        StreamingTranscriptionProvider provider,
        IReadOnlyCollection<string> vocabularyWords
    )
    {
        if (provider is StreamingTranscriptionProvider.ElevenLabs or StreamingTranscriptionProvider.OpenAI or StreamingTranscriptionProvider.Xai ||
            vocabularyWords.Count == 0)
        {
            return null;
        }

        var terms = vocabularyWords
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var vocabulary = string.Join(", ", terms);
        return string.IsNullOrWhiteSpace(vocabulary) ? null : vocabulary;
    }
}
