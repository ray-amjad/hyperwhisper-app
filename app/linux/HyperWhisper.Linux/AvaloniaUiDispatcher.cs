using Avalonia.Threading;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux;

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public async ValueTask InvokeAsync(Func<ValueTask> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckAccess()) { await action(); return; }
        await Dispatcher.UIThread.InvokeAsync(async () => await action());
    }
}
