using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace HyperWhisper.Views.Controls.Onboarding;

/// <summary>
/// Renders a "Ctrl+Shift+Space" style display string as a row of keycaps. See the
/// XAML header.
/// </summary>
public partial class OnboardingKeycaps : WpfUserControl
{
    public OnboardingKeycaps()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The already-formatted shortcut, i.e. the seam's ShortcutDisplay. Never
    /// parsed into key codes here: this control does presentation only.
    /// </summary>
    public static readonly DependencyProperty ShortcutProperty =
        DependencyProperty.Register(
            nameof(Shortcut),
            typeof(string),
            typeof(OnboardingKeycaps),
            new PropertyMetadata(string.Empty, OnShortcutChanged));

    public string Shortcut
    {
        get => (string)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    private static void OnShortcutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((OnboardingKeycaps)d).Rebuild();

    private void Rebuild()
    {
        Caps.Children.Clear();
        AutomationProperties.SetName(this, Shortcut ?? string.Empty);

        if (string.IsNullOrWhiteSpace(Shortcut))
            return;

        var parts = Shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var label = new TextBlock
            {
                Text = part,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var cap = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = label
            };
            cap.SetResourceReference(Border.BackgroundProperty, "InputBackgroundBrush");
            cap.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            Caps.Children.Add(cap);
        }
    }
}
