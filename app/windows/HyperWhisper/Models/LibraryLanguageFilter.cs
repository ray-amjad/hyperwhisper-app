namespace HyperWhisper.Models;

/// <summary>
/// Canonical option set + matching logic for the Model Library language filter.
/// Mirror of macOS <c>LibraryLanguageFilter</c> (LibraryModel.swift).
///
/// The dropdown is one entry per *base* language (region/script collapsed) drawn
/// from <see cref="LanguageInfo.AllLanguages"/>, so matching is always
/// base-to-base: picking "Spanish" (es) keeps any model whose set contains es
/// (which subsumes es-419 etc. after normalization).
/// </summary>
public static class LibraryLanguageFilter
{
    /// <summary>Sentinel for "Any language" (no filtering).</summary>
    public const string AnyCode = "";

    /// <summary>
    /// Reduce a BCP-47 / locale code to its base ISO code: the part before the
    /// first '-' or '_', lowercased. "en-US" → "en", "zh-TW" → "zh", "es-419" →
    /// "es". "auto" is preserved. Mirrors macOS LanguageData.normalizeLanguageCode.
    /// </summary>
    public static string BaseNormalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "auto";
        var trimmed = code.Trim();
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        var normalized = trimmed.Replace('_', '-');
        var dash = normalized.IndexOf('-');
        var baseCode = dash >= 0 ? normalized[..dash] : normalized;
        return baseCode.ToLowerInvariant();
    }

    /// <summary>
    /// One dropdown entry per base language, popular-first. Built from
    /// <see cref="LanguageInfo.AllLanguages"/> with "Automatic" dropped and
    /// region/script variants collapsed onto their base (e.g. zh-TW → zh).
    /// </summary>
    public static readonly IReadOnlyList<LanguageInfo> Languages = BuildLanguages();

    private static IReadOnlyList<LanguageInfo> BuildLanguages()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<LanguageInfo>();
        foreach (var lang in LanguageInfo.AllLanguages)
        {
            var baseCode = BaseNormalize(lang.Code);
            if (baseCode == "auto") continue;
            if (!seen.Add(baseCode)) continue;
            // Keep the base code (not the region variant) as the option's code.
            list.Add(lang.Code == baseCode ? lang : new LanguageInfo(baseCode, lang.DisplayName));
        }
        return list;
    }

    /// <summary>
    /// Base-normalize a set of provider codes into the shape the filter compares
    /// against (region stripped, "auto" removed).
    /// </summary>
    public static HashSet<string> BaseCodes(IEnumerable<string> codes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in codes)
        {
            var b = BaseNormalize(c);
            if (b == "auto") continue;
            set.Add(b);
        }
        return set;
    }

    /// <summary>Display name for a base code, falling back to the code.</summary>
    public static string DisplayName(string baseCode)
    {
        foreach (var lang in Languages)
        {
            if (lang.Code == baseCode) return lang.DisplayName;
        }
        return baseCode;
    }
}
