using System.Collections.Immutable;

namespace HyperWhisper.PortableApplication.Persistence;

public enum VocabularyConflictPolicy
{
    Skip,
    Replace,
}

public enum BackupModeImportBehavior
{
    ReplaceAll,
    MergeSelected,
}

public sealed record BackupModeContents(Guid Id, string Name, bool IsDefault);

public sealed record BackupContents(
    int SchemaVersion,
    string Platform,
    DateTimeOffset ExportDate,
    bool HasSettings,
    bool HasModes,
    bool HasVocabulary,
    bool ContainsCredentials,
    bool ContainsLicenseKey,
    ImmutableArray<BackupModeContents> Modes,
    int VocabularyCount);

public sealed record BackupExportSelection(
    bool IncludeSettings = true,
    bool IncludeModes = true,
    bool IncludeVocabulary = true,
    bool IncludeCredentials = false,
    ImmutableHashSet<Guid>? SelectedModeIds = null)
{
    public static BackupExportSelection All { get; } = new();

    public static BackupExportSelection SelectedModes(IEnumerable<Guid> modeIds) => new(
        IncludeSettings: false,
        IncludeModes: true,
        IncludeVocabulary: false,
        SelectedModeIds: modeIds.ToImmutableHashSet());
}

public sealed record BackupImportSelection(
    bool ImportSettings = true,
    bool ImportModes = true,
    bool ImportVocabulary = true,
    BackupModeImportBehavior ModeBehavior = BackupModeImportBehavior.ReplaceAll,
    ImmutableHashSet<Guid>? SelectedModeIds = null,
    VocabularyConflictPolicy VocabularyConflictPolicy = VocabularyConflictPolicy.Replace)
{
    public static BackupImportSelection All { get; } = new();

    public static BackupImportSelection SelectedModes(IEnumerable<Guid> modeIds) => new(
        ImportSettings: false,
        ImportModes: true,
        ImportVocabulary: false,
        ModeBehavior: BackupModeImportBehavior.MergeSelected,
        SelectedModeIds: modeIds.ToImmutableHashSet());
}

public sealed record BackupMergePreview(
    bool WillImportSettings,
    int ModesAdded,
    int ModesReplaced,
    int ModesRemoved,
    int VocabularyAdded,
    int VocabularyReplaced,
    int VocabularySkipped,
    ImmutableArray<Guid> MissingSelectedModeIds);

public sealed record BackupImportSummary(
    bool SettingsImported,
    int ModesAdded,
    int ModesReplaced,
    int ModesRemoved,
    int VocabularyAdded,
    int VocabularyReplaced,
    int VocabularySkipped);
