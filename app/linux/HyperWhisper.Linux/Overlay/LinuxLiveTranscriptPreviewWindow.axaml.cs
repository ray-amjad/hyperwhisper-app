using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HyperWhisper.LiveStreaming;

namespace HyperWhisper.Linux.Overlay;

internal sealed partial class LinuxLiveTranscriptPreviewWindow : Window, ILinuxLiveTranscriptPreviewSurface
{
    private readonly LinuxLiveTranscriptPreviewViewModel _viewModel;

    public LinuxLiveTranscriptPreviewWindow(LinuxLiveTranscriptPreviewViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        ShowActivated = false;
        Focusable = false;
        Opened += (_, _) =>
        {
            PositionAboveRecordingOverlay();
            LinuxOverlayWindowPolicy.TryApply(this, clickThrough: true);
        };
    }

    public void Apply(EphemeralLiveTranscriptSnapshot snapshot)
    {
        _viewModel.Apply(snapshot);
        try
        {
            if (!IsVisible) Show();
            PositionAboveRecordingOverlay();
            LinuxOverlayWindowPolicy.TryApply(this, clickThrough: true);
            this.FindControl<ScrollViewer>("PreviewScroller")?.ScrollToEnd();
        }
        catch { }
    }

    public void HideBestEffort()
    {
        try { if (IsVisible) Hide(); } catch { }
    }

    void IDisposable.Dispose()
    {
        try { Close(); } catch { }
    }

    private void PositionAboveRecordingOverlay()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var area = screen.WorkingArea;
        Position = new(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Bottom - height - (int)Math.Round(84 * scale));
    }
}
