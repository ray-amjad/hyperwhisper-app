namespace HyperWhisper.Linux;

/// <summary>The hourly storage-maintenance loop on its one PeriodicTimer. A PeriodicTimer allows one
/// pending WaitForNextTickAsync, so a second start returns the running loop instead of faulting a new
/// one (#833). A failed tick is reported and the loop keeps ticking; only cancellation ends it.</summary>
internal sealed class LinuxStorageMaintenanceLoop(
    PeriodicTimer timer, Func<CancellationToken, Task> tick, Func<Exception, Task> report)
{
    internal Task? Running { get; private set; }

    internal Task EnsureStarted(CancellationToken cancellationToken) => Running ??= RunAsync(cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await tick(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                { await report(exception).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
