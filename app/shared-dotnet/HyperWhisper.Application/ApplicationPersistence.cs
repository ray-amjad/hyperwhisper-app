using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed class ApplicationDb(Func<HyperWhisperDbContext> createContext)
{
    private readonly Func<HyperWhisperDbContext> _createContext =
        createContext ?? throw new ArgumentNullException(nameof(createContext));

    public ApplicationDb(IAppPaths paths)
        : this(() => new HyperWhisperDbContext(paths))
    {
    }

    public HyperWhisperDbContext CreateContext() => _createContext();

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
    }
}

public interface ITranscriptionHistoryStore
{
    Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class HistoryRepository : ITranscriptionHistoryStore
{
    private readonly ApplicationDb _database;
    private readonly string? _recordingsRoot;

    public HistoryRepository(ApplicationDb database, IAppPaths? paths = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _recordingsRoot = paths is null ? null : Path.GetFullPath(paths.RecordingsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public async Task<IReadOnlyList<Transcript>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.Transcripts.AsNoTracking()
            .OrderByDescending(item => item.Date)
            .ToListAsync(cancellationToken);
    }

    public async Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.Transcripts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        await using var context = _database.CreateContext();
        context.Transcripts.Add(transcript);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        await using var context = _database.CreateContext();
        if (!await context.Transcripts.AnyAsync(item => item.Id == transcript.Id, cancellationToken))
            return false;
        context.Transcripts.Update(transcript);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var transcript = await context.Transcripts.FindAsync(new object[] { id }, cancellationToken);
        if (transcript == null) return false;
        context.Transcripts.Remove(transcript);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Transcript>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var rows = context.Transcripts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            rows = rows.Where(item => item.Text.Contains(term)
                || (item.TranscribedText != null && item.TranscribedText.Contains(term))
                || (item.PostProcessedText != null && item.PostProcessedText.Contains(term)));
        }
        return await rows.OrderByDescending(item => item.Date).ToListAsync(cancellationToken);
    }

    public async Task<HistoryDeletionResult> DeleteAsync(Guid id, bool deleteAudio, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var transcript = await context.Transcripts.FindAsync(new object[] { id }, cancellationToken);
        if (transcript == null) return new(false, false, null);
        var paths = new[] { transcript.AudioFilePath, transcript.TrimmedAudioFilePath }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToArray();
        context.Transcripts.Remove(transcript);
        await context.SaveChangesAsync(cancellationToken);
        if (!deleteAudio) return new(true, false, null);
        if (_recordingsRoot is null || paths.Any(path => !IsContainedRecording(path!)))
            return new(true, false, "The transcript was removed, but external audio was retained for safety.");
        try
        {
            foreach (var path in paths) File.Delete(path!);
            return new(true, true, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(true, false, "The transcript was removed, but an audio file could not be deleted.");
        }
    }

    private bool IsContainedRecording(string path)
    {
        try { return Path.GetFullPath(path).StartsWith(_recordingsRoot!, StringComparison.Ordinal); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }

    public async Task<int> FailOrphanedProcessingAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var orphaned = await context.Transcripts
            .Where(item => item.Status == TranscriptStatus.Processing)
            .ToListAsync(cancellationToken);
        foreach (var transcript in orphaned)
        {
            const string reason = "Transcription did not finish";
            transcript.Status = TranscriptStatus.Failed;
            transcript.FailedReason = string.IsNullOrWhiteSpace(transcript.FailedReason)
                ? reason
                : transcript.FailedReason;
            transcript.Text = string.IsNullOrWhiteSpace(transcript.Text) ? reason : transcript.Text;
        }
        if (orphaned.Count > 0) await context.SaveChangesAsync(cancellationToken);
        return orphaned.Count;
    }
}

public sealed record HistoryDeletionResult(bool TranscriptDeleted, bool AudioDeleted, string? Warning);

public sealed class VocabularyRepository(ApplicationDb database)
{
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<IReadOnlyList<VocabularyItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.VocabularyItems.AsNoTracking()
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Word)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(VocabularyItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Word))
            throw new ArgumentException("A vocabulary word is required.", nameof(item));
        await using var context = _database.CreateContext();
        context.VocabularyItems.Add(item);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<VocabularyItem> UpsertAsync(VocabularyItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var word = item.Word.Trim();
        if (word.Length == 0) throw new ArgumentException("A vocabulary word is required.", nameof(item));
        await using var context = _database.CreateContext();
        var existing = await context.VocabularyItems.FirstOrDefaultAsync(candidate => candidate.Id == item.Id, cancellationToken)
            ?? (await context.VocabularyItems.ToListAsync(cancellationToken))
                .FirstOrDefault(candidate => string.Equals(candidate.Word, word, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            item.Word = word;
            context.VocabularyItems.Add(item);
            existing = item;
        }
        else
        {
            existing.Word = word;
            existing.Replacement = string.IsNullOrWhiteSpace(item.Replacement) ? null : item.Replacement.Trim();
            existing.SortOrder = item.SortOrder;
        }
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<int> MergeAsync(IEnumerable<VocabularyItem> items, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item.Word)))
        {
            _ = await UpsertAsync(item, cancellationToken);
            count++;
        }
        return count;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var item = await context.VocabularyItems.FindAsync(new object[] { id }, cancellationToken);
        if (item == null) return false;
        context.VocabularyItems.Remove(item);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class ModeRepository(ApplicationDb database)
{
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<IReadOnlyList<Mode>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.Modes.AsNoTracking()
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(Mode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (string.IsNullOrWhiteSpace(mode.Name))
            throw new ArgumentException("A mode name is required.", nameof(mode));
        await using var context = _database.CreateContext();
        var exists = await context.Modes.AnyAsync(item => item.Id == mode.Id, cancellationToken);
        if (exists) context.Modes.Update(mode);
        else context.Modes.Add(mode);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var mode = await context.Modes.FindAsync(new object[] { id }, cancellationToken);
        if (mode == null) return false;
        context.Modes.Remove(mode);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteSafelyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var modes = await context.Modes.OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        var target = modes.SingleOrDefault(item => item.Id == id);
        if (target is null) return false;
        if (modes.Count == 1) throw new InvalidOperationException("Cannot delete the last remaining mode.");
        context.Modes.Remove(target);
        if (target.IsDefault)
        {
            var replacement = modes.First(item => item.Id != id);
            replacement.IsDefault = true;
            replacement.ModifiedDate = DateTime.UtcNow;
        }
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpsertSafelyAsync(Mode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        mode.Name = mode.Name.Trim();
        if (mode.Name.Length == 0) throw new ArgumentException("A mode name is required.", nameof(mode));
        await using var context = _database.CreateContext();
        var all = await context.Modes.ToListAsync(cancellationToken);
        if (all.Any(item => item.Id != mode.Id && string.Equals(item.Name, mode.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A mode with that name already exists.");
        if (all.Count == 0) mode.IsDefault = true;
        if (mode.IsDefault)
            foreach (var item in all.Where(item => item.Id != mode.Id)) item.IsDefault = false;
        else if (all.Count > 0 && all.All(item => item.Id == mode.Id || !item.IsDefault))
            throw new InvalidOperationException("At least one mode must remain the default.");
        var existing = all.SingleOrDefault(item => item.Id == mode.Id);
        if (existing is null) context.Modes.Add(mode);
        else context.Entry(existing).CurrentValues.SetValues(mode);
        await context.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DurableAudioImportService(IPrivateFileService privateFiles, IAppPaths paths, long maximumBytes = 1_073_741_824)
{
    private readonly IPrivateFileService _privateFiles = privateFiles ?? throw new ArgumentNullException(nameof(privateFiles));
    private readonly string _recordingsDirectory = (paths ?? throw new ArgumentNullException(nameof(paths))).RecordingsDirectory;

    public async Task<PlatformResult<string>> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            return PlatformResult<string>.Failure("audio_import.invalid_path", "Choose a local audio file.");
        try
        {
            await using var stream = new FileStream(Path.GetFullPath(sourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
                return PlatformResult<string>.Failure("audio_import.invalid_size", "The audio file is empty or exceeds the import limit.");
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension is not (".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".webm")) extension = ".audio";
            var destination = Path.Combine(_recordingsDirectory, $"import-{Guid.NewGuid():N}{extension}");
            Directory.CreateDirectory(_recordingsDirectory);
            var temporary = Path.Combine(_recordingsDirectory, $".{Path.GetFileName(destination)}.partial");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(temporary, options))
                {
                    var buffer = new byte[128 * 1024];
                    long copied = 0;
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        copied += read;
                        if (copied > maximumBytes) return PlatformResult<string>.Failure("audio_import.invalid_size", "The audio file exceeds the import limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination);
                var restricted = _privateFiles.IsRestrictedToCurrentUser(destination);
                if (restricted.IsFailure || restricted.Value != true)
                {
                    File.Delete(destination);
                    return PlatformResult<string>.Failure("audio_import.permissions", "The imported audio could not be made private.");
                }
                return PlatformResult<string>.Success(destination);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PlatformResult<string>.Failure("audio_import.failed", "The audio file could not be imported.");
        }
    }
}

public sealed class PortableSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IPrivateFileService _files;
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public PortableSettingsService(IPrivateFileService files, IAppPaths paths)
        : this(files, Path.Combine(
            (paths ?? throw new ArgumentNullException(nameof(paths))).ConfigDirectory,
            "settings.json"))
    {
    }

    public PortableSettingsService(IPrivateFileService files, string path)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public PlatformResult Load()
    {
        var result = _files.ReadAllText(_path);
        if (result.IsFailure)
            return PlatformResult.Failure(result.Error!.Code, result.Error.Message);
        _values.Clear();
        if (result.Value == null) return PlatformResult.Success();
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Value, SerializerOptions);
            if (loaded != null)
                foreach (var entry in loaded) _values[entry.Key] = entry.Value.Clone();
            return PlatformResult.Success();
        }
        catch (JsonException)
        {
            return PlatformResult.Failure("settings.invalid_json", "The settings file is not valid JSON.");
        }
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        ValidateKey(key);
        return _values.TryGetValue(key, out var value)
            ? value.Deserialize<T>(SerializerOptions)
            : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        ValidateKey(key);
        _values[key] = JsonSerializer.SerializeToElement(value, SerializerOptions);
    }

    public IReadOnlyDictionary<string, JsonElement> Snapshot()
        => _values.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);

    public void Replace(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values.Clear();
        foreach (var entry in values) _values[entry.Key] = entry.Value.Clone();
    }

    public PlatformResult Save()
        => _files.WriteAllTextAtomically(_path, JsonSerializer.Serialize(_values, SerializerOptions));

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A settings key is required.", nameof(key));
    }
}

public sealed class ApplicationBackupService(ApplicationDb database, PortableSettingsService settings)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly PortableSettingsService _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var modes = await context.Modes.AsNoTracking().OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        var vocabulary = await context.VocabularyItems.AsNoTracking().OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        var platformExtensions = ReadObject(_settings.Get<JsonElement?>("backup.platformExtensions")) ?? new JsonObject();
        var linuxExtension = platformExtensions["linux"]?.DeepClone() as JsonObject ?? new JsonObject();
        var linuxSettings = linuxExtension["settings"]?.DeepClone() as JsonObject ?? new JsonObject();
        linuxSettings["language"] = _settings.Get("language", "auto");
        linuxSettings["localLlmBackend"] = _settings.Get("localLlmBackend", "cpu");
        linuxSettings["allowLocalLlmCpuFallback"] = _settings.Get("allowLocalLlmCpuFallback", true);
        linuxSettings["localApiEnabled"] = _settings.Get("localApiEnabled", false);
        linuxSettings["localApiPort"] = _settings.Get("localApiPort", 51671);
        linuxSettings["autostartEnabled"] = _settings.Get("autostartEnabled", false);
        linuxExtension["settings"] = linuxSettings;
        platformExtensions["linux"] = linuxExtension;
        var root = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["exportDate"] = DateTimeOffset.UtcNow.ToString("O"),
            ["appVersion"] = "1.0.0",
            ["platform"] = "linux",
            ["settings"] = BuildSharedSettings(),
            ["modes"] = new JsonArray(modes.Select(ToUniversalMode).ToArray()),
            ["vocabulary"] = new JsonArray(vocabulary.Select(item => new JsonObject
            {
                ["id"] = item.Id.ToString("D"), ["word"] = item.Word,
                ["replacement"] = item.Replacement, ["sortOrder"] = item.SortOrder,
                ["source"] = "manual",
            }).ToArray()),
            ["licenseKey"] = null,
            ["platformExtensions"] = platformExtensions,
        };
        var json = root.ToJsonString(SerializerOptions);
        var failures = SharedCoreBridge.ValidateBackup(json);
        if (failures.Count != 0) throw new InvalidOperationException("The generated universal backup did not pass shared-core validation.");
        return json;
    }

    public async Task<PlatformResult> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Backup JSON is required.", nameof(json));
        JsonObject? backup;
        try { backup = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return PlatformResult.Failure("backup.invalid_json", "The application backup is not valid JSON."); }
        int? schemaVersion;
        try { schemaVersion = backup?["schemaVersion"]?.GetValue<int>(); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        { return PlatformResult.Failure("backup.unsupported_version", "The application backup version must be an integer."); }
        if (backup is null || schemaVersion != 2)
            return PlatformResult.Failure("backup.unsupported_version", "The application backup version is not supported.");
        var validation = SharedCoreBridge.ValidateBackup(json);
        if (validation.Count != 0)
            return PlatformResult.Failure("backup.invalid", $"The universal backup failed validation at {validation[0].Path}.");

        List<Mode>? importedModes = null;
        List<VocabularyItem> vocabulary;
        try
        {
            if (backup["modes"] is JsonArray modeNodes)
            {
                importedModes = modeNodes.Select(ParseMode).ToList();
                if (importedModes.Count == 0) return PlatformResult.Failure("backup.no_modes", "A full modes backup must contain at least one mode.");
                ValidateModes(importedModes);
                if (!importedModes.Any(item => item.IsDefault)) importedModes[0].IsDefault = true;
                var firstDefault = importedModes.First(item => item.IsDefault);
                foreach (var mode in importedModes.Where(item => item.Id != firstDefault.Id)) mode.IsDefault = false;
            }
            vocabulary = backup["vocabulary"] is JsonArray vocabularyNodes
                ? vocabularyNodes.Select(ParseVocabulary).ToList()
                : [];
            ValidateVocabulary(vocabulary);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return PlatformResult.Failure("backup.invalid_records", "The universal backup contains invalid or duplicate records.");
        }

        await using var context = _database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var previousSettings = _settings.Snapshot();
        try
        {
            if (importedModes is not null)
            {
                await context.Modes.ExecuteDeleteAsync(cancellationToken);
                context.Modes.AddRange(importedModes);
            }
            var existingVocabulary = await context.VocabularyItems.ToListAsync(cancellationToken);
            foreach (var item in vocabulary)
            {
                var existing = existingVocabulary.FirstOrDefault(candidate => string.Equals(candidate.Word, item.Word, StringComparison.OrdinalIgnoreCase));
                if (existing is null) { context.VocabularyItems.Add(item); existingVocabulary.Add(item); }
                else { existing.Replacement = item.Replacement; existing.SortOrder = item.SortOrder; }
            }
            await context.SaveChangesAsync(cancellationToken);

            if (backup["platformExtensions"] is JsonObject extensions)
            {
                _settings.Set("backup.platformExtensions", JsonSerializer.SerializeToElement(extensions, SerializerOptions));
                if (extensions["linux"]?["settings"] is JsonObject linuxSettings)
                {
                    CopySetting<string>(linuxSettings, "language");
                    CopySetting<string>(linuxSettings, "localLlmBackend");
                    CopySetting<bool>(linuxSettings, "allowLocalLlmCpuFallback");
                    CopySetting<bool>(linuxSettings, "localApiEnabled");
                    CopySetting<int>(linuxSettings, "localApiPort");
                    CopySetting<bool>(linuxSettings, "autostartEnabled");
                }
            }
            if (backup["settings"] is JsonObject sharedSettings) ApplySharedSettings(sharedSettings);
            var saved = _settings.Save();
            if (saved.IsFailure)
            {
                _settings.Replace(previousSettings);
                return PlatformResult.Failure(saved.Error!.Code, saved.Error.Message);
            }

            await transaction.CommitAsync(cancellationToken);
            return PlatformResult.Success();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            _settings.Replace(previousSettings);
            _ = _settings.Save();
            return PlatformResult.Failure("backup.invalid_settings", "The universal backup contains invalid Linux settings.");
        }
        catch
        {
            _settings.Replace(previousSettings);
            _ = _settings.Save();
            throw;
        }
    }

    private void CopySetting<T>(JsonObject source, string key)
    {
        if (source[key] is not null) _settings.Set(key, source[key]!.GetValue<T>());
    }

    private JsonObject BuildSharedSettings() => new()
    {
        ["general"] = new JsonObject
        {
            ["launchMinimized"] = _settings.Get("general.launchMinimized", false),
            ["showRecordingWindow"] = _settings.Get("general.showRecordingWindow", true),
            ["checkForUpdatesAutomatically"] = _settings.Get("general.checkForUpdatesAutomatically", true),
            ["enableErrorLogging"] = _settings.Get("general.enableErrorLogging", true),
            ["shareAnonymousSpeedData"] = _settings.Get("general.shareAnonymousSpeedData", true),
            ["enableSoundEffects"] = _settings.Get("general.enableSoundEffects", true),
        },
        ["textOutput"] = new JsonObject
        {
            ["pasteResultText"] = _settings.Get("textOutput.pasteResultText", true),
            ["removeFillerWords"] = _settings.Get("textOutput.removeFillerWords", false),
            ["restoreClipboardAfterPaste"] = _settings.Get("textOutput.restoreClipboardAfterPaste", true),
            ["hideFromClipboardHistory"] = _settings.Get("textOutput.hideFromClipboardHistory", false),
            ["clipboardRestoreDelaySeconds"] = _settings.Get("textOutput.clipboardRestoreDelaySeconds", 10d),
            ["autocapitalizeInsert"] = _settings.Get("textOutput.autocapitalizeInsert", true),
            ["storeWordTimestamps"] = _settings.Get("textOutput.storeWordTimestamps", false),
        },
        ["storage"] = new JsonObject
        {
            ["keepAudioFiles"] = _settings.Get("storage.keepAudioFiles", true),
            ["storeAsM4A"] = _settings.Get("storage.storeAsM4A", false),
        },
        ["streaming"] = new JsonObject
        {
            ["enabled"] = _settings.Get("streaming.enabled", false),
            ["provider"] = _settings.Get<string?>("streaming.provider"),
            ["language"] = _settings.Get<string?>("streaming.language"),
            ["deepgramModel"] = _settings.Get<string?>("streaming.deepgramModel"),
            ["fastFormatting"] = _settings.Get("streaming.fastFormatting", false),
            ["shortcut"] = _settings.Get<string?>("streaming.shortcut"),
        },
        ["advanced"] = new JsonObject
        {
            ["maxRecordingDuration"] = _settings.Get("advanced.maxRecordingDuration", 3600),
            ["typingSpeedWPM"] = _settings.Get("advanced.typingSpeedWPM", 45),
        },
    };

    private void ApplySharedSettings(JsonObject settings)
    {
        CopyCategory(settings, "general", ["launchMinimized", "showRecordingWindow", "checkForUpdatesAutomatically", "enableErrorLogging", "shareAnonymousSpeedData", "enableSoundEffects"]);
        CopyCategory(settings, "textOutput", ["pasteResultText", "removeFillerWords", "restoreClipboardAfterPaste", "hideFromClipboardHistory", "clipboardRestoreDelaySeconds", "autocapitalizeInsert", "storeWordTimestamps"]);
        CopyCategory(settings, "storage", ["keepAudioFiles", "storeAsM4A"]);
        CopyCategory(settings, "streaming", ["enabled", "provider", "language", "deepgramModel", "fastFormatting", "shortcut"]);
        CopyCategory(settings, "advanced", ["maxRecordingDuration", "typingSpeedWPM"]);
    }

    private void CopyCategory(JsonObject settings, string category, IReadOnlyList<string> keys)
    {
        if (settings[category] is not JsonObject source) return;
        foreach (var key in keys)
            if (source[key] is { } value) _settings.Set($"{category}.{key}", JsonSerializer.SerializeToElement(value, SerializerOptions));
    }

    private static JsonObject ToUniversalMode(Mode mode)
    {
        var extensions = ReadObject(mode.ForeignPlatformExtensions) ?? new JsonObject();
        var linux = extensions["linux"]?.DeepClone() as JsonObject ?? new JsonObject();
        linux["localEngine"] = mode.LocalEngine;
        linux["localParakeetModel"] = mode.LocalParakeetModel;
        linux["providerType"] = mode.ProviderType;
        linux["modelType"] = mode.ModelType;
        linux["enableScreenOCR"] = mode.EnableScreenOCR;
        linux["customVocabulary"] = mode.CustomVocabulary is null
            ? null
            : new JsonArray(mode.CustomVocabulary.Select(term => (JsonNode?)JsonValue.Create(term)).ToArray());
        linux["isSystemProvided"] = mode.IsSystemProvided;
        linux["createdDate"] = mode.CreatedDate.ToUniversalTime().ToString("O");
        linux["modifiedDate"] = mode.ModifiedDate.ToUniversalTime().ToString("O");
        extensions["linux"] = linux;
        return new JsonObject
        {
            ["id"] = mode.Id.ToString("D"), ["name"] = mode.Name, ["preset"] = mode.Preset,
            ["language"] = mode.Language, ["model"] = mode.Model, ["isDefault"] = mode.IsDefault,
            ["sortOrder"] = mode.SortOrder, ["punctuation"] = mode.Punctuation,
            ["capitalization"] = mode.Capitalization, ["profanityFilter"] = mode.ProfanityFilter,
            ["removeTrailingPeriod"] = mode.RemoveTrailingPeriod, ["englishSpelling"] = mode.EnglishSpelling,
            ["cloudProvider"] = mode.CloudProvider, ["cloudTranscriptionModel"] = mode.CloudTranscriptionModel,
            ["cloudTranscriptionDomain"] = mode.CloudTranscriptionDomain,
            ["postProcessingMode"] = mode.PostProcessingMode, ["postProcessingProvider"] = mode.PostProcessingProvider,
            ["languageModel"] = mode.LanguageModel, ["localPostProcessingModel"] = mode.LocalPostProcessingModel,
            ["userSystemPrompt"] = mode.UserSystemPrompt, ["customInstructions"] = mode.CustomInstructions,
            ["geminiCustomPrompt"] = mode.GeminiCustomPrompt, ["cloudAccuracyTier"] = mode.CloudAccuracyTier,
            ["cloudPostProcessingModel"] = mode.CloudPostProcessingModel,
            ["platformExtensions"] = extensions,
        };
    }

    private static Mode ParseMode(JsonNode? node)
    {
        var value = node as JsonObject ?? throw new JsonException("Mode must be an object.");
        var extensions = value["platformExtensions"] as JsonObject;
        var linux = extensions?["linux"] as JsonObject;
        var preservedExtensions = extensions?.DeepClone() as JsonObject;
        return new Mode
        {
            Id = Guid.Parse(value["id"]!.GetValue<string>()), Name = value["name"]!.GetValue<string>(),
            Preset = String(value, "preset") ?? "hyper", Language = String(value, "language") ?? "en",
            Model = String(value, "model") ?? "base", ModelType = String(linux, "modelType") ?? String(value, "model") ?? "base",
            IsDefault = Bool(value, "isDefault"), SortOrder = Int(value, "sortOrder"),
            Punctuation = Bool(value, "punctuation", true), Capitalization = Bool(value, "capitalization", true),
            ProfanityFilter = Bool(value, "profanityFilter"), RemoveTrailingPeriod = Bool(value, "removeTrailingPeriod"),
            EnglishSpelling = String(value, "englishSpelling"), CloudProvider = String(value, "cloudProvider"),
            CloudTranscriptionModel = String(value, "cloudTranscriptionModel"), CloudTranscriptionDomain = String(value, "cloudTranscriptionDomain"),
            PostProcessingMode = Int(value, "postProcessingMode"), PostProcessingProvider = String(value, "postProcessingProvider"),
            LanguageModel = String(value, "languageModel"), LocalPostProcessingModel = String(value, "localPostProcessingModel"),
            UserSystemPrompt = String(value, "userSystemPrompt"), CustomInstructions = String(value, "customInstructions"),
            GeminiCustomPrompt = String(value, "geminiCustomPrompt"), CloudAccuracyTier = String(value, "cloudAccuracyTier") ?? "elevenLabsScribeV2",
            CloudPostProcessingModel = String(value, "cloudPostProcessingModel") ?? "anthropic:claude-haiku-4-5",
            LocalEngine = String(linux, "localEngine") ?? "whisper", LocalParakeetModel = String(linux, "localParakeetModel"),
            ProviderType = String(linux, "providerType") ?? (String(value, "cloudProvider") is null ? "local" : "cloud"),
            EnableScreenOCR = Bool(linux, "enableScreenOCR"), CustomVocabulary = StringList(linux, "customVocabulary"),
            IsSystemProvided = Bool(linux, "isSystemProvided"),
            ForeignPlatformExtensions = preservedExtensions is null || preservedExtensions.Count == 0 ? null : preservedExtensions.ToJsonString(),
            CreatedDate = Date(linux, "createdDate") ?? DateTime.UtcNow,
            ModifiedDate = Date(linux, "modifiedDate") ?? DateTime.UtcNow,
        };
    }

    private static List<string>? StringList(JsonObject? value, string key)
    {
        if (value?[key] is null) return null;
        if (value[key] is JsonArray array)
            return array.Select(item => item?.GetValue<string>() ?? throw new JsonException("Vocabulary terms must be strings.")).ToList();
        var legacy = value[key]!.GetValue<string>();
        return legacy.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static DateTime? Date(JsonObject? value, string key)
    {
        var raw = String(value, key);
        if (raw is null) return null;
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new JsonException("Mode timestamp must be an ISO-8601 date.");
        return parsed;
    }

    private static VocabularyItem ParseVocabulary(JsonNode? node)
    {
        var value = node as JsonObject ?? throw new JsonException("Vocabulary item must be an object.");
        return new VocabularyItem { Id = Guid.Parse(value["id"]!.GetValue<string>()), Word = value["word"]!.GetValue<string>(), Replacement = String(value, "replacement"), SortOrder = Int(value, "sortOrder") };
    }


    private static void ValidateModes(IReadOnlyList<Mode> modes)
    {
        if (modes.Select(item => item.Id).Distinct().Count() != modes.Count
            || modes.Select(item => item.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != modes.Count
            || modes.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            throw new InvalidOperationException("Duplicate or invalid modes.");
    }

    private static void ValidateVocabulary(IReadOnlyList<VocabularyItem> vocabulary)
    {
        if (vocabulary.Select(item => item.Id).Distinct().Count() != vocabulary.Count
            || vocabulary.Select(item => item.Word.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != vocabulary.Count
            || vocabulary.Any(item => string.IsNullOrWhiteSpace(item.Word)))
            throw new InvalidOperationException("Duplicate or invalid vocabulary.");
    }

    private static JsonObject? ReadObject(JsonElement? element) => element is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText()) as JsonObject : null;
    private static JsonObject? ReadObject(string? json) { try { return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject; } catch (JsonException) { return null; } }
    private static string? String(JsonObject? value, string key) => value?[key] is null ? null : value[key]!.GetValue<string?>();
    private static bool Bool(JsonObject? value, string key, bool fallback = false) => value?[key]?.GetValue<bool?>() ?? fallback;
    private static int Int(JsonObject value, string key) => value[key]?.GetValue<int?>() ?? 0;
}
