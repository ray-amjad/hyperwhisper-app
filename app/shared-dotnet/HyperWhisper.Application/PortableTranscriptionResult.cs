using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Transcription;

public enum PortableTranscriptionErrorCode
{
    InvalidRequest,
    BackendUnavailable,
    TranscriptionFailed,
    Cancelled,
}

public sealed record PortableTranscriptionFailure(
    PortableTranscriptionErrorCode Code,
    string Message);

public sealed record PortableTranscriptionResult(
    string? Text,
    string? Provider,
    PortableTranscriptionFailure? Failure,
    string? RawText = null,
    string? PostProcessedText = null,
    string? PostProcessingProvider = null,
    TextInjectionOutcome? InjectionOutcome = null,
    TranscriptionTimestamps? Timestamps = null)
{
    public bool IsSuccess => Failure is null && !string.IsNullOrWhiteSpace(Text);

    public static PortableTranscriptionResult Success(string text, string provider) =>
        new(text, provider, null);

    public static PortableTranscriptionResult Failed(
        PortableTranscriptionErrorCode code,
        string message,
        string? provider = null) =>
        new(null, provider, new PortableTranscriptionFailure(code, message));
}
