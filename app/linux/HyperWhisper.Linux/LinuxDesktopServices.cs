using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Linux;

internal sealed class LinuxDesktopServices : IDisposable
{
    private bool _disposed;

    public LinuxDesktopServices()
    {
        Paths = new LinuxAppPaths();
        PrivateFiles = new LinuxPrivateFileService();
        GlobalShortcuts = new LinuxGlobalShortcutService();
    }

    public IAppPaths Paths { get; }
    public IPrivateFileService PrivateFiles { get; }
    public IGlobalShortcutService GlobalShortcuts { get; }

    public bool ProbeSharedCore() =>
        !SharedCoreBridge.ContainsCjk("HyperWhisper")
        && SharedCoreBridge.ContainsCjk("音声")
        && string.Equals(SharedCoreBridge.NormalizeAppType("Terminal"), "terminal", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GlobalShortcuts.Dispose();
    }
}
