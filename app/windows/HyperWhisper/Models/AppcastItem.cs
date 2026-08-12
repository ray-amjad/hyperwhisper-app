using System.Text.RegularExpressions;
using HyperWhisper.Utilities;

namespace HyperWhisper.Models;

public class AppcastItem
{
    public string Version { get; init; } = "";
    public DateTime PubDate { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public bool IsLatest { get; set; }

    public string FormattedDate => PubDate.ToString("MMM d, yyyy");

    public string ReleaseTitle
    {
        get
        {
            var match = Regex.Match(ReleaseNotes, @"<h2>(.*?)</h2>", RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }
    }

    /// <summary>
    /// Inner HTML of every &lt;li&gt;, in document order. Inline emphasis is
    /// kept here and turned into real bold/italic runs by InlineHtmlText when
    /// the bullet is rendered.
    /// </summary>
    public List<string> BulletPoints
    {
        get
        {
            var matches = Regex.Matches(ReleaseNotes, @"<li[^>]*>(.*?)</li>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return matches
                .Select(m => m.Groups[1].Value)
                .Where(content => InlineHtml.PlainText(content).Length > 0)
                .ToList();
        }
    }

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
}
