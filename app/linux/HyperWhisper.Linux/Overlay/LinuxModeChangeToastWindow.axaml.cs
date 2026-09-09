using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// The Linux port of Windows' <c>ModeChangeToastWindow</c>.
///
/// Cycling the mode is a global-hotkey action, so the main window is normally not on screen when
/// it happens. Linux only annotated the recording overlay (<c>LinuxRecordingOverlayController
/// .ModeChanged</c>), which means the confirmation was invisible unless the user happened to be
/// recording at the time — and the whole point of the mode hotkey is to switch before you record.
/// Windows floats this 200x36 pill for two seconds instead, in the same place the error toast
/// uses: centred over the recording overlay when one is up, bottom-centre of the work area
/// otherwise.
/// </summary>
internal sealed partial class LinuxModeChangeToastWindow : Window
{
    /// <summary>Windows' toast lifetime for a mode change: two seconds.</summary>
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(2);

    /// <summary>The gap Windows leaves between the toast and the recording overlay above it.</summary>
    private const int OverlayGapDip = 12;

    /// <summary>Windows' fallback: bottom-centre of the work area, this far up.</summary>
    private const int FallbackBottomMarginDip = 80;

    /// <summary>The 10px slide Windows animates in and out over.</summary>
    private const int SlideDip = 10;

    private readonly Border? _border;
    private readonly TextBlock? _modeText;
    private readonly DispatcherTimer _dismiss;
    private DispatcherTimer? _slide;
    private PixelPoint _target;
    private bool _placed;

    public LinuxModeChangeToastWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _border = this.FindControl<Border>("ToastBorder");
        _modeText = this.FindControl<TextBlock>("ToastModeText");

        _dismiss = new DispatcherTimer { Interval = DisplayDuration };
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); DismissWithAnimation(); };

        Opened += (_, _) =>
        {
            LinuxOverlayWindowPolicy.TryApply(this);
            PlaceOnScreen();
        };
    }

    /// <summary>
    /// Show "Mode: {name}". The caller passes the formatted string, so the mode.change.toast
    /// lookup stays with the localization bridge in the main window rather than being repeated in
    /// an overlay that has no access to it.
    /// </summary>
    public void ShowMode(string formattedModeLabel)
    {
        try
        {
            if (_modeText is not null) _modeText.Text = formattedModeLabel;
            if (!IsVisible) Show();
            // Placed after Show as well as on Opened: the measured size is only real once the
            // window exists, and the fixed 200x36 still has to be centred against a live screen.
            Dispatcher.UIThread.Post(() => { PlaceOnScreen(); AnimateIn(); }, DispatcherPriority.Loaded);
            _dismiss.Stop();
            _dismiss.Start();
        }
        catch { /* A mode confirmation must never become a failure of its own. */ }
    }

    public void DismissImmediately()
    {
        try
        {
            _dismiss.Stop();
            StopSlide();
            if (_border is not null) _border.Opacity = 0;
            if (IsVisible) Hide();
        }
        catch { }
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

            if (FindRecordingOverlay() is { } overlay)
            {
                var overlayWidth = (int)Math.Round(overlay.Bounds.Width * scale);
                if (overlayWidth <= 0) overlayWidth = (int)Math.Round(overlay.Width * scale);
                _target = new PixelPoint(
                    overlay.Position.X + (overlayWidth - width) / 2,
                    overlay.Position.Y - height - (int)Math.Round(OverlayGapDip * scale));
            }
            else
            {
                _target = new PixelPoint(
                    work.X + (work.Width - width) / 2,
                    work.Bottom - height - (int)Math.Round(FallbackBottomMarginDip * scale));
            }

            _placed = true;
            Position = _target;
        }
        catch { }
    }

    private static Window? FindRecordingOverlay()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime lifetime) return null;
        foreach (var window in lifetime.Windows)
            if (window is LinuxRecordingOverlayWindow && window.IsVisible) return window;
        return null;
    }

    private void AnimateIn()
    {
        if (_border is null || !_placed) return;
        SetOpacityTransition(TimeSpan.FromMilliseconds(200), new QuadraticEaseOut());
        _border.Opacity = 1;
        Slide(new PixelPoint(_target.X, _target.Y - SlideDip), _target, TimeSpan.FromMilliseconds(200));
    }

    private void DismissWithAnimation()
    {
        if (_border is null || !_placed) { DismissImmediately(); return; }
        SetOpacityTransition(TimeSpan.FromMilliseconds(150), new QuadraticEaseIn());
        _border.Opacity = 0;
        Slide(_target, new PixelPoint(_target.X, _target.Y - SlideDip), TimeSpan.FromMilliseconds(150));
        DispatcherTimer.RunOnce(() => { try { if (IsVisible) Hide(); } catch { } },
            TimeSpan.FromMilliseconds(160));
    }

    private void SetOpacityTransition(TimeSpan duration, Easing easing)
    {
        if (_border is null) return;
        _border.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = easing },
        ];
    }

    private void Slide(PixelPoint start, PixelPoint target, TimeSpan duration)
    {
        StopSlide();
        Position = start;
        if (start == target) return;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slide = timer;
        timer.Tick += (_, _) =>
        {
            var progress = duration <= TimeSpan.Zero
                ? 1
                : Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            try
            {
                Position = new PixelPoint(
                    (int)Math.Round(start.X + ((target.X - start.X) * progress)),
                    (int)Math.Round(start.Y + ((target.Y - start.Y) * progress)));
            }
            catch { }
            if (progress >= 1) StopSlide();
        };
        timer.Start();
    }

    private void StopSlide()
    {
        _slide?.Stop();
        _slide = null;
    }
}
