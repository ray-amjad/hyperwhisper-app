namespace HyperWhisper.Models;

/// <summary>
/// Cloud accuracy tier for HyperWhisper Cloud transcription.
/// Controls which STT provider is used on the backend.
/// Capability metadata (credits/min, custom-vocab support) is sourced from
/// <c>shared-app-classification/cloud-stt-catalog.json</c> via
/// <c>Services.AppClassification.CloudSttCatalog</c>.
/// </summary>
public enum CloudAccuracyTier
{
    /// <summary>Groq Whisper — Medium tier</summary>
    GroqWhisper,

    /// <summary>Deepgram Nova-3 — Medium tier</summary>
    DeepgramNova3,

    /// <summary>xAI Grok STT — High tier (no custom vocabulary plumbed through backend)</summary>
    GrokStt,

    /// <summary>Microsoft MAI-Transcribe 1.5 — High tier (HW Cloud only)</summary>
    AzureMaiTranscribe,

    /// <summary>Google Chirp 3 — High tier (HW Cloud only, no custom vocabulary)</summary>
    GoogleChirp3,

    /// <summary>ElevenLabs Scribe v2 — Highest tier</summary>
    ElevenLabsScribeV2,

    /// <summary>OpenAI Whisper / GPT-4o Transcribe</summary>
    OpenaiWhisper,

    /// <summary>Google Gemini (multimodal LLM transcription)</summary>
    Gemini,

    /// <summary>Mistral Voxtral</summary>
    MistralVoxtral,

    /// <summary>AssemblyAI Universal</summary>
    AssemblyAI,

    /// <summary>Soniox async</summary>
    Soniox
}

/// <summary>
/// Extension methods for CloudAccuracyTier.
/// </summary>
public static class CloudAccuracyTierExtensions
{
    /// <summary>
    /// Converts the accuracy tier to the corresponding X-STT-Provider header value.
    /// Prefers the catalog's <c>sttProvider</c> field (single source of truth) and
    /// falls back to an exhaustive switch so a missing/unparsed catalog entry can
    /// never produce a wrong header. Every enum value MUST have an arm here.
    /// </summary>
    public static string ToSttProvider(this CloudAccuracyTier tier)
    {
        var fromCatalog = Services.AppClassification.CloudSttCatalog.Shared
            .SttProviderForId(tier.ToStorageValue());
        if (!string.IsNullOrEmpty(fromCatalog))
            return fromCatalog;

        return tier switch
        {
            CloudAccuracyTier.GroqWhisper => "groq",
            CloudAccuracyTier.DeepgramNova3 => "deepgram",
            CloudAccuracyTier.ElevenLabsScribeV2 => "elevenlabs",
            CloudAccuracyTier.GrokStt => "grok",
            CloudAccuracyTier.AzureMaiTranscribe => "azure-mai",
            CloudAccuracyTier.GoogleChirp3 => "google-chirp",
            CloudAccuracyTier.OpenaiWhisper => "openai",
            CloudAccuracyTier.Gemini => "gemini",
            CloudAccuracyTier.MistralVoxtral => "mistral",
            CloudAccuracyTier.AssemblyAI => "assemblyai",
            CloudAccuracyTier.Soniox => "soniox",
            _ => "deepgram"
        };
    }

    /// <summary>
    /// Returns the string value stored in the database (matches the macOS
    /// <c>CloudAccuracyTier</c> raw values + the catalog <c>id</c> field).
    /// </summary>
    public static string ToStorageValue(this CloudAccuracyTier tier) => tier switch
    {
        CloudAccuracyTier.GroqWhisper => "groqWhisper",
        CloudAccuracyTier.DeepgramNova3 => "deepgramNova3",
        CloudAccuracyTier.ElevenLabsScribeV2 => "elevenLabsScribeV2",
        CloudAccuracyTier.GrokStt => "grokStt",
        CloudAccuracyTier.AzureMaiTranscribe => "azureMaiTranscribe",
        CloudAccuracyTier.GoogleChirp3 => "googleChirp3",
        CloudAccuracyTier.OpenaiWhisper => "openaiWhisper",
        CloudAccuracyTier.Gemini => "gemini",
        CloudAccuracyTier.MistralVoxtral => "mistralVoxtral",
        CloudAccuracyTier.AssemblyAI => "assemblyAI",
        CloudAccuracyTier.Soniox => "soniox",
        _ => "deepgramNova3"
    };

    /// <summary>
    /// Parses a string to CloudAccuracyTier, defaulting to Deepgram Nova-3 if invalid.
    /// Accepts both the canonical storage value and legacy provider names that previously
    /// lived in the standalone BYOK list (now folded into accuracy tiers).
    /// </summary>
    public static CloudAccuracyTier FromString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return CloudAccuracyTier.DeepgramNova3;

        var normalized = value.Trim();

        // Canonical storage value match first (case-insensitive).
        foreach (CloudAccuracyTier tier in Enum.GetValues<CloudAccuracyTier>())
        {
            if (string.Equals(tier.ToStorageValue(), normalized, StringComparison.OrdinalIgnoreCase))
                return tier;
        }

        // Catalog-driven legacy alias migration. Keeps the rename rules in
        // shared-app-classification/cloud-stt-catalog.json rather than scattered
        // hardcoded switch arms on each platform.
        var migrated = Services.AppClassification.CloudSttCatalog.Shared.GetByMigrateFromAlias(normalized);
        if (migrated != null)
        {
            foreach (CloudAccuracyTier tier in Enum.GetValues<CloudAccuracyTier>())
            {
                if (string.Equals(tier.ToStorageValue(), migrated.Id, StringComparison.OrdinalIgnoreCase))
                    return tier;
            }
        }

        return CloudAccuracyTier.DeepgramNova3;
    }
}
