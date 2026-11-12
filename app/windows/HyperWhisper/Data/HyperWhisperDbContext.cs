using System.Data.Common;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Data;

/// <summary>
/// HYPERWHISPER DATABASE CONTEXT
///
/// Entity Framework Core DbContext for managing all persistent data.
/// Provides CoreData-like functionality with automatic schema migrations.
///
/// DATABASE LOCATION: %LOCALAPPDATA%\HyperWhisper\hyperwhisper.db
/// </summary>
public class HyperWhisperDbContext : DbContext
{
    // =========================================================================
    // ENTITY SETS
    // =========================================================================

    public DbSet<Transcript> Transcripts { get; set; } = null!;
    public DbSet<Mode> Modes { get; set; } = null!;
    public DbSet<RecordingSession> RecordingSessions { get; set; } = null!;
    public DbSet<VocabularyItem> VocabularyItems { get; set; } = null!;
    public DbSet<UsageTracking> UsageTracking { get; set; } = null!;

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    private readonly string? _dbPath;

    public HyperWhisperDbContext()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("HYPERWHISPER_WINDOWS_APPDATA_ROOT");
        var hyperWhisperDir = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperWhisper")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideRoot));
        Directory.CreateDirectory(hyperWhisperDir);
        _dbPath = Path.Combine(hyperWhisperDir, "hyperwhisper.db");
    }

    public HyperWhisperDbContext(IAppPaths paths)
        : this(Path.Combine(
            (paths ?? throw new ArgumentNullException(nameof(paths))).StateDirectory,
            "hyperwhisper.db"))
    {
    }

    public HyperWhisperDbContext(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        _dbPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public HyperWhisperDbContext(DbContextOptions<HyperWhisperDbContext> options)
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured)
            options.UseSqlite($"Data Source={_dbPath}");

        // Every context, the options constructor's too, so no connection opened
        // through EF misses the busy wait (#2782).
        options.AddInterceptors(BusyTimeoutInterceptor.Instance);
    }

    /// <summary>
    /// How long SQLite itself waits for another connection's lock (#2782). The same
    /// 30 s as Microsoft.Data.Sqlite's own retry (its default command timeout), so the
    /// two do not add up: that retry's clock starts before the first step and counts
    /// the time this wait spends inside it.
    /// </summary>
    public const int BusyTimeoutMilliseconds = 30_000;

    /// <summary>
    /// Sets SQLite's native busy wait on every connection the context opens (#2782).
    /// The database is in rollback-journal mode, and EF runs a one-row UPDATE or DELETE
    /// in autocommit, committing at the statement reset. Microsoft.Data.Sqlite retries
    /// a busy step but not a busy reset, and has no connection-string keyword for
    /// busy_timeout ("Default Timeout" is only its own retry), so without this a reader
    /// on another connection failed the write at once with "database is locked".
    /// </summary>
    private sealed class BusyTimeoutInterceptor : DbConnectionInterceptor
    {
        public static readonly BusyTimeoutInterceptor Instance = new();

        private static readonly string Pragma = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (connection is not SqliteConnection) return;
            using var command = connection.CreateCommand();
            command.CommandText = Pragma;
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (connection is not SqliteConnection) return;
            await using var command = connection.CreateCommand();
            command.CommandText = Pragma;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // =====================================================================
        // TRANSCRIPT CONFIGURATION
        // =====================================================================

        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Indexes for common queries
            entity.HasIndex(e => e.Date);
            entity.HasIndex(e => e.Status);

            // Relationship to RecordingSession (one-to-one)
            entity.HasOne(e => e.RecordingSession)
                .WithOne(r => r.Transcript)
                .HasForeignKey<Transcript>(e => e.RecordingSessionId)
                .OnDelete(DeleteBehavior.SetNull);  // Keep transcript if session is deleted
        });

        // =====================================================================
        // MODE CONFIGURATION
        // =====================================================================

        modelBuilder.Entity<Mode>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Indexes for common queries
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.IsDefault);
        });

        // =====================================================================
        // RECORDING SESSION CONFIGURATION
        // =====================================================================

        modelBuilder.Entity<RecordingSession>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index for date-based queries
            entity.HasIndex(e => e.StartTime);
        });

        // =====================================================================
        // VOCABULARY ITEM CONFIGURATION
        // =====================================================================

        modelBuilder.Entity<VocabularyItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index for word lookup
            entity.HasIndex(e => e.Word);
        });

        // =====================================================================
        // USAGE TRACKING CONFIGURATION
        // =====================================================================

        modelBuilder.Entity<UsageTracking>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
    }
}
