using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
// TODO-verify (Windows/CI): Rust shared-core swap — cloud tier / pp-model migration.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// UNIVERSAL BACKUP MAPPER
///
/// Bidirectional mapping between Windows EF Core entities / SettingsService
/// and the cross-platform universal backup format (schemaVersion 2).
///
/// Export: Windows entities → UniversalBackup
/// Import: UniversalBackup → Windows entities
/// </summary>
public static class UniversalBackupMapper
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // =========================================================================
    // SHARED-CORE STORAGE-STRING MIGRATIONS (present-only)
    // =========================================================================

    // TODO-verify (Windows/CI): Rust shared-core swap.
    // Migrate persisted cloudAccuracyTier / cloudPostProcessingModel storage
    // strings via the Rust shared core instead of the hand-rolled
    // CloudAccuracyTierExtensions.FromString(...).ToStorageValue() /
    // CloudPostProcessingModelExtensions.FromString(...).ToStorageValue() pair.
    //
    // PRESENT-ONLY (mirrors the macOS M3-D decision): only migrate a non-null,
    // non-whitespace source value. When the source is null/empty we return null
    // so the caller can keep whatever default/fallback it already had, rather than
    // letting the core write its own default where the field was intentionally
    // absent. The core itself maps None/empty → its default, so we guard up front.
    private static string? MigrateCloudAccuracyTierPresent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return HyperwhisperCoreMethods.MigrateCloudAccuracyTier(value);
    }

    private static string? MigrateCloudPpModelPresent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return HyperwhisperCoreMethods.MigrateCloudPpModel(value);
    }

    // Entity-side defaults used as the present-only fallback (read from the Mode
    // entity so the canonical default lives in exactly one place).
    private static readonly Mode ModeDefaults = new();

    // =========================================================================
    // EXPORT: Windows → Universal
    // =========================================================================

    /// <summary>
    /// Maps current Windows settings to universal settings format.
    /// </summary>
    public static UniversalSettings MapSettings(SettingsService settings)
    {
        return new UniversalSettings
        {
            General = new UniversalGeneralSettings
            {
                LaunchMinimized = settings.LaunchMinimized,
                ShowRecordingWindow = settings.ShowRecordingWindow,
                CheckForUpdatesAutomatically = settings.CheckForUpdatesAutomatically,
                EnableErrorLogging = settings.EnableErrorLogging,
                EnableSoundEffects = settings.EnableSoundEffects
            },
            TextOutput = new UniversalTextOutputSettings
            {
                PasteResultText = settings.AutoPasteEnabled,
                RemoveFillerWords = settings.RemoveFillerWords,
                RestoreClipboardAfterPaste = settings.RestoreClipboardAfterPaste,
                HideFromClipboardHistory = settings.HideFromClipboardHistory,
                ClipboardRestoreDelaySeconds = settings.ClipboardRestoreDelaySeconds,
                AutocapitalizeInsert = settings.AutocapitalizeInsert
            },
            Storage = new UniversalStorageSettings
            {
                StoreAsM4A = settings.StoreAsM4A
            },
            Streaming = new UniversalStreamingSettings
            {
                Enabled = settings.StreamingEnabled,
                Provider = settings.StreamingProvider,
                Language = settings.StreamingLanguage,
                DeepgramModel = settings.StreamingDeepgramModel,
                FastFormatting = settings.StreamingFastFormatting,
                Shortcut = settings.StreamingShortcut.ToPersistedString()
            },
            Advanced = new UniversalAdvancedSettings
            {
                TypingSpeedWPM = settings.TypingSpeedWPM
            },
        };
    }

    /// <summary>
    /// Maps a Windows Mode entity to a universal mode.
    /// Windows-only fields are packed into platformExtensions.windows.
    /// </summary>
    public static UniversalMode MapMode(Mode mode)
    {
        var universal = new UniversalMode
        {
            Id = mode.Id,
            Name = mode.Name,
            Preset = mode.Preset,
            Language = mode.Language,
            Model = mode.Model,
            IsDefault = mode.IsDefault,
            SortOrder = mode.SortOrder,
            Punctuation = mode.Punctuation,
            Capitalization = mode.Capitalization,
            ProfanityFilter = mode.ProfanityFilter,
            RemoveTrailingPeriod = mode.RemoveTrailingPeriod,
            EnglishSpelling = mode.EnglishSpelling,
            CloudProvider = mode.CloudProvider,
            CloudTranscriptionModel = mode.CloudTranscriptionModel,
            CloudTranscriptionDomain = mode.CloudTranscriptionDomain,
            PostProcessingMode = mode.PostProcessingMode,
            PostProcessingProvider = PostProcessingProviderExtensions.ToUniversalStorageValue(mode.PostProcessingProvider),
            LanguageModel = mode.LanguageModel,
            LocalPostProcessingModel = mode.LocalPostProcessingModel,
            UserSystemPrompt = mode.UserSystemPrompt,
            CustomInstructions = mode.CustomInstructions,
            GeminiCustomPrompt = mode.GeminiCustomPrompt,
            // TODO-verify (Windows/CI): Rust shared-core swap — present-only migration.
            // mode.CloudAccuracyTier/CloudPostProcessingModel are non-null entity fields
            // (carry defaults), so these are effectively always present; the present-only
            // helper still guards the empty case rather than forcing the core default.
            CloudAccuracyTier = MigrateCloudAccuracyTierPresent(mode.CloudAccuracyTier),
            CloudPostProcessingModel = MigrateCloudPpModelPresent(mode.CloudPostProcessingModel)
        };

        // Pack Windows-only fields into platformExtensions.windows
        var winExt = new WindowsModeExtensions
        {
            ModelType = mode.ModelType,
            LocalEngine = mode.LocalEngine,
            LocalParakeetModel = mode.LocalParakeetModel,
            ProviderType = mode.ProviderType,
            CloudAccuracyTier = mode.CloudAccuracyTier,
            CloudPostProcessingModel = mode.CloudPostProcessingModel,
            LocalPostProcessingModel = mode.LocalPostProcessingModel,
            EnableScreenOCR = mode.EnableScreenOCR,
            CustomVocabulary = mode.CustomVocabulary,
            IsSystemProvided = mode.IsSystemProvided,
            CreatedDate = mode.CreatedDate,
            ModifiedDate = mode.ModifiedDate
        };

        var winJson = JsonSerializer.SerializeToElement(winExt, CamelCaseOptions);
        universal.PlatformExtensions = new Dictionary<string, JsonElement>
        {
            ["windows"] = winJson
        };

        // Re-attach any preserved foreign (non-Windows) per-mode slices captured on
        // a prior v2 import (H4) so e.g. a macOS mode's per-mode data survives a
        // Windows round-trip. Our own "windows" slice always wins.
        if (!string.IsNullOrWhiteSpace(mode.ForeignPlatformExtensions))
        {
            try
            {
                var foreign = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    mode.ForeignPlatformExtensions);
                if (foreign != null)
                {
                    foreach (var kvp in foreign)
                    {
                        if (kvp.Key == "windows") continue;
                        universal.PlatformExtensions[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn(
                    $"UniversalBackupMapper: Failed to merge foreign platform extensions for '{mode.Name}': {ex.Message}");
            }
        }

        return universal;
    }

    /// <summary>
    /// Maps a Windows VocabularyItem to a universal vocabulary item (drops CreatedDate).
    /// </summary>
    public static UniversalVocabularyItem MapVocabularyItem(VocabularyItem item)
    {
        return new UniversalVocabularyItem
        {
            Id = item.Id,
            Word = item.Word,
            Replacement = item.Replacement,
            SortOrder = item.SortOrder,
            Source = item.Source
        };
    }

    /// <summary>
    /// Reads all API keys from ApiKeyService and maps to universal format.
    /// </summary>
    public static UniversalApiKeys MapApiKeys(ApiKeyService apiKeyService)
    {
        return new UniversalApiKeys
        {
            OpenAI = apiKeyService.GetApiKey(PostProcessingProvider.OpenAI),
            Anthropic = apiKeyService.GetApiKey(PostProcessingProvider.Anthropic),
            Groq = apiKeyService.GetApiKey(PostProcessingProvider.Groq),
            Gemini = apiKeyService.GetApiKey(PostProcessingProvider.Gemini),
            Cerebras = apiKeyService.GetApiKey(PostProcessingProvider.Cerebras),
            // Fireworks removed — deprecated no-op backup field, never populated.
            Deepgram = apiKeyService.GetApiKey(TranscriptionApiKeyType.Deepgram),
            AssemblyAI = apiKeyService.GetApiKey(TranscriptionApiKeyType.AssemblyAI),
            ElevenLabs = apiKeyService.GetApiKey(TranscriptionApiKeyType.ElevenLabs),
            Mistral = apiKeyService.GetApiKey(TranscriptionApiKeyType.Mistral),
            Soniox = apiKeyService.GetApiKey(TranscriptionApiKeyType.Soniox),
            Grok = apiKeyService.GetApiKey(PostProcessingProvider.Grok)
        };
    }

    /// <summary>
    /// Builds the top-level platformExtensions.windows object with Windows-specific settings.
    /// </summary>
    public static Dictionary<string, JsonElement> BuildPlatformExtensions(SettingsService settings)
    {
        var result = new Dictionary<string, JsonElement>();

        // Build Windows-specific settings
        var winSettings = new WindowsSettingsExtensions
        {
            MinimizeToTray = settings.MinimizeToTray,
            HideFromClipboardHistory = settings.HideFromClipboardHistory,
            ThemeMode = (int)settings.ThemeMode,
            AutoDeleteEnabled = settings.AutoDeleteEnabled,
            AutoDeleteDaysOld = settings.AutoDeleteDaysOld,
            ParakeetEnabled = settings.ParakeetEnabled,
            KeepMicrophoneWarm = settings.KeepMicrophoneWarm,
            MediaControlMode = settings.MediaControlMode,
            ToggleShortcut = settings.ToggleShortcut.ToPersistedString(),
            CancelShortcut = settings.CancelShortcut.ToPersistedString(),
            ChangeModeShortcut = settings.ChangeModeShortcut.ToPersistedString(),
            StreamingShortcut = settings.StreamingShortcut.ToPersistedString(),
            StreamingEnabled = settings.StreamingEnabled,
            StreamingProvider = settings.StreamingProvider,
            StreamingLanguage = settings.StreamingLanguage,
            StreamingDeepgramModel = settings.StreamingDeepgramModel,
            StreamingFastFormatting = settings.StreamingFastFormatting,
            AutoIncreaseMicVolume = settings.AutoIncreaseMicVolume,
            AutocapitalizeInsert = settings.AutocapitalizeInsert,
            CustomEndpoints = settings.CustomEndpoints
        };

        var settingsJson = JsonSerializer.SerializeToElement(winSettings, CamelCaseOptions);
        var windowsObj = new Dictionary<string, JsonElement>
        {
            ["settings"] = settingsJson
        };
        result["windows"] = JsonSerializer.SerializeToElement(windowsObj);

        return result;
    }

    // =========================================================================
    // IMPORT: Universal → Windows
    // =========================================================================

    /// <summary>
    /// Applies universal settings to SettingsService (cross-platform settings only).
    /// </summary>
    public static void ApplySettings(UniversalSettings universalSettings, SettingsService settings)
    {
        if (universalSettings.General != null)
        {
            var g = universalSettings.General;
            if (g.LaunchMinimized.HasValue) settings.LaunchMinimized = g.LaunchMinimized.Value;
            if (g.ShowRecordingWindow.HasValue) settings.ShowRecordingWindow = g.ShowRecordingWindow.Value;
            if (g.CheckForUpdatesAutomatically.HasValue) settings.CheckForUpdatesAutomatically = g.CheckForUpdatesAutomatically.Value;
            if (g.EnableErrorLogging.HasValue) settings.EnableErrorLogging = g.EnableErrorLogging.Value;
            if (g.EnableSoundEffects.HasValue) settings.EnableSoundEffects = g.EnableSoundEffects.Value;
        }

        if (universalSettings.TextOutput != null)
        {
            var t = universalSettings.TextOutput;
            if (t.PasteResultText.HasValue) settings.AutoPasteEnabled = t.PasteResultText.Value;
            if (t.RemoveFillerWords.HasValue) settings.RemoveFillerWords = t.RemoveFillerWords.Value;
            if (t.RestoreClipboardAfterPaste.HasValue) settings.RestoreClipboardAfterPaste = t.RestoreClipboardAfterPaste.Value;
            if (t.HideFromClipboardHistory.HasValue) settings.HideFromClipboardHistory = t.HideFromClipboardHistory.Value;
            if (t.ClipboardRestoreDelaySeconds.HasValue) settings.ClipboardRestoreDelaySeconds = t.ClipboardRestoreDelaySeconds.Value;
            if (t.AutocapitalizeInsert.HasValue) settings.AutocapitalizeInsert = t.AutocapitalizeInsert.Value;
            if (t.RemoveFillerWords.HasValue) settings.RemoveFillerWords = t.RemoveFillerWords.Value;
        }

        if (universalSettings.Storage != null)
        {
            var s = universalSettings.Storage;
            if (s.StoreAsM4A.HasValue) settings.StoreAsM4A = s.StoreAsM4A.Value;
        }

        if (universalSettings.Streaming != null)
        {
            var s = universalSettings.Streaming;
            if (s.Enabled.HasValue) settings.StreamingEnabled = s.Enabled.Value;
            if (!string.IsNullOrWhiteSpace(s.Provider)) settings.StreamingProvider = s.Provider;
            if (!string.IsNullOrWhiteSpace(s.Language)) settings.StreamingLanguage = s.Language;
            if (!string.IsNullOrWhiteSpace(s.DeepgramModel)) settings.StreamingDeepgramModel = s.DeepgramModel;
            if (s.FastFormatting.HasValue) settings.StreamingFastFormatting = s.FastFormatting.Value;
            if (!string.IsNullOrWhiteSpace(s.Shortcut))
                settings.StreamingShortcut = KeyboardShortcut.FromPersistedString(s.Shortcut);
        }

        if (universalSettings.Advanced != null)
        {
            var a = universalSettings.Advanced;
            if (a.TypingSpeedWPM.HasValue) settings.TypingSpeedWPM = a.TypingSpeedWPM.Value;
        }
    }

    /// <summary>
    /// Applies Windows-specific settings from platformExtensions.windows.settings.
    /// </summary>
    public static void ApplyWindowsPlatformSettings(
        Dictionary<string, JsonElement>? platformExtensions,
        SettingsService settings,
        bool replaceExisting = false)
    {
        if (platformExtensions == null) return;
        if (!platformExtensions.TryGetValue("windows", out var windowsElement)) return;

        try
        {
            if (windowsElement.TryGetProperty("settings", out var settingsElement))
            {
                var winSettings = JsonSerializer.Deserialize<WindowsSettingsExtensions>(
                    settingsElement.GetRawText(), CamelCaseOptions);

                if (winSettings == null) return;

                if (winSettings.MinimizeToTray.HasValue) settings.MinimizeToTray = winSettings.MinimizeToTray.Value;
                if (winSettings.HideFromClipboardHistory.HasValue) settings.HideFromClipboardHistory = winSettings.HideFromClipboardHistory.Value;
                if (winSettings.ThemeMode.HasValue) settings.ThemeMode = (ThemeMode)winSettings.ThemeMode.Value;
                if (winSettings.AutoDeleteEnabled.HasValue) settings.AutoDeleteEnabled = winSettings.AutoDeleteEnabled.Value;
                if (winSettings.AutoDeleteDaysOld.HasValue) settings.AutoDeleteDaysOld = winSettings.AutoDeleteDaysOld.Value;
                if (winSettings.ParakeetEnabled.HasValue) settings.ParakeetEnabled = winSettings.ParakeetEnabled.Value;
                if (winSettings.KeepMicrophoneWarm.HasValue) settings.KeepMicrophoneWarm = winSettings.KeepMicrophoneWarm.Value;
                if (!string.IsNullOrEmpty(winSettings.MediaControlMode)) settings.MediaControlMode = winSettings.MediaControlMode;

                if (!string.IsNullOrEmpty(winSettings.ToggleShortcut))
                    settings.ToggleShortcut = KeyboardShortcut.FromPersistedString(winSettings.ToggleShortcut);
                if (!string.IsNullOrEmpty(winSettings.CancelShortcut))
                    settings.CancelShortcut = KeyboardShortcut.FromPersistedString(winSettings.CancelShortcut);
                if (!string.IsNullOrEmpty(winSettings.ChangeModeShortcut))
                    settings.ChangeModeShortcut = KeyboardShortcut.FromPersistedString(winSettings.ChangeModeShortcut);
                if (!string.IsNullOrEmpty(winSettings.StreamingShortcut))
                    settings.StreamingShortcut = KeyboardShortcut.FromPersistedString(winSettings.StreamingShortcut);

                if (winSettings.StreamingEnabled.HasValue) settings.StreamingEnabled = winSettings.StreamingEnabled.Value;
                if (!string.IsNullOrEmpty(winSettings.StreamingProvider)) settings.StreamingProvider = winSettings.StreamingProvider;
                if (!string.IsNullOrEmpty(winSettings.StreamingLanguage)) settings.StreamingLanguage = winSettings.StreamingLanguage;
                if (!string.IsNullOrEmpty(winSettings.StreamingDeepgramModel)) settings.StreamingDeepgramModel = winSettings.StreamingDeepgramModel;
                if (winSettings.StreamingFastFormatting.HasValue) settings.StreamingFastFormatting = winSettings.StreamingFastFormatting.Value;

                if (winSettings.AutoIncreaseMicVolume.HasValue) settings.AutoIncreaseMicVolume = winSettings.AutoIncreaseMicVolume.Value;
                if (winSettings.AutocapitalizeInsert.HasValue) settings.AutocapitalizeInsert = winSettings.AutocapitalizeInsert.Value;
                var customEndpoints = ResolveCustomEndpointImport(
                    settings.CustomEndpoints,
                    winSettings.CustomEndpoints,
                    replaceExisting);
                if (customEndpoints != null) settings.CustomEndpoints = customEndpoints;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"UniversalBackupMapper: Failed to apply Windows platform settings: {ex.Message}");
        }
    }

    private static List<CustomPostProcessingEndpoint>? ResolveCustomEndpointImport(
        List<CustomPostProcessingEndpoint> existingEndpoints,
        List<CustomPostProcessingEndpoint>? importedEndpoints,
        bool replaceExisting)
    {
        if (importedEndpoints == null) return null;
        if (replaceExisting) return importedEndpoints;
        if (importedEndpoints.Count == 0) return null;

        return MergeCustomEndpoints(existingEndpoints, importedEndpoints);
    }

    private static List<CustomPostProcessingEndpoint> MergeCustomEndpoints(
        List<CustomPostProcessingEndpoint> existingEndpoints,
        List<CustomPostProcessingEndpoint> importedEndpoints)
    {
        var mergedEndpoints = new List<CustomPostProcessingEndpoint>(existingEndpoints);
        var indexById = new Dictionary<Guid, int>();

        for (var i = 0; i < mergedEndpoints.Count; i++)
        {
            indexById[mergedEndpoints[i].Id] = i;
        }

        foreach (var importedEndpoint in importedEndpoints)
        {
            if (indexById.TryGetValue(importedEndpoint.Id, out var existingIndex))
            {
                mergedEndpoints[existingIndex] = importedEndpoint;
                continue;
            }

            indexById[importedEndpoint.Id] = mergedEndpoints.Count;
            mergedEndpoints.Add(importedEndpoint);
        }

        return mergedEndpoints;
    }

    /// <summary>
    /// Maps a universal mode to a Windows Mode entity.
    /// Extracts platformExtensions.windows if present, otherwise applies defaults.
    /// </summary>
    public static Mode MapToMode(UniversalMode universal)
    {
        var normalizedCloudProvider = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared
            .NormalizeCloudProvider(universal.CloudProvider);

        var mode = new Mode
        {
            Id = universal.Id,
            Name = universal.Name,
            Preset = universal.Preset,
            Language = universal.Language,
            Model = universal.Model,
            IsDefault = universal.IsDefault,
            SortOrder = universal.SortOrder,
            Punctuation = universal.Punctuation,
            Capitalization = universal.Capitalization,
            ProfanityFilter = universal.ProfanityFilter,
            RemoveTrailingPeriod = universal.RemoveTrailingPeriod ?? false,
            EnglishSpelling = universal.EnglishSpelling,
            CloudProvider = normalizedCloudProvider.Provider,
            // Resolve legacy provider-specific model IDs so older backups import onto current models.
            CloudTranscriptionModel = universal.CloudTranscriptionModel is { } legacyModel
                ? CloudTranscriptionModels.ResolveModelAlias(
                    legacyModel,
                    CloudTranscriptionProviderExtensions.FromIdentifier(universal.CloudProvider))
                : null,
            // Domain (X-STT-Domain) only applies to HyperWhisper Cloud modes; gate
            // it so a stale domain on a BYOK mode isn't imported incorrectly.
            CloudTranscriptionDomain = normalizedCloudProvider.Provider == "hyperwhisper"
                ? universal.CloudTranscriptionDomain
                : null,
            PostProcessingMode = universal.PostProcessingMode,
            PostProcessingProvider = PostProcessingProviderExtensions.NormalizeStorageValue(universal.PostProcessingProvider),
            LanguageModel = universal.LanguageModel,
            LocalPostProcessingModel = universal.LocalPostProcessingModel,
            UserSystemPrompt = universal.UserSystemPrompt,
            CustomInstructions = universal.CustomInstructions,
            GeminiCustomPrompt = universal.GeminiCustomPrompt,
            // TODO-verify (Windows/CI): Rust shared-core swap — present-only migration.
            // Precedence preserved: the catalog-normalized provider tier wins; otherwise
            // migrate the universal value via the core ONLY when present; otherwise keep
            // the Mode entity's own default (non-null field) rather than forcing the core
            // default where the field was intentionally absent.
            CloudAccuracyTier = normalizedCloudProvider.AccuracyTier
                ?? MigrateCloudAccuracyTierPresent(universal.CloudAccuracyTier)
                ?? ModeDefaults.CloudAccuracyTier,
            CloudPostProcessingModel = MigrateCloudPpModelPresent(universal.CloudPostProcessingModel)
                ?? ModeDefaults.CloudPostProcessingModel
        };

        // Try to extract Windows-specific fields from platformExtensions
        WindowsModeExtensions? winExt = null;
        if (universal.PlatformExtensions != null &&
            universal.PlatformExtensions.TryGetValue("windows", out var windowsElement))
        {
            try
            {
                winExt = JsonSerializer.Deserialize<WindowsModeExtensions>(
                    windowsElement.GetRawText(), CamelCaseOptions);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"UniversalBackupMapper: Failed to deserialize Windows mode extensions for '{universal.Name}': {ex.Message}");
            }
        }

        if (winExt != null)
        {
            // Windows export — use stored values
            mode.ModelType = winExt.ModelType;
            mode.LocalEngine = winExt.LocalEngine ?? "whisper";
            mode.LocalParakeetModel = winExt.LocalParakeetModel;
            mode.ProviderType = winExt.ProviderType;
            // TODO-verify (Windows/CI): Rust shared-core swap — present-only migration.
            // Only migrate the Windows-extension value when it is present; otherwise keep
            // the value already set above from the universal section (do not overwrite a
            // present universal value with the core default).
            mode.CloudAccuracyTier =
                MigrateCloudAccuracyTierPresent(winExt.CloudAccuracyTier) ?? mode.CloudAccuracyTier;
            mode.CloudPostProcessingModel =
                MigrateCloudPpModelPresent(winExt.CloudPostProcessingModel) ?? mode.CloudPostProcessingModel;
            mode.LocalPostProcessingModel = winExt.LocalPostProcessingModel ?? mode.LocalPostProcessingModel;
            mode.EnableScreenOCR = winExt.EnableScreenOCR ?? false;
            mode.CustomVocabulary = winExt.CustomVocabulary;
            mode.IsSystemProvided = winExt.IsSystemProvided ?? false;
            mode.CreatedDate = winExt.CreatedDate ?? DateTime.UtcNow;
            mode.ModifiedDate = winExt.ModifiedDate ?? DateTime.UtcNow;
        }
        else
        {
            // macOS or other platform export — apply sensible defaults
            mode.ModelType = universal.Model;
            mode.LocalEngine = "whisper";
            mode.LocalParakeetModel = null;
            mode.ProviderType = !string.IsNullOrEmpty(universal.CloudProvider) ? "cloud" : "local";
            // TODO-verify (Windows/CI): Rust shared-core swap — present-only migration.
            // Migrate the universal value via the core ONLY when present; otherwise keep
            // the value already set in the object initializer (which used the same
            // universal.* source) rather than forcing the core default.
            mode.CloudAccuracyTier =
                MigrateCloudAccuracyTierPresent(universal.CloudAccuracyTier) ?? mode.CloudAccuracyTier;
            mode.CloudPostProcessingModel =
                MigrateCloudPpModelPresent(universal.CloudPostProcessingModel) ?? mode.CloudPostProcessingModel;
            mode.EnableScreenOCR = false;
            mode.CustomVocabulary = null;
            mode.IsSystemProvided = false;
            mode.CreatedDate = DateTime.UtcNow;
            mode.ModifiedDate = DateTime.UtcNow;
        }

        // Preserve every NON-Windows per-mode platformExtensions slice (e.g. the
        // macos blob) verbatim so it survives a Windows round-trip (H4). Stored as
        // raw JSON on the entity; MapMode re-emits it on the next export.
        if (universal.PlatformExtensions != null)
        {
            var foreign = new Dictionary<string, JsonElement>();
            foreach (var kvp in universal.PlatformExtensions)
            {
                if (kvp.Key == "windows") continue;
                foreign[kvp.Key] = kvp.Value;
            }
            if (foreign.Count > 0)
            {
                mode.ForeignPlatformExtensions = JsonSerializer.Serialize(foreign);
            }
        }

        return mode;
    }

    /// <summary>
    /// Maps a universal vocabulary item to a Windows VocabularyItem (adds CreatedDate).
    /// </summary>
    public static VocabularyItem MapToVocabularyItem(UniversalVocabularyItem universal)
    {
        return new VocabularyItem
        {
            Id = universal.Id,
            Word = universal.Word,
            Replacement = universal.Replacement,
            SortOrder = universal.SortOrder,
            Source = universal.Source,
            CreatedDate = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Writes universal API keys to ApiKeyService (PasswordVault).
    /// Only writes non-null keys; does not clear existing keys that aren't in the backup.
    /// </summary>
    public static void ApplyApiKeys(UniversalApiKeys apiKeys, ApiKeyService apiKeyService)
    {
        if (!string.IsNullOrEmpty(apiKeys.OpenAI)) apiKeyService.SetApiKey(PostProcessingProvider.OpenAI, apiKeys.OpenAI);
        if (!string.IsNullOrEmpty(apiKeys.Anthropic)) apiKeyService.SetApiKey(PostProcessingProvider.Anthropic, apiKeys.Anthropic);
        if (!string.IsNullOrEmpty(apiKeys.Groq)) apiKeyService.SetApiKey(PostProcessingProvider.Groq, apiKeys.Groq);
        if (!string.IsNullOrEmpty(apiKeys.Gemini)) apiKeyService.SetApiKey(PostProcessingProvider.Gemini, apiKeys.Gemini);
        if (!string.IsNullOrEmpty(apiKeys.Cerebras)) apiKeyService.SetApiKey(PostProcessingProvider.Cerebras, apiKeys.Cerebras);
        // Fireworks removed — deprecated no-op backup field, never applied on restore.
        if (!string.IsNullOrEmpty(apiKeys.Deepgram)) apiKeyService.SetApiKey(TranscriptionApiKeyType.Deepgram, apiKeys.Deepgram);
        if (!string.IsNullOrEmpty(apiKeys.AssemblyAI)) apiKeyService.SetApiKey(TranscriptionApiKeyType.AssemblyAI, apiKeys.AssemblyAI);
        if (!string.IsNullOrEmpty(apiKeys.ElevenLabs)) apiKeyService.SetApiKey(TranscriptionApiKeyType.ElevenLabs, apiKeys.ElevenLabs);
        if (!string.IsNullOrEmpty(apiKeys.Mistral)) apiKeyService.SetApiKey(TranscriptionApiKeyType.Mistral, apiKeys.Mistral);
        if (!string.IsNullOrEmpty(apiKeys.Soniox)) apiKeyService.SetApiKey(TranscriptionApiKeyType.Soniox, apiKeys.Soniox);
        if (!string.IsNullOrEmpty(apiKeys.Grok)) apiKeyService.SetApiKey(PostProcessingProvider.Grok, apiKeys.Grok);
    }
}
