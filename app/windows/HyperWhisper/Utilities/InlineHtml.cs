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

        // Close the current run at a <b>/<i>/<a> boundary. A space waiting to be
        // written belongs *outside* the element: with the text before an opening
        // tag, and with the text after a closing one. So "<b>See</b> <a>here</a>"
        // does not underline and tint the space in front of the link, and
        // "<a><b>the page</b> </a>now" does not leave a linked space behind
        // either. Appending to an empty buffer makes that space its own run,
        // carrying the style and destination in force where it is emitted.
        void FlushAtTagBoundary(bool isClosing)
        {
            if (pendingSpace && !isClosing)
            {
                current.Append(' ');
                pendingSpace = false;
            }

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
            var tag = ParseTag(raw);

            if (tag.Name == "br")
            {
                AppendLineBreak();
                return;
            }

            // A tag that closes itself opens and closes in one go, so it changes
            // no state at all. Acting on it would push a depth or a link entry
            // nothing ever pops, and the rest of the note would render bold,
            // italic or linked: "<a …/>", "<b/>", "<i/>".
            if (tag.IsSelfClosing) return;

            switch (tag.Name)
            {
                case "b":
                case "strong":
                    FlushAtTagBoundary(tag.IsClosing);
                    boldDepth = tag.IsClosing ? Math.Max(0, boldDepth - 1) : boldDepth + 1;
                    break;
                case "i":
                case "em":
                    FlushAtTagBoundary(tag.IsClosing);
                    italicDepth = tag.IsClosing ? Math.Max(0, italicDepth - 1) : italicDepth + 1;
                    break;
                case "a":
                    FlushAtTagBoundary(tag.IsClosing);
                    if (tag.IsClosing)
                    {
                        if (linkStack.Count > 0) linkStack.RemoveAt(linkStack.Count - 1);
                    }
                    else
                    {
                        linkStack.Add(LinkFrom(tag.Href));
                    }
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

            if (character == '&' && DecodeEntityAt(html, index, out var afterEntity) is { } decoded)
            {
                Append(decoded);
                index = afterEntity;
                continue;
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
    /// <remarks>
    /// A quote only opens a value where <see cref="ParseTag"/> would read one:
    /// straight after an '='. Anywhere else it is an ordinary character, so the
    /// apostrophe in a bare "href=it's" cannot pair up with a later one and run
    /// the scan past the '&gt;' that really ends the tag.
    /// </remarks>
    private static int FindTagEnd(string html, int start)
    {
        var index = start + 1;
        var inValuePosition = false;

        while (index < html.Length)
        {
            var character = html[index];

            if (inValuePosition && character is '"' or '\'')
            {
                var quotedEnd = html.IndexOf(character, index + 1);
                if (quotedEnd < 0) break;
                index = quotedEnd + 1;
                inValuePosition = false;
                continue;
            }

            if (character == '>') return index;

            // Whitespace between '=' and the value is allowed, so it leaves the
            // position alone rather than ending it.
            if (!char.IsWhiteSpace(character)) inValuePosition = character == '=';
            index++;
        }

        return html.IndexOf('>', start);
    }

    /// <summary>
    /// Destination for an &lt;a&gt;'s href, or null when it is missing or is
    /// not a scheme we are willing to open.
    /// </summary>
    private static Uri? LinkFrom(string? href)
    {
        if (href is null) return null;

        // Entities only: feeds escape query separators, so "?a=1&amp;b=2" has to
        // be decoded before it is a URL. Nothing else about the href may change —
        // running it through the whole parser stripped markup and collapsed
        // whitespace inside it, quietly opening a different address than the
        // feed asked for.
        var decoded = DecodeEntities(href).Trim();

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)) return null;
        return AllowedSchemes.Contains(uri.Scheme) ? uri : null;
    }

    /// <summary>
    /// One tag, tokenized: the element name (lower-cased), whether it closes an
    /// element, whether it closes itself, and its href if it has one.
    /// </summary>
    private readonly record struct Tag(string Name, bool IsClosing, bool IsSelfClosing, string? Href);

    /// <summary>
    /// Walk a tag's body once — a leading '/', the element name, then attribute
    /// by attribute — and report everything the caller needs to know about it.
    /// The first href wins; its value keeps its own case.
    /// </summary>
    /// <remarks>
    /// The tag is walked rather than searched, so a value that happens to
    /// contain "href=" — a title, say — can never be mistaken for the attribute
    /// itself, and a bare value that ends in '/' — most URLs — is not mistaken
    /// for a self-closing tag. Only a '/' standing where a name may start, with
    /// nothing but whitespace after it, closes the tag. An unterminated quote
    /// gives up on the rest of the tag instead of inventing a value out of it.
    /// </remarks>
    private static Tag ParseTag(string raw)
    {
        var body = raw.Trim();
        var index = 0;

        var isClosing = body.StartsWith('/');
        if (isClosing) index++;

        var name = string.Empty;
        var haveName = false;
        var isSelfClosing = false;
        string? href = null;

        while (index < body.Length)
        {
            while (index < body.Length && char.IsWhiteSpace(body[index])) index++;
            if (index >= body.Length) break;

            // A '/' where a name could start is the tag closing itself — "<a/>",
            // "<br />", "<a href=… />" — but only if nothing but whitespace
            // follows it, so the next token read clears this again. A '/' inside
            // a bare value, on the other hand, is simply part of the URL.
            if (body[index] == '/')
            {
                isSelfClosing = true;
                index++;
                continue;
            }

            isSelfClosing = false;

            var tokenStart = index;
            while (index < body.Length && !char.IsWhiteSpace(body[index])
                   && body[index] != '=' && body[index] != '/') index++;
            var tokenEnd = index;

            while (index < body.Length && char.IsWhiteSpace(body[index])) index++;

            string? value = null;
            if (index < body.Length && body[index] == '=')
            {
                index++;
                while (index < body.Length && char.IsWhiteSpace(body[index])) index++;
                if (index >= body.Length) break;   // "href=" with nothing after it

                var quote = body[index];
                if (quote is '"' or '\'')
                {
                    var quotedEnd = body.IndexOf(quote, index + 1);
                    if (quotedEnd < 0) break;   // unterminated: nothing here is trustworthy
                    value = body[(index + 1)..quotedEnd];
                    index = quotedEnd + 1;
                }
                else
                {
                    var valueStart = index;
                    while (index < body.Length && !char.IsWhiteSpace(body[index])) index++;
                    value = body[valueStart..index];
                }
            }

            // The first token is the element name; every token after it is an
            // attribute, valued or not.
            if (!haveName)
            {
                name = body[tokenStart..tokenEnd].ToLowerInvariant();
                haveName = true;
            }
            else if (href is null &&
                     body.AsSpan(tokenStart, tokenEnd - tokenStart).Equals("href", StringComparison.OrdinalIgnoreCase))
            {
                href = value;
            }
        }

        return new Tag(name, isClosing, isSelfClosing, href);
    }

    /// <summary>
    /// Decode every character entity in a fragment, leaving all other text —
    /// markup included — exactly as it was.
    /// </summary>
    private static string DecodeEntities(string text)
    {
        if (!text.Contains('&')) return text;

        var result = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            if (text[index] == '&' && DecodeEntityAt(text, index, out var afterEntity) is { } decoded)
            {
                result.Append(decoded);
                index = afterEntity;
                continue;
            }

            result.Append(text[index]);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Decode the entity starting at <paramref name="start"/> (an '&amp;'), and
    /// report where it ends. Null when there is no complete, recognised entity
    /// there, in which case the '&amp;' stays literal text.
    /// </summary>
    private static string? DecodeEntityAt(string text, int start, out int end)
    {
        end = start;

        var limit = Math.Min(text.Length, start + EntityScanLimit);
        var semicolon = text.IndexOf(';', start, limit - start);
        if (semicolon <= start) return null;

        if (DecodeEntity(text[(start + 1)..semicolon]) is not { } decoded) return null;

        end = semicolon + 1;
        return decoded;
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
