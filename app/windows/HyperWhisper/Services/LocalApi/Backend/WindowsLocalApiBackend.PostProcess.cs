using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using SharedApi = HyperWhisper.LocalApi;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// `POST /post-process` — run free-form text through the same post-processing
/// pipeline the GUI uses. Resolves the working Mode (saved id, preset, prompt,
/// or per-call provider/model overrides), then dispatches directly through
/// <see cref="PostProcessingService.ProcessPreservingBreaksAsync"/> — NOT the
/// orchestrator, since the orchestrator's wrapping (vocabulary replacements,
/// filler-word removal) is transcription-specific and stays out of scope here.
/// Dictated paragraph breaks ("new line" / "new paragraph") ARE preserved,
/// matching the GUI pipeline: the text is pre-split on dictated commands, each
/// segment is post-processed independently, and the breaks are restored
/// afterwards. Windows additionally accepts optional `applicationContext` and
/// never gathers foreground app context automatically for API requests.
/// </summary>
/// <remarks>
/// The shared server validates the body first: non-empty text within its
/// length cap, <c>preset</c> and <c>prompt</c> not both, and at least one of
/// <c>mode_id</c>, <c>preset</c> or <c>prompt</c>.
/// </remarks>
internal sealed partial class WindowsLocalApiBackend
{
    public async ValueTask<SharedApi.PostProcessResult> PostProcessAsync(
        SharedApi.PostProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trimmedText = (request.Text ?? "").Trim();
        var workingMode = BuildWorkingMode(request);

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
                result = await svc.ProcessPreservingBreaksAsync(
                    trimmedText,
                    workingMode,
                    LocalApiApplicationContext.From(request.ApplicationContext)?.ToApplicationContext(),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw WindowsLocalApiFailure.Business(LocalApiErrorCode.Timeout, "Post-processing was cancelled");
            }
            catch (Exception ex)
            {
                LoggingService.Error("LocalAPI /post-process: PostProcessingService threw", ex);
                throw WindowsLocalApiFailure.Business(LocalApiErrorCode.TranscriptionFailed, ex.Message);
            }
            var latencyMs = (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds);

            // No account key on a HyperWhisper Cloud run: refuse with the
            // envelope `/transcribe` gives for the same mode (issue #1487),
            // not the generic TRANSCRIPTION_FAILED the captured warning
            // below would produce.
            if (!result.WasApplied && result.CloudAccountMissing)
            {
                throw WindowsLocalApiFailure.Transcription(new TranscriptionException(
                    TranscriptionErrorCode.CloudAccountRequired,
                    "HyperWhisper Cloud requires an account key",
                    "HyperWhisper Cloud"));
            }

            // `result.AnyPartialFailure` covers the multi-segment case where
            // some segments applied and at least one did not — `WasApplied`
            // alone is OR-aggregated across segments and would otherwise
            // report `ok: true` with a response that's silently a mix of
            // correctly-processed and raw/unprocessed segment text. Several
            // genuine per-segment failure paths never raise `WarningOccurred`,
            // so this branch must not depend on `capturedWarning` being set.
            if (result.AnyPartialFailure)
            {
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.TranscriptionFailed,
                    !string.IsNullOrEmpty(capturedWarning)
                        ? capturedWarning
                        : "Post-processing partially failed: some segments were processed and at least one was not.");
            }

            if (!result.WasApplied)
            {
                if (!string.IsNullOrEmpty(capturedWarning))
                {
                    throw WindowsLocalApiFailure.Business(LocalApiErrorCode.TranscriptionFailed, capturedWarning);
                }
                // Post-processing silently skipped (PostProcessingMode == 0
                // slipped past BuildWorkingMode, or empty system prompt).
                // Treat as no-op success — return the input text labelled
                // `provider: "none"` so callers can distinguish. Nothing ran,
                // so the stored `LanguageModel` is the only label there is and
                // is not a claim about a run (#314). `post_processed: false`
                // says the same thing in the field every head shares (#499).
                return new SharedApi.PostProcessResult(
                    result.Text,
                    "none",
                    workingMode.LanguageModel ?? "",
                    workingMode.Preset ?? "hyper",
                    latencyMs,
                    PostProcessed: false);
            }

            // Report the provider and model that ACTUALLY ran (issue #314),
            // not the ones stored on the Mode — every fallback inside
            // PostProcessingService happens after the Mode was read. `preset`
            // is not remapped anywhere, so it still comes off the Mode.
            var labels = ResponseLabels(
                workingMode.PostProcessingProvider,
                workingMode.LanguageModel,
                result.ResolvedProvider,
                result.ResolvedModel);

            return new SharedApi.PostProcessResult(
                result.Text,
                labels.Provider,
                labels.Model,
                workingMode.Preset ?? "hyper",
                latencyMs,
                // Reached only past the `!result.WasApplied` guard above, so
                // an LLM ran and this text is its output.
                PostProcessed: true);
        }
        finally
        {
            svc.WarningOccurred -= warningHandler;
        }
    }

    // =========================================================================
    // Response labels
    // =========================================================================

    /// <summary>
    /// Decide the `provider` and `model` fields of the success body: what
    /// actually ran, falling back to the working Mode's stored values only when
    /// nothing ran (issue #314).
    /// <para>
    /// <see cref="PostProcessingService"/> fills
    /// <see cref="PostProcessingResult.ResolvedProvider"/> /
    /// <see cref="PostProcessingResult.ResolvedModel"/> only on
    /// <see cref="PostProcessingResult.Applied"/>, so a non-null resolved value
    /// already means "an LLM produced this text". No separate
    /// <see cref="PostProcessingResult.WasApplied"/> cross-check is needed here,
    /// and a run that picked a model and then failed cannot leave a stale label.
    /// </para>
    /// <para>
    /// PROVIDER SPELLING IS PRESERVED. When the caller's stored provider NAMES
    /// the provider that ran, the caller's own spelling is echoed back verbatim,
    /// so this field changes ONLY in the cases that are the bug — a genuine
    /// provider substitution. This is load-bearing on Windows: the resolved
    /// HyperWhisper Cloud value is <c>"hyperwhispercloud"</c> while a mode synced
    /// from macOS stores <c>"hyperwhisper"</c>, and both name the same provider —
    /// emitting the resolved spelling would change the wire value for every
    /// ordinary cloud request. Hence the comparison parses both sides
    /// (<see cref="PostProcessingProviderExtensions.FromString"/>) instead of
    /// comparing strings, with a plain string match first so a
    /// <c>custom:&lt;guid&gt;</c> string — which parses to
    /// <see cref="PostProcessingProvider.None"/>, as does every OTHER custom
    /// endpoint — is preserved only when it is genuinely the same one.
    /// </para>
    /// <para>
    /// An empty resolved model is treated as absent: <c>""</c> is "no answer",
    /// not an answer. When nothing ran at all, `model` can still be <c>""</c> —
    /// that is honest, and this method is not reached on the skipped path.
    /// </para>
    /// </summary>
    internal static (string Provider, string Model) ResponseLabels(
        string? storedProvider,
        string? storedModel,
        string? resolvedProvider,
        string? resolvedModel)
    {
        var stored = storedProvider?.Trim() ?? "";
        var resolved = resolvedProvider?.Trim() ?? "";

        string provider;
        if (resolved.Length == 0)
        {
            provider = stored.Length == 0 ? "hyperwhisper" : stored;
        }
        else if (stored.Length > 0 && NamesTheSameProvider(stored, resolved))
        {
            provider = stored;
        }
        else
        {
            provider = resolved;
        }

        // A RUN THAT DID NOT NAME ITS MODEL IS STILL A RUN. The fallback keys on
        // `resolvedModel is null` — "nothing ran" — NOT on the resolved string
        // being empty. `""` is reported as `""`.
        var model = resolvedModel is null ? (storedModel ?? "") : resolvedModel.Trim();

        return (provider, model);
    }

    /// <summary>
    /// True when two provider strings name the same provider. Exact (ignoring
    /// case) counts, which covers custom-endpoint ids; otherwise both must parse
    /// to the same non-<see cref="PostProcessingProvider.None"/> enum case, so
    /// two DIFFERENT custom endpoints — both of which parse to `None` — are not
    /// mistaken for each other.
    /// </summary>
    private static bool NamesTheSameProvider(string stored, string resolved)
    {
        if (string.Equals(stored, resolved, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var storedParsed = PostProcessingProviderExtensions.FromString(stored);
        return storedParsed != PostProcessingProvider.None
            && storedParsed == PostProcessingProviderExtensions.FromString(resolved);
    }

    // =========================================================================
    // Mode resolution
    // =========================================================================

    /// <summary>
    /// Build the in-memory Mode that drives this request. Three shapes:
    ///   1. `mode_id` alone, no overrides → load saved Mode and use it
    ///      as-is, or reject it if post-processing is disabled.
    ///   2. `mode_id` + any of preset/prompt/provider/model → clone saved Mode
    ///      and apply overrides.
    ///   3. No `mode_id` but preset/prompt/provider/model present → build a
    ///      transient Mode from defaults and apply overrides.
    /// Never persisted.
    /// <para>
    /// `internal` rather than `private` so <c>HyperWhisper.SmokeTests</c> can pin
    /// the override rules directly. Shape 3 is the only one it exercises — the
    /// other two need <see cref="ModeService"/>.
    /// </para>
    /// </summary>
    internal static Mode BuildWorkingMode(SharedApi.PostProcessRequest req)
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
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{modeId}' is not a valid mode id");
            }
            var stored = ModeService.Instance.GetMode(guid);
            if (stored == null)
            {
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{modeId}'");
            }
            if (stored.PostProcessingMode == 0)
            {
                throw WindowsLocalApiFailure.Business(
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
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{modeId}' is not a valid mode id");
            }
            var stored = ModeService.Instance.GetMode(guid);
            if (stored == null)
            {
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{modeId}'");
            }
            mode = LocalApiTransientMode.Create(
                stored,
                "__local_api_pp_transient__",
                LocalApiTransientModeFields.Shared);
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
            // A LOCAL run resolves from `LocalPostProcessingModel ?? LanguageModel`
            // (`PostProcessingService.ProcessAsync`), so writing only
            // `LanguageModel` let a mode that already had a `LocalPostProcessingModel`
            // silently ignore the caller's `model` and run the baseline GGUF.
            //
            // KEYED ON THE PROVIDER, NOT ON `PostProcessingMode`. Windows decides
            // a local run from the provider STRING alone — `ProcessAsync` parses
            // `mode.PostProcessingProvider` and never reads `PostProcessingMode`
            // beyond the `== 0` skip — so a mode with post-processing switched off
            // (`PostProcessingMode == 0`) whose provider is still `"local_llm"`
            // runs the local GGUF as soon as the rule below promotes it to 1. A
            // mode-keyed guard skipped this write on exactly that shape and ran
            // the STALE GGUF, now honestly reported as a model the caller never
            // asked for. `FromString` is the same predicate `ProcessAsync` routes
            // on, so the two cannot disagree.
            //
            // The portable head keys on `PostProcessingMode == 2` instead
            // (`LinuxLocalApiAdapters.BuildWorkingModeAsync`) and is correct to:
            // its router requires mode 2 AND the provider string
            // (`LinuxPostProcessingRouter.ProcessAsync`). Same intent, different
            // routers.
            if (PostProcessingProviderExtensions.FromString(mode.PostProcessingProvider)
                == PostProcessingProvider.LocalLlm)
            {
                mode.LocalPostProcessingModel = model;
            }
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
            // (ModeDefaults.cs): ElevenLabs Scribe v2 + Anthropic Claude Haiku 5.5.
            CloudAccuracyTier = "elevenLabsScribeV2",
            CloudPostProcessingModel = "anthropic:claude-haiku-5-5",
            SortOrder = int.MaxValue,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
    }
}
