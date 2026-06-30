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
                var cleanedText = PromptBuilder.ExtractCleanedTextLenient(response);
                if (string.IsNullOrWhiteSpace(cleanedText))
                {
                    LoggingService.Warn("PostProcessingService: Empty/markerless cleaned text from cloud; keeping original transcription");
                    return PostProcessingResult.Skipped(text);
                }
                LoggingService.Info($"PostProcessingService: Successfully processed ({text.Length} -> {cleanedText.Length} chars)");
                return PostProcessingResult.Applied(cleanedText);
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
            string response;

            if (isCustomEndpoint)
            {
                response = await CallCustomEndpointAsync(mode, text, systemPrompt, userMessage, cancellationToken);
            }
            else
            {
                LoggingService.Info($"PostProcessingService: Processing with {provider}/{resolvedModelId}");

                response = provider switch
                {
                    PostProcessingProvider.OpenAI => await CallOpenAIAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    PostProcessingProvider.Anthropic => await CallAnthropicAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    PostProcessingProvider.Groq => await CallGroqAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    PostProcessingProvider.Grok => await CallGrokAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    PostProcessingProvider.Gemini => await CallGeminiAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    PostProcessingProvider.Cerebras => await CallCerebrasAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    PostProcessingProvider.Mistral => await CallMistralAsync(apiKey!, resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    PostProcessingProvider.LocalLlm => await CallLocalLlmAsync(resolvedModelId!, systemPrompt, userMessage, cancellationToken),
                    _ => text
                };
            }

            // Extract the cleaned text from the response
            var cleanedText = PromptBuilder.ExtractCleanedTextLenient(response);
            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                // Empty output, or a response missing the <<CLEANED>> marker (PromptBuilder returns
                // empty in that case) — keep the original transcription rather than pasting empty
                // text or a prompt-echo.
                LoggingService.Warn("PostProcessingService: Empty/markerless cleaned text from model; keeping original transcription");
                return PostProcessingResult.Skipped(text);
            }
            LoggingService.Info($"PostProcessingService: Successfully processed ({text.Length} -> {cleanedText.Length} chars)");

            return PostProcessingResult.Applied(cleanedText);
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
    /// Calls the OpenAI Chat Completions API.
    /// </summary>
    private async Task<string> CallOpenAIAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

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
        // Use structured system content with cache_control for prompt caching
        // The system prompt is static per mode/preset and gets cached by Anthropic,
        // while dynamic content (time, app context, vocabulary) is in the user message.
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
            max_tokens = 4096,
            system = systemContent,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    /// <summary>
    /// Calls the Groq API (OpenAI-compatible endpoint).
    /// </summary>
    private async Task<string> CallGroqAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>
    /// Calls the xAI Grok API (OpenAI-compatible endpoint).
    /// </summary>
    private async Task<string> CallGrokAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>
    /// Calls the Google Gemini API (OpenAI-compatible endpoint).
    /// Gemini provides an OpenAI-compatible endpoint for easy integration.
    /// API Endpoint: https://generativelanguage.googleapis.com/v1beta/openai/chat/completions
    /// Auth: Bearer token (same as OpenAI)
    /// </summary>
    private async Task<string> CallGeminiAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>
    /// Calls the Cerebras API (OpenAI-compatible endpoint).
    /// Cerebras provides ultra-fast inference on custom silicon.
    /// API Endpoint: https://api.cerebras.ai/v1/chat/completions
    /// Auth: Bearer token (same as OpenAI)
    /// </summary>
    private async Task<string> CallCerebrasAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.cerebras.ai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>
    /// Calls the Mistral API (OpenAI-compatible endpoint).
    /// Mistral provides an OpenAI-compatible /chat/completions endpoint.
    /// API Endpoint: https://api.mistral.ai/v1/chat/completions
    /// Auth: Bearer token (same as OpenAI)
    /// </summary>
    private async Task<string> CallMistralAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.mistral.ai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
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
        string text,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        // Look up the custom endpoint
        var endpoint = CustomEndpointManager.Instance.EndpointFromProviderString(mode.PostProcessingProvider);
        if (endpoint == null)
        {
            LoggingService.Warn($"PostProcessingService: Custom endpoint not found for '{mode.PostProcessingProvider}'");
            return text;
        }

        LoggingService.Info($"PostProcessingService: Processing with custom endpoint '{endpoint.Name}' / {endpoint.ModelName}");

        var requestBody = new
        {
            model = endpoint.ModelName,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 4096
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.EndpointURL);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
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

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
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
