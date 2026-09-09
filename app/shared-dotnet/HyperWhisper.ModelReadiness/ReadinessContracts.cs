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
    RateLimited,
    Unreachable,
    Malformed,
    Installed,
    Downloadable,
    Unsupported,
}

/// <param name="SupportedLanguages">
/// Language codes in ONE code space: for a cloud STT row, the provider's raw
/// upstream codes exactly as <c>cloud-stt-catalog.json</c> declares them
/// (<c>nb</c>, <c>fil</c>, Deepgram's <c>multi</c> and <c>ar-AE</c>); for a local
/// row, the model's own list. Never the folded picker space — a caller matching
/// against this list must use the same codes the catalog uses, and every cloud
/// row must answer that question the same way. It is PROVIDER-level for cloud
/// rows, so where a provider's models differ it is the union; see
/// <paramref name="ModelLanguageCount"/> for the per-model figure.
/// </param>
/// <param name="ModelLanguageCount">
/// How many languages THIS model supports, when that differs from
/// <paramref name="SupportedLanguages"/>.Count — i.e. when the provider's models
/// do not share one table. Null means "the provider list is this model's list".
///
/// A COUNT and not a code list, deliberately. The only per-model language data
/// in the tree (<c>shared-models/models-catalog.json</c>) is written in the
/// FOLDED PICKER code space, and putting those codes into
/// <paramref name="SupportedLanguages"/> made one field answer in two spaces:
/// the Azure rows said <c>no</c> where every other row says <c>nb</c>, so a
/// caller feeding it catalog-native codes silently got a different answer for
/// one vendor. A count carries the fact the Model Library actually shows without
/// carrying a code space with it.
/// </param>
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
    bool RequiresCredential = true,
    int? ModelLanguageCount = null);

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

public enum ProviderHealthOutcome { Healthy, Unauthorized, RateLimited, Unreachable, Malformed, Unsupported }

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
    string? Detail = null);

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
