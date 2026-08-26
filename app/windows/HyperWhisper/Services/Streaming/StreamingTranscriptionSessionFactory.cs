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
            Model: provider == StreamingTranscriptionProvider.Deepgram ? settings.StreamingDeepgramModel : null,
            FastFormatting: settings.StreamingFastFormatting,
            RemoveFillerWords: settings.RemoveFillerWords
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
