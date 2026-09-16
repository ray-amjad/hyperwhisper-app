// UIA PROBE HOST
//
// One background STA thread for every UI Automation probe the app makes, and a
// timeout that really bounds the caller.
//
// WHY THIS EXISTS (issue #672)
// ============================
// A UIA call is cross-process: it asks the FOREGROUND application's provider to
// answer. When that provider is slow or wedged, the call blocks for as long as
// the provider takes — seconds, not milliseconds.
//
// Run that on the WPF UI thread and it is a whole-desktop freeze, not a slow
// app. Both WH_KEYBOARD_LL hooks (KeyboardShortcutService, PushToTalkMonitor)
// are delivered to the thread that installed them, which is the UI thread. A UI
// thread parked inside UIA stops answering the hook, so every keystroke in every
// application stalls until the call returns.
//
// THE TRAP THIS CLASS REPLACES
// ============================
// `Application.Current.Dispatcher.Invoke(probe, TimeSpan.FromMilliseconds(200))`
// looks like it bounds the probe. It does not, for two separate reasons:
//
//   1. The timeout only bounds the wait for the operation to START. At
//      DispatcherPriority.Send the operation starts at once, and a timeout
//      cannot interrupt a delegate that is already running — the pump that would
//      process the timeout is parked inside the delegate. `operation.Abort()`
//      likewise only cancels an operation that has not started.
//   2. That overload (Delegate, TimeSpan, params object[]) RETURNS NULL on a
//      timeout; it does not throw. A `catch (TimeoutException)` around it is
//      dead code.
//
// So the probe must run on a thread that is allowed to hang. Here it runs on a
// dedicated STA thread and the caller waits on an event with a real timeout.
// When a probe hangs, this thread is what hangs: the caller gets its fallback
// and the UI thread never stops.
//
// A hung probe does hold the thread, so probes queued behind it time out too and
// take their fallback. That is the intended degradation — field detection is an
// optimisation, and a missed probe costs quality, not a frozen keyboard.

using System.Threading;
using System.Windows.Threading;

namespace HyperWhisper.Services.Platform;

/// <summary>
/// Runs UI Automation probes on a dedicated background STA thread, never on the
/// caller's thread, and gives up on a probe that overruns its timeout.
/// </summary>
public static class UiaProbeHost
{
    /// <summary>
    /// The bound every probe site used before this class existed. Long enough
    /// for a healthy provider (a probe normally answers in 5-50ms), short enough
    /// that a caller on the dictation path never waits.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static Thread? _thread;

    /// <summary>
    /// Runs <paramref name="probe"/> on the STA thread and returns its result.
    /// Returns <paramref name="fallback"/> when the probe overruns
    /// <see cref="DefaultTimeout"/>, throws, or the STA thread is unavailable.
    /// </summary>
    /// <param name="name">Probe name, for the log line only.</param>
    public static T Probe<T>(string name, Func<T> probe, T fallback)
        => Probe(name, probe, DefaultTimeout, fallback);

    /// <summary>
    /// Runs <paramref name="probe"/> on the STA thread and returns its result.
    /// Returns <paramref name="fallback"/> when the probe overruns
    /// <paramref name="timeout"/>, throws, or the STA thread is unavailable.
    ///
    /// Never throws, and never runs the probe on the calling thread — which is
    /// the whole point. See the header of this file.
    /// </summary>
    public static T Probe<T>(string name, Func<T> probe, TimeSpan timeout, T fallback)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var dispatcher = EnsureDispatcher();
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            LoggingService.Warn($"UiaProbeHost: {name} skipped — the STA thread is unavailable");
            return fallback;
        }

        // A probe that itself calls Probe. We are already on the STA thread, so
        // queueing would wait for a pump that is parked in this very call.
        if (dispatcher.CheckAccess())
            return RunGuarded(name, probe, fallback);

        DispatcherOperation<T> operation;
        try
        {
            operation = dispatcher.InvokeAsync(() => RunGuarded(name, probe, fallback), DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"UiaProbeHost: {name} could not be queued — {ex.Message}");
            return fallback;
        }

        DispatcherOperationStatus status;
        try
        {
            status = operation.Wait(timeout);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"UiaProbeHost: {name} wait failed — {ex.Message}");
            return fallback;
        }

        if (status == DispatcherOperationStatus.Completed)
            return operation.Result;

        // Cancels the operation only if it has not started. One that HAS started
        // keeps running on the STA thread; we walk away and take the fallback,
        // which is exactly what the caller's thread needs to do.
        operation.Abort();
        LoggingService.Warn(
            $"UiaProbeHost: {name} did not answer within {timeout.TotalMilliseconds:F0}ms — using the fallback");
        return fallback;
    }

    private static T RunGuarded<T>(string name, Func<T> probe, T fallback)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"UiaProbeHost: {name} failed — {ex.Message}");
            return fallback;
        }
    }

    private static Dispatcher? EnsureDispatcher()
    {
        var existing = Volatile.Read(ref _dispatcher);
        if (existing != null)
            return existing;

        lock (Gate)
        {
            if (_dispatcher != null)
                return _dispatcher;

            StartStaThread();
            return _dispatcher;
        }
    }

    /// <summary>
    /// Starts the STA thread and its dispatcher pump. Background thread, started
    /// once and never stopped: it costs one idle thread and it ends with the
    /// process. Nothing calls InvokeShutdown, so the pump keeps running for the
    /// life of the app.
    /// </summary>
    private static void StartStaThread()
    {
        var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref _dispatcher, Dispatcher.CurrentDispatcher);
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                LoggingService.Error("UiaProbeHost: STA thread crashed", ex);
            }
            finally
            {
                // Unblocks a starter that is still waiting after a crash before
                // the dispatcher was published.
                ready.Set();
            }
        })
        {
            Name = "HyperWhisper-UIA-Probe",
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            LoggingService.Warn("UiaProbeHost: the STA thread did not start within 5 seconds");
            return;
        }

        _thread = thread;
        LoggingService.Info("UiaProbeHost: STA probe thread started");
    }

    /// <summary>
    /// Test seam. The managed id of the STA thread, or null before it starts.
    /// </summary>
    internal static int? StaThreadId => _thread?.ManagedThreadId;
}
