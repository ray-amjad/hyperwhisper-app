using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace HyperWhisper.Views.Windows;

internal static class ToastWindowBehavior
{
    private static readonly Duration ShowDuration = TimeSpan.FromMilliseconds(200);
    private static readonly Duration HideDuration = TimeSpan.FromMilliseconds(150);

    public static Window? FindVisibleRecordingOverlay()
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

    public static void AnimateIn(Window window, UIElement content, double slideDistance)
    {
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = ShowDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        var slideDown = new DoubleAnimation
        {
            From = window.Top - slideDistance,
            To = window.Top,
            Duration = ShowDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        content.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        window.BeginAnimation(Window.TopProperty, slideDown);
    }

    public static void AnimateOut(Window window, UIElement content, double slideDistance, Action completed)
    {
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = HideDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        var slideUp = new DoubleAnimation
        {
            From = window.Top,
            To = window.Top - slideDistance,
            Duration = HideDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) => completed();

        content.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        window.BeginAnimation(Window.TopProperty, slideUp);
    }
}
