using System;
using System.Collections.Generic;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

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
            // Model stays Deepgram-only. Gemini Live has exactly one model, so its
            // strategy defaults to it rather than reading the Deepgram model box,
            // which is what this free-text setting really is.
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

    /// <summary>
    /// Whether this provider accepts custom vocabulary while the language is left on
    /// auto-detect. Deepgram Nova-3 silently drops keyterms there, so the settings
    /// page warns; Gemini does not, and warning about it would be wrong.
    /// Lives here rather than in the page so the page keeps no provider list of its
    /// own (see the warning on BuildVocabulary below).
    /// </summary>
    public static bool SupportsVocabularyWithoutLanguage(StreamingTranscriptionProvider provider) =>
        provider switch
        {
            // The one real constraint: Deepgram Nova-3 accepts `keyterm` only in
            // monolingual mode and silently ignores it otherwise.
            StreamingTranscriptionProvider.Deepgram => false,
            // HyperWhisper Cloud inherits its live vendor's answer, because the
            // cloud tier picker decides which upstream actually serves the session.
            StreamingTranscriptionProvider.HyperWhisperCloud =>
                !HyperWhisperCloudStreamingStrategy.TierRequiresLanguageForVocabulary(
                    SettingsService.Instance.StreamingCloudTier),
            // xAI and Gemini both take vocabulary with or without a language.
            // Providers with no vocabulary at all never reach here - the caller
            // returns on SupportsVocabulary first.
            _ => true
        };

    private static IStreamingProviderStrategy CreateStrategy(StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.Deepgram => new DeepgramStreamingStrategy(),
        StreamingTranscriptionProvider.ElevenLabs => new ElevenLabsStreamingStrategy(),
        StreamingTranscriptionProvider.OpenAI => new OpenAIStreamingStrategy(),
        StreamingTranscriptionProvider.Xai => new XaiStreamingStrategy(),
        StreamingTranscriptionProvider.GeminiTranscribe => new GeminiStreamingStrategy(),
        // The cloud tier is a path selector on the ONE cloud strategy, deliberately
        // not a StreamingTranscriptionProvider case: the credit and entitlement
        // wiring keys off provider == hyperwhisperCloud and must keep matching.
        _ => new HyperWhisperCloudStreamingStrategy(SettingsService.Instance.StreamingCloudTier)
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
        StreamingTranscriptionProvider.GeminiTranscribe =>
            ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.GeminiTranscribe),
        _ => null
    };

    private static TranscriptionApiKeyType? GetApiKeyType(StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.Deepgram => TranscriptionApiKeyType.Deepgram,
        StreamingTranscriptionProvider.ElevenLabs => TranscriptionApiKeyType.ElevenLabs,
        StreamingTranscriptionProvider.Xai => TranscriptionApiKeyType.Grok,
        StreamingTranscriptionProvider.GeminiTranscribe => TranscriptionApiKeyType.GeminiTranscribe,
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
    // internal for HyperWhisper.SmokeTests, which drives it through the real FFI.
    internal static string? BuildVocabulary(
        IStreamingProviderStrategy strategy,
        IReadOnlyCollection<string> vocabularyWords
    )
    {
        if (!strategy.SupportsVocabulary || vocabularyWords.Count == 0)
        {
            return null;
        }

        // Sanitize/dedupe through the shared core, exactly like the batch path and
        // macOS: an imported backup can carry angle brackets or whitespace runs,
        // and every strategy downstream applies its own length cap AFTER this.
        // No cap here on purpose — the strategies own the caps.
        var terms = HyperwhisperCoreMethods.NormalizeVocabularyTerms([.. vocabularyWords], null);

        var vocabulary = string.Join(", ", terms);
        return string.IsNullOrWhiteSpace(vocabulary) ? null : vocabulary;
    }
}
