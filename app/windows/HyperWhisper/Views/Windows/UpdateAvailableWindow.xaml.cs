using System.Text.RegularExpressions;
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
    /// Parses simple HTML (h2, ul/li, p) into themed WPF TextBlocks.
    /// Handles the typical appcast release notes format without needing a WebBrowser.
    /// Inline emphasis (&lt;b&gt;, &lt;i&gt;) is carried through to the TextBlock
    /// rather than stripped, so a bold lead-in in the feed still reads as bold.
    /// </summary>
    private static void ParseHtmlToTextBlocks(string html, StackPanel container)
    {
        // Strip outer tags like <body>, <html>
        html = html.Trim();

        // Process <h2> headers
        // Process <li> items
        // Process <p> paragraphs
        // Fall back to plain text lines

        // Each entry keeps its inline markup; only block tags are consumed here.
        var lines = new List<(string html, bool isHeader, bool isBullet)>();

        // Simple sequential parse: walk through the HTML and extract elements in order
        var elementRegex = new Regex(
            @"<(h[23]|li|p)[^>]*>(.*?)</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var matches = elementRegex.Matches(html);

        if (matches.Count > 0)
        {
            foreach (Match match in matches)
            {
                var tag = match.Groups[1].Value.ToLowerInvariant();
                var content = match.Groups[2].Value;

                if (InlineHtml.PlainText(content).Length == 0) continue;

                bool isHeader = tag.StartsWith("h");
                bool isBullet = tag == "li";

                lines.Add((content, isHeader, isBullet));
            }
        }
        else
        {
            // Fallback: no block tags, so the note is a run of lines. Each line
            // keeps its own markup and is parsed exactly once — by the card.
            // Flattening the note here and parsing the result again there
            // dropped every <a href> before it could be rendered, and turned
            // markup a feed had escaped so it would *show* — "&lt;a href=…&gt;"
            // — into a live link. A <br> ends a line here, as it did when the
            // flattening pass turned it into one.
            foreach (var line in Regex.Split(html, @"<br\s*/?>|\n", RegexOptions.IgnoreCase))
            {
                var trimmed = line.Trim();
                if (InlineHtml.PlainText(trimmed).Trim().Length == 0) continue;

                bool isBullet = trimmed.StartsWith("-") || trimmed.StartsWith("*");
                if (isBullet) trimmed = trimmed.TrimStart('-', '*', ' ');
                lines.Add((trimmed, false, isBullet));
            }
        }

        // Build WPF elements with improved spacing
        foreach (var (text, isHeader, isBullet) in lines)
        {
            container.Children.Add(CreateReleaseNoteCard(text, isHeader, isBullet));
        }
    }

    private static Border CreateReleaseNoteCard(string html, bool isHeader, bool isBullet)
    {
        // Glyph selection reads the wording, so it needs the text without markup.
        var text = InlineHtml.PlainText(html);

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
        InlineHtmlText.Apply(textBlock, html);

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
