using System.Text.Json;
using System.Text.Json.Nodes;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
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
    // SHARED-CORE MODE NORMALIZATION
    // =========================================================================

    /// <summary>
    /// Canonicalize an imported mode's five cloud-routing fields in the Rust
    /// shared core (<c>normalize_universal_mode_json</c>): the cloudProvider
    /// catalog fold, the legacy model-alias tables, the present-only
    /// cloudAccuracyTier / cloudPostProcessingModel migration including the
    /// platformExtensions.windows override, and the cloudTranscriptionDomain
    /// gate. Linux's importer calls the same function, so both heads agree.
    /// </summary>
    /// <remarks>
    /// PRESENT-ONLY: the core returns a field ABSENT (null here) when no source
    /// supplied one, so the caller keeps the Mode entity's own default instead of
    /// the core's. Only the five cloud-routing properties of the result are read;
    /// every other field is taken from the original DTO.
    /// </remarks>
    private static UniversalMode NormalizeCloudRouting(UniversalMode universal)
    {
        try
        {
            var json = HyperwhisperCoreMethods.NormalizeUniversalModeJson(
                JsonSerializer.Serialize(universal, CamelCaseOptions));
            return JsonSerializer.Deserialize<UniversalMode>(json, CamelCaseOptions) ?? universal;
        }
        catch (Exception ex)
        {
            // A malformed mode must not abort the whole restore; fall back to the
            // raw values, which is what an un-normalized import used to do.
            LoggingService.Warn(
                $"UniversalBackupMapper: cloud-routing normalization failed for '{universal.Name}': {ex.Message}");
            return universal;
        }
    }

    // PRESENT-ONLY storage-string migrations, still used by the EXPORT half
    // (MapMode). The IMPORT half no longer needs them — NormalizeCloudRouting
    // above does the whole job in one core call — but on export there is no
    // universal mode to hand the core, only a Mode entity, so the two scalar
    // migrations are called directly.
    //
    // Present-only: a null/whitespace source returns null so the caller keeps
    // whatever default it already had, rather than letting the core write its own
    // default where the field was intentionally absent.
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

    // Entity-side defaults for an absent field (read from the Mode entity so the
    // canonical default lives in exactly one place).
    private static readonly Mode ModeDefaults = new();

    // =========================================================================
    // EXPORT: Windows → Universal
    // =========================================================================

    /// <summary>
    /// Maps current Windows settings to universal settings format, through the
    /// shared core's Windows settings adapter
    /// (<c>hw-backup mapping.rs: windows_settings_to_universal</c>).
    /// </summary>
    /// <remarks>
    /// The (native, universal) renames — <c>textOutput.pasteResultText</c> ←
    /// <c>AutoPasteEnabled</c>, and all six <c>Streaming*</c> keys — live in the
    /// core's <c>WINDOWS_*_PAIRS</c> tables, so Windows, Linux and macOS answer
    /// from one map. The core emits the five universal categories and nothing
    /// else; <see cref="BuildPlatformExtensions"/> is still the only thing that
    /// writes <c>platformExtensions.windows</c>, and it stays a CURATED list.
    /// </remarks>
    public static UniversalSettings MapSettings(SettingsService settings)
    {
        var universalJson = HyperwhisperCoreMethods.WindowsSettingsToUniversalSettingsJson(
            settings.BuildBackupSettingsSnapshot());
        return JsonSerializer.Deserialize<UniversalSettings>(universalJson, CamelCaseOptions)
            ?? new UniversalSettings();
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
    /// <remarks>
    /// <para>
    /// The universal→native RENAME is the shared core's job
    /// (<c>hw-backup mapping.rs: universal_to_windows_settings</c>), so this
    /// method reads a NATIVE-shaped, PascalCase blob. The setter chain itself
    /// cannot shrink and does not: <see cref="SettingsService"/> is a typed facade
    /// whose setters dirty-check, <c>Save()</c> under a lock and raise
    /// <c>SettingsChanged</c> (which re-registers global shortcuts). There is no
    /// Windows analogue of Linux's <c>PortableSettingsService.Replace</c>.
    /// </para>
    /// <para>
    /// The VALUE rewrites stay native too, and stay in the setters where they
    /// already were: <c>StreamingProvider</c> falls back to
    /// <c>hyperwhisperCloud</c> for an unknown value, <c>StreamingDeepgramModel</c>
    /// collapses to <c>nova-3-general</c>, <c>StreamingShortcut</c> is
    /// re-canonicalised through <c>FromPersistedString</c>/<c>ToPersistedString</c>
    /// and <c>ClipboardRestoreDelaySeconds</c> is clamped to [1, 60]. The core
    /// renames and regroups; it never interprets.
    /// </para>
    /// </remarks>
    public static void ApplySettings(UniversalSettings universalSettings, SettingsService settings)
    {
        var native = BuildNativeSettings(universalSettings, settings);
        if (native is null) return;

        using var document = JsonDocument.Parse(native.ToJsonString());
        var n = document.RootElement;

        // general
        if (TryBool(n, "LaunchMinimized", out var launchMinimized)) settings.LaunchMinimized = launchMinimized;
        if (TryBool(n, "ShowRecordingWindow", out var showRecordingWindow)) settings.ShowRecordingWindow = showRecordingWindow;
        if (TryBool(n, "CheckForUpdatesAutomatically", out var checkForUpdates)) settings.CheckForUpdatesAutomatically = checkForUpdates;
        if (TryBool(n, "EnableErrorLogging", out var enableErrorLogging)) settings.EnableErrorLogging = enableErrorLogging;
        // Absent means the backup predates the setting — keep what the user has.
        if (TryBool(n, "ShareAnonymousSpeedData", out var shareSpeedData)) settings.ShareAnonymousSpeedData = shareSpeedData;
        if (TryBool(n, "EnableSoundEffects", out var enableSoundEffects)) settings.EnableSoundEffects = enableSoundEffects;

        // textOutput
        if (TryBool(n, "AutoPasteEnabled", out var autoPaste)) settings.AutoPasteEnabled = autoPaste;
        if (TryBool(n, "RemoveFillerWords", out var removeFillerWords)) settings.RemoveFillerWords = removeFillerWords;
        if (TryBool(n, "RestoreClipboardAfterPaste", out var restoreClipboard)) settings.RestoreClipboardAfterPaste = restoreClipboard;
        if (TryBool(n, "HideFromClipboardHistory", out var hideFromHistory)) settings.HideFromClipboardHistory = hideFromHistory;
        if (TryDouble(n, "ClipboardRestoreDelaySeconds", out var clipboardDelay)) settings.ClipboardRestoreDelaySeconds = clipboardDelay;
        if (TryBool(n, "AutocapitalizeInsert", out var autocapitalize)) settings.AutocapitalizeInsert = autocapitalize;

        // storage
        if (TryBool(n, "StoreAsM4A", out var storeAsM4A)) settings.StoreAsM4A = storeAsM4A;

        // streaming — the four string arms are whitespace-gated, the two bool arms are not
        if (TryBool(n, "StreamingEnabled", out var streamingEnabled)) settings.StreamingEnabled = streamingEnabled;
        if (TryNonBlankString(n, "StreamingProvider", out var streamingProvider)) settings.StreamingProvider = streamingProvider;
        if (TryNonBlankString(n, "StreamingLanguage", out var streamingLanguage)) settings.StreamingLanguage = streamingLanguage;
        if (TryNonBlankString(n, "StreamingDeepgramModel", out var deepgramModel)) settings.StreamingDeepgramModel = deepgramModel;
        if (TryBool(n, "StreamingFastFormatting", out var fastFormatting)) settings.StreamingFastFormatting = fastFormatting;
        if (TryNonBlankString(n, "StreamingShortcut", out var streamingShortcut))
            settings.StreamingShortcut = KeyboardShortcut.FromPersistedString(streamingShortcut);

        // advanced
        if (TryInt(n, "TypingSpeedWPM", out var typingSpeedWPM)) settings.TypingSpeedWPM = typingSpeedWPM;
    }

    /// <summary>
    /// Converts <paramref name="universalSettings"/> to the native Windows shape in
    /// the shared core, then DEEP-MERGES the result over a baseline snapshot of the
    /// live settings, mirroring <c>BackupManager.swift</c>'s
    /// <c>currentSettingsBaseline()</c> → <c>deepMerged(over:)</c> → apply.
    /// Returns <c>null</c> when the core could not answer, which leaves every live
    /// setting untouched.
    /// </summary>
    /// <remarks>
    /// The merge is currently INERT by construction: the core is present-only, so
    /// every key it omits is filled from the baseline, and a baseline value written
    /// back through its own setter fails that setter's dirty-check and does nothing
    /// (<c>SettingsService.ApplyDefaults</c> materialises the four streaming fields
    /// whose setters compare the raw stored value, so even those are inert). It is
    /// wired anyway because it is the defence the day the core returns a COMPLETE
    /// native blob — at that point an absent backup key would otherwise arrive as a
    /// core default and clobber a live setting. <c>SmokeTests</c> asserts the
    /// no-op, so the wiring cannot rot unnoticed.
    /// </remarks>
    private static JsonObject? BuildNativeSettings(
        UniversalSettings universalSettings, SettingsService settings)
    {
        try
        {
            var baseline = JsonNode.Parse(settings.BuildBackupSettingsSnapshot())?.AsObject();
            if (baseline is null) return null;

            // WhenWritingNull means an explicit JSON null never reaches the core,
            // which is what makes null behave exactly like an absent key.
            var universalJson = JsonSerializer.Serialize(universalSettings, CamelCaseOptions);
            var fromCore = JsonNode.Parse(
                HyperwhisperCoreMethods.UniversalSettingsToWindowsSettingsJson(universalJson))?.AsObject();
            if (fromCore is null) return baseline;

            return DeepMerge(baseline, fromCore);
        }
        catch (Exception ex)
        {
            // A malformed settings block must not abort the whole restore.
            LoggingService.Warn($"UniversalBackupMapper: settings conversion failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="overlay"/> deep-merged over <paramref name="baseline"/>:
    /// overlay wins per key, nested objects merge recursively, and a key only in
    /// the baseline survives. Mirrors <c>BackupManager.swift</c>'s
    /// <c>deepMerged(over:)</c>. The blobs are flat today; the recursion is kept so
    /// the semantics stay right if a nested shape ever crosses.
    /// </summary>
    private static JsonObject DeepMerge(JsonObject baseline, JsonObject overlay)
    {
        var merged = baseline.DeepClone().AsObject();
        foreach (var entry in overlay)
        {
            if (entry.Value is JsonObject nestedOverlay
                && merged[entry.Key] is JsonObject nestedBaseline)
            {
                merged[entry.Key] = DeepMerge(nestedBaseline, nestedOverlay);
                continue;
            }
            merged[entry.Key] = entry.Value?.DeepClone();
        }
        return merged;
    }

    private static bool TryBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryNonBlankString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind != JsonValueKind.String) return false;
        var raw = property.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return false;
        value = raw;
        return true;
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
        var normalized = NormalizeCloudRouting(universal);

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
            // The cloudProvider fold, the legacy model-alias resolution and the
            // cloudTranscriptionDomain gate all happen inside the shared core now.
            CloudProvider = normalized.CloudProvider,
            CloudTranscriptionModel = normalized.CloudTranscriptionModel,
            CloudTranscriptionDomain = normalized.CloudTranscriptionDomain,
            PostProcessingMode = universal.PostProcessingMode,
            PostProcessingProvider = PostProcessingProviderExtensions.NormalizeStorageValue(universal.PostProcessingProvider),
            LanguageModel = universal.LanguageModel,
            LocalPostProcessingModel = universal.LocalPostProcessingModel,
            UserSystemPrompt = universal.UserSystemPrompt,
            CustomInstructions = universal.CustomInstructions,
            GeminiCustomPrompt = universal.GeminiCustomPrompt,
            // The core already applied the full precedence chain, including the
            // platformExtensions.windows override. It returns the field ABSENT when
            // no source supplied one, so the Mode entity's own default applies here
            // rather than the core's (deepgramNova3 / grok:grok-4.3), which is not
            // what either .NET head ships.
            CloudAccuracyTier = normalized.CloudAccuracyTier ?? ModeDefaults.CloudAccuracyTier,
            CloudPostProcessingModel =
                normalized.CloudPostProcessingModel ?? ModeDefaults.CloudPostProcessingModel
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
            // winExt.CloudAccuracyTier / .CloudPostProcessingModel are NOT read here:
            // the core already folded them in with the right precedence (a present
            // Windows-extension value wins over the universal one, an absent one keeps
            // whatever the universal section and the provider fold produced).
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
            // Tier / post-processing model already resolved by the core above.
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
