using Avalonia.Threading;

namespace HyperWhisper.Linux.Overlay;

internal sealed class AvaloniaLinuxOverlayDispatcher : ILinuxOverlayDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

internal static class LinuxRecordingOverlayFactory
{
    public static LinuxRecordingOverlayController Create()
    {
        var viewModel = new LinuxRecordingOverlayViewModel();
        var window = new LinuxRecordingOverlayWindow(viewModel);
        return new(viewModel, new AvaloniaLinuxOverlayDispatcher(), window);
    }
}
