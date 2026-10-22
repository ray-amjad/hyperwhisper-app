// PARAKEET TRANSCRIPTION SERVICE — LIFECYCLE
// Shared-daemon housekeeping: the idle reload and its lease (#1608), the pending-request
// count, and the deferred unload of a busy daemon (#1544).

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    /// <summary>
    /// Makes the daemon fit <paramref name="modelId"/> + <paramref name="language"/> for a
    /// caller that shares it with someone else's job — the Local API, a History retry,
    /// onboarding's Try It — without ending that job (#1608), and returns a LEASE the
    /// caller holds until its own transcription has ended.
    /// <see cref="InitializeAsync"/> is the user's own mode switch: it cancels whatever is
    /// in flight. This instead waits, honouring <paramref name="cancellationToken"/>, until
    /// it holds the transcription lock with nobody else counted in (a request inside
    /// <see cref="TranscribeAsync"/>, running, queued or auto-restarting, or another
    /// caller's lease; a caller-cancel drain holds the lock, so it is waited out too). It
    /// then reloads while still holding the lock, so no request can start on the old
    /// daemon between the wait and the teardown.
    /// <para>
    /// The reload and the transcription it is for are ONE reserved unit: the lease counts
    /// the caller as a pending request from the moment the model fits until it is
    /// disposed, so a second waiting reload (another model) cannot load its model in the
    /// gap before the caller's <see cref="TranscribeAsync"/> (review round 1). When the
    /// warm daemon already fits, the lease is taken at once without the lock.
    /// </para>
    /// <para>
    /// While it reloads, the reservation is visible to teardown like an active request:
    /// a GUI <see cref="DisposeModel"/> / <see cref="InitializeAsync"/> cancels it with no
    /// grace, the reload stops only the daemon it started and releases the lock, and this
    /// throws <see cref="TranscriptionErrorCode.Cancelled"/>. The user's deliberate mode
    /// switch wins and never waits out a 30–90 s daemon start on the UI thread.
    /// </para>
    /// </summary>
    /// <param name="language">The init form: null for auto-detect, as for InitializeAsync.</param>
    /// <returns>The lease; dispose it after the transcription. Reloaded is false when no reload was needed.</returns>
    public async Task<ModelLease> ReloadWhenIdleAsync(
        string modelId,
        string modelDirectory,
        string? language,
        CancellationToken cancellationToken)
    {
        var loggedWait = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The warm daemon fits: count the caller in and go, as before #1608 a warm
            // request did not wait for the job in flight either (it queues on the lock).
            if (TryLeaseWarmDaemon(modelId, language) is { } warmLease)
            {
                return warmLease;
            }

            await _transcriptionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-checked under the lock: a reload queued ahead of this one may have
                // loaded this very model already.
                if (TryLeaseWarmDaemon(modelId, language) is { } lease)
                {
                    return lease;
                }

                if (TryReserveIdleReload() is { } reloadTeardownCts)
                {
                    return await RunIdleReloadAsync(modelDirectory, language, reloadTeardownCts).ConfigureAwait(false);
                }
            }
            finally
            {
                _transcriptionLock.Release();
            }

            if (!loggedWait)
            {
                LoggingService.Info("ParakeetTranscriptionService: Waiting for the other transcription to finish before reloading the model");
                loggedWait = true;
            }

            await Task.Delay(IdleReloadPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The reserved reload, with the transcription lock held and the caller already counted
    /// in. On success the count passes to the returned lease; on any failure it is dropped.
    /// The caller's own token is not linked in: like the GUI's load, a load once started is
    /// finished, so a disconnecting client does not leave the shared daemon half-started.
    /// </summary>
    private async Task<ModelLease> RunIdleReloadAsync(
        string modelDirectory,
        string? language,
        CancellationTokenSource reloadTeardownCts)
    {
        var leased = false;
        try
        {
            try
            {
                await InitializeCoreAsync(
                    modelDirectory,
                    language,
                    advanceTeardownGeneration: false,
                    transcriptionLockHeld: true,
                    idleReloadCancellation: reloadTeardownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (reloadTeardownCts.IsCancellationRequested)
            {
                LoggingService.Info("ParakeetTranscriptionService: A model teardown cancelled the idle reload");
                throw CreateTeardownCancelledException(ex);
            }

            if (!EndIdleReload(reloadTeardownCts))
            {
                // The teardown asked after READY arrived. Its DisposeModel is waiting on the
                // lock to stop this daemon anyway; stopping it here (still under the lock, so
                // it is this reload's own) spares that wait its graceful-exit pause, and the
                // caller must not run on a daemon the user just unloaded.
                LoggingService.Info("ParakeetTranscriptionService: A model teardown overtook the idle reload; stopping its daemon");
                StopDaemonInstance(_daemonProcess);
                throw CreateTeardownCancelledException(null);
            }

            leased = true;
            return new ModelLease(this, reloaded: true);
        }
        finally
        {
            EndIdleReload(reloadTeardownCts);
            if (!leased)
            {
                ExitRequest();
            }
        }
    }

    /// <summary>
    /// The caller's hold on the model it asked <see cref="ReloadWhenIdleAsync"/> for. While
    /// it is held, the caller counts as a pending request, so no other idle reload replaces
    /// the model before the caller's transcription has run. Dispose it once that
    /// transcription has ended (success or failure); disposing twice is harmless.
    /// </summary>
    public sealed class ModelLease : IDisposable
    {
        private ParakeetTranscriptionService? _owner;

        internal ModelLease(ParakeetTranscriptionService owner, bool reloaded)
        {
            _owner = owner;
            Reloaded = reloaded;
        }

        /// <summary>True when this call respawned the daemon; false when the warm one fit.</summary>
        public bool Reloaded { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitRequest();
    }

    /// <summary>
    /// A lease without the lock when the warm daemon fits and no idle reload is running.
    /// Checked and counted in one step under <see cref="_drainSync"/>, where an idle reload
    /// registers itself, so a lease is never handed out on a model being replaced.
    /// </summary>
    private ModelLease? TryLeaseWarmDaemon(string modelId, string? language)
    {
        lock (_drainSync)
        {
            if (_idleReloadTeardownCts != null || NeedsReload(modelId, language))
            {
                return null;
            }

            _pendingRequests++;
        }

        return new ModelLease(this, reloaded: false);
    }

    /// <summary>
    /// Counts a request in and returns the teardown generation it belongs to, in one step
    /// under <see cref="_drainSync"/> so it cannot interleave with <see cref="TryReserveIdleReload"/>.
    /// </summary>
    private long EnterRequest()
    {
        lock (_drainSync)
        {
            _pendingRequests++;
            return CurrentTeardownGeneration;
        }
    }

    private void ExitRequest()
    {
        bool unloadNow;
        lock (_drainSync)
        {
            _pendingRequests--;
            unloadNow = _pendingRequests == 0 && _unloadWhenDrained;
        }

        if (unloadNow)
        {
            // Off this thread: the last caller out may still hold the transcription lock
            // (TranscribeAsync), or be a lease disposed on the Local API request thread.
            // ast-grep-ignore: no-discarded-task-run -- the last request or lease out may hold the transcription lock the unload needs, so it cannot run inline; UnloadWhenDrainedAsync catches and logs its own failures
            _ = Task.Run(UnloadWhenDrainedAsync);
        }
    }

    /// <summary>
    /// A housekeeping unload found the daemon busy (#1544): unload it once the last request
    /// or lease counted in has left. With nobody counted in, the lock holder is a caller-cancel
    /// drain, a teardown or a mode switch; the unload then waits for that lock instead. A
    /// deliberate teardown or switch clears the arm, and <paramref name="stillWanted"/> keeps
    /// a daemon the selected mode uses again.
    /// </summary>
    private void ArmUnloadWhenDrained(Func<bool>? stillWanted)
    {
        bool waitForLock;
        lock (_drainSync)
        {
            ArmUnloadWhenDrainedLocked(stillWanted);
            waitForLock = _pendingRequests == 0;
        }

        if (waitForLock)
        {
            // ast-grep-ignore: no-discarded-task-run -- the caller is a mode write on the UI or Local API request thread, and the lock it could not take may be held for minutes; UnloadWhenDrainedAsync catches and logs its own failures
            _ = Task.Run(UnloadWhenDrainedAsync);
        }
    }

    /// <summary>Arms the deferred unload. The caller holds <see cref="_drainSync"/>.</summary>
    private void ArmUnloadWhenDrainedLocked(Func<bool>? stillWanted)
    {
        _unloadWhenDrained = true;
        _unloadWhenDrainedStillWanted = stillWanted;
        _unloadArmCount++;
    }

    /// <summary>
    /// The deferred half of <see cref="TryDisposeModelIfIdle"/>. Re-checked under the lock:
    /// a request that counted in meanwhile keeps the daemon (and re-triggers this when it
    /// leaves), and a deliberate DisposeModel / InitializeAsync in between cleared the flag.
    /// </summary>
    private async Task UnloadWhenDrainedAsync()
    {
        try
        {
            await _transcriptionLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            Func<bool>? stillWanted;
            int armCount;
            lock (_drainSync)
            {
                if (!_unloadWhenDrained || _pendingRequests > 0)
                {
                    return;
                }

                if (!IsInitialized)
                {
                    // A teardown that held the lock already stopped the daemon.
                    _unloadWhenDrained = false;
                    _unloadWhenDrainedStillWanted = null;
                    return;
                }

                stillWanted = _unloadWhenDrainedStillWanted;
                armCount = _unloadArmCount;
            }

            // Outside _drainSync: the check reads app state, and must not run under a lock
            // ExitRequest takes. The transcription lock held here keeps a new job off the
            // daemon meanwhile, and the re-check below catches a request counted in since.
            if (stillWanted != null && !stillWanted())
            {
                LoggingService.Info("ParakeetTranscriptionService: Keeping the daemon after its transcriptions finished; the selected mode uses it again");
                lock (_drainSync)
                {
                    // A mode write that armed again while the app was asked has queued its own
                    // unload, which waits for this lock and asks again.
                    if (_unloadArmCount == armCount)
                    {
                        _unloadWhenDrained = false;
                        _unloadWhenDrainedStillWanted = null;
                    }
                }
                return;
            }

            lock (_drainSync)
            {
                if (!_unloadWhenDrained || _pendingRequests > 0)
                {
                    return;
                }

                _unloadWhenDrained = false;
                _unloadWhenDrainedStillWanted = null;
                Interlocked.Increment(ref _teardownGeneration);
            }

            LoggingService.Info("ParakeetTranscriptionService: Unloading the daemon now that the transcriptions using it have finished");
            DisposeModelCore(advanceTeardownGeneration: false, transcriptionLockHeld: true);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ParakeetTranscriptionService: Deferred daemon unload failed: {ex.Message}");
        }
        finally
        {
            try { _transcriptionLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// With the transcription lock held and nobody counted in: advances the teardown
    /// generation, counts the reloading caller in, and registers the reload's teardown CTS
    /// (so <see cref="DisposeModel"/> can cancel it), all in one step. Null when busy. A
    /// request that enters after this belongs to the new generation, waits on the lock,
    /// and runs on the reloaded daemon.
    /// </summary>
    private CancellationTokenSource? TryReserveIdleReload()
    {
        lock (_drainSync)
        {
            if (_pendingRequests > 0)
            {
                return null;
            }

            Interlocked.Increment(ref _teardownGeneration);
            _pendingRequests++;
            var cts = new CancellationTokenSource();
            _idleReloadTeardownCts = cts;
            return cts;
        }
    }

    /// <summary>
    /// Unregisters the idle reload (idempotent). False when a teardown cancelled it. The
    /// CTS is not disposed, for the same reason as <see cref="EndActiveRequest"/>.
    /// </summary>
    private bool EndIdleReload(CancellationTokenSource reloadTeardownCts)
    {
        lock (_drainSync)
        {
            if (ReferenceEquals(_idleReloadTeardownCts, reloadTeardownCts))
            {
                _idleReloadTeardownCts = null;
            }

            return !reloadTeardownCts.IsCancellationRequested;
        }
    }

    /// <summary>
    /// The housekeeping unload (#1544): stops the daemon only when nobody is using it,
    /// and never waits for or cancels a job. <see cref="DisposeModel"/> is the user's own
    /// teardown; this is for "the selected mode no longer needs Parakeet", which a mode
    /// write by the Local API or the GUI re-runs while a Local API job may be on the
    /// daemon. That caller must not block its thread for the job (seconds to minutes),
    /// and must not end someone else's transcription to free memory.
    /// <para>
    /// The lock is tried without waiting and the pending count checked under
    /// <see cref="_drainSync"/> in the same step as the generation advance, exactly as
    /// <see cref="TryReserveIdleReload"/> does: a request or lease counted in before this
    /// keeps the daemon; one counted in after belongs to the new generation, waits on the
    /// lock, and finds the daemon gone (a lease-less request then auto-restarts it).
    /// </para>
    /// </summary>
    /// <param name="stillWanted">
    /// Asked again when a deferred unload runs, off the UI thread: true when the app still does
    /// not need the daemon. Null unloads it whatever the app needs by then.
    /// </param>
    /// <returns>False when the daemon is busy: it is left running, and unloaded once the last request or lease on it has left, if <paramref name="stillWanted"/> still says so.</returns>
    public bool TryDisposeModelIfIdle(Func<bool>? stillWanted = null)
    {
        if (!_transcriptionLock.Wait(0))
        {
            ArmUnloadWhenDrained(stillWanted);
            return false;
        }

        try
        {
            lock (_drainSync)
            {
                if (_pendingRequests > 0)
                {
                    ArmUnloadWhenDrainedLocked(stillWanted);
                    return false;
                }

                _unloadWhenDrained = false;
                _unloadWhenDrainedStillWanted = null;
                Interlocked.Increment(ref _teardownGeneration);
            }

            DisposeModelCore(advanceTeardownGeneration: false, transcriptionLockHeld: true);
            return true;
        }
        finally
        {
            _transcriptionLock.Release();
        }
    }
}
