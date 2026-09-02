using System.Windows;
using System.Windows.Controls;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Views.Controls.Onboarding;

/// <summary>
/// The eight segment progress hairline. See the XAML header.
/// </summary>
public partial class OnboardingStepProgress : WpfUserControl
{
    private readonly Border[] _segments = new Border[OnboardingSteps.Count];

    public OnboardingStepProgress()
    {
        InitializeComponent();
        BuildSegments();
        Apply();
    }

    /// <summary>The zero-based index of the step currently on screen.</summary>
    public static readonly DependencyProperty CurrentProperty =
        DependencyProperty.Register(
            nameof(Current),
            typeof(int),
            typeof(OnboardingStepProgress),
            new PropertyMetadata(0, OnCurrentChanged));

    public int Current
    {
        get => (int)GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    private static void OnCurrentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((OnboardingStepProgress)d).Apply();

    private void BuildSegments()
    {
        Segments.Columns = OnboardingSteps.Count;

        for (var i = 0; i < OnboardingSteps.Count; i++)
        {
            // The 2 gap between segments is a margin rather than a spacer column,
            // so the UniformGrid still divides the width into exactly Count parts.
            var segment = new Border
            {
                Margin = new Thickness(0, 0, i < OnboardingSteps.Count - 1 ? 2 : 0, 0)
            };

            _segments[i] = segment;
            Segments.Children.Add(segment);
        }
    }

    private void Apply()
    {
        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];

            // SetResourceReference, not FindResource: ThemeService swaps the colour
            // dictionary at runtime, and a resolved brush would freeze the hairline
            // in whichever theme the window opened in.
            if (i == Current)
            {
                segment.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                segment.Opacity = 1;
            }
            else if (i < Current)
            {
                segment.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                segment.Opacity = 0.45;
            }
            else
            {
                segment.SetResourceReference(Border.BackgroundProperty, "ProgressBackgroundBrush");
                segment.Opacity = 1;
            }
        }
    }
}
