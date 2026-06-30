namespace HyperWhisper.Data.Entities;

/// <summary>
/// USAGE TRACKING ENTITY
///
/// Tracks user license status and usage metrics.
/// Matches macOS Core Data UsageTracking entity for cross-platform consistency.
/// </summary>
public class UsageTracking
{
    // =========================================================================
    // IDENTITY
    // =========================================================================

    /// <summary>Unique identifier for the usage tracking record.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    // =========================================================================
    // USAGE METRICS
    // =========================================================================

    /// <summary>Total transcription seconds used today (resets daily).</summary>
    public long DailyTranscriptionSeconds { get; set; }

    /// <summary>Total number of models downloaded.</summary>
    public int TotalModelsDownloaded { get; set; }

    // =========================================================================
    // DATES
    // =========================================================================

    /// <summary>When the user first used the app.</summary>
    public DateTime FirstUsageDate { get; set; } = DateTime.UtcNow;

    /// <summary>Last time daily counters were reset.</summary>
    public DateTime LastResetDate { get; set; } = DateTime.UtcNow;

    /// <summary>Last time license was validated with the server.</summary>
    public DateTime? LastValidationDate { get; set; }

    /// <summary>When the license was activated.</summary>
    public DateTime? LicenseActivatedDate { get; set; }

    // =========================================================================
    // LICENSE
    // =========================================================================

    /// <summary>License status: "trial", "active", "expired".</summary>
    public string LicenseStatus { get; set; } = "trial";

    /// <summary>Customer email associated with the license.</summary>
    public string? CustomerEmail { get; set; }
}
