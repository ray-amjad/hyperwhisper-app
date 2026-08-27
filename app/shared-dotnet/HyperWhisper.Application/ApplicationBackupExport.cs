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

    /// <summary>
    /// Builds the universal-v2 shared <c>settings</c> block from the live settings
    /// store, through the shared core's Linux settings adapter
    /// (<c>hw-backup mapping.rs: linux_settings_to_universal</c>).
    /// </summary>
    /// <remarks>
    /// The WHOLE store is handed to the core, which promotes only the keys that
    /// have a row in its <c>LINUX_*_PAIRS</c> tables — so nothing Linux-only and
    /// nothing device-local (<c>selectedModeId</c>, model paths) can reach the
    /// exported file through this path. The Linux half of the map is near-identity:
    /// <c>PortableSettingsService</c>'s dotted keys ARE the universal keys. The
    /// export defaults live in the same tables, which is what keeps an untouched
    /// profile exporting all 23 shared keys. <see cref="ApplyLinuxSettings"/>
    /// stays native on purpose — its defaults are already duplicated against the
    /// live UI defaults, and a Rust copy would be a third home.
    /// </remarks>
    private JsonObject BuildSharedSettings()
    {
        var nativeJson = JsonSerializer.Serialize(_settings.Snapshot(), SerializerOptions);
        return JsonNode.Parse(SharedCoreBridge.LinuxSettingsToUniversal(nativeJson)) as JsonObject
            ?? new JsonObject();
    }

    /// <summary>
    /// Applies an imported universal-v2 shared <c>settings</c> block to the live
    /// settings store, through the shared core's Linux settings adapter
    /// (<c>hw-backup mapping.rs: universal_to_linux_settings</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The core reproduces <c>CopyCategory</c>'s rules exactly: the pairs tables
    /// are a per-category ALLOWLIST, so an unknown key inside a known category and
    /// a whole unknown category are both dropped, and an explicit JSON <c>null</c>
    /// leaves the live value alone. That drop is the Linux half of the unknown-key
    /// gap issue #288 names; it is reproduced here, not fixed.
    /// </para>
    /// <para>
    /// The core's answer is DEEP-MERGED over a baseline snapshot of the live store
    /// before it is written back, mirroring <c>BackupManager.swift</c>'s
    /// <c>currentSettingsBaseline()</c> → <c>deepMerged(over:)</c> → apply. The
    /// merge is inert today — the core is present-only, so every key it omits is
    /// simply carried over from the baseline unchanged, which is exactly what the
    /// old per-key <c>Set</c> loop did. It is wired anyway because it is the
    /// defence the day the core returns a COMPLETE native blob. Note the baseline
    /// is snapshotted HERE, not at the start of the import, so the
    /// <c>platformExtensions.linux</c> writes that <c>ApplySelectedSettings</c>
    /// performs first are inside it and survive.
    /// </para>
    /// </remarks>
    private void ApplySharedSettings(JsonObject settings)
    {
        var fromCore = JsonNode.Parse(
            SharedCoreBridge.UniversalSettingsToLinuxSettings(settings.ToJsonString())) as JsonObject
            ?? new JsonObject();

        var baseline = new JsonObject();
        foreach (var entry in _settings.Snapshot())
            baseline[entry.Key] = JsonNode.Parse(entry.Value.GetRawText());

        var merged = DeepMerge(baseline, fromCore);
        _settings.Replace(merged.ToDictionary(
            entry => entry.Key,
            entry => JsonSerializer.SerializeToElement(entry.Value, SerializerOptions),
            StringComparer.Ordinal));
    }

    /// <summary>
    /// <paramref name="overlay"/> deep-merged over <paramref name="baseline"/>:
    /// overlay wins per key, nested objects merge recursively, and a key only in
    /// the baseline survives. Mirrors <c>BackupManager.swift</c>'s
    /// <c>deepMerged(over:)</c>. Both blobs are flat maps of dotted keys today, so
    /// the recursion never fires; it is kept so the semantics stay macOS's.
    /// </summary>
    private static JsonObject DeepMerge(JsonObject baseline, JsonObject overlay)
    {
        var merged = baseline.DeepClone().AsObject();
        foreach (var entry in overlay)
        {
            if (entry.Value is JsonObject nestedOverlay && merged[entry.Key] is JsonObject nestedBaseline)
            {
                merged[entry.Key] = DeepMerge(nestedBaseline, nestedOverlay);
                continue;
            }
            merged[entry.Key] = entry.Value?.DeepClone();
        }
        return merged;
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

    // Entity-side defaults for a cloud-routing field the backup does not supply.
    // Read from the Mode entity so the canonical default lives in exactly one
    // place — the literals that used to sit inline here were a THIRD copy of the
    // pair, and drifting from Mode.cs would have gone unnoticed.
    private static readonly Mode ModeDefaults = new();

    /// <summary>
    /// Canonicalize a universal mode's five cloud-routing fields in the Rust
    /// shared core. The same <c>normalize_universal_mode_json</c> call the Windows
    /// importer makes, so the two heads now agree: before this, Linux ran no
    /// cloudAccuracyTier / cloudPostProcessingModel migration, no catalog
    /// cloudProvider fold, no legacy model-alias resolution and no
    /// cloudTranscriptionDomain gate. Absent fields stay absent — the caller
    /// applies <see cref="ModeDefaults"/>.
    /// </summary>
    private static JsonObject NormalizeCloudRouting(JsonObject mode)
        => JsonNode.Parse(SharedCoreBridge.NormalizeUniversalMode(mode.ToJsonString()))
            as JsonObject
            ?? throw new JsonException("Mode normalization did not return an object.");

    private static Mode ParseMode(JsonNode? node)
    {
        var value = node as JsonObject ?? throw new JsonException("Mode must be an object.");
        var normalized = NormalizeCloudRouting(value);
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
            EnglishSpelling = String(value, "englishSpelling"), CloudProvider = String(normalized, "cloudProvider"),
            CloudTranscriptionModel = String(normalized, "cloudTranscriptionModel"), CloudTranscriptionDomain = String(normalized, "cloudTranscriptionDomain"),
            PostProcessingMode = Int(value, "postProcessingMode"), PostProcessingProvider = String(value, "postProcessingProvider"),
            LanguageModel = String(value, "languageModel"), LocalPostProcessingModel = String(value, "localPostProcessingModel"),
            UserSystemPrompt = String(value, "userSystemPrompt"), CustomInstructions = String(value, "customInstructions"),
            GeminiCustomPrompt = String(value, "geminiCustomPrompt"),
            CloudAccuracyTier = String(normalized, "cloudAccuracyTier") ?? ModeDefaults.CloudAccuracyTier,
            CloudPostProcessingModel = String(normalized, "cloudPostProcessingModel") ?? ModeDefaults.CloudPostProcessingModel,
            LocalEngine = String(linux, "localEngine") ?? "whisper", LocalParakeetModel = String(linux, "localParakeetModel"),
            ProviderType = String(linux, "providerType") ?? (String(value, "cloudProvider") is null ? "local" : "cloud"),
            EnableScreenOCR = Bool(linux, "enableScreenOCR"), CustomVocabulary = StringList(linux, "customVocabulary"),
            IsSystemProvided = Bool(linux, "isSystemProvided"),
            ForeignPlatformExtensions = preservedExtensions is null || preservedExtensions.Count == 0 ? null : preservedExtensions.ToJsonString(),
            CreatedDate = Date(linux, "createdDate") ?? DateTime.UtcNow,
            ModifiedDate = Date(linux, "modifiedDate") ?? DateTime.UtcNow,
        };
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
