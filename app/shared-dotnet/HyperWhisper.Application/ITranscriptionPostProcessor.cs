using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Transcription;

public sealed record PortablePostProcessingResult(
    string Text,
    bool WasApplied,
    string? Provider,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    public static PortablePostProcessingResult Applied(string text, string provider) =>
        new(text, true, provider);

    public static PortablePostProcessingResult Skipped(
        string original,
        string? failureCode = null,
        string? failureMessage = null) =>
        new(original, false, null, failureCode, failureMessage);
}

public interface ITranscriptionPostProcessor
{
    Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        ApplicationContextSnapshot? applicationContext,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(transcript, mode, cancellationToken);

    // Compatibility entry point for processors that do not consume desktop
    // context. New processors should override the context-aware overload.
    Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        CancellationToken cancellationToken = default);
}
