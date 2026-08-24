using HyperWhisper.Platform.Abstractions;
using HyperWhisper.Storage;

var passed = 0;
await VerifyAsync("inventory counts nested regular recordings", async fixture =>
{
    fixture.WriteRecording("first.wav", 7, ageDays: 5);
    fixture.WriteRecording("nested/second.m4a", 11, ageDays: 1);
    var result = await fixture.Service.InventoryAsync();
    Assert(result == new RecordingInventoryResult(StorageLifecycleStatus.Completed, 2, 18, 0));
});

await VerifyAsync("disabled retention is non-destructive", async fixture =>
{
    var path = fixture.WriteRecording("old.wav", 7, ageDays: 30);
    var result = await fixture.Service.RunRetentionCleanupAsync(new(true, false, 7), fixture.Now);
    Assert(result.Status == StorageLifecycleStatus.Disabled && File.Exists(path));
});

await VerifyAsync("retention deletes only files older than cutoff", async fixture =>
{
    var old = fixture.WriteRecording("old.wav", 7, ageDays: 8);
    var cutoff = fixture.WriteRecording("cutoff.wav", 9, ageDays: 7);
    var recent = fixture.WriteRecording("recent.wav", 11, ageDays: 6);
    var result = await fixture.Service.RunRetentionCleanupAsync(new(true, true, 7), fixture.Now);
    Assert(result == new StorageCleanupResult(StorageLifecycleStatus.Completed, 1, 1, 7, 0));
    Assert(!File.Exists(old) && File.Exists(cutoff) && File.Exists(recent));
});

await VerifyAsync("delete now shares configured retention semantics", async fixture =>
{
    fixture.WriteRecording("old.wav", 3, ageDays: 31);
    fixture.WriteRecording("recent.wav", 5, ageDays: 29);
    var result = await fixture.Service.DeleteNowAsync(new(true, true, 30), fixture.Now);
    Assert(result.FilesDeleted == 1 && result.BytesDeleted == 3);
});

await VerifyAsync("invalid day settings never delete", async fixture =>
{
    var path = fixture.WriteRecording("old.wav", 4, ageDays: 500);
    var low = await fixture.Service.RunRetentionCleanupAsync(new(true, true, 0), fixture.Now);
    var high = await fixture.Service.RunRetentionCleanupAsync(new(true, true, 366), fixture.Now);
    Assert(low.Status == StorageLifecycleStatus.InvalidPolicy);
    Assert(high.Status == StorageLifecycleStatus.InvalidPolicy && File.Exists(path));
});

await VerifyAsync("keep audio retains app recording", async fixture =>
{
    var path = fixture.WriteRecording("kept.wav", 4, ageDays: 1);
    var result = await fixture.Service.EnforceKeepAudioAsync(path, keepAudio: true);
    Assert(result.Status == StorageLifecycleStatus.Disabled && File.Exists(path));
});

await VerifyAsync("disabled keep audio removes app recording", async fixture =>
{
    var path = fixture.WriteRecording("discarded.wav", 13, ageDays: 1);
    var result = await fixture.Service.EnforceKeepAudioAsync(path, keepAudio: false);
    Assert(result == new StorageCleanupResult(StorageLifecycleStatus.Completed, 1, 1, 13, 0));
    Assert(!File.Exists(path));
});

await VerifyAsync("caller original outside app root is never deleted", async fixture =>
{
    var original = fixture.WriteOriginal("caller-original.wav", 17);
    var result = await fixture.Service.EnforceKeepAudioAsync(original, keepAudio: false);
    Assert(result.Status == StorageLifecycleStatus.PartialFailure && result.FailedOrUnsafeFiles == 1);
    Assert(File.Exists(original));
});

await VerifyAsync("file symlink is skipped and target survives", async fixture =>
{
    var target = fixture.WriteOriginal("external.wav", 19);
    var link = Path.Combine(fixture.RecordingsRoot, "linked.wav");
    File.CreateSymbolicLink(link, target);
    var inventory = await fixture.Service.InventoryAsync();
    var cleanup = await fixture.Service.DeleteNowAsync(new(true, true, 1), fixture.Now);
    Assert(inventory.FileCount == 0 && inventory.SkippedUnsafeEntries == 1);
    Assert(cleanup.FilesDeleted == 0 && cleanup.FailedOrUnsafeFiles == 1);
    Assert(File.Exists(target) && File.Exists(link));
});

await VerifyAsync("directory symlink is not traversed", async fixture =>
{
    var externalDirectory = Path.Combine(fixture.Root, "external-dir");
    Directory.CreateDirectory(externalDirectory);
    var target = Path.Combine(externalDirectory, "external.wav");
    File.WriteAllBytes(target, new byte[23]);
    File.SetLastWriteTimeUtc(target, fixture.Now.AddDays(-100).UtcDateTime);
    Directory.CreateSymbolicLink(Path.Combine(fixture.RecordingsRoot, "linked-dir"), externalDirectory);
    var result = await fixture.Service.DeleteNowAsync(new(true, true, 1), fixture.Now);
    Assert(result.FilesDeleted == 0 && result.FailedOrUnsafeFiles == 1 && File.Exists(target));
});

await VerifyAsync("single-file enforcement rejects linked parent", async fixture =>
{
    var externalDirectory = Path.Combine(fixture.Root, "linked-parent-target");
    Directory.CreateDirectory(externalDirectory);
    var target = Path.Combine(externalDirectory, "external.wav");
    File.WriteAllBytes(target, new byte[27]);
    var linkedDirectory = Path.Combine(fixture.RecordingsRoot, "linked-parent");
    Directory.CreateSymbolicLink(linkedDirectory, externalDirectory);
    var result = await fixture.Service.EnforceKeepAudioAsync(
        Path.Combine(linkedDirectory, "external.wav"), keepAudio: false);
    Assert(result.Status == StorageLifecycleStatus.PartialFailure && File.Exists(target));
});

await VerifyAsync("symlink recordings root is rejected", async fixture =>
{
    var externalDirectory = Path.Combine(fixture.Root, "external-root");
    Directory.CreateDirectory(externalDirectory);
    var target = Path.Combine(externalDirectory, "external.wav");
    File.WriteAllBytes(target, new byte[29]);
    Directory.Delete(fixture.RecordingsRoot);
    Directory.CreateSymbolicLink(fixture.RecordingsRoot, externalDirectory);
    var result = await fixture.Service.InventoryAsync();
    Assert(result.Status == StorageLifecycleStatus.UnsafeRecordingsRoot && File.Exists(target));
});

await VerifyAsync("delete failures are stable and privacy safe", async fixture =>
{
    var path = fixture.WriteRecording("private-name.wav", 31, ageDays: 10);
    fixture.Files.FailDeletes = true;
    var result = await fixture.Service.DeleteNowAsync(new(true, true, 1), fixture.Now);
    Assert(result == new StorageCleanupResult(StorageLifecycleStatus.PartialFailure, 1, 0, 0, 1));
    Assert(!result.ToString().Contains(path, StringComparison.Ordinal));
});

await VerifyAsync("missing recordings root has empty inventory", async fixture =>
{
    Directory.Delete(fixture.RecordingsRoot);
    var result = await fixture.Service.InventoryAsync();
    Assert(result == new RecordingInventoryResult(StorageLifecycleStatus.Completed, 0, 0, 0));
});

Console.WriteLine($"Storage lifecycle verification passed ({passed}/14 scenarios). Ownership bounds, retention, keep-audio, and symlink safety are enforced.");

async Task VerifyAsync(string name, Func<Fixture, Task> test)
{
    using var fixture = new Fixture();
    try
    {
        await test(fixture);
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        throw;
    }
}

static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("Assertion failed.");
}

sealed class Fixture : IDisposable
{
    public Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-storage-tests-{Guid.NewGuid():N}");
        RecordingsRoot = Path.Combine(Root, "recordings");
        Directory.CreateDirectory(RecordingsRoot);
        Files = new TestPrivateFiles();
        Service = new PortableStorageLifecycleService(new TestPaths(Root), Files);
    }

    public string Root { get; }
    public string RecordingsRoot { get; }
    public DateTimeOffset Now { get; } = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    public TestPrivateFiles Files { get; }
    public PortableStorageLifecycleService Service { get; }

    public string WriteRecording(string relativePath, int bytes, int ageDays)
    {
        var path = Path.Combine(RecordingsRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays).UtcDateTime);
        return path;
    }

    public string WriteOriginal(string name, int bytes)
    {
        var directory = Path.Combine(Root, "originals");
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

sealed class TestPrivateFiles : IPrivateFileService
{
    public bool FailDeletes { get; set; }
    public PlatformResult Delete(string path)
    {
        if (FailDeletes) return PlatformResult.Failure("test_delete_failed", "Deletion failed.");
        File.Delete(path);
        return PlatformResult.Success();
    }

    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents) => throw new NotSupportedException();
    public PlatformResult WriteAllTextAtomically(string path, string contents) => throw new NotSupportedException();
    public PlatformResult<byte[]?> ReadAllBytes(string path) => throw new NotSupportedException();
    public PlatformResult<string?> ReadAllText(string path) => throw new NotSupportedException();
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => throw new NotSupportedException();
}
