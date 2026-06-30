using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// UPDATE TOAST WINDOW
///
/// Pill-shaped toast notification that appears bottom-right when a background
/// update is detected. Auto-dismisses after 8 seconds with fade animations.
/// Clicking anywhere invokes the NetSparkle click handler (opens full update dialog).
///
/// Design matches ErrorToastWindow pattern.
/// </summary>
public partial class UpdateToastWindow : Window
{
    private readonly Action? _clickHandler;
    private readonly DispatcherTimer _autoDismissTimer;
    private const int AutoDismissSeconds = 8;

    public UpdateToastWindow(Action? clickHandler)
    {
        InitializeComponent();

        _clickHandler = clickHandler;

        _autoDismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AutoDismissSeconds)
        };
        _autoDismissTimer.Tick += (s, e) =>
        {
            _autoDismissTimer.Stop();
            DismissWithAnimation();
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        AnimateIn();
        _autoDismissTimer.Start();
        LoggingService.Debug("UpdateToastWindow: Shown");
    }

    private void Border_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _autoDismissTimer.Stop();
        LoggingService.Info("UpdateToastWindow: Clicked, opening update dialog");
        Close();
        _clickHandler?.Invoke();
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Bottom - Height - 16;
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

        var slideUp = new DoubleAnimation
        {
            From = Top + 10,
            To = Top,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        MainBorder.BeginAnimation(OpacityProperty, fadeIn);
        BeginAnimation(TopProperty, slideUp);
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

        var slideDown = new DoubleAnimation
        {
            From = Top,
            To = Top + 10,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (s, e) =>
        {
            Close();
            LoggingService.Debug("UpdateToastWindow: Auto-dismissed");
        };

        MainBorder.BeginAnimation(OpacityProperty, fadeOut);
        BeginAnimation(TopProperty, slideDown);
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoDismissTimer.Stop();
        base.OnClosed(e);
    }
}
