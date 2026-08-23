using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
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

public sealed class HistoryRepository(ApplicationDb database) : ITranscriptionHistoryStore
{
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));

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

public sealed record ApplicationBackup
{
    public int Version { get; init; } = 1;
    public List<Transcript> Transcripts { get; init; } = new();
    public List<Mode> Modes { get; init; } = new();
    public List<VocabularyItem> Vocabulary { get; init; } = new();
    public Dictionary<string, JsonElement> Settings { get; init; } = new(StringComparer.Ordinal);
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
        var backup = new ApplicationBackup
        {
            Transcripts = await context.Transcripts.AsNoTracking().OrderBy(item => item.Date).ToListAsync(cancellationToken),
            Modes = await context.Modes.AsNoTracking().OrderBy(item => item.SortOrder).ToListAsync(cancellationToken),
            Vocabulary = await context.VocabularyItems.AsNoTracking().OrderBy(item => item.SortOrder).ToListAsync(cancellationToken),
            Settings = _settings.Snapshot().ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal)
        };
        return JsonSerializer.Serialize(backup, SerializerOptions);
    }

    public async Task<PlatformResult> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Backup JSON is required.", nameof(json));
        ApplicationBackup? backup;
        try { backup = JsonSerializer.Deserialize<ApplicationBackup>(json, SerializerOptions); }
        catch (JsonException) { return PlatformResult.Failure("backup.invalid_json", "The application backup is not valid JSON."); }
        if (backup == null || backup.Version != 1)
            return PlatformResult.Failure("backup.unsupported_version", "The application backup version is not supported.");

        await using var context = _database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var previousSettings = _settings.Snapshot();
        await context.Transcripts.ExecuteDeleteAsync(cancellationToken);
        await context.Modes.ExecuteDeleteAsync(cancellationToken);
        await context.VocabularyItems.ExecuteDeleteAsync(cancellationToken);
        context.Transcripts.AddRange(backup.Transcripts);
        context.Modes.AddRange(backup.Modes);
        context.VocabularyItems.AddRange(backup.Vocabulary);
        await context.SaveChangesAsync(cancellationToken);

        _settings.Replace(backup.Settings);
        var saved = _settings.Save();
        if (saved.IsFailure)
        {
            _settings.Replace(previousSettings);
            return PlatformResult.Failure(saved.Error!.Code, saved.Error.Message);
        }

        try
        {
            await transaction.CommitAsync(cancellationToken);
            return PlatformResult.Success();
        }
        catch
        {
            _settings.Replace(previousSettings);
            _ = _settings.Save();
            throw;
        }
    }
}
