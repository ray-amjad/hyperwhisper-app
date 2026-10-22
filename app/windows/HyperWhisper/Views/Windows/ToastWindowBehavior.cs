using System;
using System.Windows;
using System.Windows.Media.Animation;
using HyperWhisper.Utilities;

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

    /// <summary>
    /// The overlay's bounds in the DIP of the monitor it is on: the unit
    /// <see cref="MonitorWorkArea.Place"/> hands a toast's placement. Left/Top
    /// and size are in the overlay's own DIP, which is that monitor's once it has settled
    /// there; they are rebased in case it has not (issue #1501).
    /// </summary>
    public static Rect OverlayBounds(Window overlay)
    {
        var width = overlay.ActualWidth > 0 ? overlay.ActualWidth : overlay.Width;
        var height = overlay.ActualHeight > 0 ? overlay.ActualHeight : overlay.Height;
        var topLeft = new System.Windows.Point(overlay.Left, overlay.Top);

        if (MonitorWorkArea.OfWindow(overlay) is { } monitor)
        {
            var from = MonitorWorkArea.WindowScale(overlay);
            topLeft = MonitorWorkArea.Rebase(topLeft, from, monitor.Scale);
            var size = MonitorWorkArea.Rebase(new System.Windows.Point(width, height), from, monitor.Scale);
            width = size.X;
            height = size.Y;
        }

        return new Rect(topLeft.X, topLeft.Y, width, height);
    }

    /// <summary>
    /// Drops the slide animation's hold on <see cref="Window.TopProperty"/>.
    /// AnimateIn and AnimateOut end with FillBehavior.HoldEnd, which keeps the
    /// animated Top over any value set later, so a re-used toast would otherwise
    /// reappear where it was first shown, in whatever DPI that was.
    /// </summary>
    public static void ReleasePosition(Window window) =>
        window.BeginAnimation(Window.TopProperty, null);

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
