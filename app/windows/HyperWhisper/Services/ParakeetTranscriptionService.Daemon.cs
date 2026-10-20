// PARAKEET TRANSCRIPTION SERVICE — DAEMON
// Daemon process supervision: crash detection, the stderr pump, kill, and the
// graceful shutdown that unloads the model.

using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    // =========================================================================
    // DAEMON LIFECYCLE
    // =========================================================================

    /// <summary>
    /// Handles the daemon process exiting unexpectedly (crash detection).
    /// Sets _isReady to false so subsequent transcription attempts will fail fast
    /// or trigger auto-restart.
    /// </summary>
    private void OnDaemonExited(object? sender, EventArgs e)
    {
        var exitCode = -1;
        try
        {
            exitCode = _daemonProcess?.ExitCode ?? -1;
        }
        catch
        {
            // Process may already be disposed
        }

        _isReady = false;
        LoggingService.Warn($"ParakeetTranscriptionService: Daemon process exited unexpectedly (exit code: {exitCode})");
    }

    /// <summary>
    /// Starts a background thread that reads stderr from the daemon and logs
    /// each line as debug output. This captures diagnostic messages from the
    /// C++ engine without blocking the main communication channel.
    /// </summary>
    private static StderrCapture StartStderrReader(Process process)
    {
        var capture = new StderrCapture();
        _ = ReadStderrAsync(process.StandardError, capture);
        return capture;
    }

    private static async Task ReadStderrAsync(StreamReader stderrReader, StderrCapture capture)
    {
        try
        {
            while (true)
            {
                var line = await stderrReader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break; // Stream closed

                capture.Add(line);
                // The capture keeps the raw line (the #1598 classifier matches the model
                // directory in it); the log file gets it without the user's identifiers
                // (#854): the profile directory becomes %USERPROFILE%, a redirected
                // local-app-data directory %LOCALAPPDATA%, the bare account name %USER%.
                LoggingService.Debug($"ParakeetTranscriptionService [stderr]: {SentryService.RedactUserIdentifiers(line)}");
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected when process is disposed during shutdown
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ParakeetTranscriptionService: Stderr reader stopped: {ex.Message}");
        }
        finally
        {
            capture.Complete();
        }
    }

    /// <summary>
    /// The daemon's first stderr lines (its startup diagnostics), kept so a startup
    /// error can be classified by its cause (#1598). Bounded: a long-lived daemon's
    /// later lines are only logged.
    /// </summary>
    private sealed class StderrCapture
    {
        private const int MaxLines = 200;
        private readonly List<string> _lines = new();
        // The LAST lines too, for a start failure's report (#854): a daemon that
        // dies writes its cause last. One capture per daemon start, so it starts empty.
        private readonly Queue<string> _tail = new();
        private int _lineCount;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(string line)
        {
            lock (_lines)
            {
                if (_lines.Count < MaxLines) _lines.Add(line);
                _tail.Enqueue(line);
                if (_tail.Count > StartFailureTailLines) _tail.Dequeue();
                _lineCount++;
            }
        }

        /// <summary>Every stderr line the daemon wrote so far, and its last ones.</summary>
        public (int LineCount, IReadOnlyList<string> Tail) TailSnapshot()
        {
            lock (_lines)
            {
                return (_lineCount, _tail.ToArray());
            }
        }

        public void Complete() => _ended.TrySetResult();

        public IReadOnlyList<string> Snapshot()
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }

        /// <summary>Waits until stderr reaches its end, or the timeout passes.</summary>
        public async Task WaitForEndAsync(TimeSpan timeout)
        {
            try
            {
                await _ended.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Classify on what arrived; a missing cause means "not marked".
            }
        }
    }

    /// <summary>
    /// Forcefully kills the daemon process if it is still running.
    /// Used during timeout and error recovery scenarios.
    /// </summary>
    private void KillDaemonProcess() => KillDaemonProcess(_daemonProcess);

    /// <summary>
    /// The exit code of a daemon that closed stdout before READY, once it has
    /// exited (waits up to <paramref name="waitMilliseconds"/>, 1 s by default), or
    /// null when it is still running or unreadable.
    /// </summary>
    private static int? TryGetEarlyExitCode(Process? process, int waitMilliseconds = 1000)
    {
        try
        {
            if (process == null) return null;
            return process.WaitForExit(waitMilliseconds) ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void KillDaemonProcess(Process? process)
    {
        try
        {
            if (process != null && !process.HasExited)
            {
                LoggingService.Debug($"ParakeetTranscriptionService: Killing daemon process (PID: {process.Id})");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ParakeetTranscriptionService: Failed to kill daemon process: {ex.Message}");
        }
    }

    /// <summary>
    /// Kills the daemon instance a request (or its drain) captured, and clears
    /// _isReady only while that instance is still the current daemon. If a teardown
    /// already replaced it with a new model's daemon, the new one is left alone (#1562).
    /// </summary>
    private void StopDaemonInstance(Process? process)
    {
        if (ReferenceEquals(_daemonProcess, process))
        {
            _isReady = false;
        }

        KillDaemonProcess(process);
    }

    /// <param name="transcriptionLockHeld">
    /// The caller (<see cref="ReloadWhenIdleAsync"/>) already holds the lock with no request
    /// pending, so there is nothing to cancel or wait for, and the lock stays the caller's.
    /// </param>
    /// <param name="idleReloadCancellation">
    /// The idle reload's teardown token: a GUI teardown cuts the old daemon's graceful-exit
    /// wait short (it is force-killed), so the reload hands the lock back at once.
    /// </param>
    private void DisposeModelCore(
        bool advanceTeardownGeneration,
        bool transcriptionLockHeld = false,
        CancellationToken idleReloadCancellation = default)
    {
        // FIRST, before any cancel or wait: every request that entered TranscribeAsync
        // before this point is now stale. One still queued on the lock fails as Cancelled
        // when it gets it, instead of finding the daemon gone and auto-restarting the old
        // model (#1562 review). Only the auto-restart's own reload skips this.
        if (advanceTeardownGeneration)
        {
            Interlocked.Increment(ref _teardownGeneration);

            // The user's own unload or mode switch supersedes a deferred housekeeping one:
            // a switch to a Parakeet mode must keep the daemon it is about to load.
            lock (_drainSync)
            {
                _unloadWhenDrained = false;
                _unloadWhenDrainedStillWanted = null;
            }
        }

        // Mark the provider unavailable BEFORE waiting on the lock. IsAvailable keys off
        // _isReady, so clearing it here closes the window where — during the up-to-65s
        // wait below — the Local API path (TranscriptionOrchestrator.TranscribeLocalAsync)
        // could still see IsAvailable == true and queue another Parakeet request behind
        // the in-flight transcription. Such a queued request would resume after teardown,
        // hit DaemonCrashed, and trigger an auto-restart that undoes this disposal / mode
        // switch. A transcription already past its IsAvailable guard holds the lock; the
        // wait below either lets it finish (a short clip) or cancels it as Cancelled. An
        // idle reload holding the lock has no grace (no active request budget) and is
        // cancelled at once (#1608 review).
        _isReady = false;
        if (!transcriptionLockHeld)
        {
            CancelAndWaitForInFlightDrain("model disposal");
        }

        // Serialize teardown against an in-flight TranscribeInternalAsync (which holds
        // this same lock while awaiting ReadLineAsync/WriteLineAsync on the stdio streams).
        // Without this, a re-init — e.g. a mode switch, file transcription, or retry on
        // the UI thread — could dispose the StreamReader/StreamWriter and the daemon
        // process out from under a transcription running on a thread-pool thread (the
        // provider is a process-wide singleton shared with the Local API server),
        // surfacing as an ObjectDisposedException / NullReferenceException.
        //
        // This runs on the UI thread (mode switch), so the total wait never exceeds the
        // per-engine response FLOOR (60 / 120 / 180 s) plus 5 s, as on main. Since #1562 a
        // request's own budget can be hours, so teardown must not simply wait the request
        // out and then dispose the streams under its live read: the read would fail as
        // DaemonCrashed and TranscribeAsync would auto-restart the OLD model, undoing the
        // mode switch and re-running the whole file. Instead:
        //   1. A short in-flight request (budget up to 2x the floor) gets up to the floor
        //      to finish on its own, as every request did on main.
        //   2. Otherwise (or if it has not finished by then) teardown cancels it. The
        //      request kills the daemon, fails as Cancelled (never DaemonCrashed, so no
        //      auto-restart), and releases the lock within milliseconds.
        //   3. Teardown takes the lock for the rest of the floor + 5 s. Only a request
        //      that ignores the cancel runs that out; teardown then proceeds rather than
        //      deadlock, and that request still reports Cancelled.
        var lockTaken = false;
        if (!transcriptionLockHeld)
        {
            var floorSeconds = ResponseFloorSeconds(_isQwen3, _isOnline);
            var teardownClock = Stopwatch.StartNew();
            var teardownWait = TeardownLockWait(floorSeconds);
            var grace = ComputeTeardownGrace(floorSeconds, SnapshotActiveRequestBudget());
            lockTaken = grace > TimeSpan.Zero && _transcriptionLock.Wait(grace);
            if (!lockTaken)
            {
                CancelActiveRequest("model disposal");
                // A caller cancel may have handed the request to a drain meanwhile.
                CancelAndWaitForInFlightDrain("model disposal");
                // Wait in short slices and re-cancel each time, so a request that took the
                // lock just before it registered itself is cancelled too.
                while (true)
                {
                    var remaining = teardownWait - teardownClock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        LoggingService.Warn($"ParakeetTranscriptionService: Transcription lock not released within {teardownWait.TotalSeconds:F0}s; tearing down anyway");
                        break;
                    }

                    lockTaken = _transcriptionLock.Wait(remaining < TeardownCancelSlice ? remaining : TeardownCancelSlice);
                    if (lockTaken)
                    {
                        break;
                    }

                    CancelActiveRequest("model disposal");
                }
            }
        }
        try
        {
            // Step 1: Send quit command if daemon is running
            if (_daemonProcess != null && !_daemonProcess.HasExited && _stdinWriter != null)
            {
                try
                {
                    LoggingService.Debug("ParakeetTranscriptionService: Sending quit command to daemon...");
                    var quitCommand = JsonSerializer.Serialize(new { command = "quit" }, s_requestJsonOptions);
                    _stdinWriter.WriteLine(quitCommand);
                    _stdinWriter.Flush();
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"ParakeetTranscriptionService: Failed to send quit command: {ex.Message}");
                }
            }

            // Step 2: Wait for graceful exit
            if (_daemonProcess != null && !_daemonProcess.HasExited)
            {
                LoggingService.Debug("ParakeetTranscriptionService: Waiting for daemon to exit (3s timeout)...");
                var exited = WaitForDaemonExit(_daemonProcess, TimeSpan.FromSeconds(3), idleReloadCancellation);

                if (!exited)
                {
                    // Step 3: Force kill if still running
                    LoggingService.Warn("ParakeetTranscriptionService: Daemon did not exit gracefully, force-killing...");
                    KillDaemonProcess();
                }
                else
                {
                    LoggingService.Debug("ParakeetTranscriptionService: Daemon exited gracefully");
                }
            }

            // Step 4: Clean up resources using SafeDispose pattern
            if (_stdinWriter != null)
            {
                try { _stdinWriter.Dispose(); } catch (Exception ex) { LoggingService.Warn($"ParakeetTranscriptionService: Failed to dispose stdin writer: {ex.Message}"); }
                _stdinWriter = null;
            }

            if (_stdoutReader != null)
            {
                try { _stdoutReader.Dispose(); } catch (Exception ex) { LoggingService.Warn($"ParakeetTranscriptionService: Failed to dispose stdout reader: {ex.Message}"); }
                _stdoutReader = null;
            }

            if (_daemonProcess != null)
            {
                try
                {
                    _daemonProcess.Exited -= OnDaemonExited;
                    _daemonProcess.Dispose();
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"ParakeetTranscriptionService: Failed to dispose daemon process: {ex.Message}");
                }
                _daemonProcess = null;
            }

            _loadedModelId = null;
            _activeProvider = null;

            LoggingService.Debug("ParakeetTranscriptionService: Model disposed and daemon stopped");
        }
        finally
        {
            if (lockTaken) _transcriptionLock.Release();
        }
    }

    /// <summary>
    /// Waits for the daemon to exit, up to <paramref name="timeout"/>. With a cancellable
    /// token (an idle reload's) it waits in short slices and gives up as soon as the token
    /// is cancelled, so a GUI teardown is not held for the graceful-exit pause.
    /// </summary>
    private static bool WaitForDaemonExit(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return process.WaitForExit(timeout);
        }

        var clock = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (process.WaitForExit(remaining < IdleReloadPollInterval ? remaining : IdleReloadPollInterval))
            {
                return true;
            }
        }

        return process.HasExited;
    }
}
