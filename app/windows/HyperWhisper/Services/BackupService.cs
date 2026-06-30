using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
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
                // Windows platform settings live alongside the cross-platform settings section.
                PlatformExtensions = selection.IncludeSettings
                    ? UniversalBackupMapper.BuildPlatformExtensions(SettingsService.Instance)
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
            var backup = JsonSerializer.Deserialize<UniversalBackup>(json, UniversalJsonOptions);

            if (backup == null)
                return Result<BackupContents>.Failure("Invalid backup file: could not parse JSON");

            // TODO-verify (Windows/CI): Rust shared-core swap — structural validation
            // (replaces the hand-rolled `SchemaVersion != 2` check; core also enforces
            // required top-level fields + platform enum + modes/vocab array shape).
            var structureError = ValidateBackupStructure(backup);
            if (structureError != null)
                return Result<BackupContents>.Failure(structureError);

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
            return Result<BackupContents>.Failure("Invalid backup file: not valid JSON");
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
            var backup = JsonSerializer.Deserialize<UniversalBackup>(json, UniversalJsonOptions);

            if (backup == null)
                return Result<string>.Failure("Invalid backup file: could not parse JSON");

            // TODO-verify (Windows/CI): Rust shared-core swap — reject structurally
            // invalid backups BEFORE mutating any state (replaces `SchemaVersion != 2`).
            var structureError = ValidateBackupStructure(backup);
            if (structureError != null)
                return Result<string>.Failure(structureError);

            bool settingsImported = false;
            int modesImported = 0;
            int vocabImported = 0;
            bool apiKeysImported = false;

            // 1. Apply cross-platform + Windows platform settings.
            // Marshal onto the UI thread (see ApplyImport): the setters mutate an
            // in-memory object graph Save() serializes unguarded and fire UI-affine
            // SettingsChanged handlers, neither of which is safe off a Task.
            {
                var settings = SettingsService.Instance;
                var universalSettings = backup.Settings;
                var platformExtensions = backup.PlatformExtensions;
                settings.ApplyImport(() =>
                {
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

                    // Apply Windows-specific platform settings (only from Windows backups)
                    UniversalBackupMapper.ApplyWindowsPlatformSettings(
                        platformExtensions, settings, replaceExisting);
                });
            }

            var modes = backup.Modes?.Select(UniversalBackupMapper.MapToMode).ToList();
            var vocabulary = backup.Vocabulary?.Select(UniversalBackupMapper.MapToVocabularyItem).ToList();

            if (replaceExisting)
            {
                (modesImported, vocabImported) = ReplaceDatabaseEntities(modes, vocabulary);
            }
            else
            {
                // 3. Import modes
                if (modes != null && modes.Count > 0)
                {
                    modesImported = ImportEntities(modes, ctx => ctx.Modes);
                }

                // 4. Import vocabulary
                if (vocabulary != null && vocabulary.Count > 0)
                {
                    vocabImported = ImportEntities(vocabulary, ctx => ctx.VocabularyItems);
                }
            }

            // 5. Apply API keys
            if (backup.ApiKeys != null)
            {
                try
                {
                    UniversalBackupMapper.ApplyApiKeys(backup.ApiKeys, ApiKeyService.Instance);
                    apiKeysImported = true;
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
            return Result<string>.Failure("Invalid backup file: not valid JSON");
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
            var backup = JsonSerializer.Deserialize<UniversalBackup>(json, UniversalJsonOptions);

            if (backup == null)
                return Result<ImportSummary>.Failure("Invalid backup file: could not parse JSON");

            // TODO-verify (Windows/CI): Rust shared-core swap — structural validation
            // before applying any selected section (replaces `SchemaVersion != 2`).
            var structureError = ValidateBackupStructure(backup);
            if (structureError != null)
                return Result<ImportSummary>.Failure(structureError);

            var summary = new ImportSummary();

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
                    settings.ApplyImport(() =>
                    {
                        UniversalBackupMapper.ApplySettings(universalSettings, settings);
                        UniversalBackupMapper.ApplyWindowsPlatformSettings(
                            platformExtensions, settings);
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

            // 2. Modes — merge by Id (existing upsert semantics).
            if (selection.IncludeModes && backup.Modes is { Count: > 0 })
            {
                var modes = backup.Modes.Select(UniversalBackupMapper.MapToMode).ToList();
                summary.ModesImported = ImportEntities(modes, ctx => ctx.Modes);
            }

            // 3. Vocabulary — merge by Word (case-insensitive, trimmed).
            if (selection.IncludeVocabulary && backup.Vocabulary is { Count: > 0 })
            {
                var (added, conflicts) = MergeVocabulary(backup.Vocabulary, selection.VocabularyConflict);
                summary.VocabularyAdded = added;
                summary.VocabularyConflicts = conflicts;
            }

            // 4. API keys — merge (only non-empty keys written).
            if (selection.IncludeApiKeys && backup.ApiKeys != null)
            {
                try
                {
                    UniversalBackupMapper.ApplyApiKeys(backup.ApiKeys, ApiKeyService.Instance);
                    summary.ApiKeysImported = true;
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
            return Result<ImportSummary>.Failure("Invalid backup file: not valid JSON");
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
            var backup = JsonSerializer.Deserialize<UniversalBackup>(json, UniversalJsonOptions);

            if (backup == null)
                return Result<VocabularyMergePreview>.Failure("Invalid backup file: could not parse JSON");

            // Match the structural gate used by Inspect/ImportSelective so the preview never
            // reports counts for a file the actual import would reject.
            // TODO-verify (Windows/CI): Rust shared-core swap (replaces `SchemaVersion != 2`).
            var structureError = ValidateBackupStructure(backup);
            if (structureError != null)
                return Result<VocabularyMergePreview>.Failure(structureError);

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
            foreach (var item in incoming)
            {
                var key = (item.Word ?? string.Empty).Trim().ToLowerInvariant();
                if (key.Length == 0)
                    continue;

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
            return Result<VocabularyMergePreview>.Failure("Invalid backup file: not valid JSON");
        }
        catch (Exception ex)
        {
            LoggingService.Error("BackupService: Vocab merge preview failed", ex);
            return Result<VocabularyMergePreview>.Failure($"Could not read backup file: {ex.Message}");
        }
    }

    /// <summary>
    /// Merges incoming vocabulary items into the database by Word (case-insensitive,
    /// trimmed). Existing words are never deleted. On a Word match the conflict policy
    /// decides whether to Skip (leave existing) or Replace (update Replacement/SortOrder/
    /// Source on the existing row, keeping its Id). Unmatched words are inserted with a
    /// fresh Guid and CreatedDate=UtcNow. Returns (added, conflicts).
    /// </summary>
    private static (int added, int conflicts) MergeVocabulary(
        List<UniversalVocabularyItem> items,
        VocabConflict conflict)
    {
        int added = 0;
        int conflicts = 0;

        lock (_dbLock)
        {
            using var context = new HyperWhisperDbContext();

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

            foreach (var incoming in items)
            {
                var trimmedWord = (incoming.Word ?? string.Empty).Trim();
                if (trimmedWord.Length == 0)
                    continue;

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
        }

        // Notify listeners (outside the DB write) so vocab UI refreshes.
        try
        {
            VocabularyService.Instance.NotifyVocabularyChanged();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"BackupService: Failed to raise VocabularyChanged after merge: {ex.Message}");
        }

        return (added, conflicts);
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
    /// Generic merge import with batched upsert to avoid EF change tracker accumulation.
    /// </summary>
    private static int ImportEntities<T>(
        List<T> items,
        Func<HyperWhisperDbContext, DbSet<T>> dbSetSelector) where T : class
    {
        int count = 0;

        lock (_dbLock)
        {
            for (int i = 0; i < items.Count; i += ImportBatchSize)
            {
                using var context = new HyperWhisperDbContext();
                var batch = items.Skip(i).Take(ImportBatchSize);

                foreach (var item in batch)
                {
                    var dbSet = dbSetSelector(context);
                    var key = context.Entry(item).Property("Id").CurrentValue;
                    var existing = dbSet.Find(key);
                    if (existing != null)
                        context.Entry(existing).CurrentValues.SetValues(item);
                    else
                        dbSet.Add(item);
                    count++;
                }

                context.SaveChanges();
            }
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
    public string SourcePlatform { get; set; } = "";

    public string ToLogString() =>
        $"settings={(SettingsImported ? "yes" : "no")}, modes={ModesImported}, " +
        $"vocabAdded={VocabularyAdded}, vocabConflicts={VocabularyConflicts}, " +
        $"apiKeys={(ApiKeysImported ? "yes" : "no")}, source={SourcePlatform}";
}
