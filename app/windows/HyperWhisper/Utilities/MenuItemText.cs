namespace HyperWhisper.Utilities;

/// <summary>
/// BOUNDING FREE USER TEXT FOR A WinForms MENU ITEM
///
/// A mode name is free user text with no cap anywhere — not in the editor, not
/// in the entity, not in the column, not in the Local API. Issue #492 bounded
/// the WPF surfaces that render it with <c>TextTrimming</c> and a
/// <c>MaxWidth</c>: the display is capped, the underlying string is untouched,
/// and a tooltip carries the whole thing.
///
/// A <see cref="System.Windows.Forms.ToolStripMenuItem"/> has no equivalent.
/// A <c>ToolStripDropDownMenu</c> sizes itself to its widest item and never
/// ellipsises, so a 300-character mode name renders a submenu wider than the
/// monitor and puts every other entry out of reach. The only place to bound it
/// is the string handed to the item, so that is what this does — and, exactly
/// as on the WPF side, the caller keeps the full name for the tooltip and for
/// anything that is not display.
///
/// The cap is on characters rather than pixels because a menu item is laid out
/// long before there is a device context to measure against, and because a
/// character cap is deterministic enough to assert. It is not an exact pixel
/// bound — 60 wide CJK glyphs are roughly twice the width of 60 Latin ones —
/// but it bounds the item to a fraction of any real display either way, which
/// is the property the menu needs.
/// </summary>
public static class MenuItemText
{
    /// <summary>
    /// Characters of the original text kept before the ellipsis. At the menu's
    /// ~9pt Segoe UI this is roughly 420px of Latin text: comfortably readable,
    /// far longer than any real mode name, and a small fraction of the narrowest
    /// display the app supports.
    /// </summary>
    public const int MaxLength = 60;

    /// <summary>Appended in place of everything past <see cref="MaxLength"/>.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// The bounded, single-line form of <paramref name="text"/>, safe to hand to
    /// a <see cref="System.Windows.Forms.ToolStripMenuItem"/> label.
    /// </summary>
    /// <remarks>
    /// Two things are bounded, because a menu item grows in both directions:
    /// width, via the length cap; and height, because WinForms renders a literal
    /// newline as a line break and the Local API will accept a mode name with one
    /// in it. Whitespace runs — including newlines and tabs — collapse to a single
    /// space so the item stays exactly one row tall.
    /// </remarks>
    public static string Bound(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var collapsed = CollapseWhitespace(text);
        if (collapsed.Length <= MaxLength)
            return collapsed;

        // Never cut between the two halves of a surrogate pair: the lone
        // surrogate left behind renders as a replacement glyph.
        var cut = MaxLength;
        if (char.IsHighSurrogate(collapsed[cut - 1]))
            cut--;

        return collapsed[..cut].TrimEnd() + Ellipsis;
    }

    /// <summary>Characters per line in the tooltip built by <see cref="Tooltip"/>.</summary>
    public const int TooltipLineLength = 60;

    /// <summary>Lines <see cref="Tooltip"/> will show before it gives up and elides.</summary>
    public const int TooltipMaxLines = 10;

    /// <summary>
    /// True when <see cref="Bound"/> would drop or rewrite part of
    /// <paramref name="text"/>, i.e. when the item needs a tooltip carrying the
    /// whole name. False for the ordinary short name, which would only get a
    /// tooltip repeating the label back at the user.
    /// </summary>
    public static bool NeedsFullTextTooltip(string? text)
        => !string.IsNullOrEmpty(text) && Bound(text) != text;

    /// <summary>
    /// What to put in <c>ToolStripItem.ToolTipText</c> for
    /// <paramref name="text"/>, or <c>null</c> when the label already shows all
    /// of it and a tooltip would just repeat itself.
    /// </summary>
    /// <remarks>
    /// WRAPPED, and this is the whole point. A WinForms tooltip is a single line
    /// unless the string carries its own newlines: handing it a 300-character
    /// mode name renders a tooltip about 2100px wide, off both edges of the
    /// screen, which is the same defect as the menu it was meant to relieve —
    /// observed on a VM before this was written. So the tooltip is bounded in
    /// both directions: <see cref="TooltipLineLength"/> characters per line and
    /// at most <see cref="TooltipMaxLines"/> lines, then an ellipsis.
    ///
    /// This is the Win32 counterpart of the WPF surfaces' explicit
    /// <c>&lt;ToolTip&gt;&lt;TextBlock TextWrapping="Wrap" MaxWidth="360"/&gt;</c>,
    /// which exists for exactly the same reason (#492).
    /// </remarks>
    public static string? Tooltip(string? text)
    {
        if (!NeedsFullTextTooltip(text))
            return null;

        var collapsed = CollapseWhitespace(text!);
        var lines = new List<string>();
        var index = 0;

        while (index < collapsed.Length && lines.Count < TooltipMaxLines)
        {
            if (collapsed.Length - index <= TooltipLineLength)
            {
                lines.Add(collapsed[index..]);
                index = collapsed.Length;
                break;
            }

            // Break on the last space that fits, so a name made of words stays
            // readable; fall back to a hard break for one long run of characters.
            var window = collapsed.Substring(index, TooltipLineLength + 1);
            var space = window.LastIndexOf(' ');

            var take = space > 0 ? space : TooltipLineLength;
            if (space <= 0 && char.IsHighSurrogate(collapsed[index + take - 1]))
                take--;

            lines.Add(collapsed.Substring(index, take));
            index += take;

            while (index < collapsed.Length && collapsed[index] == ' ')
                index++;
        }

        if (index < collapsed.Length)
            lines[^1] += Ellipsis;

        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Collapses every run of whitespace to a single space and trims the ends,
    /// without allocating when there is nothing to collapse.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        var needsWork = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != ' ' && char.IsWhiteSpace(c))
            {
                needsWork = true;
                break;
            }

            if (c == ' ' && (i == 0 || i == text.Length - 1 || text[i + 1] == ' '))
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
            return text;

        var builder = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
