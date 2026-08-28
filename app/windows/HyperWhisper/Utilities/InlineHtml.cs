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
// The PARSER is not here. Issue #284 moved it into the shared Rust core
// (hw-releasenotes) and this is now a facade over ReleaseNotesParseInline /
// ReleaseNotesPlainText: the tokenizer, the entity decoder and the scheme
// allowlist existed in both C# and Swift, and every fix to one of them had to
// be made twice. macOS ReleaseNotesHTML.swift is the same facade over the same
// two calls, so the two heads can no longer drift apart. The InlineHtml block
// of HyperWhisper.SmokeTests still pins the answer this file returns.
//
// Deliberately free of WPF types: WPF binding lives in InlineHtmlText.cs, so
// this parser stays testable from the smoke-test console host.

using uniffi.hyperwhisper_core;

namespace HyperWhisper.Utilities;

/// <summary>
/// Inline emphasis carried by a stretch of text, plus the destination of the
/// &lt;a href&gt; it sits inside, if any.
/// </summary>
public readonly record struct HtmlRun(string Text, bool Bold, bool Italic, Uri? Link = null);

public static class InlineHtml
{
    /// <summary>
    /// Tag-free, entity-decoded text — for glyph selection, logging and tests.
    /// </summary>
    /// <param name="collapseWhitespace">
    /// False keeps existing line breaks, for callers that split the result into lines.
    /// </param>
    public static string PlainText(string? html, bool collapseWhitespace = true) =>
        string.IsNullOrEmpty(html)
            ? string.Empty
            : HyperwhisperCoreMethods.ReleaseNotesPlainText(html, collapseWhitespace);

    /// <summary>
    /// Split a fragment into styled runs. HTML collapses whitespace, and feed
    /// entries are indented across several lines, so runs of whitespace become
    /// a single space unless the caller asks to keep them.
    /// </summary>
    /// <remarks>
    /// <c>Link</c> arrives from the core as the feed's href verbatim — already
    /// entity-decoded, trimmed, and checked against the http/https/mailto
    /// allowlist there. It is handed to <c>Uri.TryCreate</c> untouched:
    /// decoding or trimming it a second time would open a different address
    /// than the feed asked for, and the allowlist decision is not re-made here.
    /// <c>Uri.TryCreate</c> itself stays, because a string the core allows but
    /// <c>Uri</c> cannot parse is no link at all — which is what it was before
    /// this moved to Rust. <c>UriKind.Absolute</c> is what still rejects a
    /// relative "/path".
    /// </remarks>
    public static List<HtmlRun> Parse(string? html, bool collapseWhitespace = true)
    {
        var runs = new List<HtmlRun>();

        // InlineHtmlText.OnSourceChanged clears a TextBlock by binding null, so
        // the empty answer is given here rather than across the FFI boundary.
        if (string.IsNullOrEmpty(html)) return runs;

        foreach (var run in HyperwhisperCoreMethods.ReleaseNotesParseInline(html, collapseWhitespace))
        {
            Uri? link = run.link is { } href && Uri.TryCreate(href, UriKind.Absolute, out var uri)
                ? uri
                : null;

            runs.Add(new HtmlRun(run.text, run.bold, run.italic, link));
        }

        return runs;
    }
}
