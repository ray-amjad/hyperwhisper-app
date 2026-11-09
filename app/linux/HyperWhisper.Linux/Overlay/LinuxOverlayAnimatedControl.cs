using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// A control the recording pill animates at ~30 fps. Its frame timer runs only while the control is
/// effectively visible in a shown window, so an idle pill does no work:
/// <list type="bullet">
/// <item>it starts when the control becomes effectively visible (IsVisible changes on it or an ancestor:
/// its state's face or the pill is shown again), when it is attached while visible, and, as a
/// fallback, from Render;</item>
/// <item>it stops when the control stops being effectively visible, when it is detached, and on any tick
/// that finds it hidden.</item>
/// </list>
/// The ticks run at Background priority, below input and rendering, so the animation can never
/// starve either. Each start from stopped calls <see cref="OnAnimationStarted"/>, so a new recording
/// begins from a fresh state rather than the last one's.
/// </summary>
internal abstract class LinuxOverlayAnimatedControl : Control
{
    protected static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTick;
    private readonly List<Visual> _watched = [];

    protected LinuxOverlayAnimatedControl()
    {
        // Not the (interval, priority, callback) constructor: that one starts the timer at once.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FrameInterval };
        _timer.Tick += (_, _) => OnTick();
        IsHitTestVisible = false;
    }

    /// <summary>True while the frame timer runs.</summary>
    internal bool IsFrameTimerRunning => _timer.IsEnabled;

    /// <summary>How many timer frames have run, for the render probe.</summary>
    internal int FrameCount { get; private set; }

    /// <summary>Moves the animation on by <paramref name="elapsed"/> of real time.</summary>
    internal abstract void Advance(TimeSpan elapsed);

    /// <summary>Called when the timer starts from stopped, before the first frame.</summary>
    protected virtual void OnAnimationStarted() { }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Posted, never inline: SyncTimer invalidates the visual, which throws inside a render pass.
        if (!_timer.IsEnabled) Dispatcher.UIThread.Post(SyncTimer, DispatcherPriority.Normal);
    }

    // Visual.IsEffectivelyVisibleChanged is not public, so watch IsVisible on this control and on every
    // ancestor up to the window: any of them changing can change this control's effective visibility.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Unwatch();
        for (Visual? visual = this; visual is not null; visual = visual.GetVisualParent())
        {
            visual.PropertyChanged += OnWatchedPropertyChanged;
            _watched.Add(visual);
        }
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unwatch();
        StopTimer();
    }

    private void OnWatchedPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Posted: the effective visibility of this control settles after the ancestor's own change event.
        if (e.Property == IsVisibleProperty) Dispatcher.UIThread.Post(SyncTimer, DispatcherPriority.Normal);
    }

    private void Unwatch()
    {
        foreach (var visual in _watched) visual.PropertyChanged -= OnWatchedPropertyChanged;
        _watched.Clear();
    }

    private bool IsShown() => IsEffectivelyVisible && TopLevel.GetTopLevel(this) is { IsVisible: true };

    private void SyncTimer()
    {
        if (!IsShown())
        {
            StopTimer();
            return;
        }
        if (_timer.IsEnabled) return;
        OnAnimationStarted();
        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        _timer.Start();
        InvalidateVisual();
    }

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
