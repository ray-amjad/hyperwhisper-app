using System.Net.Http;
using System.Xml.Linq;
using HyperWhisper.Models;

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

            XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";

            var items = doc.Descendants("item")
                .Select(item =>
                {
                    var version = item.Element(sparkle + "version")?.Value
                                  ?? item.Element(sparkle + "shortVersionString")?.Value
                                  ?? "";
                    var pubDateStr = item.Element("pubDate")?.Value ?? "";
                    DateTime.TryParse(pubDateStr, out var pubDate);
                    var releaseNotes = item.Element(sparkle + "releaseNotesLink") != null
                        ? ""
                        : item.Element("description")?.Value ?? "";

                    return new AppcastItem
                    {
                        Version = version,
                        PubDate = pubDate,
                        ReleaseNotes = releaseNotes
                    };
                })
                .Where(i => !string.IsNullOrEmpty(i.Version) && i.HasReleaseNotes)
                .GroupBy(i => i.Version)
                .Select(g => g.First())
                .OrderByDescending(i => i.PubDate)
                .ToList();

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

    public void ClearCache()
    {
        _cachedReleases = null;
        _cacheTime = DateTime.MinValue;
        _lastFailureTime = DateTime.MinValue;
    }

    private List<AppcastItem> CreateReleaseResult(int maxCount)
    {
        return (_cachedReleases ?? [])
            .Take(maxCount)
            .Select(item => new AppcastItem
            {
                Version = item.Version,
                PubDate = item.PubDate,
                ReleaseNotes = item.ReleaseNotes,
                IsLatest = item.IsLatest
            })
            .ToList();
    }
}
