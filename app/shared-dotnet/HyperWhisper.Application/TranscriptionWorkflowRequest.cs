using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using HyperWhisper.SpeechOutput;

namespace HyperWhisper.PortableApplication.Transcription;

public sealed record TranscriptionWorkflowRequest(
    string? Language = null,
    string? ModeName = null,
    Guid? ModeId = null,
    Mode? SelectedMode = null,
    IReadOnlyList<string>? Vocabulary = null,
    ApplicationContextSnapshot? ApplicationContext = null,
    IReadOnlyList<PortableVocabularyReplacement>? VocabularyReplacements = null,
    IReadOnlyList<PortableVocabularyReplacement>? ModeVocabularyReplacements = null,
    SpeechOutputProcessingOptions? OutputOptions = null,
    bool PasteResultText = true,
    PortableCursorContext CursorContext = PortableCursorContext.Unknown,
    bool StoreWordTimestamps = true)
{
    /// <summary>Freezes mutable mode and list state for one transcription operation.</summary>
    public TranscriptionWorkflowRequest Snapshot() => this with
    {
        SelectedMode = SelectedMode is null ? null : CloneMode(SelectedMode),
        Vocabulary = Vocabulary?.ToArray(),
        ApplicationContext = ApplicationContext is null ? null : ApplicationContext with { },
        VocabularyReplacements = VocabularyReplacements?.ToArray(),
        ModeVocabularyReplacements = ModeVocabularyReplacements?.ToArray(),
        OutputOptions = OutputOptions is null ? null : OutputOptions with { },
    };

    private static Mode CloneMode(Mode value) => new()
    {
        Id = value.Id, Name = value.Name, Preset = value.Preset,
        IsDefault = value.IsDefault, IsSystemProvided = value.IsSystemProvided, SortOrder = value.SortOrder,
        Language = value.Language, Model = value.Model, ModelType = value.ModelType,
        LocalEngine = value.LocalEngine, LocalParakeetModel = value.LocalParakeetModel,
        CloudProvider = value.CloudProvider, CloudTranscriptionModel = value.CloudTranscriptionModel,
        CloudTranscriptionDomain = value.CloudTranscriptionDomain, ProviderType = value.ProviderType,
        CloudAccuracyTier = value.CloudAccuracyTier, GeminiCustomPrompt = value.GeminiCustomPrompt,
        Punctuation = value.Punctuation, Capitalization = value.Capitalization,
        ProfanityFilter = value.ProfanityFilter, RemoveTrailingPeriod = value.RemoveTrailingPeriod,
        EnglishSpelling = value.EnglishSpelling, PostProcessingMode = value.PostProcessingMode,
        PostProcessingProvider = value.PostProcessingProvider, LanguageModel = value.LanguageModel,
        LocalPostProcessingModel = value.LocalPostProcessingModel, UserSystemPrompt = value.UserSystemPrompt,
        CustomInstructions = value.CustomInstructions, EnableScreenOCR = value.EnableScreenOCR,
        CloudPostProcessingModel = value.CloudPostProcessingModel,
        CustomVocabulary = value.CustomVocabulary?.ToList(),
        CreatedDate = value.CreatedDate, ModifiedDate = value.ModifiedDate,
        ForeignPlatformExtensions = value.ForeignPlatformExtensions,
    };
}
