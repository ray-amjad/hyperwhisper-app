// POST-PROCESSING SERVICE
// Handles AI-powered text enhancement via LLM APIs (OpenAI, Anthropic, Groq, Grok).
// Takes raw transcription text and returns enhanced/formatted text based on mode settings.
//
// API INTEGRATION:
// Endpoints, JSON bodies, auth headers, the api.groq.com completion cap and the
// --TRANSCRIPT-- wrapper are NOT written here. They live in the shared Rust core
// (hw-net/src/providers/llm) and arrive through LlmPostProcessing.BuildRequest,
// so Windows, macOS and the Linux head all send the same bytes (issue #282).
//
// ERROR HANDLING:
// - Returns original text on failure (graceful degradation)
// - Logs errors for debugging
// - HTTP providers time out after 30 seconds; Local LLM inference times out after 60 seconds

using System.IO;
using System.Net.Http;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.SharedCore;
using HyperWhisper.Utilities;
// LLM completion termination/wrapper policy lives in the shared Rust core so all
// platforms share one implementation. See EvaluateLlmResponseJson / EvaluateCompletion.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Service for post-processing transcriptions using LLM APIs.
/// Implements IDisposable to properly clean up HttpClient.
/// </summary>
public class PostProcessingService : IDisposable
{
    // =========================================================================
    // HTTP CLIENT
    // =========================================================================

    private readonly HttpClient _httpClient;
    private readonly LocalLlmModelService _localLlmModelService = new();
    private readonly LocalLlmService _localLlmService = new();
    private bool _disposed;

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>
    /// Raised when post-processing fails and falls back to original text.
    /// </summary>
    public event EventHandler<ErrorToastEventArgs>? WarningOccurred;

    public PostProcessingService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Processes transcription text while honouring the paragraph breaks the user
    /// dictated ("new line" / "new paragraph").
    /// <para>
    /// The LLM will not keep a mid-body break, whatever the prompt says: measured
    /// against the cloud model, it merged a dictated "new paragraph" back into one
    /// paragraph on 5 of 5 runs — even with the break already inserted in its input
    /// and an explicit do-not-merge-paragraphs instruction (issue #1). So the break is never shown
    /// to the LLM. The transcript is split on the dictated commands, each segment is
    /// post-processed independently, and the breaks are restored afterwards.
    /// </para>
    /// <para>
    /// A transcript with no dictated break is a single segment and takes exactly the
    /// old path — one LLM call, no added latency for the common case.
    /// </para>
    /// </summary>
    public async Task<PostProcessingResult> ProcessPreservingBreaksAsync(
        string text,
        Mode mode,
        ApplicationContext? applicationContext = null,
        CancellationToken cancellationToken = default)
    {
        var segments = TranscriptionTextProcessing.SplitOnDictatedBreaks(text);
        if (segments.Count <= 1)
        {
            return await ProcessAsync(text, mode, applicationContext, cancellationToken);
        }

        LoggingService.Info($"PostProcessingService: dictated break(s) found — post-processing {segments.Count} segments separately");

        var processed = new List<string>(segments.Count);
        var anyApplied = false;
        // Segments here are always non-empty (SplitOnDictatedBreaks filters those
        // out) and share the same `mode`, so a per-segment ProcessAsync failure
        // (WasApplied == false) can only be a genuine runtime failure (provider
        // error, rejected/timed-out response, etc.) — never a settings-driven
        // skip, since settings-driven skips (post-processing disabled, no
        // provider, empty system prompt, ...) are uniform across every segment
        // in a single call and would already make `anyApplied` false overall.
        // Track that separately from the OR-aggregated `anyApplied` so a
        // partial failure (some segments applied, at least one did not) isn't
        // silently reported as a full success.
        var anyFailed = false;
        // Resolved provider/model (#314) are aggregated FIRST-NON-NULL rather than
        // last-write-wins: every segment shares one `mode`, so they can only
        // disagree if a provider fell back differently mid-request, and the first
        // segment that actually ran is the honest answer for the call as a whole.
        // A disagreement is logged, never asserted — Debug.Assert is compiled out
        // of Release, and failing a working transcription over a label would be
        // worse than the mislabel.
        string? resolvedProvider = null;
        string? resolvedModel = null;
        foreach (var segment in segments)
        {
            var result = await ProcessAsync(segment, mode, applicationContext, cancellationToken);
            anyApplied |= result.WasApplied;
            anyFailed |= !result.WasApplied;
            if (result.ResolvedProvider != null)
            {
                if (resolvedProvider == null)
                {
                    resolvedProvider = result.ResolvedProvider;
                }
                else if (!string.Equals(resolvedProvider, result.ResolvedProvider, StringComparison.Ordinal))
                {
                    LoggingService.Warn(
                        $"PostProcessingService: segments disagree on the provider that ran — reporting the first ('{resolvedProvider}'), saw '{result.ResolvedProvider}'");
                }
            }
            if (result.ResolvedModel != null)
            {
                if (resolvedModel == null)
                {
                    resolvedModel = result.ResolvedModel;
                }
                else if (!string.Equals(resolvedModel, result.ResolvedModel, StringComparison.Ordinal))
                {
                    LoggingService.Warn(
                        $"PostProcessingService: segments disagree on the model that ran — reporting the first ('{resolvedModel}'), saw '{result.ResolvedModel}'");
                }
            }
            var trimmed = result.Text.Trim();
            if (trimmed.Length > 0)
            {
                processed.Add(trimmed);
            }
        }

        return new PostProcessingResult(
            string.Join("\n\n", processed),
            anyApplied,
            AnyPartialFailure: anyApplied && anyFailed,
            ResolvedProvider: resolvedProvider,
            ResolvedModel: resolvedModel);
    }

    /// <summary>
    /// Processes transcription text using the LLM configured in the mode.
    /// </summary>
    /// <param name="text">The raw transcription text.</param>
    /// <param name="mode">The mode containing post-processing settings.</param>
    /// <param name="applicationContext">Optional application context for prompt enrichment.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The processed text, or the original text if processing fails or is disabled.</returns>
    public async Task<PostProcessingResult> ProcessAsync(
        string text,
        Mode mode,
        ApplicationContext? applicationContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            LoggingService.Debug("PostProcessingService: Empty transcript, skipping");
            return PostProcessingResult.Skipped(text);
        }

        // Check if post-processing is enabled
        if (mode.PostProcessingMode == 0)
        {
            LoggingService.Debug("PostProcessingService: Post-processing disabled for this mode");
            return PostProcessingResult.Skipped(text);
        }

        // Get the provider
        var isCustomEndpoint = CustomPostProcessingEndpoint.IsCustomProviderString(mode.PostProcessingProvider);
        var provider = isCustomEndpoint
            ? PostProcessingProvider.None
            : PostProcessingProviderExtensions.FromString(mode.PostProcessingProvider ?? "");

        if (!isCustomEndpoint && provider == PostProcessingProvider.None)
        {
            LoggingService.Debug("PostProcessingService: No provider configured");
            return PostProcessingResult.Skipped(text);
        }

        // HyperWhisper Cloud uses license/device auth, not API keys — handle separately
        if (provider == PostProcessingProvider.HyperWhisperCloud)
        {
            try
            {
                var cloudVocabulary = VocabularyService.Instance.GetVocabularyWords(100);
                var cloudSystemPrompt = PromptBuilder.SystemPrompt(mode, applicationContext);
                if (string.IsNullOrEmpty(cloudSystemPrompt))
                {
                    LoggingService.Debug("PostProcessingService: Empty system prompt, skipping");
                    return PostProcessingResult.Skipped(text);
                }
                var cloudSystemInfo = PromptBuilder.SystemInfo(mode, cloudVocabulary, applicationContext);

                // The transcript travels in `text`, ONCE. It used to be sent twice
                // here — again inside `prompt`, wrapped in --TRANSCRIPT-- markers —
                // which macOS never did. That is a different input-token count, a
                // different credit cost and a different prompt for the same
                // recording (#282). The hosted route builds its own provider call
                // from `prompt`, so `prompt` carries only the two prompt halves.
                var fullPrompt = $"{cloudSystemPrompt}\n\n{cloudSystemInfo}";

                LoggingService.Info("PostProcessingService: Processing with HyperWhisper Cloud");

                using var cloudService = new HyperWhisperCloudService();
                var cloudModel = CloudPostProcessingModelExtensions.FromString(mode.CloudPostProcessingModel);
                var (response, servedModel) = await cloudService.PostProcessAsync(
                    text,
                    fullPrompt,
                    cloudModel.ToLlmProviderHeader(),
                    cloudModel.ToLlmModelHeader(),
                    cancellationToken);
                // The hosted /post-process contract already validates provider termination
                // and strips wrapper markers before returning `corrected`. Do not apply the
                // provider-native wrapper contract a second time on this normalized response.
                if (string.IsNullOrWhiteSpace(response))
                {
                    LoggingService.Warn("PostProcessingService: Empty cloud response; keeping original transcription");
                    return PostProcessingResult.Skipped(text);
                }
                LoggingService.Info($"PostProcessingService: Successfully processed ({text.Length} -> {response.Length} chars)");
                // Record what RAN, not what the Mode stored (#314). This branch
                // never reads `mode.LanguageModel` — the engine comes from
                // `mode.CloudPostProcessingModel` — so echoing `LanguageModel`
                // reported an unrelated field, not just a stale one.
                //
                // PREFER WHAT THE BACKEND SERVED. The hosted /post-process route
                // runs its OWN provider fallback — a 5xx on the primary provider,
                // or a prompt-leakage reroute — and names the (provider, model)
                // pair that actually answered in the `X-LLM-Provider` RESPONSE
                // header. The `X-LLM-Model` value we sent is only what we ASKED
                // for, so reporting it after a server-side reroute repeats #314
                // one level deeper. Fall back to the requested value only when the
                // header is absent (older backend, or a proxy stripped it).
                return PostProcessingResult.Applied(
                    response,
                    PostProcessingProvider.HyperWhisperCloud.ToStringValue(),
                    servedModel ?? cloudModel.ToLlmModelHeader() ?? cloudModel.ModelId);
            }
            catch (OperationCanceledException)
            {
                LoggingService.Info("PostProcessingService: Operation cancelled");
                return PostProcessingResult.Skipped(text);
            }
            catch (HttpRequestException ex)
            {
                LoggingService.Error($"PostProcessingService: HTTP error: {ex.Message}");
                WarningOccurred?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("postprocessing.error.failed")));
                return PostProcessingResult.Skipped(text);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"PostProcessingService: Failed: {ex.Message}");
                WarningOccurred?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("postprocessing.error.failed")));
                return PostProcessingResult.Skipped(text);
            }
        }

        // For built-in providers, get the API key and model
        string? apiKey = null;
        string? resolvedModelId = null;
        if (!isCustomEndpoint)
        {
            if (provider.RequiresApiKey())
            {
                apiKey = ApiKeyService.Instance.GetApiKey(provider);
                if (string.IsNullOrEmpty(apiKey))
                {
                    LoggingService.Warn($"PostProcessingService: No API key configured for {provider}");
                    WarningOccurred?.Invoke(this, new ErrorToastEventArgs(
                        Loc.S("postprocessing.error.apiKeyNotSet"),
                        showSettingsButton: true,
                        openApiKeysManager: true));
                    return PostProcessingResult.Skipped(text);
                }
            }

            var selectedModelId = provider == PostProcessingProvider.LocalLlm
                ? mode.LocalPostProcessingModel ?? mode.LanguageModel
                : mode.LanguageModel;
            var modelIdMigrated = LanguageModelInfo.MigrateModelId(selectedModelId);
            var model = LanguageModelInfo.GetById(modelIdMigrated ?? "");
            if (model == null || model.Provider != provider)
            {
                var fallback = LanguageModelInfo.GetDefaultForProvider(provider);
                if (fallback == null)
                {
                    LoggingService.Warn($"PostProcessingService: Unknown model '{selectedModelId}' for {provider}");
                    return PostProcessingResult.Skipped(text);
                }

                LoggingService.Warn($"PostProcessingService: Unknown model '{selectedModelId}' for {provider}; using {fallback.Id}");
                model = fallback;
            }
            resolvedModelId = model.Id;
        }

        // Fetch global vocabulary words for prompt context
        var vocabulary = VocabularyService.Instance.GetVocabularyWords(100);

        // Build the static system prompt (cached across requests) and dynamic system info
        var systemPrompt = PromptBuilder.SystemPrompt(mode, applicationContext);
        if (string.IsNullOrEmpty(systemPrompt))
        {
            LoggingService.Debug("PostProcessingService: Empty system prompt, skipping");
            return PostProcessingResult.Skipped(text);
        }
        var systemInfo = PromptBuilder.SystemInfo(mode, vocabulary, applicationContext);

        // Wrap the transcript with markers, prepending dynamic system info.
        // System info rides in the user message so the static system prompt stays
        // byte-identical between requests and keeps its cache hit. The wrapper is
        // the shared one (#282): every HTTP provider gets it from the Rust request
        // builder, and the in-process local LLM below asks for the same string so
        // the two cannot drift.
        var userMessage = LlmPostProcessing.WrapTranscript(systemInfo, text);

        // The custom-endpoint model name is only known inside
        // CallCustomEndpointAsync — it comes from the lenient validator, which can
        // repair the endpoint's stored ModelName — so it is handed back here for
        // the resolved label (#314) rather than looked up a second time.
        string? customEndpointModel = null;

        try
        {
            CompletionEvaluation evaluation;

            if (isCustomEndpoint)
            {
                var (responseJson, endpointModel) = await CallCustomEndpointAsync(mode, systemPrompt, systemInfo, text, cancellationToken);
                customEndpointModel = endpointModel;
                evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(WireProtocol.OpenAiChat, responseJson, text);
            }
            else
            {
                LoggingService.Info($"PostProcessingService: Processing with {provider}/{resolvedModelId}");

                evaluation = provider switch
                {
                    // One arm for every HTTP provider: the endpoint, the body shape
                    // and the auth header all come from the shared builder, so
                    // Anthropic no longer needs a branch of its own here.
                    PostProcessingProvider.OpenAI or PostProcessingProvider.Groq or PostProcessingProvider.Grok
                        or PostProcessingProvider.Gemini or PostProcessingProvider.Cerebras
                        or PostProcessingProvider.Mistral or PostProcessingProvider.Anthropic =>
                        HyperwhisperCoreMethods.EvaluateLlmResponseJson(
                            provider == PostProcessingProvider.Anthropic
                                ? WireProtocol.AnthropicMessages
                                : WireProtocol.OpenAiChat,
                            await CallLlmAsync(
                                MapToPortableProvider(provider), apiKey!, resolvedModelId!,
                                systemPrompt, systemInfo, text, cancellationToken),
                            text),
                    PostProcessingProvider.LocalLlm => HyperwhisperCoreMethods.EvaluateCompletion(text, await CallLocalLlmAsync(resolvedModelId!, systemPrompt, userMessage, cancellationToken), CompletionState.Unspecified),
                    _ => HyperwhisperCoreMethods.EvaluateCompletion(text, "", CompletionState.Malformed)
                };
            }

            if (!evaluation.accepted)
            {
                LoggingService.Warn($"PostProcessingService: Response rejected ({evaluation.failure}); keeping original transcription");
                WarningOccurred?.Invoke(this, new ErrorToastEventArgs(Loc.S("postprocessing.error.failed")));
                return PostProcessingResult.Skipped(evaluation.text);
            }
            LoggingService.Info($"PostProcessingService: Successfully processed ({text.Length} -> {evaluation.text.Length} chars)");

            // Record what RAN (#314). For a custom endpoint the caller's own
            // `custom:<guid>` string IS the resolved provider — it is already the
            // wire vocabulary — and the model is the validator's repaired name.
            // Otherwise `resolvedModelId` is the post-fallback id that went into
            // the request body: the migrated id, or the provider default that
            // replaced a model the Mode named for another provider.
            return PostProcessingResult.Applied(
                evaluation.text,
                isCustomEndpoint ? mode.PostProcessingProvider : provider.ToStringValue(),
                isCustomEndpoint ? customEndpointModel : resolvedModelId);
        }
        catch (OperationCanceledException)
        {
            LoggingService.Info("PostProcessingService: Operation cancelled");
            return PostProcessingResult.Skipped(text);
        }
        catch (HttpRequestException ex)
        {
            LoggingService.Error($"PostProcessingService: HTTP error: {ex.Message}");
            WarningOccurred?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("postprocessing.error.failed")));
            return PostProcessingResult.Skipped(text);
        }
        catch (FileNotFoundException ex) when (ex.Message.Contains("Local LLM model", StringComparison.OrdinalIgnoreCase))
        {
            LoggingService.Error($"PostProcessingService: Local LLM model missing: {ex.Message}");
            WarningOccurred?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("postprocessing.error.failed"),
                showSettingsButton: true,
                settingsSection: "Models"));
            return PostProcessingResult.Skipped(text);
        }
        catch (Exception ex) when (!isCustomEndpoint && provider == PostProcessingProvider.LocalLlm)
        {
            LoggingService.Error($"PostProcessingService: Local LLM failed: {ex.Message}");
            WarningOccurred?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("postprocessing.error.failed"),
                showSettingsButton: true,
                settingsSection: "Models"));
            return PostProcessingResult.Skipped(text);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"PostProcessingService: Failed: {ex.Message}");
            WarningOccurred?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("postprocessing.error.failed")));
            return PostProcessingResult.Skipped(text);
        }
    }

    // =========================================================================
    // API IMPLEMENTATIONS
    // =========================================================================

    /// <summary>
    /// Calls any HTTP post-processing provider.
    /// </summary>
    /// <remarks>
    /// One method for all of them. The endpoint, the JSON body, the auth header
    /// and the Groq completion cap come from the shared Rust builder (#282), so
    /// there is nothing provider-specific left to branch on here. What stays
    /// native is the transport: this HttpClient and its 30-second timeout.
    /// </remarks>
    private async Task<string> CallLlmAsync(
        PortableLlmProvider provider,
        string apiKey,
        string model,
        string systemPrompt,
        string systemInfo,
        string transcript,
        CancellationToken cancellationToken)
    {
        using var request = LlmPostProcessing.BuildRequest(new PortableLlmRequest(
            provider, model, apiKey, systemPrompt, systemInfo, transcript));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Maps a Windows <see cref="PostProcessingProvider"/> onto the shared
    /// provider enum the Rust builder takes.
    /// </summary>
    internal static PortableLlmProvider MapToPortableProvider(PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.OpenAI => PortableLlmProvider.OpenAi,
        PostProcessingProvider.Anthropic => PortableLlmProvider.Anthropic,
        PostProcessingProvider.Groq => PortableLlmProvider.Groq,
        PostProcessingProvider.Grok => PortableLlmProvider.Grok,
        PostProcessingProvider.Gemini => PortableLlmProvider.Gemini,
        PostProcessingProvider.Cerebras => PortableLlmProvider.Cerebras,
        PostProcessingProvider.Mistral => PortableLlmProvider.Mistral,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider has no shared LLM builder arm")
    };


    /// <summary>
    /// Calls the local LLamaSharp runtime for offline post-processing.
    /// </summary>
    private async Task<string> CallLocalLlmAsync(
        string modelId,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        if (!PlatformHelper.SupportsLocalLlmPostProcessing)
        {
            throw new PlatformNotSupportedException(
                "Local LLM post-processing is not supported by this Windows architecture.");
        }

        var model = LocalLlmModelInfo.GetById(modelId) ?? LocalLlmModelInfo.GetDefault();
        if (!_localLlmModelService.IsModelDownloaded(model))
        {
            throw new FileNotFoundException(
                Loc.S("settings.models.localLlm.missingModel", model.DisplayName),
                _localLlmModelService.GetModelPath(model));
        }

        var modelPath = _localLlmModelService.GetModelPath(model);
        LoggingService.Info($"PostProcessingService: Processing with local LLM {model.DisplayName}");
        return await _localLlmService.GenerateAsync(modelPath, systemPrompt, userMessage, cancellationToken);
    }

    // =========================================================================
    // CUSTOM ENDPOINT
    // =========================================================================

    /// <summary>
    /// Calls a custom OpenAI-compatible endpoint for post-processing.
    /// Prompts are built by the caller (ProcessAsync) to avoid duplication.
    /// </summary>
    /// <remarks>
    /// The URL is validated leniently, not strictly. A user's saved endpoint may
    /// predate the tightened rule (#282), and silently skipping post-processing
    /// forever is the failure this change exists to stop — so an endpoint that is
    /// still safe to call keeps working, with a warning naming the repair.
    /// </remarks>
    /// <returns>
    /// The raw response JSON, plus the model name actually called — the
    /// validator's <c>Model</c>, which can differ from the endpoint's stored
    /// <c>ModelName</c>. The caller needs the second value for the resolved
    /// label on <see cref="PostProcessingResult"/> (#314); it is returned here
    /// rather than re-derived so the endpoint lookup is not duplicated.
    /// </returns>
    private async Task<(string ResponseJson, string Model)> CallCustomEndpointAsync(
        Mode mode,
        string systemPrompt,
        string systemInfo,
        string transcript,
        CancellationToken cancellationToken)
    {
        // Look up the custom endpoint
        var endpoint = CustomEndpointManager.Instance.EndpointFromProviderString(mode.PostProcessingProvider);
        if (endpoint == null)
        {
            LoggingService.Warn($"PostProcessingService: Custom endpoint not found for '{mode.PostProcessingProvider}'");
            throw new InvalidOperationException($"Custom endpoint not found for '{mode.PostProcessingProvider}'");
        }

        LoggingService.Info($"PostProcessingService: Processing with custom endpoint '{endpoint.Name}' / {endpoint.ModelName}");

        var verdict = LlmPostProcessing.ValidateExistingCustomEndpoint(
            endpoint.EndpointURL, endpoint.ModelName);
        if (verdict.Status == PortableEndpointStatus.NeedsRepair)
        {
            LoggingService.Warn(
                $"PostProcessingService: Custom endpoint '{endpoint.Name}' needs repair ({verdict.Message}); "
                + $"suggested URL: {verdict.Suggestion ?? "none"}");
        }
        if (!verdict.IsUsable)
        {
            throw new InvalidOperationException(
                $"Custom endpoint '{endpoint.Name}' cannot be called: {verdict.Message}");
        }

        var apiKey = CustomEndpointManager.Instance.GetApiKey(endpoint.Id);
        using var request = LlmPostProcessing.BuildRequest(new PortableLlmRequest(
            PortableLlmProvider.Custom,
            verdict.Model,
            apiKey ?? string.Empty,
            systemPrompt,
            systemInfo,
            transcript,
            CustomEndpoint: verdict.Url));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadAsStringAsync(cancellationToken), verdict.Model);
    }

    // =========================================================================
    // IDISPOSABLE
    // =========================================================================

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _httpClient.Dispose();
                _localLlmService.Dispose();
            }
            _disposed = true;
        }
    }
}

/// <param name="Text">The (possibly post-processed) text.</param>
/// <param name="WasApplied">True if at least one LLM call actually ran and
/// produced this text (OR-aggregated across segments for
/// <see cref="PostProcessingService.ProcessPreservingBreaksAsync"/>).</param>
/// <param name="AnyPartialFailure">True only for a multi-segment call where
/// some segments applied and at least one did not — i.e. <paramref name="WasApplied"/>
/// is `true` but the result is a mix of processed and raw/unprocessed segment
/// text. Callers that only check <see cref="WasApplied"/> would otherwise
/// treat this as a full success.</param>
/// <param name="ResolvedProvider">The provider that ACTUALLY produced
/// <paramref name="Text"/> — not the one stored on the Mode (issue #314). Speaks
/// the same vocabulary as <c>Mode.PostProcessingProvider</c>: a
/// <see cref="PostProcessingProviderExtensions.ToStringValue"/> result
/// (<c>"anthropic"</c>, <c>"local_llm"</c>, …) or a <c>custom:&lt;guid&gt;</c>
/// string, so a caller can put it on the wire unchanged. Written ONLY by
/// <see cref="PostProcessingResult.Applied"/>, so a non-null value means "an LLM
/// ran, and this is what it was" — a run that resolves a model and then fails
/// leaves no stale label behind, and a reader needs no
/// <paramref name="WasApplied"/> cross-check. Null when nothing ran; readers
/// then fall back to the Mode's stored labels.</param>
/// <param name="ResolvedModel">See <paramref name="ResolvedProvider"/>. The
/// post-fallback model id: the replacement that stood in for an unknown or
/// retired id, the <c>X-LLM-Model</c> value the cloud route really sent, or the
/// model name the custom-endpoint validator repaired to.</param>
public readonly record struct PostProcessingResult(
    string Text,
    bool WasApplied,
    bool AnyPartialFailure = false,
    string? ResolvedProvider = null,
    string? ResolvedModel = null)
{
    public static PostProcessingResult Applied(string text, string? provider = null, string? model = null) =>
        new(text, true, ResolvedProvider: provider, ResolvedModel: model);
    public static PostProcessingResult Skipped(string text) => new(text, false);
}

// `OpenAICompatibleProvider` and its endpoint table used to live here. It was the
// Windows copy of a URL list that also existed in PostProcessingProvider.swift and
// CloudPostProcessingService.cs; the three were byte-identical and only the arm
// count drifted. The one copy now lives in hw-net (issue #282).
