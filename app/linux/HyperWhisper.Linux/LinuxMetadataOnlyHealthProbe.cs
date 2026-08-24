using HyperWhisper.ModelReadiness;

namespace HyperWhisper.Linux;

/// <summary>
/// Conservative Linux readiness probe. Credential presence and local installation are checked by
/// the shared service; providers are never sent inference content. Provider-specific authenticated
/// metadata probes can be added independently without turning page load into network activity.
/// </summary>
internal sealed class LinuxMetadataOnlyHealthProbe : IProviderHealthProbe
{
    private static readonly HttpMessageInvoker Transport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });
    private static readonly ProviderMetadataHealthProbe Probe = new(Transport);

    public async ValueTask<ProviderHealthResponse> CheckAsync(
        ProviderHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await Probe.CheckAsync(request, cancellationToken).ConfigureAwait(false);
        LinuxProviderReadinessSnapshot.Record(request.ProviderId, request.Surface, result);
        return result;
    }
}

internal static class LinuxProviderReadinessSnapshot
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Provider, ModelSurface Surface), ProviderHealthResponse> Values = new();

    public static void Record(string provider, ModelSurface surface, ProviderHealthResponse response)
    {
        lock (Sync) Values[(Normalize(provider), surface)] = response;
    }

    public static ProviderHealthResponse? Get(string provider, ModelSurface surface)
    {
        lock (Sync) return Values.GetValueOrDefault((Normalize(provider), surface));
    }

    private static string Normalize(string provider) => provider.Trim().ToLowerInvariant();
}
