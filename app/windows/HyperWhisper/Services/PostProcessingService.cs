// POST-PROCESSING SERVICE
// Handles AI-powered text enhancement via LLM APIs (OpenAI, Anthropic, Groq, Grok).
// Takes raw transcription text and returns enhanced/formatted text based on mode settings.
//
// API INTEGRATION:
// - OpenAI: POST https://api.openai.com/v1/chat/completions
// - Anthropic: POST https://api.anthropic.com/v1/messages
// - Groq: POST https://api.groq.com/openai/v1/chat/completions (OpenAI-compatible)
// - Grok: POST https://api.x.ai/v1/chat/completions (OpenAI-compatible)
//
// ERROR HANDLING:
// - Returns original text on failure (graceful degradation)
// - Logs errors for debugging
// - HTTP providers time out after 30 seconds; Local LLM inference times out after 60 seconds

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
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
        foreach (var segment in segments)
        {
            var result = await ProcessAsync(segment, mode, applicationContext, cancellationToken);
            anyApplied |= result.WasApplied;
            var trimmed = result.Text.Trim();
            if (trimmed.Length > 0)
            {
                processed.Add(trimmed);
            }
        }

        return new PostProcessingResult(string.Join("\n\n", processed), anyApplied);
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

                var cloudUserMessage = PromptBuilder.WrapTranscript(text);
                var fullPrompt = $"{cloudSystemPrompt}\n\n{cloudSystemInfo}\n\n{cloudUserMessage}";

                LoggingService.Info("PostProcessingService: Processing with HyperWhisper Cloud");

                using var cloudService = new HyperWhisperCloudService();
                var cloudModel = CloudPostProcessingModelExtensions.FromString(mode.CloudPostProcessingModel);
                var response = await cloudService.PostProcessAsync(
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
                return PostProcessingResult.Applied(response);
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

        // Wrap the transcript with markers, prepending dynamic system info
        // System info is in the user message so the static system prompt benefits from caching
        var userMessage = systemInfo + "\n\n" + PromptBuilder.WrapTranscript(text);

        try
        {
            CompletionEvaluation evaluation;

            if (isCustomEndpoint)
            {
                var responseJson = await CallCustomEndpointAsync(mode, systemPrompt, userMessage, cancellationToken);
                evaluation = HyperwhisperCoreMethods.EvaluateLlmResponseJson(WireProtocol.OpenAiChat, responseJson, text);
            }
            else
            {
                LoggingService.Info($"PostProcessingService: Processing with {provider}/{resolvedModelId}");

                evaluation = provider switch
                {
                    PostProcessingProvider.OpenAI or PostProcessingProvider.Groq or PostProcessingProvider.Grok
                        or PostProcessingProvider.Gemini or PostProcessingProvider.Cerebras or PostProcessingProvider.Mistral =>
                        HyperwhisperCoreMethods.EvaluateLlmResponseJson(WireProtocol.OpenAiChat, await CallOpenAICompatibleAsync(MapToOpenAICompatibleProvider(provider), apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken), text),
                    PostProcessingProvider.Anthropic => HyperwhisperCoreMethods.EvaluateLlmResponseJson(WireProtocol.AnthropicMessages, await CallAnthropicAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken), text),
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

            return PostProcessingResult.Applied(evaluation.text);
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
    /// Calls any provider that implements the OpenAI Chat Completions protocol.
    /// Provider-specific behavior belongs in <see cref="OpenAICompatibleProviderExtensions"/>.
    /// </summary>
    private async Task<string> CallOpenAICompatibleAsync(
        OpenAICompatibleProvider provider,
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, provider.Endpoint());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            BuildOpenAIRequestJson(model, systemPrompt, userMessage),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    internal static string BuildOpenAIRequestJson(
        string model,
        string systemPrompt,
        string userMessage)
    {
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        };

        return JsonSerializer.Serialize(requestBody);
    }

    /// <summary>
    /// Maps the OpenAI-compatible subset of <see cref="PostProcessingProvider"/> to the
    /// dedicated <see cref="OpenAICompatibleProvider"/> enum used by <see cref="CallOpenAICompatibleAsync"/>.
    /// </summary>
    private static OpenAICompatibleProvider MapToOpenAICompatibleProvider(PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.OpenAI => OpenAICompatibleProvider.OpenAI,
        PostProcessingProvider.Groq => OpenAICompatibleProvider.Groq,
        PostProcessingProvider.Grok => OpenAICompatibleProvider.Grok,
        PostProcessingProvider.Gemini => OpenAICompatibleProvider.Gemini,
        PostProcessingProvider.Cerebras => OpenAICompatibleProvider.Cerebras,
        PostProcessingProvider.Mistral => OpenAICompatibleProvider.Mistral,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not OpenAI-compatible")
    };

    /// <summary>
    /// Calls the Anthropic Messages API.
    /// </summary>
    private async Task<string> CallAnthropicAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(
            BuildAnthropicRequestJson(model, systemPrompt, userMessage),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    internal static string BuildAnthropicRequestJson(
        string model,
        string systemPrompt,
        string userMessage)
    {
        // The static system prompt is cacheable; dynamic context stays in the user message.
        var systemContent = new[]
        {
            new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = systemPrompt,
                ["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" }
            }
        };

        var requestBody = new
        {
            model,
            max_tokens = 8192,
            system = systemContent,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        return JsonSerializer.Serialize(requestBody);
    }

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
    private async Task<string> CallCustomEndpointAsync(
        Mode mode,
        string systemPrompt,
        string userMessage,
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

        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, endpoint.EndpointURL);
        request.Content = new StringContent(
            BuildOpenAIRequestJson(endpoint.ModelName, systemPrompt, userMessage),
            Encoding.UTF8,
            "application/json"
        );

        // Add auth if API key is set (optional for local endpoints)
        var apiKey = CustomEndpointManager.Instance.GetApiKey(endpoint.Id);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
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

public readonly record struct PostProcessingResult(string Text, bool WasApplied)
{
    public static PostProcessingResult Applied(string text) => new(text, true);
    public static PostProcessingResult Skipped(string text) => new(text, false);
}

internal enum OpenAICompatibleProvider
{
    OpenAI,
    Groq,
    Grok,
    Gemini,
    Cerebras,
    Mistral
}

internal static class OpenAICompatibleProviderExtensions
{
    public static string Endpoint(this OpenAICompatibleProvider provider) => provider switch
    {
        OpenAICompatibleProvider.OpenAI => "https://api.openai.com/v1/chat/completions",
        OpenAICompatibleProvider.Groq => "https://api.groq.com/openai/v1/chat/completions",
        OpenAICompatibleProvider.Grok => "https://api.x.ai/v1/chat/completions",
        OpenAICompatibleProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        OpenAICompatibleProvider.Cerebras => "https://api.cerebras.ai/v1/chat/completions",
        OpenAICompatibleProvider.Mistral => "https://api.mistral.ai/v1/chat/completions",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
