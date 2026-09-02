using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using HyperWhisper.Localization;

namespace HyperWhisper.Views.Controls.Onboarding;

/// <summary>
/// The model download bar. See the XAML header.
/// </summary>
public partial class OnboardingProgressBar : WpfUserControl
{
    public OnboardingProgressBar()
    {
        InitializeComponent();
    }

    /// <summary>Download fraction, 0 to 1. Anything outside that range is clamped.</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(OnboardingProgressBar),
            new PropertyMetadata(0d, OnValueChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((OnboardingProgressBar)d).ApplyFill();

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => ApplyFill();

    private void ApplyFill()
    {
        var fraction = Math.Clamp(Value, 0d, 1d);
        Fill.Width = Track.ActualWidth * fraction;

        AutomationProperties.SetHelpText(
            this,
            Loc.S("onboarding.a11y.percent", (int)Math.Round(fraction * 100)));
    }
}
