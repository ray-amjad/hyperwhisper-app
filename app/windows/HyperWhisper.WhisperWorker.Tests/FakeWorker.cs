using System.Collections.Concurrent;
using System.Threading.Channels;
using HyperWhisper.LocalInference;
using HyperWhisper.WhisperWorker;

namespace HyperWhisper.WhisperWorker.Tests;

/// <summary>How one fake worker process answers. The defaults behave like a healthy worker.</summary>
internal sealed class FakeBehavior
{
    public bool SendReady { get; init; } = true;

    /// <summary>Handles a load or transcribe; null uses the healthy default.</summary>
    public Func<FakeProcess, WhisperWorkerCommand, Task>? Handle { get; init; }

    /// <summary>How long a shutdown takes before the fake exits.</summary>
    public TimeSpan ShutdownDelay { get; init; }

    /// <summary>A shutdown or EOF the fake ignores (it then has to be killed).</summary>
    public bool IgnoreShutdown { get; init; }

    /// <summary>Called on <c>cancel</c>; null ignores it.</summary>
    public Action<FakeProcess, WhisperWorkerCancelCommand>? OnCancel { get; init; }

    public static Task DefaultHandle(FakeProcess process, WhisperWorkerCommand command)
    {
        switch (command)
        {
            case WhisperWorkerLoadCommand load:
                process.Send(WhisperWorkerProtocol.BuildLoadOk(load.Id, "vulkan", load.GpuName, "Vulkan: fake"));
                break;
            case WhisperWorkerTranscribeCommand transcribe:
                process.Send(WhisperWorkerProtocol.BuildTranscribeOk(transcribe.Id, "hello", " hello", "en", 1));
                break;
        }
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory worker process: two channels and an exit.</summary>
internal sealed class FakeProcess : IWhisperWorkerProcess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stdin = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FakeBehavior _behavior;
    private readonly FakeLauncher _owner;
    private int _busy;
    private int _exited;

    /// <summary>
    /// Exceptions thrown inside any fake's read loop or request handler. The
    /// harness fails if this is not empty at the end, so no fault goes unseen.
    /// </summary>
    public static ConcurrentQueue<Exception> Faults { get; } = new();

    public FakeProcess(int id, FakeBehavior behavior, FakeLauncher owner)
    {
        Id = id;
        _behavior = behavior;
        _owner = owner;
        Observe(Task.Run(RunAsync));
    }

    public int Id { get; }

    public ConcurrentQueue<WhisperWorkerCommand> Commands { get; } = new();

    public bool Killed { get; private set; }

    public bool ShutdownReceived { get; private set; }

    public bool HasExited => _exit.Task.IsCompleted;

    /// <summary>Set when two loads or transcribes were ever in the worker at once.</summary>
    public bool SawOverlap { get; private set; }

    public void Send(string line)
    {
        // A reply ends the request it answers, before the client can see it.
        if (WhisperWorkerProtocol.TryParseMessage(line, out var message) && message is WhisperWorkerReply)
        {
            Interlocked.Decrement(ref _busy);
        }
        _stdout.Writer.TryWrite(line);
    }

    public void Exit()
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0) return;
        // Counted dead before anyone waiting on the exit can run.
        _owner.OnExit(this);
        _stdout.Writer.TryComplete();
        _exit.TrySetResult();
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _stdout.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (HasExited || !_stdin.Writer.TryWrite(line)) throw new IOException("The fake worker's stdin is closed.");
        return Task.CompletedTask;
    }

    public void CloseInput() => _stdin.Writer.TryComplete();

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        Killed = !HasExited || Killed;
        Exit();
    }

    public void Dispose()
    {
    }

    private static void Observe(Task task) =>
        task.ContinueWith(
            t => Faults.Enqueue(t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task RunAsync()
    {
        if (_behavior.SendReady) Send(WhisperWorkerProtocol.BuildReady(null, Id));
        await foreach (var line in _stdin.Reader.ReadAllAsync())
        {
            if (!WhisperWorkerProtocol.TryParseCommand(line, out var command, out _, out _)) continue;
            Commands.Enqueue(command!);
            switch (command)
            {
                case WhisperWorkerShutdownCommand:
                    ShutdownReceived = true;
                    if (_behavior.IgnoreShutdown) continue;
                    await Task.Delay(_behavior.ShutdownDelay);
                    Exit();
                    return;
                case WhisperWorkerCancelCommand cancel:
                    _behavior.OnCancel?.Invoke(this, cancel);
                    continue;
            }

            // Run the request off the read loop, like the real worker, so a
            // cancel can still be read while it runs.
            // A new request while an earlier one has no reply yet is an overlap.
            if (Interlocked.Increment(ref _busy) > 1) SawOverlap = true;
            Observe(Task.Run(() => (_behavior.Handle ?? FakeBehavior.DefaultHandle)(this, command!)));
        }

        // stdin EOF: the real worker exits.
        if (!_behavior.IgnoreShutdown)
        {
            await Task.Delay(_behavior.ShutdownDelay);
            Exit();
        }
    }
}

internal sealed class FakeLauncher : IWhisperWorkerLauncher
{
    private readonly Func<int, FakeBehavior> _behaviorFor;
    private readonly object _sync = new();
    private int _alive;

    public FakeLauncher(Func<int, FakeBehavior>? behaviorFor = null)
    {
        _behaviorFor = behaviorFor ?? (_ => new FakeBehavior());
    }

    public List<FakeProcess> Processes { get; } = [];

    /// <summary>The most worker processes alive at one time.</summary>
    public int PeakAlive { get; private set; }

    public Exception? FailStartWith { get; init; }

    public FakeProcess this[int index] => Processes[index];

    public IWhisperWorkerProcess Start()
    {
        if (FailStartWith is not null) throw FailStartWith;
        lock (_sync)
        {
            var index = Processes.Count;
            var process = new FakeProcess(1000 + index, _behaviorFor(index), this);
            Processes.Add(process);
            _alive++;
            PeakAlive = Math.Max(PeakAlive, _alive);
            return process;
        }
    }

    public void OnExit(FakeProcess process)
    {
        lock (_sync) _alive--;
    }
}

/// <summary>An ILocalWhisperService that records what it was asked and answers as told.</summary>
internal sealed class FakeWhisperService : ILocalWhisperService
{
    public List<LocalWhisperLoadOptions> Loads { get; } = [];
    public List<LocalWhisperRequest> Requests { get; } = [];
    public Func<LocalWhisperLoadOptions, LocalWhisperResult> LoadResult { get; set; } =
        _ => LocalWhisperResult.Success(string.Empty, "Cpu: fake");
    public Func<LocalWhisperRequest, CancellationToken, Task<LocalWhisperResult>> TranscribeResult { get; set; } =
        (_, _) => Task.FromResult(LocalWhisperResult.Success("hello", "Cpu: fake") with
        {
            UnstrippedText = " hello [BLANK_AUDIO]",
            DetectedLanguage = "en",
            SegmentCount = 2,
        });
    public bool Disposed { get; private set; }

    public bool IsLoaded { get; private set; }
    public string? Runtime => IsLoaded ? "Cpu: fake" : null;

    public Task<LocalWhisperResult> LoadAsync(LocalWhisperLoadOptions options, CancellationToken cancellationToken = default)
    {
        Loads.Add(options);
        var result = LoadResult(options);
        IsLoaded = result.IsSuccess;
        return Task.FromResult(result);
    }

    public Task<LocalWhisperResult> TranscribeAsync(LocalWhisperRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return TranscribeResult(request, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
