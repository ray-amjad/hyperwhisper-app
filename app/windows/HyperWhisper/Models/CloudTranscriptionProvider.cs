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
// 10. GeminiTranscribe - Google Gemini 3.5 Transcribe (dedicated STT endpoint)

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

    /// <summary>Microsoft MAI-Transcribe (2 and 1.5) via Azure Speech (HyperWhisper Cloud only).</summary>
    MicrosoftAzureSpeech = 12,

    /// <summary>Google Cloud Speech-to-Text V2 Chirp 3 (HyperWhisper Cloud only).</summary>
    GoogleSpeech = 13,

    /// <summary>
    /// Google Gemini 3.5 Transcribe — the dedicated /v1beta/interactions STT
    /// endpoint. Same vendor as <see cref="Gemini"/> but a different API and its
    /// OWN key slot; the two are never interchangeable.
    /// </summary>
    GeminiTranscribe = 14,

    /// <summary>Meta Muse Voice Transcribe 1.0 batch transcription.</summary>
    Meta = 15
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
        CloudTranscriptionProvider.GeminiTranscribe => Loc.S("provider.geminiTranscribe"),
        CloudTranscriptionProvider.Meta => Loc.S("provider.meta"),
        _ => Loc.S("provider.none")
    };

    /// <summary>
    /// Whether a content-free health probe can say anything about this vendor's
    /// API key.
    ///
    /// False for Meta MuseSTT alone: it documents no validation endpoint, so
    /// CloudProviderHealthService answers <c>ProviderHealth.Unknown</c> for it
    /// unconditionally and a caller that waits for <c>Healthy</c> waits forever. A
    /// present Meta key is CONFIGURED but unvalidated until the first
    /// transcription, and callers have to be able to say that rather than treat
    /// the silence as a failure.
    ///
    /// Single-sourced here because both sides need it: the health service, which
    /// produces the Unknown, and the onboarding Configure step, which has to open
    /// its gate on it. #393 shipped Meta with full BYOK support and left the two
    /// facts in one file; the onboarding chip strip made that a dead end.
    /// </summary>
    public static bool SupportsKeyHealthProbe(this CloudTranscriptionProvider provider) =>
        provider != CloudTranscriptionProvider.Meta;

    /// <summary>
    /// The bare file name, without extension, of this provider's logo under
    /// Assets/Providers. Single-sourced here because two places need it:
    /// ModelLibraryManager builds LibraryModel.ProviderAssetName from it, and the
    /// onboarding Configure step draws the same marks on its provider chips.
    /// GeminiTranscribe is the same vendor as Gemini and deliberately reuses the
    /// Gemini logo rather than shipping a duplicate PNG. "providerMeta" is a
    /// sentinel with no PNG behind it: LibraryModel draws a monogram for it
    /// instead, so nothing may build an image path from it unguarded.
    ///
    /// Use <see cref="TryGetAssetPath"/> to build a pack URI. Do not concatenate.
    /// </summary>
    public static string GetAssetName(this CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => "providerOpenAI",
        CloudTranscriptionProvider.Groq => "providerGroq",
        CloudTranscriptionProvider.Deepgram => "providerDeepgram",
        CloudTranscriptionProvider.AssemblyAI => "providerAssemblyAI",
        CloudTranscriptionProvider.ElevenLabs => "providerElevenLabs",
        CloudTranscriptionProvider.Mistral => "providerMistral",
        CloudTranscriptionProvider.Soniox => "providerSoniox",
        CloudTranscriptionProvider.Gemini => "providerGemini",
        CloudTranscriptionProvider.GeminiTranscribe => "providerGemini",
        CloudTranscriptionProvider.Meta => "providerMeta",
        CloudTranscriptionProvider.Grok => "providerGrok",
        CloudTranscriptionProvider.MicrosoftAzureSpeech => "providerMicrosoft",
        CloudTranscriptionProvider.GoogleSpeech => "providerGoogle",
        _ => "providerLocalWhisper"
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
        CloudTranscriptionProvider.GeminiTranscribe => "geminiTranscribe",
        CloudTranscriptionProvider.Meta => "meta",
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
        // Lower-cased form of the "geminiTranscribe" identifier — the switch runs
        // on ToLowerInvariant(), so the camelCase spelling would never match.
        "geminitranscribe" => CloudTranscriptionProvider.GeminiTranscribe,
        "meta" => CloudTranscriptionProvider.Meta,
        _ => CloudTranscriptionProvider.None
    };

    /// <summary>
    /// Parses a <c>cloud-stt-catalog.json</c> <c>sttProvider</c> value — the
    /// backend's <c>X-STT-Provider</c> dispatch key — to this enum.
    ///
    /// This is a DIFFERENT namespace from <see cref="FromIdentifier"/>, which
    /// parses the identifiers we persist in a mode (<c>GetIdentifier()</c>'s
    /// inverse). Two catalog values exist only here and have no storage
    /// identifier spelling: <c>azure-mai</c> and <c>gemini-transcribe</c>.
    /// Feeding either to <see cref="FromIdentifier"/> returns
    /// <see cref="CloudTranscriptionProvider.None"/>, which is what silently
    /// disabled the Mode editor's per-model Azure language filter — the branch
    /// was there, the resolution never reached it.
    ///
    /// Everything else delegates, so the two namespaces stay one table.
    /// </summary>
    public static CloudTranscriptionProvider FromCatalogSttProvider(string? sttProvider)
        => sttProvider?.Trim().ToLowerInvariant() switch
        {
            "azure-mai" => CloudTranscriptionProvider.MicrosoftAzureSpeech,
            "gemini-transcribe" => CloudTranscriptionProvider.GeminiTranscribe,
            var other => FromIdentifier(other),
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
        // Deepgram, AssemblyAI, ElevenLabs, Mistral, Soniox have their own keys
        // handled via TranscriptionApiKeyType enum. GeminiTranscribe is Google
        // too, but deliberately does NOT share the Gemini key — it is a separate
        // API with its own key slot (TranscriptionApiKeyType.GeminiTranscribe),
        // so it must fall through to None here.
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
        // Same Google AI Studio console as Gemini — one place issues both keys.
        CloudTranscriptionProvider.GeminiTranscribe => "https://aistudio.google.com/apikey",
        CloudTranscriptionProvider.Grok => "https://console.x.ai/",
        CloudTranscriptionProvider.MicrosoftAzureSpeech => "",
        CloudTranscriptionProvider.GoogleSpeech => "",
        CloudTranscriptionProvider.Meta => "https://dev.meta.ai/docs/speech-to-text/",
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
        // Gemini 3.5 Transcribe sends the audio INLINE as base64 in the JSON body
        // (~33% inflation), so the raw-file cap is well below the request cap.
        CloudTranscriptionProvider.GeminiTranscribe => 14L * 1024 * 1024, // 14 MB raw
        CloudTranscriptionProvider.HyperWhisperCloud => 2L * 1024 * 1024 * 1024, // 2 GB
        CloudTranscriptionProvider.Mistral => 100L * 1024 * 1024, // 100 MB
        CloudTranscriptionProvider.Soniox => 1L * 1024 * 1024 * 1024, // 1 GB
        CloudTranscriptionProvider.Grok => 500L * 1024 * 1024, // 500 MB
        CloudTranscriptionProvider.MicrosoftAzureSpeech => 300L * 1024 * 1024, // 300 MB
        // Google Speech V2 inline `content` caps near 10 MB. Matches the
        // backend's 9.5 MB AudioTooLargeError guard.
        CloudTranscriptionProvider.GoogleSpeech => 9_500_000L,
        CloudTranscriptionProvider.Meta => 32L * 1024 * 1024,
        _ => 25L * 1024 * 1024 // 25 MB (OpenAI, Groq)
    };

    /// <summary>
    /// Whether this provider supports vocabulary/custom terms.
    /// </summary>
    public static bool SupportsVocabulary(this CloudTranscriptionProvider provider) => provider switch
    {
        // ElevenLabs: Scribe v2 supports vocabulary, v1 doesn't (model-specific check in UI)
        // Azure MAI + Google Chirp 3 are surfaced as HW Cloud accuracy tiers
        // since PR #521; they no longer appear in the standalone BYOK list.
        // Any un-migrated mode that still hits the BYOK path with these
        // provider values must NOT ship `initial_prompt` (Chirp 3 takes a
        // phrase set on the routed path only, Azure MAI uses a different
        // field). The HW Cloud send path has its own catalog-driven gate.
        // GeminiTranscribe takes a real `custom_vocabulary` field on
        // /v1beta/interactions, so it belongs in the `true` default below —
        // listed here only so an audit grep for the provider finds this site.
        CloudTranscriptionProvider.MicrosoftAzureSpeech => false,
        CloudTranscriptionProvider.GoogleSpeech => false,
        CloudTranscriptionProvider.None => false,
        _ => true
    };

}
