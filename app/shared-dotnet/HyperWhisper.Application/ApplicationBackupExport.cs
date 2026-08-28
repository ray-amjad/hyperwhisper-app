using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed partial class ApplicationBackupService(
    ApplicationDb database,
    PortableSettingsService settings,
    ICredentialStore? credentialStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly PortableSettingsService _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ICredentialStore? _credentialStore = credentialStore;

    public Task<string> ExportAsync(CancellationToken cancellationToken = default)
        => ExportAsync(BackupExportSelection.All, cancellationToken);

    public async Task<string> ExportAsync(
        BackupExportSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.IncludeModes && selection.SelectedModeIds is not null)
            throw new ArgumentException("Selected mode IDs require mode export to be enabled.", nameof(selection));
        await using var context = _database.CreateContext();
        var modes = selection.IncludeModes
            ? await context.Modes.AsNoTracking().OrderBy(item => item.SortOrder).ToListAsync(cancellationToken)
            : [];
        if (selection.SelectedModeIds is { } selectedModeIds)
        {
            var availableIds = modes.Select(item => item.Id).ToHashSet();
            if (selectedModeIds.Any(id => !availableIds.Contains(id)))
                throw new ArgumentException("A selected mode does not exist.", nameof(selection));
            modes = modes.Where(item => selectedModeIds.Contains(item.Id)).ToList();
        }
        var vocabulary = selection.IncludeVocabulary
            ? await context.VocabularyItems.AsNoTracking().OrderBy(item => item.SortOrder).ToListAsync(cancellationToken)
            : [];
        var platformExtensions = ReadObject(_settings.Get<JsonElement?>("backup.platformExtensions")) ?? new JsonObject();
        var linuxExtension = platformExtensions["linux"]?.DeepClone() as JsonObject ?? new JsonObject();
        var linuxSettings = linuxExtension["settings"]?.DeepClone() as JsonObject ?? new JsonObject();
        ApplyLinuxSettings(linuxSettings);
        linuxExtension["settings"] = linuxSettings;
        platformExtensions["linux"] = linuxExtension;
        var root = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["exportDate"] = DateTimeOffset.UtcNow.ToString("O"),
            ["appVersion"] = "1.0.0",
            ["platform"] = "linux",
        };
        if (selection.IncludeSettings)
        {
            root["settings"] = BuildSharedSettings();
            root["platformExtensions"] = platformExtensions;
        }
        if (selection.IncludeModes)
        {
            var modeNodes = modes.Select(ToUniversalMode).ToArray();
            if (modeNodes.Length > 0 && !modeNodes.Any(item => item["isDefault"]?.GetValue<bool>() == true))
                modeNodes[0]["isDefault"] = true;
            root["modes"] = new JsonArray(modeNodes);
        }
        if (selection.IncludeVocabulary)
            root["vocabulary"] = new JsonArray(vocabulary.Select(item => new JsonObject
            {
                ["id"] = item.Id.ToString("D"), ["word"] = item.Word,
                ["replacement"] = item.Replacement, ["sortOrder"] = item.SortOrder,
                ["source"] = "manual",
            }).ToArray());
        if (selection.IncludeCredentials)
            root["apiKeys"] = ExportCredentials();
        var json = root.ToJsonString(SerializerOptions);
        var failures = SharedCoreBridge.ValidateBackup(json);
        if (failures.Count != 0) throw new InvalidOperationException("The generated universal backup did not pass shared-core validation.");
        return json;
    }

    /// <summary>
    /// Writes the Linux platform extension settings into <paramref name="linuxSettings"/>.
    /// </summary>
    private void ApplyLinuxSettings(JsonObject linuxSettings)
    {
        linuxSettings["language"] = _settings.Get("language", "auto");
        linuxSettings["localWhisperBackend"] = _settings.Get("localWhisperBackend", "auto");
        linuxSettings["allowLocalWhisperCpuFallback"] = _settings.Get("allowLocalWhisperCpuFallback", true);
        linuxSettings["localLlmBackend"] = _settings.Get("localLlmBackend", "cpu");
        linuxSettings["allowLocalLlmCpuFallback"] = _settings.Get("allowLocalLlmCpuFallback", true);
        linuxSettings["localApiEnabled"] = _settings.Get("localApiEnabled", false);
        linuxSettings["localApiPort"] = _settings.Get("localApiPort", 51671);
        linuxSettings["autostartEnabled"] = _settings.Get("autostartEnabled", false);
        linuxSettings["toggleShortcutModifiers"] = _settings.Get("toggleShortcutModifiers", "Control, Alt");
        linuxSettings["toggleShortcutKey"] = _settings.Get("toggleShortcutKey", string.Empty);
        linuxSettings["cancelShortcutModifiers"] = _settings.Get("cancelShortcutModifiers", "None");
        linuxSettings["cancelShortcutKey"] = _settings.Get("cancelShortcutKey", "Escape");
        linuxSettings["changeModeShortcutModifiers"] = _settings.Get("changeModeShortcutModifiers", "Control, Shift");
        linuxSettings["changeModeShortcutKey"] = _settings.Get("changeModeShortcutKey", "Period");
        linuxSettings["streamingShortcutModifiers"] = _settings.Get("streamingShortcutModifiers", "Control, Shift");
        linuxSettings["streamingShortcutKey"] = _settings.Get("streamingShortcutKey", "Space");
        linuxSettings["pushToTalkMode"] = _settings.Get("pushToTalkMode", "Disabled");
        linuxSettings["pushToTalkModifier"] = _settings.Get("pushToTalkModifier", "LeftAlt");
        linuxSettings["pushToTalkShortcutModifiers"] = _settings.Get("pushToTalkShortcutModifiers", "None");
        linuxSettings["pushToTalkShortcutKey"] = _settings.Get("pushToTalkShortcutKey", string.Empty);
        linuxSettings["pushToTalkDoublePressLock"] = _settings.Get("pushToTalkDoublePressLock", false);
        linuxSettings["autoIncreaseMicVolume"] = _settings.Get("autoIncreaseMicVolume", false);
        linuxSettings["keepMicrophoneWarm"] = _settings.Get("keepMicrophoneWarm", false);
        linuxSettings["audioEnvironmentPolicy"] = _settings.Get("audioEnvironmentPolicy", "unchanged");
        linuxSettings["autoDeleteEnabled"] = _settings.Get("autoDeleteEnabled", false);
        linuxSettings["autoDeleteDaysOld"] = _settings.Get("autoDeleteDaysOld", 30);
        linuxSettings["enableVoiceActivityTrimming"] = _settings.Get("audio.enableVoiceActivityTrimming", true);
        linuxSettings["themeMode"] = _settings.Get("themeMode", "system");
        linuxSettings["minimizeToTray"] = _settings.Get("minimizeToTray", true);
        linuxSettings["soundEffectsVolume"] = _settings.Get("soundEffectsVolume", 1d);
        linuxSettings["customEndpoints"] = JsonSerializer.SerializeToNode(
            _settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []), SerializerOptions);
    }

    private void CopySetting<T>(JsonObject source, string key)
    {
        if (source[key] is not null) _settings.Set(key, source[key]!.GetValue<T>());
    }

    private JsonObject BuildSharedSettings() => new()
    {
        ["general"] = new JsonObject
        {
            ["launchMinimized"] = _settings.Get("general.launchMinimized", false),
            ["showRecordingWindow"] = _settings.Get("general.showRecordingWindow", true),
            ["checkForUpdatesAutomatically"] = _settings.Get("general.checkForUpdatesAutomatically", true),
            ["enableErrorLogging"] = _settings.Get("general.enableErrorLogging", true),
            ["shareAnonymousSpeedData"] = _settings.Get("general.shareAnonymousSpeedData", true),
            ["enableSoundEffects"] = _settings.Get("general.enableSoundEffects", true),
        },
        ["textOutput"] = new JsonObject
        {
            ["pasteResultText"] = _settings.Get("textOutput.pasteResultText", true),
            ["removeFillerWords"] = _settings.Get("textOutput.removeFillerWords", true),
            ["restoreClipboardAfterPaste"] = _settings.Get("textOutput.restoreClipboardAfterPaste", true),
            ["hideFromClipboardHistory"] = _settings.Get("textOutput.hideFromClipboardHistory", true),
            ["clipboardRestoreDelaySeconds"] = _settings.Get("textOutput.clipboardRestoreDelaySeconds", 10d),
            ["autocapitalizeInsert"] = _settings.Get("textOutput.autocapitalizeInsert", true),
            ["storeWordTimestamps"] = _settings.Get("textOutput.storeWordTimestamps", true),
        },
        ["storage"] = new JsonObject
        {
            ["keepAudioFiles"] = _settings.Get("storage.keepAudioFiles", true),
            ["storeAsM4A"] = _settings.Get("storage.storeAsM4A", false),
        },
        ["streaming"] = new JsonObject
        {
            ["enabled"] = _settings.Get("streaming.enabled", false),
            ["provider"] = _settings.Get<string?>("streaming.provider"),
            ["language"] = _settings.Get<string?>("streaming.language"),
            ["deepgramModel"] = _settings.Get<string?>("streaming.deepgramModel"),
            ["cloudTier"] = _settings.Get<string?>("streaming.cloudTier"),
            ["fastFormatting"] = _settings.Get("streaming.fastFormatting", false),
            ["shortcut"] = _settings.Get<string?>("streaming.shortcut"),
        },
        ["advanced"] = new JsonObject
        {
            ["maxRecordingDuration"] = _settings.Get("advanced.maxRecordingDuration", 3600),
            ["typingSpeedWPM"] = _settings.Get("advanced.typingSpeedWPM", 40),
        },
    };

    private void ApplySharedSettings(JsonObject settings)
    {
        CopyCategory(settings, "general", ["launchMinimized", "showRecordingWindow", "checkForUpdatesAutomatically", "enableErrorLogging", "shareAnonymousSpeedData", "enableSoundEffects"]);
        CopyCategory(settings, "textOutput", ["pasteResultText", "removeFillerWords", "restoreClipboardAfterPaste", "hideFromClipboardHistory", "clipboardRestoreDelaySeconds", "autocapitalizeInsert", "storeWordTimestamps"]);
        CopyCategory(settings, "storage", ["keepAudioFiles", "storeAsM4A"]);
        CopyCategory(settings, "streaming", ["enabled", "provider", "language", "deepgramModel", "cloudTier", "fastFormatting", "shortcut"]);
        CopyCategory(settings, "advanced", ["maxRecordingDuration", "typingSpeedWPM"]);
    }

    private void CopyCategory(JsonObject settings, string category, IReadOnlyList<string> keys)
    {
        if (settings[category] is not JsonObject source) return;
        foreach (var key in keys)
            if (source[key] is { } value) _settings.Set($"{category}.{key}", JsonSerializer.SerializeToElement(value, SerializerOptions));
    }

    private static JsonObject ToUniversalMode(Mode mode)
    {
        var extensions = ReadObject(mode.ForeignPlatformExtensions) ?? new JsonObject();
        var linux = extensions["linux"]?.DeepClone() as JsonObject ?? new JsonObject();
        linux["localEngine"] = mode.LocalEngine;
        linux["localParakeetModel"] = mode.LocalParakeetModel;
        linux["providerType"] = mode.ProviderType;
        linux["modelType"] = mode.ModelType;
        linux["enableScreenOCR"] = mode.EnableScreenOCR;
        linux["customVocabulary"] = mode.CustomVocabulary is null
            ? null
            : new JsonArray(mode.CustomVocabulary.Select(term => (JsonNode?)JsonValue.Create(term)).ToArray());
        linux["isSystemProvided"] = mode.IsSystemProvided;
        linux["createdDate"] = mode.CreatedDate.ToUniversalTime().ToString("O");
        linux["modifiedDate"] = mode.ModifiedDate.ToUniversalTime().ToString("O");
        extensions["linux"] = linux;
        return new JsonObject
        {
            ["id"] = mode.Id.ToString("D"), ["name"] = mode.Name, ["preset"] = mode.Preset,
            ["language"] = mode.Language, ["model"] = mode.Model, ["isDefault"] = mode.IsDefault,
            ["sortOrder"] = mode.SortOrder, ["punctuation"] = mode.Punctuation,
            ["capitalization"] = mode.Capitalization, ["profanityFilter"] = mode.ProfanityFilter,
            ["removeTrailingPeriod"] = mode.RemoveTrailingPeriod, ["englishSpelling"] = mode.EnglishSpelling,
            ["cloudProvider"] = mode.CloudProvider, ["cloudTranscriptionModel"] = mode.CloudTranscriptionModel,
            ["cloudTranscriptionDomain"] = mode.CloudTranscriptionDomain,
            ["postProcessingMode"] = mode.PostProcessingMode, ["postProcessingProvider"] = mode.PostProcessingProvider,
            ["languageModel"] = mode.LanguageModel, ["localPostProcessingModel"] = mode.LocalPostProcessingModel,
            ["userSystemPrompt"] = mode.UserSystemPrompt, ["customInstructions"] = mode.CustomInstructions,
            ["geminiCustomPrompt"] = mode.GeminiCustomPrompt, ["cloudAccuracyTier"] = mode.CloudAccuracyTier,
            ["cloudPostProcessingModel"] = mode.CloudPostProcessingModel,
            ["platformExtensions"] = extensions,
        };
    }

    private static Mode ParseMode(JsonNode? node)
    {
        var value = node as JsonObject ?? throw new JsonException("Mode must be an object.");
        var extensions = value["platformExtensions"] as JsonObject;
        var linux = extensions?["linux"] as JsonObject;
        var preservedExtensions = extensions?.DeepClone() as JsonObject;
        return new Mode
        {
            Id = Guid.Parse(value["id"]!.GetValue<string>()), Name = value["name"]!.GetValue<string>(),
            Preset = String(value, "preset") ?? "hyper", Language = String(value, "language") ?? "en",
            Model = String(value, "model") ?? "base", ModelType = String(linux, "modelType") ?? String(value, "model") ?? "base",
            IsDefault = Bool(value, "isDefault"), SortOrder = Int(value, "sortOrder"),
            Punctuation = Bool(value, "punctuation", true), Capitalization = Bool(value, "capitalization", true),
            ProfanityFilter = Bool(value, "profanityFilter"), RemoveTrailingPeriod = Bool(value, "removeTrailingPeriod"),
            EnglishSpelling = String(value, "englishSpelling"), CloudProvider = String(value, "cloudProvider"),
            CloudTranscriptionModel = String(value, "cloudTranscriptionModel"), CloudTranscriptionDomain = String(value, "cloudTranscriptionDomain"),
            PostProcessingMode = Int(value, "postProcessingMode"), PostProcessingProvider = String(value, "postProcessingProvider"),
            LanguageModel = String(value, "languageModel"), LocalPostProcessingModel = String(value, "localPostProcessingModel"),
            UserSystemPrompt = String(value, "userSystemPrompt"), CustomInstructions = String(value, "customInstructions"),
            GeminiCustomPrompt = String(value, "geminiCustomPrompt"), CloudAccuracyTier = RestoredCloudAccuracyTier(value),
            CloudPostProcessingModel = String(value, "cloudPostProcessingModel") ?? "anthropic:claude-haiku-4-5",
            LocalEngine = String(linux, "localEngine") ?? "whisper", LocalParakeetModel = String(linux, "localParakeetModel"),
            ProviderType = String(linux, "providerType") ?? (String(value, "cloudProvider") is null ? "local" : "cloud"),
            EnableScreenOCR = Bool(linux, "enableScreenOCR"), CustomVocabulary = StringList(linux, "customVocabulary"),
            IsSystemProvided = Bool(linux, "isSystemProvided"),
            ForeignPlatformExtensions = preservedExtensions is null || preservedExtensions.Count == 0 ? null : preservedExtensions.ToJsonString(),
            CreatedDate = Date(linux, "createdDate") ?? DateTime.UtcNow,
            ModifiedDate = Date(linux, "modifiedDate") ?? DateTime.UtcNow,
        };
    }

    /// <summary>
    /// The restored accuracy tier, canonicalised through the shared core's
    /// <c>migrate_cloud_accuracy_tier</c>.
    ///
    /// A backup file is the one input that is arbitrarily old — it can be written
    /// by any past version, on any platform, and restored years later. The
    /// Windows <c>UniversalBackupMapper</c> has always run this value through the
    /// core; this portable path did not, so a v7-era backup carrying the retired
    /// <c>googleChirp3</c> (or a v5-era <c>high</c>) landed in the database
    /// verbatim, after the one-shot EF migration that would have fixed it had
    /// already been recorded as applied.
    ///
    /// Migrated ONLY when the key is present and non-empty: absent means the
    /// backup never had a tier, and the core answers <c>deepgramNova3</c> for an
    /// empty input, which is not this path's documented default.
    /// </summary>
    private static string RestoredCloudAccuracyTier(JsonObject value)
    {
        var stored = String(value, "cloudAccuracyTier");
        return string.IsNullOrWhiteSpace(stored)
            ? "elevenLabsScribeV2"
            : SharedCoreBridge.CanonicalCloudSttTier(stored);
    }

    private static List<string>? StringList(JsonObject? value, string key)
    {
        if (value?[key] is null) return null;
        if (value[key] is JsonArray array)
            return array.Select(item => item?.GetValue<string>() ?? throw new JsonException("Vocabulary terms must be strings.")).ToList();
        var legacy = value[key]!.GetValue<string>();
        return legacy.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static DateTime? Date(JsonObject? value, string key)
    {
        var raw = String(value, key);
        if (raw is null) return null;
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new JsonException("Mode timestamp must be an ISO-8601 date.");
        return parsed;
    }

    private static VocabularyItem ParseVocabulary(JsonNode? node)
    {
        var value = node as JsonObject ?? throw new JsonException("Vocabulary item must be an object.");
        return new VocabularyItem { Id = Guid.Parse(value["id"]!.GetValue<string>()), Word = value["word"]!.GetValue<string>(), Replacement = String(value, "replacement"), SortOrder = Int(value, "sortOrder") };
    }


    private static void ValidateModes(IReadOnlyList<Mode> modes)
    {
        if (modes.Select(item => item.Id).Distinct().Count() != modes.Count
            || modes.Select(item => item.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != modes.Count
            || modes.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            throw new InvalidOperationException("Duplicate or invalid modes.");
    }

    private static void ValidateVocabulary(IReadOnlyList<VocabularyItem> vocabulary)
    {
        if (vocabulary.Select(item => item.Id).Distinct().Count() != vocabulary.Count
            || vocabulary.Select(item => item.Word.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != vocabulary.Count
            || vocabulary.Any(item => string.IsNullOrWhiteSpace(item.Word)))
            throw new InvalidOperationException("Duplicate or invalid vocabulary.");
    }

    private static JsonObject? ReadObject(JsonElement? element) => element is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText()) as JsonObject : null;
    private static JsonObject? ReadObject(string? json) { try { return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject; } catch (JsonException) { return null; } }
    private static string? String(JsonObject? value, string key) => value?[key] is null ? null : value[key]!.GetValue<string?>();
    private static bool Bool(JsonObject? value, string key, bool fallback = false) => value?[key]?.GetValue<bool?>() ?? fallback;
    private static int Int(JsonObject value, string key) => value[key]?.GetValue<int?>() ?? 0;
}
