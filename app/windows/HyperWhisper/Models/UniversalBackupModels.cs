using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperWhisper.Models;

/// <summary>
/// UNIVERSAL BACKUP MODELS
///
/// C# model classes matching the shared cross-platform backup schema
/// (shared-backup/hyperwhisper-backup.schema.json, schemaVersion 2).
///
/// These models are used for serializing/deserializing .hwbackup.json files
/// that can be exchanged between macOS and Windows.
/// </summary>
public class UniversalBackup
{
    // Default 0 (not 2): a legacy macOS v1 backup has "version" but no "schemaVersion" key.
    // Defaulting to 2 would let such a file silently pass the `SchemaVersion != 2` guards and
    // be treated as a universal v2 bridge file. With 0 it's correctly rejected. Export always
    // sets this to 2 explicitly, so written files are unaffected.
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("exportDate")]
    public DateTime ExportDate { get; set; }

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "windows";

    [JsonPropertyName("settings")]
    public UniversalSettings? Settings { get; set; }

    [JsonPropertyName("modes")]
    public List<UniversalMode>? Modes { get; set; }

    [JsonPropertyName("vocabulary")]
    public List<UniversalVocabularyItem>? Vocabulary { get; set; }

    [JsonPropertyName("apiKeys")]
    public UniversalApiKeys? ApiKeys { get; set; }

    [JsonPropertyName("licenseKey")]
    public string? LicenseKey { get; set; }

    [JsonPropertyName("platformExtensions")]
    public Dictionary<string, JsonElement>? PlatformExtensions { get; set; }

    /// <summary>
    /// Unknown TOP-LEVEL keys, captured so a file written by a newer build is not
    /// truncated when an older one re-exports it. Persisted between import and
    /// export in <c>SettingsData.BackupUnknownRootKeys</c>.
    /// </summary>
    /// <remarks>
    /// <c>platformExtensions</c> can never land here — it is a declared property —
    /// and its foreign slices have their own store,
    /// <c>SettingsData.BackupForeignPlatformExtensions</c>.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

// =========================================================================
// SETTINGS
// =========================================================================

/// <remarks>
/// Every settings type below carries a <c>[JsonExtensionData]</c> bag. Together
/// they mirror the universal <c>settings</c> tree, which is what lets
/// <c>UniversalBackupMapper</c> re-emit a captured key at its ORIGINAL nesting
/// level — <c>textOutput.storeWordTimestamps</c> comes back under
/// <c>textOutput</c>, never at the root — with no manual path arithmetic.
/// A key matching a declared <c>[JsonPropertyName]</c> never lands in the bag.
/// </remarks>
public class UniversalSettings
{
    [JsonPropertyName("general")]
    public UniversalGeneralSettings? General { get; set; }

    [JsonPropertyName("textOutput")]
    public UniversalTextOutputSettings? TextOutput { get; set; }

    [JsonPropertyName("storage")]
    public UniversalStorageSettings? Storage { get; set; }

    [JsonPropertyName("streaming")]
    public UniversalStreamingSettings? Streaming { get; set; }

    [JsonPropertyName("advanced")]
    public UniversalAdvancedSettings? Advanced { get; set; }

    /// <summary>Unknown SECTIONS of the settings block (a whole future category).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

public class UniversalGeneralSettings
{
    [JsonPropertyName("launchMinimized")]
    public bool? LaunchMinimized { get; set; }

    [JsonPropertyName("showRecordingWindow")]
    public bool? ShowRecordingWindow { get; set; }

    [JsonPropertyName("checkForUpdatesAutomatically")]
    public bool? CheckForUpdatesAutomatically { get; set; }

    [JsonPropertyName("enableErrorLogging")]
    public bool? EnableErrorLogging { get; set; }

    [JsonPropertyName("shareAnonymousSpeedData")]
    public bool? ShareAnonymousSpeedData { get; set; }

    [JsonPropertyName("enableSoundEffects")]
    public bool? EnableSoundEffects { get; set; }

    /// <summary>Unknown keys of the <c>general</c> section.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

public class UniversalTextOutputSettings
{
    [JsonPropertyName("pasteResultText")]
    public bool? PasteResultText { get; set; }

    [JsonPropertyName("removeFillerWords")]
    public bool? RemoveFillerWords { get; set; }

    [JsonPropertyName("restoreClipboardAfterPaste")]
    public bool? RestoreClipboardAfterPaste { get; set; }

    [JsonPropertyName("hideFromClipboardHistory")]
    public bool? HideFromClipboardHistory { get; set; }

    [JsonPropertyName("clipboardRestoreDelaySeconds")]
    public double? ClipboardRestoreDelaySeconds { get; set; }

    [JsonPropertyName("autocapitalizeInsert")]
    public bool? AutocapitalizeInsert { get; set; }

    /// <summary>
    /// Unknown keys of the <c>textOutput</c> section. This is where
    /// <c>storeWordTimestamps</c> lands: it exists on macOS and on Linux, Windows
    /// has no property for it and is not gaining one, and before this bag existed
    /// it died at deserialize — so a mac→Windows→mac trip lost it for good (#288).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

public class UniversalStorageSettings
{
    [JsonPropertyName("keepAudioFiles")]
    public bool? KeepAudioFiles { get; set; }

    [JsonPropertyName("storeAsM4A")]
    public bool? StoreAsM4A { get; set; }

    /// <summary>Unknown keys of the <c>storage</c> section.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

public class UniversalAdvancedSettings
{
    [JsonPropertyName("maxRecordingDuration")]
    public int? MaxRecordingDuration { get; set; }

    [JsonPropertyName("typingSpeedWPM")]
    public int? TypingSpeedWPM { get; set; }

    /// <summary>Unknown keys of the <c>advanced</c> section.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

public class UniversalStreamingSettings
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("deepgramModel")]
    public string? DeepgramModel { get; set; }

    /// <summary>
    /// Which vendor HyperWhisper Cloud's live route uses, as a cloud-stt-catalog
    /// entry id. Absent or null means the default, deepgramNova3 - which is what
    /// every backup written before this key existed carries.
    /// </summary>
    [JsonPropertyName("cloudTier")]
    public string? CloudTier { get; set; }

    [JsonPropertyName("fastFormatting")]
    public bool? FastFormatting { get; set; }

    [JsonPropertyName("shortcut")]
    public string? Shortcut { get; set; }

    /// <summary>Unknown keys of the <c>streaming</c> section.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

// =========================================================================
// MODES
// =========================================================================

public class UniversalMode
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "hyper";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("punctuation")]
    public bool Punctuation { get; set; } = true;

    [JsonPropertyName("capitalization")]
    public bool Capitalization { get; set; } = true;

    [JsonPropertyName("profanityFilter")]
    public bool ProfanityFilter { get; set; }

    [JsonPropertyName("removeTrailingPeriod")]
    public bool? RemoveTrailingPeriod { get; set; }

    [JsonPropertyName("englishSpelling")]
    public string? EnglishSpelling { get; set; }

    [JsonPropertyName("cloudProvider")]
    public string? CloudProvider { get; set; }

    [JsonPropertyName("cloudTranscriptionModel")]
    public string? CloudTranscriptionModel { get; set; }

    [JsonPropertyName("cloudTranscriptionDomain")]
    public string? CloudTranscriptionDomain { get; set; }

    [JsonPropertyName("postProcessingMode")]
    public int PostProcessingMode { get; set; }

    [JsonPropertyName("postProcessingProvider")]
    public string? PostProcessingProvider { get; set; }

    [JsonPropertyName("languageModel")]
    public string? LanguageModel { get; set; }

    [JsonPropertyName("localPostProcessingModel")]
    public string? LocalPostProcessingModel { get; set; }

    [JsonPropertyName("userSystemPrompt")]
    public string? UserSystemPrompt { get; set; }

    [JsonPropertyName("customInstructions")]
    public string? CustomInstructions { get; set; }

    [JsonPropertyName("geminiCustomPrompt")]
    public string? GeminiCustomPrompt { get; set; }

    [JsonPropertyName("cloudAccuracyTier")]
    public string? CloudAccuracyTier { get; set; }

    [JsonPropertyName("cloudPostProcessingModel")]
    public string? CloudPostProcessingModel { get; set; }

    [JsonPropertyName("platformExtensions")]
    public Dictionary<string, JsonElement>? PlatformExtensions { get; set; }

    /// <summary>
    /// Unknown per-mode keys. CAPTURED BUT NOT PERSISTED, deliberately: a mode's
    /// home is the EF entity <c>Data/Entities/Mode.cs</c>, so a store for these
    /// would need a real EF migration, which is out of scope for #288. The bag
    /// still earns its place — it keeps an unknown key alive for the lifetime of
    /// the DTO, and it documents the gap at the point where it exists.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

// =========================================================================
// VOCABULARY
// =========================================================================

public class UniversalVocabularyItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("word")]
    public string Word { get; set; } = "";

    [JsonPropertyName("replacement")]
    public string? Replacement { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Unknown per-item keys. Captured but NOT persisted, for the same reason as
    /// <see cref="UniversalMode.Additional"/>: the home is an EF entity.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; set; }
}

// =========================================================================
// API KEYS
// =========================================================================

public class UniversalApiKeys
{
    [JsonPropertyName("openai")]
    public string? OpenAI { get; set; }

    [JsonPropertyName("groq")]
    public string? Groq { get; set; }

    /// <summary>
    /// DEPRECATED no-op. Fireworks AI was removed as a transcription provider.
    /// The JSON property is retained so older backups still deserialize without
    /// error; it is never populated on export and never applied on restore.
    /// </summary>
    [JsonPropertyName("fireworks")]
    public string? Fireworks { get; set; }

    [JsonPropertyName("anthropic")]
    public string? Anthropic { get; set; }

    [JsonPropertyName("gemini")]
    public string? Gemini { get; set; }

    [JsonPropertyName("deepgram")]
    public string? Deepgram { get; set; }

    [JsonPropertyName("assemblyai")]
    public string? AssemblyAI { get; set; }

    [JsonPropertyName("elevenlabs")]
    public string? ElevenLabs { get; set; }

    [JsonPropertyName("mistral")]
    public string? Mistral { get; set; }

    [JsonPropertyName("cerebras")]
    public string? Cerebras { get; set; }

    [JsonPropertyName("soniox")]
    public string? Soniox { get; set; }

    [JsonPropertyName("grok")]
    public string? Grok { get; set; }

    /// <summary>
    /// Google Gemini 3.5 Transcribe (<c>TranscriptionApiKeyType.GeminiTranscribe</c>).
    /// A SEPARATE slot from <see cref="Gemini"/>, which is the legacy Gemini
    /// post-processing key: same "AIza" shape, different API, and users may hold
    /// different keys for them. Because the legacy key does round-trip, a missing
    /// field here fails silently - the restored machine looks configured for
    /// Gemini and is not configured for Gemini 3.5 Transcribe.
    ///
    /// Squashed lowercase like every other multi-word provider key here
    /// (<c>assemblyai</c>, <c>elevenlabs</c>) - shared-backup/AGENTS.md pins the
    /// apiKeys object to "lowercase provider-name keys". The camelCase catalog id
    /// (<c>geminiTranscribe</c>) is accepted too on IMPORT, via
    /// <see cref="AdditionalKeys"/>, so a backup written by a platform that
    /// spelled it that way still restores - see
    /// <c>UniversalBackupMapper.ApplyApiKeys</c>.
    /// </summary>
    [JsonPropertyName("geminitranscribe")]
    public string? GeminiTranscribe { get; set; }

    [JsonPropertyName("meta")]
    public string? Meta { get; set; }

    /// <summary>Captures unknown provider keys for round-trip preservation.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalKeys { get; set; }
}

// =========================================================================
// WINDOWS PLATFORM EXTENSIONS (for mode-level platformExtensions.windows)
// =========================================================================

/// <summary>
/// Windows-only mode fields stored in platformExtensions.windows.
/// Used as a helper for serialization/deserialization — not part of the schema contract.
/// </summary>
public class WindowsModeExtensions
{
    [JsonPropertyName("modelType")]
    public string? ModelType { get; set; }

    [JsonPropertyName("localEngine")]
    public string? LocalEngine { get; set; }

    [JsonPropertyName("localParakeetModel")]
    public string? LocalParakeetModel { get; set; }

    [JsonPropertyName("providerType")]
    public string? ProviderType { get; set; }

    [JsonPropertyName("cloudAccuracyTier")]
    public string? CloudAccuracyTier { get; set; }

    [JsonPropertyName("cloudPostProcessingModel")]
    public string? CloudPostProcessingModel { get; set; }

    [JsonPropertyName("localPostProcessingModel")]
    public string? LocalPostProcessingModel { get; set; }

    [JsonPropertyName("enableScreenOCR")]
    public bool? EnableScreenOCR { get; set; }

    [JsonPropertyName("customVocabulary")]
    public List<string>? CustomVocabulary { get; set; }

    [JsonPropertyName("isSystemProvided")]
    public bool? IsSystemProvided { get; set; }

    [JsonPropertyName("createdDate")]
    public DateTime? CreatedDate { get; set; }

    [JsonPropertyName("modifiedDate")]
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>
/// Windows-only settings stored in platformExtensions.windows.settings.
/// </summary>
public class WindowsSettingsExtensions
{
    [JsonPropertyName("minimizeToTray")]
    public bool? MinimizeToTray { get; set; }

    [JsonPropertyName("hideFromClipboardHistory")]
    public bool? HideFromClipboardHistory { get; set; }

    [JsonPropertyName("themeMode")]
    public int? ThemeMode { get; set; }

    [JsonPropertyName("autoDeleteEnabled")]
    public bool? AutoDeleteEnabled { get; set; }

    [JsonPropertyName("autoDeleteDaysOld")]
    public int? AutoDeleteDaysOld { get; set; }

    [JsonPropertyName("parakeetEnabled")]
    public bool? ParakeetEnabled { get; set; }

    [JsonPropertyName("keepMicrophoneWarm")]
    public bool? KeepMicrophoneWarm { get; set; }

    [JsonPropertyName("mediaControlMode")]
    public string? MediaControlMode { get; set; }

    [JsonPropertyName("toggleShortcut")]
    public string? ToggleShortcut { get; set; }

    [JsonPropertyName("cancelShortcut")]
    public string? CancelShortcut { get; set; }

    [JsonPropertyName("changeModeShortcut")]
    public string? ChangeModeShortcut { get; set; }

    [JsonPropertyName("streamingShortcut")]
    public string? StreamingShortcut { get; set; }

    [JsonPropertyName("streamingEnabled")]
    public bool? StreamingEnabled { get; set; }

    [JsonPropertyName("streamingProvider")]
    public string? StreamingProvider { get; set; }

    [JsonPropertyName("streamingLanguage")]
    public string? StreamingLanguage { get; set; }

    [JsonPropertyName("streamingDeepgramModel")]
    public string? StreamingDeepgramModel { get; set; }

    [JsonPropertyName("streamingFastFormatting")]
    public bool? StreamingFastFormatting { get; set; }

    [JsonPropertyName("autoIncreaseMicVolume")]
    public bool? AutoIncreaseMicVolume { get; set; }

    [JsonPropertyName("autocapitalizeInsert")]
    public bool? AutocapitalizeInsert { get; set; }

    [JsonPropertyName("customEndpoints")]
    public List<CustomPostProcessingEndpoint>? CustomEndpoints { get; set; }
}
