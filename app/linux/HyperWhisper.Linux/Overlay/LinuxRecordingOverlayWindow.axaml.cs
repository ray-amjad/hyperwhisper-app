using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HyperWhisper.Linux.Overlay;

internal sealed partial class LinuxRecordingOverlayWindow : Window, ILinuxRecordingOverlaySurface
{
    public LinuxRecordingOverlayWindow(LinuxRecordingOverlayViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            PositionAtBottomCenter();
            LinuxOverlayWindowPolicy.TryApply(this);
        };
    }

    public void ShowBestEffort()
    {
        try
        {
            if (!IsVisible) Show();
            PositionAtBottomCenter();
        }
        catch { /* Overlay feedback must not block transcription. */ }
    }

    public void HideBestEffort()
    {
        try { if (IsVisible) Hide(); } catch { }
    }

    void IDisposable.Dispose()
    {
        try { Close(); } catch { }
    }

    private void PositionAtBottomCenter()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var pixelWidth = (int)Math.Round(Width * scale);
        var pixelHeight = (int)Math.Round(Height * scale);
        Position = new(
            screen.WorkingArea.X + (screen.WorkingArea.Width - pixelWidth) / 2,
            screen.WorkingArea.Bottom - pixelHeight - (int)Math.Round(20 * scale));
    }
}
