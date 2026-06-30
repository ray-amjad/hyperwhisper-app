using System.Windows;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using SystemColors = System.Windows.SystemColors;
using UserControl = System.Windows.Controls.UserControl;

namespace HyperWhisper.Views.Controls;

/// <summary>
/// A five-segment rating gauge that mirrors the macOS ModelRow speed/accuracy
/// bars: the first <see cref="Rating"/> segments use <see cref="FilledBrush"/>
/// and the rest use <see cref="UnfilledBrush"/>.
/// </summary>
public partial class GaugeBar : UserControl
{
    public GaugeBar()
    {
        InitializeComponent();
        if (UnfilledBrush is null) SetResourceReference(UnfilledBrushProperty, "BorderBrush");
        Loaded += (_, _) => Refresh();
    }

    public static readonly DependencyProperty RatingProperty =
        DependencyProperty.Register(
            nameof(Rating), typeof(int), typeof(GaugeBar),
            new PropertyMetadata(0, OnVisualChanged));

    public static readonly DependencyProperty FilledBrushProperty =
        DependencyProperty.Register(
            nameof(FilledBrush), typeof(Brush), typeof(GaugeBar),
            new PropertyMetadata(null, OnVisualChanged));

    // Lets callers pass a theme resource KEY (e.g. "AccentBrush") instead of a
    // resolved brush, so the fill tracks light/dark switches like a DynamicResource.
    public static readonly DependencyProperty FilledBrushKeyProperty =
        DependencyProperty.Register(
            nameof(FilledBrushKey), typeof(string), typeof(GaugeBar),
            new PropertyMetadata(null, OnFilledBrushKeyChanged));

    public static readonly DependencyProperty UnfilledBrushProperty =
        DependencyProperty.Register(
            nameof(UnfilledBrush), typeof(Brush), typeof(GaugeBar),
            new PropertyMetadata(null, OnVisualChanged));

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
            // raises a FilledBrush change, which repaints via OnVisualChanged.
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

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GaugeBar)d).Refresh();

    private void Refresh()
    {
        var filled = FilledBrush ?? SystemColors.HighlightBrush;
        var unfilled = UnfilledBrush ?? SystemColors.ControlBrush;
        var rating = Rating;

        for (var i = 0; i < Segments.Children.Count; i++)
        {
            if (Segments.Children[i] is Border segment)
            {
                segment.Background = i < rating ? filled : unfilled;
            }
        }
    }
}
