using System.Collections.ObjectModel;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class BackupModeSelectionViewModel : ViewModelBase
{
    private readonly Action _changed;
    private bool _isSelected;

    public BackupModeSelectionViewModel(Guid id, string name, bool isDefault, bool isSelected, Action changed)
    {
        Id = id;
        Name = name;
        IsDefault = isDefault;
        _isSelected = isSelected;
        _changed = changed;
    }

    public Guid Id { get; }
    public string Name { get; }
    public bool IsDefault { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            _changed();
        }
    }
}

public sealed class BackupViewModel : ViewModelBase
{
    private readonly ApplicationBackupService _service;
    private string _path = string.Empty;
    private bool _exportSettings = true;
    private bool _exportModes = true;
    private bool _exportVocabulary = true;
    private bool _exportCredentials;
    private bool _plaintextCredentialExportAcknowledged;
    private bool _importSettings = true;
    private bool _importModes = true;
    private bool _importVocabulary = true;
    private bool _importCredentials;
    private bool _replaceAllModes = true;
    private VocabularyConflictPolicy _vocabularyConflictPolicy = VocabularyConflictPolicy.Replace;
    private BackupContents? _contents;
    private BackupMergePreview? _preview;
    private string? _inspectedJson;
    private string _contentsSummary = "Inspect a universal backup to see its contents.";
    private string _previewSummary = "Preview the selected import before confirming.";
    private string _operationSummary = string.Empty;
    private bool _importConfirmed;

    public BackupViewModel(ApplicationBackupService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InspectCommand = new AsyncCommand(_ => InspectAsync());
        PreviewCommand = new AsyncCommand(_ => PreviewAsync());
        ExportCommand = new AsyncCommand(_ => ExportAsync());
        ImportCommand = new AsyncCommand(_ => ImportAsync(), _ => CanConfirmImport);
    }

    public string Path
    {
        get => _path;
        set
        {
            if (!Set(ref _path, value)) return;
            ClearInspection();
        }
    }
    public bool ExportSettings { get => _exportSettings; set => Set(ref _exportSettings, value); }
    public bool ExportModes { get => _exportModes; set => Set(ref _exportModes, value); }
    public bool ExportVocabulary { get => _exportVocabulary; set => Set(ref _exportVocabulary, value); }
    public bool ExportCredentials
    {
        get => _exportCredentials;
        set
        {
            if (!Set(ref _exportCredentials, value)) return;
            if (!value) PlaintextCredentialExportAcknowledged = false;
        }
    }
    public bool PlaintextCredentialExportAcknowledged
    {
        get => _plaintextCredentialExportAcknowledged;
        set => Set(ref _plaintextCredentialExportAcknowledged, value);
    }
    public string PlaintextCredentialWarning =>
        "Warning: exported API keys are stored as plaintext in this backup file. Anyone with the file can use them. Store it securely and delete it when finished.";
    public bool ImportSettings { get => _importSettings; set { if (Set(ref _importSettings, value)) InvalidatePreview(); } }
    public bool ImportModes { get => _importModes; set { if (Set(ref _importModes, value)) InvalidatePreview(); } }
    public bool ImportVocabulary { get => _importVocabulary; set { if (Set(ref _importVocabulary, value)) InvalidatePreview(); } }
    public bool ImportCredentials { get => _importCredentials; set { if (Set(ref _importCredentials, value)) InvalidatePreview(); } }
    public bool ReplaceAllModes { get => _replaceAllModes; set { if (Set(ref _replaceAllModes, value)) InvalidatePreview(); } }
    public VocabularyConflictPolicy VocabularyConflictPolicy
    {
        get => _vocabularyConflictPolicy;
        set
        {
            if (!Set(ref _vocabularyConflictPolicy, value)) return;
            Notify(nameof(SkipVocabularyConflicts));
            Notify(nameof(ReplaceVocabularyConflicts));
            InvalidatePreview();
        }
    }
    public bool SkipVocabularyConflicts
    {
        get => VocabularyConflictPolicy == VocabularyConflictPolicy.Skip;
        set { if (value) VocabularyConflictPolicy = VocabularyConflictPolicy.Skip; }
    }
    public bool ReplaceVocabularyConflicts
    {
        get => VocabularyConflictPolicy == VocabularyConflictPolicy.Replace;
        set { if (value) VocabularyConflictPolicy = VocabularyConflictPolicy.Replace; }
    }
    public BackupContents? Contents { get => _contents; private set { if (Set(ref _contents, value)) NotifyInspection(); } }
    public BackupMergePreview? Preview { get => _preview; private set { if (Set(ref _preview, value)) NotifyPreview(); } }
    public string ContentsSummary { get => _contentsSummary; private set => Set(ref _contentsSummary, value); }
    public string PreviewSummary { get => _previewSummary; private set => Set(ref _previewSummary, value); }
    public string OperationSummary { get => _operationSummary; private set => Set(ref _operationSummary, value); }
    public bool HasInspectedBackup => Contents is not null;
    public bool ContainsImportableCredentials => Contents is { ContainsCredentials: true };
    public bool ContainsUnsupportedSensitiveData => Contents is { ContainsLicenseKey: true };
    public string SensitiveDataNotice => Contents switch
    {
        { ContainsCredentials: true, ContainsLicenseKey: true } =>
            "API keys can be imported only when explicitly selected. Account, license, and entitlement keys are never imported.",
        { ContainsCredentials: true } => "API keys can be imported only when explicitly selected.",
        { ContainsLicenseKey: true } => "Account, license, and entitlement keys are never imported.",
        _ => "API keys are opt-in. Account, license, and entitlement keys are never exported or imported.",
    };
    public bool ImportConfirmed
    {
        get => _importConfirmed;
        set
        {
            if (!Set(ref _importConfirmed, value)) return;
            Notify(nameof(CanConfirmImport));
            ((AsyncCommand)ImportCommand).RaiseCanExecuteChanged();
        }
    }
    public bool CanConfirmImport => Contents is not null && Preview is not null && ImportConfirmed;
    public int PreviewModesAdded => Preview?.ModesAdded ?? 0;
    public int PreviewModesReplaced => Preview?.ModesReplaced ?? 0;
    public int PreviewModesRemoved => Preview?.ModesRemoved ?? 0;
    public int PreviewVocabularyAdded => Preview?.VocabularyAdded ?? 0;
    public int PreviewVocabularyReplaced => Preview?.VocabularyReplaced ?? 0;
    public int PreviewVocabularySkipped => Preview?.VocabularySkipped ?? 0;
    public ObservableCollection<BackupModeSelectionViewModel> ImportModeSelections { get; } = new();
    public UiStatus Status { get; } = new();
    public ICommand InspectCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public event EventHandler? Imported;

    public async Task InspectAsync(CancellationToken cancellationToken = default)
    {
        ClearInspection();
        if (!ValidatePath("Choose a backup file.")) return;
        try
        {
            Status.Busy("Inspecting universal backup…");
            var json = await File.ReadAllTextAsync(Path, cancellationToken);
            var result = _service.Inspect(json);
            if (result.IsFailure) { Status.Failure(result.Error!.Code, result.Error.Message); return; }
            _inspectedJson = json;
            Contents = result.Value!;
            foreach (var mode in Contents.Modes)
                ImportModeSelections.Add(new(mode.Id, mode.Name, mode.IsDefault, true, InvalidatePreview));
            ContentsSummary = $"Schema {Contents.SchemaVersion} from {Contents.Platform}; "
                + $"settings {(Contents.HasSettings ? "included" : "absent")}, {Contents.Modes.Length} modes, "
                + $"{Contents.VocabularyCount} vocabulary items.";
            Status.Success("Universal backup inspected");
        }
        catch (OperationCanceledException) { Status.Failure("backup.cancelled", "Backup inspection was cancelled."); }
        catch (IOException) { Status.Failure("backup.read_failed", "Could not read the universal backup file."); }
        catch (UnauthorizedAccessException) { Status.Failure("backup.read_denied", "Permission to read the universal backup file was denied."); }
        catch (Exception) { Status.Failure("backup.inspect_failed", "Could not inspect the universal backup."); }
    }

    public async Task PreviewAsync(CancellationToken cancellationToken = default)
    {
        InvalidatePreview();
        if (Contents is null) { Status.Failure("backup.inspect_required", "Inspect the backup before previewing its import."); return; }
        if (!ValidatePath("Choose a backup file.")) return;
        try
        {
            Status.Busy("Previewing universal backup import…");
            var result = await _service.PreviewImportAsync(_inspectedJson!, CreateImportSelection(), cancellationToken);
            if (result.IsFailure) { Status.Failure(result.Error!.Code, result.Error.Message); return; }
            Preview = result.Value!;
            PreviewSummary = $"Settings {(Preview.WillImportSettings ? "will be imported" : "unchanged")}; "
                + $"modes +{Preview.ModesAdded}, replace {Preview.ModesReplaced}, remove {Preview.ModesRemoved}; "
                + $"vocabulary +{Preview.VocabularyAdded}, replace {Preview.VocabularyReplaced}, skip {Preview.VocabularySkipped}; "
                + $"API keys {Preview.CredentialsToImport}.";
            Status.Success("Import preview ready for confirmation");
        }
        catch (OperationCanceledException) { Status.Failure("backup.cancelled", "Backup preview was cancelled."); }
        catch (IOException) { Status.Failure("backup.read_failed", "Could not read the universal backup file."); }
        catch (UnauthorizedAccessException) { Status.Failure("backup.read_denied", "Permission to read the universal backup file was denied."); }
        catch (Exception) { Status.Failure("backup.preview_failed", "Could not preview the universal backup import."); }
    }

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (!ValidatePath("Choose a backup destination.")) return;
        if (ExportCredentials && !PlaintextCredentialExportAcknowledged)
        {
            Status.Failure("backup.plaintext_acknowledgement_required", "Acknowledge the plaintext API-key warning before exporting credentials.");
            return;
        }
        try
        {
            Status.Busy("Exporting universal backup…");
            var selection = new BackupExportSelection(ExportSettings, ExportModes, ExportVocabulary, ExportCredentials);
            await File.WriteAllTextAsync(Path, await _service.ExportAsync(selection, cancellationToken), cancellationToken);
            OperationSummary = $"Exported settings: {ExportSettings}; modes: {ExportModes}; vocabulary: {ExportVocabulary}; "
                + $"API keys: {ExportCredentials}. Account, license, and entitlement keys were excluded.";
            Status.Success("Universal backup exported");
        }
        catch (OperationCanceledException) { Status.Failure("backup.cancelled", "Backup export was cancelled."); }
        catch (IOException) { Status.Failure("backup.export_failed", "Could not write the universal backup file."); }
        catch (UnauthorizedAccessException) { Status.Failure("backup.export_denied", "Permission to write the universal backup file was denied."); }
        catch (InvalidOperationException) { Status.Failure("backup.export_invalid", "The generated universal backup failed validation."); }
        catch (Exception) { Status.Failure("backup.export_failed", "Could not export the universal backup."); }
    }

    public async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConfirmImport) { Status.Failure("backup.confirmation_required", "Preview the selected import before confirming it."); return; }
        if (!ValidatePath("Choose a backup file.")) return;
        try
        {
            Status.Busy("Importing universal backup…");
            var result = await _service.ImportAsync(_inspectedJson!, CreateImportSelection(), cancellationToken);
            if (result.IsFailure) { InvalidatePreview(); Status.Failure(result.Error!.Code, result.Error.Message); return; }
            var summary = result.Value!;
            OperationSummary = $"Imported settings: {summary.SettingsImported}; modes +{summary.ModesAdded}, "
                + $"replaced {summary.ModesReplaced}, removed {summary.ModesRemoved}; vocabulary +{summary.VocabularyAdded}, "
                + $"replaced {summary.VocabularyReplaced}, skipped {summary.VocabularySkipped}; API keys {summary.CredentialsImported}. "
                + "Account, license, and entitlement keys were ignored.";
            InvalidatePreview();
            Status.Success("Universal backup imported");
            Imported?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { Status.Failure("backup.cancelled", "Backup import was cancelled."); }
        catch (IOException) { Status.Failure("backup.read_failed", "Could not read the universal backup file."); }
        catch (UnauthorizedAccessException) { Status.Failure("backup.read_denied", "Permission to read the universal backup file was denied."); }
        catch (Exception) { InvalidatePreview(); Status.Failure("backup.import_failed", "Could not import the universal backup."); }
    }

    private BackupImportSelection CreateImportSelection() => new(
        ImportSettings,
        ImportModes,
        ImportVocabulary,
        ReplaceAllModes ? BackupModeImportBehavior.ReplaceAll : BackupModeImportBehavior.MergeSelected,
        ReplaceAllModes ? null : System.Collections.Immutable.ImmutableHashSet.CreateRange(
            ImportModeSelections.Where(item => item.IsSelected).Select(item => item.Id)),
        VocabularyConflictPolicy,
        ImportCredentials);

    private bool ValidatePath(string message)
    {
        if (System.IO.Path.IsPathFullyQualified(Path)) return true;
        Status.Failure("backup.path_required", message);
        return false;
    }

    private void ClearInspection()
    {
        Contents = null;
        _inspectedJson = null;
        ImportModeSelections.Clear();
        ContentsSummary = "Inspect a universal backup to see its contents.";
        InvalidatePreview();
    }

    private void InvalidatePreview()
    {
        Preview = null;
        ImportConfirmed = false;
        PreviewSummary = "Preview the selected import before confirming.";
    }

    private void NotifyInspection()
    {
        Notify(nameof(HasInspectedBackup));
        Notify(nameof(ContainsUnsupportedSensitiveData));
        Notify(nameof(ContainsImportableCredentials));
        Notify(nameof(SensitiveDataNotice));
        Notify(nameof(CanConfirmImport));
        ((AsyncCommand)ImportCommand).RaiseCanExecuteChanged();
    }

    private void NotifyPreview()
    {
        Notify(nameof(CanConfirmImport));
        Notify(nameof(PreviewModesAdded));
        Notify(nameof(PreviewModesReplaced));
        Notify(nameof(PreviewModesRemoved));
        Notify(nameof(PreviewVocabularyAdded));
        Notify(nameof(PreviewVocabularyReplaced));
        Notify(nameof(PreviewVocabularySkipped));
        ((AsyncCommand)ImportCommand).RaiseCanExecuteChanged();
    }
}
