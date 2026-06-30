// ERROR TOAST WINDOW
// A compact, auto-dismissing error pill that appears above the recording dialog.
// Matches macOS InlineErrorToast design: 360x40 pill with warning icon,
// truncated error message, countdown timer, and optional settings button.
//
// BEHAVIOR:
// - Appears with fade-in animation
// - Auto-dismisses after countdown (default 8 seconds)
// - Dismisses with fade-out animation
// - Positioned above the recording dialog (or bottom-center of screen)

using System;
using System.Windows;
using System.Windows.Media.Animation;
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
        // Try to find the recording overlay window and position above it
        var recordingWindow = FindRecordingOverlayWindow();

        if (recordingWindow != null && recordingWindow.IsVisible)
        {
            // Position centered above the recording dialog, 12px gap (matching macOS)
            Left = recordingWindow.Left + (recordingWindow.ActualWidth - Width) / 2;
            Top = recordingWindow.Top - Height - 12;
        }
        else
        {
            // Fallback: bottom-center of work area, 80 pixels from bottom
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Bottom - Height - 80;
        }
    }

    private static Window? FindRecordingOverlayWindow()
    {
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (window is RecordingOverlayWindow && window.IsVisible)
            {
                return window;
            }
        }
        return null;
    }

    private void AnimateIn()
    {
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        var slideDown = new DoubleAnimation
        {
            From = Top - 10,
            To = Top,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        MainBorder.BeginAnimation(OpacityProperty, fadeIn);
        BeginAnimation(TopProperty, slideDown);
    }

    private void DismissWithAnimation()
    {
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        var slideUp = new DoubleAnimation
        {
            From = Top,
            To = Top - 10,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (s, e) =>
        {
            Hide();
            Dismissed?.Invoke(this, EventArgs.Empty);
            LoggingService.Debug("ErrorToast: Dismissed");
        };

        MainBorder.BeginAnimation(OpacityProperty, fadeOut);
        BeginAnimation(TopProperty, slideUp);
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
