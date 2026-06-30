using System.IO;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;

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

    private readonly string _dbPath;

    public HyperWhisperDbContext()
    {
        var hyperWhisperDir = AppPaths.AppDataRoot;
        Directory.CreateDirectory(hyperWhisperDir);
        _dbPath = Path.Combine(hyperWhisperDir, "hyperwhisper.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={_dbPath}");
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
