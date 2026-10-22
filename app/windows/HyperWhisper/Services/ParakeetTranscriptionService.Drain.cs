// PARAKEET TRANSCRIPTION SERVICE — DRAIN
// The request that holds the transcription lock, and the background drain that takes
// the lock over when its caller cancels (#1562).

using System.Diagnostics;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    /// <summary>
    /// Drains and discards the single in-flight result line the daemon produces for a
    /// request that was cancelled by the caller after the request was sent to stdin.
    ///
    /// Decode is synchronous in the daemon, so the result arrives within normal inference
    /// time. Draining it keeps the stdout protocol aligned so the next transcription reads
    /// its own result instead of this stale one — letting the daemon survive cancellation
    /// instead of paying a 5-30s cold-start reload.
    ///
    /// MUST be called while holding <see cref="_transcriptionLock"/> so no other
    /// transcription races on the stdout stream. If the drain times out or the daemon has
    /// gone away, the daemon is force-killed so the next call reloads from a clean state
    /// rather than reading a desynced stdout line.
    /// </summary>
    private void StartInFlightDrain(Task<string?> inFlightReadTask, CancellationTokenSource drainCts, CancellationTokenSource requestTeardownCts, Process? daemonProcess)
    {
        lock (_drainSync)
        {
            // Hand over from "active request" to "drain" in one step under _drainSync, the
            // lock DisposeModel cancels under. Teardown therefore sees either the request
            // (and cancels requestTeardownCts, checked below) or the drain (and cancels it).
            if (ReferenceEquals(_activeRequestTeardownCts, requestTeardownCts))
            {
                _activeRequestTeardownCts = null;
            }

            var drainTask = DrainInFlightResultAndReleaseLockAsync(inFlightReadTask, drainCts, daemonProcess);
            _inFlightDrainCts = drainCts;
            _inFlightDrainTask = drainTask;
            if (drainTask.IsCompleted)
            {
                _inFlightDrainCts = null;
                _inFlightDrainTask = null;
            }
            else if (requestTeardownCts.IsCancellationRequested)
            {
                // Teardown cancelled the request after its caller did: drain no longer
                // needed — the daemon is going away — so end it now.
                _ = drainCts.CancelAsync();
            }
        }
    }

    /// <summary>
    /// Registers the request that now holds _transcriptionLock so DisposeModel can end it.
    /// Its budget starts at the floor and is raised once the audio length is known.
    /// </summary>
    private CancellationTokenSource BeginActiveRequest()
    {
        var cts = new CancellationTokenSource();
        lock (_drainSync)
        {
            _activeRequestTeardownCts = cts;
            _activeRequestBudget = TimeSpan.FromSeconds(ResponseFloorSeconds(_isQwen3, _isOnline));
        }
        return cts;
    }

    private void SetActiveRequestBudget(CancellationTokenSource requestTeardownCts, TimeSpan budget)
    {
        lock (_drainSync)
        {
            if (ReferenceEquals(_activeRequestTeardownCts, requestTeardownCts))
            {
                _activeRequestBudget = budget;
            }
        }
    }

    /// <summary>
    /// Unregisters the request. The CTS is not disposed: it has no timer and no wait
    /// handle, and DisposeModel may still be running its CancelAsync callbacks.
    /// </summary>
    private void EndActiveRequest(CancellationTokenSource requestTeardownCts)
    {
        lock (_drainSync)
        {
            if (ReferenceEquals(_activeRequestTeardownCts, requestTeardownCts))
            {
                _activeRequestTeardownCts = null;
            }
        }
    }

    /// <summary>The budget of the request holding the lock, or null when none is.</summary>
    private TimeSpan? SnapshotActiveRequestBudget()
    {
        lock (_drainSync)
        {
            return _activeRequestTeardownCts != null ? _activeRequestBudget : null;
        }
    }

    /// <summary>
    /// Cancels whatever holds the lock on someone's behalf: the in-flight request, or an
    /// idle reload (#1608 review), if any. CancelAsync flips the token at once but runs
    /// the continuation on the thread pool, never inline on this (UI) thread under
    /// _drainSync.
    /// </summary>
    private void CancelActiveRequest(string reason)
    {
        lock (_drainSync)
        {
            if (_activeRequestTeardownCts is { IsCancellationRequested: false } cts)
            {
                LoggingService.Info($"ParakeetTranscriptionService: Cancelling the in-flight transcription for {reason}");
                _ = cts.CancelAsync();
            }

            if (_idleReloadTeardownCts is { IsCancellationRequested: false } reloadCts)
            {
                LoggingService.Info($"ParakeetTranscriptionService: Cancelling the idle model reload for {reason}");
                _ = reloadCts.CancelAsync();
            }
        }
    }

    private async Task DrainInFlightResultAndReleaseLockAsync(Task<string?> inFlightReadTask, CancellationTokenSource drainCts, Process? daemonProcess)
    {
        try
        {
            // Bounded timeout that is NOT linked to the (already-cancelled) caller token, so the
            // original stdout read is not cancelled immediately. The result should arrive within
            // inference time; this just guards against a wedged daemon.
            try
            {
                var drained = await inFlightReadTask.ConfigureAwait(false);
                if (drained == null)
                {
                    // Daemon closed stdout (crashed or exited) — clean up so the next call reloads.
                    LoggingService.Warn("ParakeetTranscriptionService: Daemon closed stdout while draining cancelled result; resetting");
                    StopDaemonInstance(daemonProcess);
                }
                else
                {
                    LoggingService.Debug("ParakeetTranscriptionService: Drained in-flight result after cancellation; daemon kept alive");
                }
            }
            catch (Exception ex)
            {
                // Timeout or stream error — fall back to killing the daemon to guarantee the
                // stdout protocol is aligned for the next transcription.
                LoggingService.Warn($"ParakeetTranscriptionService: Failed to drain in-flight result ({ex.Message}); killing daemon to stay aligned");
                StopDaemonInstance(daemonProcess);
            }
        }
        finally
        {
            try
            {
                _transcriptionLock.Release();
            }
            catch (ObjectDisposedException ex)
            {
                LoggingService.Debug($"ParakeetTranscriptionService: Drain completed after transcription lock disposal: {ex.Message}");
            }

            lock (_drainSync)
            {
                if (ReferenceEquals(_inFlightDrainCts, drainCts))
                {
                    _inFlightDrainCts = null;
                    _inFlightDrainTask = null;
                }
            }

            drainCts.Dispose();
        }
    }

    private static async Task ObserveInFlightReadAsync(Task<string?> inFlightReadTask)
    {
        try
        {
            await inFlightReadTask.ConfigureAwait(false);
        }
        catch
        {
            // The daemon is being killed on timeout; this only observes late read faults.
        }
    }

    private void CancelAndWaitForInFlightDrain(string reason)
    {
        Task? drainTask;
        CancellationTokenSource? drainCts;
        lock (_drainSync)
        {
            drainTask = _inFlightDrainTask;
            drainCts = _inFlightDrainCts;
        }

        if (drainTask == null || drainTask.IsCompleted)
        {
            return;
        }

        LoggingService.Debug($"ParakeetTranscriptionService: Cancelling in-flight drain before {reason}");
        try
        {
            drainCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Drain completed between the state snapshot and cancellation.
        }

        try
        {
            if (!drainTask.Wait(TimeSpan.FromSeconds(1)))
            {
                LoggingService.Warn($"ParakeetTranscriptionService: In-flight drain did not finish before {reason}; continuing teardown");
            }
        }
        catch (AggregateException ex)
        {
            LoggingService.Debug($"ParakeetTranscriptionService: In-flight drain ended during {reason}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
