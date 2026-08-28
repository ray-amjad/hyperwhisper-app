using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Events;
using NetSparkleUpdater.Interfaces;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Utilities;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// UPDATE AVAILABLE WINDOW
///
/// Themed dialog showing update information and release notes.
/// Implements IUpdateAvailable for NetSparkle integration.
///
/// Layout: App name, version comparison, scrollable release notes card, action buttons.
/// </summary>
public partial class UpdateAvailableWindow : Window, IUpdateAvailable
{
    // =========================================================================
    // STATE
    // =========================================================================

    private readonly List<AppCastItem> _updates;

    public UpdateAvailableResult Result { get; private set; } = UpdateAvailableResult.None;
    public AppCastItem CurrentItem => _updates.FirstOrDefault()!;

    public event UserRespondedToUpdate? UserResponded;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public UpdateAvailableWindow(List<AppCastItem> updates, string currentVersion, bool isUpdateAlreadyDownloaded)
    {
        InitializeComponent();

        _updates = updates;

        var latestItem = updates.FirstOrDefault();
        if (latestItem != null)
        {
            // Show just version number inside the card
            NewVersionText.Text = latestItem.Version ?? "";
            CurrentVersionText.Text = Loc.S("update.available.currentVersion", currentVersion);

            // Parse and display release notes
            if (!string.IsNullOrWhiteSpace(latestItem.Description))
            {
                ParseHtmlToTextBlocks(latestItem.Description, ReleaseNotesPanel);
            }
        }

        // Change install button text if already downloaded
        if (isUpdateAlreadyDownloaded)
        {
            InstallButton.Content = Loc.S("update.available.installReady");
        }

        LoggingService.Info($"UpdateAvailableWindow: Showing update v{latestItem?.Version} (current: {currentVersion})");
    }

    // =========================================================================
    // IUPDATEAVAILABLE INTERFACE
    // =========================================================================

    public void BringToFront()
    {
        Dispatcher.Invoke(() =>
        {
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    void IUpdateAvailable.HideReleaseNotes()
    {
        ReleaseNotesHeader.Visibility = Visibility.Collapsed;
        ReleaseNotesCard.Visibility = Visibility.Collapsed;
    }

    void IUpdateAvailable.HideSkipButton()
    {
        SkipButton.Visibility = Visibility.Collapsed;
    }

    void IUpdateAvailable.HideRemindMeLaterButton()
    {
        RemindLaterButton.Visibility = Visibility.Collapsed;
    }

    // =========================================================================
    // BUTTON HANDLERS
    // =========================================================================

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Info("UpdateAvailableWindow: User chose Install");
        Result = UpdateAvailableResult.InstallUpdate;
        UserResponded?.Invoke(this, new UpdateResponseEventArgs(Result, CurrentItem));
        Close();
    }

    private void RemindLaterButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Info("UpdateAvailableWindow: User chose Remind Later");
        Result = UpdateAvailableResult.RemindMeLater;
        UserResponded?.Invoke(this, new UpdateResponseEventArgs(Result, CurrentItem));
        Close();
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Info("UpdateAvailableWindow: User chose Skip");
        Result = UpdateAvailableResult.SkipUpdate;
        UserResponded?.Invoke(this, new UpdateResponseEventArgs(Result, CurrentItem));
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // If user closed via X button without clicking a button
        if (Result == UpdateAvailableResult.None)
        {
            Result = UpdateAvailableResult.RemindMeLater;
            UserResponded?.Invoke(this, new UpdateResponseEventArgs(Result, CurrentItem));
        }
        base.OnClosed(e);
    }

    // =========================================================================
    // RELEASE NOTES PARSING
    // =========================================================================

    /// <summary>
    /// Turns the feed's release-notes HTML (h2/h3, ul/li, p) into themed WPF
    /// cards, without needing a WebBrowser. Inline emphasis (&lt;b&gt;,
    /// &lt;i&gt;) and links are carried through to the TextBlock rather than
    /// stripped, so a bold lead-in in the feed still reads as bold.
    /// </summary>
    /// <remarks>
    /// The block split lives in the shared Rust core now (#284). This was the
    /// third copy of one &lt;li&gt; extractor — a
    /// <c>&lt;(h[23]|li|p)[^&gt;]*&gt;(.*?)&lt;/\1&gt;</c> walker with its own
    /// &lt;br&gt;-split fallback — and the three had drifted: that
    /// backreference needed the exact characters "&lt;/li&gt;", so a feed
    /// writing "&lt;/li &gt;" lost the bullet here while macOS kept it, and
    /// <c>[^&gt;]*</c> ended an open tag at a "&gt;" inside a quoted attribute.
    /// <c>InlineHtml.SplitBlocks</c> keeps the more forgiving reading of both.
    ///
    /// The fallback's hard-won guard survives intact, in Rust: a note with no
    /// block markup is still one card per line, and each line still keeps its
    /// own markup and is parsed EXACTLY ONCE. Flattening the note here and
    /// parsing the result again in the card dropped every &lt;a href&gt; before
    /// it could render, and turned markup a feed had escaped so it would *show*
    /// — "&amp;lt;a href=…&amp;gt;" — into a live link, because the first pass
    /// decoded the entities and the second read the result as a tag. The core
    /// pins that with <c>escaped_markup_in_the_fallback_is_parsed_exactly_once</c>,
    /// and nothing below re-parses a block's text.
    /// </remarks>
    private static void ParseHtmlToTextBlocks(string html, StackPanel container)
    {
        foreach (var block in InlineHtml.SplitBlocks(html))
        {
            container.Children.Add(CreateReleaseNoteCard(block));
        }
    }

    private static Border CreateReleaseNoteCard(HtmlBlock block)
    {
        bool isHeader = block.Kind == HtmlBlockKind.Heading;
        bool isBullet = block.Kind == HtmlBlockKind.Bullet;

        // Glyph selection reads the wording, so it needs the text without
        // markup — which is the block's run texts joined, since the core
        // already dropped the tags and decoded the entities. Identical to the
        // PlainText(html) call this replaces, without a second parse.
        var text = string.Concat(block.Runs.Select(run => run.Text));

        var badge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        badge.SetResourceReference(BackgroundProperty, isHeader ? "AccentBrush" : "HoverBackgroundBrush");

        var badgeText = new TextBlock
        {
            Text = ChangeGlyphFor(text, isHeader, isBullet),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        badgeText.SetResourceReference(ForegroundProperty, isHeader ? "TextOnAccentBrush" : "AccentBrush");
        badge.Child = badgeText;

        var textBlock = new TextBlock
        {
            FontSize = isHeader ? 13 : 12,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        textBlock.SetResourceReference(ForegroundProperty, isHeader ? "TextPrimaryBrush" : "TextSecondaryBrush");
        InlineHtmlText.Apply(textBlock, block.Runs);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(badge);
        Grid.SetColumn(textBlock, 1);
        grid.Children.Add(textBlock);

        var card = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(9),
            Child = grid
        };
        card.SetResourceReference(BackgroundProperty, "CardBackgroundBrush");
        card.SetResourceReference(BorderBrushProperty, "BorderBrush");
        card.BorderThickness = new Thickness(1);
        return card;
    }

    private static string ChangeGlyphFor(string text, bool isHeader, bool isBullet)
    {
        if (text.StartsWith("Remove", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Removed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Deleted", StringComparison.OrdinalIgnoreCase))
        {
            return "-";
        }

        if (text.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Updated", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Fix", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Improve", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Improved", StringComparison.OrdinalIgnoreCase))
        {
            return "~";
        }

        return isHeader || isBullet ? "+" : "~";
    }
}
