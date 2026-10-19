using HyperWhisper.Data.Entities;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.Persistence;

/// <summary>
/// Folds a mode stored with a retired standalone cloud provider (<c>googlespeech</c>,
/// <c>microsoftazurespeech</c>) onto HyperWhisper Cloud (issue #601).
/// </summary>
/// <remarks>
/// <para>
/// On this head such a row cannot transcribe at all: the router maps it to
/// <c>GoogleChirp</c>/<c>AzureMai</c>, whose request carries no base URL, and the shared core
/// refuses it before any I/O. Windows (<c>ModeService.NormalizeLegacyCloudModeValues</c>) and
/// macOS (<c>PersistenceController.normalizeCloudProviderIfNeeded</c>) fold the same rows at
/// startup; this is the portable head's copy of that pass, run from
/// <see cref="ApplicationDb.InitializeAsync"/>.
/// </para>
/// <para>
/// The provider decision is the shared core's (<see cref="SharedCoreBridge.NormalizeCloudProvider"/>).
/// The tier rule is the native heads': the tier the alias stood for replaces
/// <c>CloudAccuracyTier</c> only when that is blank or a default tier: the old cross-head default
/// <c>deepgramNova3</c>, or this head's own new-mode default <c>elevenLabsScribeV2</c> (the tier
/// the Mode editor pre-fills, and the one #601 measured on a stored <c>googleSpeech</c> row). A
/// legacy row never dispatched by its tier, so a default there is not a choice; a user who
/// paired a legacy provider with any other tier keeps it.
/// </para>
/// <para>
/// Only a legacy ALIAS is rewritten. The shared normalizer also lower-cases every other
/// provider id; Windows stores that too, but this head's parsers are already case-insensitive,
/// so rewriting those rows here would be a write with no behavioural change.
/// </para>
/// <para>
/// Like <see cref="DefaultModePolicy"/>, it does not touch a database: the caller mutates the
/// tracked entities and saves inside its own transaction.
/// </para>
/// </remarks>
public static class LegacyCloudProviderFold
{
    /// <summary>The default tier both native heads treat as "not customised".</summary>
    public const string LegacyDefaultTier = "deepgramNova3";

    /// <summary>This head's new-mode default tier (<c>ModesViewModel</c>), also "not customised".</summary>
    public const string PortableDefaultTier = "elevenLabsScribeV2";

    /// <summary>
    /// The fold for one provider/tier pair. Returns <c>false</c>, and the inputs unchanged,
    /// when <paramref name="cloudProvider"/> is not a legacy alias.
    /// </summary>
    public static bool TryFold(string? cloudProvider, string? cloudAccuracyTier,
        out string foldedProvider, out string? foldedTier)
    {
        foldedProvider = cloudProvider ?? string.Empty;
        foldedTier = cloudAccuracyTier;
        if (string.IsNullOrWhiteSpace(cloudProvider)) return false;
        var (provider, aliasTier) = SharedCoreBridge.NormalizeCloudProvider(cloudProvider);
        if (string.IsNullOrEmpty(aliasTier) || string.IsNullOrEmpty(provider)) return false;
        foldedProvider = provider;
        if (IsBlankOrDefaultTier(cloudAccuracyTier)) foldedTier = aliasTier;
        return true;
    }

    /// <summary>
    /// Fold every legacy row of <paramref name="modes"/> in place.
    /// </summary>
    /// <returns>Whether any row changed, so the caller can skip its save.</returns>
    public static bool Apply(IEnumerable<Mode> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var changed = false;
        foreach (var mode in modes)
        {
            if (!TryFold(mode.CloudProvider, mode.CloudAccuracyTier, out var provider, out var tier)) continue;
            mode.CloudProvider = provider;
            if (!string.IsNullOrEmpty(tier)) mode.CloudAccuracyTier = tier;
            changed = true;
        }
        return changed;
    }

    // Windows compares the tier after its own canonicalisation; macOS compares it raw. Reading
    // it through the shared canonicaliser is the Windows rule, and it agrees with macOS for
    // every value macOS could have written.
    private static bool IsBlankOrDefaultTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return true;
        var canonical = SharedCoreBridge.CanonicalCloudSttTier(tier);
        return string.Equals(canonical, LegacyDefaultTier, StringComparison.Ordinal)
            || string.Equals(canonical, PortableDefaultTier, StringComparison.Ordinal);
    }
}
