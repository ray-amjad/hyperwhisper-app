namespace HyperWhisper.Data.Entities;

/// <summary>
/// TRANSCRIPT ENTITY
///
/// Represents a single transcription record in the history.
/// Matches the macOS Core Data Transcript entity for feature parity.
///
/// DATA FLOW:
/// 1. Created with Processing status when recording stops
/// 2. Updated to Completed/Failed after transcription finishes
/// 3. Can be retried (creates new transcript or updates existing)
/// 4. Deleted removes both the record and associated audio file
///
/// STORAGE:
/// - Transcript data: %LOCALAPPDATA%\HyperWhisper\history.json
/// - Audio files: %LOCALAPPDATA%\HyperWhisper\Audio\{Id}.wav
/// </summary>
public class Transcript
{
    // =========================================================================
    // IDENTITY
    // =========================================================================

    /// <summary>Unique identifier for the transcript.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    // =========================================================================
    // CONTENT
    // =========================================================================

    /// <summary>
    /// Final displayed text. This is either:
    /// - The post-processed text (if AI enhancement was used)
    /// - The raw transcription (if no post-processing)
    /// - An error message (if transcription failed)
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// Original raw transcription from Whisper before any post-processing.
    /// Null if transcription hasn't completed yet.
    /// </summary>
    public string? TranscribedText { get; set; }

    /// <summary>
    /// AI-enhanced version of the transcription after post-processing.
    /// Null if post-processing wasn't used or hasn't completed.
    /// </summary>
    public string? PostProcessedText { get; set; }

    // =========================================================================
    // METADATA
    // =========================================================================

    /// <summary>When the transcription was created (UTC).</summary>
    public DateTime Date { get; set; } = DateTime.UtcNow;

    /// <summary>Duration of the recording in seconds.</summary>
    public double Duration { get; set; }

    /// <summary>
    /// Absolute path to the audio file on disk.
    /// Format: %LOCALAPPDATA%\HyperWhisper\Audio\{Id}.wav
    /// </summary>
    public string? AudioFilePath { get; set; }

    /// <summary>
    /// Absolute path to the VAD-trimmed audio file on disk.
    /// Null if VAD trimming wasn't performed.
    /// </summary>
    public string? TrimmedAudioFilePath { get; set; }

    // =========================================================================
    // STATUS
    // =========================================================================

    /// <summary>Current status of the transcription.</summary>
    public TranscriptStatus Status { get; set; } = TranscriptStatus.Processing;

    /// <summary>Error message if transcription failed. Null otherwise.</summary>
    public string? FailedReason { get; set; }

    // =========================================================================
    // PROVIDER INFORMATION
    // =========================================================================

    /// <summary>
    /// Transcription provider used (e.g., "Whisper Base", "Whisper Large-v3-turbo").
    /// Shown as a badge in the detail view.
    /// </summary>
    public string? TranscriptionProvider { get; set; }

    /// <summary>
    /// Post-processing provider used (e.g., "OpenAI GPT-4", "Groq").
    /// Null if no post-processing was used. Shown as a badge in the detail view.
    /// </summary>
    public string? PostProcessingProvider { get; set; }

    /// <summary>Name of the mode used for transcription (e.g., "Hyper", "Message").</summary>
    public string? Mode { get; set; }

    // =========================================================================
    // RELATIONSHIPS
    // =========================================================================

    /// <summary>Foreign key to the Mode entity used for this transcription.</summary>
    public Guid? ModeId { get; set; }

    /// <summary>Foreign key to the RecordingSession.</summary>
    public Guid? RecordingSessionId { get; set; }

    /// <summary>Navigation property to the RecordingSession.</summary>
    public RecordingSession? RecordingSession { get; set; }

    // =========================================================================
    // RETRY SUPPORT
    // =========================================================================

    /// <summary>Number of times this transcript has been retried.</summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>When the last retry attempt occurred. Null if never retried.</summary>
    public DateTime? LastRetryDate { get; set; }
}

/// <summary>
/// TRANSCRIPT STATUS
///
/// Represents the lifecycle state of a transcription:
/// - Processing: Recording stopped, transcription in progress
/// - Completed: Transcription finished successfully
/// - Failed: Transcription failed (audio file kept for retry)
/// </summary>
public enum TranscriptStatus
{
    /// <summary>Transcription is in progress. Shows spinner in UI.</summary>
    Processing,

    /// <summary>Transcription completed successfully.</summary>
    Completed,

    /// <summary>Transcription failed. Audio file retained for retry.</summary>
    Failed
}
