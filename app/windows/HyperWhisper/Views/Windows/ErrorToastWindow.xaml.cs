// ERROR TOAST WINDOW
// A compact, auto-dismissing error pill that appears above the recording dialog.
// Matches macOS InlineErrorToast design: a 360px-wide pill with warning icon,
// error message, countdown timer, and optional settings button.
// The pill is 40px tall for a short message and grows downwards for a longer
// one - the message wraps rather than being trimmed at one line (issue #489).
//
// BEHAVIOR:
// - Appears with fade-in animation
// - Auto-dismisses after countdown (default 8 seconds)
// - Dismisses with fade-out animation
// - Positioned above the recording dialog (or bottom-center of screen)

using System;
using System.Windows;
using System.Windows.Threading;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

public partial class ErrorToastWindow : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private int _remainingSeconds;
    private const int DefaultCountdownSeconds = 8;

    public event EventHandler? SettingsRequested;
    public event EventHandler? Dismissed;

    public ErrorToastWindow()
    {
        InitializeComponent();

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += OnCountdownTick;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAboveRecordingDialog();
    }

    /// <summary>
    /// Shows the error toast with the specified message.
    /// </summary>
    /// <param name="message">The error message to display</param>
    /// <param name="showSettingsButton">Whether to show the "Open Settings" button</param>
    /// <param name="countdownSeconds">Auto-dismiss countdown in seconds (default 8)</param>
    /// <param name="guidanceText">Optional guidance text shown below the error message</param>
    public void ShowError(string message, bool showSettingsButton = false, int countdownSeconds = DefaultCountdownSeconds, string? guidanceText = null)
    {
        ErrorMessage.Text = message;
        SettingsButton.Visibility = showSettingsButton ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(guidanceText))
        {
            GuidanceText.Text = guidanceText;
            GuidanceText.Visibility = Visibility.Visible;
            // Use longer countdown when guidance is present so user has time to read
            countdownSeconds = Math.Max(countdownSeconds, 12);
        }
        else
        {
            GuidanceText.Visibility = Visibility.Collapsed;
        }

        _remainingSeconds = countdownSeconds;
        CountdownText.Text = _remainingSeconds.ToString();

        Show();
        PositionAboveRecordingDialog();
        AnimateIn();

        _countdownTimer.Start();

        LoggingService.Debug($"ErrorToast: Showing '{message}' (showSettings={showSettingsButton}, countdown={countdownSeconds}s, hasGuidance={!string.IsNullOrEmpty(guidanceText)})");
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        CountdownText.Text = _remainingSeconds.ToString();

        if (_remainingSeconds <= 0)
        {
            _countdownTimer.Stop();
            DismissWithAnimation();
        }
    }

    private void PositionAboveRecordingDialog()
    {
        // The height depends on how many lines the message wrapped onto, so measure
        // before placing: Show() on a re-used window does not always flush layout
        // first, and a stale height puts the pill in the wrong place.
        UpdateLayout();
        // ActualHeight is what the last layout pass produced; Height stays NaN while
        // SizeToContent owns it, and MinHeight is the pill's floor before first layout.
        var height = ActualHeight > 0 ? ActualHeight : MinHeight;

        // Try to find the recording overlay window and position above it
        var recordingWindow = ToastWindowBehavior.FindVisibleRecordingOverlay();
        var workArea = SystemParameters.WorkArea;

        if (recordingWindow != null && recordingWindow.IsVisible)
        {
            // Position centered above the recording dialog, 12px gap (matching macOS)
            Left = recordingWindow.Left + (recordingWindow.ActualWidth - Width) / 2;
            Top = recordingWindow.Top - height - 12;

            // A tall toast above a dialog near the top of the screen would otherwise
            // start off the top edge and hide its own first lines. Only for a dialog
            // inside the primary work area: SystemParameters.WorkArea describes no
            // other monitor - which is the assumption RecordingOverlayWindow's own
            // PositionOverlay makes too - and a dialog on a monitor ABOVE the primary
            // has a legitimately negative Top that must be left alone.
            if (Top < workArea.Top && recordingWindow.Top >= workArea.Top)
            {
                Top = workArea.Top;
            }
        }
        else
        {
            // Fallback: bottom-center of work area, 80 pixels from bottom
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = Math.Max(workArea.Top, workArea.Bottom - height - 80);
        }
    }

    private void AnimateIn()
    {
        ToastWindowBehavior.AnimateIn(this, MainBorder, slideDistance: 10);
    }

    private void DismissWithAnimation()
    {
        ToastWindowBehavior.AnimateOut(this, MainBorder, slideDistance: 10, () =>
        {
            Hide();
            Dismissed?.Invoke(this, EventArgs.Empty);
            LoggingService.Debug("ErrorToast: Dismissed");
        });
    }

    public void DismissImmediately()
    {
        _countdownTimer.Stop();
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        DismissWithAnimation();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        _countdownTimer.Stop();
        base.OnClosed(e);
    }
}
