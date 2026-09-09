using HyperWhisper.Data.Entities;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.Data;

/// <summary>
/// MODE DEFAULTS
///
/// The Windows head's view of THE .NET first-install seeder. Every field, the
/// mode count and both catalog lookups live in
/// <see cref="PortableModeDefaults"/>, which reads them from the shared Rust
/// core (<c>hw-catalog::mode_seed</c>).
///
/// This used to be a second, hand-maintained copy of the same six modes. The
/// two .NET heads compile ONE <c>Mode</c> entity (HyperWhisper.csproj removes
/// <c>Data\Entities\**\*.cs</c> from this assembly and
/// HyperWhisper.Application link-compiles them), so there is nothing to
/// translate between the heads and no reason for a second seeder to exist.
/// Windows and Linux/portable now differ only in WHERE the emptiness guard
/// lives: DatabaseInitializer here, ApplicationDb there.
/// </summary>
public static class ModeDefaults
{
    /// <summary>
    /// The seeded mode's well-known id, shared with macOS. An alias of
    /// <see cref="PortableModeDefaults.HyperModeId"/> so the onboarding
    /// restore-point lookups that anchor on it keep reading one value.
    /// </summary>
    public static readonly Guid DefaultModeId = PortableModeDefaults.HyperModeId;

    /// <summary>
    /// The modes a brand-new install starts with: exactly one, from the shared
    /// core, carrying the system region's spelling variant.
    /// </summary>
    public static List<Mode> GetDefaultModes() => [.. PortableModeDefaults.CreateForCurrentRegion()];
}
