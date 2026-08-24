using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

/// <summary>The Avalonia composition root supplies its UI SynchronizationContext.</summary>
public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext _context;
    public SynchronizationContextUiDispatcher(SynchronizationContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));
    public bool CheckAccess() => ReferenceEquals(SynchronizationContext.Current, _context);
    public void Post(Action action) { ArgumentNullException.ThrowIfNull(action); _context.Post(static value => ((Action)value!).Invoke(), action); }
    public async ValueTask InvokeAsync(Func<ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action); cancellationToken.ThrowIfCancellationRequested();
        if (CheckAccess()) { await action().ConfigureAwait(false); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _context.Post(async value =>
        {
            var tuple = ((Func<ValueTask> Action, TaskCompletionSource Completion))value!;
            if (tuple.Completion.Task.IsCompleted) return;
            try { await tuple.Action().ConfigureAwait(false); tuple.Completion.TrySetResult(); }
            catch (Exception exception) { tuple.Completion.TrySetException(exception); }
        }, (action, completion));
        await completion.Task.ConfigureAwait(false);
    }
}
