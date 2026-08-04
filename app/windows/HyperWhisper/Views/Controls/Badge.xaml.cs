using System.Windows;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace HyperWhisper.Views.Controls;

/// <summary>
/// Small rounded pill badge (macOS ModelRow tag/badge parity). Reused for both
/// the Model Library's plain Tag pill and the local-LLM CPU/GPU runtime badge,
/// which previously copy-pasted the same Border/TextBlock markup twice.
///
/// <see cref="ForegroundKey"/> lets callers pass a theme resource KEY (e.g.
/// "WarningBrush"/"AccentBrush") instead of a resolved brush, using the same
/// SetResourceReference technique <see cref="GaugeBar"/> already uses for its
/// binding-driven <c>FilledBrushKey</c>, so the color tracks live light/dark
/// theme switches like a literal <c>DynamicResource</c> would.
/// </summary>
public partial class Badge : UserControl
{
    public Badge()
    {
        InitializeComponent();
        ApplyForegroundKey(ForegroundKey);
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(Badge),
            new PropertyMetadata(string.Empty, OnTextChanged));

    // Defaults to the plain Tag pill's original static foreground so existing
    // callers that don't care about theming (e.g. "EN", "Verified") need not
    // set anything.
    public static readonly DependencyProperty ForegroundKeyProperty =
        DependencyProperty.Register(
            nameof(ForegroundKey), typeof(string), typeof(Badge),
            new PropertyMetadata("TextSecondaryBrush", OnForegroundKeyChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? ForegroundKey
    {
        get => (string?)GetValue(ForegroundKeyProperty);
        set => SetValue(ForegroundKeyProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Badge)d).BadgeText.Text = e.NewValue as string ?? string.Empty;

    private static void OnForegroundKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Badge)d).ApplyForegroundKey(e.NewValue as string);

    private void ApplyForegroundKey(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            // DynamicResource-style reference: re-resolves on theme switch.
            BadgeText.SetResourceReference(TextBlock.ForegroundProperty, key);
        }
        else
        {
            BadgeText.ClearValue(TextBlock.ForegroundProperty);
        }
    }
}
