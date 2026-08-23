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
    public static LinuxRecordingOverlayController Create(
        Func<string, string> text,
        Action? stop = null,
        Action? confirmCancel = null,
        Action? dismissCancel = null)
    {
        var viewModel = new LinuxRecordingOverlayViewModel();
        var window = new LinuxRecordingOverlayWindow(viewModel, new JsonLinuxOverlayPlacementStore());
        if (stop is not null) window.StopRequested += (_, _) => stop();
        if (confirmCancel is not null) window.ConfirmCancelRequested += (_, _) => confirmCancel();
        if (dismissCancel is not null) window.DismissCancelRequested += (_, _) => dismissCancel();
        return new(viewModel, new AvaloniaLinuxOverlayDispatcher(), window, text);
    }
}
