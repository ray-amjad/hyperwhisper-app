using System.Text;

namespace HyperWhisper.ModelReadiness;

public enum ModelDeployment { Local, Cloud }
public enum ModelWorkload { Voice, Text }
public enum ModelSurface { BatchTranscription, StreamingTranscription, PostProcessing, CustomEndpoint }
public enum ReadinessState
{
    Unknown,
    MissingCredential,
    Checking,
    Healthy,
    Unauthorized,
    Unreachable,
    Installed,
    Downloadable,
    Unsupported,
}

public sealed record ModelCapability(
    string Key,
    string DisplayName,
    string ProviderId,
    string ModelId,
    ModelDeployment Deployment,
    ModelWorkload Workload,
    ModelSurface Surface,
    bool SupportsCustomVocabulary,
    bool SupportsAllLanguages,
    IReadOnlyList<string> SupportedLanguages,
    bool SupportsStreaming,
    string? Runtime = null,
    long? RecommendedVramBytes = null,
    long? ApproximateSizeBytes = null,
    bool IsEnglishOnly = false,
    bool CloudTierEligible = false,
    bool ByokEligible = false,
    Uri? Endpoint = null,
    string? CredentialAccount = null,
    bool RequiresCredential = true);

public sealed record CustomEndpointDefinition(
    Guid Id,
    string DisplayName,
    Uri Endpoint,
    string ModelId,
    string CredentialAccount,
    bool RequiresCredential = true);

/// <summary>A single provider-scoped secret. Its value is never included in diagnostics.</summary>
public sealed class ProviderCredential
{
    public ProviderCredential(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
    public string Value { get; }
    public bool IsPresent => !string.IsNullOrWhiteSpace(Value);
    public override string ToString() => "[redacted]";
}

public interface IProviderCredentialSource
{
    ValueTask<ProviderCredential?> GetCredentialAsync(string account, CancellationToken cancellationToken = default);
}

public sealed record ProviderHealthRequest(
    string ProviderId,
    string ModelId,
    ModelSurface Surface,
    ProviderCredential Credential,
    Uri? Endpoint = null);

public enum ProviderHealthOutcome { Healthy, Unauthorized, Unreachable, Unsupported }

public sealed record ProviderHealthResponse(ProviderHealthOutcome Outcome, string? Detail = null)
{
    public const int MaximumDetailBytes = 4096;
}

/// <summary>
/// Performs a metadata-only provider check. Implementations must not perform inference or send
/// audio, transcripts, prompts, vocabulary, or credentials other than Request.Credential.
/// </summary>
public interface IProviderHealthProbe
{
    ValueTask<ProviderHealthResponse> CheckAsync(
        ProviderHealthRequest request,
        CancellationToken cancellationToken = default);
}

public interface ILocalModelReadinessSource
{
    ValueTask<bool> IsInstalledAsync(ModelCapability model, CancellationToken cancellationToken = default);
}

public sealed record ModelReadiness(
    string CapabilityKey,
    ReadinessState State,
    string? Detail = null,
    DateTimeOffset? CheckedAt = null);

public sealed class ModelReadinessChangedEventArgs : EventArgs
{
    public ModelReadinessChangedEventArgs(ModelReadiness readiness) => Readiness = readiness;
    public ModelReadiness Readiness { get; }
}

internal static class ReadinessText
{
    public static string? BoundAndRedact(string? detail, ProviderCredential credential)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        if (Encoding.UTF8.GetByteCount(detail) > ProviderHealthResponse.MaximumDetailBytes)
            return "Provider returned an oversized health response.";

        var value = credential.Value;
        return string.IsNullOrEmpty(value)
            ? detail
            : detail.Replace(value, "[redacted]", StringComparison.Ordinal);
    }
}
