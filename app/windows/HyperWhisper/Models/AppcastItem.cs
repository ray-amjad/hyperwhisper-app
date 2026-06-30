using System.Text.RegularExpressions;

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

    public List<string> BulletPoints
    {
        get
        {
            var matches = Regex.Matches(ReleaseNotes, @"<li>(.*?)</li>", RegexOptions.Singleline);
            return matches.Select(m => m.Groups[1].Value.Trim()).ToList();
        }
    }

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
}
