using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HyperWhisper.Linux;

/// <summary>
/// The segmented input-level meter under the Settings → Sound microphone picker (spec: mic picker
/// in Settings). <see cref="Level"/> is the 0..1 display level of
/// <c>MicrophoneLevelTestViewModel.Level</c>, already mapped from dBFS. Idle, the level is 0 and
/// every segment is the plain grey border brush; a lit segment is the accent colour, and the top
/// segments turn amber so a clipping-close input reads as loud.
///
/// A StackPanel of fixed segments for the reason <see cref="GaugeBar"/> gives: never restyled, and
/// no template pass.
/// </summary>
public sealed class MicrophoneLevelBar : StackPanel
{
    public const int SegmentCount = 24;
    private const int LoudSegments = 4;

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<MicrophoneLevelBar, double>(nameof(Level));

    /// <summary>0..1. Values outside the range are clamped.</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>How many segments a level lights: a level above zero always lights one.</summary>
    public static int LitSegments(double level)
        => double.IsNaN(level) || level <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(level * SegmentCount), 0, SegmentCount);

    public MicrophoneLevelBar()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        ActualThemeVariantChanged += (_, _) => Refresh();
        for (var i = 0; i < SegmentCount; i++)
        {
            Children.Add(new Border
            {
                Width = 8,
                Height = 10,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 0, i == SegmentCount - 1 ? 0 : 3, 0),
            });
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty) Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    private void Refresh()
    {
        var lit = LitSegments(Level);
        var accent = Resolve("HwAccentBrush");
        var loud = Resolve("HwWarningBrush") ?? accent;
        var idle = Resolve("HwBorderBrush");
        for (var i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not Border segment) continue;
            segment.Background = i >= lit ? idle : i >= SegmentCount - LoudSegments ? loud : accent;
        }
    }

    private IBrush? Resolve(string key) => this.TryFindResource(key, out var value) ? value as IBrush : null;
}
