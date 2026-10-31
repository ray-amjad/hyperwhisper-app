using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using Microsoft.Data.Sqlite;

// #2929: a word renamed since the export keeps its row id (VocabularyRepository.UpsertAsync
// matches by id). Restoring that backup matched no word, re-added the backup's item under the
// id that is already a row, and EF's identity conflict rolled back the whole import as
// `backup.invalid_settings`. The restore must succeed, bring back the old word, keep the
// renamed one, and agree with the preview's "vocabulary +1".
internal static class VocabularyRenamedRestoreTests
{
    public static async Task RunAsync()
    {
        foreach (var policy in new[] { VocabularyConflictPolicy.Replace, VocabularyConflictPolicy.Skip })
            await RenamedWordIsRestoredAsync(policy);
    }

    private static async Task RenamedWordIsRestoredAsync(VocabularyConflictPolicy policy)
    {
        var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.Backup.Application.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new RenameTestPaths(root);
            var settings = new PortableSettingsService(new RenameMemoryPrivateFileService(), paths);
            Check(settings.Load().IsSuccess, "settings did not initialize");
            var database = new ApplicationDb(paths);
            await database.MigrateAsync();
            await new ModeRepository(database).UpsertAsync(new Mode { Id = Guid.NewGuid(), Name = "First", IsDefault = true, SortOrder = 0 });
            var vocabulary = new VocabularyRepository(database);
            var service = new ApplicationBackupService(database, settings);

            var id = Guid.NewGuid();
            await vocabulary.UpsertAsync(new VocabularyItem { Id = id, Word = "Kubernets", SortOrder = 0 });
            var backup = await service.ExportAsync(new BackupExportSelection(IncludeSettings: false, IncludeModes: false, IncludeVocabulary: true));
            await vocabulary.UpsertAsync(new VocabularyItem { Id = id, Word = "Kubernetes", SortOrder = 0 });
            Check((await vocabulary.ListAsync()) is [{ Word: "Kubernetes" } renamed] && renamed.Id == id,
                "the rename did not keep the row id, so this test no longer reproduces #2929");

            var selection = new BackupImportSelection(ImportSettings: false, ImportModes: false, ImportVocabulary: true,
                VocabularyConflictPolicy: policy);
            var preview = await service.PreviewImportAsync(backup, selection);
            Check(preview.IsSuccess && preview.Value is { VocabularyAdded: 1, VocabularyReplaced: 0, VocabularySkipped: 0 },
                $"[{policy}] preview of a renamed-word restore was not 'vocabulary +1'");

            var result = await service.ImportAsync(backup, selection);
            Check(result.IsSuccess,
                $"[{policy}] restoring a backup after renaming its word failed (#2929): {result.Error?.Code}: {result.Error?.Message}");
            Check(result.Value is { VocabularyAdded: 1, VocabularyReplaced: 0, VocabularySkipped: 0 },
                $"[{policy}] the import summary disagreed with the preview");

            var after = await vocabulary.ListAsync();
            Check(after.Count == 2, $"[{policy}] expected 2 words after the restore, found {after.Count}");
            Check(after.SingleOrDefault(item => item.Word == "Kubernetes")?.Id == id,
                $"[{policy}] the restore changed the user's renamed row");
            Check(after.SingleOrDefault(item => item.Word == "Kubernets") is { } restored && restored.Id != id,
                $"[{policy}] the backup's old word was not restored as its own row (#2929)");

            // The merged list still exports and restores through Linux's own importer.
            var roundTrip = await service.ImportAsync(
                await service.ExportAsync(new BackupExportSelection(IncludeSettings: false, IncludeModes: false, IncludeVocabulary: true)),
                selection);
            Check(roundTrip.IsSuccess && (await vocabulary.ListAsync()).Count == 2,
                $"[{policy}] the merged list did not round-trip: {roundTrip.Error?.Code}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException error) { Console.Error.WriteLine($"warning: could not remove {root}: {error.Message}"); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

file sealed class RenameTestPaths(string root) : IAppPaths
{
    public string DataDirectory => root;
    public string ConfigDirectory => root;
    public string CacheDirectory => root;
    public string StateDirectory => root;
    public string LogsDirectory => root;
    public string ModelsDirectory => root;
    public string RecordingsDirectory => root;
    public string RuntimeDirectory => root;
    public string TemporaryDirectory => root;
}

file sealed class RenameMemoryPrivateFileService : IPrivateFileService
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        _files[path] = contents.ToArray();
        return PlatformResult.Success();
    }
    public PlatformResult WriteAllTextAtomically(string path, string contents)
        => WriteAllBytesAtomically(path, Encoding.UTF8.GetBytes(contents));
    public PlatformResult<byte[]?> ReadAllBytes(string path)
        => PlatformResult<byte[]?>.Success(_files.TryGetValue(path, out var bytes) ? bytes.ToArray() : null);
    public PlatformResult<string?> ReadAllText(string path)
        => PlatformResult<string?>.Success(_files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null);
    public PlatformResult Delete(string path) { _files.Remove(path); return PlatformResult.Success(); }
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => PlatformResult<bool>.Success(true);
}
