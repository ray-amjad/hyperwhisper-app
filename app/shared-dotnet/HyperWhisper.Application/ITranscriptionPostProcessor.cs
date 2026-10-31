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
/// <param name="Provider">
/// The display label of the run (<c>"OpenAI · gpt-5.6-luna"</c>) — UI text.
/// <see cref="ProviderLabel"/> names the same value. A processor that sets no
/// <see cref="ProviderId"/> has only this one string, and it is then used as
/// the id too (<see cref="StoredProvider"/>).
/// </param>
/// <param name="ProviderId">
/// The provider id the run used, in the spelling a Mode stores and the macOS and
/// Windows heads emit (issue #1158): <c>openai</c>, <c>anthropic</c>,
/// <c>groq</c>, <c>grok</c>, <c>gemini</c>, <c>cerebras</c>, <c>mistral</c>,
/// <c>hyperwhispercloud</c>, <c>custom:&lt;uuid&gt;</c> or <c>local_llm</c>.
/// Appended last so no existing positional call site shifts.
/// </param>
public sealed record PortablePostProcessingResult(
    string Text,
    bool WasApplied,
    string? Provider,
    string? FailureCode = null,
    string? FailureMessage = null,
    string? Model = null,
    string? ProviderId = null)
{
    /// <summary>The display label (<see cref="Provider"/>), named for what it is.</summary>
    public string? ProviderLabel => Provider;

    /// <summary>
    /// What the Local API <c>provider</c> field and the transcript row's
    /// <c>PostProcessingProvider</c> get: the <see cref="ProviderId"/>, or the
    /// single <see cref="Provider"/> string of a processor that names no id.
    /// </summary>
    public string? StoredProvider =>
        string.IsNullOrWhiteSpace(ProviderId) ? Provider : ProviderId;

    /// <summary>
    /// The <see cref="FailureCode"/> for a HyperWhisper Cloud run refused
    /// because no account key is stored (issue #1487). The Local API answers
    /// <c>MISSING_API_KEY</c> for it, the same refusal <c>/transcribe</c> gives.
    /// </summary>
    public const string CloudAccountRequiredFailureCode = "postprocessing.cloud_account_required";

    public static PortablePostProcessingResult Applied(
        string text, string provider, string? model = null, string? providerId = null) =>
        new(text, true, provider, Model: model, ProviderId: providerId);

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
