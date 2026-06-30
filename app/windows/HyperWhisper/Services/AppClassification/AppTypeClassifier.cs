using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyperWhisper.Services.AppClassification;

public enum AppType
{
    Email,
    Ai,
    WorkMessaging,
    PersonalMessaging,
    Document,
    Code,
    Terminal,
    Sensitive,
    Other
}

public sealed record AppClassificationResult(
    AppType AppType,
    string Confidence,
    string Source,
    string? Matched);

public static class AppTypeExtensions
{
    public static string ToPromptValue(this AppType appType) => appType switch
    {
        AppType.WorkMessaging => "work_messaging",
        AppType.PersonalMessaging => "personal_messaging",
        _ => appType.ToString().ToLowerInvariant()
    };

    public static string ToCategory(this AppType appType) => appType switch
    {
        AppType.Email => "Email Client",
        AppType.Ai => "AI",
        AppType.WorkMessaging or AppType.PersonalMessaging => "Communication",
        AppType.Document => "Document",
        AppType.Code => "Code Editor",
        AppType.Terminal => "Terminal",
        AppType.Sensitive => "Sensitive",
        _ => "Application"
    };

    public static string ToTextFormat(this AppType appType) => appType switch
    {
        AppType.Email => "email",
        AppType.Code => "code",
        AppType.Terminal => "command",
        AppType.Document => "markdown",
        _ => "text"
    };
}

public sealed class AppTypeClassifier
{
    public static AppTypeClassifier Shared { get; } = new();

    private static readonly Regex EmailRegex = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

    private readonly AppTypeCatalog _catalog;
    private readonly PreparedEntry[] _orderedEntries;

    private AppTypeClassifier()
    {
        _catalog = LoadCatalog();
        _orderedEntries = PrepareEntries(_catalog);
    }

    // KEPT NATIVE (Rust shared-core wave): the core's AppClassify(bundleId,
    // processName, host?, title) drops the focusedElementType / focusedContent
    // inputs this method uses for email detection (subject/compose/to:/cc: and the
    // EmailRegex focused-element-text fallback). Swapping would silently lose that
    // signal, so — mirroring the macOS AppTypeClassifier decision — Classify stays
    // native this wave. Revisit if/when the FFI grows a focused-element param.
    public AppClassificationResult Classify(
        string processName,
        string? browserHost,
        string browserHostConfidence,
        string? windowTitle,
        string? browserTabTitle,
        string? focusedElementType,
        string? focusedContent)
    {
        if (TryMatchHost(browserHost, browserHostConfidence, out var hostMatch))
            return hostMatch;

        if (TryMatchProcess(processName, out var processMatch))
            return processMatch;

        var title = string.Join(" ", new[] { browserTabTitle, windowTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        if (TryMatchTitle(title, out var titleMatch))
            return titleMatch;

        if (TryMatchFocusedElement(focusedElementType, focusedContent, out var focusedMatch))
            return focusedMatch;

        return new AppClassificationResult(AppType.Other, "unknown", "default", null);
    }

    private bool TryMatchHost(string? host, string confidence, out AppClassificationResult result)
    {
        var normalizedHost = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            result = default!;
            return false;
        }

        foreach (var entry in _orderedEntries)
        {
            var matched = entry.Hosts.FirstOrDefault(candidate =>
                normalizedHost.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                || normalizedHost.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                result = new AppClassificationResult(entry.Type, confidence, "browserHost", matched);
                return true;
            }
        }

        result = default!;
        return false;
    }

    private bool TryMatchProcess(string processName, out AppClassificationResult result)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            result = default!;
            return false;
        }

        foreach (var entry in _orderedEntries)
        {
            if (entry.ProcessNames.TryGetValue(processName, out var matched))
            {
                result = new AppClassificationResult(entry.Type, "strong", "processName", matched);
                return true;
            }
        }

        result = default!;
        return false;
    }

    private bool TryMatchTitle(string title, out AppClassificationResult result)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            result = default!;
            return false;
        }

        foreach (var entry in _orderedEntries)
        {
            foreach (var keyword in entry.TitleKeywords)
            {
                if (KeywordMatches(keyword, title))
                {
                    result = new AppClassificationResult(entry.Type, "medium", "title", keyword.Value);
                    return true;
                }
            }
        }

        result = default!;
        return false;
    }

    private static bool KeywordMatches(PreparedKeyword keyword, string title)
    {
        if (keyword.IsSubstring)
            return title.Contains(keyword.Value, StringComparison.Ordinal);

        return keyword.WordBoundaryRegex!.IsMatch(title);
    }

    private static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().ToLowerInvariant();
        if (!trimmed.Contains("://"))
            trimmed = "https://" + trimmed;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }

        return trimmed.StartsWith("www.", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
    }

    private static bool TryMatchFocusedElement(
        string? focusedElementType,
        string? focusedContent,
        out AppClassificationResult result)
    {
        var pieces = string.Join(" ", new[] { focusedElementType, focusedContent }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        if (pieces.Contains("subject") || pieces.Contains("compose") || pieces.Contains("to:") || pieces.Contains("cc:"))
        {
            result = new AppClassificationResult(AppType.Email, "medium", "focusedElement", null);
            return true;
        }

        if (EmailRegex.IsMatch(pieces))
        {
            result = new AppClassificationResult(AppType.Email, "weak", "focusedElementText", null);
            return true;
        }

        result = default!;
        return false;
    }

    private static PreparedEntry[] PrepareEntries(AppTypeCatalog catalog)
    {
        AppType[] order =
        [
            AppType.Sensitive,
            AppType.Email,
            AppType.Terminal,
            AppType.Code,
            AppType.Ai,
            AppType.WorkMessaging,
            AppType.PersonalMessaging,
            AppType.Document
        ];

        var result = new List<PreparedEntry>(order.Length);
        foreach (var type in order)
        {
            if (!catalog.Types.TryGetValue(ToCatalogKey(type), out var entry))
                continue;

            var processNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in entry.WindowsProcesses)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    processNames[name] = name;
            }

            var keywords = new List<PreparedKeyword>(entry.TitleKeywords.Length);
            foreach (var raw in entry.TitleKeywords)
            {
                var normalized = raw?.Trim().ToLowerInvariant() ?? string.Empty;
                if (normalized.Length == 0)
                    continue;

                var isSubstring = normalized.Contains('.') || normalized.Contains('/') || normalized.Contains(' ');
                Regex? boundaryRegex = isSubstring
                    ? null
                    : new Regex(
                        $@"(?<![A-Za-z0-9_]){Regex.Escape(normalized)}(?![A-Za-z0-9_])",
                        RegexOptions.Compiled | RegexOptions.CultureInvariant);
                keywords.Add(new PreparedKeyword(normalized, isSubstring, boundaryRegex));
            }

            result.Add(new PreparedEntry(type, entry.Hosts, processNames, keywords.ToArray()));
        }

        return result.ToArray();
    }

    private sealed record PreparedKeyword(string Value, bool IsSubstring, Regex? WordBoundaryRegex);

    private sealed record PreparedEntry(
        AppType Type,
        string[] Hosts,
        Dictionary<string, string> ProcessNames,
        PreparedKeyword[] TitleKeywords);

    private static string ToCatalogKey(AppType type) => type switch
    {
        AppType.Ai => "ai",
        AppType.WorkMessaging => "workMessaging",
        AppType.PersonalMessaging => "personalMessaging",
        _ => type.ToString().ToLowerInvariant()
    };

    private static AppTypeCatalog LoadCatalog()
    {
        const string resourceName = "HyperWhisper.SharedAppClassification.app-type-catalog.json";
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return new AppTypeCatalog();

        return JsonSerializer.Deserialize<AppTypeCatalog>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppTypeCatalog();
    }

    private sealed class AppTypeCatalog
    {
        public Dictionary<string, AppTypeCatalogEntry> Types { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AppTypeCatalogEntry
    {
        public string[] MacBundleIds { get; init; } = [];
        public string[] WindowsProcesses { get; init; } = [];
        public string[] Hosts { get; init; } = [];
        public string[] TitleKeywords { get; init; } = [];
    }
}
