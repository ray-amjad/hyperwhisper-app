// CLOUD TRANSCRIPTION PROVIDER ENUM
// Defines the available cloud transcription providers.
// This mirrors the macOS CloudProvider enum for cross-platform consistency.
//
// PROVIDERS:
// 1. OpenAI - Whisper API (whisper-1, gpt-4o-transcribe, gpt-4o-mini-transcribe)
// 2. Groq - Fast Whisper inference (whisper-large-v3-turbo, whisper-large-v3)
// 3. Deepgram - Nova models (nova-3, nova-2, enhanced, base, whisper)
// 4. AssemblyAI - Universal and SLAM-1 models
// 5. ElevenLabs - Scribe speech-to-text
// 6. Mistral - Voxtral audio transcription
// 7. Soniox - Async/file speech-to-text
// 8. HyperWhisperCloud - Built-in cloud service (no API key required)
// 9. Gemini - Google Gemini multimodal transcription

using HyperWhisper.Localization;

namespace HyperWhisper.Models;

/// <summary>
/// Cloud transcription providers available in the app.
/// </summary>
public enum CloudTranscriptionProvider
{
    /// <summary>No cloud provider selected (use local transcription).</summary>
    None = 0,

    /// <summary>OpenAI Whisper API (whisper-1, gpt-4o-transcribe, gpt-4o-mini-transcribe).</summary>
    OpenAI = 1,

    /// <summary>Groq Whisper API (whisper-large-v3-turbo, whisper-large-v3).</summary>
    Groq = 2,

    // Value 3 was Fireworks AI (removed). The numeric value is intentionally
    // left as a gap so persisted modes/backups don't shift to another provider.

    /// <summary>Deepgram Nova models (nova-3, nova-2, enhanced, base, whisper).</summary>
    Deepgram = 4,

    /// <summary>AssemblyAI (Universal, SLAM-1 models).</summary>
    AssemblyAI = 5,

    /// <summary>ElevenLabs Scribe speech-to-text.</summary>
    ElevenLabs = 6,

    /// <summary>Mistral Voxtral audio transcription.</summary>
    Mistral = 7,

    /// <summary>Soniox async speech-to-text.</summary>
    Soniox = 8,

    /// <summary>HyperWhisper Cloud - built-in service, no API key required.</summary>
    HyperWhisperCloud = 9,

    /// <summary>Google Gemini multimodal audio transcription.</summary>
    Gemini = 10,

    /// <summary>xAI Grok speech-to-text (batch HTTP).</summary>
    Grok = 11,

    /// <summary>Microsoft MAI-Transcribe 1.5 via Azure Speech (HyperWhisper Cloud only).</summary>
    MicrosoftAzureSpeech = 12,

    /// <summary>Google Cloud Speech-to-Text V2 Chirp 3 (HyperWhisper Cloud only).</summary>
    GoogleSpeech = 13
}

/// <summary>
/// Extension methods for CloudTranscriptionProvider.
/// </summary>
public static class CloudTranscriptionProviderExtensions
{
    /// <summary>
    /// Gets the display name for UI presentation.
    /// </summary>
    public static string GetDisplayName(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => Loc.S("provider.openai"),
        CloudTranscriptionProvider.Groq => Loc.S("provider.groq"),
        CloudTranscriptionProvider.Deepgram => Loc.S("provider.deepgram"),
        CloudTranscriptionProvider.AssemblyAI => Loc.S("provider.assemblyai"),
        CloudTranscriptionProvider.ElevenLabs => Loc.S("provider.elevenlabs"),
        CloudTranscriptionProvider.Mistral => Loc.S("provider.mistral"),
        CloudTranscriptionProvider.Soniox => Loc.S("provider.soniox"),
        CloudTranscriptionProvider.HyperWhisperCloud => Loc.S("provider.hyperwhisper"),
        CloudTranscriptionProvider.Gemini => Loc.S("provider.gemini"),
        CloudTranscriptionProvider.Grok => Loc.S("provider.grok"),
        CloudTranscriptionProvider.MicrosoftAzureSpeech => Loc.S("provider.microsoftAzureSpeech"),
        CloudTranscriptionProvider.GoogleSpeech => Loc.S("provider.googleSpeech"),
        _ => Loc.S("provider.none")
    };

    /// <summary>
    /// Gets the string identifier used in Mode.CloudProvider field.
    /// Used for JSON serialization and cross-platform compatibility.
    /// </summary>
    public static string GetIdentifier(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => "openai",
        CloudTranscriptionProvider.Groq => "groq",
        CloudTranscriptionProvider.Deepgram => "deepgram",
        CloudTranscriptionProvider.AssemblyAI => "assemblyai",
        CloudTranscriptionProvider.ElevenLabs => "elevenlabs",
        CloudTranscriptionProvider.Mistral => "mistral",
        CloudTranscriptionProvider.Soniox => "soniox",
        CloudTranscriptionProvider.HyperWhisperCloud => "hyperwhisper",
        CloudTranscriptionProvider.Gemini => "gemini",
        CloudTranscriptionProvider.Grok => "grok",
        CloudTranscriptionProvider.MicrosoftAzureSpeech => "microsoftAzureSpeech",
        CloudTranscriptionProvider.GoogleSpeech => "googleSpeech",
        _ => ""
    };

    /// <summary>
    /// Parses a string identifier to CloudTranscriptionProvider.
    /// </summary>
    public static CloudTranscriptionProvider FromIdentifier(string? identifier) => identifier?.ToLowerInvariant() switch
    {
        "openai" => CloudTranscriptionProvider.OpenAI,
        "groq" => CloudTranscriptionProvider.Groq,
        "deepgram" => CloudTranscriptionProvider.Deepgram,
        "assemblyai" => CloudTranscriptionProvider.AssemblyAI,
        "elevenlabs" => CloudTranscriptionProvider.ElevenLabs,
        "mistral" => CloudTranscriptionProvider.Mistral,
        "soniox" => CloudTranscriptionProvider.Soniox,
        "hyperwhisper" => CloudTranscriptionProvider.HyperWhisperCloud,
        "gemini" => CloudTranscriptionProvider.Gemini,
        "grok" => CloudTranscriptionProvider.Grok,
        "microsoftazurespeech" => CloudTranscriptionProvider.MicrosoftAzureSpeech,
        "googlespeech" => CloudTranscriptionProvider.GoogleSpeech,
        _ => CloudTranscriptionProvider.None
    };

    /// <summary>
    /// Gets the PostProcessingProvider that shares the same API key.
    /// This allows reusing existing API keys for transcription.
    /// Some providers (Deepgram, AssemblyAI, ElevenLabs, Mistral) have their own keys.
    /// </summary>
    public static PostProcessingProvider GetApiKeyProvider(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => PostProcessingProvider.OpenAI,
        CloudTranscriptionProvider.Groq => PostProcessingProvider.Groq,
        CloudTranscriptionProvider.Gemini => PostProcessingProvider.Gemini,
        CloudTranscriptionProvider.Grok => PostProcessingProvider.Grok,
        // Deepgram, AssemblyAI, ElevenLabs, Mistral have their own keys
        // handled via TranscriptionApiKeyType enum
        _ => PostProcessingProvider.None
    };

    /// <summary>
    /// Whether this provider requires an API key.
    /// HyperWhisper Cloud uses device_id/license_key instead.
    /// </summary>
    public static bool RequiresApiKey(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.None => false,
        CloudTranscriptionProvider.HyperWhisperCloud => false,
        CloudTranscriptionProvider.MicrosoftAzureSpeech => false,
        CloudTranscriptionProvider.GoogleSpeech => false,
        _ => true
    };

    /// <summary>
    /// Gets the base API endpoint URL for the provider.
    /// </summary>
    public static string GetApiBaseUrl(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => "https://api.openai.com",
        CloudTranscriptionProvider.Groq => "https://api.groq.com",
        CloudTranscriptionProvider.Deepgram => "https://api.deepgram.com",
        CloudTranscriptionProvider.AssemblyAI => "https://api.assemblyai.com",
        CloudTranscriptionProvider.ElevenLabs => "https://api.elevenlabs.io",
        CloudTranscriptionProvider.Mistral => "https://api.mistral.ai",
        CloudTranscriptionProvider.Soniox => "https://api.soniox.com",
        CloudTranscriptionProvider.HyperWhisperCloud => "https://transcribe-prod-v2.hyperwhisper.com",
        CloudTranscriptionProvider.Gemini => "https://generativelanguage.googleapis.com",
        CloudTranscriptionProvider.Grok => "https://api.x.ai",
        // HW Cloud only — backend routes through the Fly /transcribe endpoint.
        CloudTranscriptionProvider.MicrosoftAzureSpeech => "https://transcribe-prod-v2.hyperwhisper.com",
        CloudTranscriptionProvider.GoogleSpeech => "https://transcribe-prod-v2.hyperwhisper.com",
        _ => ""
    };

    /// <summary>
    /// Gets the URL where users can obtain an API key for this provider.
    /// </summary>
    public static string GetApiKeyUrl(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => "https://platform.openai.com/api-keys",
        CloudTranscriptionProvider.Groq => "https://console.groq.com/keys",
        CloudTranscriptionProvider.Deepgram => "https://console.deepgram.com/",
        CloudTranscriptionProvider.AssemblyAI => "https://www.assemblyai.com/app/account",
        CloudTranscriptionProvider.ElevenLabs => "https://elevenlabs.io/app/settings/api-keys",
        CloudTranscriptionProvider.Mistral => "https://console.mistral.ai/api-keys",
        CloudTranscriptionProvider.Soniox => "https://console.soniox.com",
        CloudTranscriptionProvider.HyperWhisperCloud => "", // No API key needed
        CloudTranscriptionProvider.Gemini => "https://aistudio.google.com/apikey",
        CloudTranscriptionProvider.Grok => "https://console.x.ai/",
        CloudTranscriptionProvider.MicrosoftAzureSpeech => "",
        CloudTranscriptionProvider.GoogleSpeech => "",
        _ => ""
    };

    /// <summary>
    /// Gets the maximum file size in bytes supported by this provider.
    /// </summary>
    public static long GetMaxFileSizeBytes(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.Deepgram => 2L * 1024 * 1024 * 1024, // 2 GB
        CloudTranscriptionProvider.AssemblyAI => 5L * 1024 * 1024 * 1024, // 5 GB
        CloudTranscriptionProvider.ElevenLabs => 3L * 1024 * 1024 * 1024, // 3 GB
        CloudTranscriptionProvider.Gemini => 2L * 1024 * 1024 * 1024, // 2 GB (Files API upload limit)
        CloudTranscriptionProvider.HyperWhisperCloud => 2L * 1024 * 1024 * 1024, // 2 GB
        CloudTranscriptionProvider.Mistral => 100L * 1024 * 1024, // 100 MB
        CloudTranscriptionProvider.Soniox => 1L * 1024 * 1024 * 1024, // 1 GB
        CloudTranscriptionProvider.Grok => 500L * 1024 * 1024, // 500 MB
        CloudTranscriptionProvider.MicrosoftAzureSpeech => 300L * 1024 * 1024, // 300 MB
        // Google Speech V2 inline `content` caps near 10 MB. Matches the
        // backend's 9.5 MB AudioTooLargeError guard.
        CloudTranscriptionProvider.GoogleSpeech => 9_500_000L,
        _ => 25L * 1024 * 1024 // 25 MB (OpenAI, Groq)
    };

    /// <summary>
    /// Whether this provider supports vocabulary/custom terms.
    /// </summary>
    public static bool SupportsVocabulary(this CloudTranscriptionProvider provider) => provider switch
    {
        // ElevenLabs: Scribe v2 supports vocabulary, v1 doesn't (model-specific check in UI)
        CloudTranscriptionProvider.Mistral => false,
        // Grok STT has no prompt or keyterm parameter
        CloudTranscriptionProvider.Grok => false,
        // Azure MAI + Google Chirp 3 are surfaced as HW Cloud accuracy tiers
        // since PR #521; they no longer appear in the standalone BYOK list.
        // Any un-migrated mode that still hits the BYOK path with these
        // provider values must NOT ship `initial_prompt` (Chirp 3 drops it,
        // Azure MAI uses a different field). The HW Cloud send path has its
        // own catalog-driven gate.
        CloudTranscriptionProvider.MicrosoftAzureSpeech => false,
        CloudTranscriptionProvider.GoogleSpeech => false,
        CloudTranscriptionProvider.None => false,
        _ => true
    };

    /// <summary>
    /// Gets all available providers (excluding None).
    /// </summary>
    public static IEnumerable<CloudTranscriptionProvider> GetAllProviders()
    {
        return Enum.GetValues<CloudTranscriptionProvider>()
            .Where(p => p != CloudTranscriptionProvider.None);
    }
}
