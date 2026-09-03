using System.Globalization;
using HyperWhisper.Data.Entities;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.Persistence;

/// <summary>
/// THE .NET first-install seeder (#285). Creates the one cross-platform
/// first-install mode, from the shared core.
///
/// This used to build six modes ("Hyper", "Voice to Text", "Message", "Mail",
/// "Note", "Meeting") from a hand-rolled <c>JsonDocument</c> parse of the two
/// shared catalogs — the fourth reimplementation of catalog parsing in the
/// repo, and a third answer to "what does a fresh install look like" next to
/// macOS' one mode and Windows' own copy of the six. Now
/// <c>hw-catalog::mode_seed</c> is the only definition, this class only maps it
/// onto the EF entity, and Windows' <c>ModeDefaults</c> delegates here rather
/// than keeping a parallel list in sync.
///
/// Seeding runs ONLY when the store holds no modes — <c>ApplicationDb</c> for
/// Linux/portable, <c>DatabaseInitializer</c> for Windows. Existing users are
/// untouched by the drop from six modes to one; that guard is the contract and
/// does not move.
/// </summary>
public static class PortableModeDefaults
{
    /// <summary>
    /// The seeded mode's well-known id, identical on all three heads and
    /// anchored by onboarding restore-point lookups. Kept as a literal rather
    /// than read off the seed: this is a static field, and resolving it through
    /// the FFI would load the native library from a type initializer, where a
    /// failure surfaces as an unrelated <c>TypeInitializationException</c>. The
    /// mode-defaults suite asserts it equals <c>ModeSeedDefault(...).Id</c>.
    /// </summary>
    public static readonly Guid HyperModeId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>The seed for the current system region, stamped now.</summary>
    public static IReadOnlyList<Mode> CreateForCurrentRegion() =>
        CreateForRegion(CurrentRegionCode(), DateTime.UtcNow);

    /// <summary>
    /// The seed for an ISO 3166-1 alpha-2 region code. Returns exactly one
    /// mode; the list shape is kept because both call sites <c>AddRange</c> it.
    /// </summary>
    public static IReadOnlyList<Mode> CreateForRegion(string? regionCode, DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("The seed timestamp must be UTC.", nameof(utcNow));
        return [ToMode(SharedCoreBridge.ModeSeedDefault(regionCode), utcNow)];
    }

    /// <summary>
    /// Maps a shared seed onto the EF entity. Every field the core names is
    /// written explicitly, including the two the old seeders left alone
    /// (<c>ProfanityFilter</c>, <c>CustomInstructions</c>): a field the seeder
    /// skips inherits the entity's own default, which is a different value on a
    /// different head.
    ///
    /// <c>Mode.Model</c> is deliberately NOT set from <c>ProviderType</c>. They
    /// are different columns here — <c>Model</c> is the local engine's model id
    /// — and only macOS overloads its <c>model</c> attribute with the
    /// cloud/local routing token.
    /// </summary>
    private static Mode ToMode(PortableModeSeed seed, DateTime utcNow) => new()
    {
        Id = Guid.Parse(seed.Id),
        Name = seed.Name,
        Preset = seed.Preset,
        Language = seed.Language,
        ProviderType = seed.ProviderType,
        CloudProvider = seed.CloudProvider,
        CloudAccuracyTier = seed.CloudAccuracyTier,
        CloudTranscriptionModel = seed.CloudTranscriptionModel,
        PostProcessingMode = seed.PostProcessingMode,
        PostProcessingProvider = seed.PostProcessingProvider,
        CloudPostProcessingModel = seed.CloudPostProcessingModel,
        EnglishSpelling = seed.EnglishSpelling,
        Punctuation = seed.Punctuation,
        Capitalization = seed.Capitalization,
        ProfanityFilter = seed.ProfanityFilter,
        CustomInstructions = seed.CustomInstructions,
        IsDefault = seed.IsDefault,
        IsSystemProvided = seed.IsSystemProvided,
        SortOrder = seed.SortOrder,
        CreatedDate = utcNow,
        ModifiedDate = utcNow
    };

    /// <summary>
    /// The spelling token to seed into a brand-new mode, for an ISO 3166-1
    /// alpha-2 region code. The region table lives in the shared core, so macOS,
    /// Windows and this portable head all read one table. An unknown, empty or
    /// null code gives "american".
    /// </summary>
    public static string EnglishSpellingForRegion(string? regionCode) =>
        SharedCoreBridge.EnglishSpellingForRegion(regionCode);

    private static string CurrentRegionCode()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
