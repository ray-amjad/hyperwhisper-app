using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// Where the toast sends the user when they press "Open Settings".
///
/// Windows expresses this as two fields on ErrorToastEventArgs rather than an enum, with
/// OpenApiKeysManager taking precedence over SettingsSection. The three reachable outcomes are
/// modelled directly here so a caller cannot express a combination Windows never produces.
/// </summary>
internal enum LinuxErrorToastAction
{
    /// <summary>No button. The toast is a notice only.</summary>
    None,

    /// <summary>Windows: OpenApiKeysManager. Model Library with the API-keys modal auto-opened.</summary>
    ApiKeys,

    /// <summary>Windows: SettingsSection == "Models". The Model Library, no modal.</summary>
    ModelLibrary,

    /// <summary>Windows: SettingsSection == "Cloud". Settings, HyperWhisper Cloud section.</summary>
    CloudSettings,
}

/// <summary>
/// The Linux port of Windows' <c>ErrorToastWindow</c>.
///
/// Failures used to land in an 11px status-bar line inside the main window, which is exactly where
/// nobody is looking: the whole point of a dictation failure is that the user is typing into some
/// OTHER application at the time. This is the same 360-wide pill Windows floats above the recording
/// overlay, with the same countdown and the same "Open Settings" jump.
/// </summary>
internal sealed partial class LinuxErrorToastWindow : Window
{
    /// <summary>Windows' DefaultCountdownSeconds.</summary>
    private const int DefaultCountdownSeconds = 8;

    /// <summary>Windows raises the countdown to at least this when guidance text is present.</summary>
    private const int GuidanceCountdownSeconds = 12;

    /// <summary>The gap Windows leaves between the toast and the recording overlay above it.</summary>
    private const int OverlayGapDip = 12;

    /// <summary>Windows' fallback: bottom-centre of the work area, this far up.</summary>
    private const int FallbackBottomMarginDip = 80;

    /// <summary>The 10px slide Windows animates in and out over.</summary>
    private const int SlideDip = 10;

    // Resolved by name rather than through generated fields, the way every other window in this
    // project does it (see ConfirmWindow and LinuxRecordingOverlayWindow).
    private readonly Border? _border;
    private readonly TextBlock? _message;
    private readonly TextBlock? _countdownText;
    private readonly TextBlock? _guidance;
    private readonly Button? _settingsButton;

    private readonly DispatcherTimer _countdown;
    private DispatcherTimer? _slide;
    private int _remainingSeconds;
    private LinuxErrorToastAction _action = LinuxErrorToastAction.None;

    /// <summary>
    /// Where the toast belongs, held rather than read back from <see cref="Window.Position"/>.
    ///
    /// The Position GETTER is stale immediately after the setter on X11 -- it still reports the
    /// old value until the compositor answers. Reading it to find the slide's destination
    /// therefore returned 0,0 and the animation drove the toast to the top-left corner, half off
    /// the screen, however correctly it had just been placed.
    /// </summary>
    private PixelPoint _target;
    private bool _placed;

    /// <summary>Raised when "Open Settings" is pressed, carrying where to go.</summary>
    public event EventHandler<LinuxErrorToastAction>? SettingsRequested;

    public LinuxErrorToastWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Set here, not in the XAML: the localization test matches (Title|Text|Content|...)="…"
        // lexically, so a literal SizeToContent="Height" reads to it as Content="Height".
        SizeToContent = SizeToContent.Height;
        // The toast is feedback, never a keyboard target -- the same rule the recording overlay
        // follows, so dictation keeps going to the application the user is actually typing into.
        Focusable = LinuxOverlayInteractionPolicy.Focusable;

        _border = this.FindControl<Border>("ToastBorder");
        _message = this.FindControl<TextBlock>("ToastMessage");
        _countdownText = this.FindControl<TextBlock>("ToastCountdown");
        _guidance = this.FindControl<TextBlock>("ToastGuidance");
        _settingsButton = this.FindControl<Button>("ToastSettingsButton");

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += OnCountdownTick;

        SetOpacityTransition(TimeSpan.FromMilliseconds(200), new QuadraticEaseOut());
        Opened += (_, _) =>
        {
            LinuxOverlayWindowPolicy.TryApply(this);
            // Windows positions on Loaded AND again after Show(), because the measured height is
            // only real once the window exists. Placing here as well is what stops the toast
            // being left at the compositor's default 0,0.
            PlaceOnScreen();
        };
    }

    /// <summary>
    /// Show a failure. Mirrors Windows' <c>ShowError(message, showSettingsButton, countdownSeconds,
    /// guidanceText)</c>, including the rule that guidance text lengthens the countdown.
    /// </summary>
    public void ShowError(string message, LinuxErrorToastAction action = LinuxErrorToastAction.None,
        string? guidanceText = null, int countdownSeconds = DefaultCountdownSeconds)
    {
        try
        {
            _action = action;
            if (_message is not null) _message.Text = message;
            if (_settingsButton is not null) _settingsButton.IsVisible = action != LinuxErrorToastAction.None;

            if (!string.IsNullOrEmpty(guidanceText))
            {
                if (_guidance is not null)
                {
                    _guidance.Text = guidanceText;
                    _guidance.IsVisible = true;
                }
                // Math.Max, not an assignment: an explicitly longer countdown still wins.
                countdownSeconds = Math.Max(countdownSeconds, GuidanceCountdownSeconds);
            }
            else if (_guidance is not null)
            {
                _guidance.IsVisible = false;
            }

            _remainingSeconds = countdownSeconds;
            if (_countdownText is not null)
                _countdownText.Text = _remainingSeconds.ToString(System.Globalization.CultureInfo.CurrentCulture);

            if (!IsVisible) Show();
            // Windows positions after Show() as well as on Loaded, because the measured height is
            // only real once the window exists -- and this one grows with the guidance line.
            Dispatcher.UIThread.Post(() => { PlaceOnScreen(); AnimateIn(); }, DispatcherPriority.Loaded);

            _countdown.Stop();
            _countdown.Start();
        }
        catch { /* Error feedback must never become a second failure. */ }
    }

    /// <summary>Hide with no animation, the Windows <c>DismissImmediately</c>.</summary>
    public void DismissImmediately()
    {
        try
        {
            _countdown.Stop();
            StopSlide();
            if (_border is not null) _border.Opacity = 0;
            if (IsVisible) Hide();
        }
        catch { }
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_countdownText is not null)
            _countdownText.Text = Math.Max(0, _remainingSeconds)
                .ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (_remainingSeconds > 0) return;
        _countdown.Stop();
        DismissWithAnimation();
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        _countdown.Stop();
        var action = _action;
        DismissWithAnimation();
        if (action == LinuxErrorToastAction.None) return;
        foreach (var handler in SettingsRequested?.GetInvocationList() ?? [])
            try { ((EventHandler<LinuxErrorToastAction>)handler)(this, action); } catch { }
    }

    // =====================================================================================
    // PLACEMENT
    // Windows centres the toast over the recording overlay with a 12px gap, and falls back to
    // bottom-centre of the work area 80px up when the overlay is not on screen.
    // =====================================================================================

    private void PlaceOnScreen()
    {
        try
        {
            // ScreenFromWindow is null until the window is mapped, and Primary can be null too on
            // a bare X server. Falling through to the first screen is what keeps the toast from
            // being abandoned at the compositor's default 0,0 -- which is where it sat, half off
            // the top of the display, before this fallback existed.
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary
                ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
            if (screen is null) return;
            var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
            var work = screen.WorkingArea;
            if (work.Width <= 0 || work.Height <= 0) work = screen.Bounds;
            if (work.Width <= 0 || work.Height <= 0) return;
            var width = (int)Math.Round(Bounds.Width * scale);
            var height = (int)Math.Round(Bounds.Height * scale);
            if (width <= 0) width = (int)Math.Round(Width * scale);
            if (height <= 0) height = (int)Math.Round(MinHeight * scale);

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

    /// <summary>
    /// The anchor is the recording overlay, and only while it is actually on screen -- Windows
    /// checks IsVisible for the same reason: a hidden overlay still has a stale position.
    /// </summary>
    private static Window? FindRecordingOverlay()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime lifetime) return null;
        foreach (var window in lifetime.Windows)
            if (window is LinuxRecordingOverlayWindow && window.IsVisible) return window;
        return null;
    }

    // =====================================================================================
    // ANIMATION
    // 200ms in on a quadratic ease-out, 150ms out on a quadratic ease-in, each paired with a
    // 10px slide. Window position is an integer PixelPoint, so the slide is stepped by a timer
    // rather than by a transition.
    // =====================================================================================

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
                    start.X + (int)Math.Round((target.X - start.X) * progress),
                    start.Y + (int)Math.Round((target.Y - start.Y) * progress));
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
