using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace HyperWhisper.PortableApplication.Persistence;

/// <summary>
/// Clears the row EF Core 9's SQLite migrator leaves in <c>__EFMigrationsLock</c>
/// when the process dies inside <c>MigrateAsync</c> (#996). EF deletes the row
/// only on a clean finish, and the next <c>MigrateAsync</c> polls for it with no
/// timeout, so one kill hangs every later launch on "Preparing local database…".
/// </summary>
/// <remarks>
/// Call this ONLY from a process that owns the per-profile single-instance guard
/// and before it migrates (Linux: <c>LinuxSingleInstanceCoordinator</c>, acquired
/// in <c>App.OnFrameworkInitializationCompleted</c>; Windows:
/// <c>SingleInstanceGuard</c>, acquired in <c>App.OnStartup</c>). Under that guard
/// no other process can be migrating this file, so any row present is stale.
/// An age threshold would not do: a crash and an immediate relaunch would still
/// meet a fresh row and wait on it forever.
/// </remarks>
public static class StaleMigrationLock
{
    // SqliteHistoryRepository.LockTableName in EF Core 9. The regression test
    // seeds the row EF itself would write, so a rename upstream fails the test.
    private const string LockTableName = "__EFMigrationsLock";

    /// <returns>The number of stale lock rows removed.</returns>
    public static async Task<int> ClearAsync(DatabaseFacade database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{LockTableName}'";
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                return 0;
            command.CommandText = $"DELETE FROM \"{LockTableName}\"";
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }
}
