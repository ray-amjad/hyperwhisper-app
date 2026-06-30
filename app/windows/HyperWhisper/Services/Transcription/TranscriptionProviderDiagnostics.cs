namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Provider-level metadata captured during a transcription attempt.
/// This is attached to results/exceptions so the UI can report diagnostics.
/// </summary>
public record TranscriptionProviderDiagnostics(
    string ProviderDisplayName,
    string? BackendRequestId = null,
    string? BackendSttProvider = null,
    bool? BackendNoSpeechDetected = null,
    int? HttpStatusCode = null,
    double? ResponseLatencyMs = null,
    bool? EmptyTranscriptWithoutFlag = null
);

/// <summary>
/// Optional interface for providers that expose per-attempt diagnostics.
/// </summary>
public interface ITranscriptionDiagnosticsSource
{
    TranscriptionProviderDiagnostics? LastDiagnostics { get; }
}
