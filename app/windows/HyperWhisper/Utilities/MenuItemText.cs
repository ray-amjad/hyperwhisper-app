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
/// THE BOUND IS ON RENDERED WIDTH, not on a character count. A character count
/// is not a width: sixty full-width CJK glyphs are about twice sixty Latin ones,
/// and a mode name in Japanese is not exotic in an app that ships forty locales.
/// So the label is measured with the menu's own font and cut to fit
/// <see cref="MaxWidthPixels"/>. <see cref="MaxLength"/> stays as a second,
/// cheaper ceiling so the answer is bounded even where measurement is not
/// available.
/// </summary>
public static class MenuItemText
{
    /// <summary>
    /// The widest a menu label may render, in the same device pixels
    /// <see cref="System.Windows.Forms.TextRenderer"/> measures in. Roughly sixty
    /// Latin characters at the menu font: far longer than any real mode name, and
    /// a fraction of the narrowest display Windows supports, so the submenu always
    /// has room to open beside its parent.
    /// </summary>
    public const int MaxWidthPixels = 420;

    /// <summary>
    /// Hard ceiling on characters kept before the ellipsis, applied on top of
    /// <see cref="MaxWidthPixels"/>. It is what bounds the label if measurement
    /// is unavailable — a menu can be rebuilt on a session with no usable device
    /// context, and a tray that throws there is worse than one that over-trims.
    /// </summary>
    public const int MaxLength = 60;

    /// <summary>Appended in place of everything that did not fit.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// The bounded, single-line form of <paramref name="text"/>, safe to hand to
    /// a <see cref="System.Windows.Forms.ToolStripMenuItem"/> label.
    /// </summary>
    /// <remarks>
    /// Two things are bounded, because a menu item grows in both directions:
    /// width, by measuring; and height, because WinForms renders a literal newline
    /// as a line break and the Local API will accept a mode name with one in it.
    /// Whitespace runs — including newlines and tabs — collapse to a single space
    /// so the item stays exactly one row tall.
    /// </remarks>
    public static string Bound(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var collapsed = CollapseWhitespace(text);
        var fits = FittingLength(collapsed, 0, MaxWidthPixels);

        if (fits >= collapsed.Length)
            return collapsed;

        return collapsed[..fits].TrimEnd() + Ellipsis;
    }

    /// <summary>The widest a single tooltip line may render, in device pixels.</summary>
    public const int TooltipLineWidthPixels = MaxWidthPixels;

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
    /// both directions: each line is measured to
    /// <see cref="TooltipLineWidthPixels"/>, and there are at most
    /// <see cref="TooltipMaxLines"/> lines before an ellipsis.
    ///
    /// This is the Win32 counterpart of the WPF surfaces' explicit
    /// <c>&lt;ToolTip&gt;&lt;TextBlock TextWrapping="Wrap" MaxWidth MaxHeight/&gt;</c>,
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
            var fits = FittingLength(collapsed, index, TooltipLineWidthPixels);
            if (index + fits >= collapsed.Length)
            {
                lines.Add(collapsed[index..]);
                index = collapsed.Length;
                break;
            }

            // Break on the last space inside what fits, so a name made of words
            // stays readable; fall back to the measured cut for one long run.
            var space = collapsed.LastIndexOf(' ', index + fits, fits);
            var take = space > index ? space - index : fits;

            // A single glyph wider than a whole line would otherwise make this
            // loop take nothing and never end.
            if (take <= 0)
                take = char.IsHighSurrogate(collapsed[index]) ? 2 : 1;

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
    /// How many characters of <paramref name="text"/>, starting at
    /// <paramref name="start"/>, render inside <paramref name="maxWidth"/> — never
    /// more than <see cref="MaxLength"/>, and never splitting a surrogate pair.
    /// </summary>
    /// <remarks>
    /// Rendered width is monotonic in length, so this is a binary search: about
    /// six measurements per label rather than one per character.
    /// </remarks>
    private static int FittingLength(string text, int start, int maxWidth)
    {
        var available = text.Length - start;
        var high = Math.Min(available, MaxLength);
        if (high <= 0)
            return 0;

        // The common case by far: a real mode name that fits whole.
        if (high == available && MeasuredWidth(text.AsSpan(start, high)) <= maxWidth)
            return high;

        var low = 0;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (MeasuredWidth(text.AsSpan(start, middle)) <= maxWidth)
                low = middle;
            else
                high = middle - 1;
        }

        // Never cut between the two halves of a surrogate pair: the lone
        // surrogate left behind renders as a replacement glyph.
        if (low > 0 && char.IsHighSurrogate(text[start + low - 1]))
            low--;

        return low;
    }

    /// <summary>
    /// The width this text renders at in the menu's own font, or 0 when there is
    /// no device context to measure against — in which case
    /// <see cref="MaxLength"/> is the only bound left, which is the point of
    /// having it.
    /// </summary>
    private static bool _measurementFailureLogged;

    private static int MeasuredWidth(ReadOnlySpan<char> text)
    {
        try
        {
            var font = System.Drawing.SystemFonts.MenuFont ?? System.Drawing.SystemFonts.DefaultFont;
            return System.Windows.Forms.TextRenderer.MeasureText(
                text.ToString(),
                font,
                new System.Drawing.Size(int.MaxValue, int.MaxValue),
                System.Windows.Forms.TextFormatFlags.NoPadding).Width;
        }
        catch (Exception ex)
        {
            // Once, not once per glyph of a binary search over every menu item.
            if (!_measurementFailureLogged)
            {
                _measurementFailureLogged = true;
                Services.LoggingService.Warn(
                    $"MenuItemText: cannot measure menu labels, falling back to the {MaxLength}-character " +
                    $"cap: {ex.Message}");
            }

            return 0;
        }
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
