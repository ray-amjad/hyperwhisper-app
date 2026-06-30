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

            // Apply any pending migrations automatically
            LoggingService.Debug("DatabaseInitializer: Applying migrations...");
            await context.Database.MigrateAsync();
            LoggingService.Info("DatabaseInitializer: Migrations applied successfully");

            // Ensure default modes exist
            await EnsureDefaultModesAsync(context);

            // Normalize legacy cloud provider/tier/model identifiers after
            // schema migration/default seeding so existing rows match the
            // current Mode Editor and macOS startup migrations.
            ModeService.NormalizeLegacyCloudModeValues();

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
