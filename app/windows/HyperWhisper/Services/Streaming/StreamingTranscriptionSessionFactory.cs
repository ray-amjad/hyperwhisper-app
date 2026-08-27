using System;
using System.Collections.Generic;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;
// Aliased, not imported: HyperWhisper.SharedCore also declares
// CloudTranscriptionProvider, which would clash with HyperWhisper.Models.
using SharedCoreBridge = HyperWhisper.SharedCore.SharedCoreBridge;

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

        var config = new StreamingSessionConfig(
            LicenseKey: LicenseManager.Instance.GetStoredLicenseKey(),
            DeviceId: DeviceIdService.Instance.GetDeviceId(),
            Language: settings.StreamingLanguage,
            Vocabulary: BuildVocabulary(provider, vocabularyWords),
            ApiKey: apiKey,
            // Model stays Deepgram-only. Gemini Live has exactly one model, so its
            // strategy defaults to it rather than reading the Deepgram model box,
            // which is what this free-text setting really is.
            Model: provider == StreamingTranscriptionProvider.Deepgram ? settings.StreamingDeepgramModel : null,
            FastFormatting: settings.StreamingFastFormatting,
            RemoveFillerWords: settings.RemoveFillerWords,
            // Read for HyperWhisper Cloud only; the core ignores it elsewhere.
            CloudTier: settings.StreamingCloudTier
        );

        return Result<StreamingTranscriptionClient>.Success(
            new StreamingTranscriptionClient(new LiveProtocolStreamingStrategy(provider, config), config));
    }

    /// <summary>
    /// Whether this provider's live API consumes custom vocabulary at all.
    /// The shared core owns the answer (issue #281); UI must ask here rather
    /// than keep its own provider list, or the two drift (see BuildVocabulary
    /// below). A free function on purpose: the settings page reads it with no
    /// credential and no session in hand.
    /// </summary>
    public static bool SupportsVocabulary(StreamingTranscriptionProvider provider) =>
        SharedCoreBridge.LiveSupportsVocabulary(LiveProtocolStreamingStrategy.CoreProvider(provider));

    /// <summary>
    /// Whether this provider accepts custom vocabulary while the language is left on
    /// auto-detect. Deepgram Nova-3 silently drops keyterms there, so the settings
    /// page warns; Gemini does not, and warning about it would be wrong.
    /// Lives here rather than in the page so the page keeps no provider list of its
    /// own (see the warning on BuildVocabulary below).
    ///
    /// The answer itself comes from the shared core, which also resolves the
    /// HyperWhisper Cloud case: that tier inherits its live vendor's answer,
    /// because the cloud tier picker decides which upstream actually serves the
    /// session. The tier is a path selector, deliberately not a
    /// StreamingTranscriptionProvider case - the credit and entitlement wiring
    /// keys off provider == hyperwhisperCloud and must keep matching.
    /// </summary>
    public static bool SupportsVocabularyWithoutLanguage(StreamingTranscriptionProvider provider) =>
        SharedCoreBridge.LiveSupportsVocabularyWithoutLanguage(
            LiveProtocolStreamingStrategy.CoreProvider(provider),
            SettingsService.Instance.StreamingCloudTier);

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

    // The shared core is the single owner of "does this provider take
    // vocabulary". A second provider list here is what made the xAI keyterm
    // support dead on arrival: the strategy said yes and this method still said
    // no.
    //
    // A TERM LIST, not the comma-joined string this used to build. Joining is a
    // per-provider wire decision - xAI repeats `keyterm=`, HyperWhisper Cloud
    // sends one comma-joined `vocabulary=`, Deepgram repeats `keyterm=` - and it
    // moved into the protocol with everything else (issue #281).
    //
    // internal for HyperWhisper.SmokeTests, which drives it through the real FFI.
    internal static IReadOnlyList<string>? BuildVocabulary(
        StreamingTranscriptionProvider provider,
        IReadOnlyCollection<string> vocabularyWords
    )
    {
        if (!SupportsVocabulary(provider) || vocabularyWords.Count == 0)
        {
            return null;
        }

        // Sanitize/dedupe through the shared core, exactly like the batch path and
        // macOS: an imported backup can carry angle brackets or whitespace runs.
        // No cap here on purpose - each protocol applies its own vendor cap on
        // top, inside the core.
        var terms = HyperwhisperCoreMethods.NormalizeVocabularyTerms([.. vocabularyWords], null);
        return terms.Count == 0 ? null : terms;
    }
}
