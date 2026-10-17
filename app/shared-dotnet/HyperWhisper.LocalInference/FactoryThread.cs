using System.Collections.Concurrent;

namespace HyperWhisper.LocalInference;

/// <summary>
/// One dedicated thread that owns a factory: the factory is made on it, and every
/// <see cref="RunAsync{TResult}"/> callback runs on it too. This is how the ARM64
/// thread rule (Whisper.net's <c>CreateBuilder()</c> fails when it runs on another
/// thread than the one that created the factory) keeps one factory across many
/// requests: the old in-process code built it once per file, and a long file is
/// now one request per chunk.
/// </summary>
/// <remarks>
/// The factory is made on first use and kept. A callback that throws drops it, so
/// the next call builds a fresh one (the old code never reused a failed factory).
/// Not thread-safe for concurrent callers beyond the queue; the owner serializes.
/// </remarks>
internal sealed class FactoryThread<TFactory> : IDisposable
    where TFactory : class, IDisposable
{
    private readonly Func<TFactory> _create;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private TFactory? _factory;
    private int _factoriesBuilt;
    private bool _disposed;

    public FactoryThread(Func<TFactory> create, string name)
    {
        _create = create;
        _thread = new Thread(Drain) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>How many factories this thread has made (a test seam).</summary>
    public int FactoriesBuilt => Volatile.Read(ref _factoriesBuilt);

    /// <summary>Whether the thread holds a factory now (a test seam; read between calls).</summary>
    public bool HoldsFactory => Volatile.Read(ref _factory) is not null;

    /// <summary>The managed id of the owning thread (a test seam).</summary>
    public int ThreadId => _thread.ManagedThreadId;

    public Task<TResult> RunAsync<TResult>(Func<TFactory, TResult> use)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                if (_factory is null)
                {
                    _factory = _create();
                    Interlocked.Increment(ref _factoriesBuilt);
                }
                completion.SetResult(use(_factory));
            }
            catch (Exception exception)
            {
                TryDropFactory();
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    /// <summary>Disposes the kept factory, on its own thread; the next call builds a new one.</summary>
    public Task ResetAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                DropFactory();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    /// <summary>Disposes the factory on its thread and ends the thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work.Add(TryDropFactory);
        _work.CompleteAdding();
        _thread.Join();
        _work.Dispose();
    }

    private void DropFactory()
    {
        var factory = _factory;
        _factory = null;
        factory?.Dispose();
    }

    // On the thread, where nothing may escape: an unhandled throw there ends the process.
    private void TryDropFactory()
    {
        try
        {
            DropFactory();
        }
        catch
        {
        }
    }

    private void Drain()
    {
        foreach (var item in _work.GetConsumingEnumerable())
        {
            item();
        }
    }
}
