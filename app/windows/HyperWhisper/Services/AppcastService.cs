// APPCAST FETCH FOR THE RECENT UPDATES LIST
//
// Fetches the Windows appcast feed and turns it into the AppcastItem list the
// Updates page renders.
//
// The SELECTION RULES are not here. Issue #353 moved them into the shared Rust
// core (hw-releasenotes' `appcast` module) and this is now a facade: read the
// <item> elements with XDocument, hand every one of them over as a raw
// HwAppcastFeedEntry in document order, and map what comes back. Which field
// the version comes from, which entries are dropped, how duplicates collapse
// and how the list is ordered are all decided once, in Rust, for this head and
// macOS both — the two had drifted into different answers for every one of
// those questions.
//
// So: NO Where, GroupBy or OrderByDescending below. Re-applying a rule here
// would let this head drift from the shared answer again, which is the whole
// defect #353 closes. The only thing this file still decides is what the XML
// reader looks at, which is native by design (no XML crate in the core).
//
// Everything around the selection step is unchanged and stays native: the
// singleton, both cache TTLs, the post-failure back-off, the catch ladder, the
// per-call cap in CreateReleaseResult, and `items[0].IsLatest = true` — which
// means "index 0 of the returned list" and so belongs to the caller, not to a
// feed entry. It can no longer drift, because the ordering that defines index
// 0 is now shared.

using System.Net.Http;
using System.Xml.Linq;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

public class AppcastService
{
    private static AppcastService? _instance;
    private static readonly object _lock = new();

    public static AppcastService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new AppcastService();
                }
            }
            return _instance;
        }
    }

    private const string AppcastUrl = "https://www.hyperwhisper.com/appcast-windows.xml";
    private const int RequestTimeoutSeconds = 10;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    // Shorter TTL after a failure so a stalled/unreachable network (e.g. captive
    // portal) doesn't re-trigger a full fetch on every reopen of the Updates page.
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
    };
    private List<AppcastItem>? _cachedReleases;
    private DateTime _cacheTime = DateTime.MinValue;
    private DateTime _lastFailureTime = DateTime.MinValue;

    private AppcastService() { }

    public async Task<Result<List<AppcastItem>>> GetRecentReleasesAsync(
        int maxCount = 5,
        CancellationToken cancellationToken = default)
    {
        // Return cached if still valid
        if (_cachedReleases != null && DateTime.Now - _cacheTime < CacheDuration)
        {
            return Result<List<AppcastItem>>.Success(CreateReleaseResult(maxCount));
        }

        // Back off after a recent transient network failure so an unreachable
        // network doesn't stall the UI on every reopen of the Updates page.
        if (DateTime.Now - _lastFailureTime < FailureCacheDuration)
        {
            return Result<List<AppcastItem>>.Failure(
                new TimeoutException("Appcast fetch recently failed; backing off."));
        }

        try
        {
            LoggingService.Debug("AppcastService: Fetching appcast from " + AppcastUrl);
            var xml = await _httpClient.GetStringAsync(AppcastUrl, cancellationToken);
            var doc = XDocument.Parse(xml);

            var items = SelectReleases(doc);

            if (items.Count > 0)
            {
                items[0].IsLatest = true;
            }

            _cachedReleases = items;
            _cacheTime = DateTime.Now;
            _lastFailureTime = DateTime.MinValue;

            LoggingService.Info($"AppcastService: Fetched {items.Count} releases");
            return Result<List<AppcastItem>>.Success(CreateReleaseResult(maxCount));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _lastFailureTime = DateTime.Now;
            var timeout = new TimeoutException("Appcast fetch timed out.", ex);
            LoggingService.Error("AppcastService: Fetch timed out.", timeout);
            return Result<List<AppcastItem>>.Failure(timeout);
        }
        catch (OperationCanceledException ex)
        {
            LoggingService.Debug("AppcastService: Fetch cancelled by caller.");
            return Result<List<AppcastItem>>.Failure(ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            _lastFailureTime = DateTime.Now;
            LoggingService.Error($"AppcastService: Network error fetching appcast: {ex.Message}", ex);
            return Result<List<AppcastItem>>.Failure(ex);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"AppcastService: Failed to fetch appcast: {ex.Message}", ex);
            return Result<List<AppcastItem>>.Failure(ex);
        }
    }

    /// <summary>
    /// The feed's &lt;item&gt; elements as the Recent Updates list shows them:
    /// filtered, deduplicated and newest first.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="GetRecentReleasesAsync"/> so the whole
    /// XML-to-items step can be driven from a document without an HTTP fetch —
    /// the smoke suite replays the committed <c>appcast-windows.xml</c> through
    /// it and pins the answer against the 15 releases this head shipped before
    /// #353. It reads the XML and maps the result; every rule between those two
    /// steps belongs to <c>AppcastSelectReleases</c>.
    /// <para>
    /// <see cref="AppcastItem.IsLatest"/> is deliberately NOT set here. It means
    /// "index 0 of the list the caller is about to render", so the caller owns
    /// it.
    /// </para>
    /// </remarks>
    internal static List<AppcastItem> SelectReleases(XDocument doc)
    {
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";

        // Every <item>, in DOCUMENT ORDER, with no rule applied — not even the
        // "" fallbacks the old reader used. An absent element stays null,
        // because "absent" and "present but empty" are the core's distinction
        // to make, not this reader's. `hasReleaseNotesLink` is the same
        // expression this file used to branch on; it now sets a flag and Rust
        // decides what it means.
        var entries = new List<HwAppcastFeedEntry>();
        foreach (var item in doc.Descendants("item"))
        {
            entries.Add(new HwAppcastFeedEntry(
                @title: item.Element("title")?.Value,
                @sparkleVersion: item.Element(sparkle + "version")?.Value,
                @sparkleShortVersionString: item.Element(sparkle + "shortVersionString")?.Value,
                @pubDate: item.Element("pubDate")?.Value,
                @description: item.Element("description")?.Value,
                @hasReleaseNotesLink: item.Element(sparkle + "releaseNotesLink") != null));
        }

        // Filter, dedupe and order in one shared call. What comes back is
        // already newest-first with duplicate versions collapsed, so nothing
        // below re-sorts, re-filters or re-dedupes it.
        var items = new List<AppcastItem>(entries.Count);
        foreach (var release in HyperwhisperCoreMethods.AppcastSelectReleases(entries))
        {
            items.Add(new AppcastItem
            {
                Version = release.version,
                // LocalDateTime, not UtcDateTime: the old DateTime.TryParse on
                // an offset-bearing string returned Kind = Local, and
                // FormattedDate renders the value as it stands — so converting
                // to UTC here would silently shift every displayed date by the
                // local offset. The core bounds the year to 1..=9999, which is
                // what keeps this call from throwing on a hostile feed.
                PubDate = DateTimeOffset.FromUnixTimeSeconds(release.pubDateEpochSecs).LocalDateTime,
                // Via the object initializer, because this setter parses the
                // note (issue #284) and it must run exactly once per item.
                ReleaseNotes = release.releaseNotes
            });
        }

        return items;
    }

    public void ClearCache()
    {
        _cachedReleases = null;
        _cacheTime = DateTime.MinValue;
        _lastFailureTime = DateTime.MinValue;
    }

    /// <summary>
    /// A defensive copy of the cached releases, newest first.
    /// </summary>
    /// <remarks>
    /// <c>Copy()</c>, not a fresh object initializer: setting
    /// <c>ReleaseNotes</c> parses the note (issue #284), so rebuilding each item
    /// here would re-parse the whole cache on every read — which is the cost this
    /// change removes, not one to move somewhere else. The copy shares the
    /// already-parsed title and bullets.
    /// </remarks>
    private List<AppcastItem> CreateReleaseResult(int maxCount)
    {
        return (_cachedReleases ?? [])
            .Take(maxCount)
            .Select(item => item.Copy())
            .ToList();
    }
}
