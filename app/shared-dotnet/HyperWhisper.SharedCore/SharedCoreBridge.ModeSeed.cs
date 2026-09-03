using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// The canonical first-run mode seed (#285), as the .NET heads see it.
///
/// A public mirror of the binding's <c>internal</c> <c>ModeSeed</c> record, so
/// the heads that seed a database (<c>HyperWhisper.Application</c>, and Windows
/// through it) do not need <c>InternalsVisibleTo</c> on the raw binding. Same
/// shape, same field order, same guarantees — the record is deliberately
/// exhaustive and has no nullable field, because a field a head stops writing
/// inherits a hostile store default rather than an empty one.
///
/// <c>ProviderType</c> is the <c>"cloud"</c> token. On the .NET heads it goes
/// to <c>Mode.ProviderType</c> and NOT to <c>Mode.Model</c>; macOS has no
/// <c>providerType</c> column and writes the same token to <c>mode.model</c>.
/// The two columns are not interchangeable.
/// </summary>
public sealed record PortableModeSeed(
    string Id,
    string Name,
    string Preset,
    string Language,
    string ProviderType,
    string CloudProvider,
    string CloudAccuracyTier,
    string CloudTranscriptionModel,
    int PostProcessingMode,
    string PostProcessingProvider,
    string CloudPostProcessingModel,
    string EnglishSpelling,
    bool Punctuation,
    bool Capitalization,
    bool ProfanityFilter,
    string CustomInstructions,
    bool IsDefault,
    bool IsSystemProvided,
    int SortOrder);

public static partial class SharedCoreBridge
{
    /// <summary>
    /// The ONE mode a brand-new install seeds, for an ISO 3166-1 alpha-2 region
    /// code. Trimming, case folding, the region table and both catalog lookups
    /// are the core's; a null, empty or unknown code seeds American spelling.
    ///
    /// Returns a single seed, never a list: "exactly one mode on a fresh
    /// install" is then not a count a head can disagree about. Never throws —
    /// the core falls back to pinned literals rather than failing, because this
    /// is the first-launch path. That is deliberately stronger than the
    /// <c>InvalidDataException</c> the portable seeder used to raise from its
    /// own <c>JsonDocument</c> catalog parse, since <c>hw-catalog</c>'s
    /// <c>catalog_resolution_matches_the_fallback_literals</c> test pins the
    /// embedded catalog to those literals before the binary ships.
    ///
    /// Seeding only runs when the store holds no modes. That guard lives in
    /// each head's initializer and does not move.
    /// </summary>
    public static PortableModeSeed ModeSeedDefault(string? regionCode)
    {
        var seed = HyperwhisperCoreMethods.ModeSeedDefault(regionCode);
        return new PortableModeSeed(
            seed.id,
            seed.name,
            seed.preset,
            seed.language,
            seed.providerType,
            seed.cloudProvider,
            seed.cloudAccuracyTier,
            seed.cloudTranscriptionModel,
            seed.postProcessingMode,
            seed.postProcessingProvider,
            seed.cloudPostProcessingModel,
            seed.englishSpelling,
            seed.punctuation,
            seed.capitalization,
            seed.profanityFilter,
            seed.customInstructions,
            seed.isDefault,
            seed.isSystemProvided,
            seed.sortOrder);
    }
}
