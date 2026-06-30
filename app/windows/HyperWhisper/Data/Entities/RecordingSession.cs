namespace HyperWhisper.Data.Entities;

/// <summary>
/// RECORDING SESSION ENTITY
///
/// Tracks metadata about an audio recording session.
/// Matches macOS Core Data RecordingSession entity for cross-platform consistency.
/// </summary>
public class RecordingSession
{
    // =========================================================================
    // IDENTITY
    // =========================================================================

    /// <summary>Unique identifier for the recording session.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    // =========================================================================
    // TIMING
    // =========================================================================

    /// <summary>When the recording started (UTC).</summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>When the recording ended (UTC). Null if still recording.</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>Duration of the recording in seconds.</summary>
    public double DurationInSeconds { get; set; }

    // =========================================================================
    // DEVICE INFORMATION
    // =========================================================================

    /// <summary>Audio device identifier.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Human-readable device name.</summary>
    public string? DeviceName { get; set; }

    // =========================================================================
    // AUDIO FORMAT
    // =========================================================================

    /// <summary>Sample rate in Hz (e.g., 16000, 44100).</summary>
    public double SampleRate { get; set; }

    /// <summary>Number of audio channels (1=mono, 2=stereo).</summary>
    public int ChannelCount { get; set; }

    /// <summary>Audio format (e.g., "PCM", "Float32").</summary>
    public string AudioFormat { get; set; } = "PCM";

    // =========================================================================
    // STATUS
    // =========================================================================

    /// <summary>Current status: "recording", "processing", "completed", "failed".</summary>
    public string Status { get; set; } = "recording";

    /// <summary>Path to the audio file on disk.</summary>
    public string? AudioFilePath { get; set; }

    /// <summary>Error message if recording failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts.</summary>
    public int RetryCount { get; set; }

    // =========================================================================
    // RELATIONSHIPS
    // =========================================================================

    /// <summary>Navigation property to the associated transcript.</summary>
    public Transcript? Transcript { get; set; }
}
