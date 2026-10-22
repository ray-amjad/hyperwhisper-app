using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Utilities;
using uniffi.hyperwhisper_core;
using SharedApi = HyperWhisper.LocalApi;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// `/modes`, its mapping half: project a Mode to the wire DTO, apply a PATCH
/// body to a Mode, and the provider-type, name and post-processing checks the
/// create and patch handlers in <c>WindowsLocalApiBackend.Modes.cs</c> call.
/// Split out of that file (#860); the bodies are unchanged.
/// </summary>
internal sealed partial class WindowsLocalApiBackend
{
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
        var isCloud = LocalModeModel.IsCloud(mode);
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
    internal static void ApplyPatch(ModePatchDto patch, Mode mode)
    {
        if (patch.Name is { } n) mode.Name = n.Trim();
        if (patch.Preset is { } p) mode.Preset = p;
        if (patch.Language is { } l) mode.Language = l;
        // The engine goes first, because `model` below is routed by it (issue
        // #1542). A PATCH carrying both `localEngine` and `model` must write the
        // model into the field of the engine it asks for, as POST already does.
        if (patch.LocalEngine is { } v19) mode.LocalEngine = v19;
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
        // An explicit `localParakeetModel` still wins over `model`: it is applied after.
        if (patch.LocalParakeetModel is { } v20) mode.LocalParakeetModel = v20;
        if (patch.LocalPostProcessingModel is { } v21) mode.LocalPostProcessingModel = v21;
        if (patch.CustomVocabulary is { } v22) mode.CustomVocabulary = v22;
        if (patch.ProviderType is { } v23) mode.ProviderType = v23;

        // A local mode stores what the mode editor stores: Model = its local id,
        // CloudProvider = null (#1624, Ray inbox #450). `GET /modes` then
        // answers `cloudProvider: null` for it.
        if (!LocalModeModel.IsCloud(mode)) LocalModeModel.ApplyOnDevice(mode);
    }

    /// <summary>
    /// A PATCH that makes a mode on-device without naming the local model, on a
    /// mode whose <see cref="Mode.Model"/> is <c>"cloud"</c> (#1624). Ray (inbox
    /// #450): refuse it and ask for <c>model</c>; never pick a default local
    /// model, which could start a download the caller did not ask for.
    /// </summary>
    internal static bool SwitchesToLocalWithoutModel(ModePatchDto patch, Mode mode)
    {
        if (patch.ProviderType != "local") return false;
        if (!string.Equals(mode.Model?.Trim(), LocalModeModel.CloudModel, StringComparison.OrdinalIgnoreCase)) return false;
        if (NamesLocalModel(patch.Model)) return false;
        // `localParakeetModel` names the model only for the Parakeet engine.
        var engine = patch.LocalEngine ?? mode.LocalEngine;
        return !(string.Equals(engine, "parakeet", StringComparison.OrdinalIgnoreCase)
                 && NamesLocalModel(patch.LocalParakeetModel));
    }

    private static bool NamesLocalModel(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && !string.Equals(id.Trim(), LocalModeModel.CloudModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fold a caller's <c>providerType</c> onto the two values this head routes
    /// on (#1548). <c>local</c> and <c>cloud</c> are accepted in any casing and
    /// stored lowercase; <c>null</c> (key absent) stays <c>null</c>. Every other
    /// value is refused, because readers disagreed on it: the orchestrator ran
    /// it on-device, while the editor showed it as Cloud and saved it as Cloud.
    /// </summary>
    internal static bool TryNormalizeProviderType(string? raw, out string? normalized)
    {
        normalized = null;
        if (raw == null) return true;
        if (string.Equals(raw, "local", StringComparison.OrdinalIgnoreCase)) normalized = "local";
        else if (string.Equals(raw, "cloud", StringComparison.OrdinalIgnoreCase)) normalized = "cloud";
        else return false;
        return true;
    }

    private static SharedApi.LocalApiFailureException InvalidProviderType(string? raw)
        => WindowsLocalApiFailure.Business(
            LocalApiErrorCode.InvalidRequest,
            $"providerType must be 'local' or 'cloud', got '{raw}'",
            "Send 'local' for on-device transcription or 'cloud' for a cloud provider, or omit the key.");

    /// <summary>
    /// Log the keys of a mode body this head does not store, classified by the
    /// shared key union (issue #356 item 2).
    /// </summary>
    /// <remarks>
    /// This head has always IGNORED an unrecognised key — System.Text.Json's
    /// default <c>UnmappedMemberHandling</c> is <c>Skip</c> and
    /// <see cref="LocalApiResponder.JsonOptions"/> does not change it — and the
    /// verdict for #356 is that ignoring is right, because <c>openapi.yaml</c>
    /// documents five keys as "Windows only. macOS ignores this key" and so
    /// invites a cross-platform client to send keys a head does not implement.
    /// So this is a LOG, not a gate: nothing here rejects. Its value is that
    /// `mode_key_classification` is the one written-down union, so a key that a
    /// future release adds to one head and not to another shows up here as
    /// <c>Unknown</c> instead of vanishing silently.
    /// </remarks>
    private static void LogIgnoredModeKeys(IReadOnlyList<string> presentKeys)
    {
        foreach (var key in presentKeys)
        {
            if (HyperwhisperCoreMethods.LocalApiModeKeyClassification(key) == HwLocalApiModeKeyClass.Unknown)
            {
                System.Diagnostics.Debug.WriteLine($"Local API: ignoring unrecognised mode field '{key}'.");
            }
        }
    }

    /// <summary>
    /// Whether a <c>PATCH</c> body's <c>name</c> collides with another stored
    /// mode. <c>false</c> when the caller is not actually renaming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #356, review round 1. The "only when the caller is actually
    /// renaming" half of this check was dropped when the collision rule moved
    /// into <c>hw-localapi</c>. Without it a read-modify-write client — <c>GET
    /// /modes/{id}</c>, change one field, <c>PATCH</c> the whole object back —
    /// has its UNCHANGED name compared against every other stored mode, and
    /// duplicate names are producible on this head:
    /// <c>HyperWhisperDbContext</c> declares a NON-unique index on <c>Name</c>,
    /// and neither <c>ModeService.SaveMode</c>/<c>UpdateMode</c> nor
    /// <c>ModeEditorWindow</c> checks for a collision. A mode sharing a name
    /// with another became patchable only by omitting <c>name</c> entirely.
    /// macOS never lost its guard.
    /// </para>
    /// <para>
    /// "Not actually renaming" is asked with the SAME shared comparison key, not
    /// with a fourth definition of "the same name". That is sound as well as
    /// tidy: if the comparison key does not change, the multiset of keys in
    /// storage is identical after the write and before it, so the write cannot
    /// introduce a collision that did not already exist.
    /// </para>
    /// </remarks>
    internal static bool PatchNameCollides(string trimmedName, string storedName, IReadOnlyList<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(otherNames);
        if (HyperwhisperCoreMethods.LocalApiModeNameConflict(trimmedName, [storedName])) return false;
        return HyperwhisperCoreMethods.LocalApiModeNameConflict(trimmedName, otherNames.ToList());
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
