namespace HyperWhisper.Data.Entities;

/// <summary>
/// MODE ENTITY
///
/// Represents a transcription profile that bundles together settings for different contexts.
/// Matches macOS Core Data Mode entity for cross-platform consistency.
/// </summary>
public class Mode
{
    // =========================================================================
    // IDENTITY
    // =========================================================================

    /// <summary>Unique identifier for the mode.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-visible name (e.g., "Meetings", "Notes").</summary>
    public string Name { get; set; } = "Default";

    /// <summary>Preset type for styling and behavior hints.</summary>
    public string Preset { get; set; } = "hyper";

    /// <summary>Whether this is the default mode.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whether this mode was created by the system (vs user).</summary>
    public bool IsSystemProvided { get; set; }

    /// <summary>Display order in the mode list.</summary>
    public int SortOrder { get; set; }

    // =========================================================================
    // TRANSCRIPTION SETTINGS
    // =========================================================================

    /// <summary>
    /// ISO language code for transcription.
    /// "auto" = auto-detect, "en" = English, "ja" = Japanese, etc.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Model identifier for local transcription.
    /// e.g., "base", "medium", "large-v3-turbo"
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Model type identifier (legacy compatibility).
    /// Same as Model property for backward compatibility.
    /// </summary>
    public string? ModelType { get; set; }

    /// <summary>
    /// Local transcription engine: "whisper" or "parakeet".
    /// Determines which engine is used for local transcription.
    /// </summary>
    public string LocalEngine { get; set; } = "whisper";

    /// <summary>
    /// Parakeet model identifier (e.g., "parakeet-v2", "parakeet-v3").
    /// Only used when LocalEngine is "parakeet".
    /// </summary>
    public string? LocalParakeetModel { get; set; }

    // =========================================================================
    // CLOUD SETTINGS
    // =========================================================================

    /// <summary>Cloud provider: "hyperwhisper", "openai", "groq", etc.</summary>
    public string? CloudProvider { get; set; }

    /// <summary>Cloud transcription model ID.</summary>
    public string? CloudTranscriptionModel { get; set; }

    /// <summary>
    /// Cloud transcription domain for the HyperWhisper Cloud path
    /// (<c>X-STT-Domain</c> header). Currently only "medical" or null.
    /// </summary>
    public string? CloudTranscriptionDomain { get; set; }

    /// <summary>Provider type: "local" or "cloud".</summary>
    public string? ProviderType { get; set; }

    /// <summary>Cloud accuracy route for HyperWhisper Cloud.</summary>
    public string CloudAccuracyTier { get; set; } = "elevenLabsScribeV2";

    /// <summary>Custom transcription prompt for Gemini provider (max 2000 chars).</summary>
    public string? GeminiCustomPrompt { get; set; }

    // =========================================================================
    // TEXT FORMATTING
    // =========================================================================

    /// <summary>Add punctuation to transcription.</summary>
    public bool Punctuation { get; set; } = true;

    /// <summary>Capitalize text appropriately.</summary>
    public bool Capitalization { get; set; } = true;

    /// <summary>Filter profanity from output.</summary>
    public bool ProfanityFilter { get; set; }

    /// <summary>Remove trailing period from transcription output.</summary>
    public bool RemoveTrailingPeriod { get; set; }

    /// <summary>English spelling variant: "american", "british", etc.</summary>
    public string? EnglishSpelling { get; set; }

    // =========================================================================
    // POST-PROCESSING
    // =========================================================================

    /// <summary>Post-processing mode: 0=off, 1=cloud, 2=local.</summary>
    public int PostProcessingMode { get; set; }

    /// <summary>Post-processing provider: "openai", "anthropic", etc.</summary>
    public string? PostProcessingProvider { get; set; }

    /// <summary>Language model for post-processing: "gpt-4.1-nano", etc.</summary>
    public string? LanguageModel { get; set; }

    /// <summary>Local GGUF model for on-device post-processing.</summary>
    public string? LocalPostProcessingModel { get; set; }

    /// <summary>User-supplied system prompt for post-processing (max 2000 chars).</summary>
    public string? UserSystemPrompt { get; set; }

    /// <summary>Custom instructions for "custom" preset type.</summary>
    public string? CustomInstructions { get; set; }

    /// <summary>Whether to capture screen text via OCR at recording start for post-processing context.</summary>
    public bool EnableScreenOCR { get; set; }

    /// <summary>
    /// Cloud post-processing model for HyperWhisper Cloud, persisted as the
    /// provider-qualified "&lt;engineId&gt;:&lt;modelId&gt;" key (e.g.
    /// "anthropic:claude-haiku-4-5"). Free string column — no EF default / migration.
    /// </summary>
    public string CloudPostProcessingModel { get; set; } = "anthropic:claude-haiku-4-5";

    // =========================================================================
    // VOCABULARY
    // =========================================================================

    /// <summary>Custom vocabulary terms for better accuracy.</summary>
    public List<string>? CustomVocabulary { get; set; }

    // =========================================================================
    // TIMESTAMPS
    // =========================================================================

    /// <summary>When the mode was created.</summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>When the mode was last modified.</summary>
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

    // =========================================================================
    // CROSS-PLATFORM PASSTHROUGH
    // =========================================================================

    /// <summary>
    /// Raw JSON of the mode's NON-Windows <c>platformExtensions</c> slices (e.g.
    /// the <c>macos</c> blob), captured on universal-v2 import and re-emitted on
    /// export so a foreign platform's per-mode data survives a Windows round-trip
    /// (H4). Free nullable TEXT column; <c>null</c> = no foreign slices preserved.
    /// </summary>
    public string? ForeignPlatformExtensions { get; set; }
}
