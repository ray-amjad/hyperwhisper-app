namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Provider-level metadata captured during a transcription attempt.
/// This is attached to results/exceptions so the UI can report diagnostics.
/// </summary>
/// <param name="AttemptSource">
/// Which arm of <c>TranscriptionOrchestrator</c> produced this record — see
/// <see cref="TranscriptionAttemptSource"/>. Reported as the
/// <c>provider_attempt_source</c> Sentry tag. Before it existed, only
/// HyperWhisper Cloud filled a diagnostics record at all, so a no-speech event
/// from a local engine or from a BYOK cloud provider was indistinguishable from
/// one where the diagnostics were simply dropped.
/// </param>
/// <param name="AttemptElapsedMs">
/// Wall-clock milliseconds the orchestrator measured around the provider call.
/// This is NOT <see cref="ResponseLatencyMs"/>: that one is the provider's own
/// HTTP timing and stays null for anything that does not make an HTTP request.
/// This one is filled for every arm, so "the engine answered in 40 ms" and "the
/// engine answered in 40 s" stop looking the same in Sentry.
/// </param>
/// <param name="RawResultLength">
/// Character count of the raw result the provider returned, or null when the
/// attempt threw before returning one. It is a COUNT, never the text: the
/// no-speech path only ever sees a value that is empty or whitespace, and 0 vs
/// a small non-zero count is the difference between "the provider returned
/// nothing" and "the provider returned whitespace", which is the fault we
/// cannot currently tell apart on the local arm.
/// </param>
public record TranscriptionProviderDiagnostics(
    string ProviderDisplayName,
    string? BackendRequestId = null,
    string? BackendSttProvider = null,
    bool? BackendNoSpeechDetected = null,
    int? HttpStatusCode = null,
    double? ResponseLatencyMs = null,
    bool? EmptyTranscriptWithoutFlag = null,
    string? AttemptSource = null,
    double? AttemptElapsedMs = null,
    int? RawResultLength = null
);

/// <summary>
/// The stable <c>provider_attempt_source</c> slugs. Fixed strings, so Sentry can
/// facet on them: the whole point is to split one no-speech group by which arm
/// produced it.
/// </summary>
public static class TranscriptionAttemptSource
{
    /// <summary>A cloud provider that reports its own diagnostics (HyperWhisper Cloud).</summary>
    public const string CloudInstrumented = "cloud_instrumented";

    /// <summary>
    /// A cloud provider that does not implement <see cref="ITranscriptionDiagnosticsSource"/>
    /// — every BYOK vendor. The orchestrator fills the display name and its own
    /// elapsed time; the backend fields stay unknown because nothing captured them.
    /// </summary>
    public const string CloudUninstrumented = "cloud_uninstrumented";

    /// <summary>A local engine (Parakeet, Whisper).</summary>
    public const string LocalEngine = "local_engine";

    /// <summary>No record reached the diagnostic at all.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Optional interface for providers that expose per-attempt diagnostics.
/// </summary>
public interface ITranscriptionDiagnosticsSource
{
    TranscriptionProviderDiagnostics? LastDiagnostics { get; }
}
