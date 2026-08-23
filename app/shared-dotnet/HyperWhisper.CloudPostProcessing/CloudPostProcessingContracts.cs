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

public sealed record CloudPostProcessingResult(
    string Text,
    bool WasApplied,
    string? Provider,
    CloudPostProcessingFailure? Failure)
{
    public static CloudPostProcessingResult Applied(string text, string provider) =>
        new(text, true, provider, null);

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
