namespace HyperWhisper.ModelReadiness;

public sealed class ModelReadinessService
{
    private readonly IProviderCredentialSource _credentials;
    private readonly IProviderHealthProbe _probe;
    private readonly ILocalModelReadinessSource _local;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    public ModelReadinessService(
        IProviderCredentialSource credentials,
        IProviderHealthProbe probe,
        ILocalModelReadinessSource local,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Health timeout must be between zero and 30 seconds.");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<ModelReadinessChangedEventArgs>? ReadinessChanged;
    public event EventHandler<string>? CredentialInvalidated;

    public async ValueTask<ModelReadiness> CheckAsync(
        ModelCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.Deployment == ModelDeployment.Local)
        {
            var installed = await _local.IsInstalledAsync(capability, cancellationToken).ConfigureAwait(false);
            return Publish(capability.Key, installed ? ReadinessState.Installed : ReadinessState.Downloadable);
        }

        if (capability.RequiresCredential && string.IsNullOrWhiteSpace(capability.CredentialAccount))
            return Publish(capability.Key, ReadinessState.Unsupported, "No credential mapping is available.");

        ProviderCredential credential;
        if (capability.RequiresCredential)
        {
            var stored = await _credentials.GetCredentialAsync(capability.CredentialAccount!, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null || !stored.IsPresent)
                return Publish(capability.Key, ReadinessState.MissingCredential);
            credential = stored;
        }
        else
        {
            credential = new ProviderCredential(string.Empty);
        }

        Publish(capability.Key, ReadinessState.Checking);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var response = await _probe.CheckAsync(new ProviderHealthRequest(
                capability.ProviderId, capability.ModelId, capability.Surface, credential, capability.Endpoint),
                timeout.Token).ConfigureAwait(false);
            var detail = ReadinessText.BoundAndRedact(response.Detail, credential);
            var state = response.Outcome switch
            {
                ProviderHealthOutcome.Healthy => ReadinessState.Healthy,
                ProviderHealthOutcome.Unauthorized => ReadinessState.Unauthorized,
                ProviderHealthOutcome.RateLimited => ReadinessState.RateLimited,
                ProviderHealthOutcome.Unreachable => ReadinessState.Unreachable,
                ProviderHealthOutcome.Malformed => ReadinessState.Malformed,
                ProviderHealthOutcome.Unsupported => ReadinessState.Unsupported,
                _ => ReadinessState.Unknown,
            };
            return Publish(capability.Key, state, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Publish(capability.Key, ReadinessState.Unreachable, "Provider health check timed out.");
        }
        catch (HttpRequestException)
        {
            return Publish(capability.Key, ReadinessState.Unreachable, "Provider could not be reached.");
        }
    }

    /// <summary>Allows a credential store to invalidate only rows backed by the changed account.</summary>
    public void NotifyCredentialChanged(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        CredentialInvalidated?.Invoke(this, account);
    }

    private ModelReadiness Publish(string key, ReadinessState state, string? detail = null)
    {
        var readiness = new ModelReadiness(key, state, detail, _timeProvider.GetUtcNow());
        ReadinessChanged?.Invoke(this, new ModelReadinessChangedEventArgs(readiness));
        return readiness;
    }
}
