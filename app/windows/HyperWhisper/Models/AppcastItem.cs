using HyperWhisper.Utilities;

namespace HyperWhisper.Models;

public class AppcastItem
{
    private readonly string _releaseNotes = "";
    private readonly IReadOnlyList<HtmlRun> _releaseTitle = [];
    private readonly IReadOnlyList<IReadOnlyList<HtmlRun>> _bulletPoints = [];

    public string Version { get; init; } = "";
    public DateTime PubDate { get; init; }

    /// <summary>
    /// The feed entry's release-notes HTML.
    /// </summary>
    /// <remarks>
    /// PARSE ONCE, AT CONSTRUCTION. <see cref="ReleaseTitle"/> and
    /// <see cref="BulletPoints"/> used to be getters that re-ran a Regex — and
    /// then a second parse per bullet in the renderer — on every read, and WPF
    /// reads a bound property on every layout pass of every card in the Recent
    /// Updates list. The whole note is now read here, in one call, and the
    /// results are stored. The class has no constructor (it is built with
    /// object-initializer syntax at AppcastService and in the smoke suite), so
    /// this init accessor is the one place that can do it.
    /// </remarks>
    public string ReleaseNotes
    {
        get => _releaseNotes;
        init
        {
            _releaseNotes = value;

            // An entry with no notes parses as the empty fragment: no title, no
            // bullets. There is no second parse anywhere below, or in the views.
            var note = InlineHtml.ParseNote(value);
            _releaseTitle = note.Title;
            _bulletPoints = note.Bullets;
        }
    }

    public bool IsLatest { get; set; }

    /// <summary>
    /// Built with object-initializer syntax, so the parameterless constructor is
    /// spelled out — declaring the copy constructor below would otherwise remove
    /// the implicit one and break every call site.
    /// </summary>
    public AppcastItem() { }

    /// <summary>
    /// Copies an item WITHOUT re-reading its notes: the parsed title and bullets
    /// are carried over as they are.
    /// </summary>
    /// <remarks>
    /// <see cref="ReleaseNotes"/>'s init accessor is the one place the note is
    /// parsed, so copying an item through it would parse the same feed entry a
    /// second time — which is what <see cref="Copy"/> exists to avoid. It also
    /// keeps the copy off the FFI boundary entirely, so handing out a cached
    /// list cannot fail.
    /// </remarks>
    private AppcastItem(AppcastItem source)
    {
        Version = source.Version;
        PubDate = source.PubDate;
        IsLatest = source.IsLatest;
        _releaseNotes = source._releaseNotes;
        _releaseTitle = source._releaseTitle;
        _bulletPoints = source._bulletPoints;
    }

    /// <summary>
    /// A copy of this item that shares its already-parsed note.
    /// </summary>
    public AppcastItem Copy() => new(this);

    public string FormattedDate => PubDate.ToString("MMM d, yyyy");

    /// <summary>
    /// The heading shown above the bullet list, as styled runs — empty when the
    /// note has no title.
    /// </summary>
    /// <remarks>
    /// Runs, not a string: the title carries the feed's own emphasis, and
    /// HomePage has always rendered it through InlineHtmlText rather than as
    /// plain text. Under decision (c) of #284 the title may also be the content
    /// before the list, which on the macOS-shaped feed is a bare
    /// "&lt;b&gt;…&lt;/b&gt;" — so dropping to a string would strip emphasis the
    /// UI shows today. macOS stores the same thing as an AttributedString.
    /// Use <see cref="HasReleaseTitle"/> to hide the row; a list is never "".
    /// </remarks>
    public IReadOnlyList<HtmlRun> ReleaseTitle => _releaseTitle;

    /// <summary>
    /// Whether the note has a title at all — the visibility signal that the
    /// XAML DataTrigger's <c>Value=""</c> used to give.
    /// </summary>
    public bool HasReleaseTitle => _releaseTitle.Count > 0;

    /// <summary>
    /// Every &lt;li&gt; of the note, in document order, already split into
    /// styled runs. An item that carries no text is dropped by the core.
    /// </summary>
    /// <remarks>
    /// This was the inner HTML of each item, re-parsed by InlineHtmlText at
    /// render time. The extraction and the inline parse now happen once,
    /// together, in hw-releasenotes — which is what stops this head's
    /// "&lt;li[^&gt;]*&gt;(.*?)&lt;/li&gt;" and macOS's own scanner drifting
    /// apart again (#284).
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<HtmlRun>> BulletPoints => _bulletPoints;

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
}
