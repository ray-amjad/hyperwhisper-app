using System.Runtime.InteropServices;

namespace HyperWhisper.Linux;

/// <summary>
/// Routes SIGTERM, SIGINT and SIGQUIT into the same quit as the tray's Quit (#1038). The runtime
/// default ends the process without MainWindow.OnClosing, so a muted sink and a 100% mic stayed
/// that way after a logout or a `systemctl --user stop`. The first signal is cancelled and asks for
/// the quit; a watchdog exits with the signal's own status if that quit hangs, and a second signal
/// falls through to the runtime default. The Local API's own handler (LocalApiHost.RegisterSignalCleanup,
/// #957) still runs on the same shared PosixSignalContext and deletes the discovery file; the
/// graceful quit then stops the host.
/// </summary>
internal sealed class LinuxShutdownSignals : IDisposable
{
    // Covers the whole teardown after the quit: the cancel (which waits for a transcription in flight),
    // the Local API stop, storage maintenance, the platform service disposes (2-3 s joins each) and a
    // 2 s Sentry flush. The audio is restored before any of it, so a quit this cuts short leaves it back.
    private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(20);

    private const int Idle = 0, Signalled = 1, Fired = 2, Disposed = 3;
    private readonly Action _requestQuit;
    private readonly Action<int> _forceExit;
    private readonly TimeSpan _grace;
    private readonly List<PosixSignalRegistration> _registrations = new(3);
    private Timer? _watchdog;
    private int _state;

    internal LinuxShutdownSignals(Action requestQuit, Action<int> forceExit, TimeSpan grace)
    {
        _requestQuit = requestQuit;
        _forceExit = forceExit;
        _grace = grace;
    }

    internal int RegistrationCount => _registrations.Count;
    internal bool HasFired => Volatile.Read(ref _state) == Fired;

    public static LinuxShutdownSignals Register(Action requestQuit) =>
        Register(requestQuit, Environment.Exit, PosixSignalRegistration.Create, Console.Error);

    // A signal the platform refuses keeps the runtime default rather than failing startup; the others
    // still route into the quit.
    internal static LinuxShutdownSignals Register(
        Action requestQuit,
        Action<int> forceExit,
        Func<PosixSignal, Action<PosixSignalContext>, PosixSignalRegistration> create,
        TextWriter error)
    {
        var signals = new LinuxShutdownSignals(requestQuit, forceExit, WatchdogGrace);
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
        {
            try { signals._registrations.Add(create(signal, signals.Handle)); }
            catch (Exception exception) { error.WriteLine($"HyperWhisper {signal} registration failed: {exception.Message}"); }
        }
        return signals;
    }

    internal void Handle(PosixSignalContext context)
    {
        if (Interlocked.CompareExchange(ref _state, Signalled, Idle) != Idle) return;
        context.Cancel = true;
        var exitCode = context.Signal switch { PosixSignal.SIGINT => 130, PosixSignal.SIGQUIT => 131, _ => 143 };
        _watchdog = new Timer(_ => OnWatchdogElapsed(exitCode), null, _grace, Timeout.InfiniteTimeSpan);
        try { _requestQuit(); }
        catch (Exception) { }
    }

    // A callback the timer already queued can still run after Dispose, so the state, not the timer's
    // own Dispose, is what stops a clean quit from being overwritten with 143.
    internal void OnWatchdogElapsed(int exitCode)
    {
        if (Interlocked.CompareExchange(ref _state, Fired, Signalled) == Signalled) _forceExit(exitCode);
    }

    public void Dispose()
    {
        var state = Volatile.Read(ref _state);
        while (state is Idle or Signalled)
        {
            var seen = Interlocked.CompareExchange(ref _state, Disposed, state);
            if (seen == state) break;
            state = seen;
        }
        // Lost to the watchdog: its exit is already under way. Already disposed: nothing left to do.
        if (state is Fired or Disposed) return;
        foreach (var registration in _registrations)
        {
            try { registration.Dispose(); }
            catch (Exception) { }
        }
        _registrations.Clear();
        _watchdog?.Dispose();
    }
}
