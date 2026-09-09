using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// The Linux port of Windows' <c>FileTranscriptionProgressWindow</c>.
///
/// Linux drove file-import progress from an inline <c>ProgressBar</c> and a "Cancel import" button
/// inside Home's Linux-only "Record and Transcribe" card, so both were invisible from any other
/// page and from a hidden window — which is where the user normally is during an import that takes
/// minutes. Windows floats a small modeless always-on-top window instead, and this is the same
/// one: 300x110, bottom-centre of the work area, never focusable, one pill Cancel.
/// </summary>
internal sealed partial class LinuxFileTranscriptionProgressWindow : Window
{
    /// <summary>Windows' fallback margin, shared with the two toasts.</summary>
    private const int BottomMarginDip = 80;

    private readonly TextBlock? _fileName;
    private readonly TextBlock? _percent;
    private readonly Border? _track;
    private readonly Border? _fill;

    /// <summary>Raised when the pill Cancel is pressed. The caller runs the actual cancellation.</summary>
    public event EventHandler? CancelRequested;

    public LinuxFileTranscriptionProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _fileName = this.FindControl<TextBlock>("ProgressFileName");
        _percent = this.FindControl<TextBlock>("ProgressPercent");
        _track = this.FindControl<Border>("ProgressTrack");
        _fill = this.FindControl<Border>("ProgressFill");

        Opened += (_, _) =>
        {
            LinuxOverlayWindowPolicy.TryApply(this);
            PlaceOnScreen();
        };
    }

    /// <summary>Show the window for a file, at zero progress.</summary>
    public void ShowForFile(string fileName)
    {
        try
        {
            if (_fileName is not null) _fileName.Text = fileName;
            SetProgress(0);
            if (!IsVisible) Show();
            Dispatcher.UIThread.Post(PlaceOnScreen, DispatcherPriority.Loaded);
        }
        catch { /* Progress feedback must never break the import it is reporting on. */ }
    }

    /// <summary>Set progress as a 0..1 fraction, the shape Recording.ImportProgress already has.</summary>
    public void SetProgress(double fraction)
    {
        try
        {
            var clamped = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
            if (_percent is not null)
                _percent.Text = ((int)Math.Round(clamped * 100))
                    .ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
            // The fill is a plain Border rather than a ProgressBar so the 6px/3px geometry is
            // exactly the Windows one; its width therefore has to follow the track by hand.
            if (_track is not null && _fill is not null)
            {
                var available = _track.Bounds.Width;
                if (available <= 0) available = Width - 40 - 8 - 32;
                _fill.Width = Math.Max(0, available * clamped);
            }
        }
        catch { }
    }

    public void HideProgress()
    {
        try { if (IsVisible) Hide(); }
        catch { }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        foreach (var handler in CancelRequested?.GetInvocationList() ?? [])
            try { ((EventHandler)handler)(this, EventArgs.Empty); } catch { }
    }

    private void PlaceOnScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary
                ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
            if (screen is null) return;
            var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
            var work = screen.WorkingArea;
            if (work.Width <= 0 || work.Height <= 0) work = screen.Bounds;
            if (work.Width <= 0 || work.Height <= 0) return;
            var width = (int)Math.Round(Width * scale);
            var height = (int)Math.Round(Height * scale);
            Position = new PixelPoint(
                work.X + (work.Width - width) / 2,
                work.Bottom - height - (int)Math.Round(BottomMarginDip * scale));
        }
        catch { }
    }
}
