using HyperWhisper.Data.Entities;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// Creates request-only Mode clones shared by Local API endpoints.
/// </summary>
internal static class LocalApiTransientMode
{
    internal static Mode CreateFromSharedFields(Mode source, string transientName)
    {
        return new Mode
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
    }
}
