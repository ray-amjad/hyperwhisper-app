using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Storage;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.PortableApplication.Transcription;

using var fixture = new Fixture();
await fixture.Database.InitializeAsync();
var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

var owned = fixture.WriteRecording("recording-old.wav", 17);
var trimmed = fixture.WriteRecording("trimmed-old.wav", 19);
var recent = fixture.WriteRecording("recording-recent.wav", 23);
var external = fixture.WriteExternal("caller-original.wav", 29);
var linkedTarget = fixture.WriteExternal("linked-target.wav", 31);
var linked = Path.Combine(fixture.Paths.RecordingsDirectory, "linked.wav");
File.CreateSymbolicLink(linked, linkedTarget);

await using (var context = fixture.Database.CreateContext())
{
    context.Transcripts.AddRange(
        new Transcript { Text = "old owned", Status = TranscriptStatus.Completed, Date = now.AddDays(-31).UtcDateTime, AudioFilePath = owned, TrimmedAudioFilePath = trimmed },
        new Transcript { Text = "old external", Status = TranscriptStatus.Completed, Date = now.AddDays(-32).UtcDateTime, AudioFilePath = external },
        new Transcript { Text = "old linked", Status = TranscriptStatus.Completed, Date = now.AddDays(-33).UtcDateTime, AudioFilePath = linked },
        new Transcript { Text = "recent", Status = TranscriptStatus.Completed, Date = now.AddDays(-29).UtcDateTime, AudioFilePath = recent });
    await context.SaveChangesAsync();
}

var result = await fixture.Coordinator.CleanupAsync(new(true, true, 30), now);
Assert(result.Status == StorageLifecycleStatus.Completed, "cleanup status");
Assert(result.TranscriptsDeleted == 3 && result.AudioFilesDeleted == 2, "cleanup counts");
Assert(result.AudioBytesDeleted == 36 && result.ProtectedExternalFiles == 2, "byte and protected counts");
Assert(!File.Exists(owned) && !File.Exists(trimmed), "owned files deleted");
Assert(File.Exists(recent) && File.Exists(external) && File.Exists(linkedTarget) && File.Exists(linked), "protected and recent files retained");
await using (var context = fixture.Database.CreateContext())
    Assert(await context.Transcripts.CountAsync() == 1 && await context.Transcripts.AnyAsync(item => item.Text == "recent"), "transactional row cleanup");
Console.WriteLine("PASS transcript-age cleanup commits rows and deletes only app-owned audio");

var disabled = await fixture.Coordinator.CleanupAsync(new(true, false, 30), now);
Assert(disabled.Status == StorageLifecycleStatus.Disabled, "disabled policy");
await using (var context = fixture.Database.CreateContext())
    Assert(await context.Transcripts.CountAsync() == 1, "disabled cleanup preserves rows");
Console.WriteLine("PASS disabled cleanup preserves database and files");

var invalid = await fixture.Coordinator.CleanupAsync(new(true, true, 0), now);
Assert(invalid.Status == StorageLifecycleStatus.InvalidPolicy, "invalid policy");
Console.WriteLine("PASS invalid retention is rejected before persistence");

var failingRoot = Path.Combine(fixture.Root, "database-directory");
Directory.CreateDirectory(failingRoot);
var failingDatabase = new ApplicationDb(() => new HyperWhisper.Data.HyperWhisperDbContext(failingRoot));
var failingCoordinator = new TranscriptStorageCoordinator(failingDatabase, fixture.Storage);
var persistenceFailure = await failingCoordinator.CleanupAsync(new(true, true, 30), now);
Assert(persistenceFailure.Status == StorageLifecycleStatus.PersistenceFailure, "stable persistence failure");
Assert(File.Exists(recent), "persistence failure cannot delete audio");
Console.WriteLine("PASS persistence failure is stable and occurs before file deletion");

var inventory = await fixture.Coordinator.InventoryAsync();
Assert(inventory.FileCount == 1 && inventory.TotalBytes == 23 && inventory.SkippedUnsafeEntries == 1, "inventory after cleanup");
Console.WriteLine("PASS inventory reports retained app-owned files without following links");

var cancellationAudio = fixture.WriteRecording("recording-cancel-during-delete.wav", 37);
await using (var context = fixture.Database.CreateContext())
{
    context.Transcripts.Add(new Transcript
    {
        Text = "cancel during file cleanup", Status = TranscriptStatus.Completed,
        Date = now.AddDays(-40).UtcDateTime, AudioFilePath = cancellationAudio,
    });
    await context.SaveChangesAsync();
}
using (var cancellation = new CancellationTokenSource())
{
    fixture.Files.AfterDelete = cancellation.Cancel;
    var completedAfterCommit = await fixture.Coordinator.CleanupAsync(new(true, true, 30), now, cancellation.Token);
    Assert(completedAfterCommit.TranscriptsDeleted == 1 && completedAfterCommit.AudioFilesDeleted == 1,
        "post-commit cleanup ignores cancellation");
    Assert(!File.Exists(cancellationAudio), "post-commit audio removed");
}
fixture.Files.AfterDelete = null;
Console.WriteLine("PASS cancellation after row commit cannot orphan app-owned audio");

var completedAudio = fixture.WriteRecording("recording-completed.wav", 41);
var retentionStore = new MemoryHistoryStore();
using (var workflow = new TranscriptionWorkflow(
    new TestRecorder(completedAudio), new TestDevices(), new TestTranscriber(), retentionStore,
    audioRetention: new CompletedAudioRetention(() => false, fixture.Storage)))
{
    workflow.RefreshDevices();
    Assert((await workflow.StartRecordingAsync()).IsSuccess, "retention workflow start");
    var workflowResult = await workflow.StopAndTranscribeAsync(new());
    Assert(workflowResult.IsSuccess,
        $"retention workflow completion ({workflowResult.Failure?.Code}: {workflowResult.Failure?.Message})");
}
Assert(!File.Exists(completedAudio), "keep-audio off deletes completed owned recording");
Assert(retentionStore.Item is { AudioFilePath: null, Status: TranscriptStatus.Completed },
    "history clears deleted audio path before deletion");
Console.WriteLine("PASS keep-audio off clears history path then deletes completed app-owned audio");
Console.WriteLine("Storage application integration verification passed (7/7 scenarios).");

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"Assertion failed: {name}");
}

sealed class Fixture : IDisposable
{
    public Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-storage-app-tests-{Guid.NewGuid():N}");
        Paths = new TestPaths(Root);
        Directory.CreateDirectory(Paths.RecordingsDirectory);
        Files = new DiskPrivateFiles();
        Database = new ApplicationDb(Paths);
        Storage = new PortableStorageLifecycleService(Paths, Files);
        Coordinator = new TranscriptStorageCoordinator(Database, Storage);
    }

    public string Root { get; }
    public TestPaths Paths { get; }
    public DiskPrivateFiles Files { get; }
    public ApplicationDb Database { get; }
    public PortableStorageLifecycleService Storage { get; }
    public TranscriptStorageCoordinator Coordinator { get; }

    public string WriteRecording(string name, int bytes)
    {
        var path = Path.Combine(Paths.RecordingsDirectory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string WriteExternal(string name, int bytes)
    {
        var directory = Path.Combine(Root, "caller-files");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

sealed class TestPaths(string root) : IAppPaths
{
    public string DataDirectory => root;
    public string ConfigDirectory => root;
    public string CacheDirectory => root;
    public string StateDirectory => root;
    public string LogsDirectory => root;
    public string ModelsDirectory => root;
    public string RecordingsDirectory => Path.Combine(root, "recordings");
    public string RuntimeDirectory => root;
    public string TemporaryDirectory => root;
}

sealed class DiskPrivateFiles : IPrivateFileService
{
    public Action? AfterDelete { get; set; }
    public PlatformResult Delete(string path) { File.Delete(path); AfterDelete?.Invoke(); return PlatformResult.Success(); }
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents) => throw new NotSupportedException();
    public PlatformResult WriteAllTextAtomically(string path, string contents) => throw new NotSupportedException();
    public PlatformResult<byte[]?> ReadAllBytes(string path) => throw new NotSupportedException();
    public PlatformResult<string?> ReadAllText(string path) => throw new NotSupportedException();
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => throw new NotSupportedException();
}

sealed class TestRecorder(string path) : IAudioRecorder
{
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording { get; private set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(1);
    public PlatformResult Start(AudioRecordingOptions options) { IsRecording = true; return PlatformResult.Success(); }
    public PlatformResult<string> Stop() { IsRecording = false; return PlatformResult<string>.Success(path); }
    public void Dispose() { }
}

sealed class TestDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() =>
        PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([new("test", "Test microphone", true)]);
    public void Dispose() { }
}

sealed class TestTranscriber : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability => new(true, "Test backend");
    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath, string? language, CancellationToken cancellationToken = default) =>
        Task.FromResult(PortableTranscriptionResult.Success("stored words", "Test backend"));
}

sealed class MemoryHistoryStore : ITranscriptionHistoryStore
{
    public Transcript? Item { get; private set; }
    public Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Item);
    public Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        Item = transcript;
        return Task.CompletedTask;
    }
    public Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        Item = transcript;
        return Task.FromResult(true);
    }
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(true);
}
