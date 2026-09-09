using HyperWhisper.Data.Entities;

namespace HyperWhisper.Services.LocalApi;

internal enum LocalApiTransientModeFields
{
    Shared,
    SharedAndTranscription
}

/// <summary>
/// Creates request-only Mode clones shared by Local API endpoints. The base
/// clone copies only the 24 fields shared by the post-processing and
/// transcription endpoints. It deliberately replaces source identity,
/// ordering, and timestamps, and omits persistence flags and foreign-platform
/// extensions. The transcription field set additionally copies ModelType and
/// CloudTranscriptionDomain. CustomVocabulary keeps its existing shallow-copy
/// semantics.
/// </summary>
internal static class LocalApiTransientMode
{
    internal static Mode Create(
        Mode source,
        string transientName,
        LocalApiTransientModeFields fields)
    {
        var mode = new Mode
        {
            Id = Guid.NewGuid(),
            Name = transientName,
            Preset = source.Preset,
            Language = source.Language,
            Model = source.Model,
            Punctuation = source.Punctuation,
            Capitalization = source.Capitalization,
            ProfanityFilter = source.ProfanityFilter,
            CustomInstructions = source.CustomInstructions,
            UserSystemPrompt = source.UserSystemPrompt,
            LanguageModel = source.LanguageModel,
            CloudProvider = source.CloudProvider,
            CloudTranscriptionModel = source.CloudTranscriptionModel,
            ProviderType = source.ProviderType,
            PostProcessingMode = source.PostProcessingMode,
            PostProcessingProvider = source.PostProcessingProvider,
            EnglishSpelling = source.EnglishSpelling,
            CloudAccuracyTier = source.CloudAccuracyTier,
            RemoveTrailingPeriod = source.RemoveTrailingPeriod,
            EnableScreenOCR = source.EnableScreenOCR,
            GeminiCustomPrompt = source.GeminiCustomPrompt,
            CloudPostProcessingModel = source.CloudPostProcessingModel,
            LocalEngine = source.LocalEngine,
            LocalParakeetModel = source.LocalParakeetModel,
            LocalPostProcessingModel = source.LocalPostProcessingModel,
            CustomVocabulary = source.CustomVocabulary,
            SortOrder = int.MaxValue,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        if (fields == LocalApiTransientModeFields.SharedAndTranscription)
        {
            mode.ModelType = source.ModelType;
            mode.CloudTranscriptionDomain = source.CloudTranscriptionDomain;
        }

        return mode;
    }
}
