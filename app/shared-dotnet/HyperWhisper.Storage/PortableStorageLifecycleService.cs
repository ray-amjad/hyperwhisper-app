using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Storage;

public sealed record StorageRetentionPolicy(bool KeepAudio, bool AutoDeleteEnabled, int AutoDeleteDays)
{
    public const int MinimumAutoDeleteDays = 1;
    public const int MaximumAutoDeleteDays = 365;

    public bool IsValid => AutoDeleteDays is >= MinimumAutoDeleteDays and <= MaximumAutoDeleteDays;
}

public enum StorageLifecycleStatus
{
    Completed,
    Disabled,
    InvalidPolicy,
    UnsafeRecordingsRoot,
    PersistenceFailure,
    PartialFailure
}

public sealed record RecordingInventoryResult(
    StorageLifecycleStatus Status,
    int FileCount,
    long TotalBytes,
    int SkippedUnsafeEntries);

public sealed record StorageCleanupResult(
    StorageLifecycleStatus Status,
    int FilesMatched,
    int FilesDeleted,
    long BytesDeleted,
    int FailedOrUnsafeFiles);

/// <summary>
/// Inventories and deletes audio only inside the app-owned recordings directory.
/// Symbolic links are never followed. Results deliberately omit paths and exception
/// messages so they are safe to display or include in privacy-preserving diagnostics.
/// </summary>
public sealed class PortableStorageLifecycleService
{
    private readonly string _recordingsRoot;
    private readonly IPrivateFileService _privateFiles;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public PortableStorageLifecycleService(IAppPaths paths, IPrivateFileService privateFiles)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _privateFiles = privateFiles ?? throw new ArgumentNullException(nameof(privateFiles));
        _recordingsRoot = NormalizeRoot(paths.RecordingsDirectory);
    }

    public async Task<RecordingInventoryResult> InventoryAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scan = Scan(DateTimeOffset.MaxValue, matchAll: true, cancellationToken);
            return scan.RootSafe
                ? new(StorageLifecycleStatus.Completed, scan.Files.Count, scan.Files.Sum(file => file.Length), scan.SkippedUnsafe)
                : new(StorageLifecycleStatus.UnsafeRecordingsRoot, 0, 0, 0);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Returns true only for an existing regular file reached without crossing a link.</summary>
    public bool IsAppOwnedRecording(string? path) =>
        !string.IsNullOrWhiteSpace(path) && IsSafeRoot() && InspectCandidate(path) is not null;

    /// <summary>Runs the configured age cleanup immediately, matching the Windows Delete Now behavior.</summary>
    public Task<StorageCleanupResult> DeleteNowAsync(
        StorageRetentionPolicy policy,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        RunRetentionCleanupAsync(policy, utcNow, cancellationToken);

    public async Task<StorageCleanupResult> RunRetentionCleanupAsync(
        StorageRetentionPolicy policy,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValid)
            return Empty(StorageLifecycleStatus.InvalidPolicy);
        if (!policy.AutoDeleteEnabled)
            return Empty(StorageLifecycleStatus.Disabled);

        var cutoff = utcNow.ToUniversalTime().AddDays(-policy.AutoDeleteDays);
        return await DeleteMatchingAsync(cutoff, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enforces the post-transcription keep-audio preference for a single path.
    /// Caller-owned imports outside the recordings directory are always retained.
    /// </summary>
    public async Task<StorageCleanupResult> EnforceKeepAudioAsync(
        string? audioPath,
        bool keepAudio,
        CancellationToken cancellationToken = default)
    {
        if (keepAudio || string.IsNullOrWhiteSpace(audioPath))
            return Empty(StorageLifecycleStatus.Disabled);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsSafeRoot())
                return Empty(StorageLifecycleStatus.UnsafeRecordingsRoot);

            var candidate = InspectCandidate(audioPath);
            if (candidate is null)
                return new(StorageLifecycleStatus.PartialFailure, 0, 0, 0, 1);

            return DeleteFiles([candidate], skippedUnsafe: 0, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<StorageCleanupResult> DeleteMatchingAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scan = Scan(cutoff, matchAll: false, cancellationToken);
            if (!scan.RootSafe)
                return Empty(StorageLifecycleStatus.UnsafeRecordingsRoot);
            return DeleteFiles(scan.Files, scan.SkippedUnsafe, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private StorageCleanupResult DeleteFiles(
        IReadOnlyList<RecordingFile> files,
        int skippedUnsafe,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        long bytesDeleted = 0;
        var failed = skippedUnsafe;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Inspect again immediately before deletion. File.Delete removes a replaced
            // symlink itself on supported platforms, but rejecting it is clearer and safer.
            var current = InspectCandidate(file.Path);
            if (current is null)
            {
                failed++;
                continue;
            }

            var result = _privateFiles.Delete(current.Path);
            if (result.IsSuccess)
            {
                deleted++;
                bytesDeleted = checked(bytesDeleted + current.Length);
            }
            else
            {
                failed++;
            }
        }

        var status = failed == 0 ? StorageLifecycleStatus.Completed : StorageLifecycleStatus.PartialFailure;
        return new(status, files.Count, deleted, bytesDeleted, failed);
    }

    private ScanResult Scan(DateTimeOffset cutoff, bool matchAll, CancellationToken cancellationToken)
    {
        if (!IsSafeRoot()) return new(false, [], 0);
        if (!Directory.Exists(_recordingsRoot)) return new(true, [], 0);

        var files = new List<RecordingFile>();
        var skipped = 0;
        var pending = new Stack<string>();
        pending.Push(_recordingsRoot);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                skipped++;
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception exception) when (IsExpectedFileFailure(exception))
                    {
                        skipped++;
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped++;
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    var candidate = InspectCandidate(entry);
                    if (candidate is null)
                    {
                        skipped++;
                        continue;
                    }

                    if (matchAll || candidate.LastWriteTimeUtc < cutoff)
                        files.Add(candidate);
                }
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                skipped++;
            }
        }

        return new(true, files, skipped);
    }

    private RecordingFile? InspectCandidate(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!IsContained(fullPath)) return null;
            if (HasLinkedPathComponent(fullPath)) return null;
            var info = new FileInfo(fullPath);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return null;
            return new(fullPath, info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception) || exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private bool IsSafeRoot()
    {
        try
        {
            if (!Path.IsPathFullyQualified(_recordingsRoot)) return false;
            if (!Directory.Exists(_recordingsRoot)) return true;
            return (File.GetAttributes(_recordingsRoot) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return false;
        }
    }

    private bool IsContained(string path) =>
        path.StartsWith(_recordingsRoot + Path.DirectorySeparatorChar, PathComparison);

    private bool HasLinkedPathComponent(string path)
    {
        var relative = Path.GetRelativePath(_recordingsRoot, path);
        var current = _recordingsRoot;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("The recordings directory must be absolute.", nameof(root));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static StorageCleanupResult Empty(StorageLifecycleStatus status) => new(status, 0, 0, 0, 0);

    private sealed record RecordingFile(string Path, long Length, DateTimeOffset LastWriteTimeUtc);
    private sealed record ScanResult(bool RootSafe, IReadOnlyList<RecordingFile> Files, int SkippedUnsafe);
}
