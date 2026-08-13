// INLINE HTML FOR RELEASE NOTES
//
// Converts the small slice of HTML the appcast feeds use for release notes
// into styled runs the UI can render.
//
// Supported: <b>/<strong>, <i>/<em>, <a href>, <br>, and character entities.
// Everything else is dropped, keeping its text content — so a feed that grows
// a <span> degrades to plain text instead of leaking markup into the UI, which
// is what used to happen with <b> in the Recent Updates cards.
//
// Only http, https and mailto links are carried through. A javascript: or
// data: href keeps its label and loses the link, so a compromised feed cannot
// turn a release note into something the user can click into running code.
//
// Mirrors macOS ReleaseNotesHTML.swift — both platforms read the same feeds,
// so keep the two in step.
//
// Deliberately free of WPF types: WPF binding lives in InlineHtmlText.cs, so
// this parser stays testable from the smoke-test console host.

using System.Globalization;
using System.Text;

namespace HyperWhisper.Utilities;

/// <summary>
/// Inline emphasis carried by a stretch of text, plus the destination of the
/// &lt;a href&gt; it sits inside, if any.
/// </summary>
public readonly record struct HtmlRun(string Text, bool Bold, bool Italic, Uri? Link = null);

public static class InlineHtml
{
    /// <summary>Longest entity we look ahead for, including '&amp;' and ';'.</summary>
    private const int EntityScanLimit = 12;

    /// <summary>Characters that end an element name inside a tag.</summary>
    private static readonly char[] TagNameDelimiters = ['/', ' ', '\n', '\r', '\t'];

    /// <summary>
    /// Schemes we are willing to hand to the shell. Anything else — most of
    /// all javascript: and data: — keeps its label and loses its link.
    /// </summary>
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    private static readonly Dictionary<string, string> NamedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = " ",
        ["mdash"] = "—",
        ["ndash"] = "–",
        ["hellip"] = "…"
    };

    /// <summary>
    /// Tag-free, entity-decoded text — for glyph selection, logging and tests.
    /// </summary>
    /// <param name="collapseWhitespace">
    /// False keeps existing line breaks, for callers that split the result into lines.
    /// </param>
    public static string PlainText(string? html, bool collapseWhitespace = true)
    {
        var text = new StringBuilder();
        foreach (var run in Parse(html, collapseWhitespace))
        {
            text.Append(run.Text);
        }
        return text.ToString();
    }

    /// <summary>
    /// Split a fragment into styled runs. HTML collapses whitespace, and feed
    /// entries are indented across several lines, so runs of whitespace become
    /// a single space unless the caller asks to keep them.
    /// </summary>
    public static List<HtmlRun> Parse(string? html, bool collapseWhitespace = true)
    {
        var runs = new List<HtmlRun>();
        if (string.IsNullOrEmpty(html)) return runs;

        var current = new StringBuilder();
        var boldDepth = 0;
        var italicDepth = 0;
        var pendingSpace = false;
        var producedText = false;

        // One entry per open <a>, null when its href was missing or unusable —
        // so the matching </a> still pops the right thing and the label
        // survives as ordinary text. The innermost entry wins: a nested <a>
        // with a rejected href must not inherit the outer destination.
        var linkStack = new List<Uri?>();

        void Flush()
        {
            if (current.Length == 0) return;
            runs.Add(new HtmlRun(current.ToString(), boldDepth > 0, italicDepth > 0, linkStack.LastOrDefault()));
            current.Clear();
        }

        // Close the current run at a <b>/<i>/<a> boundary. A space waiting to
        // be written belongs to the text before the tag, so "bold <i>x</i>"
        // keeps its space outside the italic run — and "<b>See</b> <a>here</a>"
        // does not underline and tint the space in front of the link.
        void FlushAtTagBoundary()
        {
            if (!pendingSpace)
            {
                Flush();
                return;
            }

            if (current.Length == 0)
            {
                // Nothing left to hang the space on, so it becomes its own run
                // carrying the style and destination in force when it was read.
                runs.Add(new HtmlRun(" ", boldDepth > 0, italicDepth > 0, linkStack.LastOrDefault()));
                pendingSpace = false;
                return;
            }

            current.Append(' ');
            pendingSpace = false;
            Flush();
        }

        void Append(string text)
        {
            foreach (var character in text)
            {
                if (character is ' ' or '\n' or '\r' or '\t')
                {
                    if (collapseWhitespace)
                    {
                        pendingSpace = producedText;
                        continue;
                    }

                    if (character is '\n' or '\r')
                    {
                        producedText = false;
                    }
                }

                if (pendingSpace)
                {
                    current.Append(' ');
                    pendingSpace = false;
                }

                current.Append(character);
                producedText = true;
            }
        }

        void AppendLineBreak()
        {
            current.Append('\n');
            pendingSpace = false;
            producedText = false;
        }

        void HandleTag(string raw)
        {
            var name = raw.Trim();
            var isClosing = name.StartsWith('/');
            if (isClosing) name = name[1..];

            // "<a …/>" opens and closes in one go, like "<br/>": it must not
            // push an entry nothing will ever pop, or every remaining word in
            // the note renders as part of the link.
            var isSelfClosing = !isClosing && name.EndsWith('/');

            // Keep the element name only: "br/" -> "br", "a href=…" -> "a".
            var end = name.IndexOfAny(TagNameDelimiters);
            if (end >= 0) name = name[..end];

            switch (name.ToLowerInvariant())
            {
                case "b":
                case "strong":
                    FlushAtTagBoundary();
                    boldDepth = isClosing ? Math.Max(0, boldDepth - 1) : boldDepth + 1;
                    break;
                case "i":
                case "em":
                    FlushAtTagBoundary();
                    italicDepth = isClosing ? Math.Max(0, italicDepth - 1) : italicDepth + 1;
                    break;
                case "a":
                    FlushAtTagBoundary();
                    if (isClosing)
                    {
                        if (linkStack.Count > 0) linkStack.RemoveAt(linkStack.Count - 1);
                    }
                    else if (!isSelfClosing)
                    {
                        linkStack.Add(LinkFromTag(raw));
                    }
                    break;
                case "br":
                    AppendLineBreak();
                    break;
            }
        }

        var index = 0;
        while (index < html.Length)
        {
            var character = html[index];

            if (character == '<')
            {
                var close = FindTagEnd(html, index);
                if (close < 0)
                {
                    // Unterminated tag: the rest is text, not markup.
                    Append(html[index..]);
                    break;
                }

                HandleTag(html[(index + 1)..close]);
                index = close + 1;
                continue;
            }

            if (character == '&')
            {
                var limit = Math.Min(html.Length, index + EntityScanLimit);
                var semicolon = html.IndexOf(';', index, limit - index);
                if (semicolon > index && DecodeEntity(html[(index + 1)..semicolon]) is { } decoded)
                {
                    Append(decoded);
                    index = semicolon + 1;
                    continue;
                }
            }

            Append(character.ToString());
            index++;
        }

        Flush();
        return runs;
    }

    /// <summary>
    /// Index of the '&gt;' that ends the tag opened at <paramref name="start"/>,
    /// ignoring any '&gt;' inside a quoted attribute value — a URL may carry one
    /// in its query. Returns -1 when the tag is never closed. A quote that is
    /// never closed falls back to the first '&gt;', so one malformed attribute
    /// cannot swallow the rest of the fragment as markup.
    /// </summary>
    private static int FindTagEnd(string html, int start)
    {
        var index = start + 1;

        while (index < html.Length)
        {
            var character = html[index];

            if (character is '"' or '\'')
            {
                var quotedEnd = html.IndexOf(character, index + 1);
                if (quotedEnd < 0) break;
                index = quotedEnd + 1;
                continue;
            }

            if (character == '>') return index;
            index++;
        }

        return html.IndexOf('>', start);
    }

    /// <summary>
    /// Destination of an &lt;a …&gt; tag, or null when it has no usable href.
    /// <paramref name="raw"/> is the tag's contents, without the angle brackets.
    /// </summary>
    private static Uri? LinkFromTag(string raw)
    {
        if (AttributeValue(raw, "href") is not { } href) return null;

        // Feeds escape query separators, so "?a=1&amp;b=2" has to be decoded
        // before it is a URL.
        var decoded = PlainText(href).Trim();

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)) return null;
        return AllowedSchemes.Contains(uri.Scheme) ? uri : null;
    }

    /// <summary>
    /// Value of an attribute inside a tag, quoted or bare. Case-insensitive on
    /// the name; the value keeps its own case.
    /// </summary>
    /// <remarks>
    /// The tag is walked attribute by attribute rather than searched for the
    /// name, so a value that happens to contain "href=" — a title, say — can
    /// never be mistaken for the attribute itself. An unterminated quote gives
    /// up (null) instead of inventing a value out of the rest of the tag.
    /// </remarks>
    private static string? AttributeValue(string tag, string name)
    {
        var index = 0;

        while (index < tag.Length)
        {
            while (index < tag.Length && char.IsWhiteSpace(tag[index])) index++;
            if (index >= tag.Length) break;

            // The element name is the first token and has no '=', so it falls
            // through the valueless-attribute path below.
            var nameStart = index;
            while (index < tag.Length && !char.IsWhiteSpace(tag[index]) && tag[index] != '=') index++;
            var nameLength = index - nameStart;

            while (index < tag.Length && char.IsWhiteSpace(tag[index])) index++;
            if (index >= tag.Length || tag[index] != '=') continue;   // valueless attribute

            index++;
            while (index < tag.Length && char.IsWhiteSpace(tag[index])) index++;
            if (index >= tag.Length) return null;

            var isTarget = nameLength == name.Length &&
                string.Compare(tag, nameStart, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0;

            var quote = tag[index];
            if (quote is '"' or '\'')
            {
                index++;
                var quotedEnd = tag.IndexOf(quote, index);
                if (quotedEnd < 0) return null;   // unterminated: nothing here is trustworthy
                if (isTarget) return tag[index..quotedEnd];
                index = quotedEnd + 1;
                continue;
            }

            var valueStart = index;
            while (index < tag.Length && !char.IsWhiteSpace(tag[index])) index++;
            if (isTarget) return tag[valueStart..index];
        }

        return null;
    }

    /// <summary>
    /// Decode the body of an entity ("amp", "#8212", "#x2014").
    /// Returns null for anything unrecognised, so it stays literal text.
    /// </summary>
    private static string? DecodeEntity(string body)
    {
        if (NamedEntities.TryGetValue(body, out var named)) return named;
        if (!body.StartsWith('#')) return null;

        var digits = body[1..];
        var isHex = digits.StartsWith('x') || digits.StartsWith('X');

        var parsed = isHex
            ? int.TryParse(digits[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1
            : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var dec) ? dec : -1;

        if (parsed < 0 || parsed > 0x10FFFF) return null;

        try
        {
            return char.ConvertFromUtf32(parsed);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Surrogate code point — not a character on its own.
            return null;
        }
    }
}
