using HyperWhisper.Data.Entities;
using HyperWhisper.Storage;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed record TranscriptStorageCleanupResult(
    StorageLifecycleStatus Status,
    int TranscriptsDeleted,
    int AudioFilesDeleted,
    long AudioBytesDeleted,
    int ProtectedExternalFiles,
    int AudioDeleteFailures,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Coordinates the Windows-compatible transcript-age cleanup with the portable
/// app-owned-file boundary. Database rows commit atomically before audio cleanup;
/// external originals and linked paths are retained. A failed audio delete is
/// reported without exposing its path; the inventory continues to report it.
/// </summary>
public sealed class TranscriptStorageCoordinator(
    ApplicationDb database,
    PortableStorageLifecycleService storage)
{
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly PortableStorageLifecycleService _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<RecordingInventoryResult> InventoryAsync(CancellationToken cancellationToken = default) =>
        _storage.InventoryAsync(cancellationToken);

    public async Task<TranscriptStorageCleanupResult> CleanupAsync(
        StorageRetentionPolicy policy,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValid) return Empty(StorageLifecycleStatus.InvalidPolicy, utcNow);
        if (!policy.AutoDeleteEnabled) return Empty(StorageLifecycleStatus.Disabled, utcNow);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var cutoff = utcNow.ToUniversalTime().AddDays(-policy.AutoDeleteDays).UtcDateTime;
                List<Transcript> transcripts;
                await using (var context = _database.CreateContext())
                await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    transcripts = await context.Transcripts
                        .Where(item => item.Date < cutoff)
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                    if (transcripts.Count != 0)
                    {
                        context.Transcripts.RemoveRange(transcripts);
                        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                var paths = transcripts
                    .SelectMany(item => new[] { item.AudioFilePath, item.TrimmedAudioFilePath })
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var protectedFiles = paths.Count(path => !_storage.IsAppOwnedRecording(path));
                var deleted = 0;
                long bytes = 0;
                var failures = 0;
                foreach (var path in paths.Where(_storage.IsAppOwnedRecording))
                {
                    // Once the row transaction commits, finish this bounded cleanup
                    // even if shutdown is requested so no new orphan is introduced.
                    var result = await _storage.EnforceKeepAudioAsync(
                        path, keepAudio: false, CancellationToken.None).ConfigureAwait(false);
                    deleted += result.FilesDeleted;
                    bytes = checked(bytes + result.BytesDeleted);
                    failures += result.FailedOrUnsafeFiles;
                }

                var status = failures == 0 ? StorageLifecycleStatus.Completed : StorageLifecycleStatus.PartialFailure;
                return new(status, transcripts.Count, deleted, bytes, protectedFiles, failures, utcNow.ToUniversalTime());
            }
            catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException)
            {
                return Empty(StorageLifecycleStatus.PersistenceFailure, utcNow);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TranscriptStorageCleanupResult Empty(StorageLifecycleStatus status, DateTimeOffset now) =>
        new(status, 0, 0, 0, 0, 0, now.ToUniversalTime());
}
