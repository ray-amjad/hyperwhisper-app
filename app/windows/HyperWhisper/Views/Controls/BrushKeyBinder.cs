using System.Windows;
using System.Windows.Controls;

namespace HyperWhisper.Views.Controls;

/// <summary>
/// Attached property that lets XAML bind a <see cref="TextBlock"/>'s
/// Foreground to a theme resource KEY (e.g. "WarningBrush"/"AccentBrush")
/// instead of a resolved brush, so the color tracks live light/dark theme
/// switches like a literal <c>DynamicResource</c> would.
///
/// Binding markup cannot nest inside <c>DynamicResource</c>
/// (<c>{DynamicResource {Binding Key}}</c> isn't valid XAML), so this
/// mirrors the same technique <see cref="GaugeBar"/> already uses for its
/// binding-driven <c>FilledBrushKey</c> (SetResourceReference in code,
/// re-applied on change), generalized as an attached property so a plain
/// TextBlock can use it without a dedicated custom control.
/// </summary>
public static class BrushKeyBinder
{
    public static readonly DependencyProperty ForegroundKeyProperty =
        DependencyProperty.RegisterAttached(
            "ForegroundKey", typeof(string), typeof(BrushKeyBinder),
            new PropertyMetadata(null, OnForegroundKeyChanged));

    public static string? GetForegroundKey(DependencyObject obj) => (string?)obj.GetValue(ForegroundKeyProperty);
    public static void SetForegroundKey(DependencyObject obj, string? value) => obj.SetValue(ForegroundKeyProperty, value);

    private static void OnForegroundKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        if (e.NewValue is string key && key.Length > 0)
        {
            // DynamicResource-style reference: re-resolves on theme switch.
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, key);
        }
        else
        {
            textBlock.ClearValue(TextBlock.ForegroundProperty);
        }
    }
}
