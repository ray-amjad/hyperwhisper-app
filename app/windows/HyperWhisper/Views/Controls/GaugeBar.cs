using System.Windows;
using Brush = System.Windows.Media.Brush;
using DrawingContext = System.Windows.Media.DrawingContext;
using Size = System.Windows.Size;
using SystemColors = System.Windows.SystemColors;

namespace HyperWhisper.Views.Controls;

/// <summary>
/// A five-segment rating gauge that mirrors the macOS ModelRow speed/accuracy
/// bars: the first <see cref="Rating"/> segments use <see cref="FilledBrush"/>
/// and the rest use <see cref="UnfilledBrush"/>.
/// </summary>
/// <remarks>
/// Drawn in <see cref="OnRender"/> with no XAML and no child elements. The
/// Model Library shows two per row, 158 on one page, and a UserControl with five
/// <c>Border</c>s cost about 100 ms of UI-thread time per visit in XAML parsing,
/// layout and brush registration (#2385).
/// </remarks>
public class GaugeBar : FrameworkElement
{
    private const int SegmentCount = 5;
    private const double SegmentWidth = 12;
    private const double SegmentHeight = 4;
    private const double SegmentGap = 3;
    private const double CornerRadius = 1;

    public GaugeBar()
    {
        if (UnfilledBrush is null) SetResourceReference(UnfilledBrushProperty, "BorderBrush");
    }

    public static readonly DependencyProperty RatingProperty =
        DependencyProperty.Register(
            nameof(Rating), typeof(int), typeof(GaugeBar),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledBrushProperty =
        DependencyProperty.Register(
            nameof(FilledBrush), typeof(Brush), typeof(GaugeBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Lets callers pass a theme resource KEY (e.g. "AccentBrush") instead of a
    // resolved brush, so the fill tracks light/dark switches like a DynamicResource.
    public static readonly DependencyProperty FilledBrushKeyProperty =
        DependencyProperty.Register(
            nameof(FilledBrushKey), typeof(string), typeof(GaugeBar),
            new PropertyMetadata(null, OnFilledBrushKeyChanged));

    public static readonly DependencyProperty UnfilledBrushProperty =
        DependencyProperty.Register(
            nameof(UnfilledBrush), typeof(Brush), typeof(GaugeBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Rating
    {
        get => (int)GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    public Brush? FilledBrush
    {
        get => (Brush?)GetValue(FilledBrushProperty);
        set => SetValue(FilledBrushProperty, value);
    }

    public string? FilledBrushKey
    {
        get => (string?)GetValue(FilledBrushKeyProperty);
        set => SetValue(FilledBrushKeyProperty, value);
    }

    private static void OnFilledBrushKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (GaugeBar)d;
        if (e.NewValue is string key && key.Length > 0)
        {
            // DynamicResource-style reference: re-resolves on theme switch and
            // raises a FilledBrush change, which repaints (AffectsRender).
            gauge.SetResourceReference(FilledBrushProperty, key);
        }
        else
        {
            gauge.ClearValue(FilledBrushProperty);
        }
    }

    public Brush? UnfilledBrush
    {
        get => (Brush?)GetValue(UnfilledBrushProperty);
        set => SetValue(UnfilledBrushProperty, value);
    }

    private static double GaugeWidth => SegmentCount * SegmentWidth + (SegmentCount - 1) * SegmentGap;

    protected override Size MeasureOverride(Size availableSize) => new(GaugeWidth, SegmentHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var filled = FilledBrush ?? SystemColors.HighlightBrush;
        var unfilled = UnfilledBrush ?? SystemColors.ControlBrush;
        var rating = Rating;

        // Left-aligned and vertically centred, as the old horizontal StackPanel was.
        var top = Math.Max(0, (RenderSize.Height - SegmentHeight) / 2);
        for (var i = 0; i < SegmentCount; i++)
        {
            var rect = new Rect(i * (SegmentWidth + SegmentGap), top, SegmentWidth, SegmentHeight);
            drawingContext.DrawRoundedRectangle(i < rating ? filled : unfilled, null, rect, CornerRadius, CornerRadius);
        }
    }
}
