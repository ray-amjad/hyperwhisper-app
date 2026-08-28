// INLINE HTML TEXT (WPF)
//
// TextBlock.Inlines is not a dependency property, so release notes containing
// <b> could not be bound and were shown as literal markup. This attached
// property takes the raw fragment and rebuilds the TextBlock's inlines with
// real bold/italic runs and clickable links.
//
// Usage in XAML:
//   <TextBlock utilities:InlineHtmlText.Source="{Binding}"/>   <- raw HTML
//   <TextBlock utilities:InlineHtmlText.Runs="{Binding}"/>     <- already parsed
//
// Set one of them instead of Text — they all fight over the same content.
//
// Prefer Runs. Source parses the fragment every time the binding produces a
// value, which for an ItemsControl is every layout pass of every item; Runs
// takes the styled runs a model parsed once, when it was built. AppcastItem
// does exactly that (#284), so the Recent Updates list no longer re-parses the
// whole feed on every redraw.
//
// Source is kept as the entry point for a caller that genuinely holds nothing
// but a fragment. After #284 no XAML in this app binds it: both release-notes
// surfaces pre-parse, the Recent Updates cards through AppcastItem and the
// update dialog through InlineHtml.SplitBlocks. Apply(TextBlock, string) is
// still the shortest path from a bare fragment to rendered inlines, and the
// smoke suite pins it.

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

    // =========================================================================
    // RUNS — the same rendering from an already-parsed fragment
    // =========================================================================

    public static readonly DependencyProperty RunsProperty =
        DependencyProperty.RegisterAttached(
            "Runs",
            typeof(IReadOnlyList<HtmlRun>),
            typeof(InlineHtmlText),
            new PropertyMetadata(null, OnRunsChanged));

    public static void SetRuns(DependencyObject element, IReadOnlyList<HtmlRun>? value)
        => element.SetValue(RunsProperty, value);

    public static IReadOnlyList<HtmlRun>? GetRuns(DependencyObject element)
        => (IReadOnlyList<HtmlRun>?)element.GetValue(RunsProperty);

    private static void OnRunsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock textBlock) return;

        // Cleared the same way Source is: binding null empties the TextBlock
        // rather than leaving the previous item's runs behind when a container
        // is recycled.
        textBlock.Inlines.Clear();
        Apply(textBlock, e.NewValue as IReadOnlyList<HtmlRun>);
    }

    /// <summary>Replace a TextBlock's inlines with the parsed fragment.</summary>
    public static void Apply(TextBlock textBlock, string? html)
        => Apply(textBlock, InlineHtml.Parse(html));

    /// <summary>Replace a TextBlock's inlines with runs parsed earlier.</summary>
    public static void Apply(TextBlock textBlock, IReadOnlyList<HtmlRun>? runs)
    {
        if (runs is null) return;

        // The anchor open across the runs being added, so that "<a>see <b>this</b>
        // page</a>" — three runs, one destination — becomes one link region
        // rather than three siblings: one tab stop, one tooltip, one hyperlink
        // announced to a screen reader. macOS renders that anchor as one link too.
        Hyperlink? openLink = null;

        foreach (var run in runs)
        {
            var text = new Run(run.Text);

            // Only override what the fragment asks for, so an unemphasised run
            // still inherits the TextBlock's own weight and style.
            if (run.Bold) text.FontWeight = FontWeights.Bold;
            if (run.Italic) text.FontStyle = FontStyles.Italic;

            if (run.Link is not { } link)
            {
                openLink = null;
                textBlock.Inlines.Add(text);
                continue;
            }

            if (openLink is { NavigateUri: { } open } && open.AbsoluteUri == link.AbsoluteUri)
            {
                openLink.Inlines.Add(text);
                continue;
            }

            openLink = BuildHyperlink(text, link);
            textBlock.Inlines.Add(openLink);
        }
    }

    /// <summary>
    /// Wrap a run in a link. Accent colour on top of the Hyperlink's own
    /// underline and hand cursor, so it reads as clickable in either theme —
    /// the stock blue is close to invisible on the dark one.
    /// </summary>
    private static Hyperlink BuildHyperlink(Run text, Uri uri)
    {
        var hyperlink = new Hyperlink(text)
        {
            NavigateUri = uri,
            ToolTip = uri.AbsoluteUri
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
            // AbsoluteUri, not ToString(): ToString() decodes percent-escapes,
            // so ".../whats%20new" would reach the shell as ".../whats new".
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"InlineHtmlText: failed to open link '{e.Uri}': {ex.Message}");
        }
    }
}
