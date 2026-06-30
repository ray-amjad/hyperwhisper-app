using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyperWhisper.Services.LocalApi.Endpoints;

/// <summary>
/// `POST /post-process` — run free-form text through the same post-processing
/// pipeline the GUI uses. Resolves the working Mode (saved id, preset, prompt,
/// or per-call provider/model overrides), then dispatches directly through
/// <see cref="PostProcessingService"/> — NOT the orchestrator, since the
/// orchestrator's wrapping (vocabulary replacements, filler-word removal) is
/// transcription-specific. Core wire shape matches macOS `PostProcessEndpoint`;
/// Windows additionally accepts optional `applicationContext` and never gathers
/// foreground app context automatically for API requests.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PostProcessEndpoints
{
    public static void Map(IEndpointRouteBuilder app, LocalApiServer server)
    {
        app.MapPost("/post-process", async (HttpContext ctx) =>
        {
            PostProcessRequest? req;
            try
            {
                req = await ctx.Request.ReadFromJsonAsync<PostProcessRequest>(LocalApiResponder.JsonOptions);
            }
            catch
            {
                return LocalApiResponder.BadRequest(
                    "Invalid JSON body",
                    "Required: 'text' plus one of mode_id / preset / prompt.");
            }
            if (req == null)
            {
                return LocalApiResponder.BadRequest("Empty request body");
            }

            // VALIDATION — matches macOS PostProcessEndpoint
            var trimmedText = (req.Text ?? "").Trim();
            if (trimmedText.Length == 0)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    "'text' cannot be empty");
            }

            var hasPreset = !string.IsNullOrWhiteSpace(req.Preset);
            var hasPrompt = !string.IsNullOrWhiteSpace(req.Prompt);
            var hasModeId = !string.IsNullOrWhiteSpace(req.ModeId);

            if (hasPreset && hasPrompt)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    "'preset' and 'prompt' are mutually exclusive");
            }

            if (!hasModeId && !hasPreset && !hasPrompt)
            {
                return LocalApiResponder.Failure(
                    LocalApiErrorCode.InvalidRequest,
                    "Provide at least one of 'mode_id', 'preset', or 'prompt'");
            }

            Mode workingMode;
            try
            {
                workingMode = BuildWorkingMode(req);
            }
            catch (ApiInputException aiex)
            {
                return LocalApiResponder.Failure(aiex.Code, aiex.Message, aiex.Hint);
            }

            using var svc = new PostProcessingService();
            string? capturedWarning = null;
            EventHandler<ErrorToastEventArgs> warningHandler = (_, e) => capturedWarning = e.Message;
            svc.WarningOccurred += warningHandler;

            try
            {
                var started = DateTime.UtcNow;
                PostProcessingResult result;
                try
                {
                    result = await svc.ProcessAsync(
                        trimmedText,
                        workingMode,
                        req.ApplicationContext?.ToApplicationContext(),
                        ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    return LocalApiResponder.Failure(
                        LocalApiErrorCode.Timeout,
                        "Post-processing was cancelled");
                }
                catch (Exception ex)
                {
                    LoggingService.Error("LocalAPI /post-process: PostProcessingService threw", ex);
                    return LocalApiResponder.Failure(
                        LocalApiErrorCode.TranscriptionFailed,
                        ex.Message);
                }
                var latencyMs = (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds);

                if (!result.WasApplied)
                {
                    if (!string.IsNullOrEmpty(capturedWarning))
                    {
                        return LocalApiResponder.Failure(
                            LocalApiErrorCode.TranscriptionFailed,
                            capturedWarning);
                    }
                    // Post-processing silently skipped (PostProcessingMode == 0
                    // slipped past BuildWorkingMode, or empty system prompt).
                    // Treat as no-op success — return the input text labelled
                    // `provider: "none"` so callers can distinguish.
                    return LocalApiResponder.Ok(new PostProcessResponse
                    {
                        Text = result.Text,
                        Provider = "none",
                        Model = workingMode.LanguageModel ?? "",
                        Preset = workingMode.Preset ?? "hyper",
                        LatencyMs = latencyMs
                    });
                }

                return LocalApiResponder.Ok(new PostProcessResponse
                {
                    Text = result.Text,
                    Provider = workingMode.PostProcessingProvider ?? "hyperwhisper",
                    Model = workingMode.LanguageModel ?? "",
                    Preset = workingMode.Preset ?? "hyper",
                    LatencyMs = latencyMs
                });
            }
            finally
            {
                svc.WarningOccurred -= warningHandler;
            }
        });
    }

    // =========================================================================
    // Mode resolution
    // =========================================================================

    /// <summary>
    /// Build the in-memory Mode that drives this request. Three shapes:
    ///   1. `mode_id` alone, no overrides → load saved Mode, force
    ///      PostProcessingMode to 1 if it was 0 (request implies caller wants
    ///      post-processing on), and use as-is.
    ///   2. `mode_id` + any of preset/prompt/provider/model → clone saved Mode
    ///      and apply overrides.
    ///   3. No `mode_id` but preset/prompt/provider/model present → build a
    ///      transient Mode from defaults and apply overrides.
    /// Never persisted.
    /// </summary>
    private static Mode BuildWorkingMode(PostProcessRequest req)
    {
        var preset = req.Preset?.Trim();
        var prompt = req.Prompt?.Trim();
        var provider = req.Provider?.Trim();
        var model = req.Model?.Trim();
        var hasOverride = !string.IsNullOrEmpty(preset)
            || !string.IsNullOrEmpty(prompt)
            || !string.IsNullOrEmpty(provider)
            || !string.IsNullOrEmpty(model);

        var modeId = req.ModeId?.Trim();

        // Branch 1 — saved mode, no overrides
        if (!string.IsNullOrEmpty(modeId) && !hasOverride)
        {
            if (!Guid.TryParse(modeId, out var guid))
            {
                throw new ApiInputException(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{modeId}' is not a valid mode id");
            }
            var stored = ModeService.Instance.GetMode(guid);
            if (stored == null)
            {
                throw new ApiInputException(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{modeId}'");
            }
            if (stored.PostProcessingMode == 0)
            {
                throw new ApiInputException(
                    LocalApiErrorCode.InvalidRequest,
                    $"Mode '{stored.Name}' has post-processing disabled. Supply an explicit 'provider' or 'preset' to override.");
            }
            return stored;
        }

        // Branch 2 — saved mode + overrides
        Mode mode;
        if (!string.IsNullOrEmpty(modeId))
        {
            if (!Guid.TryParse(modeId, out var guid))
            {
                throw new ApiInputException(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{modeId}' is not a valid mode id");
            }
            var stored = ModeService.Instance.GetMode(guid);
            if (stored == null)
            {
                throw new ApiInputException(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{modeId}'");
            }
            mode = CloneMode(stored);
        }
        else
        {
            mode = NewDefaultMode();
        }

        // Branch 3 — apply overrides in order: preset → prompt → provider → model
        if (!string.IsNullOrEmpty(preset))
        {
            mode.Preset = preset;
        }
        if (!string.IsNullOrEmpty(prompt))
        {
            mode.Preset = "custom";
            mode.CustomInstructions = prompt;
        }
        if (!string.IsNullOrEmpty(provider))
        {
            mode.PostProcessingProvider = provider;
            mode.PostProcessingMode = IsLocalLlmProvider(provider) ? 2 : 1;
        }
        if (!string.IsNullOrEmpty(model))
        {
            mode.LanguageModel = model;
        }

        // After applying overrides, the request implied post-processing is
        // wanted — guarantee PostProcessingMode reflects that.
        if (mode.PostProcessingMode == 0)
        {
            mode.PostProcessingMode = 1;
        }

        return mode;
    }

    private static bool IsLocalLlmProvider(string id)
    {
        return string.Equals(id, "local_llm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "localLLM", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "localLlm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shallow clone of a saved Mode into a transient, non-persisted instance.
    /// </summary>
    private static Mode CloneMode(Mode baseline)
    {
        return new Mode
        {
            Id = Guid.NewGuid(),
            Name = "__local_api_pp_transient__",
            Preset = baseline.Preset,
            Language = baseline.Language,
            Model = baseline.Model,
            Punctuation = baseline.Punctuation,
            Capitalization = baseline.Capitalization,
            ProfanityFilter = baseline.ProfanityFilter,
            CustomInstructions = baseline.CustomInstructions,
            UserSystemPrompt = baseline.UserSystemPrompt,
            LanguageModel = baseline.LanguageModel,
            CloudProvider = baseline.CloudProvider,
            CloudTranscriptionModel = baseline.CloudTranscriptionModel,
            ProviderType = baseline.ProviderType,
            PostProcessingMode = baseline.PostProcessingMode,
            PostProcessingProvider = baseline.PostProcessingProvider,
            EnglishSpelling = baseline.EnglishSpelling,
            CloudAccuracyTier = baseline.CloudAccuracyTier,
            RemoveTrailingPeriod = baseline.RemoveTrailingPeriod,
            EnableScreenOCR = baseline.EnableScreenOCR,
            GeminiCustomPrompt = baseline.GeminiCustomPrompt,
            CloudPostProcessingModel = baseline.CloudPostProcessingModel,
            LocalEngine = baseline.LocalEngine,
            LocalParakeetModel = baseline.LocalParakeetModel,
            LocalPostProcessingModel = baseline.LocalPostProcessingModel,
            CustomVocabulary = baseline.CustomVocabulary,
            SortOrder = int.MaxValue,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Sensible defaults for a brand-new transient Mode when the request
    /// supplies no `mode_id`. Cloud post-processing on by default; the
    /// override pass will swap fields as needed.
    /// </summary>
    private static Mode NewDefaultMode()
    {
        return new Mode
        {
            Id = Guid.NewGuid(),
            Name = "__local_api_pp_transient__",
            Preset = "hyper",
            Language = "en",
            Model = "base",
            Punctuation = true,
            Capitalization = true,
            ProfanityFilter = false,
            CustomInstructions = "",
            PostProcessingMode = 1,
            PostProcessingProvider = "hyperwhispercloud",
            ProviderType = "cloud",
            // Defaults mirror the GUI/default-mode recommendation
            // (ModeDefaults.cs): ElevenLabs Scribe v2 + Anthropic Claude Haiku 4.5.
            CloudAccuracyTier = "elevenLabsScribeV2",
            CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
            SortOrder = int.MaxValue,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
    }

    private sealed class ApiInputException : Exception
    {
        public string Code { get; }
        public string? Hint { get; }
        public ApiInputException(string code, string message, string? hint = null) : base(message)
        {
            Code = code;
            Hint = hint;
        }
    }
}
