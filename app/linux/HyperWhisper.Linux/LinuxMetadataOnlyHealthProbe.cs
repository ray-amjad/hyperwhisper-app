using HyperWhisper.ModelReadiness;

namespace HyperWhisper.Linux;

/// <summary>
/// Conservative Linux readiness probe. Credential presence and local installation are checked by
/// the shared service; providers are never sent inference content. Provider-specific authenticated
/// metadata probes can be added independently without turning page load into network activity.
/// </summary>
internal sealed class LinuxMetadataOnlyHealthProbe : IProviderHealthProbe
{
    public ValueTask<ProviderHealthResponse> CheckAsync(
        ProviderHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ProviderHealthResponse(
            ProviderHealthOutcome.Unsupported,
            "This provider has no content-free health endpoint in the Linux build."));
    }
}
