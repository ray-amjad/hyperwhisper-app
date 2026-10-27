using Microsoft.EntityFrameworkCore;
using HyperWhisper.Services;

namespace HyperWhisper.Data;

/// <summary>
/// DATABASE INITIALIZER
///
/// Handles database initialization and auto-migration at app startup.
/// Provides CoreData-like automatic migration behavior.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Initializes the database with automatic migration.
    /// Call this at app startup before any database operations.
    /// </summary>
    public static async Task InitializeAsync()
    {
        try
        {
            LoggingService.Info("DatabaseInitializer: Starting database initialization...");

            using var context = new HyperWhisperDbContext();

            // A kill inside a previous MigrateAsync leaves EF's lock row, and
            // MigrateAsync would then wait on it forever (#996). App.OnStartup
            // holds SingleInstanceGuard by now, so any row here is stale.
            var staleLocks = await PortableApplication.Persistence.StaleMigrationLock.ClearAsync(context.Database);
            if (staleLocks > 0)
                LoggingService.Warn($"DatabaseInitializer: Cleared {staleLocks} stale EF migration lock row(s)");

            // MigrateAsync costs about 230 ms on the UI thread even with nothing
            // to apply (#979), so ask first. A new database lists every migration.
            var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
            {
                LoggingService.Debug($"DatabaseInitializer: Applying {pending.Count} migration(s)...");
                await context.Database.MigrateAsync();
                LoggingService.Info("DatabaseInitializer: Migrations applied successfully");
            }
            else
            {
                LoggingService.Debug("DatabaseInitializer: No pending migrations");
            }

            // Ensure default modes exist
            await EnsureDefaultModesAsync(context);

            // Legacy cloud tier/model values are normalized by the ModeService
            // constructor, before anything reads a mode (#979).

            LoggingService.Info("DatabaseInitializer: Database initialization complete");
        }
        catch (Exception ex)
        {
            LoggingService.Error("DatabaseInitializer: Failed to initialize database", ex);
            throw;
        }
    }

    /// <summary>
    /// Ensures default modes exist in the database.
    /// If no modes exist, creates the default set.
    /// </summary>
    private static async Task EnsureDefaultModesAsync(HyperWhisperDbContext context)
    {
        var modesExist = await context.Modes.AnyAsync();
        if (modesExist)
        {
            LoggingService.Debug("DatabaseInitializer: Modes already exist, skipping default creation");
            return;
        }

        LoggingService.Info("DatabaseInitializer: Creating default modes...");
        var defaultModes = ModeDefaults.GetDefaultModes();
        context.Modes.AddRange(defaultModes);
        await context.SaveChangesAsync();

        LoggingService.Info($"DatabaseInitializer: Created {defaultModes.Count} default modes");
    }
}
