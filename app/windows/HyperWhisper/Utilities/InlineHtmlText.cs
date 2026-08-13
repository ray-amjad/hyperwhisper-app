// INLINE HTML TEXT (WPF)
//
// TextBlock.Inlines is not a dependency property, so release notes containing
// <b> could not be bound and were shown as literal markup. This attached
// property takes the raw fragment and rebuilds the TextBlock's inlines with
// real bold/italic runs and clickable links.
//
// Usage in XAML:
//   <TextBlock utilities:InlineHtmlText.Source="{Binding}"/>
//
// Set Source instead of Text — the two fight over the same content.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using HyperWhisper.Services;

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
            var text = new Run(run.Text);

            // Only override what the fragment asks for, so an unemphasised run
            // still inherits the TextBlock's own weight and style.
            if (run.Bold) text.FontWeight = FontWeights.Bold;
            if (run.Italic) text.FontStyle = FontStyles.Italic;

            textBlock.Inlines.Add(run.Link is { } link ? BuildHyperlink(text, link) : text);
        }
    }

    /// <summary>
    /// Wrap a run in a link. Accent colour on top of the Hyperlink's own
    /// underline and hand cursor, so it reads as clickable in either theme —
    /// the stock blue is close to invisible on the dark one.
    /// </summary>
    private static Inline BuildHyperlink(Run text, string link)
    {
        // InlineHtml already vetted the scheme; anything it lets through
        // parses, so a failure here means it changed under us.
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return text;

        var hyperlink = new Hyperlink(text)
        {
            NavigateUri = uri,
            ToolTip = link
        };

        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        hyperlink.RequestNavigate += OnRequestNavigate;

        return hyperlink;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"InlineHtmlText: failed to open link '{e.Uri}': {ex.Message}");
        }
    }
}
