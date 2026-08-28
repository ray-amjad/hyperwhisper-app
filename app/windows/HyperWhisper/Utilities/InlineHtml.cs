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

/// <summary>What a block-level element is for.</summary>
public enum HtmlBlockKind
{
    /// <summary>An &lt;h2&gt; or &lt;h3&gt;.</summary>
    Heading,

    /// <summary>An &lt;li&gt;, or a "-"/"*" line in the plain-text fallback.</summary>
    Bullet,

    /// <summary>A &lt;p&gt;, or a plain line in the fallback.</summary>
    Paragraph
}

/// <summary>
/// One block-level element, already split into styled runs.
/// </summary>
/// <remarks>
/// <c>Runs</c> is never empty: the core drops a block that carries no text, so
/// "&lt;li&gt;  &lt;/li&gt;" never reaches a caller.
/// </remarks>
public readonly record struct HtmlBlock(HtmlBlockKind Kind, IReadOnlyList<HtmlRun> Runs);

/// <summary>
/// A release note as the Recent Updates cards render it: the heading above the
/// bullet list, and the bullets.
/// </summary>
/// <param name="Title">
/// The heading's runs, or an EMPTY list when the note has no title. Empty
/// rather than null because it is bound straight into XAML, where "no runs"
/// and "no title" render the same and a null check at every use site does not.
/// </param>
public sealed record HtmlReleaseNote(
    IReadOnlyList<HtmlRun> Title,
    IReadOnlyList<IReadOnlyList<HtmlRun>> Bullets);

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
        // InlineHtmlText.OnSourceChanged clears a TextBlock by binding null, so
        // the empty answer is given here rather than across the FFI boundary.
        if (string.IsNullOrEmpty(html)) return [];

        return RunsFrom(HyperwhisperCoreMethods.ReleaseNotesParseInline(html, collapseWhitespace));
    }

    /// <summary>
    /// Every block of a release note, in document order — the update dialog's
    /// view of a fragment.
    /// </summary>
    /// <remarks>
    /// This replaced the third copy of the &lt;li&gt; extractor (#284): a
    /// <c>&lt;(h[23]|li|p)[^&gt;]*&gt;(.*?)&lt;/\1&gt;</c> regex walker in
    /// UpdateAvailableWindow with its own &lt;br&gt;-split fallback. A note with
    /// no block markup at all still falls back to one block per line, and each
    /// line still keeps its own markup and is parsed EXACTLY ONCE — flattening
    /// the note and parsing the result again dropped every &lt;a href&gt; and
    /// turned markup a feed had escaped so it would show into a live link.
    /// <c>hw_releasenotes::split_blocks</c> owns that guard now and pins it with
    /// a test; nothing here re-parses a block's text.
    /// </remarks>
    public static List<HtmlBlock> SplitBlocks(string? html)
    {
        if (string.IsNullOrEmpty(html)) return [];

        return HyperwhisperCoreMethods.ReleaseNotesSplitBlocks(html)
            .Select(block => new HtmlBlock(KindFrom(block.kind), RunsFrom(block.runs)))
            .ToList();
    }

    /// <summary>
    /// A release note split into the title the cards show above the bullet list,
    /// and the bullets themselves — parsed once, together.
    /// </summary>
    /// <remarks>
    /// Decision (c) of #284: the title is the first &lt;h2&gt;
    /// case-insensitively and whatever attributes it carries, else the content
    /// before the first &lt;ul&gt; (or before the first &lt;li&gt; when there is
    /// no &lt;ul&gt;). This head used to take only the first
    /// <c>&lt;h2&gt;</c> — case-SENSITIVE, no attributes allowed — and macOS
    /// took only the content before the list. Each feed keeps rendering exactly
    /// as it does today and each head gains the other's shape.
    /// </remarks>
    public static HtmlReleaseNote ParseNote(string? html)
    {
        if (string.IsNullOrEmpty(html)) return new HtmlReleaseNote([], []);

        var note = HyperwhisperCoreMethods.ReleaseNotesParse(html);

        // The core reports "no title" as a missing block; the app reports it as
        // no runs, so a caller never has to null-check before rendering.
        IReadOnlyList<HtmlRun> title = note.title is { } block ? RunsFrom(block.runs) : [];

        var bullets = new List<IReadOnlyList<HtmlRun>>(note.bullets.Count);
        foreach (var bullet in note.bullets) bullets.Add(RunsFrom(bullet.runs));

        return new HtmlReleaseNote(title, bullets);
    }

    /// <summary>
    /// Map the core's runs onto the app's own, building the <c>Uri</c> here.
    /// </summary>
    /// <remarks>
    /// The single place a core type crosses into the app, so the UniFFI types
    /// stay internal to the binding assembly and no other file has to see them.
    /// </remarks>
    private static List<HtmlRun> RunsFrom(List<HwRun> runs)
    {
        var mapped = new List<HtmlRun>(runs.Count);

        foreach (var run in runs)
        {
            Uri? link = run.link is { } href && Uri.TryCreate(href, UriKind.Absolute, out var uri)
                ? uri
                : null;

            mapped.Add(new HtmlRun(run.text, run.bold, run.italic, link));
        }

        return mapped;
    }

    private static HtmlBlockKind KindFrom(HwBlockKind kind) => kind switch
    {
        HwBlockKind.Heading => HtmlBlockKind.Heading,
        HwBlockKind.Bullet => HtmlBlockKind.Bullet,
        // Paragraph is the core's own answer for anything that is not a heading
        // or a bullet, so it is the safe arm for a kind added there later: the
        // block still renders, as body text, rather than throwing on a feed.
        _ => HtmlBlockKind.Paragraph
    };
}
