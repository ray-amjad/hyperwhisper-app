using System.Globalization;

namespace HyperWhisper.Utilities;

/// <summary>
/// ENGLISH SPELLING REGION DEFAULT
///
/// Picks the English spelling variant to SEED into a mode the user has not
/// touched yet: the default modes written at first install, and every brand-new
/// mode created afterwards. A user in London had to change "American" to
/// "British" on every mode they made; deriving it from the system region once
/// removes that step.
///
/// This only supplies a STARTING value. An existing mode's stored
/// <c>EnglishSpelling</c> is never re-derived, so a user who already picked a
/// variant (or a mode restored from a backup) keeps it.
///
/// The macOS app mirrors this table in
/// <c>Utilities/EnglishSpellingRegionDefault.swift</c> — change both together.
/// </summary>
public static class EnglishSpellingRegionDefault
{
    public const string American = "american";
    public const string British = "british";
    public const string Australian = "australian";
    public const string Canadian = "canadian";

    /// <summary>Regions whose written English follows Canadian spelling.</summary>
    private static readonly HashSet<string> CanadianRegions = new(StringComparer.Ordinal)
    {
        "CA"
    };

    /// <summary>Regions whose written English follows Australian spelling.</summary>
    private static readonly HashSet<string> AustralianRegions = new(StringComparer.Ordinal)
    {
        "AU", "CC", "CX", "NF"
    };

    /// <summary>
    /// Regions whose written English follows British spelling. New Zealand,
    /// Ireland and South Africa sit here because the app offers no separate
    /// variant for them and British is the closest of the four.
    /// </summary>
    private static readonly HashSet<string> BritishRegions = new(StringComparer.Ordinal)
    {
        // British Isles and Europe
        "GB", "IE", "IM", "JE", "GG", "GI", "MT", "CY",
        // Africa
        "ZA", "NG", "GH", "KE", "UG", "TZ", "RW", "ZM", "ZW", "BW", "NA",
        "MW", "MU", "SC", "SZ", "LS", "GM", "SL", "SS",
        // South and South-East Asia
        "IN", "PK", "BD", "LK", "NP", "BT", "MV", "SG", "MY", "BN", "HK",
        // Caribbean and South Atlantic
        "JM", "TT", "BB", "BS", "BZ", "GY", "AG", "DM", "GD", "KN", "LC",
        "VC", "VG", "KY", "TC", "MS", "AI", "BM", "FK", "SH",
        // Oceania
        "NZ", "FJ", "PG", "SB", "VU", "WS", "TO", "KI", "TV", "NR", "CK",
        "NU", "TK"
    };

    /// <summary>
    /// The spelling variant to seed into a new mode, from the system region.
    /// </summary>
    public static string ForCurrentRegion() => ForRegion(CurrentRegionCode());

    /// <summary>
    /// Maps an ISO 3166-1 alpha-2 region code to a spelling variant.
    /// An unknown, empty or null code gives <see cref="American"/>, the value
    /// the app used for every mode before this table existed.
    /// </summary>
    public static string ForRegion(string? regionCode)
    {
        var code = (regionCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            return American;
        }

        if (CanadianRegions.Contains(code)) return Canadian;
        if (AustralianRegions.Contains(code)) return Australian;
        if (BritishRegions.Contains(code)) return British;
        return American;
    }

    /// <summary>
    /// Reads the current Windows region. <see cref="RegionInfo"/> throws for the
    /// invariant culture and for any culture with no region (e.g. a bare "en"),
    /// so failures fall back to an empty code and therefore to American.
    /// </summary>
    private static string CurrentRegionCode()
    {
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
