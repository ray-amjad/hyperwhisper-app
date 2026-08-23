using System.Windows;
using System.Windows.Threading;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

public sealed class WpfUiDispatcher : PlatformContracts.IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher() : this(System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { }
    internal WpfUiDispatcher(Dispatcher dispatcher)
        => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public async ValueTask InvokeAsync(
        Func<ValueTask> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("The WPF dispatcher is shutting down.");
        if (_dispatcher.CheckAccess())
        {
            await action();
            return;
        }

        var dispatched = await _dispatcher.InvokeAsync(
            action, DispatcherPriority.Normal, cancellationToken).Task;
        await dispatched;
    }
}
