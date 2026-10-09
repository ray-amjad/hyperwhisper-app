namespace HyperWhisper.PortableApplication.Transcription;

public interface IRecordedAudioTranscriber
{
    TranscriptionBackendCapability Capability { get; }

    Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default) =>
        TranscribeAsync(audioPath, request.Language, cancellationToken);

    // Compatibility entry point for fixed local backends. Mode-aware routers
    // override the request overload above; existing platform implementations
    // continue to receive the normalized language without losing compatibility.
    Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? language,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A transcriber whose local engine has its own vocabulary correction to run
/// over its raw output: the Parakeet family's phonetic (Beider-Morse) pass,
/// issue #283.
///
/// The transcriber does NOT run it inside <c>TranscribeAsync</c>.
/// <see cref="TranscriptionWorkflow"/> calls this once, right after it has kept
/// the engine's text as the raw transcript (the History row's
/// <c>TranscribedText</c>), and before <c>SpeechOutputProcessor</c>'s
/// <c>\b</c>-anchored replacement pass. So the raw transcript is the engine's
/// own text, as it is for Whisper, and each vocabulary pass runs exactly once
/// per transcription (issue #1622, the Linux side of Windows #1596, whose
/// Windows-only interface is <c>HyperWhisper.Services.ILocalVocabularyCorrection</c>;
/// this one has a different name so a Windows file that imports both
/// namespaces never sees an ambiguous type).
/// </summary>
public interface ILocalEngineVocabularyCorrection
{
    /// <summary>
    /// Returns <paramref name="rawText"/> with the engine's own vocabulary
    /// correction applied, for the engine <paramref name="request"/> selected.
    /// Must never write a vocabulary row's replacement value: that is the
    /// <c>\b</c> pass's job, and doing it here too applies a swap twice.
    /// </summary>
    string ApplyLocalVocabularyCorrection(string rawText, TranscriptionWorkflowRequest request);
}
