using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using VocabularyWords = HyperWhisper.PortableApplication.Persistence.VocabularyWords;
using HyperWhisper.Models;
// TODO-verify (Windows/CI): Rust shared-core swap — backup validate/normalize/migrate.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// BACKUP SERVICE
///
/// Exports and imports app configuration and data as .hwbackup.json files
/// using the universal cross-platform format (schemaVersion 2).
/// Compatible with macOS exports for cross-platform migration.
/// </summary>
public class BackupService
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static BackupService? _instance;
    private static readonly object _singletonLock = new();
    private static readonly object _dbLock = new();

    public static BackupService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_singletonLock)
                {
                    _instance ??= new BackupService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private static readonly JsonSerializerOptions UniversalJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const int ImportBatchSize = 200;

    // =========================================================================
    // EXPORT
    // =========================================================================

    /// <summary>
    /// Exports app data to a .hwbackup.json universal cross-platform backup file.
    /// Legacy overload: includes settings, modes and vocabulary; API keys gated by flag.
    /// </summary>
    public Result<string> Export(string filePath, bool includeApiKeys)
    {
        return Export(filePath, new BackupExportSelection
        {
            IncludeSettings = true,
            IncludeModes = true,
            IncludeVocabulary = true,
            IncludeApiKeys = includeApiKeys
        });
    }

    /// <summary>
    /// Exports app data to a .hwbackup.json universal cross-platform backup file,
    /// including only the sections selected in <paramref name="selection"/>.
    /// Deselected sections are written as null and omitted from the JSON.
    /// </summary>
    public Result<string> Export(string filePath, BackupExportSelection selection)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<string>.Failure("File path cannot be empty");

        if (selection == null)
            throw new ArgumentException("Selection cannot be null", nameof(selection));

        if (!selection.IncludeSettings && !selection.IncludeModes
            && !selection.IncludeVocabulary && !selection.IncludeApiKeys)
            return Result<string>.Failure("No sections selected for export");

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var modes = selection.IncludeModes
                ? ModeService.Instance.GetAllModes()
                : null;
            var vocabulary = selection.IncludeVocabulary
                ? VocabularyService.Instance.GetAll()
                : null;

            var backup = new UniversalBackup
            {
                SchemaVersion = 2,
                ExportDate = DateTime.UtcNow,
                AppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                Platform = "windows",
                Settings = selection.IncludeSettings
                    ? UniversalBackupMapper.MapSettings(SettingsService.Instance)
                    : null,
                Modes = modes?.Select(UniversalBackupMapper.MapMode).ToList(),
                Vocabulary = vocabulary?.Select(UniversalBackupMapper.MapVocabularyItem).ToList(),
                // Windows platform settings live alongside the cross-platform settings
                // section. BuildPlatformExtensions also re-attaches the macOS / Linux
                // top-level slices preserved from the last import.
                PlatformExtensions = selection.IncludeSettings
                    ? UniversalBackupMapper.BuildPlatformExtensions(SettingsService.Instance)
                    : null,
                // Top-level keys a newer build wrote and this one has no property for.
                Additional = selection.IncludeSettings
                    ? UniversalBackupMapper.ReadUnknownRootKeys(SettingsService.Instance)
                    : null
            };

            if (selection.IncludeApiKeys)
                backup.ApiKeys = UniversalBackupMapper.MapApiKeys(ApiKeyService.Instance);

            var json = JsonSerializer.Serialize(backup, UniversalJsonOptions);

            // TODO-verify (Windows/CI): Rust shared-core swap — self-validate the
            // serialized document with the shared core before writing it to disk, so
            // we never emit a structurally invalid backup (mirrors the macOS export
            // self-check). File I/O + JsonSerializer stay native.
            var structureError = ValidateBackupStructure(backup);
            if (structureError != null)
            {
                LoggingService.Error($"BackupService: Export self-validation failed: {structureError}");
                return Result<string>.Failure($"Export failed: {structureError}");
            }

            File.WriteAllText(filePath, json);

            LoggingService.Info($"BackupService: Exported backup to {filePath} (settings={selection.IncludeSettings}, modes={modes?.Count ?? 0}, vocab={vocabulary?.Count ?? 0}, apiKeys={selection.IncludeApiKeys})");
            return Result<string>.Success(filePath);
        }
        catch (Exception ex)
        {
            LoggingService.Error("BackupService: Export failed", ex);
            return Result<string>.Failure($"Export failed: {ex.Message}");
        }
    }

    // =========================================================================
    // INSPECT
    // =========================================================================

    /// <summary>
    /// Parses a backup file and reports which sections are present (non-null),
    /// without applying any changes. Used to drive the selective-import UI.
    /// </summary>
    public Result<BackupContents> Inspect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<BackupContents>.Failure("File path cannot be empty");

        if (!File.Exists(filePath))
            return Result<BackupContents>.Failure("Backup file not found");

        try
        {
            var json = File.ReadAllText(filePath);
            var (backup, loadError) = LoadValidatedBackup(json);
            if (backup == null)
                return Result<BackupContents>.Failure(loadError!);

            var contents = new BackupContents
            {
                Platform = backup.Platform,
                // Key-presence is the source of truth: a present-but-empty section (e.g. "vocabulary": [])
                // is reported as present (tickable, no-op on import), NOT absent.
                HasSettings = backup.Settings != null,
                HasModes = backup.Modes != null,
                ModeCount = backup.Modes?.Count ?? 0,
                HasVocabulary = backup.Vocabulary != null,
                VocabularyCount = backup.Vocabulary?.Count ?? 0,
                HasApiKeys = backup.ApiKeys != null,
                HasLicense = !string.IsNullOrEmpty(backup.LicenseKey)
            };

            return Result<BackupContents>.Success(contents);
        }
        catch (JsonException ex)
        {
            LoggingService.Error("BackupService: Invalid JSON while inspecting backup", ex);
            return Result<BackupContents>.Failure(InvalidBackupFieldMessage(ex));
        }
        catch (Exception ex)
        {
            LoggingService.Error("BackupService: Inspect failed", ex);
            return Result<BackupContents>.Failure($"Could not read backup file: {ex.Message}");
        }
    }

    // =========================================================================
    // IMPORT
    // =========================================================================

    /// <summary>
    /// Imports app data from a .hwbackup.json backup file.
    /// </summary>
    public Result<string> Import(string filePath, bool replaceExisting)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<string>.Failure("File path cannot be empty");

        if (!File.Exists(filePath))
            return Result<string>.Failure("Backup file not found");

        try
        {
            var json = File.ReadAllText(filePath);
            var (backup, loadError) = LoadValidatedBackup(json);
            if (backup == null)
                return Result<string>.Failure(loadError!);

            bool settingsImported = false;
            int modesImported = 0;
            int vocabImported = 0;
            bool apiKeysImported = false;

            // 1. Apply cross-platform + Windows platform settings.
            // Marshal onto the UI thread (see ApplyImport): the setters mutate an
            // in-memory object graph Save() serializes unguarded and fire UI-affine
            // SettingsChanged handlers, neither of which is safe off a Task.
            var settings = SettingsService.Instance;
            // What settings.json held before step 1, put back if the DB step fails
            // (issue #1614). Null until the batch below actually starts.
            SettingsService.ImportSnapshot? settingsBefore = null;
            {
                var universalSettings = backup.Settings;
                var platformExtensions = backup.PlatformExtensions;
                var importedBackup = backup;
                settings.ApplyImport(() =>
                {
                    settingsBefore = settings.CaptureImportSnapshot();

                    if (universalSettings != null)
                    {
                        try
                        {
                            UniversalBackupMapper.ApplySettings(universalSettings, settings);
                            settingsImported = true;
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error("BackupService: Failed to apply settings", ex);
                        }
                    }

                    // Apply Windows-specific platform settings (only from Windows
                    // backups); ALSO preserves the macOS / Linux top-level slices.
                    UniversalBackupMapper.ApplyWindowsPlatformSettings(
                        platformExtensions, settings, replaceExisting);

                    // Preserve unknown top-level keys. Deliberately here and NOT in
                    // LoadValidatedBackup — that helper also serves Inspect, and
                    // merely LOOKING at a file must not write settings.
                    UniversalBackupMapper.CaptureUnknownRootKeys(importedBackup, settings);
                });
            }

            try
            {
                // Mapping is inside the guarded step too: a row that cannot be
                // mapped fails the import exactly like a row the database refuses.
                var modes = backup.Modes?.Select(UniversalBackupMapper.MapToMode).ToList();
                var vocabulary = backup.Vocabulary?.Select(UniversalBackupMapper.MapToVocabularyItem).ToList();

                if (replaceExisting)
                {
                    (modesImported, vocabImported) = ReplaceDatabaseEntities(modes, vocabulary);
                }
                else
                {
                    // 3 + 4. Merge modes and vocabulary in ONE transaction, the same
                    // all-or-nothing shape as the replace branch (issue #1605).
                    (modesImported, vocabImported) = MergeDatabaseEntities(modes, vocabulary);
                }
            }
            catch (Exception ex)
            {
                // The transaction rolled the modes and vocabulary back, so the
                // backup's settings must not stay applied on their own (issue #1614).
                var settingsRestored = RestoreSettingsAfterFailedImport(settings, settingsBefore, "import");
                throw new ImportDatabaseStepException(ex, settingsRestored);
            }

            // A backup carries whatever `isDefault` its own machine held, so a
            // restore is the realistic way a database ends up with two default
            // modes or none (issue #536). Both branches above write straight
            // into the DbSet, so this is where the invariant is re-established.
            if (modesImported > 0)
            {
                ModeService.Instance.EnforceDefaultModeInvariant();
                NotifyModesChanged("import");
            }

            // 5. Apply API keys
            if (backup.ApiKeys != null)
            {
                try
                {
                    apiKeysImported = UniversalBackupMapper.ApplyApiKeys(backup.ApiKeys, ApiKeyService.Instance);
                    if (!apiKeysImported)
                        LoggingService.Warn("BackupService: One or more API keys could not be written to Credential Manager");
                }
                catch (Exception ex)
                {
                    LoggingService.Error("BackupService: Failed to apply API keys", ex);
                }
            }

            var platformNote = backup.Platform != "windows"
                ? $" (from {backup.Platform} backup)"
                : "";
            var summary = $"Settings: {(settingsImported ? "yes" : "no")}, Modes: {modesImported}, Vocabulary: {vocabImported}, API Keys: {(apiKeysImported ? "yes" : "no")}{platformNote}";
            LoggingService.Info($"BackupService: Import completed — {summary}");
            return Result<string>.Success(summary);
        }
        catch (JsonException ex)
        {
            LoggingService.Error("BackupService: Invalid JSON in backup", ex);
            return Result<string>.Failure(InvalidBackupFieldMessage(ex));
        }
        catch (ImportDatabaseStepException ex)
        {
            LoggingService.Error("BackupService: Import failed in the database step", ex.InnerException ?? ex);
            return Result<string>.Failure(ex.UserMessage);
        }
        catch (Exception ex)
        {
            LoggingService.Error("BackupService: Import failed", ex);
            return Result<string>.Failure($"Import failed: {ex.Message}");
        }
    }

    // =========================================================================
    // SELECTIVE IMPORT (merge-only — never wipes existing data)
    // =========================================================================

    /// <summary>
    /// Imports only the user-selected, present sections from a backup file using
    /// merge semantics. NEVER clears existing data. Vocabulary is merged by Word
    /// (case-insensitive, trimmed) so cross-platform files with foreign UUIDs do
    /// not create duplicates.
    /// </summary>
    public Result<ImportSummary> ImportSelective(string filePath, ImportSelection selection)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<ImportSummary>.Failure("File path cannot be empty");

        if (selection == null)
            throw new ArgumentException("Selection cannot be null", nameof(selection));

        if (!File.Exists(filePath))
            return Result<ImportSummary>.Failure("Backup file not found");

        try
        {
            var json = File.ReadAllText(filePath);
            var (backup, loadError) = LoadValidatedBackup(json);
            if (backup == null)
                return Result<ImportSummary>.Failure(loadError!);

            var summary = new ImportSummary();
            // What settings.json held before step 1, put back if a DB step fails
            // (issue #1614). Null when step 1 did not run.
            SettingsService.ImportSnapshot? settingsBefore = null;

            // 1. Settings (cross-platform + Windows platform settings) — merge.
            if (selection.IncludeSettings && backup.Settings != null)
            {
                try
                {
                    // ImportSelective runs under Task.Run, but the SettingsService setters
                    // these calls drive mutate an in-memory object graph that Save()
                    // serializes (unguarded) and fire UI-affine SettingsChanged handlers.
                    // ApplyImport marshals the whole batch onto the UI thread so the
                    // mutations stay single-threaded with normal UI usage and the change
                    // notifications fire on the UI thread.
                    var settings = SettingsService.Instance;
                    var universalSettings = backup.Settings;
                    var platformExtensions = backup.PlatformExtensions;
                    var importedBackup = backup;
                    settings.ApplyImport(() =>
                    {
                        settingsBefore = settings.CaptureImportSnapshot();
                        UniversalBackupMapper.ApplySettings(universalSettings, settings);
                        UniversalBackupMapper.ApplyWindowsPlatformSettings(
                            platformExtensions, settings);
                        UniversalBackupMapper.CaptureUnknownRootKeys(importedBackup, settings);
                    });
                    summary.SettingsImported = true;
                }
                catch (OperationCanceledException ex)
                {
                    LoggingService.Info($"BackupService: Settings import canceled: {ex.Message}");
                    return Result<ImportSummary>.Failure("Import canceled because the app is shutting down");
                }
                catch (Exception ex)
                {
                    LoggingService.Error("BackupService: Failed to apply settings (selective)", ex);
                }
            }

            // Steps 2 and 3 are the database step, written in ONE transaction: a
            // failure anywhere in it rolls back the modes AND the vocabulary, and
            // then the backup's settings are put back too (issues #1605, #1614).
            // "Import failed" therefore means nothing changed.
            var importModes = selection.IncludeModes && backup.Modes is { Count: > 0 };
            var importVocabulary = selection.IncludeVocabulary && backup.Vocabulary is { Count: > 0 };
            // With neither section selected there is no database step at all: a
            // settings-only or API-keys-only import opens no context and no
            // transaction, so a locked or busy database cannot fail it (counts stay 0).
            if (importModes || importVocabulary)
            {
                try
                {
                    // 2. Modes — merge by Id (existing upsert semantics).
                    // 3. Vocabulary — merge by Word (case-insensitive, trimmed).
                    // Mapping is inside the guarded step: a row that cannot be mapped
                    // fails the import exactly like a row the database refuses.
                    var modes = importModes
                        ? backup.Modes!.Select(UniversalBackupMapper.MapToMode).ToList()
                        : null;
                    var (modesImported, added, conflicts) = MergeModesAndVocabulary(
                        modes,
                        importVocabulary ? backup.Vocabulary : null,
                        selection.VocabularyConflict);
                    summary.ModesImported = modesImported;
                    summary.VocabularyAdded = added;
                    summary.VocabularyConflicts = conflicts;
                }
                catch (Exception ex)
                {
                    var settingsRestored = RestoreSettingsAfterFailedImport(
                        SettingsService.Instance, settingsBefore, "selective import");
                    throw new ImportDatabaseStepException(ex, settingsRestored);
                }
            }

            if (importModes)
            {
                // A backup carries whatever `isDefault` its own machine held, and
                // a merge can therefore land a second default beside the local
                // one, or none at all (issue #536). The merge above goes straight
                // into the DbSet, so this runs after the commit, on committed data.
                ModeService.Instance.EnforceDefaultModeInvariant();
                NotifyModesChanged("selective import");
            }

            if (importVocabulary)
            {
                // Notify listeners (after the commit) so the vocab UI refreshes.
                try
                {
                    VocabularyService.Instance.NotifyVocabularyChanged();
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"BackupService: Failed to raise VocabularyChanged after merge: {ex.Message}");
                }
            }

            // 4. API keys — merge (only non-empty keys written).
            if (selection.IncludeApiKeys && backup.ApiKeys != null)
            {
                try
                {
                    // Count what the backup actually carries, through the same mapping
                    // (writers that only count), so the page can tell "restored" from
                    // "there was nothing to restore": an export with no keys configured
                    // still writes an empty apiKeys object (#2377).
                    var keysInBackup = 0;
                    UniversalBackupMapper.ApplyApiKeys(backup.ApiKeys, (_, _) => keysInBackup++, (_, _) => keysInBackup++);
                    summary.ApiKeysInBackup = keysInBackup;
                    summary.ApiKeysImported = UniversalBackupMapper.ApplyApiKeys(backup.ApiKeys, ApiKeyService.Instance);
                    if (!summary.ApiKeysImported)
                        LoggingService.Warn("BackupService: One or more API keys could not be written to Credential Manager (selective)");
                }
                catch (Exception ex)
                {
                    LoggingService.Error("BackupService: Failed to apply API keys (selective)", ex);
                }
            }

            summary.SourcePlatform = backup.Platform;
            LoggingService.Info($"BackupService: Selective import completed — {summary.ToLogString()}");
            return Result<ImportSummary>.Success(summary);
        }
        catch (JsonException ex)
        {
            LoggingService.Error("BackupService: Invalid JSON in backup (selective)", ex);
            return Result<ImportSummary>.Failure(InvalidBackupFieldMessage(ex));
        }
        catch (ImportDatabaseStepException ex)
        {
            LoggingService.Error("BackupService: Selective import failed in the database step", ex.InnerException ?? ex);
            return Result<ImportSummary>.Failure(ex.UserMessage);
        }
        catch (Exception ex)
        {
            LoggingService.Error("BackupService: Selective import failed", ex);
            return Result<ImportSummary>.Failure($"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes how many incoming vocabulary words are new vs. already present
    /// (matched by Word, case-insensitive + trimmed), without writing anything.
    /// Used to build the pre-merge confirmation summary.
    /// </summary>
    public Result<VocabularyMergePreview> PreviewVocabularyMerge(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<VocabularyMergePreview>.Failure("File path cannot be empty");

        if (!File.Exists(filePath))
            return Result<VocabularyMergePreview>.Failure("Backup file not found");

        try
        {
            var json = File.ReadAllText(filePath);
            // Same gate as Inspect/ImportSelective so the preview never reports
            // counts for a file the actual import would reject.
            var (backup, loadError) = LoadValidatedBackup(json);
            if (backup == null)
                return Result<VocabularyMergePreview>.Failure(loadError!);

            var incoming = backup.Vocabulary ?? new List<UniversalVocabularyItem>();

            HashSet<string> existingWords;
            lock (_dbLock)
            {
                using var context = new HyperWhisperDbContext();
                existingWords = context.VocabularyItems
                    .Select(v => v.Word)
                    .ToList()
                    .Select(w => (w ?? string.Empty).Trim().ToLowerInvariant())
                    .ToHashSet();
            }

            int newCount = 0;
            int conflictCount = 0;

            // Mirror MergeVocabulary exactly: a word inserted earlier in the file
            // counts as a conflict if it recurs later (same as the real merge).
            foreach (var (word, _) in VocabularyWordLines(incoming))
            {
                var key = word.ToLowerInvariant();

                if (existingWords.Contains(key))
                {
                    conflictCount++;
                }
                else
                {
                    newCount++;
                    existingWords.Add(key);
                }
            }

            return Result<VocabularyMergePreview>.Success(
                new VocabularyMergePreview { NewCount = newCount, ConflictCount = conflictCount });
        }
        catch (JsonException ex)
        {
            LoggingService.Error("BackupService: Invalid JSON while previewing vocab merge", ex);
            return Result<VocabularyMergePreview>.Failure(InvalidBackupFieldMessage(ex));
        }
        catch (Exception ex)
        {
            LoggingService.Error("BackupService: Vocab merge preview failed", ex);
            return Result<VocabularyMergePreview>.Failure($"Could not read backup file: {ex.Message}");
        }
    }

    /// <summary>
    /// Import Selected's database step: merges <paramref name="modes"/> (upsert by Id,
    /// batched) and <paramref name="vocabulary"/> (by Word, see MergeVocabulary) in ONE
    /// transaction, committed once at the end. A throw anywhere rolls back both, so the
    /// database holds the old modes and vocabulary or all of the backup's, never a part
    /// (issues #1605, #1614). Either list may be null to skip that section.
    /// Returns (modesImported, vocabularyAdded, vocabularyConflicts).
    /// </summary>
    private static (int modesImported, int added, int conflicts) MergeModesAndVocabulary(
        List<Mode>? modes,
        List<UniversalVocabularyItem>? vocabulary,
        VocabConflict conflict)
    {
        int modesImported = 0;
        int added = 0;
        int conflicts = 0;

        lock (_dbLock)
        {
            using var context = new HyperWhisperDbContext();
            using var transaction = context.Database.BeginTransaction();

            if (modes != null && modes.Count > 0)
            {
                modesImported = MergeEntitySet(context, context.Modes, modes);
            }

            if (vocabulary != null && vocabulary.Count > 0)
            {
                (added, conflicts) = MergeVocabulary(context, vocabulary, conflict);
            }

            // Disposing an uncommitted transaction rolls it back, so any throw
            // above undoes the modes and the vocabulary alike.
            transaction.Commit();
        }

        return (modesImported, added, conflicts);
    }

    /// <summary>
    /// One entry per word line of the incoming items, trimmed, blank lines dropped (issue
    /// #1777). A backup made before #1475 can carry a word with line breaks inside it; it
    /// imports as one word per line, the way the add field saves a paste, and each line
    /// carries the item's replacement, sort order and source.
    /// </summary>
    private static IEnumerable<(string Word, UniversalVocabularyItem Item)> VocabularyWordLines(
        IEnumerable<UniversalVocabularyItem> items) =>
        items.SelectMany(item => VocabularyWords.SplitLines(item.Word).Select(line => (line, item)));

    /// <summary>
    /// Merges incoming vocabulary items by Word (case-insensitive, trimmed) inside the
    /// caller's context and transaction; the caller commits. Existing words are never
    /// deleted. On a Word match the conflict policy decides whether to Skip (leave
    /// existing) or Replace (update Replacement/SortOrder/Source on the existing row,
    /// keeping its Id). Unmatched words are inserted with a fresh Guid and
    /// CreatedDate=UtcNow. Returns (added, conflicts).
    /// </summary>
    private static (int added, int conflicts) MergeVocabulary(
        HyperWhisperDbContext context,
        List<UniversalVocabularyItem> items,
        VocabConflict conflict)
    {
        int added = 0;
        int conflicts = 0;

        // Snapshot existing words once, keyed by trimmed lowercase Word.
        // Last-writer-wins if the DB already contains case-variant duplicates.
        var existingByWord = new Dictionary<string, VocabularyItem>();
        foreach (var existing in context.VocabularyItems)
        {
            var key = (existing.Word ?? string.Empty).Trim().ToLowerInvariant();
            if (key.Length == 0)
                continue;
            existingByWord[key] = existing;
        }

        var nextSortOrder = context.VocabularyItems.Any()
            ? context.VocabularyItems.Max(v => v.SortOrder) + 1
            : 0;

        foreach (var (trimmedWord, incoming) in VocabularyWordLines(items))
        {
            var key = trimmedWord.ToLowerInvariant();

            if (existingByWord.TryGetValue(key, out var match))
            {
                // Word already present (either pre-existing or inserted earlier this pass).
                conflicts++;

                if (conflict == VocabConflict.Replace)
                {
                    match.Replacement = string.IsNullOrWhiteSpace(incoming.Replacement)
                        ? null
                        : incoming.Replacement.Trim();
                    match.SortOrder = incoming.SortOrder;
                    match.Source = incoming.Source;
                    // Keep existing Id and CreatedDate.
                }
                // Skip: leave the existing row untouched.
                continue;
            }

            // New word — insert with a fresh identity.
            var item = new VocabularyItem
            {
                Id = Guid.NewGuid(),
                Word = trimmedWord,
                Replacement = string.IsNullOrWhiteSpace(incoming.Replacement)
                    ? null
                    : incoming.Replacement.Trim(),
                SortOrder = nextSortOrder++,
                Source = incoming.Source,
                CreatedDate = DateTime.UtcNow
            };
            context.VocabularyItems.Add(item);
            existingByWord[key] = item;
            added++;
        }

        context.SaveChanges();

        return (added, conflicts);
    }

    /// <summary>
    /// Tells the rest of the app the mode rows changed (issue #1592). The import
    /// wrote them straight into the DbSet, which raises nothing, so the tray menus
    /// and the status bar kept the list they had before. Never throws: the rows are
    /// committed, and a subscriber's fault must not turn the import into a failure.
    /// </summary>
    private static void NotifyModesChanged(string operation)
    {
        try
        {
            ModeService.Instance.NotifyModesChanged();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"BackupService: Failed to raise ModeChanged after {operation}: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts settings.json back to <paramref name="before"/> after an import's database
    /// step failed (issue #1614). Never throws: the caller is already failing with the
    /// database error, and that is the message the user must see, so a restore that
    /// itself fails is only logged. Returns false only when that restore threw, so the
    /// backup's settings may still be applied.
    /// </summary>
    private static bool RestoreSettingsAfterFailedImport(
        SettingsService settings,
        SettingsService.ImportSnapshot? before,
        string operation)
    {
        // Step 1 never ran (settings not selected, or the batch was refused).
        if (before == null)
            return true;

        try
        {
            if (settings.RestoreImportSnapshot(before))
                LoggingService.Info($"BackupService: the {operation} failed in the database step; restored the settings it had applied");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Error(
                $"BackupService: the {operation} failed in the database step and the settings it applied could not be restored", ex);
            return false;
        }
    }

    /// <summary>
    /// An import's database step failed and its transaction rolled back (issue #1695).
    /// Carries the plain sentence the user sees; the EF Core / SQLite exception it wraps
    /// ("See the inner exception for details") goes to the log only.
    /// </summary>
    private sealed class ImportDatabaseStepException : Exception
    {
        public ImportDatabaseStepException(Exception inner, bool settingsRestored)
            : base("The import's database step failed", inner)
        {
            UserMessage = settingsRestored
                ? "Import failed: the database refused the change. Nothing was imported."
                : "Import failed: the database refused the change. Your modes and vocabulary were not changed.";
        }

        public string UserMessage { get; }
    }

    // =========================================================================
    // SHARED-CORE STRUCTURAL VALIDATION
    // =========================================================================

    // TODO-verify (Windows/CI): Rust shared-core swap.
    // Structural validation of a universal-v2 backup document now delegates to the
    // Rust shared core via the JSON-string boundary. The core checks the same
    // load-bearing invariants the hand-rolled C# did (root object, the four
    // required top-level fields, schemaVersion == 2, platform enum, modes/vocab
    // arrays with required ids). Empty list == structurally valid.
    //
    // Boundary: we already hold the deserialized typed model; serialize it back to
    // a JSON string with the same JsonSerializer options used for file I/O, hand
    // that string to ValidateBackupJson, and translate any HwValidationError list
    // into a single user-facing failure message. Native JsonSerializer (de)serde,
    // file I/O, and the Windows platformExtensions mapper are untouched.
    /// <summary>
    /// Parse + structurally validate a backup JSON document. Returns the parsed
    /// backup, or a user-facing error message when the JSON does not parse or
    /// fails the shared core's structural validation. Single implementation for
    /// the Inspect / Import / ImportSelective / PreviewVocabularyMerge sites.
    /// </summary>
    private static (UniversalBackup? Backup, string? Error) LoadValidatedBackup(string json)
    {
        UniversalBackup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<UniversalBackup>(json, UniversalJsonOptions);
        }
        catch (JsonException ex)
        {
            // The serializer throws the same exception for text that is not JSON and
            // for a well-formed document with one wrong-typed field (issue #2095).
            // Only the first is "not valid JSON"; the second names the field.
            LoggingService.Error("BackupService: Backup JSON did not deserialize", ex);
            return (null, IsWellFormedJson(json)
                ? InvalidBackupFieldMessage(ex)
                : "Invalid backup file: not valid JSON");
        }
        if (backup == null)
            return (null, "Invalid backup file: could not parse JSON");

        // TODO-verify (Windows/CI): Rust shared-core swap — structural validation
        // BEFORE any caller mutates state (replaces the hand-rolled
        // `SchemaVersion != 2` check).
        var structureError = ValidateBackupStructure(backup);
        return structureError != null ? (null, structureError) : (backup, null);
    }

    private static bool IsWellFormedJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The user-facing message for a JSON document that parsed but holds a value of the
    /// wrong type, in the shared core validator's shape ("&lt;path&gt;: must be ..."), e.g.
    /// "Invalid backup file: vocabulary[0].sortOrder: must be an integer" (issue #2095).
    /// The serializer's own text names a .NET type, so it stays in the log only.
    /// </summary>
    private static string InvalidBackupFieldMessage(JsonException ex)
    {
        var path = ex.Path;
        if (string.IsNullOrEmpty(path) || path == "$")
            path = "$";
        else if (path.StartsWith("$.", StringComparison.Ordinal))
            path = path[2..];
        else if (path.StartsWith('$'))
            path = path[1..];

        return $"Invalid backup file: {path}: {ExpectedTypeFromMessage(ex.Message)}";
    }

    private static string ExpectedTypeFromMessage(string message)
    {
        // IntegralInt32JsonConverter's own messages ("Expected a whole number ...").
        if (message.StartsWith("Expected a whole number", StringComparison.Ordinal))
            return "must be an integer";

        // The default converters: "The JSON value could not be converted to <type>. Path: ...".
        const string marker = "could not be converted to ";
        var at = message.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return "has the wrong type";

        var type = message[(at + marker.Length)..];
        var end = type.IndexOf(". Path:", StringComparison.Ordinal);
        if (end >= 0)
            type = type[..end];
        type = type.TrimEnd('.');

        // Nullable`1[System.Int32] reads as its inner type.
        const string nullable = "System.Nullable`1[";
        if (type.StartsWith(nullable, StringComparison.Ordinal) && type.EndsWith(']'))
            type = type[nullable.Length..^1];

        if (type.EndsWith("[]", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.IList`1", StringComparison.Ordinal))
            return "must be a list";

        return type switch
        {
            "System.Int16" or "System.Int32" or "System.Int64"
                or "System.UInt16" or "System.UInt32" or "System.UInt64"
                or "System.Byte" or "System.SByte" => "must be an integer",
            "System.Double" or "System.Single" or "System.Decimal" => "must be a number",
            "System.Boolean" => "must be true or false",
            "System.String" => "must be a string",
            "System.DateTime" or "System.DateTimeOffset" => "must be a date",
            "System.Guid" => "must be a UUID",
            _ when type.StartsWith("HyperWhisper.", StringComparison.Ordinal)
                || type.StartsWith("System.Collections.Generic.Dictionary`2", StringComparison.Ordinal)
                => "must be an object",
            _ => "has the wrong type"
        };
    }

    private static string? ValidateBackupStructure(UniversalBackup backup)
    {
        // Re-serialize the typed model so the core validates the exact shape we parsed.
        string json = JsonSerializer.Serialize(backup, UniversalJsonOptions);

        List<HwValidationError> errors = HyperwhisperCoreMethods.ValidateBackupJson(json);
        if (errors.Count == 0)
            return null; // structurally valid

        // Surface the first error; log them all for diagnosis. The generated
        // HwValidationError positional record exposes `@path` + `@message`
        // (the FfiConverter reads value.@path / value.@message — see binding).
        var first = errors[0];
        LoggingService.Error(
            $"BackupService: Backup failed structural validation ({errors.Count} error(s)): "
            + string.Join("; ", errors.Select(e => $"{e.@path}: {e.@message}")));
        return $"Invalid backup file: {first.@path}: {first.@message}";
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    /// <summary>
    /// Replaces database-backed backup entities in one transaction so clear+import
    /// cannot leave modes or vocabulary partially replaced if a later batch fails.
    /// </summary>
    private static (int modesImported, int vocabularyImported) ReplaceDatabaseEntities(
        List<Mode>? modes,
        List<VocabularyItem>? vocabulary)
    {
        int modesImported = 0;
        int vocabularyImported = 0;

        lock (_dbLock)
        {
            using var context = new HyperWhisperDbContext();
            using var transaction = context.Database.BeginTransaction();

            if (modes != null && modes.Count > 0)
            {
                modesImported = ReplaceEntitySet(context, context.Modes, modes);
            }

            if (vocabulary != null && vocabulary.Count > 0)
            {
                vocabularyImported = ReplaceEntitySet(context, context.VocabularyItems, vocabulary);
            }

            transaction.Commit();
        }

        return (modesImported, vocabularyImported);
    }

    private static int ReplaceEntitySet<T>(
        HyperWhisperDbContext context,
        DbSet<T> dbSet,
        List<T> items) where T : class
    {
        int count = 0;

        dbSet.RemoveRange(dbSet);
        context.SaveChanges();
        context.ChangeTracker.Clear();

        for (int i = 0; i < items.Count; i += ImportBatchSize)
        {
            var batch = items.Skip(i).Take(ImportBatchSize).ToList();
            dbSet.AddRange(batch);
            context.SaveChanges();
            count += batch.Count;
            context.ChangeTracker.Clear();
        }

        return count;
    }

    /// <summary>
    /// Merges (upserts by Id) database-backed backup entities in ONE transaction,
    /// committed once after the last batch. A crash, kill or failed batch therefore
    /// leaves the database exactly as it was, never a silent partial import
    /// (issue #1605). A failure rolls back and rethrows, so the caller's catch turns
    /// it into a failed Result as before.
    /// </summary>
    private static (int modesImported, int vocabularyImported) MergeDatabaseEntities(
        List<Mode>? modes,
        List<VocabularyItem>? vocabulary)
    {
        int modesImported = 0;
        int vocabularyImported = 0;

        lock (_dbLock)
        {
            using var context = new HyperWhisperDbContext();
            using var transaction = context.Database.BeginTransaction();

            if (modes != null && modes.Count > 0)
            {
                modesImported = MergeEntitySet(context, context.Modes, modes);
            }

            if (vocabulary != null && vocabulary.Count > 0)
            {
                vocabularyImported = MergeEntitySet(context, context.VocabularyItems, vocabulary);
            }

            // Disposing an uncommitted transaction rolls it back, so any throw
            // above undoes every batch already written.
            transaction.Commit();
        }

        return (modesImported, vocabularyImported);
    }

    /// <summary>
    /// Batched upsert by Id inside the caller's transaction. Each batch is flushed
    /// and the change tracker cleared so memory stays bounded, but nothing is
    /// committed here: the caller commits once.
    /// </summary>
    private static int MergeEntitySet<T>(
        HyperWhisperDbContext context,
        DbSet<T> dbSet,
        List<T> items) where T : class
    {
        int count = 0;

        for (int i = 0; i < items.Count; i += ImportBatchSize)
        {
            var batch = items.Skip(i).Take(ImportBatchSize);

            foreach (var item in batch)
            {
                var key = context.Entry(item).Property("Id").CurrentValue;
                // Find reads through the open transaction, so an Id repeated in a
                // later batch updates the row an earlier batch inserted.
                var existing = dbSet.Find(key);
                if (existing != null)
                    context.Entry(existing).CurrentValues.SetValues(item);
                else
                    dbSet.Add(item);
                count++;
            }

            context.SaveChanges();
            context.ChangeTracker.Clear();
        }

        return count;
    }
}

// =============================================================================
// SELECTION / RESULT TYPES (granular vocab-only backup bridge)
// =============================================================================

/// <summary>
/// Which sections to write when exporting. Deselected sections are omitted
/// from the JSON entirely (their UniversalBackup property is left null).
/// </summary>
public sealed class BackupExportSelection
{
    public bool IncludeSettings { get; set; } = true;
    public bool IncludeModes { get; set; } = true;
    public bool IncludeVocabulary { get; set; } = true;
    public bool IncludeApiKeys { get; set; }

    /// <summary>True when at least one section is selected.</summary>
    public bool HasAnySection =>
        IncludeSettings || IncludeModes || IncludeVocabulary || IncludeApiKeys;

    /// <summary>True when only vocabulary is selected (the vocab-only bridge case).</summary>
    public bool IsVocabularyOnly =>
        IncludeVocabulary && !IncludeSettings && !IncludeModes && !IncludeApiKeys;
}

/// <summary>
/// Describes which sections are present (non-null) in a backup file, as reported
/// by <see cref="BackupService.Inspect"/>. Drives the selective-import UI.
/// </summary>
public sealed class BackupContents
{
    public string Platform { get; set; } = "";
    public bool HasSettings { get; set; }
    public bool HasModes { get; set; }
    public int ModeCount { get; set; }
    public bool HasVocabulary { get; set; }
    public int VocabularyCount { get; set; }
    public bool HasApiKeys { get; set; }
    public bool HasLicense { get; set; }
}

/// <summary>Conflict policy for vocabulary merge when an incoming Word already exists.</summary>
public enum VocabConflict
{
    /// <summary>Leave the existing word/replacement untouched (default).</summary>
    Skip = 0,
    /// <summary>Update the existing row's Replacement/SortOrder/Source (keeps Id).</summary>
    Replace = 1
}

/// <summary>Which present sections the user chose to apply during a selective import.</summary>
public sealed class ImportSelection
{
    public bool IncludeSettings { get; set; }
    public bool IncludeModes { get; set; }
    public bool IncludeVocabulary { get; set; }
    public bool IncludeApiKeys { get; set; }

    /// <summary>How to handle vocabulary words that already exist (by Word).</summary>
    public VocabConflict VocabularyConflict { get; set; } = VocabConflict.Skip;
}

/// <summary>Pre-merge preview counts for the vocabulary confirmation dialog.</summary>
public sealed class VocabularyMergePreview
{
    public int NewCount { get; set; }
    public int ConflictCount { get; set; }
}

/// <summary>Outcome of a selective import.</summary>
public sealed class ImportSummary
{
    public bool SettingsImported { get; set; }
    public int ModesImported { get; set; }
    public int VocabularyAdded { get; set; }
    public int VocabularyConflicts { get; set; }
    public bool ApiKeysImported { get; set; }
    /// <summary>Non-empty keys the backup's apiKeys section carried (0 when it held none).</summary>
    public int ApiKeysInBackup { get; set; }
    public string SourcePlatform { get; set; } = "";

    public string ToLogString() =>
        $"settings={(SettingsImported ? "yes" : "no")}, modes={ModesImported}, " +
        $"vocabAdded={VocabularyAdded}, vocabConflicts={VocabularyConflicts}, " +
        $"apiKeys={(ApiKeysImported ? "yes" : "no")}, apiKeysInBackup={ApiKeysInBackup}, source={SourcePlatform}";
}
