// MODE CHANGE TOAST WINDOW
// A compact, auto-dismissing pill that appears above the recording dialog
// when the user cycles transcription modes via keyboard shortcut.
// Matches macOS ModeChangeToast design: 200x36 pill with sparkles icon + mode name.
//
// BEHAVIOR:
// - Appears with fade-in + slide-down animation (200ms)
// - Auto-dismisses after 2 seconds
// - Dismisses with fade-out + slide-up animation (150ms)
// - Positioned 12px above the recording overlay
// - Rapid cycling: previous toast dismissed immediately before showing new one

using System;
using System.Windows;
using System.Windows.Threading;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Utilities;

namespace HyperWhisper.Views.Windows;

public partial class ModeChangeToastWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;

    public event EventHandler? Dismissed;

    public ModeChangeToastWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _dismissTimer.Tick += OnDismissTimerTick;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAboveRecordingDialog();
    }

    /// <summary>
    /// Shows the mode change toast with the specified mode name.
    /// </summary>
    public void ShowMode(string modeName)
    {
        ModeText.Text = Loc.S("mode.change.toast", modeName);

        Show();
        PositionAboveRecordingDialog();
        AnimateIn();

        _dismissTimer.Stop();
        _dismissTimer.Start();

        LoggingService.Debug($"ModeToast: Showing '{modeName}'");
    }

    private void OnDismissTimerTick(object? sender, EventArgs e)
    {
        _dismissTimer.Stop();
        DismissWithAnimation();
    }

    private void PositionAboveRecordingDialog()
    {
        var recordingWindow = ToastWindowBehavior.FindVisibleRecordingOverlay();
        var overlay = recordingWindow != null && recordingWindow.IsVisible ? recordingWindow : null;

        // A held slide animation would mask the Top set below (issue #1501).
        ToastWindowBehavior.ReleasePosition(this);

        // In the DIP of the overlay's monitor (or the primary one), not
        // SystemParameters.WorkArea, which keeps the DPI the app started at.
        MonitorWorkArea.Place(this,
            () => overlay != null ? MonitorWorkArea.OfWindow(overlay) : MonitorWorkArea.Primary(),
            workArea =>
            {
                if (overlay != null)
                {
                    var anchor = ToastWindowBehavior.OverlayBounds(overlay);

                    // Position 12 pixels above the recording dialog (matching macOS gap)
                    return new System.Windows.Point(
                        anchor.Left + (anchor.Width - Width) / 2,
                        anchor.Top - Height - 12);
                }

                // Fallback: center on the work area (offset by a taskbar docked left, #1940),
                // position as if recording overlay is at bottom
                return new System.Windows.Point(
                    workArea.Left + (workArea.Width - Width) / 2,
                    workArea.Bottom - Height - 80);
            });
    }

    private void AnimateIn()
    {
        ToastWindowBehavior.AnimateIn(this, MainBorder, slideDistance: 20);
    }

    private void DismissWithAnimation()
    {
        ToastWindowBehavior.AnimateOut(this, MainBorder, slideDistance: 10, () =>
        {
            Hide();
            Dismissed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void DismissImmediately()
    {
        _dismissTimer.Stop();
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismissTimer.Stop();
        base.OnClosed(e);
    }
}
