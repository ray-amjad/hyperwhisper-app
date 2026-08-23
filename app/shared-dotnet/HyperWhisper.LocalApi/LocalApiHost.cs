using System.Net;
using System.Text.Json;
using HyperWhisper.Platform.Abstractions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace HyperWhisper.LocalApi;

public sealed record LocalApiHostFailure(string Code, string Message);

public sealed record LocalApiHostState(
    bool IsRunning,
    int Port,
    string? BaseAddress,
    LocalApiHostFailure? Failure)
{
    public static LocalApiHostState Stopped { get; } = new(false, 0, null, null);
    public static LocalApiHostState Failed(string code, string message) => new(false, 0, null, new(code, message));
}

public sealed record LocalApiDiscovery(
    int Port,
    int Pid,
    string StartedAt,
    int ApiVersion,
    string AppVersion,
    string Token);

public sealed class PortableLocalApiHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions DiscoveryJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IPrivateFileService _privateFiles;
    private readonly ILocalApiBackend _backend;
    private readonly string _tokenPath;
    private readonly string _discoveryPath;
    private readonly string _appVersion;
    private readonly int _preferredPort;
    private readonly long _maxRequestBytes;
    private readonly int _maxUploadBytes;
    private readonly Func<int, string, CancellationToken, Task<Microsoft.AspNetCore.Builder.WebApplication>> _startApplication;
    private Microsoft.AspNetCore.Builder.WebApplication? _application;
    private LocalApiHostState _state = LocalApiHostState.Stopped;
    private int _disposed;

    public PortableLocalApiHost(
        IPrivateFileService privateFiles,
        IAppPaths paths,
        ILocalApiBackend backend,
        string appVersion,
        int preferredPort = 51671,
        long maxRequestBytes = 52_428_800,
        int maxUploadBytes = 50_331_648,
        Func<int, string, CancellationToken, Task<Microsoft.AspNetCore.Builder.WebApplication>>? applicationStarter = null)
    {
        _privateFiles = privateFiles ?? throw new ArgumentNullException(nameof(privateFiles));
        ArgumentNullException.ThrowIfNull(paths);
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? throw new ArgumentException("App version is required.", nameof(appVersion)) : appVersion;
        if (preferredPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(preferredPort));
        if (maxRequestBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxRequestBytes));
        if (maxUploadBytes <= 0 || maxUploadBytes > maxRequestBytes) throw new ArgumentOutOfRangeException(nameof(maxUploadBytes));
        _preferredPort = preferredPort;
        _maxRequestBytes = maxRequestBytes;
        _maxUploadBytes = maxUploadBytes;
        _startApplication = applicationStarter ?? StartOnPortAsync;
        _tokenPath = Path.Combine(paths.DataDirectory, "local-api-token");
        _discoveryPath = Path.Combine(paths.DataDirectory, "local-api.json");
    }

    public LocalApiHostState State => _state;
    public string DiscoveryPath => _discoveryPath;

    public async Task<LocalApiHostState> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return LocalApiHostState.Failed("local_api.disposed", "The Local API host has been disposed.");
        try { await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalApiHostState.Failed("local_api.cancelled", "Local API startup was cancelled.");
        }
        try
        {
            if (_application is not null) return _state;
            string token;
            try { token = new LocalApiTokenStore(_privateFiles, _tokenPath).LoadOrCreate(); }
            catch (InvalidOperationException) { return _state = LocalApiHostState.Failed("local_api.token", "The Local API credential could not be loaded securely."); }

            Microsoft.AspNetCore.Builder.WebApplication application;
            try
            {
                application = await _startApplication(_preferredPort, token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (_preferredPort != 0 && LocalApiBindFallback.IsBindFailure(exception))
            {
                try
                {
                    application = await _startApplication(0, token, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return _state = LocalApiHostState.Failed("local_api.cancelled", "Local API startup was cancelled during loopback fallback.");
                }
                catch (Exception)
                {
                    return _state = LocalApiHostState.Failed("local_api.bind", "The Local API could not bind a fallback loopback port.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return _state = LocalApiHostState.Failed("local_api.cancelled", "Local API startup was cancelled.");
            }
            catch (Exception)
            {
                return _state = LocalApiHostState.Failed("local_api.bind", "The Local API could not bind a loopback port.");
            }

            Uri? address;
            try { address = GetLoopbackAddress(application); }
            catch (Exception) { address = null; }
            if (address is null)
            {
                var cleaned = await CleanupFailedStartAsync(application).ConfigureAwait(false);
                return _state = cleaned
                    ? LocalApiHostState.Failed("local_api.address", "Kestrel did not report a valid IPv4 loopback address.")
                    : LocalApiHostState.Failed("local_api.cleanup", "The invalid Local API listener stopped, but discovery cleanup could not be confirmed.");
            }

            var discovery = new LocalApiDiscovery(address.Port, Environment.ProcessId, DateTimeOffset.UtcNow.ToString("O"), 1, _appVersion, token);
            var write = _privateFiles.WriteAllTextAtomically(_discoveryPath, JsonSerializer.Serialize(discovery, DiscoveryJson));
            var restricted = write.IsSuccess ? _privateFiles.IsRestrictedToCurrentUser(_discoveryPath) : PlatformResult<bool>.Failure("local_api.discovery", "Discovery write failed.");
            if (write.IsFailure || restricted.IsFailure || restricted.Value != true)
            {
                var cleaned = await CleanupFailedStartAsync(application).ConfigureAwait(false);
                return _state = cleaned
                    ? LocalApiHostState.Failed("local_api.discovery", "The Local API discovery file could not be written privately.")
                    : LocalApiHostState.Failed("local_api.cleanup", "The Local API discovery write failed and cleanup could not be confirmed.");
            }

            _application = application;
            return _state = new(true, address.Port, address.ToString(), null);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<LocalApiHostState> StopAsync(CancellationToken cancellationToken = default)
    {
        // Once shutdown is requested, cleanup is non-cancellable: leaving a
        // discovery file containing a still-valid token is worse than a late
        // cancellation response.
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var wasCancelled = cancellationToken.IsCancellationRequested;
            var application = _application;
            _application = null;
            Exception? shutdownFailure = null;
            if (application is not null)
            {
                try { await application.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) { shutdownFailure = exception; }
                try { await application.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { shutdownFailure ??= exception; }
            }
            var deleted = _privateFiles.Delete(_discoveryPath);
            if (deleted.IsFailure) return _state = LocalApiHostState.Failed("local_api.cleanup", "The Local API stopped but its discovery file could not be removed.");
            if (shutdownFailure is not null)
                return _state = LocalApiHostState.Failed("local_api.shutdown", "The Local API discovery file was removed, but the web host reported a shutdown failure.");
            if (wasCancelled || cancellationToken.IsCancellationRequested)
                return _state = LocalApiHostState.Failed("local_api.cancelled", "Local API shutdown completed after cancellation was requested.");
            return _state = LocalApiHostState.Stopped;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Microsoft.AspNetCore.Builder.WebApplication> StartOnPortAsync(int port, string token, CancellationToken cancellationToken)
    {
        var options = new PortableLocalApiOptions(token, port, _maxRequestBytes, _maxUploadBytes);
        var application = PortableLocalApi.Build([], options, _backend);
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return application;
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> CleanupFailedStartAsync(Microsoft.AspNetCore.Builder.WebApplication application)
    {
        try { await application.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
        try { await application.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
        try { return _privateFiles.Delete(_discoveryPath).IsSuccess; }
        catch (Exception) { return false; }
    }

    private static Uri? GetLoopbackAddress(Microsoft.AspNetCore.Builder.WebApplication application)
    {
        var feature = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        foreach (var raw in feature?.Addresses ?? [])
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var address)
                && IPAddress.TryParse(address.Host, out var ip)
                && ip.Equals(IPAddress.Loopback)) return address;
        }
        return null;
    }
}
