using System.Runtime.Versioning;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Utilities;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// `/modes` CRUD — list, get, create, partial-update, delete. Thin wrapper
/// over <see cref="ModeService.Instance"/>. Wire shape matches macOS
/// `ModesEndpoint` so the same MCP / cURL snippets work cross-platform; the
/// extra Windows-only fields (LocalEngine, LocalParakeetModel, etc.) ride
/// along as additional JSON keys that macOS clients ignore.
/// </summary>
/// <remarks>
/// The shared server hands a mode over as a <see cref="JsonElement"/>, so the
/// Windows <see cref="ModeDto"/> is still the shape: it is read from the
/// element and written back to one with <see cref="LocalApiResponder.JsonOptions"/>,
/// which keeps every key and the nulls-omitted shape a Windows mode always had.
/// A body that does not fit the DTO is a <see cref="JsonException"/>, which the
/// shared server answers as its 400 invalid-JSON envelope.
/// </remarks>
internal sealed partial class WindowsLocalApiBackend
{
    public ValueTask<IReadOnlyList<JsonElement>> GetModesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> modes = ModeService.Instance.GetAllModes()
            .Select(mode => ToJson(ToDto(mode)))
            .ToList();
        return ValueTask.FromResult(modes);
    }

    /// <summary>
    /// The mode, or null for an id that is not a GUID or names no mode; the
    /// shared server answers null as <c>MODE_NOT_FOUND</c>.
    /// </summary>
    public ValueTask<JsonElement?> GetModeAsync(string id, CancellationToken cancellationToken)
    {
        var mode = FindMode(id);
        return ValueTask.FromResult(mode is null ? (JsonElement?)null : ToJson(ToDto(mode)));
    }

    public ValueTask<JsonElement> CreateModeAsync(JsonElement body, CancellationToken cancellationToken)
    {
        // The KEYS come off the element too (issue #356). This head cannot
        // infer which keys the caller sent from `ModeDto` — its three booleans
        // are non-nullable, so absent and `false` are the same value — and the
        // required-key check needs to know.
        var presentKeys = KeysOf(body);
        var dto = body.Deserialize<ModeDto>(LocalApiResponder.JsonOptions)
            ?? throw WindowsLocalApiFailure.BadRequest("Empty request body");

        LogIgnoredModeKeys(presentKeys);

        var trimmedName = (dto.Name ?? "").Trim();
        // ONE MODE CONTRACT (issue #356 items 2 and 5). `validate_mode` owns
        // the required-key set, both numeric ranges and every length bound.
        var validation = HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
            HwLocalApiModeOperation.Create,
            presentKeys.ToList(),
            // `ModeDto.Name` is declared non-nullable, but System.Text.Json
            // happily assigns null from an explicit `"name": null` — which
            // passes the required-key check. Coalescing here hands the
            // shared rule an empty string, which it refuses, instead of a
            // `None` it would skip.
            dto.Name ?? "",
            dto.Language,
            dto.Preset,
            (long?)dto.PostProcessingMode,
            (long?)dto.SortOrder,
            dto.UserSystemPrompt,
            dto.GeminiCustomPrompt,
            dto.CustomVocabulary));
        if (validation != null)
        {
            throw WindowsLocalApiFailure.From(validation);
        }
        if (!TryNormalizeProviderType(dto.ProviderType, out var providerType))
        {
            throw InvalidProviderType(dto.ProviderType);
        }

        // Name uniqueness — `ModeService` does not enforce, so we mirror the
        // macOS endpoint check inline. The comparison key and the failure
        // are the shared ones.
        if (HyperwhisperCoreMethods.LocalApiModeNameConflict(
                trimmedName,
                ModeService.Instance.GetAllModes().Select(m => m.Name).ToList()))
        {
            throw WindowsLocalApiFailure.From(HyperwhisperCoreMethods.LocalApiModeNameTakenFailure(
                trimmedName, HwLocalApiModeOperation.Create));
        }

        var normalized = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared
            .NormalizeCloudProvider(dto.CloudProvider);
        var mode = new Mode
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Preset = dto.Preset ?? "hyper",
            Language = dto.Language ?? "en",
            Punctuation = dto.Punctuation,
            Capitalization = dto.Capitalization,
            ProfanityFilter = dto.ProfanityFilter,
            CustomInstructions = dto.CustomInstructions,
            UserSystemPrompt = dto.UserSystemPrompt,
            IsDefault = dto.IsDefault ?? false,
            IsSystemProvided = dto.IsSystemProvided ?? false,
            SortOrder = dto.SortOrder ?? 0,
            LanguageModel = dto.LanguageModel,
            CloudTranscriptionModel = dto.CloudTranscriptionModel,
            CloudTranscriptionDomain = dto.CloudTranscriptionDomain,
            CloudProvider = normalized.Provider,
            PostProcessingMode = dto.PostProcessingMode ?? 0,
            PostProcessingProvider = dto.PostProcessingProvider,
            // An omitted spelling lands on the system region's variant, the
            // same value the GUI seeds into a new mode.
            EnglishSpelling = dto.EnglishSpelling ?? EnglishSpellingRegionDefault.ForCurrentRegion(),
            // Fallback transcription tier mirrors the GUI/default-mode
            // recommendation (ModeDefaults.cs: ElevenLabs Scribe v2) so
            // API/MCP-created modes don't silently use the retired default.
            CloudAccuracyTier = normalized.AccuracyTier ?? dto.CloudAccuracyTier ?? "elevenLabsScribeV2",
            RemoveTrailingPeriod = dto.RemoveTrailingPeriod ?? false,
            EnableScreenOCR = dto.EnableScreenOcr ?? false,
            GeminiCustomPrompt = dto.GeminiCustomPrompt,
            // Fallback post-processing model mirrors the GUI/default-mode
            // recommendation (ModeDefaults.cs: Anthropic Claude Haiku 5.5).
            CloudPostProcessingModel = dto.CloudPostProcessingModel ?? "anthropic:claude-haiku-5-5",
            LocalEngine = dto.LocalEngine ?? "whisper",
            LocalParakeetModel = dto.LocalParakeetModel,
            LocalPostProcessingModel = dto.LocalPostProcessingModel,
            CustomVocabulary = dto.CustomVocabulary,
            ProviderType = providerType
        };

        // Dispatch the incoming `model` field onto the right entity column.
        // The GUI keys off ModelType for local Whisper, LocalParakeetModel
        // for Parakeet, and CloudTranscriptionModel for cloud. Legacy
        // Model is mirrored alongside ModelType for older readers.
        var dtoModel = dto.Model?.Trim();
        if (string.Equals(dtoModel, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            mode.ProviderType = "cloud";
            mode.Model = "cloud";
        }
        else if (string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(dtoModel))
            {
                mode.LocalParakeetModel = dtoModel;
                mode.Model = dtoModel;
            }
        }
        else
        {
            var local = string.IsNullOrEmpty(dtoModel) ? "base" : dtoModel!;
            mode.ModelType = local;
            mode.Model = local;
        }

        // Normalize macOS-style "cloud" sentinel back into ProviderType="cloud"
        // when no explicit ProviderType was passed by the caller.
        if (string.IsNullOrEmpty(mode.ProviderType))
        {
            mode.ProviderType = string.Equals(mode.Model, "cloud", StringComparison.OrdinalIgnoreCase)
                ? "cloud"
                : "local";
        }

        // A local mode stores what the mode editor stores (#1624, Ray inbox #450).
        if (!LocalModeModel.IsCloud(mode)) LocalModeModel.ApplyOnDevice(mode);

        if (ValidatePostProcessingSelection(mode) is { } validationError)
        {
            throw WindowsLocalApiFailure.Business(LocalApiErrorCode.InvalidRequest, validationError);
        }

        if (dto.UseStreamingTranscription.HasValue)
        {
            SettingsService.Instance.StreamingEnabled = dto.UseStreamingTranscription.Value;
        }

        ModeService.Instance.SaveMode(mode);
        return ValueTask.FromResult(ToJson(ToDto(mode)));
    }

    /// <summary>
    /// Apply a partial body. Null for an id that is not a GUID or names no
    /// mode, which the shared server answers as <c>MODE_NOT_FOUND</c>.
    /// </summary>
    public ValueTask<JsonElement?> PatchModeAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        var existing = FindMode(id);
        if (existing == null)
        {
            return ValueTask.FromResult<JsonElement?>(null);
        }

        var presentKeys = KeysOf(body);
        var patch = body.Deserialize<ModePatchDto>(LocalApiResponder.JsonOptions)
            ?? throw WindowsLocalApiFailure.BadRequest("Empty request body");

        LogIgnoredModeKeys(presentKeys);

        // The same bounds as the create path, minus the required-key rule:
        // `ModePatch` has no `required:` list, because "any field omitted is
        // left untouched" is what a patch means (issue #356).
        var patchValidation = HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
            HwLocalApiModeOperation.Patch,
            presentKeys.ToList(),
            patch.Name,
            patch.Language,
            patch.Preset,
            (long?)patch.PostProcessingMode,
            (long?)patch.SortOrder,
            patch.UserSystemPrompt,
            patch.GeminiCustomPrompt,
            patch.CustomVocabulary));
        if (patchValidation != null)
        {
            throw WindowsLocalApiFailure.From(patchValidation);
        }
        if (!TryNormalizeProviderType(patch.ProviderType, out var patchProviderType))
        {
            throw InvalidProviderType(patch.ProviderType);
        }
        patch.ProviderType = patchProviderType;

        // Name uniqueness check — ONLY WHEN THE CALLER IS ACTUALLY RENAMING
        // (issue #356, review round 1; see PatchNameCollides). The head still
        // filters out the record it is writing, because only the head knows
        // which one that is; the comparison itself is shared.
        if (patch.Name is { } rawName)
        {
            var trimmed = rawName.Trim();
            // The default mode's name is fixed (issue #536).
            if (!ModeService.CanRename(existing, trimmed))
            {
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.InvalidRequest,
                    "The default mode's name cannot be changed",
                    "Rename a different mode, or make a different mode the default first.");
            }
            if (PatchNameCollides(
                    trimmed,
                    existing.Name,
                    ModeService.Instance.GetAllModes()
                        .Where(m => m.Id != existing.Id)
                        .Select(m => m.Name)
                        .ToList()))
            {
                throw WindowsLocalApiFailure.From(HyperwhisperCoreMethods.LocalApiModeNameTakenFailure(
                    trimmed, HwLocalApiModeOperation.Patch));
            }
        }

        // Exactly one mode is the default (issue #536). Setting the flag is
        // always allowed — it moves, clearing whichever mode held it — but
        // clearing the only one is refused.
        if (patch.IsDefault is { } requestedDefault
            && !ModeService.Instance.CanWriteDefaultFlag(existing.Id, requestedDefault))
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.InvalidRequest,
                "At least one mode must be the default",
                "Set 'isDefault' on another mode instead — that clears this one.");
        }

        if (SwitchesToLocalWithoutModel(patch, existing))
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.InvalidRequest,
                "Switching this mode to on-device needs a 'model'",
                "Send 'model' with the on-device model id (for example 'base') together with providerType 'local'.");
        }

        ApplyPatch(patch, existing);
        if (ValidatePostProcessingSelection(existing) is { } validationError)
        {
            throw WindowsLocalApiFailure.Business(LocalApiErrorCode.InvalidRequest, validationError);
        }
        if (patch.UseStreamingTranscription is { } useStreaming)
        {
            SettingsService.Instance.StreamingEnabled = useStreaming;
        }
        existing.ModifiedDate = DateTime.UtcNow;
        ModeService.Instance.UpdateMode(existing);

        // Re-read rather than echoing the entity we just sent in. Making
        // this mode the default clears the flag on another row, and the
        // repair happens on the service's own tracked copies, so `existing`
        // can report a flag the database no longer agrees with.
        var saved = ModeService.Instance.GetMode(existing.Id) ?? existing;
        return ValueTask.FromResult<JsonElement?>(ToJson(ToDto(saved)));
    }

    /// <summary>
    /// False for an id that is not a GUID or names no mode, which the shared
    /// server answers as <c>MODE_NOT_FOUND</c>.
    /// </summary>
    public ValueTask<bool> DeleteModeAsync(string id, CancellationToken cancellationToken)
    {
        var mode = FindMode(id);
        if (mode == null)
        {
            return ValueTask.FromResult(false);
        }

        // ModeService.DeleteMode() refuses to delete the last remaining
        // Mode and returns false in that case — mirror to INVALID_REQUEST
        // so the wire shape matches macOS's "Cannot delete the last
        // remaining mode" error.
        if (!ModeService.Instance.DeleteMode(mode.Id))
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.InvalidRequest,
                "Cannot delete the last remaining mode",
                "Create a replacement mode first, then delete this one.");
        }
        return ValueTask.FromResult(true);
    }

    private static Mode? FindMode(string? id) =>
        Guid.TryParse(id?.Trim(), out var guid) ? ModeService.Instance.GetMode(guid) : null;

    internal static IReadOnlyList<string> KeysOf(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
            ? body.EnumerateObject().Select(property => property.Name).ToList()
            : [];

    internal static JsonElement ToJson(ModeDto dto) =>
        JsonSerializer.SerializeToElement(dto, LocalApiResponder.JsonOptions);
}
