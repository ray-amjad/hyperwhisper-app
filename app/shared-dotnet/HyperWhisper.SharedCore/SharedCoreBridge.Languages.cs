using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// One language the pickers can offer, as the shared catalog knows it (issue
/// #285). Mirrors the core's <c>HwLanguage</c>.
///
/// <para><c>Code</c> is always the canonical BCP-47 tag — <c>en-GB</c>, not
/// <c>en_gb</c> — so it is safe to persist and to compare against another
/// canonical code.</para>
///
/// <para>A null <c>DisplayName</c> means the catalog does not know the code,
/// and the host must localize it with its own system database. That split is
/// deliberate rather than a gap: the catalog carries English names for the
/// codes the app itself offers, and everything else — a code a provider
/// advertised that we have never listed — is exactly the case the platform
/// frameworks answer better than a table would. It is where
/// <c>Locale.localizedString(forIdentifier:)</c> lives on macOS, and
/// <c>CultureInfo</c> on the .NET heads. Fall back to <c>Code</c> when there is
/// no system name either; a raw tag reads better than an empty row.</para>
/// </summary>
public sealed record PortableLanguage(string Code, string? DisplayName);

public static partial class SharedCoreBridge
{
    /// <summary>
    /// Canonicalize a BCP-47 tag (issue #285): <c>en_gb</c> becomes
    /// <c>en-GB</c>, <c>ZH-HANT</c> becomes <c>zh-Hant</c>, surrounding
    /// whitespace is trimmed, and an empty or whitespace-only tag becomes
    /// <c>auto</c>.
    ///
    /// <para>The one spelling rule for every code the app stores or compares.
    /// Canonicalizing before a lookup is what makes a stored <c>en_GB</c> —
    /// which the Windows picker used to match against nothing and render as the
    /// raw tag — resolve to a real row.</para>
    ///
    /// <para>Note that a five-character subtag lowercases, which is why the
    /// LatAm Spanish key is <c>es-latam</c> and not <c>es-LATAM</c>.</para>
    /// </summary>
    public static string CanonicalizeLanguageCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return HyperwhisperCoreMethods.LanguageCanonicalize(code);
    }

    /// <summary>
    /// The canonical tag to persist. Differs from
    /// <see cref="CanonicalizeLanguageCode"/> in exactly one place: a null,
    /// empty or whitespace-only code becomes <c>en</c> rather than <c>auto</c>,
    /// because a mode with no stored language transcribes as English.
    /// </summary>
    public static string CanonicalLanguageCode(string? code) =>
        HyperwhisperCoreMethods.LanguageCanonicalCode(code);

    /// <summary>
    /// The 2-letter ISO 639 code, for the APIs that refuse anything longer.
    /// <c>auto</c> survives as itself; a null code becomes <c>en</c>; a code
    /// that is not two letters to begin with (<c>eng</c>, <c>yue</c>) is handed
    /// back unchanged rather than truncated.
    /// </summary>
    public static string NormalizeLanguageCode(string? code) =>
        HyperwhisperCoreMethods.LanguageNormalize(code);

    /// <summary>
    /// Whether a code means English, region and script variants included
    /// (<c>en-GB</c>, <c>en_us</c>). A null or absent code counts as English,
    /// matching <see cref="CanonicalLanguageCode"/>'s default; an empty string
    /// does not, because an empty stored value is an explicit "automatic".
    /// </summary>
    public static bool IsEnglishLanguage(string? code) =>
        HyperwhisperCoreMethods.LanguageIsEnglish(code);

    /// <summary>
    /// Look one code up, canonicalizing it first. Null means the catalog does
    /// not know it — use <see cref="CanonicalizeLanguageCode"/> for the tag and
    /// localize the name natively. See <see cref="PortableLanguage"/> for why
    /// that half stays on the host.
    ///
    /// <para>Named for the core function rather than for the .NET type of the
    /// same name in the Windows head (<c>HyperWhisper.Models.LanguageInfo</c>).
    /// That type is in a different assembly and a different namespace, so there
    /// is no ambiguity — and it is now a facade over this call, so the shared
    /// name is the accurate one.</para>
    /// </summary>
    public static PortableLanguage? LanguageInfo(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var native = HyperwhisperCoreMethods.LanguageInfo(code);
        return native is null ? null : ToPortableLanguage(native);
    }

    /// <summary>
    /// Every language the pickers offer, in picker order: <c>auto</c> first,
    /// then the popular codes in <see cref="PopularLanguageCodes"/> order, then
    /// the rest alphabetically by display name.
    ///
    /// <para>One FFI call returns the whole list, so bind it once into a static
    /// rather than calling it per row.</para>
    /// </summary>
    public static IReadOnlyList<PortableLanguage> AllLanguages() =>
        // A UniFFI sequence return is never null, but the generated signature
        // does not say so; `?? []` keeps a picker binding off a null reference.
        HyperwhisperCoreMethods.LanguageAll()?.Select(ToPortableLanguage).ToList() ?? [];

    /// <summary>
    /// The codes the pickers float to the top, in the order they appear there.
    /// Does not include <c>auto</c>, which sorts above them all.
    /// </summary>
    public static IReadOnlyList<string> PopularLanguageCodes() =>
        // As with AllLanguages: never null in practice, defended anyway.
        HyperwhisperCoreMethods.LanguagePopularCodes() ?? [];

    /// <summary>
    /// Canonical rows for a provider's advertised code list, deduplicated, in
    /// the order given. An unknown code keeps its canonical form and comes back
    /// with a null <see cref="PortableLanguage.DisplayName"/> — it is still a
    /// row, so a provider that advertises something we have never listed shows
    /// up in the picker instead of vanishing from it.
    /// </summary>
    public static IReadOnlyList<PortableLanguage> ResolveLanguages(IEnumerable<string>? codes) =>
        HyperwhisperCoreMethods.LanguageResolve(
            // A null code cannot cross the FFI, and neither can a null list.
            codes?.Where(code => code is not null).ToList() ?? [])
            ?.Select(ToPortableLanguage).ToList() ?? [];

    /// <summary>
    /// Move <c>auto</c> to the front of a list if it is present and not already
    /// there, leaving every other row in its given order. What a
    /// provider-filtered picker calls after <see cref="ResolveLanguages"/>, so
    /// "Automatic" stays the first entry however the provider ordered its list.
    /// </summary>
    public static IReadOnlyList<PortableLanguage> PrioritizeAutomaticLanguage(
        IEnumerable<PortableLanguage>? languages) =>
        HyperwhisperCoreMethods.LanguagePrioritizeAutomatic(
            languages?
                // A null row cannot cross the FFI.
                .Where(language => language is not null)
                .Select(language => new HwLanguage(language.Code, language.DisplayName))
                .ToList() ?? [])
            ?.Select(ToPortableLanguage).ToList() ?? [];

    private static PortableLanguage ToPortableLanguage(HwLanguage language) =>
        new(language.code, language.displayName);
}
