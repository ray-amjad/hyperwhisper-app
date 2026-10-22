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
/// Everything that belongs to ONE transcription call. The model id and the
/// custom prompt live here, not on the provider, because the providers are
/// cached singletons shared by every caller of the process-wide orchestrator
/// (a GUI dictation and a Local API request can overlap). State written onto
/// a shared instance between "resolve the provider" and "send" leaks from one
/// call into another (issue #753).
/// </summary>
/// <param name="AudioPath">Absolute path to the audio file.</param>
/// <param name="Language">ISO 639-1 language code, or null for auto-detect.</param>
/// <param name="Vocabulary">Custom vocabulary terms, or null.</param>
/// <param name="ModelId">
/// The model this call must run. An empty id means "the provider's catalog
/// default"; each API-key provider applies its own alias resolution to it.
/// </param>
/// <param name="CustomPrompt">Extra prompt text. Only Gemini reads it.</param>
public sealed record TranscriptionRequest(
    string AudioPath,
    string? Language,
    IReadOnlyList<string>? Vocabulary,
    string ModelId,
    string? CustomPrompt);

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
    /// Transcribes one request, using the model id and custom prompt it carries.
    /// The API-key (BYOK) providers implement this and honour
    /// <see cref="TranscriptionRequest.ModelId"/>. The default, used by the
    /// local engines and the HW-Cloud-routed services, forwards to the
    /// 4-argument overload: those providers take their model from elsewhere
    /// (a loaded model, or a per-request instance) and have no custom prompt.
    /// </summary>
    Task<string> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => TranscribeAsync(request.AudioPath, request.Language, request.Vocabulary, cancellationToken);

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

/// <summary>
/// A local engine that has its own vocabulary correction to run over its raw
/// output (the Parakeet family's phonetic pass, issue #283).
///
/// The engine does NOT run it inside its own <c>TranscribeAsync</c>:
/// <see cref="Transcription.TranscriptionOrchestrator"/> calls this once, right
/// after it has kept the engine's text as <c>RawText</c>, and before its own
/// <c>\b</c>-anchored <see cref="VocabularyProcessor"/> pass. So the History row's
/// raw transcript is the engine's own text, as it is for Whisper, and each
/// vocabulary pass runs exactly once per transcription (issue #1596).
/// </summary>
public interface ILocalVocabularyCorrection
{
    /// <summary>
    /// Returns <paramref name="rawText"/> with the engine's own vocabulary
    /// correction applied. Must never throw: on any failure it returns
    /// <paramref name="rawText"/> unchanged.
    /// </summary>
    string ApplyLocalVocabularyCorrection(string rawText);
}
