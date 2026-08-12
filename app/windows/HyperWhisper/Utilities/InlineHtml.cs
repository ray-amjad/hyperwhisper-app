// INLINE HTML FOR RELEASE NOTES
//
// Converts the small slice of HTML the appcast feeds use for release notes
// into styled runs the UI can render.
//
// Supported: <b>/<strong>, <i>/<em>, <br>, and character entities.
// Everything else is dropped, keeping its text content — so a feed that grows
// a <span> or an <a> degrades to plain text instead of leaking markup into the
// UI, which is what used to happen with <b> in the Recent Updates cards.
//
// Mirrors macOS ReleaseNotesHTML.swift — both platforms read the same feeds,
// so keep the two in step.
//
// Deliberately free of WPF types: WPF binding lives in InlineHtmlText.cs, so
// this parser stays testable from the smoke-test console host.

using System.Globalization;
using System.Text;

namespace HyperWhisper.Utilities;

/// <summary>Inline emphasis carried by a stretch of text.</summary>
public readonly record struct HtmlRun(string Text, bool Bold, bool Italic);

public static class InlineHtml
{
    /// <summary>Longest entity we look ahead for, including '&amp;' and ';'.</summary>
    private const int EntityScanLimit = 12;

    /// <summary>Characters that end an element name inside a tag.</summary>
    private static readonly char[] TagNameDelimiters = ['/', ' ', '\n', '\r', '\t'];

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

        void Flush()
        {
            if (current.Length == 0) return;
            runs.Add(new HtmlRun(current.ToString(), boldDepth > 0, italicDepth > 0));
            current.Clear();
        }

        // Close the current run at a <b>/<i> boundary. A space waiting to be
        // written belongs to the text before the tag, so "bold <i>x</i>" keeps
        // its space outside the italic run.
        void FlushAtTagBoundary()
        {
            if (pendingSpace && current.Length > 0)
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
            var name = raw.Trim();
            var isClosing = name.StartsWith('/');
            if (isClosing) name = name[1..];

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
                var close = html.IndexOf('>', index);
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
