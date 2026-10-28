using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// A control the recording pill animates at ~30 fps. Its frame timer runs only while the control is
/// effectively visible in a shown window: a tick that finds it hidden (its state's panel collapsed,
/// the pill hidden) or detached stops the timer, so an idle pill does no work. Render only runs once
/// the control is shown again, so that is where a stopped timer restarts.
/// </summary>
internal abstract class LinuxOverlayAnimatedControl : Control
{
    protected static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTick;

    protected LinuxOverlayAnimatedControl()
    {
        // Not the (interval, priority, callback) constructor: that one starts the timer at once.
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += (_, _) => OnTick();
        IsHitTestVisible = false;
    }

    /// <summary>True while the frame timer runs.</summary>
    internal bool IsFrameTimerRunning => _timer.IsEnabled;

    /// <summary>How many timer frames have run, for the render probe.</summary>
    internal int FrameCount { get; private set; }

    /// <summary>Moves the animation on by <paramref name="elapsed"/> of real time.</summary>
    internal abstract void Advance(TimeSpan elapsed);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_timer.IsEnabled || !IsShown()) return;
        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

    private bool IsShown() => IsEffectivelyVisible && TopLevel.GetTopLevel(this) is { IsVisible: true };

    private void StopTimer()
    {
        _timer.Stop();
        _clock.Reset();
    }

    private void OnTick()
    {
        if (!IsShown())
        {
            StopTimer();
            return;
        }
        FrameCount++;
        var now = _clock.Elapsed;
        Advance(now - _lastTick);
        _lastTick = now;
        InvalidateVisual();
    }
}
