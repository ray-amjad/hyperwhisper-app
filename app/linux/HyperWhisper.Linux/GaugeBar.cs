using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HyperWhisper.Linux;

/// <summary>
/// The five-segment rating meter in the Model Library's "Speed / Accuracy" column, one bar for
/// speed and one for accuracy. Geometry is the Windows control's, segment for segment
/// (app/windows/HyperWhisper/Views/Controls/GaugeBar.xaml): five 12x4 bars, CornerRadius 1, 3px
/// apart with no trailing margin, so the whole bar is 5*12 + 4*3 = 72px wide.
///
/// It derives from StackPanel rather than carrying a ControlTemplate because the segments are
/// fixed furniture, never restyled, and a template would put a second layout pass in the row
/// template of a list that recycles.
/// </summary>
public sealed class GaugeBar : StackPanel
{
    private const int SegmentCount = 5;

    /// <summary>How many segments are lit, 0-5. Values outside the range are clamped.</summary>
    public static readonly StyledProperty<int> RatingProperty =
        AvaloniaProperty.Register<GaugeBar, int>(nameof(Rating));

    /// <summary>
    /// The resource KEY of the lit-segment brush, not the brush. The row's readiness decides it
    /// (HwAccentBrush / HwTextSecondaryBrush / HwWarningBrush), so it changes as the row changes
    /// and has to be re-resolved rather than bound once.
    /// </summary>
    public static readonly StyledProperty<string?> FilledBrushKeyProperty =
        AvaloniaProperty.Register<GaugeBar, string?>(nameof(FilledBrushKey));

    public int Rating
    {
        get => GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    public string? FilledBrushKey
    {
        get => GetValue(FilledBrushKeyProperty);
        set => SetValue(FilledBrushKeyProperty, value);
    }

    public GaugeBar()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        // A light/dark switch changes which brush OBJECT each key resolves to, so the keys have
        // to be looked up again. The colours inside a given brush already follow the theme on
        // their own, being DynamicResource-bound in Brushes.axaml; this is only about the swap.
        ActualThemeVariantChanged += (_, _) => Refresh();
        for (var i = 0; i < SegmentCount; i++)
        {
            Children.Add(new Border
            {
                Width = 12,
                Height = 4,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 0, i == SegmentCount - 1 ? 0 : 3, 0),
            });
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RatingProperty || change.Property == FilledBrushKeyProperty) Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Resource lookup walks the visual tree, so it can only succeed once we are in it.
        // A recycled row re-attaches, which is exactly when the brushes need re-resolving.
        Refresh();
    }

    private void Refresh()
    {
        var filled = Resolve(FilledBrushKey) ?? Resolve("HwAccentBrush");
        // Windows leaves the unlit segments on the plain border brush, so the bar still reads
        // as a five-step scale rather than as a shorter bar.
        var unfilled = Resolve("HwBorderBrush");
        var rating = Math.Clamp(Rating, 0, SegmentCount);

        for (var i = 0; i < Children.Count; i++)
        {
            if (Children[i] is Border segment) segment.Background = i < rating ? filled : unfilled;
        }
    }

    private IBrush? Resolve(string? key)
        => key is { Length: > 0 } && this.TryFindResource(key, out var value) ? value as IBrush : null;
}
