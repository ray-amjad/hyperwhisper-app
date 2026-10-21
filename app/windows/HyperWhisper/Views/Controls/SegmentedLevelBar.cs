using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HyperWhisper.Views.Controls;

/// <summary>
/// A horizontal segmented input-level bar for Settings -> Sound -> Microphone.
/// <see cref="Level"/> (0 to 1) lights that fraction of the segments in the
/// accent colour; the rest, and every segment while <see cref="Active"/> is
/// false, are grey. Idle the bar is empty and grey, by design: visiting the
/// page opens no microphone.
///
/// The onboarding meter (OnboardingLevelMeter) scrolls a waveform and swaps in
/// a line of copy when inactive; the Settings mockup is a static segmented bar,
/// so this is a separate control fed by the same level maths (AudioLevelMath).
/// </summary>
public sealed class SegmentedLevelBar : WpfUserControl
{
    public const int SegmentCount = 24;

    private readonly Border[] _segments = new Border[SegmentCount];
    private int _lit = -1;

    public SegmentedLevelBar()
    {
        Height = 10;
        var grid = new UniformGrid { Rows = 1, Columns = SegmentCount };
        for (var i = 0; i < SegmentCount; i++)
        {
            var segment = new Border
            {
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(i == 0 ? 0 : 1.5, 0, i == SegmentCount - 1 ? 0 : 1.5, 0),
            };
            _segments[i] = segment;
            grid.Children.Add(segment);
        }

        Content = grid;
        Apply();
    }

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(float),
            typeof(SegmentedLevelBar),
            new PropertyMetadata(0f, (d, _) => ((SegmentedLevelBar)d).Apply()));

    /// <summary>The current level, 0 to 1.</summary>
    public float Level
    {
        get => (float)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public static readonly DependencyProperty ActiveProperty =
        DependencyProperty.Register(
            nameof(Active),
            typeof(bool),
            typeof(SegmentedLevelBar),
            new PropertyMetadata(false, (d, _) => ((SegmentedLevelBar)d).Apply()));

    /// <summary>False: every segment grey, whatever the level.</summary>
    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    /// <summary>How many of <paramref name="segments"/> a level lights.</summary>
    public static int LitSegments(float level, int segments, bool active)
    {
        if (!active || float.IsNaN(level)) return 0;
        return (int)Math.Round(Math.Clamp(level, 0f, 1f) * segments, MidpointRounding.AwayFromZero);
    }

    private void Apply()
    {
        var lit = LitSegments(Level, SegmentCount, Active);
        if (lit == _lit) return;
        _lit = lit;

        for (var i = 0; i < SegmentCount; i++)
        {
            // Resource references, not resolved brushes, so a runtime theme swap
            // repaints the bar like everything else.
            _segments[i].SetResourceReference(
                Border.BackgroundProperty,
                i < lit ? "AccentBrush" : "ProgressBackgroundBrush");
        }
    }
}
