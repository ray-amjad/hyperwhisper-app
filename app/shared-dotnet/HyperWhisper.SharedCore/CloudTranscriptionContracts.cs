namespace HyperWhisper.SharedCore;

public enum CloudTranscriptionProvider
{
    OpenAi,
    Groq,
    ElevenLabs,
    Mistral,
    Grok,
    Deepgram,
    AssemblyAi,
    Soniox,
    Gemini,
    AzureMai,
    GoogleChirp,
    HyperWhisperCloud,
}

public sealed record CloudCredential(string? ApiKey = null, string? LicenseKey = null, string? DeviceId = null);

public interface ICloudCredentialSource
{
    ValueTask<CloudCredential?> GetCredentialAsync(
        CloudTranscriptionProvider provider,
        CancellationToken cancellationToken);
}

public sealed record CloudTranscriptionRequest(
    CloudTranscriptionProvider Provider,
    string AudioPath,
    string Model,
    string? Language = null,
    IReadOnlyList<string>? Vocabulary = null,
    string? Prompt = null,
    string? AudioMime = null,
    string? BaseUrl = null,
    string? RoutedProvider = null,
    string? RoutedModel = null,
    string? RoutedDomain = null);

public enum CloudTranscriptionErrorCode
{
    Unauthorized,
    QuotaExceeded,
    FileTooLarge,
    RateLimited,
    ProviderUnavailable,
    NoSpeech,
    InvalidRequest,
    Network,
    Cancelled,
    Unsupported,
    Unknown,
}

public sealed record CloudTranscriptionFailure(
    CloudTranscriptionErrorCode Code,
    string Message,
    CloudTranscriptionProvider Provider,
    int? HttpStatus = null,
    int? RetryAfterSeconds = null);

public sealed record CloudTranscript(
    string Text,
    double? CreditsRemaining,
    double? Cost,
    string? RawProvider);

public sealed record CloudTranscriptionResult(
    CloudTranscript? Transcript,
    CloudTranscriptionFailure? Failure,
    int Attempts)
{
    public bool IsSuccess => Transcript is not null && Failure is null;

    public static CloudTranscriptionResult Success(CloudTranscript transcript, int attempts) =>
        new(transcript, null, attempts);

    public static CloudTranscriptionResult Failed(CloudTranscriptionFailure failure, int attempts) =>
        new(null, failure, attempts);
}

public sealed record CloudProviderDescriptor(
    CloudTranscriptionProvider Provider,
    string CatalogId,
    string DisplayName,
    bool IsMultiStep,
    bool SupportsBatch);

public sealed record CloudTranscriptionEvent(
    CloudTranscriptionProvider Provider,
    int Attempt,
    int? Status,
    string Stage);

public interface ICloudTranscriptionObserver
{
    void OnEvent(CloudTranscriptionEvent value);
}

public interface ICloudTranscriptionDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemCloudTranscriptionDelay : ICloudTranscriptionDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
