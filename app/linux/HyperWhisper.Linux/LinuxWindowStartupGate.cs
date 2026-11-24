namespace HyperWhisper.Linux;

internal enum LinuxWindowOpenedWork { None, Launch, RetryStartUp, RestartTray }

/// <summary>What a window Opened may start (#833). Avalonia raises Opened on every Show() after Hide(),
/// so a later Opened only retries what has not yet succeeded, as main's re-run did: the start-up chain
/// until it succeeds once, then the tray helper while it is down. Nothing starts while one is running.</summary>
internal sealed class LinuxWindowStartupGate
{
    private bool _attempted, _running, _succeeded;

    internal LinuxWindowOpenedWork Next(bool trayAvailable)
    {
        if (_running) return LinuxWindowOpenedWork.None;
        if (_succeeded && trayAvailable) return LinuxWindowOpenedWork.None;
        _running = true;
        if (_succeeded) return LinuxWindowOpenedWork.RestartTray;
        var first = !_attempted;
        _attempted = true;
        return first ? LinuxWindowOpenedWork.Launch : LinuxWindowOpenedWork.RetryStartUp;
    }

    internal void Finish(bool succeeded)
    {
        _running = false;
        _succeeded |= succeeded;
    }
}
