using System.Text;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.PortableApplication.Transcription;

var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.Application.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var paths = new TestPaths(root);
    var database = new ApplicationDb(paths);
    await database.MigrateAsync();
    await RunTranscriptionWorkflowTestsAsync(root);

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

static async Task RunTranscriptionWorkflowTestsAsync(string root)
{
    static async Task<(ApplicationDb Database, HistoryRepository History)> CreateStoreAsync(string parent, string name)
    {
        var storeRoot = Path.Combine(parent, name);
        Directory.CreateDirectory(storeRoot);
        var database = new ApplicationDb(new TestPaths(storeRoot));
        await database.MigrateAsync();
        return (database, new HistoryRepository(database));
    }

    var successStore = await CreateStoreAsync(root, "workflow-success");
    var successAudio = Path.Combine(root, "success.wav");
    await File.WriteAllBytesAsync(successAudio, [1, 2, 3]);
    using (var recorder = new FakeRecorder(successAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success("portable words", "Test Whisper"))),
        successStore.History))
    {
        var observedStates = new List<TranscriptionWorkflowState>();
        workflow.Changed += (_, _) => throw new InvalidOperationException("simulated subscriber failure");
        workflow.Changed += (_, _) => observedStates.Add(workflow.Snapshot.State);
        workflow.RefreshDevices();
        var started = await workflow.StartRecordingAsync();
        Assert(started.IsSuccess, "recording start did not report unambiguous success");
        Assert(workflow.Snapshot.State == TranscriptionWorkflowState.Recording, "recording did not enter Recording state");
        var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest("en", "Test", Guid.NewGuid()));
        Assert(result.IsSuccess, "successful recording transcription failed");
        var saved = await successStore.History.ListAsync();
        Assert(saved.Count == 1 && saved[0].Text == "portable words" && saved[0].Status == TranscriptStatus.Completed,
            "successful transcription was not persisted exactly once");
        Assert(observedStates.Contains(TranscriptionWorkflowState.Completed), "completion transition was not notified");
        var fileResult = await workflow.TranscribeFileAsync(successAudio, new TranscriptionWorkflowRequest("en", "File test"));
        Assert(fileResult.IsSuccess && (await successStore.History.ListAsync()).Count == 2,
            "successful file transcription was not persisted");
    }

    var persistenceStore = await CreateStoreAsync(root, "workflow-persistence-failure");
    await using (var context = persistenceStore.Database.CreateContext())
    {
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_transcript BEFORE INSERT ON Transcripts BEGIN SELECT RAISE(ABORT, 'simulated persistence failure'); END;");
    }
    var persistenceAudio = Path.Combine(root, "persistence.wav");
    await File.WriteAllBytesAsync(persistenceAudio, [1]);
    using (var recorder = new FakeRecorder(persistenceAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success("valid backend text", "Test Whisper"))),
        persistenceStore.History))
    {
        workflow.RefreshDevices();
        var result = await workflow.TranscribeFileAsync(persistenceAudio, new TranscriptionWorkflowRequest());
        Assert(!result.IsSuccess && workflow.Snapshot.State == TranscriptionWorkflowState.Failed
            && workflow.Snapshot.ErrorCode == "workflow.persistence_failed",
            "persistence failure after backend success was not classified or exposed correctly");
        Assert((await persistenceStore.History.ListAsync()).Count == 0, "persistence failure created fake completed history");
    }

    var failureStore = await CreateStoreAsync(root, "workflow-failure");
    var failureAudio = Path.Combine(root, "failure.wav");
    await File.WriteAllBytesAsync(failureAudio, [1]);
    using (var recorder = new FakeRecorder(failureAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Failed(
            PortableTranscriptionErrorCode.TranscriptionFailed,
            "expected backend rejection"))),
        failureStore.History))
    {
        var failedWasNotified = false;
        workflow.Changed += (_, _) => failedWasNotified |= workflow.Snapshot.State == TranscriptionWorkflowState.Failed;
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest());
        Assert(!result.IsSuccess && failedWasNotified, "backend result failure transition was not visible");
        Assert((await failureStore.History.ListAsync()).Count == 0, "failed transcription created fake history");
    }

    var exceptionStore = await CreateStoreAsync(root, "workflow-exception");
    var exceptionAudio = Path.Combine(root, "exception.wav");
    await File.WriteAllBytesAsync(exceptionAudio, [1]);
    using (var recorder = new FakeRecorder(exceptionAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => throw new InvalidOperationException("simulated backend exception")),
        exceptionStore.History))
    {
        workflow.RefreshDevices();
        var result = await workflow.TranscribeFileAsync(exceptionAudio, new TranscriptionWorkflowRequest());
        Assert(!result.IsSuccess && workflow.Snapshot.ErrorCode == "workflow.backend_failed",
            "backend exception was misclassified as a persistence failure");
        Assert((await exceptionStore.History.ListAsync()).Count == 0, "backend exception created fake history");
    }

    var cancellationStore = await CreateStoreAsync(root, "workflow-cancel");
    var cancellationAudio = Path.Combine(root, "cancel.wav");
    await File.WriteAllBytesAsync(cancellationAudio, [1]);
    var transcriptionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var recorder = new FakeRecorder(cancellationAudio))
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber(async (_, _, cancellationToken) =>
        {
            transcriptionEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PortableTranscriptionResult.Success("unreachable", "Test Whisper");
        }),
        cancellationStore.History))
    {
        workflow.RefreshDevices();
        var task = workflow.TranscribeFileAsync(cancellationAudio, new TranscriptionWorkflowRequest());
        await transcriptionEntered.Task;
        await workflow.CancelAsync();
        var result = await task;
        Assert(result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled, "file transcription cancellation was not structured");
        Assert((await cancellationStore.History.ListAsync()).Count == 0, "cancelled transcription created fake history");
    }

    var raceStore = await CreateStoreAsync(root, "workflow-stop-race");
    var raceAudio = Path.Combine(root, "race.wav");
    await File.WriteAllBytesAsync(raceAudio, [1]);
    using (var recorder = new FakeRecorder(raceAudio) { BlockStop = true })
    using (var devices = new FakeDevices())
    using (var workflow = new TranscriptionWorkflow(
        recorder,
        devices,
        new FakeTranscriber((_, _, _) => Task.FromResult(PortableTranscriptionResult.Success("should cancel", "Test Whisper"))),
        raceStore.History))
    {
        workflow.RefreshDevices();
        await workflow.StartRecordingAsync();
        var stopping = Task.Run(() => workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest()));
        await recorder.StopEntered.Task;
        await workflow.CancelAsync();
        Assert(recorder.StopCount == 1, "cancel raced with stop and called recorder.Stop twice");
        recorder.ReleaseStop.Set();
        var result = await stopping;
        Assert(result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled, "stop/cancel race did not cancel transcription");
        Assert(recorder.StopCount == 1, "stop/cancel race called recorder.Stop twice after completion");
        Assert((await raceStore.History.ListAsync()).Count == 0, "stop/cancel race created fake history");
    }
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

file sealed class FakeDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() =>
        PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([new AudioInputDevice("test", "Test microphone", true)]);
    public void Dispose() { }
}

file sealed class FakeRecorder(string outputPath) : IAudioRecorder
{
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording { get; private set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(1.5);
    public bool BlockStop { get; init; }
    public int StopCount { get; private set; }
    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim ReleaseStop { get; } = new(initialState: false);
    public PlatformResult Start(AudioRecordingOptions options)
    {
        IsRecording = true;
        return PlatformResult.Success();
    }
    public PlatformResult<string> Stop()
    {
        StopCount++;
        StopEntered.TrySetResult();
        if (BlockStop) ReleaseStop.Wait(TimeSpan.FromSeconds(5));
        IsRecording = false;
        return PlatformResult<string>.Success(outputPath);
    }
    public void Dispose()
    {
        ReleaseStop.Set();
        ReleaseStop.Dispose();
    }
}

file sealed class FakeTranscriber(
    Func<string, string?, CancellationToken, Task<PortableTranscriptionResult>> transcribe) : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability { get; } = new(true, "Test Whisper");
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language, CancellationToken cancellationToken = default) =>
        transcribe(audioPath, language, cancellationToken);
}
