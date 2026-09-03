namespace HyperWhisper.CloudPostProcessing;

public enum CloudPostProcessingProvider
{
    OpenAi,
    Anthropic,
    Groq,
    Grok,
    Gemini,
    Cerebras,
    Mistral,
    HyperWhisperCloud,
    Custom,
}

public enum CloudPostProcessingFailureCode
{
    InvalidRequest,
    MissingCredential,
    ProviderUnavailable,
    RejectedResponse,
    RequestFailed,
    TimedOut,
    Cancelled,
}

public sealed record CustomPostProcessingEndpoint(
    Guid Id,
    string EndpointUrl,
    string Model);

public sealed record CloudPostProcessingRequest(
    string Transcript,
    string SystemPrompt,
    string SystemInfo,
    CloudPostProcessingProvider Provider,
    string? Model = null,
    string? HyperWhisperCloudModel = null,
    CustomPostProcessingEndpoint? CustomEndpoint = null);

public sealed record CloudPostProcessingFailure(
    CloudPostProcessingFailureCode Code,
    string Message);

/// <param name="Provider">
/// A human display label for the run — <c>"OpenAI · gpt-5.6-luna"</c>. NOT the
/// wire provider id the macOS and Windows heads emit.
/// </param>
/// <param name="Model">
/// The model that ACTUALLY RAN (issue #314): the post-fallback BYOK id, the
/// custom endpoint's model name, or the <c>X-LLM-Model</c> value sent to
/// HyperWhisper Cloud. Set only on <see cref="Applied"/> — never on
/// <see cref="Failed"/> — so a non-null value already means "an LLM produced
/// this text" and no <see cref="WasApplied"/> cross-check is needed downstream.
/// The <paramref name="Provider"/> label already embeds the model on this head,
/// which makes this look redundant; it is populated anyway so the Local API
/// `model` field means the same thing on all three heads.
/// </param>
public sealed record CloudPostProcessingResult(
    string Text,
    bool WasApplied,
    string? Provider,
    CloudPostProcessingFailure? Failure,
    string? Model = null)
{
    public static CloudPostProcessingResult Applied(string text, string provider, string? model = null) =>
        new(text, true, provider, null, model);

    public static CloudPostProcessingResult Failed(
        string original,
        CloudPostProcessingFailureCode code,
        string message) =>
        new(original, false, null, new CloudPostProcessingFailure(code, message));
}

public sealed record PostProcessingCredential(
    string? ApiKey = null,
    string? LicenseKey = null,
    string? DeviceId = null);

public interface IPostProcessingCredentialSource
{
    ValueTask<PostProcessingCredential?> GetCredentialAsync(
        CloudPostProcessingProvider provider,
        Guid? customEndpointId,
        CancellationToken cancellationToken = default);
}
