using System.Globalization;
using System.Text;

namespace HyperWhisper.Linux.Platform.Desktop;

/// <summary>One mode as the application hands it to the tray: its id and its free-text name.</summary>
public readonly record struct TrayModeEntry(Guid Id, string? Name);

/// <summary>One row of the tray's Select Mode submenu: the id stays in this process, only the label is sent.</summary>
public sealed record TrayModeRow(Guid Id, string Label);

/// <summary>
/// The tray's Select Mode submenu, built the way Windows builds it
/// (MainWindow.xaml.cs RefreshModeMenu / TrayModeMenuModes): one row per mode in list
/// order, a check on the active one, at most <see cref="MaximumRows"/> rows plus the
/// active mode when it sits past them, then "More..." (#1593, #1870). Labels are
/// bounded like MenuItemText.Bound: one line, whitespace collapsed, at most
/// <see cref="MaximumLabelLength"/> characters before an ellipsis. A blank name is
/// sent as "" and the helper shows its localized "Unnamed".
/// </summary>
public sealed class TrayModeMenu
{
    /// <summary>Windows' TrayModeMenuLimit.</summary>
    public const int MaximumRows = 50;

    /// <summary>Windows' MenuItemText.MaxLength.</summary>
    public const int MaximumLabelLength = 60;

    public const string Ellipsis = "\u2026";

    private TrayModeMenu(IReadOnlyList<TrayModeRow> rows, int activeIndex, bool enabled, bool hasMore)
    {
        Rows = rows;
        ActiveIndex = activeIndex;
        Enabled = enabled;
        HasMore = hasMore;
    }

    public IReadOnlyList<TrayModeRow> Rows { get; }

    /// <summary>Index into <see cref="Rows"/> of the active mode, or -1.</summary>
    public int ActiveIndex { get; }

    /// <summary>False while onboarding is open: the flow owns the active mode then.</summary>
    public bool Enabled { get; }

    /// <summary>True when the list was cut at <see cref="MaximumRows"/>.</summary>
    public bool HasMore { get; }

    public static TrayModeMenu Create(IEnumerable<TrayModeEntry> modes, Guid? activeId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var all = modes.ToList();
        var rows = new List<TrayModeRow>(Math.Min(all.Count, MaximumRows + 1));
        var activeIndex = -1;
        for (var index = 0; index < all.Count && index < MaximumRows; index++)
        {
            if (activeId is { } id && all[index].Id == id && activeIndex < 0) activeIndex = rows.Count;
            rows.Add(new TrayModeRow(all[index].Id, BoundLabel(all[index].Name)));
        }
        // The active mode past the cap still shows, as one extra row before "More..." (#1870).
        if (activeIndex < 0 && activeId is { } active)
        {
            var past = all.Skip(MaximumRows).Where(entry => entry.Id == active).Take(1).ToList();
            if (past.Count == 1)
            {
                activeIndex = rows.Count;
                rows.Add(new TrayModeRow(active, BoundLabel(past[0].Name)));
            }
        }
        return new TrayModeMenu(rows, activeIndex, enabled, all.Count > MaximumRows);
    }

    /// <summary>
    /// A single-line label of at most <see cref="MaximumLabelLength"/> characters plus an
    /// ellipsis. Whitespace runs (newlines, tabs, line and paragraph separators) collapse
    /// to one space; other control and format characters are dropped, except the zero-width
    /// joiner and non-joiner that real words and emoji need. A surrogate pair is never split.
    /// </summary>
    public static string BoundLabel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var builder = new StringBuilder(Math.Min(name.Length, MaximumLabelLength + 2));
        var pendingSpace = false;
        var length = 0;
        var truncated = false;
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            var category = char.GetUnicodeCategory(character);
            if (char.IsWhiteSpace(character) || category is UnicodeCategory.Control
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            var pair = char.IsHighSurrogate(character) && index + 1 < name.Length && char.IsLowSurrogate(name[index + 1]);
            if (!pair && (char.IsSurrogate(character) || category is UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                || category == UnicodeCategory.Format && character is not ('\u200C' or '\u200D')))
                continue;
            var width = (pendingSpace ? 1 : 0) + (pair ? 2 : 1);
            if (length + width > MaximumLabelLength)
            {
                truncated = true;
                break;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(character);
            if (pair) builder.Append(name[++index]);
            length += width;
        }
        return truncated ? builder.ToString().TrimEnd() + Ellipsis : builder.ToString();
    }
}
