namespace HyperWhisper.WhisperWorker;

/// <summary>The model the worker should hold, and how to load it.</summary>
public sealed record WhisperWorkerModel(
    string Path,
    int GpuDevice = 0,
    bool FlashAttention = true,
    string? GpuName = null);

/// <summary>What a successful <c>load</c> reported.</summary>
public sealed record WhisperWorkerLoadResult(string Runtime, string? Gpu, string? Info);

/// <summary>
/// One transcription. <see cref="RawText"/> is the segment text exactly as
/// Whisper wrote it; <see cref="Text"/> has the non-speech markers stripped.
/// A caller that splits a file into chunks joins the raw texts and strips once.
/// </summary>
public sealed record WhisperWorkerTranscript(string Text, string RawText, string? Language, int SegmentCount)
{
    /// <summary>
    /// How long this request spent starting the worker and loading the model
    /// before it could decode: zero when the worker was already up with the
    /// model, and both loads when a lost worker was restarted (#1932).
    /// </summary>
    public TimeSpan StartAndLoad { get; init; }
}

/// <summary>The client's timings. Only tests change them; the app uses the defaults.</summary>
public sealed record WhisperWorkerClientTimings
{
    /// <summary>D2 (issue #1194): a fixed five minutes, with no user setting.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>The Parakeet daemon's budget for its ready line.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a shutdown waits for the exit before it kills the worker.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a cancel waits for the worker's answer before it kills the worker.</summary>
    public TimeSpan CancelTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The watchdog on a <c>load</c> reply. A large model from a slow disk loads
    /// in about a minute; a worker silent for this long is hung (a driver stuck
    /// in whisper_init) and is killed, so it cannot hold every later request.
    /// </summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The <c>transcribe</c> watchdog's floor, for short audio.</summary>
    public TimeSpan TranscribeTimeoutFloor { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The <c>transcribe</c> watchdog per second of audio, on top of the floor.
    /// Ten times real time: a large model on a slow CPU (ARM64 runs on the CPU)
    /// with temperature fallback stays well inside it, so only a hung worker
    /// trips it. A 5-minute chunk gets 52 minutes.
    /// </summary>
    public double TranscribeTimeoutPerAudioSecond { get; init; } = 10;

    /// <summary>The watchdog for a transcribe of <paramref name="audio"/>.</summary>
    public TimeSpan TranscribeTimeoutFor(TimeSpan audio) =>
        TranscribeTimeoutFloor + TimeSpan.FromSeconds(Math.Max(0, audio.TotalSeconds) * TranscribeTimeoutPerAudioSecond);
}

/// <summary>
/// Drives the out-of-process Whisper worker (issue #1194).
///
/// Whisper.net's Vulkan backend keeps the GPU awake for as long as the process
/// that used it lives, so the model runs in <c>HyperWhisper.WhisperWorker.exe</c>
/// and that process exits after <see cref="WhisperWorkerClientTimings.IdleTimeout"/>
/// with no work. This class:
/// <list type="bullet">
/// <item>runs one request at a time: every start, load, transcribe and shutdown
/// holds <c>_gate</c>, so an idle shutdown and a new request never interleave;
/// a request that arrives during a shutdown waits for the exit and then starts
/// a new worker;</item>
/// <item>starts the worker and loads the selected model on demand, never on its own;</item>
/// <item>restarts once and retries when the worker dies mid-request;</item>
/// <item>arms the idle timer when the last request ends and nothing holds the
/// worker warm (<see cref="HoldWarm"/>, used while a Whisper recording runs).</item>
/// </list>
/// </summary>
public sealed class WhisperWorkerClient : IAsyncDisposable
{
    private readonly IWhisperWorkerLauncher _launcher;
    private readonly WhisperWorkerClientTimings _timings;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;

    /// <summary>Serializes every operation that touches the worker.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards the counters and the idle timer below.</summary>
    private readonly object _sync = new();

    private Session? _session;
    private WhisperWorkerModel? _model;
    private long _nextId;
    private int _holds;
    private int _active;
    private long _idleGeneration;
    private CancellationTokenSource? _idleDelay;
    private bool _disposed;

    public WhisperWorkerClient(
        IWhisperWorkerLauncher launcher,
        Action<string>? info = null,
        Action<string>? warn = null,
        WhisperWorkerClientTimings? timings = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _info = info ?? (_ => { });
        _warn = warn ?? _info;
        _timings = timings ?? new WhisperWorkerClientTimings();
    }

    /// <summary>The selected model, or null. Selection does not start the worker.</summary>
    public WhisperWorkerModel? Model => Volatile.Read(ref _model);

    /// <summary>True while a worker process is up.</summary>
    public bool IsRunning => Volatile.Read(ref _session) is { HasExited: false };

    /// <summary>The running worker's process id, or null.</summary>
    public int? WorkerProcessId => Volatile.Read(ref _session) is { HasExited: false } session ? session.ProcessId : null;

    /// <summary>What the running worker's last successful load reported.</summary>
    public WhisperWorkerLoadResult? LoadResult => Volatile.Read(ref _session)?.LoadResult;

    /// <summary>How many worker processes this client has started.</summary>
    public int StartCount { get; private set; }

    /// <summary>How many workers the idle timer has shut down.</summary>
    public int IdleShutdownCount { get; private set; }

    // =====================================================================
    // PUBLIC OPERATIONS
    // =====================================================================

    /// <summary>
    /// Records the model to serve. Never starts the worker (D5). A running worker
    /// that holds another model is shut down, and its exit awaited, so two
    /// processes never hold a model at once. Null clears the selection and
    /// stops the worker.
    /// </summary>
    public async Task SelectModelAsync(WhisperWorkerModel? model, CancellationToken cancellationToken = default)
    {
        await RunExclusiveAsync(async () =>
        {
            Volatile.Write(ref _model, model);
            var session = _session;
            if (session is not null && (model is null || !Equals(session.LoadedModel, model)))
            {
                _info(model is null
                    ? "WhisperWorkerClient: Model cleared; stopping the worker"
                    : "WhisperWorkerClient: Another model selected; stopping the worker that holds the old one");
                await ShutdownSessionAsync(session).ConfigureAwait(false);
            }
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts the worker if it is down and loads the selected model.</summary>
    public Task<WhisperWorkerLoadResult> WarmUpAsync(CancellationToken cancellationToken = default) =>
        RunRequestAsync((session, _, _) => Task.FromResult(session.LoadResult!), cancellationToken);

    /// <summary>
    /// Transcribes one 16 kHz mono 16-bit WAV. Starts and loads the worker when it
    /// is down. Cancelling sends <c>cancel</c>; a worker that does not answer
    /// within <see cref="WhisperWorkerClientTimings.CancelTimeout"/> is killed.
    /// A worker that does not answer within the watchdog for
    /// <paramref name="audioDuration"/> (<see cref="WhisperWorkerClientTimings.TranscribeTimeoutFor"/>)
    /// is killed and the request fails as <c>timeout</c>, with no retry.
    /// </summary>
    /// <param name="audioDuration">The chunk's length; null reads it from the WAV's size.</param>
    public Task<WhisperWorkerTranscript> TranscribeAsync(
        string wavPath,
        string? language,
        WhisperWorkerTranscribeOptions options,
        CancellationToken cancellationToken = default,
        TimeSpan? audioDuration = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(wavPath);
        ArgumentNullException.ThrowIfNull(options);
        var timeout = _timings.TranscribeTimeoutFor(audioDuration ?? WavDuration(wavPath));
        return RunRequestAsync(
            async (session, startAndLoad, token) =>
                await TranscribeOnAsync(session, wavPath, language, options, timeout, token).ConfigureAwait(false)
                    with { StartAndLoad = startAndLoad },
            cancellationToken);
    }

    /// <summary>The length of a WhisperWav file from its size; zero (the floor alone) if it cannot be read.</summary>
    private static TimeSpan WavDuration(string wavPath)
    {
        try
        {
            var dataBytes = Math.Max(0, new FileInfo(wavPath).Length - 44);
            return TimeSpan.FromSeconds(dataBytes / (2.0 * WhisperWav.SampleRate));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>Stops the worker now, keeping the selection. The next request starts it again.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            if (_session is { } session)
            {
                await ShutdownSessionAsync(session).ConfigureAwait(false);
            }
            return 0;
        }, cancellationToken);

    /// <summary>
    /// Keeps the worker from idling out until the returned handle is disposed.
    /// A Whisper-mode recording holds one, so the worker it warmed up is still
    /// there when the recording ends.
    /// </summary>
    public IDisposable HoldWarm()
    {
        lock (_sync)
        {
            _holds++;
            CancelIdleLocked();
        }
        return new Hold(this);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CancelIdleLocked();
        }
        // A request that holds the gate ends on its own watchdog at the latest.
        // Dispose does not wait that long: after ShutdownTimeout it kills the
        // worker, which fails the running request (no retry after dispose) and
        // frees the gate.
        if (!await _gate.WaitAsync(_timings.ShutdownTimeout).ConfigureAwait(false))
        {
            _warn("WhisperWorkerClient: A request still runs at dispose; killing the worker");
            Volatile.Read(ref _session)?.Kill();
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        try
        {
            if (_session is { } session)
            {
                await ShutdownSessionAsync(session).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // =====================================================================
    // REQUEST PIPELINE
    // =====================================================================

    /// <param name="operation">
    /// The request on a loaded worker. It is passed the time this request spent
    /// so far in <see cref="EnsureLoadedAsync"/> (worker start and model load).
    /// </param>
    private async Task<T> RunRequestAsync<T>(
        Func<Session, TimeSpan, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(async () =>
        {
            var startAndLoad = TimeSpan.Zero;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // A warm worker with the model costs nothing here, and counts as zero.
                    var warm = _session is { HasExited: false } current
                        && Equals(current.LoadedModel, Volatile.Read(ref _model));
                    var loading = System.Diagnostics.Stopwatch.StartNew();
                    Session session;
                    try
                    {
                        session = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!warm) startAndLoad += loading.Elapsed;
                    }
                    return await operation(session, startAndLoad, cancellationToken).ConfigureAwait(false);
                }
                catch (WorkerLostException lost)
                {
                    await DiscardSessionAsync().ConfigureAwait(false);
                    // A cancel that raced the exit is a cancel: no new worker for it.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attempt > 0 || IsDisposed)
                    {
                        throw new WhisperWorkerException(lost.Code, lost.Message, lost);
                    }
                    _warn($"WhisperWorkerClient: {lost.Message}; restarting the worker once and retrying");
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds the gate for <paramref name="body"/>, with the idle timer off while it
    /// waits and runs, and armed again after when nothing else needs the worker.
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active++;
            CancelIdleLocked();
        }
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Queued behind the gate while DisposeAsync ran: the client is shut
                // down, and a worker started now would never be stopped.
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return await body().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            lock (_sync)
            {
                _active--;
                ArmIdleLocked();
            }
        }
    }

    private async Task<Session> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var model = Volatile.Read(ref _model)
            ?? throw new WhisperWorkerException(WhisperWorkerErrorCodes.NotLoaded, "No Whisper model is selected.");

        if (_session is { HasExited: true })
        {
            // It died between requests (or idled out on its own): just start anew.
            _info("WhisperWorkerClient: The worker is no longer running; starting a new one");
            await DiscardSessionAsync().ConfigureAwait(false);
        }

        if (_session is null)
        {
            await StartSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var session = _session!;
        if (Equals(session.LoadedModel, model))
        {
            return session;
        }

        var id = Interlocked.Increment(ref _nextId);
        var line = WhisperWorkerProtocol.BuildCommand(new WhisperWorkerLoadCommand(
            id, model.Path, model.GpuDevice, model.FlashAttention, model.GpuName));
        _info($"WhisperWorkerClient: Loading {System.IO.Path.GetFileName(model.Path)} in the worker (pid {session.ProcessId})");

        WhisperWorkerReply reply;
        var replyTask = await session.SendRequestAsync(id, line).ConfigureAwait(false);
        try
        {
            reply = await replyTask.WaitAsync(_timings.LoadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A load cannot be cancelled inside Whisper.net. Stop the worker rather
            // than leave a half-loaded one that the next request would wait on.
            _info("WhisperWorkerClient: Cancelled during the model load; stopping the worker");
            await DiscardSessionAsync().ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            throw await HungAsync("load", _timings.LoadTimeout).ConfigureAwait(false);
        }

        if (!reply.Ok)
        {
            var code = reply.Code ?? WhisperWorkerErrorCodes.Protocol;
            if (code == WhisperWorkerErrorCodes.Runtime)
            {
                // Whisper.net caches a failed native load for the life of the
                // process, so this worker can never load. The next try starts fresh.
                await ShutdownSessionAsync(session).ConfigureAwait(false);
            }
            throw new WhisperWorkerException(code, reply.Message ?? "The Whisper model could not be loaded.");
        }

        session.LoadedModel = model;
        session.LoadResult = new WhisperWorkerLoadResult(reply.Runtime ?? "unknown", reply.Gpu, reply.Info);
        _info($"WhisperWorkerClient: Model loaded (runtime {session.LoadResult.Runtime}"
              + (reply.Gpu is null ? ")" : $", GPU {reply.Gpu})"));
        return session;
    }

    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        // The one place a worker starts: never after dispose, or nothing stops it.
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var process = _launcher.Start(); // throws WhisperWorkerException(start_failed)
        StartCount++;
        var session = new Session(process, _info, _warn);
        Volatile.Write(ref _session, session);
        _info($"WhisperWorkerClient: Started the worker (pid {session.ProcessId})");

        try
        {
            var ready = await session.Ready.WaitAsync(_timings.ReadyTimeout, cancellationToken).ConfigureAwait(false);
            _info($"WhisperWorkerClient: Worker ready (pid {ready.Pid})");
        }
        catch (TimeoutException)
        {
            throw new WorkerLostException(
                WhisperWorkerErrorCodes.Timeout,
                $"The Whisper worker did not report ready within {_timings.ReadyTimeout.TotalSeconds:0} s");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DiscardSessionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<WhisperWorkerTranscript> TranscribeOnAsync(
        Session session,
        string wavPath,
        string? language,
        WhisperWorkerTranscribeOptions options,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var line = WhisperWorkerProtocol.BuildCommand(
            new WhisperWorkerTranscribeCommand(id, wavPath, language, options));
        var replyTask = await session.SendRequestAsync(id, line).ConfigureAwait(false);

        WhisperWorkerReply reply;
        try
        {
            reply = await replyTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelInFlightAsync(session, id, replyTask).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            throw await HungAsync("transcribe", timeout).ConfigureAwait(false);
        }

        if (reply.Ok)
        {
            var raw = reply.RawText ?? reply.Text ?? string.Empty;
            return new WhisperWorkerTranscript(reply.Text ?? string.Empty, raw, reply.Language, reply.SegmentCount);
        }

        var code = reply.Code ?? WhisperWorkerErrorCodes.Protocol;
        if (code == WhisperWorkerErrorCodes.NotLoaded)
        {
            session.LoadedModel = null;
        }
        else if (code == WhisperWorkerErrorCodes.Runtime)
        {
            await ShutdownSessionAsync(session).ConfigureAwait(false);
        }
        throw new WhisperWorkerException(code, reply.Message ?? "Local Whisper transcription failed.");
    }

    /// <summary>
    /// Asks the worker to drop request <paramref name="target"/>, and waits for it
    /// to answer so the next request does not queue behind a still-running
    /// decode. A worker that stays silent, or dies, is killed.
    /// </summary>
    private async Task CancelInFlightAsync(Session session, long target, Task<WhisperWorkerReply> replyTask)
    {
        _info($"WhisperWorkerClient: Cancelling request {target}");
        try
        {
            var cancelId = Interlocked.Increment(ref _nextId);
            await session.WriteAsync(WhisperWorkerProtocol.BuildCommand(
                new WhisperWorkerCancelCommand(cancelId, target))).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _warn($"WhisperWorkerClient: Could not send cancel ({exception.GetType().Name})");
        }

        try
        {
            await replyTask.WaitAsync(_timings.CancelTimeout).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            _warn($"WhisperWorkerClient: The worker did not answer a cancel within {_timings.CancelTimeout.TotalSeconds:0} s; killing it");
        }
        catch (WorkerLostException)
        {
            _warn("WhisperWorkerClient: The worker exited while cancelling");
        }
        await DiscardSessionAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A live worker that did not answer within the watchdog is hung: kill it so
    /// the gate frees and the next request starts fresh. The error is
    /// <c>timeout</c> and is not retried (a retry would likely hang again).
    /// </summary>
    private async Task<WhisperWorkerException> HungAsync(string request, TimeSpan timeout)
    {
        _warn($"WhisperWorkerClient: The worker did not answer {request} within {timeout.TotalSeconds:0} s; killing it");
        await DiscardSessionAsync().ConfigureAwait(false);
        return new WhisperWorkerException(
            WhisperWorkerErrorCodes.Timeout,
            $"The Whisper worker did not answer {request} within {timeout.TotalSeconds:0} s");
    }

    // =====================================================================
    // WORKER LIFETIME (call with _gate held)
    // =====================================================================

    private async Task ShutdownSessionAsync(Session session)
    {
        if (!ReferenceEquals(_session, session)) return;
        if (!session.HasExited)
        {
            // Before the write: the worker can exit before CloseInput below runs.
            session.ExpectExit();
            try
            {
                await session.WriteAsync(WhisperWorkerProtocol.BuildCommand(new WhisperWorkerShutdownCommand()))
                    .ConfigureAwait(false);
            }
            catch
            {
                // The worker's stdin is gone; closing it below is the same signal.
            }
            session.CloseInput();
            try
            {
                await session.WaitForExitAsync(_timings.ShutdownTimeout).ConfigureAwait(false);
                _info($"WhisperWorkerClient: Worker exited (pid {session.ProcessId})");
            }
            catch (TimeoutException)
            {
                _warn($"WhisperWorkerClient: The worker did not exit within {_timings.ShutdownTimeout.TotalSeconds:0} s; killing it");
                session.Kill();
            }
        }
        Volatile.Write(ref _session, null);
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DiscardSessionAsync()
    {
        var session = _session;
        if (session is null) return;
        Volatile.Write(ref _session, null);
        session.Kill();
        await session.DisposeAsync().ConfigureAwait(false);
    }

    // =====================================================================
    // IDLE TIMER (call with _sync held)
    // =====================================================================

    private bool IsDisposed
    {
        get
        {
            lock (_sync) return _disposed;
        }
    }

    private void CancelIdleLocked()
    {
        _idleGeneration++;
        _idleDelay?.Cancel();
        _idleDelay?.Dispose();
        _idleDelay = null;
    }

    private void ArmIdleLocked()
    {
        if (_disposed || _holds > 0 || _active > 0) return;
        CancelIdleLocked();
        var generation = _idleGeneration;
        var delay = new CancellationTokenSource();
        _idleDelay = delay;
        _ = IdleAfterAsync(generation, delay.Token);
    }

    private async Task IdleAfterAsync(long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(_timings.IdleTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            lock (_sync)
            {
                // A request, a hold or a newer timer came in while this one slept
                // or waited on the gate: it is no longer the last word.
                if (generation != _idleGeneration || _holds > 0 || _active > 0 || _disposed) return;
            }
            if (_session is { } session)
            {
                _info($"WhisperWorkerClient: Idle for {_timings.IdleTimeout.TotalMinutes:0.##} min; stopping the worker");
                IdleShutdownCount++;
                await ShutdownSessionAsync(session).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _warn($"WhisperWorkerClient: Idle shutdown failed ({exception.GetType().Name}: {exception.Message})");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseHold()
    {
        lock (_sync)
        {
            _holds--;
            ArmIdleLocked();
        }
    }

    private sealed class Hold(WhisperWorkerClient owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.ReleaseHold();
        }
    }

    // =====================================================================
    // ONE WORKER PROCESS
    // =====================================================================

    /// <summary>The worker exited, or never said ready. Retried once by the caller.</summary>
    private sealed class WorkerLostException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly IWhisperWorkerProcess _process;
        private readonly Action<string> _log;
        private readonly Action<string> _warn;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly TaskCompletionSource<WhisperWorkerReady> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _pendingLock = new();
        private readonly Task _reader;
        private long _pendingId;
        private TaskCompletionSource<WhisperWorkerReply>? _pending;

        /// <summary>Set once the client asks the worker to stop: an exit after it is expected.</summary>
        private int _stopRequested;

        public Session(IWhisperWorkerProcess process, Action<string> log, Action<string> warn)
        {
            _process = process;
            _log = log;
            _warn = warn;
            ProcessId = process.Id;
            _ready.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _reader = Task.Run(ReadLoopAsync);
        }

        public int ProcessId { get; }

        public Task<WhisperWorkerReady> Ready => _ready.Task;

        public bool HasExited => _exited.Task.IsCompleted || _process.HasExited;

        public WhisperWorkerModel? LoadedModel { get; set; }

        public WhisperWorkerLoadResult? LoadResult { get; set; }

        /// <summary>
        /// Registers request <paramref name="id"/> and writes it. Returns the task of
        /// its reply, which faults with <see cref="WorkerLostException"/> if the
        /// worker exits first.
        /// </summary>
        public async Task<Task<WhisperWorkerReply>> SendRequestAsync(long id, string line)
        {
            var pending = new TaskCompletionSource<WhisperWorkerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            // A caller that stopped waiting (cancel, watchdog, failed write) leaves
            // this task behind, and the reader faults it with Lost() when the worker
            // exits. Observe that here so it never surfaces as an
            // UnobservedTaskException (which the app logs as an error).
            _ = pending.Task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            lock (_pendingLock)
            {
                if (_exited.Task.IsCompleted) throw Lost();
                _pendingId = id;
                _pending = pending;
            }
            try
            {
                await WriteAsync(line).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                throw Lost();
            }
            return pending.Task;
        }

        public async Task WriteAsync(string line)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _process.WriteLineAsync(line, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Marks the exit that follows as asked for (shutdown, stdin close or kill).</summary>
        public void ExpectExit() => Volatile.Write(ref _stopRequested, 1);

        public void CloseInput()
        {
            ExpectExit();
            _process.CloseInput();
        }

        public async Task WaitForExitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException();
            }
        }

        public void Kill()
        {
            ExpectExit();
            _process.Kill();
        }

        public async ValueTask DisposeAsync()
        {
            CloseInput();
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _process.Kill();
            }
            _process.Dispose();
        }

        private WorkerLostException Lost() => new(
            WhisperWorkerErrorCodes.Crashed,
            $"The Whisper worker (pid {ProcessId}) exited during a request");

        private async Task ReadLoopAsync()
        {
            try
            {
                string? line;
                while ((line = await _process.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) != null)
                {
                    if (!WhisperWorkerProtocol.TryParseMessage(line, out var message))
                    {
                        // Something in the worker printed to stdout. Log its size, not
                        // its words: the log ships in Export Diagnostics (#1645).
                        _log($"[whisper-worker] Ignored a non-protocol stdout line ({line.Length} chars)");
                        continue;
                    }

                    switch (message)
                    {
                        case WhisperWorkerReady ready:
                            _ready.TrySetResult(ready);
                            break;
                        case WhisperWorkerReply reply:
                            TaskCompletionSource<WhisperWorkerReply>? match = null;
                            lock (_pendingLock)
                            {
                                if (_pending is not null && _pendingId == reply.Id)
                                {
                                    match = _pending;
                                    _pending = null;
                                }
                            }
                            if (match is null)
                            {
                                _log($"[whisper-worker] Ignored a reply to request {reply.Id}, which is no longer waiting");
                            }
                            match?.TrySetResult(reply);
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                _log($"[whisper-worker] stdout reader stopped: {exception.GetType().Name}");
            }
            finally
            {
                TaskCompletionSource<WhisperWorkerReply>? orphan;
                long orphanId;
                lock (_pendingLock)
                {
                    _exited.TrySetResult();
                    orphan = _pending;
                    orphanId = _pendingId;
                    _pending = null;
                }
                // A shutdown the client asked for logs its own "Worker exited"
                // line. This one is a worker that left unasked: killed from
                // outside or crashed, so the log tells it from a clean exit (#1932).
                if (Volatile.Read(ref _stopRequested) == 0)
                {
                    _warn(orphan is null
                        ? $"WhisperWorkerClient: The worker (pid {ProcessId}) exited while idle without being asked to stop (killed or crashed)"
                        : $"WhisperWorkerClient: The worker (pid {ProcessId}) exited during request {orphanId} without being asked to stop (killed or crashed)");
                }
                orphan?.TrySetException(Lost());
                _ready.TrySetException(Lost());
            }
        }
    }
}
