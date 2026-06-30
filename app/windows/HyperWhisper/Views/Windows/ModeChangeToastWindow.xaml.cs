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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HyperWhisper.Localization;
using HyperWhisper.Services;

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
        var recordingWindow = FindRecordingOverlayWindow();

        if (recordingWindow != null && recordingWindow.IsVisible)
        {
            // Position 12 pixels above the recording dialog (matching macOS gap)
            Left = recordingWindow.Left + (recordingWindow.Width - Width) / 2;
            Top = recordingWindow.Top - Height - 12;
        }
        else
        {
            // Fallback: center on screen, position as if recording overlay is at bottom
            var workArea = SystemParameters.WorkArea;
            Left = (workArea.Width - Width) / 2;
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
            From = Top - 20,
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
        };

        MainBorder.BeginAnimation(OpacityProperty, fadeOut);
        BeginAnimation(TopProperty, slideUp);
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
