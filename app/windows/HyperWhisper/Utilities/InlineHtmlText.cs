// INLINE HTML TEXT (WPF)
//
// TextBlock.Inlines is not a dependency property, so release notes containing
// <b> could not be bound and were shown as literal markup. This attached
// property takes the raw fragment and rebuilds the TextBlock's inlines with
// real bold/italic runs.
//
// Usage in XAML:
//   <TextBlock utilities:InlineHtmlText.Source="{Binding}"/>
//
// Set Source instead of Text — the two fight over the same content.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace HyperWhisper.Utilities;

public static class InlineHtmlText
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(string),
            typeof(InlineHtmlText),
            new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject element, string? value)
        => element.SetValue(SourceProperty, value);

    public static string? GetSource(DependencyObject element)
        => (string?)element.GetValue(SourceProperty);

    private static void OnSourceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock textBlock) return;

        textBlock.Inlines.Clear();
        Apply(textBlock, e.NewValue as string);
    }

    /// <summary>Replace a TextBlock's inlines with the parsed fragment.</summary>
    public static void Apply(TextBlock textBlock, string? html)
    {
        foreach (var run in InlineHtml.Parse(html))
        {
            var inline = new Run(run.Text);

            // Only override what the fragment asks for, so an unemphasised run
            // still inherits the TextBlock's own weight and style.
            if (run.Bold) inline.FontWeight = FontWeights.Bold;
            if (run.Italic) inline.FontStyle = FontStyles.Italic;

            textBlock.Inlines.Add(inline);
        }
    }
}
