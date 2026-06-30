using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyperWhisper.Services.LocalApi.Endpoints;

/// <summary>
/// `/modes` CRUD — list, get, create, partial-update, delete. Thin wrapper
/// over <see cref="ModeService.Instance"/>. Wire shape matches macOS
/// `ModesEndpoint` so the same MCP / cURL snippets work cross-platform; the
/// extra Windows-only fields (LocalEngine, LocalParakeetModel, etc.) ride
/// along as additional JSON keys that macOS clients ignore.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ModesEndpoints
{
    public static void Map(IEndpointRouteBuilder app, LocalApiServer server)
    {
        app.MapGet("/modes", () =>
        {
            var modes = ModeService.Instance.GetAllModes();
            var dtos = modes.Select(ToDto).ToList();
            return LocalApiResponder.Ok(new ModesListResponse { Modes = dtos });
        });

        app.MapGet("/modes/{id}", (string id) =>
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{id}' is not a valid mode id");
            }
            var mode = ModeService.Instance.GetMode(guid);
            if (mode == null)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{id}'");
            }
            return LocalApiResponder.Ok(new ModeResponse { Mode = ToDto(mode) });
        });

        app.MapPost("/modes", async (HttpContext ctx) =>
        {
            ModeDto? dto;
            try
            {
                dto = await ctx.Request.ReadFromJsonAsync<ModeDto>(LocalApiResponder.JsonOptions);
            }
            catch
            {
                return LocalApiResponder.BadRequest(
                    "Invalid JSON body",
                    "Required: name. See /modes GET for the full shape.");
            }
            if (dto == null)
            {
                return LocalApiResponder.BadRequest("Empty request body");
            }

            var trimmedName = (dto.Name ?? "").Trim();
            if (trimmedName.Length == 0)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    "Mode 'name' cannot be empty");
            }

            // Name uniqueness — `ModeService` does not enforce, so we mirror the
            // macOS endpoint check inline. Match case-insensitively to match
            // typical user expectations.
            var existingByName = ModeService.Instance.GetAllModes()
                .FirstOrDefault(m => string.Equals(m.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
            if (existingByName != null)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.ModeNameTaken,
                    $"A mode named '{trimmedName}' already exists",
                    "Choose a different name or PATCH the existing mode instead.");
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
                EnglishSpelling = dto.EnglishSpelling,
                // Fallback transcription tier mirrors the GUI/default-mode
                // recommendation (ModeDefaults.cs: ElevenLabs Scribe v2) so
                // API/MCP-created modes don't silently use the retired default.
                CloudAccuracyTier = normalized.AccuracyTier ?? dto.CloudAccuracyTier ?? "elevenLabsScribeV2",
                RemoveTrailingPeriod = dto.RemoveTrailingPeriod ?? false,
                EnableScreenOCR = dto.EnableScreenOcr ?? false,
                GeminiCustomPrompt = dto.GeminiCustomPrompt,
                // Fallback post-processing model mirrors the GUI/default-mode
                // recommendation (ModeDefaults.cs: Anthropic Claude Haiku 4.5).
                CloudPostProcessingModel = dto.CloudPostProcessingModel ?? "anthropic:claude-haiku-4-5",
                LocalEngine = dto.LocalEngine ?? "whisper",
                LocalParakeetModel = dto.LocalParakeetModel,
                LocalPostProcessingModel = dto.LocalPostProcessingModel,
                CustomVocabulary = dto.CustomVocabulary,
                ProviderType = dto.ProviderType
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

            if (ValidatePostProcessingSelection(mode) is { } validationError)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    validationError);
            }

            if (dto.UseStreamingTranscription.HasValue)
            {
                SettingsService.Instance.StreamingEnabled = dto.UseStreamingTranscription.Value;
            }

            ModeService.Instance.SaveMode(mode);
            return LocalApiResponder.Ok(new ModeResponse { Mode = ToDto(mode) });
        });

        app.MapMethods("/modes/{id}", new[] { "PATCH" }, async (string id, HttpContext ctx) =>
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{id}' is not a valid mode id");
            }
            var existing = ModeService.Instance.GetMode(guid);
            if (existing == null)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{id}'");
            }

            ModePatchDto? patch;
            try
            {
                patch = await ctx.Request.ReadFromJsonAsync<ModePatchDto>(LocalApiResponder.JsonOptions);
            }
            catch
            {
                return LocalApiResponder.BadRequest("Invalid JSON body");
            }
            if (patch == null)
            {
                return LocalApiResponder.BadRequest("Empty request body");
            }

            // Name uniqueness check — only when the caller is actually renaming.
            if (patch.Name is { } rawName)
            {
                var trimmed = rawName.Trim();
                if (trimmed.Length == 0)
                {
                    return LocalApiResponder.Failure(
                        LocalApiErrorCode.InvalidRequest,
                        "Mode 'name' cannot be empty");
                }
                if (!string.Equals(trimmed, existing.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var clash = ModeService.Instance.GetAllModes()
                        .FirstOrDefault(m => string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase));
                    if (clash != null && clash.Id != existing.Id)
                    {
                        return LocalApiResponder.Failure(
                            LocalApiErrorCode.ModeNameTaken,
                            $"A mode named '{trimmed}' already exists");
                    }
                }
            }

            ApplyPatch(patch, existing);
            if (ValidatePostProcessingSelection(existing) is { } validationError)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    validationError);
            }
            if (patch.UseStreamingTranscription is { } useStreaming)
            {
                SettingsService.Instance.StreamingEnabled = useStreaming;
            }
            existing.ModifiedDate = DateTime.UtcNow;
            ModeService.Instance.UpdateMode(existing);

            return LocalApiResponder.Ok(new ModeResponse { Mode = ToDto(existing) });
        });

        app.MapDelete("/modes/{id}", (string id) =>
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{id}' is not a valid mode id");
            }
            var mode = ModeService.Instance.GetMode(guid);
            if (mode == null)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{id}'");
            }

            // ModeService.DeleteMode() refuses to delete the last remaining
            // Mode and returns false in that case — mirror to INVALID_REQUEST
            // so the wire shape matches macOS's "Cannot delete the last
            // remaining mode" error.
            var deleted = ModeService.Instance.DeleteMode(guid);
            if (!deleted)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    "Cannot delete the last remaining mode",
                    "Create a replacement mode first, then delete this one.");
            }
            return LocalApiResponder.Ok(new OkResponse());
        });
    }

    // =========================================================================
    // Projection
    // =========================================================================

    /// <summary>
    /// Convert a `Mode` entity to the wire DTO. Two pieces of normalization
    /// happen here for macOS parity:
    ///   1. `useStreamingTranscription` reflects Windows' global Streaming
    ///      setting. Streaming is configured on the Streaming page rather than
    ///      stored per mode, but the API must not claim the pipeline is absent.
    ///   2. When ProviderType is "cloud", the `model` field reports the macOS
    ///      "cloud" sentinel so cross-platform clients can branch on it.
    /// </summary>
    internal static ModeDto ToDto(Mode mode)
    {
        var isCloud = string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase);
        // For non-cloud modes the GUI keys off ModelType, so emit ModelType
        // (falling back to legacy Model). Cloud modes ignore both.
        var modelOut = isCloud
            ? "cloud"
            : (mode.ModelType ?? mode.Model ?? "base");

        return new ModeDto
        {
            Id = mode.Id.ToString("D"),
            Name = mode.Name,
            Preset = mode.Preset,
            Language = mode.Language,
            Model = modelOut,
            Punctuation = mode.Punctuation,
            Capitalization = mode.Capitalization,
            ProfanityFilter = mode.ProfanityFilter,
            CustomInstructions = mode.CustomInstructions,
            UserSystemPrompt = mode.UserSystemPrompt,
            IsDefault = mode.IsDefault,
            IsSystemProvided = mode.IsSystemProvided,
            SortOrder = mode.SortOrder,
            CreatedDate = mode.CreatedDate,
            ModifiedDate = mode.ModifiedDate,
            LanguageModel = mode.LanguageModel,
            CloudTranscriptionModel = mode.CloudTranscriptionModel,
            CloudTranscriptionDomain = mode.CloudTranscriptionDomain,
            CloudProvider = mode.CloudProvider,
            PostProcessingMode = mode.PostProcessingMode,
            PostProcessingProvider = mode.PostProcessingProvider,
            EnglishSpelling = mode.EnglishSpelling,
            UseStreamingTranscription = SettingsService.Instance.StreamingEnabled,
            CloudAccuracyTier = mode.CloudAccuracyTier,
            RemoveTrailingPeriod = mode.RemoveTrailingPeriod,
            EnableScreenOcr = mode.EnableScreenOCR,
            GeminiCustomPrompt = mode.GeminiCustomPrompt,
            CloudPostProcessingModel = mode.CloudPostProcessingModel,
            LocalEngine = mode.LocalEngine,
            LocalParakeetModel = mode.LocalParakeetModel,
            LocalPostProcessingModel = mode.LocalPostProcessingModel,
            CustomVocabulary = mode.CustomVocabulary,
            ProviderType = mode.ProviderType
        };
    }

    /// <summary>
    /// Apply only the present keys of a `ModePatchDto` onto an existing Mode.
    /// Absent (nil) keys are left untouched. Matches macOS `applyPatch`.
    /// Windows streaming is configured globally, so `useStreamingTranscription`
    /// maps to the global Streaming enabled setting instead of a per-mode column.
    /// </summary>
    private static void ApplyPatch(ModePatchDto patch, Mode mode)
    {
        if (patch.Name is { } n) mode.Name = n.Trim();
        if (patch.Preset is { } p) mode.Preset = p;
        if (patch.Language is { } l) mode.Language = l;
        if (patch.Model is { } m)
        {
            // Reverse the macOS "cloud" sentinel normalization so callers can
            // PATCH model:"cloud" to flip the mode to cloud. ModelType (the
            // canonical local-Whisper field used by the GUI) is preserved so
            // flipping back to local restores the prior local model.
            if (string.Equals(m, "cloud", StringComparison.OrdinalIgnoreCase))
            {
                mode.ProviderType = "cloud";
                mode.Model = "cloud";
            }
            else if (string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase))
            {
                mode.LocalParakeetModel = m;
                mode.Model = m;
                if (string.IsNullOrEmpty(mode.ProviderType) || string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
                {
                    mode.ProviderType = "local";
                }
            }
            else
            {
                mode.ModelType = m;
                mode.Model = m;
                if (string.IsNullOrEmpty(mode.ProviderType) || string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
                {
                    mode.ProviderType = "local";
                }
            }
        }
        if (patch.Punctuation is { } v1) mode.Punctuation = v1;
        if (patch.Capitalization is { } v2) mode.Capitalization = v2;
        if (patch.ProfanityFilter is { } v3) mode.ProfanityFilter = v3;
        if (patch.CustomInstructions is { } v4) mode.CustomInstructions = v4;
        if (patch.UserSystemPrompt is { } v5) mode.UserSystemPrompt = string.IsNullOrEmpty(v5) ? null : v5;
        if (patch.IsDefault is { } v6) mode.IsDefault = v6;
        if (patch.SortOrder is { } v7) mode.SortOrder = v7;
        if (patch.LanguageModel is { } v8) mode.LanguageModel = v8;
        if (patch.CloudTranscriptionModel is { } v9) mode.CloudTranscriptionModel = v9;
        if (patch.CloudTranscriptionDomain is { } v9d) mode.CloudTranscriptionDomain = string.IsNullOrEmpty(v9d) ? null : v9d;
        string? inferredAccuracyTier = null;
        if (patch.CloudProvider is { } v10)
        {
            var patchNormalized = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared
                .NormalizeCloudProvider(v10);
            mode.CloudProvider = patchNormalized.Provider;
            inferredAccuracyTier = patchNormalized.AccuracyTier;
        }
        if (patch.PostProcessingMode is { } v11) mode.PostProcessingMode = v11;
        if (patch.PostProcessingProvider is { } v12) mode.PostProcessingProvider = v12;
        if (patch.EnglishSpelling is { } v13) mode.EnglishSpelling = v13;
        // Streaming is a global setting on Windows. Apply it only after the
        // patch validates so rejected mode requests have no side effects.
        // Prefer an explicit patch over the migration's inferred tier so a
        // same-PATCH cloudProvider+cloudAccuracyTier pair lands as the caller
        // wrote it.
        var resolvedAccuracyTier = patch.CloudAccuracyTier ?? inferredAccuracyTier;
        if (resolvedAccuracyTier != null) mode.CloudAccuracyTier = resolvedAccuracyTier;
        if (patch.RemoveTrailingPeriod is { } v15) mode.RemoveTrailingPeriod = v15;
        if (patch.EnableScreenOcr is { } v16) mode.EnableScreenOCR = v16;
        if (patch.GeminiCustomPrompt is { } v17) mode.GeminiCustomPrompt = string.IsNullOrEmpty(v17) ? null : v17;
        if (patch.CloudPostProcessingModel is { } v18) mode.CloudPostProcessingModel = v18;
        if (patch.LocalEngine is { } v19) mode.LocalEngine = v19;
        if (patch.LocalParakeetModel is { } v20) mode.LocalParakeetModel = v20;
        if (patch.LocalPostProcessingModel is { } v21) mode.LocalPostProcessingModel = v21;
        if (patch.CustomVocabulary is { } v22) mode.CustomVocabulary = v22;
        if (patch.ProviderType is { } v23) mode.ProviderType = v23;
    }

    private static string? ValidatePostProcessingSelection(Mode mode)
    {
        var providerValue = mode.PostProcessingProvider?.Trim();
        if (string.IsNullOrEmpty(providerValue))
        {
            return null;
        }

        if (CustomPostProcessingEndpoint.IsCustomProviderString(providerValue))
        {
            return CustomEndpointManager.Instance.EndpointFromProviderString(providerValue) == null
                ? $"Custom post-processing endpoint '{providerValue}' does not exist."
                : null;
        }

        var provider = PostProcessingProviderExtensions.FromString(providerValue);
        if (provider == PostProcessingProvider.None)
        {
            return $"Unknown post-processing provider '{providerValue}'.";
        }

        if (provider == PostProcessingProvider.HyperWhisperCloud)
        {
            return null;
        }

        if (provider == PostProcessingProvider.LocalLlm && !PlatformHelper.SupportsLocalLlmPostProcessing)
        {
            return "Local LLM post-processing is not supported on this Windows architecture.";
        }

        var selectedModelId = provider == PostProcessingProvider.LocalLlm
            ? mode.LocalPostProcessingModel ?? mode.LanguageModel
            : mode.LanguageModel;
        if (string.IsNullOrWhiteSpace(selectedModelId))
        {
            return null;
        }

        var migratedModelId = LanguageModelInfo.MigrateModelId(selectedModelId);
        var model = LanguageModelInfo.GetById(migratedModelId);
        if (model == null)
        {
            return $"Unknown post-processing model '{selectedModelId}'.";
        }

        return model.Provider == provider
            ? null
            : $"Post-processing model '{selectedModelId}' belongs to {model.Provider.ToStringValue()}, not {provider.ToStringValue()}.";
    }
}
