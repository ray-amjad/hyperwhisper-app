// TRANSCRIPTION PROVIDER INTERFACE
// Defines the contract for both local (WhisperNet) and cloud transcription providers.
// This abstraction enables seamless switching between providers based on mode settings.
//
// IMPLEMENTATIONS:
// - TranscriptionService: Local GPU-accelerated transcription via WhisperNet
// - OpenAIWhisperService: Cloud transcription via OpenAI Whisper API
//
// DESIGN NOTES:
// - Async-first design for network operations
// - Vocabulary support for custom terms (improves accuracy)
// - IsAvailable check for API key validation before transcription

namespace HyperWhisper.Services;

/// <summary>
/// Common interface for transcription providers.
/// Implemented by both local (WhisperNet) and cloud (OpenAI) providers.
/// </summary>
public interface ITranscriptionProvider
{
    /// <summary>
    /// Transcribes audio from a file.
    /// </summary>
    /// <param name="audioPath">Absolute path to the audio file (WAV, MP3, etc.).</param>
    /// <param name="language">ISO 639-1 language code (e.g., "en", "ja"). Null for auto-detect.</param>
    /// <param name="vocabulary">Custom vocabulary terms for better accuracy (optional).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Transcribed text.</returns>
    /// <exception cref="TranscriptionException">Thrown when transcription fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when transcription is cancelled.</exception>
    Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the provider is ready to transcribe.
    /// For local: model is loaded.
    /// For cloud: API key is configured.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Display name of the provider (e.g., "Whisper Base", "OpenAI Whisper").
    /// Used in history records and status messages.
    /// </summary>
    string Name { get; }
}
