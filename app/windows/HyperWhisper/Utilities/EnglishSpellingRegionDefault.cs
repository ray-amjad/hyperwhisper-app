using System.Globalization;
using uniffi.hyperwhisper_core;

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
/// The ISO 3166-1 region table itself lives in the shared Rust core
/// (<c>hw-text</c>, <c>EnglishSpelling::for_region</c>). macOS, Windows and the
/// portable .NET head all read that one table, so there is no longer a copy to
/// keep in sync — only the platform's own way of asking the OS for its region.
/// </summary>
public static class EnglishSpellingRegionDefault
{
    public const string American = "american";
    public const string British = "british";
    public const string Australian = "australian";
    public const string Canadian = "canadian";

    /// <summary>
    /// The spelling variant to seed into a new mode, from the system region.
    /// </summary>
    public static string ForCurrentRegion() => ForRegion(CurrentRegionCode());

    /// <summary>
    /// Maps an ISO 3166-1 alpha-2 region code to a spelling variant. Trimming,
    /// case folding and the region table are all the core's. An unknown, empty
    /// or null code gives <see cref="American"/>, the value the app used for
    /// every mode before this table existed.
    ///
    /// Note this is a SEEDING call and is not the inverse of the prompt path's
    /// <c>EnglishSpellingFromRaw</c>: an empty stored <c>EnglishSpelling</c>
    /// means "the user never chose" and suppresses the spelling instruction
    /// entirely, which is never the right value to seed. The core never returns
    /// <c>HwEnglishSpelling.None</c> here, so no fallback is needed.
    /// </summary>
    public static string ForRegion(string? regionCode) =>
        HyperwhisperCoreMethods.EnglishSpellingRawValue(
            HyperwhisperCoreMethods.EnglishSpellingForRegion(regionCode));

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
