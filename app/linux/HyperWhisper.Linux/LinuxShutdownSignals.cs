using System.Runtime.InteropServices;

namespace HyperWhisper.Linux;

/// <summary>
/// Routes SIGTERM, SIGINT and SIGQUIT into the same quit as the tray's Quit (#1038). The runtime
/// default ends the process without MainWindow.OnClosing, so a muted sink and a 100% mic stayed
/// that way after a logout or a `systemctl --user stop`. The first signal is cancelled and asks for
/// the quit; a watchdog exits with the signal's own status if that quit hangs, and a second signal
/// falls through to the runtime default.
/// </summary>
internal sealed class LinuxShutdownSignals : IDisposable
{
    // Covers the whole teardown after the quit: the Local API stop, storage maintenance, the platform
    // service disposes (2-3 s joins each) and a 2 s Sentry flush. The audio is restored before any of it.
    private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(20);

    private readonly Action _requestQuit;
    private readonly Action<int> _forceExit;
    private readonly TimeSpan _grace;
    private readonly List<PosixSignalRegistration> _registrations = new(3);
    private Timer? _watchdog;
    private int _signalled;
    private volatile bool _disposed;

    internal LinuxShutdownSignals(Action requestQuit, Action<int> forceExit, TimeSpan grace)
    {
        _requestQuit = requestQuit;
        _forceExit = forceExit;
        _grace = grace;
    }

    public static LinuxShutdownSignals Register(Action requestQuit)
    {
        var signals = new LinuxShutdownSignals(requestQuit, Environment.Exit, WatchdogGrace);
        // A platform that refuses a registration keeps the runtime default rather than failing startup.
        try
        {
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
                signals._registrations.Add(PosixSignalRegistration.Create(signal, signals.Handle));
        }
        catch (Exception) { signals.Dispose(); }
        return signals;
    }

    internal void Handle(PosixSignalContext context)
    {
        if (_disposed || Interlocked.Exchange(ref _signalled, 1) != 0) return;
        context.Cancel = true;
        var exitCode = context.Signal switch { PosixSignal.SIGINT => 130, PosixSignal.SIGQUIT => 131, _ => 143 };
        _watchdog = new Timer(_ => OnWatchdogElapsed(exitCode), null, _grace, Timeout.InfiniteTimeSpan);
        try { _requestQuit(); }
        catch (Exception) { }
    }

    // A callback the timer already queued can still run after Dispose, so the flag, not the
    // timer's own Dispose, is what stops a clean quit from being overwritten with 143.
    internal void OnWatchdogElapsed(int exitCode)
    {
        if (!_disposed) _forceExit(exitCode);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var registration in _registrations)
        {
            try { registration.Dispose(); }
            catch (Exception) { }
        }
        _registrations.Clear();
        _watchdog?.Dispose();
    }
}
