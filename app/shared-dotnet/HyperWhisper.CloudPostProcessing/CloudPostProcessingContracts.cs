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
/// The human display label for the run — <c>"OpenAI · gpt-5.6-luna"</c>. UI
/// text only: never put it on the wire or in a transcript row. Read
/// <see cref="ProviderId"/> for that (issue #1158). <see cref="ProviderLabel"/>
/// names the same value.
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
/// <param name="ProviderId">
/// The provider id the run used, in the spelling a Mode stores and the macOS and
/// Windows heads emit (issue #1158): <c>openai</c>, <c>anthropic</c>,
/// <c>groq</c>, <c>grok</c>, <c>gemini</c>, <c>cerebras</c>, <c>mistral</c>,
/// <c>hyperwhispercloud</c> or <c>custom:&lt;uuid&gt;</c>. Set only on
/// <see cref="Applied"/>. Appended last so no positional call site shifts.
/// </param>
public sealed record CloudPostProcessingResult(
    string Text,
    bool WasApplied,
    string? Provider,
    CloudPostProcessingFailure? Failure,
    string? Model = null,
    string? ProviderId = null)
{
    /// <summary>The display label (<see cref="Provider"/>), named for what it is.</summary>
    public string? ProviderLabel => Provider;

    public static CloudPostProcessingResult Applied(
        string text, string provider, string? model = null, string? providerId = null) =>
        new(text, true, provider, null, model, providerId);

    /// <summary>
    /// The stored provider id for a provider (issue #1158) — the spelling
    /// <c>LinuxPostProcessingRouter.TryResolveProvider</c> parses back. A custom
    /// endpoint needs its id; without one it has no id and this returns null.
    /// </summary>
    public static string? IdFor(CloudPostProcessingProvider provider, Guid? customEndpointId = null) => provider switch
    {
        CloudPostProcessingProvider.OpenAi => "openai",
        CloudPostProcessingProvider.Anthropic => "anthropic",
        CloudPostProcessingProvider.Groq => "groq",
        CloudPostProcessingProvider.Grok => "grok",
        CloudPostProcessingProvider.Gemini => "gemini",
        CloudPostProcessingProvider.Cerebras => "cerebras",
        CloudPostProcessingProvider.Mistral => "mistral",
        CloudPostProcessingProvider.HyperWhisperCloud => "hyperwhispercloud",
        CloudPostProcessingProvider.Custom when customEndpointId is { } id && id != Guid.Empty => $"custom:{id:D}",
        _ => null,
    };

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
