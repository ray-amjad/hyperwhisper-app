// WINDOWS ONBOARDING RESTORE POINT
//
// Everything the source committer is about to overwrite, captured before the
// first write so "Set Up Later" can put production state back byte for byte.
// The Windows counterpart of LiveOnboardingRestorePoint in
// app/macos/hyperwhisper/Views/Onboarding/OnboardingLiveDependencies.swift.
//
// macOS lists all twenty-five Mode fields one by one because its
// createOrUpdateMode resets every omitted parameter. Windows does not need that:
// ModeService.SaveMode takes the whole entity and
// context.Entry(existing).CurrentValues.SetValues(mode) writes every column, so
// the honest snapshot is the ROW, cloned. A field added to Mode later is then
// covered automatically, where the macOS shape would silently drop it.
//
// The clone matters. ModeService hands back an entity from a context it has
// already disposed, so it is detached - but the committer mutates that same
// object on the way to Apply(), and a snapshot that aliased it would follow the
// mutation and restore nothing.

using HyperWhisper.Data.Entities;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

/// <summary>
/// The default Mode row as it was, plus which Mode was selected, plus whether a
/// flagged default existed at all.
/// </summary>
public sealed class WindowsOnboardingRestorePoint : IOnboardingRestorePoint
{
    /// <summary>
    /// False when no row carried IsDefault. Apply() creates one in that case, and
    /// Restore() has to remove what it created rather than leave a synthetic
    /// default behind.
    /// </summary>
    public required bool ModeExisted { get; init; }

    /// <summary>The id Apply() writes to: the existing default, or the well-known seed id.</summary>
    public required Guid ModeId { get; init; }

    /// <summary>A detached copy of the row, or null when none existed.</summary>
    public Mode? Snapshot { get; init; }

    /// <summary>SettingsService.SelectedModeId as it was, including null.</summary>
    public Guid? PreviousSelectedModeId { get; init; }

    /// <summary>
    /// A field-for-field copy that shares no mutable reference with the original.
    /// </summary>
    public static Mode Clone(Mode source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Preset = source.Preset,
        IsDefault = source.IsDefault,
        IsSystemProvided = source.IsSystemProvided,
        SortOrder = source.SortOrder,
        Language = source.Language,
        Model = source.Model,
        ModelType = source.ModelType,
        LocalEngine = source.LocalEngine,
        LocalParakeetModel = source.LocalParakeetModel,
        CloudProvider = source.CloudProvider,
        CloudTranscriptionModel = source.CloudTranscriptionModel,
        CloudTranscriptionDomain = source.CloudTranscriptionDomain,
        ProviderType = source.ProviderType,
        CloudAccuracyTier = source.CloudAccuracyTier,
        GeminiCustomPrompt = source.GeminiCustomPrompt,
        Punctuation = source.Punctuation,
        Capitalization = source.Capitalization,
        ProfanityFilter = source.ProfanityFilter,
        RemoveTrailingPeriod = source.RemoveTrailingPeriod,
        EnglishSpelling = source.EnglishSpelling,
        PostProcessingMode = source.PostProcessingMode,
        PostProcessingProvider = source.PostProcessingProvider,
        LanguageModel = source.LanguageModel,
        LocalPostProcessingModel = source.LocalPostProcessingModel,
        UserSystemPrompt = source.UserSystemPrompt,
        CustomInstructions = source.CustomInstructions,
        EnableScreenOCR = source.EnableScreenOCR,
        CloudPostProcessingModel = source.CloudPostProcessingModel,
        CustomVocabulary = source.CustomVocabulary is null
            ? null
            : new List<string>(source.CustomVocabulary),
        CreatedDate = source.CreatedDate,
        ModifiedDate = source.ModifiedDate,
        ForeignPlatformExtensions = source.ForeignPlatformExtensions
    };
}
