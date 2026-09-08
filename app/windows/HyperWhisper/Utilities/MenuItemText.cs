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

    /// <summary>
    /// True when <see cref="Bound"/> would drop or rewrite part of
    /// <paramref name="text"/>, i.e. when the item needs a tooltip carrying the
    /// whole name. False for the ordinary short name, which would only get a
    /// tooltip repeating the label back at the user.
    /// </summary>
    public static bool NeedsFullTextTooltip(string? text)
        => !string.IsNullOrEmpty(text) && Bound(text) != text;

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
