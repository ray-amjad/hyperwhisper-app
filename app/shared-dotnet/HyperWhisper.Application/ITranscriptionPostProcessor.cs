using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Transcription;

/// <param name="Model">
/// The model that ACTUALLY RAN (issue #314) — appended last so no existing
/// positional call site shifts. Set only by <see cref="Applied"/>, never by
/// <see cref="Skipped"/>, so a non-null value already means "an LLM produced
/// this text" and no <see cref="WasApplied"/> cross-check is needed at the
/// Local API endpoint. Null means "the processor did not name one", and the
/// endpoint then falls back to the labels stored on the Mode.
/// </param>
public sealed record PortablePostProcessingResult(
    string Text,
    bool WasApplied,
    string? Provider,
    string? FailureCode = null,
    string? FailureMessage = null,
    string? Model = null)
{
    public static PortablePostProcessingResult Applied(string text, string provider, string? model = null) =>
        new(text, true, provider, Model: model);

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
