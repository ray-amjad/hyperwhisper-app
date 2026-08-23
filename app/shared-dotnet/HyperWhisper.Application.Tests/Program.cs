using System.Text;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;

var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.Application.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var paths = new TestPaths(root);
    var database = new ApplicationDb(paths);
    await database.MigrateAsync();

    await using (var context = database.CreateContext())
    {
        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert(applied.Count() == 14, $"expected all 14 EF migrations, got {applied.Count()}");
        Assert(await context.Database.CanConnectAsync(), "SQLite database is not connectable");
    }

    var history = new HistoryRepository(database);
    var transcript = new Transcript
    {
        Text = "portable history",
        TranscribedText = "portable history",
        Status = TranscriptStatus.Completed,
        Duration = 1.25,
        Date = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc)
    };
    await history.AddAsync(transcript);
    Assert((await history.GetAsync(transcript.Id))?.Text == "portable history", "history create/read failed");
    transcript.Text = "updated history";
    Assert(await history.UpdateAsync(transcript), "history update failed");
    Assert((await history.GetAsync(transcript.Id))?.Text == "updated history", "history update did not persist");

    var vocabulary = new VocabularyRepository(database);
    var vocabularyItem = new VocabularyItem { Word = "HyperWhisper", Replacement = "HyperWhisper", SortOrder = 2 };
    await vocabulary.AddAsync(vocabularyItem);
    Assert((await vocabulary.ListAsync()).Single().Id == vocabularyItem.Id, "vocabulary CRUD failed");

    var modes = new ModeRepository(database);
    var mode = new Mode { Name = "Portable", SortOrder = 3, IsDefault = true };
    await modes.UpsertAsync(mode);
    Assert((await modes.ListAsync()).Single().Id == mode.Id, "mode create failed");
    mode.Name = "Portable Updated";
    await modes.UpsertAsync(mode);
    Assert((await modes.ListAsync()).Single().Name == "Portable Updated", "mode update failed");

    var files = new MemoryPrivateFileService();
    var settings = new PortableSettingsService(files, Path.Combine(root, "settings.json"));
    settings.Set("language", "en");
    settings.Set("enabled", true);
    Assert(settings.Save().IsSuccess, "settings save failed");
    var reloadedSettings = new PortableSettingsService(files, Path.Combine(root, "settings.json"));
    Assert(reloadedSettings.Load().IsSuccess, "settings load failed");
    Assert(reloadedSettings.Get<string>("language") == "en", "settings value did not round-trip");

    var backupService = new ApplicationBackupService(database, reloadedSettings);
    var exported = await backupService.ExportAsync();
    Assert(exported.Contains("Portable Updated", StringComparison.Ordinal), "backup omitted modes");
    Assert(exported.Contains("HyperWhisper", StringComparison.Ordinal), "backup omitted vocabulary");

    Assert(await history.DeleteAsync(transcript.Id), "history delete failed");
    Assert(await vocabulary.DeleteAsync(vocabularyItem.Id), "vocabulary delete failed");
    Assert(await modes.DeleteAsync(mode.Id), "mode delete failed");
    reloadedSettings.Set("language", "fr");
    files.FailWrites = true;
    Assert((await backupService.ImportAsync(exported)).IsFailure, "backup import ignored a settings write failure");
    Assert(await history.GetAsync(transcript.Id) is null, "failed backup import committed history");
    Assert((await vocabulary.ListAsync()).Count == 0, "failed backup import committed vocabulary");
    Assert((await modes.ListAsync()).Count == 0, "failed backup import committed modes");
    Assert(reloadedSettings.Get<string>("language") == "fr", "failed backup import changed in-memory settings");
    files.FailWrites = false;
    Assert((await backupService.ImportAsync(exported)).IsSuccess, "backup import failed");
    Assert((await history.GetAsync(transcript.Id))?.Text == "updated history", "backup did not restore history");
    Assert((await vocabulary.ListAsync()).Single().Word == "HyperWhisper", "backup did not restore vocabulary");
    Assert((await modes.ListAsync()).Single().Name == "Portable Updated", "backup did not restore modes");
    Assert(reloadedSettings.Get<string>("language") == "en", "backup did not restore settings");

    using (var shell = new HyperWhisper.PortableApplication.ViewModels.ApplicationShellViewModel(database, reloadedSettings))
    {
        await shell.InitializeAsync();
        Assert(!shell.Status.HasError, $"shell initialization failed: {shell.Status.ErrorCode}");
        foreach (var page in new[] { "home", "history", "vocabulary", "modes", "settings" })
        {
            shell.Navigate(page);
            Assert(shell.CurrentPage != null && shell.PageTitle.Length > 0, $"navigation failed for {page}");
        }

        await shell.History.DeleteAsync(null);
        Assert(shell.History.Status.ErrorCode == "history.no_selection", "history did not expose a structured selection failure");
        shell.Vocabulary.Word = " ";
        await shell.Vocabulary.AddAsync();
        Assert(shell.Vocabulary.Status.ErrorCode == "vocabulary.word_required", "vocabulary accepted an empty term");
        shell.Modes.Selected = null;
        shell.Modes.Name = "";
        await shell.Modes.SaveAsync();
        Assert(shell.Modes.Status.ErrorCode == "modes.name_required", "modes accepted an empty name");
    }

    var failingHome = new HyperWhisper.PortableApplication.ViewModels.HomeViewModel(
        new HistoryRepository(new ApplicationDb(() => throw new IOException("expected test failure"))),
        new VocabularyRepository(database),
        new ModeRepository(database));
    await failingHome.RefreshAsync();
    Assert(failingHome.Status.ErrorCode == "home.refresh_failed", "repository failure was reported as success");

    Console.WriteLine("HyperWhisper.Application persistence tests passed.");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class TestPaths(string root) : IAppPaths
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

file sealed class MemoryPrivateFileService : IPrivateFileService
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    public bool FailWrites { get; set; }

    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        if (FailWrites)
            return PlatformResult.Failure("test.write_failed", "The simulated private write failed.");
        _files[path] = contents.ToArray();
        return PlatformResult.Success();
    }

    public PlatformResult WriteAllTextAtomically(string path, string contents)
        => WriteAllBytesAtomically(path, Encoding.UTF8.GetBytes(contents));

    public PlatformResult<byte[]?> ReadAllBytes(string path)
        => PlatformResult<byte[]?>.Success(_files.TryGetValue(path, out var value) ? value.ToArray() : null);

    public PlatformResult<string?> ReadAllText(string path)
        => PlatformResult<string?>.Success(
            _files.TryGetValue(path, out var value) ? Encoding.UTF8.GetString(value) : null);

    public PlatformResult Delete(string path)
    {
        _files.Remove(path);
        return PlatformResult.Success();
    }

    public PlatformResult<bool> IsRestrictedToCurrentUser(string path)
        => PlatformResult<bool>.Success(_files.ContainsKey(path));
}
