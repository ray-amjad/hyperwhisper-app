using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using Microsoft.Data.Sqlite;

// #2930: an incoming word was matched and stored without trimming, so a backup's 'Kubernetes '
// landed as a second entry beside 'Kubernetes' (preview "added 1"), and Linux's next export of
// that list was refused by its own importer as a trimmed duplicate. Windows, UpsertAsync and
// ValidateVocabulary all trim; the import must too, in the preview, the match and the store.
internal static class VocabularyTrimMatchTests
{
    private const string Backup = """
        {"schemaVersion":2,"exportDate":"2026-10-30T00:00:00Z","appVersion":"1.0.0","platform":"macos",
         "vocabulary":[
           {"id":"6f0c3f2e-1111-4a4a-9b9b-000000000001","word":"Kubernetes ","replacement":"kube","sortOrder":0},
           {"id":"6f0c3f2e-1111-4a4a-9b9b-000000000002","word":"docker","replacement":"Docker","sortOrder":1},
           {"id":"6f0c3f2e-1111-4a4a-9b9b-000000000003","word":"  Helm  ","sortOrder":2}]}
        """;

    public static async Task RunAsync()
    {
        await TrimmedWordMatchesAsync(VocabularyConflictPolicy.Replace);
        await TrimmedWordMatchesAsync(VocabularyConflictPolicy.Skip);
    }

    private static async Task TrimmedWordMatchesAsync(VocabularyConflictPolicy policy)
    {
        var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.Backup.Application.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new TrimTestPaths(root);
            var settings = new PortableSettingsService(new TrimMemoryPrivateFileService(), paths);
            Check(settings.Load().IsSuccess, "settings did not initialize");
            var database = new ApplicationDb(paths);
            await database.MigrateAsync();
            var vocabulary = new VocabularyRepository(database);
            await vocabulary.AddAsync(new VocabularyItem { Id = Guid.NewGuid(), Word = "Kubernetes", Replacement = "K8s", SortOrder = 0 });
            // AddAsync stores the word as given, so this stands in for a row saved before the
            // repository trimmed: the existing side of the match has to be trimmed as well.
            await vocabulary.AddAsync(new VocabularyItem { Id = Guid.NewGuid(), Word = "Docker ", SortOrder = 1 });
            var service = new ApplicationBackupService(database, settings);
            var selection = new BackupImportSelection(ImportSettings: false, ImportModes: false, ImportVocabulary: true,
                VocabularyConflictPolicy: policy);
            var replace = policy == VocabularyConflictPolicy.Replace;

            var preview = await service.PreviewImportAsync(Backup, selection);
            Check(preview.IsSuccess && preview.Value is { VocabularyAdded: 1 } counts
                && counts.VocabularyReplaced == (replace ? 2 : 0) && counts.VocabularySkipped == (replace ? 0 : 2),
                $"[{policy}] preview did not count the trimmed words as conflicts (#2930): added={preview.Value?.VocabularyAdded} replaced={preview.Value?.VocabularyReplaced} skipped={preview.Value?.VocabularySkipped}");

            var result = await service.ImportAsync(Backup, selection);
            Check(result.IsSuccess, $"[{policy}] import failed: {result.Error?.Code}: {result.Error?.Message}");
            Check(result.Value is { VocabularyAdded: 1 } summary
                && summary.VocabularyReplaced == preview.Value!.VocabularyReplaced && summary.VocabularySkipped == preview.Value.VocabularySkipped,
                $"[{policy}] the import summary disagreed with the preview");

            var after = await vocabulary.ListAsync();
            var words = string.Join(", ", after.Select(item => $"[{item.Word}] -> {item.Replacement}"));
            Check(after.Count == 3, $"[{policy}] expected 3 words after the merge, found {after.Count}: {words}");
            Check(after.Count(item => string.Equals(item.Word.Trim(), "Kubernetes", StringComparison.OrdinalIgnoreCase)) == 1,
                $"[{policy}] 'Kubernetes ' landed as a second entry (#2930): {words}");
            Check(after.Single(item => item.Word.Trim() == "Kubernetes").Replacement == (replace ? "kube" : "K8s"),
                $"[{policy}] the conflict policy was not applied to the trimmed match: {words}");
            Check(after.Count(item => string.Equals(item.Word.Trim(), "docker", StringComparison.OrdinalIgnoreCase)) == 1,
                $"[{policy}] 'docker' did not match the existing untrimmed 'Docker ': {words}");
            Check(after.Any(item => item.Word == "Helm"), $"[{policy}] the new word was not stored trimmed: {words}");

            // Linux's own export of the merged list must restore through Linux's importer.
            var reimport = await service.ImportAsync(
                await service.ExportAsync(new BackupExportSelection(IncludeSettings: false, IncludeModes: false, IncludeVocabulary: true)),
                selection);
            Check(reimport.IsSuccess && (await vocabulary.ListAsync()).Count == 3,
                $"[{policy}] Linux refused its own export of the merged list: {reimport.Error?.Code}");
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

file sealed class TrimTestPaths(string root) : IAppPaths
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

file sealed class TrimMemoryPrivateFileService : IPrivateFileService
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
