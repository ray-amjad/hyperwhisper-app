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
