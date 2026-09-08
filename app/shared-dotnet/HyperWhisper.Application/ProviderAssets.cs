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

    /// <summary>
    /// The chip colour a logo is drawn on, as a "#RRGGBB" string.
    ///
    /// Each PNG is single-colour ink on transparency, and the ink differs per vendor: OpenAI,
    /// Groq and ElevenLabs are white, Nemotron and Parakeet are near-black. So no ONE background
    /// makes them all legible — a light tile hid every white mark and a dark tile hid every dark
    /// one. Windows never had the problem because it gives each logo its own brand chip
    /// (ApiKeysSettingsPage.xaml: OpenAI on #10A37F, Groq on #F55036, and so on). These are those
    /// colours, so both heads draw the same mark on the same chip.
    /// </summary>
    /// <returns>The brand colour, or a neutral dark chip for a name with no brand entry.</returns>
    public static string ChipColorFor(string? assetName)
        => assetName is not null && ChipColors.TryGetValue(assetName, out var color) ? color : NeutralChipColor;

    /// <summary>The chip a monogram is drawn on, and the fallback for an unbranded logo.</summary>
    public const string NeutralChipColor = "#1F2024";

    private static readonly Dictionary<string, string> ChipColors = new(StringComparer.Ordinal)
    {
        ["providerOpenAI"] = "#10A37F",
        ["providerGroq"] = "#F55036",
        ["providerDeepgram"] = "#13EF93",
        ["providerAssemblyAI"] = "#6B5BFF",
        ["providerElevenLabs"] = "#0F0F0F",
        ["providerMistral"] = "#FA500F",
        ["providerSoniox"] = "#2A6DF4",
        ["providerGemini"] = "#8E75B2",
        ["providerGrok"] = "#0F0F0F",
        ["providerAnthropic"] = "#D97757",
        ["providerCerebras"] = "#F15A27",
        ["providerMicrosoft"] = "#1877F2",
        ["providerGoogle"] = "#FFFFFF",
        // No vendor brand stands behind an on-device engine or the Apple mark, so these four take
        // the chip their own INK needs. Measured on the rendered rows: the Whisper mark is white
        // (srgb 242-255 across the tile), the other three are near-black.
        ["providerApple"] = "#F2F3F5",
        ["providerLocalWhisper"] = NeutralChipColor,
        ["providerParakeet"] = "#F2F3F5",
        ["providerLocalLLM"] = "#F2F3F5",
    };
}
