using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace HyperWhisper.Linux.Overlay;

internal sealed partial class LinuxRecordingOverlayWindow : Window, ILinuxRecordingOverlaySurface
{
    private readonly LinuxOverlayPlacementPersistence _placement;
    private bool _restoring;

    public LinuxRecordingOverlayWindow(LinuxRecordingOverlayViewModel viewModel,
        ILinuxOverlayPlacementStore? placementStore = null)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        ShowActivated = LinuxOverlayInteractionPolicy.ShowActivated;
        Focusable = LinuxOverlayInteractionPolicy.Focusable;
        _placement = new(placementStore ?? new JsonLinuxOverlayPlacementStore());
        PointerPressed += BeginNonActivatingMove;
        PositionChanged += (_, _) => SavePosition();
        Opened += (_, _) =>
        {
            RestorePosition();
            LinuxOverlayWindowPolicy.TryApply(this);
        };
    }

    public void ShowBestEffort()
    {
        try
        {
            if (!IsVisible) Show();
        }
        catch { /* Overlay feedback must not block transcription. */ }
    }

    public void HideBestEffort()
    {
        try { if (IsVisible) Hide(); } catch { }
    }

    void IDisposable.Dispose()
    {
        _placement.Dispose();
        try { Close(); } catch { }
    }

    private void RestorePosition()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var pixelWidth = (int)Math.Round(Width * scale);
        var pixelHeight = (int)Math.Round(Height * scale);
        var point = LinuxOverlayPlacementCalculator.Restore(_placement.LoadBestEffort(),
            ToRect(screen.WorkingArea), pixelWidth, pixelHeight, scale);
        _restoring = true;
        try { Position = new(point.X, point.Y); }
        finally { _restoring = false; }
    }

    private void SavePosition()
    {
        if (_restoring || !IsVisible) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var placement = LinuxOverlayPlacementCalculator.Capture(new(Position.X, Position.Y),
            ToRect(screen.WorkingArea), (int)Math.Round(Width * scale), (int)Math.Round(Height * scale));
        _placement.SaveDebounced(placement);
    }

    private void BeginNonActivatingMove(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try
        {
            BeginMoveDrag(args);
            LinuxOverlayWindowPolicy.TryApply(this);
        }
        catch { /* Compositor support is best-effort. */ }
    }

    private static LinuxOverlayPixelRect ToRect(PixelRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);
}
