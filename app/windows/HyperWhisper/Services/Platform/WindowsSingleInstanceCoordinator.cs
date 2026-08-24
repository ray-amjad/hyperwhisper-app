using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

/// <summary>
/// Portable facade over the existing named-mutex and window-message mechanism.
/// The static guard remains available so the current WPF startup path is unchanged.
/// </summary>
public sealed class WindowsSingleInstanceCoordinator : PlatformContracts.ISingleInstanceCoordinator
{
    private static readonly Lazy<WindowsSingleInstanceCoordinator> LazyInstance =
        new(() => new WindowsSingleInstanceCoordinator());

    private bool _disposed;

    private WindowsSingleInstanceCoordinator()
    {
    }

    public static WindowsSingleInstanceCoordinator Instance => LazyInstance.Value;

    public event EventHandler? ActivationRequested;

    public PlatformContracts.PlatformResult<bool> TryAcquire()
    {
        if (_disposed)
        {
            return PlatformContracts.PlatformResult<bool>.Failure(
                "single_instance.disposed",
                "The Windows single-instance coordinator has been disposed.");
        }

        try
        {
            return PlatformContracts.PlatformResult<bool>.Success(
                SingleInstanceGuard.TryAcquire());
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsSingleInstanceCoordinator: acquire failed", ex);
            return PlatformContracts.PlatformResult<bool>.Failure(
                "single_instance.acquire_failed",
                "Windows could not acquire the application mutex.");
        }
    }

    public PlatformContracts.PlatformResult SignalExistingInstance()
    {
        if (_disposed)
        {
            return PlatformContracts.PlatformResult.Failure(
                "single_instance.disposed",
                "The Windows single-instance coordinator has been disposed.");
        }

        try
        {
            SingleInstanceGuard.SignalExistingInstance();
            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsSingleInstanceCoordinator: signal failed", ex);
            return PlatformContracts.PlatformResult.Failure(
                "single_instance.signal_failed",
                "Windows could not signal the existing application instance.");
        }
    }

    public void Release()
    {
        if (_disposed) return;
        SingleInstanceGuard.Release();
    }

    internal void NotifyActivationRequested()
    {
        if (_disposed || ActivationRequested == null) return;

        foreach (EventHandler handler in ActivationRequested.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LoggingService.Error(
                    "WindowsSingleInstanceCoordinator: activation handler failed",
                    ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Release();
        ActivationRequested = null;
        _disposed = true;
    }
}
