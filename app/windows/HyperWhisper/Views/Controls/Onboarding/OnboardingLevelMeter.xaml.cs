using System.Windows;
using System.Windows.Controls;

namespace HyperWhisper.Views.Controls.Onboarding;

/// <summary>
/// The scrolling input level meter. See the XAML header.
/// </summary>
public partial class OnboardingLevelMeter : WpfUserControl
{
    private const int BarCount = 33;
    private const double MinBarHeight = 4;
    private const double MaxBarGrowth = 38;

    private readonly Border[] _bars = new Border[BarCount];
    private readonly double[] _history = new double[BarCount];

    public OnboardingLevelMeter()
    {
        InitializeComponent();
        BuildBars();
        ApplyActive();
    }

    /// <summary>The most recent sample, 0 to 1.</summary>
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(float),
            typeof(OnboardingLevelMeter),
            new PropertyMetadata(0f, OnLevelChanged));

    public float Level
    {
        get => (float)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>
    /// Whether a preview is genuinely running. False is a real, drawn state, not
    /// simply a level of zero.
    /// </summary>
    public static readonly DependencyProperty ActiveProperty =
        DependencyProperty.Register(
            nameof(Active),
            typeof(bool),
            typeof(OnboardingLevelMeter),
            new PropertyMetadata(false, OnActiveChanged));

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var meter = (OnboardingLevelMeter)d;
        if (meter.Active)
            meter.Push((float)e.NewValue);
    }

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((OnboardingLevelMeter)d).ApplyActive();

    private void BuildBars()
    {
        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Border
            {
                Width = 6,
                Height = MinBarHeight,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, i < BarCount - 1 ? 4 : 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            // A resource reference, not a resolved brush: the meter has to follow a
            // runtime theme swap like everything else in the flow.
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

            _bars[i] = bar;
            Bars.Children.Add(bar);
        }
    }

    private void Push(float value)
    {
        var clamped = Math.Clamp(value, 0f, 1f);

        // Shift left by one and append, so the row reads as a waveform travelling
        // rightwards rather than every bar pumping together.
        Array.Copy(_history, 1, _history, 0, BarCount - 1);
        _history[BarCount - 1] = clamped;

        for (var i = 0; i < BarCount; i++)
            _bars[i].Height = MinBarHeight + (_history[i] * MaxBarGrowth);
    }

    private void ApplyActive()
    {
        Bars.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
        InactiveLabel.Visibility = Active ? Visibility.Collapsed : Visibility.Visible;

        if (Active)
            return;

        Array.Clear(_history);
        foreach (var bar in _bars)
            bar.Height = MinBarHeight;
    }
}
