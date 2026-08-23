using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;

var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.Backup.Application.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var paths = new TestPaths(root);
    var files = new MemoryPrivateFileService();
    var settings = new PortableSettingsService(files, paths);
    Assert(settings.Load().IsSuccess, "settings did not initialize");
    settings.Set("language", "en");
    Assert(settings.Save().IsSuccess, "settings seed did not save");
    var database = new ApplicationDb(paths);
    await database.MigrateAsync();
    var modes = new ModeRepository(database);
    var first = new Mode { Id = Guid.NewGuid(), Name = "First", IsDefault = true, SortOrder = 0 };
    var second = new Mode { Id = Guid.NewGuid(), Name = "Second", SortOrder = 1 };
    await modes.UpsertAsync(first);
    await modes.UpsertAsync(second);
    var vocabulary = new VocabularyRepository(database);
    await vocabulary.AddAsync(new VocabularyItem { Id = Guid.NewGuid(), Word = "Alpha", Replacement = "old", SortOrder = 0 });
    var service = new ApplicationBackupService(database, settings);

    var vocabularyOnly = await service.ExportAsync(new BackupExportSelection(
        IncludeSettings: false, IncludeModes: false, IncludeVocabulary: true));
    var vocabularyOnlyRoot = JsonNode.Parse(vocabularyOnly)!.AsObject();
    Assert(!vocabularyOnlyRoot.ContainsKey("settings") && !vocabularyOnlyRoot.ContainsKey("platformExtensions")
        && !vocabularyOnlyRoot.ContainsKey("modes")
        && vocabularyOnlyRoot.ContainsKey("vocabulary"), "selective export did not preserve section presence");
    Assert(!vocabularyOnlyRoot.ContainsKey("apiKeys") && !vocabularyOnlyRoot.ContainsKey("licenseKey"),
        "export leaked a credential or account/license field");
    var inspectedVocabulary = service.Inspect(vocabularyOnly);
    Assert(inspectedVocabulary.IsSuccess && inspectedVocabulary.Value is
        { HasSettings: false, HasModes: false, HasVocabulary: true, VocabularyCount: 1 },
        "inspection did not distinguish absent sections");
    await AssertThrowsAsync<NotSupportedException>(
        () => service.ExportAsync(new BackupExportSelection(IncludeCredentials: true)),
        "credential export opt-in was not rejected");
    var selectedExport = JsonNode.Parse(await service.ExportAsync(BackupExportSelection.SelectedModes([second.Id])))!.AsObject();
    Assert(selectedExport["modes"]!.AsArray() is [{ } selectedMode]
        && selectedMode["id"]!.GetValue<string>() == second.Id.ToString("D")
        && selectedMode["isDefault"]!.GetValue<bool>(),
        "selected-mode export did not explicitly produce a self-contained default mode");

    var full = JsonNode.Parse(await service.ExportAsync())!.AsObject();
    full["apiKeys"] = new JsonObject { ["openai"] = "must-not-import" };
    full["licenseKey"] = "must-not-import";
    full["platformExtensions"]!["windows"] = new JsonObject { ["futureWindows"] = 17 };
    full["platformExtensions"]!["linux"]!["settings"]!["language"] = "fr";
    full["platformExtensions"]!["linux"]!["settings"]!["themeMode"] = "dark";
    full["platformExtensions"]!["linux"]!["settings"]!["minimizeToTray"] = false;
    full["settings"]!["general"]!["launchMinimized"] = true;
    full["settings"]!["general"]!["showRecordingWindow"] = false;
    full["settings"]!["advanced"]!["typingSpeedWPM"] = 80;
    var fullModes = full["modes"]!.AsArray();
    fullModes[0]!["name"] = "Imported First";
    fullModes[0]!["platformExtensions"]!["macos"] = new JsonObject { ["futureMac"] = "keep" };
    var fullVocabulary = full["vocabulary"]!.AsArray();
    fullVocabulary[0]!["replacement"] = "new";
    fullVocabulary.Add(new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString("D"), ["word"] = "Beta", ["replacement"] = "second",
        ["sortOrder"] = 1, ["source"] = "manual",
    });
    var importJson = full.ToJsonString();
    var inspection = service.Inspect(importJson);
    Assert(inspection.IsSuccess && inspection.Value is { ContainsCredentials: true, ContainsLicenseKey: true, HasModes: true },
        "inspection did not report sensitive input fields without importing them");

    var skipSelection = new BackupImportSelection(
        ImportSettings: false, ImportModes: false, ImportVocabulary: true,
        VocabularyConflictPolicy: VocabularyConflictPolicy.Skip);
    var skipPreview = await service.PreviewImportAsync(importJson, skipSelection);
    Assert(skipPreview.IsSuccess && skipPreview.Value is
        { WillImportSettings: false, VocabularyAdded: 1, VocabularySkipped: 1, VocabularyReplaced: 0 },
        "skip-conflict preview counts were wrong");
    var skipped = await service.ImportAsync(importJson, skipSelection);
    Assert(skipped.IsSuccess && skipped.Value is { VocabularyAdded: 1, VocabularySkipped: 1 },
        "skip-conflict summary counts were wrong");
    var afterSkip = await vocabulary.ListAsync();
    Assert(afterSkip.Single(item => item.Word == "Alpha").Replacement == "old" && afterSkip.Any(item => item.Word == "Beta"),
        "skip-conflict import changed the conflicting value or omitted the new value");
    Assert((await modes.ListAsync()).Any(item => item.Name == "First") && settings.Get<string>("language") == "en",
        "unselected sections were imported");

    var replaceSelection = skipSelection with { VocabularyConflictPolicy = VocabularyConflictPolicy.Replace };
    var replaced = await service.ImportAsync(importJson, replaceSelection);
    Assert(replaced.IsSuccess && replaced.Value is { VocabularyAdded: 0, VocabularyReplaced: 2 },
        "replace-conflict summary counts were wrong");
    Assert((await vocabulary.ListAsync()).Single(item => item.Word == "Alpha").Replacement == "new",
        "replace-conflict policy did not deterministically replace the existing entry");

    var selectedModes = BackupImportSelection.SelectedModes([first.Id]);
    var modePreview = await service.PreviewImportAsync(importJson, selectedModes);
    Assert(modePreview.IsSuccess && modePreview.Value is { ModesAdded: 0, ModesReplaced: 1, ModesRemoved: 0 },
        "selected-mode preview counts were wrong");
    var selectedResult = await service.ImportAsync(importJson, selectedModes);
    Assert(selectedResult.IsSuccess, "selected-mode import failed");
    var afterSelected = await modes.ListAsync();
    Assert(afterSelected.Single(item => item.Id == first.Id).Name == "Imported First"
        && afterSelected.Single(item => item.Id == second.Id).Name == "Second",
        "selected-mode merge changed an unselected mode");

    var emptySelection = selectedModes with { SelectedModeIds = ImmutableHashSet<Guid>.Empty };
    Assert((await service.ImportAsync(importJson, emptySelection)).IsSuccess,
        "an explicit empty selected-mode set was not treated as a no-op");
    var missingSelection = selectedModes with { SelectedModeIds = ImmutableHashSet.Create(Guid.NewGuid()) };
    var missingPreview = await service.PreviewImportAsync(importJson, missingSelection);
    Assert(missingPreview.IsSuccess && missingPreview.Value!.MissingSelectedModeIds.Length == 1,
        "preview did not report a missing selected mode");
    Assert((await service.ImportAsync(importJson, missingSelection)).Error?.Code == "backup.selected_modes_missing",
        "a missing selected mode was silently ignored");

    var noModes = full.DeepClone().AsObject();
    noModes.Remove("modes");
    var beforeAbsentModes = (await modes.ListAsync()).Select(item => (item.Id, item.Name)).ToArray();
    Assert((await service.ImportAsync(noModes.ToJsonString(), BackupImportSelection.All)).IsSuccess,
        "backup with an absent modes section failed");
    Assert(settings.Get<string>("themeMode") == "dark" && !settings.Get("minimizeToTray", true)
        && settings.Get("general.launchMinimized", false) && !settings.Get("general.showRecordingWindow", true)
        && settings.Get("advanced.typingSpeedWPM", 0) == 80,
        "shell, overlay, tray, or typing-speed settings did not import through their canonical mappings");
    Assert(beforeAbsentModes.SequenceEqual((await modes.ListAsync()).Select(item => (item.Id, item.Name))),
        "an absent modes section modified the current mode library");

    var emptyModes = full.DeepClone().AsObject();
    emptyModes["modes"] = new JsonArray();
    Assert((await service.ImportAsync(emptyModes.ToJsonString(), BackupImportSelection.All)).Error?.Code == "backup.no_modes",
        "an explicitly empty replace-all modes section wiped or bypassed the mode invariant");

    var rollback = full.DeepClone().AsObject();
    rollback["modes"]![0]!["name"] = "Must Roll Back";
    files.FailWrites = true;
    var failed = await service.ImportAsync(rollback.ToJsonString(), BackupImportSelection.All);
    files.FailWrites = false;
    Assert(failed.IsFailure && (await modes.ListAsync()).All(item => item.Name != "Must Roll Back")
        && settings.Get<string>("language") == "fr", "settings failure did not roll back DB and in-memory settings");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(
        () => service.ImportAsync(importJson, BackupImportSelection.All, cancelled.Token),
        "cancellation was swallowed");
    Assert((await modes.ListAsync()).All(item => item.Name != "Must Roll Back"), "cancellation mutated the database");

    var viewModelImportPath = Path.Combine(root, "view-model-import.json");
    await File.WriteAllTextAsync(viewModelImportPath, importJson);
    var backupViewModel = new BackupViewModel(service) { Path = viewModelImportPath };
    await backupViewModel.InspectAsync();
    Assert(backupViewModel.HasInspectedBackup && backupViewModel.Contents is
        { ContainsCredentials: true, ContainsLicenseKey: true }
        && backupViewModel.ContainsUnsupportedSensitiveData
        && backupViewModel.SensitiveDataNotice.Contains("not supported", StringComparison.Ordinal),
        "view model did not inspect or visibly reject sensitive backup fields");
    Assert(backupViewModel.ImportModeSelections.Count == 2 && !backupViewModel.CanConfirmImport,
        "view model did not expose inspected modes or required a preview");
    backupViewModel.ImportSettings = false;
    backupViewModel.ImportModes = false;
    backupViewModel.SkipVocabularyConflicts = true;
    await backupViewModel.PreviewAsync();
    Assert(backupViewModel.CanConfirmImport && backupViewModel.Preview is
        { WillImportSettings: false, ModesAdded: 0, ModesReplaced: 0, VocabularySkipped: 2 }
        && backupViewModel.PreviewSummary.Contains("skip 2", StringComparison.Ordinal),
        "view model preview did not reflect selected sections and conflict policy");
    backupViewModel.ReplaceVocabularyConflicts = true;
    Assert(!backupViewModel.CanConfirmImport, "changing a selection did not invalidate stale confirmation");
    await backupViewModel.PreviewAsync();
    await File.WriteAllTextAsync(viewModelImportPath, "not the inspected backup");
    var importedEventRaised = false;
    backupViewModel.Imported += (_, _) => importedEventRaised = true;
    await backupViewModel.ImportAsync();
    Assert(importedEventRaised && !backupViewModel.CanConfirmImport
        && backupViewModel.OperationSummary.Contains("Imported settings: False", StringComparison.Ordinal),
        "confirmed import did not use the inspected/previewed snapshot or expose its summary");

    var viewModelExportPath = Path.Combine(root, "view-model-export.json");
    backupViewModel.Path = viewModelExportPath;
    backupViewModel.ExportSettings = false;
    backupViewModel.ExportModes = false;
    backupViewModel.ExportVocabulary = true;
    await backupViewModel.ExportAsync();
    var viewModelExport = JsonNode.Parse(await File.ReadAllTextAsync(viewModelExportPath))!.AsObject();
    Assert(!viewModelExport.ContainsKey("settings") && !viewModelExport.ContainsKey("modes")
        && viewModelExport.ContainsKey("vocabulary") && !viewModelExport.ContainsKey("apiKeys")
        && backupViewModel.OperationSummary.Contains("account/license keys were excluded", StringComparison.Ordinal),
        "view model export did not honor section selection or disclose sensitive-field exclusion");
    backupViewModel.Path = "relative.json";
    await backupViewModel.InspectAsync();
    Assert(backupViewModel.Status.ErrorCode == "backup.path_required", "view model path validation was not stable");
    backupViewModel.Path = viewModelImportPath;
    using var viewModelCancellation = new CancellationTokenSource();
    viewModelCancellation.Cancel();
    await backupViewModel.InspectAsync(viewModelCancellation.Token);
    Assert(backupViewModel.Status.ErrorCode == "backup.cancelled", "view model cancellation was not reported distinctly");

    var roundTrip = JsonNode.Parse(await service.ExportAsync())!.AsObject();
    Assert(roundTrip["platformExtensions"]!["windows"]!["futureWindows"]!.GetValue<int>() == 17
        && roundTrip["modes"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == first.Id.ToString("D"))!
            ["platformExtensions"]!["macos"]!["futureMac"]!.GetValue<string>() == "keep",
        "foreign platform extensions were not preserved across import/export");

    Console.WriteLine("Backup application tests passed (27/27).");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
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
        if (FailWrites) return PlatformResult.Failure("test.write_failed", "Simulated write failure.");
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
