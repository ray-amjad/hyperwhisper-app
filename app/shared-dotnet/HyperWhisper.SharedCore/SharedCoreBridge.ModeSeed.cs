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
    /// is the first-launch path and the release profile aborts on panic.
    ///
    /// That is NOT stronger than the <c>InvalidDataException</c> the portable
    /// seeder used to raise from its own <c>JsonDocument</c> catalog parse, and
    /// an earlier version of this comment claiming so was wrong: at runtime
    /// there is no way to refuse to seed, so a mode is always written. What
    /// replaces the throw is a pair of build-time gates in <c>hw-catalog</c> —
    /// <c>catalog_resolution_matches_the_fallback_literals</c> pins the embedded
    /// catalog to the fallback literals, and
    /// <c>the_seeded_engine_is_one_the_picker_shows</c> fails the build if the
    /// seeded post-processing engine is gated <c>"enabled": false</c>. Both run
    /// before the binary ships.
    ///
    /// Seeding only runs when the store holds no modes. That guard lives in
    /// each head's initializer and does not move.
    /// </summary>
    public static PortableModeSeed ModeSeedDefault(string? regionCode)
    {
        var seed = HyperwhisperCoreMethods.ModeSeedDefault(regionCode);

        // ⚠️ Positional deconstruction, on purpose. Do not replace it with
        // `seed.field` reads.
        //
        // The binding's `ModeSeed` is a positional record, so this uses its
        // compiler-generated `Deconstruct`, whose arity is the record's field
        // count. Add a field to the Rust record and this line stops compiling
        // ("no suitable Deconstruct"), which is the point: reading
        // `seed.id, seed.name, …` ignores any field it does not mention, so a
        // new field would simply never reach `PortableModeSeed`, no head would
        // write it, and nothing on any platform would say so.
        //
        // `PortableModeSeed` is positional too, so once the field is added
        // there, every construction of it fails to compile until it is carried.
        // The Rust and Swift ends of the same chain are the exhaustive
        // destructures in `hw-core`'s `From<hw_catalog::ModeSeed>` and
        // `mode_seed_vectors.rs`, and
        // `ModeSeedConformanceVectorTests.theSeedRecordIsConstructedFieldByField`.
        var (
            id,
            name,
            preset,
            language,
            providerType,
            cloudProvider,
            cloudAccuracyTier,
            cloudTranscriptionModel,
            postProcessingMode,
            postProcessingProvider,
            cloudPostProcessingModel,
            englishSpelling,
            punctuation,
            capitalization,
            profanityFilter,
            customInstructions,
            isDefault,
            isSystemProvided,
            sortOrder) = seed;

        return new PortableModeSeed(
            id,
            name,
            preset,
            language,
            providerType,
            cloudProvider,
            cloudAccuracyTier,
            cloudTranscriptionModel,
            postProcessingMode,
            postProcessingProvider,
            cloudPostProcessingModel,
            englishSpelling,
            punctuation,
            capitalization,
            profanityFilter,
            customInstructions,
            isDefault,
            isSystemProvided,
            sortOrder);
    }
}
