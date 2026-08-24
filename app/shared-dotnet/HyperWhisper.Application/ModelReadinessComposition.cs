using System.Security.Cryptography;
using System.Text;
using HyperWhisper.ModelManagement;
using HyperWhisper.ModelReadiness;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.ModelLibrary;

/// <summary>Adapts the secure platform credential store without retaining secret bytes.</summary>
public sealed class SecureStoreProviderCredentialSource(ICredentialStore store) : IProviderCredentialSource
{
    private const string Resource = "HyperWhisper";
    private readonly ICredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<ProviderCredential?> GetCredentialAsync(
        string account,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _store.Read(Resource, account);
        if (result.IsFailure || result.Value is not { Length: > 0 } bytes)
            return ValueTask.FromResult<ProviderCredential?>(null);
        try
        {
            return ValueTask.FromResult<ProviderCredential?>(new(Encoding.UTF8.GetString(bytes)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>Answers local readiness from the same owner-scoped model manager used for downloads.</summary>
public sealed class PortableLocalModelReadinessSource(PortableModelManager manager) : ILocalModelReadinessSource
{
    private readonly PortableModelManager _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public ValueTask<bool> IsInstalledAsync(
        ModelCapability model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var managed = PortableModelCatalog.All.FirstOrDefault(item =>
            string.Equals(item.Id, model.ModelId, StringComparison.Ordinal));
        return ValueTask.FromResult(managed is not null && _manager.IsInstalled(managed));
    }
}

/// <summary>
/// Builds the readiness service around an explicitly supplied metadata-only probe. Merely creating
/// this composition performs no provider request; the model-library refresh commands own probing.
/// </summary>
public static class ModelReadinessComposition
{
    public static ModelReadinessService Create(
        PortableModelManager manager,
        ICredentialStore credentials,
        IProviderHealthProbe metadataOnlyProbe,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
        => new(
            new SecureStoreProviderCredentialSource(credentials),
            metadataOnlyProbe ?? throw new ArgumentNullException(nameof(metadataOnlyProbe)),
            new PortableLocalModelReadinessSource(manager),
            timeout,
            timeProvider);
}
