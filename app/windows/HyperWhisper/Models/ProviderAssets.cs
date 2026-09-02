// PROVIDER LOGOS
//
// One place that knows which provider logos actually exist, and the only
// supported way to turn a provider into an image path.
//
// This file exists because the alternative already shipped a bug. Two screens
// built "/Assets/Providers/{name}.png" by concatenation; "providerMeta" is a
// SENTINEL with no PNG behind it, and one of the two guarded it while the other
// did not. An earlier review ruled the unguarded one safe BECAUSE Meta was not on
// the onboarding chip strip - and then Meta was added to that strip, which turned
// a dead asset path live with nothing failing anywhere. WPF's ImageSourceConverter
// throws inside the binding engine, the binding engine swallows it, and the chip
// draws a blank 14x14 gap.
//
// So the name is checked against the set of files that really ship, and the
// caller gets a bool it has to look at. verify_onboarding.ps1 asserts that the set
// below is exactly the PNGs in Assets/Providers, so adding a vendor without its
// logo, or a logo without its entry, fails the gate rather than the render.

namespace HyperWhisper.Models;

public static class ProviderAssets
{
    /// <summary>
    /// The bare names, without extension, of every PNG under Assets/Providers.
    ///
    /// Hand-written on purpose. A directory scan at runtime would read the build
    /// output rather than the resource manifest, and a resource-manifest scan
    /// needs WPF loaded, which the smoke suite deliberately does not have. A
    /// literal set is testable from anywhere, and the verify script pins it to the
    /// real directory so it cannot drift.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ShippedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "providerAnthropic",
        "providerApple",
        "providerAssemblyAI",
        "providerCerebras",
        "providerDeepgram",
        "providerElevenLabs",
        "providerGemini",
        "providerGoogle",
        "providerGrok",
        "providerGroq",
        "providerLocalLLM",
        "providerLocalWhisper",
        "providerMicrosoft",
        "providerMistral",
        "providerOpenAI",
        "providerParakeet",
        "providerSoniox",
    };

    /// <summary>Whether a bare asset name has a PNG behind it.</summary>
    public static bool Exists(string? assetName) =>
        !string.IsNullOrEmpty(assetName) && ShippedNames.Contains(assetName);

    /// <summary>
    /// The relative image path for a bare asset name, or null when no such PNG
    /// ships. Callers render a fallback for null; they must never bind the null.
    /// </summary>
    public static string? PathFor(string? assetName) =>
        Exists(assetName) ? $"/Assets/Providers/{assetName}.png" : null;
}
