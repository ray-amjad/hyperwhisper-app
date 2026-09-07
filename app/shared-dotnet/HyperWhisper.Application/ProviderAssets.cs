namespace HyperWhisper.PortableApplication;

/// <summary>
/// Maps a catalog provider id to the bare file name of its logo under Assets/Providers.
///
/// The PNGs themselves live at app/windows/HyperWhisper/Assets/Providers and are referenced from
/// the Linux csproj as linked AvaloniaResource items, so there is exactly ONE copy of each image
/// in the repository. The mapping is here rather than in either head because the two would
/// otherwise drift: Windows already keys the same table off its CloudTranscriptionProvider and
/// PostProcessingProvider enums (CloudTranscriptionProvider.cs:125-153 and
/// ModelLibraryManager.cs:433-444), and the portable catalog has only the id string.
///
/// "providerMeta" is a deliberate SENTINEL with no PNG behind it, matching Windows: a row whose
/// asset is not in <see cref="ShippedNames"/> draws a letter monogram instead. Never build an
/// image path from a name without checking <see cref="Exists"/> first.
/// </summary>
public static class ProviderAssets
{
    /// <summary>The bare names that actually have a PNG. Kept in step with the Assets folder.</summary>
    public static readonly IReadOnlySet<string> ShippedNames = new HashSet<string>(StringComparer.Ordinal)
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

    // OrdinalIgnoreCase because the catalog is inconsistent about case across id spaces:
    // "geminiTranscribe" in one place, "microsoftazurespeech" in another.
    private static readonly Dictionary<string, string> ByProviderId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = "providerOpenAI",
        ["anthropic"] = "providerAnthropic",
        ["groq"] = "providerGroq",
        ["deepgram"] = "providerDeepgram",
        ["assemblyai"] = "providerAssemblyAI",
        ["elevenlabs"] = "providerElevenLabs",
        ["mistral"] = "providerMistral",
        ["soniox"] = "providerSoniox",
        ["cerebras"] = "providerCerebras",
        // One vendor, one mark: Gemini 3.5 Transcribe is the same company as Gemini and
        // deliberately reuses the logo rather than shipping a duplicate PNG.
        ["gemini"] = "providerGemini",
        ["google"] = "providerGemini",
        ["geminitranscribe"] = "providerGemini",
        // UnifiedModelCatalog takes a cloud row's provider id from the catalog's sttProvider
        // field, which spells these two with a hyphen. Without the hyphenated aliases both rows
        // fell through to the local-Whisper mark.
        ["gemini-transcribe"] = "providerGemini",
        ["azure-mai"] = "providerMicrosoft",
        ["googlespeech"] = "providerGoogle",
        // xAI's id is spelled three ways across the catalog and the mode editor.
        ["grok"] = "providerGrok",
        ["xai"] = "providerGrok",
        ["azure"] = "providerMicrosoft",
        ["microsoftazurespeech"] = "providerMicrosoft",
        ["hyperwhisper"] = "providerLocalWhisper",
        // The three ids UnifiedModelCatalog mints for on-device models, plus the two extra
        // provider ids the streaming duplicates get.
        ["localwhisper"] = "providerLocalWhisper",
        ["parakeet"] = "providerParakeet",
        ["parakeetlocal"] = "providerParakeet",
        ["nemotronlocal"] = "providerParakeet",
        ["localllm"] = "providerLocalLLM",
        // No PNG ships for Meta. Resolves to the sentinel so the row draws a monogram.
        ["meta"] = "providerMeta",
    };

    /// <summary>
    /// The logo name for a provider id. Falls back to the local-Whisper mark, as Windows does,
    /// so an id added to the catalog before its logo still renders something sane.
    /// </summary>
    public static string AssetNameFor(string? providerId)
        => providerId is not null && ByProviderId.TryGetValue(providerId, out var name)
            ? name
            : "providerLocalWhisper";

    /// <summary>Whether a bare asset name has a PNG behind it.</summary>
    public static bool Exists(string? assetName)
        => !string.IsNullOrEmpty(assetName) && ShippedNames.Contains(assetName);
}
