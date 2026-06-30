// TRANSCRIPTION RESULT
// Immutable record capturing the complete result of a transcription operation.
// Used by TranscriptionOrchestrator to return all relevant details for history storage.

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Result of a transcription operation with full provider and processing details.
/// Immutable record type for thread safety and clarity.
/// </summary>
/// <param name="RawText">Raw text from the transcription provider (before post-processing).</param>
/// <param name="FinalText">Final text after all processing (post-processing + vocabulary replacements).</param>
/// <param name="TranscriptionProvider">Display name of the transcription provider (e.g., "OpenAI Whisper-1").</param>
/// <param name="PostProcessingProvider">Display name of post-processing provider if used.</param>
/// <param name="PostProcessedText">Post-processed text (null if no post-processing was applied).</param>
/// <param name="Diagnostics">Provider diagnostics captured during the attempt.</param>
public record TranscriptionResult(
    string RawText,
    string FinalText,
    string TranscriptionProvider,
    string? PostProcessingProvider = null,
    string? PostProcessedText = null,
    TranscriptionProviderDiagnostics? Diagnostics = null
)
{
    /// <summary>
    /// Whether post-processing was applied to this transcription.
    /// </summary>
    public bool WasPostProcessed => !string.IsNullOrEmpty(PostProcessedText);
}
