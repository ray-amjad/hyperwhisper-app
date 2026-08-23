using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed partial class ApplicationBackupService
{
    public PlatformResult<BackupContents> Inspect(string json)
    {
        var parsed = ParseAndValidate(json);
        if (parsed.IsFailure)
            return PlatformResult<BackupContents>.Failure(parsed.Error!.Code, parsed.Error.Message);

        var backup = parsed.Value!;
        var modeContents = backup.Modes is null
            ? ImmutableArray<BackupModeContents>.Empty
            : backup.Modes
                .OrderBy(item => item.SortOrder).ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id)
                .Select(item => new BackupModeContents(item.Id, item.Name, item.IsDefault))
                .ToImmutableArray();
        return PlatformResult<BackupContents>.Success(new BackupContents(
            backup.SchemaVersion,
            backup.Platform,
            backup.ExportDate,
            backup.HasSettings,
            backup.HasModes,
            backup.HasVocabulary,
            backup.ContainsCredentials,
            backup.ContainsLicenseKey,
            modeContents,
            backup.Vocabulary?.Count ?? 0));
    }

    public async Task<PlatformResult<BackupMergePreview>> PreviewImportAsync(
        string json,
        BackupImportSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var parsed = ParseAndValidate(json);
        if (parsed.IsFailure)
            return PlatformResult<BackupMergePreview>.Failure(parsed.Error!.Code, parsed.Error.Message);

        await using var context = _database.CreateContext();
        var currentModes = await context.Modes.AsNoTracking().ToListAsync(cancellationToken);
        var currentVocabulary = await context.VocabularyItems.AsNoTracking().ToListAsync(cancellationToken);
        var calculation = CalculateMerge(parsed.Value!, selection, currentModes, currentVocabulary, allowMissingSelectedModes: true);
        return calculation.IsFailure
            ? PlatformResult<BackupMergePreview>.Failure(calculation.Error!.Code, calculation.Error.Message)
            : PlatformResult<BackupMergePreview>.Success(calculation.Value!.Preview);
    }

    public async Task<PlatformResult> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = await ImportAsync(json, BackupImportSelection.All, cancellationToken);
        return result.IsSuccess
            ? PlatformResult.Success()
            : PlatformResult.Failure(result.Error!.Code, result.Error.Message);
    }

    public async Task<PlatformResult<BackupImportSummary>> ImportAsync(
        string json,
        BackupImportSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var parsed = ParseAndValidate(json);
        if (parsed.IsFailure)
            return PlatformResult<BackupImportSummary>.Failure(parsed.Error!.Code, parsed.Error.Message);

        var backup = parsed.Value!;
        await using var context = _database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var currentModes = await context.Modes.OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var currentVocabulary = await context.VocabularyItems.OrderBy(item => item.SortOrder).ThenBy(item => item.Word).ToListAsync(cancellationToken);
        var calculation = CalculateMerge(backup, selection, currentModes, currentVocabulary);
        if (calculation.IsFailure)
            return PlatformResult<BackupImportSummary>.Failure(calculation.Error!.Code, calculation.Error.Message);

        var merge = calculation.Value!;
        var previousSettings = _settings.Snapshot();
        var settingsWereSaved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyModes(context, currentModes, merge.Modes, selection.ModeBehavior);
            ApplyVocabulary(context, currentVocabulary, merge.Vocabulary, selection.VocabularyConflictPolicy);
            await context.SaveChangesAsync(cancellationToken);

            // Platform extensions are preservation metadata, not credentials. Preserve
            // them even when the user chooses not to apply the settings section.
            if (backup.Root["platformExtensions"] is JsonObject extensions)
                _settings.Set("backup.platformExtensions", JsonSerializer.SerializeToElement(extensions, SerializerOptions));
            if (merge.Preview.WillImportSettings)
                ApplySelectedSettings(backup.Root);

            if (backup.Root["platformExtensions"] is JsonObject || merge.Preview.WillImportSettings)
            {
                var saved = _settings.Save();
                if (saved.IsFailure)
                {
                    _settings.Replace(previousSettings);
                    _ = _settings.Save();
                    return PlatformResult<BackupImportSummary>.Failure(saved.Error!.Code, saved.Error.Message);
                }
                settingsWereSaved = true;
            }

            await transaction.CommitAsync(cancellationToken);
            var preview = merge.Preview;
            return PlatformResult<BackupImportSummary>.Success(new BackupImportSummary(
                preview.WillImportSettings,
                preview.ModesAdded,
                preview.ModesReplaced,
                preview.ModesRemoved,
                preview.VocabularyAdded,
                preview.VocabularyReplaced,
                preview.VocabularySkipped));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            RestoreSettings(previousSettings, settingsWereSaved);
            return PlatformResult<BackupImportSummary>.Failure(
                "backup.invalid_settings",
                "The universal backup contains invalid Linux settings.");
        }
        catch
        {
            RestoreSettings(previousSettings, settingsWereSaved);
            throw;
        }
    }

    private void RestoreSettings(IReadOnlyDictionary<string, JsonElement> previousSettings, bool saveToDisk)
    {
        _settings.Replace(previousSettings);
        if (saveToDisk) _ = _settings.Save();
    }

    private void ApplySelectedSettings(JsonObject root)
    {
        if (root["platformExtensions"] is JsonObject extensions)
        {
            if (extensions["linux"]?["settings"] is JsonObject linuxSettings)
            {
                CopySetting<string>(linuxSettings, "language");
                CopySetting<string>(linuxSettings, "localWhisperBackend");
                CopySetting<bool>(linuxSettings, "allowLocalWhisperCpuFallback");
                CopySetting<string>(linuxSettings, "localLlmBackend");
                CopySetting<bool>(linuxSettings, "allowLocalLlmCpuFallback");
                CopySetting<bool>(linuxSettings, "localApiEnabled");
                CopySetting<int>(linuxSettings, "localApiPort");
                CopySetting<bool>(linuxSettings, "autostartEnabled");
                CopySetting<string>(linuxSettings, "toggleShortcutModifiers");
                CopySetting<string>(linuxSettings, "toggleShortcutKey");
                CopySetting<string>(linuxSettings, "cancelShortcutModifiers");
                CopySetting<string>(linuxSettings, "cancelShortcutKey");
                CopySetting<string>(linuxSettings, "changeModeShortcutModifiers");
                CopySetting<string>(linuxSettings, "changeModeShortcutKey");
                CopySetting<string>(linuxSettings, "streamingShortcutModifiers");
                CopySetting<string>(linuxSettings, "streamingShortcutKey");
                CopySetting<string>(linuxSettings, "pushToTalkMode");
                CopySetting<string>(linuxSettings, "pushToTalkModifier");
                CopySetting<string>(linuxSettings, "pushToTalkShortcutModifiers");
                CopySetting<string>(linuxSettings, "pushToTalkShortcutKey");
                CopySetting<bool>(linuxSettings, "pushToTalkDoublePressLock");
                CopySetting<bool>(linuxSettings, "autoIncreaseMicVolume");
                CopySetting<bool>(linuxSettings, "keepMicrophoneWarm");
                CopySetting<string>(linuxSettings, "audioEnvironmentPolicy");
                CopySetting<bool>(linuxSettings, "autoDeleteEnabled");
                CopySetting<int>(linuxSettings, "autoDeleteDaysOld");
                if (linuxSettings["customEndpoints"] is { } customEndpoints)
                    _settings.Set("customEndpoints", customEndpoints.Deserialize<PortableCustomPostProcessingEndpoint[]>(SerializerOptions) ?? []);
            }
            else if (extensions["windows"]?["settings"]?["customEndpoints"] is { } windowsCustomEndpoints)
            {
                _settings.Set("customEndpoints", windowsCustomEndpoints.Deserialize<PortableCustomPostProcessingEndpoint[]>(SerializerOptions) ?? []);
            }
        }
        if (root["settings"] is JsonObject sharedSettings) ApplySharedSettings(sharedSettings);
    }

    private static void ApplyModes(
        DbContext context,
        List<Mode> current,
        IReadOnlyList<Mode> imported,
        BackupModeImportBehavior behavior)
    {
        if (imported.Count == 0) return;
        if (behavior == BackupModeImportBehavior.ReplaceAll)
        {
            context.RemoveRange(current);
            context.AddRange(imported);
            return;
        }

        foreach (var importedMode in imported)
        {
            var existing = current.SingleOrDefault(item => item.Id == importedMode.Id);
            if (existing is null)
            {
                context.Add(importedMode);
                current.Add(importedMode);
            }
            else
            {
                context.Entry(existing).CurrentValues.SetValues(importedMode);
            }
        }
        if (imported.Any(item => item.IsDefault))
        {
            var chosen = imported.Where(item => item.IsDefault)
                .OrderBy(item => item.SortOrder).ThenBy(item => item.Id).First();
            foreach (var mode in current) mode.IsDefault = mode.Id == chosen.Id;
        }
        else if (!current.Any(item => item.IsDefault))
        {
            current.OrderBy(item => item.SortOrder).ThenBy(item => item.Id).First().IsDefault = true;
        }
    }

    private static void ApplyVocabulary(
        DbContext context,
        List<VocabularyItem> current,
        IReadOnlyList<VocabularyItem> imported,
        VocabularyConflictPolicy policy)
    {
        foreach (var item in imported.OrderBy(item => item.SortOrder).ThenBy(item => item.Word, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id))
        {
            var existing = current.FirstOrDefault(candidate => string.Equals(candidate.Word, item.Word, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                context.Add(item);
                current.Add(item);
            }
            else if (policy == VocabularyConflictPolicy.Replace)
            {
                existing.Word = item.Word.Trim();
                existing.Replacement = item.Replacement;
                existing.SortOrder = item.SortOrder;
                existing.Source = item.Source;
            }
        }
    }

    private static PlatformResult<MergeCalculation> CalculateMerge(
        ParsedBackup backup,
        BackupImportSelection selection,
        IReadOnlyList<Mode> currentModes,
        IReadOnlyList<VocabularyItem> currentVocabulary,
        bool allowMissingSelectedModes = false)
    {
        if (selection.ModeBehavior == BackupModeImportBehavior.ReplaceAll && selection.SelectedModeIds is not null)
            return PlatformResult<MergeCalculation>.Failure("backup.invalid_selection", "Selected mode IDs require merge-selected mode behavior.");
        if (selection.ModeBehavior == BackupModeImportBehavior.MergeSelected && selection.SelectedModeIds is null)
            return PlatformResult<MergeCalculation>.Failure("backup.invalid_selection", "Merge-selected mode behavior requires an explicit mode ID selection.");

        IReadOnlyList<Mode> modes = [];
        var missing = ImmutableArray<Guid>.Empty;
        if (selection.ImportModes && backup.Modes is not null)
        {
            if (selection.ModeBehavior == BackupModeImportBehavior.ReplaceAll)
            {
                if (backup.Modes.Count == 0)
                    return PlatformResult<MergeCalculation>.Failure("backup.no_modes", "Replacing modes requires at least one mode in the backup.");
                modes = backup.Modes;
            }
            else
            {
                var selected = selection.SelectedModeIds!;
                modes = backup.Modes.Where(item => selected.Contains(item.Id)).ToList();
                missing = selected.Except(backup.Modes.Select(item => item.Id)).Order().ToImmutableArray();
                if (missing.Length != 0 && !allowMissingSelectedModes)
                    return PlatformResult<MergeCalculation>.Failure("backup.selected_modes_missing", "One or more selected modes are not present in the backup.");
                var untouchedNames = currentModes.Where(item => !selected.Contains(item.Id)).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (modes.Any(item => untouchedNames.Contains(item.Name)))
                    return PlatformResult<MergeCalculation>.Failure("backup.mode_name_conflict", "A selected mode conflicts with an existing mode name.");
            }
        }

        IReadOnlyList<VocabularyItem> vocabulary = selection.ImportVocabulary && backup.Vocabulary is not null
            ? backup.Vocabulary
            : [];
        var currentModeIds = currentModes.Select(item => item.Id).ToHashSet();
        var importedModeIds = modes.Select(item => item.Id).ToHashSet();
        var existingWords = currentVocabulary.Select(item => item.Word).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vocabularyConflicts = vocabulary.Count(item => existingWords.Contains(item.Word));
        var preview = new BackupMergePreview(
            selection.ImportSettings && backup.HasSettings,
            modes.Count(item => !currentModeIds.Contains(item.Id)),
            modes.Count(item => currentModeIds.Contains(item.Id)),
            selection.ImportModes && backup.HasModes && selection.ModeBehavior == BackupModeImportBehavior.ReplaceAll
                ? currentModes.Count(item => !importedModeIds.Contains(item.Id))
                : 0,
            vocabulary.Count - vocabularyConflicts,
            selection.VocabularyConflictPolicy == VocabularyConflictPolicy.Replace ? vocabularyConflicts : 0,
            selection.VocabularyConflictPolicy == VocabularyConflictPolicy.Skip ? vocabularyConflicts : 0,
            missing);
        return PlatformResult<MergeCalculation>.Success(new MergeCalculation(preview, modes, vocabulary));
    }

    private static PlatformResult<ParsedBackup> ParseAndValidate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return PlatformResult<ParsedBackup>.Failure("backup.invalid_json", "Backup JSON is required.");
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return PlatformResult<ParsedBackup>.Failure("backup.invalid_json", "The application backup is not valid JSON."); }
        if (root is null) return PlatformResult<ParsedBackup>.Failure("backup.invalid_json", "The application backup must be a JSON object.");

        int schemaVersion;
        try { schemaVersion = root["schemaVersion"]?.GetValue<int>() ?? 0; }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        { return PlatformResult<ParsedBackup>.Failure("backup.unsupported_version", "The application backup version must be an integer."); }
        if (schemaVersion != 2)
            return PlatformResult<ParsedBackup>.Failure("backup.unsupported_version", "The application backup version is not supported.");
        var validation = SharedCoreBridge.ValidateBackup(json);
        if (validation.Count != 0)
            return PlatformResult<ParsedBackup>.Failure("backup.invalid", $"The universal backup failed validation at {validation[0].Path}.");

        try
        {
            List<Mode>? modes = null;
            if (root["modes"] is JsonArray modeNodes)
            {
                modes = modeNodes.Select(ParseMode).ToList();
                ValidateModes(modes);
                if (modes.Count > 0)
                {
                    if (!modes.Any(item => item.IsDefault)) modes[0].IsDefault = true;
                    var firstDefault = modes.Where(item => item.IsDefault).OrderBy(item => item.SortOrder).ThenBy(item => item.Id).First();
                    foreach (var mode in modes) mode.IsDefault = mode.Id == firstDefault.Id;
                }
            }
            List<VocabularyItem>? vocabulary = null;
            if (root["vocabulary"] is JsonArray vocabularyNodes)
            {
                vocabulary = vocabularyNodes.Select(ParseVocabulary).ToList();
                ValidateVocabulary(vocabulary);
            }
            var platform = root["platform"]!.GetValue<string>();
            var exportDate = DateTimeOffset.Parse(root["exportDate"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            return PlatformResult<ParsedBackup>.Success(new ParsedBackup(
                root,
                schemaVersion,
                platform,
                exportDate,
                root.ContainsKey("settings"),
                root.ContainsKey("modes"),
                root.ContainsKey("vocabulary"),
                root["apiKeys"] is JsonObject keys && keys.Count > 0,
                root["licenseKey"] is JsonValue license && !string.IsNullOrEmpty(license.GetValue<string?>()),
                modes,
                vocabulary));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return PlatformResult<ParsedBackup>.Failure("backup.invalid_records", "The universal backup contains invalid or duplicate records.");
        }
    }

    private sealed record ParsedBackup(
        JsonObject Root,
        int SchemaVersion,
        string Platform,
        DateTimeOffset ExportDate,
        bool HasSettings,
        bool HasModes,
        bool HasVocabulary,
        bool ContainsCredentials,
        bool ContainsLicenseKey,
        List<Mode>? Modes,
        List<VocabularyItem>? Vocabulary);

    private sealed record MergeCalculation(
        BackupMergePreview Preview,
        IReadOnlyList<Mode> Modes,
        IReadOnlyList<VocabularyItem> Vocabulary);
}
