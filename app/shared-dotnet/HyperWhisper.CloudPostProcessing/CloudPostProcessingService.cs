using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HyperWhisper.SharedCore;

namespace HyperWhisper.CloudPostProcessing;

public sealed class CloudPostProcessingService : IDisposable
{
    private const int MaxRequestCharacters = 1_000_000;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int GroqMaxCompletionTokens = 4096;
    private static readonly Uri HyperWhisperCloudEndpoint =
        new("https://transcribe-prod-v2.hyperwhisper.com/post-process");
    private static readonly IReadOnlyDictionary<CloudPostProcessingProvider, Uri> Endpoints =
        new Dictionary<CloudPostProcessingProvider, Uri>
        {
            [CloudPostProcessingProvider.OpenAi] = new("https://api.openai.com/v1/chat/completions"),
            [CloudPostProcessingProvider.Anthropic] = new("https://api.anthropic.com/v1/messages"),
            [CloudPostProcessingProvider.Groq] = new("https://api.groq.com/openai/v1/chat/completions"),
            [CloudPostProcessingProvider.Grok] = new("https://api.x.ai/v1/chat/completions"),
            [CloudPostProcessingProvider.Gemini] = new("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"),
            [CloudPostProcessingProvider.Cerebras] = new("https://api.cerebras.ai/v1/chat/completions"),
            [CloudPostProcessingProvider.Mistral] = new("https://api.mistral.ai/v1/chat/completions"),
        };

    private readonly IPostProcessingCredentialSource _credentials;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HyperWhisperCloudCatalog _cloudCatalog;
    private bool _disposed;

    public CloudPostProcessingService(
        IPostProcessingCredentialSource credentials,
        HttpClient? httpClient = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
        _cloudCatalog = HyperWhisperCloudCatalog.Load();
    }

    public async Task<CloudPostProcessingResult> ProcessAsync(
        CloudPostProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request);
        if (validation is not null)
        {
            return CloudPostProcessingResult.Failed(
                request.Transcript ?? string.Empty,
                CloudPostProcessingFailureCode.InvalidRequest,
                validation);
        }

        var transcript = request.Transcript;
        var userMessage = string.Concat(
            request.SystemInfo,
            "\n\n--TRANSCRIPT--\n",
            transcript,
            "\n--ENDTRANSCRIPT--");

        try
        {
            return request.Provider switch
            {
                CloudPostProcessingProvider.HyperWhisperCloud =>
                    await ProcessHyperWhisperCloudAsync(request, transcript, userMessage, cancellationToken),
                CloudPostProcessingProvider.Custom =>
                    await ProcessCustomAsync(request, transcript, userMessage, cancellationToken),
                _ => await ProcessByokAsync(request, transcript, userMessage, cancellationToken),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.Cancelled, "Post-processing was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.TimedOut, "The post-processing provider timed out.");
        }
        catch (HttpRequestException)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.RequestFailed, "The post-processing provider request failed.");
        }
        catch (JsonException)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.RejectedResponse, "The post-processing provider returned an invalid response.");
        }
        catch (IOException)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.RejectedResponse, "The post-processing provider response was too large or incomplete.");
        }
        catch (DecoderFallbackException)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.RejectedResponse, "The post-processing provider returned invalid text encoding.");
        }
        catch (Exception)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.RequestFailed, "Post-processing failed.");
        }
    }

    private async Task<CloudPostProcessingResult> ProcessByokAsync(
        CloudPostProcessingRequest request,
        string transcript,
        string userMessage,
        CancellationToken cancellationToken)
    {
        if (!Endpoints.TryGetValue(request.Provider, out var endpoint))
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.ProviderUnavailable, "The post-processing provider is not supported.");
        }
        var model = PostProcessingModelCatalog.ResolveModel(request.Provider, request.Model);
        if (model is null)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.ProviderUnavailable, "The post-processing provider has no available model.");
        }
        var credential = await _credentials.GetCredentialAsync(request.Provider, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(credential?.ApiKey))
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.MissingCredential, "An API key is required for this post-processing provider.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (request.Provider == CloudPostProcessingProvider.Anthropic)
        {
            message.Headers.TryAddWithoutValidation("x-api-key", credential.ApiKey);
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            message.Content = JsonContent(BuildAnthropicBody(model, request.SystemPrompt, userMessage));
        }
        else
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.ApiKey);
            message.Content = JsonContent(BuildOpenAiBody(
                model, request.SystemPrompt, userMessage,
                request.Provider == CloudPostProcessingProvider.Groq));
        }

        var responseJson = await SendAsync(message, cancellationToken);
        var wire = request.Provider == CloudPostProcessingProvider.Anthropic
            ? PortableLlmWireProtocol.AnthropicMessages
            : PortableLlmWireProtocol.OpenAiChat;
        return Evaluate(responseJson, wire, transcript, $"{DisplayName(request.Provider)} · {model}");
    }

    private async Task<CloudPostProcessingResult> ProcessCustomAsync(
        CloudPostProcessingRequest request,
        string transcript,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var custom = request.CustomEndpoint!;
        var endpoint = new Uri(custom.EndpointUrl.Trim(), UriKind.Absolute);
        var credential = await _credentials.GetCredentialAsync(
            CloudPostProcessingProvider.Custom, custom.Id, cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(credential?.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.ApiKey);
        }
        message.Content = JsonContent(BuildOpenAiBody(
            custom.Model.Trim(), request.SystemPrompt, userMessage,
            endpoint.Host.Equals("api.groq.com", StringComparison.OrdinalIgnoreCase)));
        var responseJson = await SendAsync(message, cancellationToken);
        return Evaluate(responseJson, PortableLlmWireProtocol.OpenAiChat, transcript,
            $"Custom endpoint · {custom.Model.Trim()}");
    }

    private async Task<CloudPostProcessingResult> ProcessHyperWhisperCloudAsync(
        CloudPostProcessingRequest request,
        string transcript,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var route = _cloudCatalog.Resolve(request.HyperWhisperCloudModel);
        if (route is null)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.ProviderUnavailable,
                "The HyperWhisper Cloud post-processing catalog is unavailable.");
        }
        var credential = await _credentials.GetCredentialAsync(
            CloudPostProcessingProvider.HyperWhisperCloud, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(credential?.LicenseKey)
            && string.IsNullOrWhiteSpace(credential?.DeviceId))
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.MissingCredential,
                "A HyperWhisper account or device identity is required.");
        }

        var body = new Dictionary<string, string>
        {
            ["text"] = transcript,
            ["prompt"] = string.Concat(request.SystemPrompt, "\n\n", userMessage),
        };
        if (!string.IsNullOrWhiteSpace(credential!.LicenseKey)) body["license_key"] = credential.LicenseKey;
        else body["device_id"] = credential.DeviceId!;

        using var message = new HttpRequestMessage(HttpMethod.Post, HyperWhisperCloudEndpoint);
        message.Headers.TryAddWithoutValidation("X-LLM-Provider", route.Value.ProviderHeader);
        message.Headers.TryAddWithoutValidation("X-LLM-Model", route.Value.ModelHeader);
        message.Content = JsonContent(body);
        var responseJson = await SendAsync(message, cancellationToken);
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("corrected", out var correctedElement)
            || correctedElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(correctedElement.GetString()))
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.RejectedResponse,
                "HyperWhisper Cloud returned an invalid response.");
        }
        return CloudPostProcessingResult.Applied(correctedElement.GetString()!.Trim(), route.Value.Label);
    }

    private static CloudPostProcessingResult Evaluate(
        string responseJson,
        PortableLlmWireProtocol protocol,
        string transcript,
        string provider)
    {
        var evaluation = SharedCoreBridge.EvaluateLlmResponseJson(protocol, responseJson, transcript);
        return evaluation.Accepted
            ? CloudPostProcessingResult.Applied(evaluation.Text, provider)
            : CloudPostProcessingResult.Failed(
                evaluation.Text,
                CloudPostProcessingFailureCode.RejectedResponse,
                "The post-processing provider response was rejected.");
    }

    private async Task<string> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "The post-processing provider returned an unsuccessful status.",
                null,
                response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new IOException("The post-processing response exceeds the size limit.");
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (target.Length + read > MaxResponseBytes)
            {
                throw new IOException("The post-processing response exceeds the size limit.");
            }
            target.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(target.GetBuffer(), 0, checked((int)target.Length));
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static object BuildOpenAiBody(
        string model,
        string systemPrompt,
        string userMessage,
        bool isGroq)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };
        if (isGroq) body["max_completion_tokens"] = GroqMaxCompletionTokens;
        return body;
    }

    private static object BuildAnthropicBody(string model, string systemPrompt, string userMessage) => new
    {
        model,
        max_tokens = 8192,
        system = new object[]
        {
            new
            {
                type = "text",
                text = systemPrompt,
                cache_control = new { type = "ephemeral" },
            },
        },
        messages = new object[] { new { role = "user", content = userMessage } },
    };

    private static string? Validate(CloudPostProcessingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Transcript)) return "A transcript is required.";
        if (request.Transcript.Length > MaxRequestCharacters) return "The transcript is too large.";
        if (string.IsNullOrWhiteSpace(request.SystemPrompt)) return "A system prompt is required.";
        if (request.SystemPrompt.Length > MaxRequestCharacters) return "The system prompt is too large.";
        if (request.SystemInfo is null) return "Dynamic prompt context is required.";
        if (request.SystemInfo.Length > MaxRequestCharacters) return "The dynamic prompt context is too large.";
        if (request.Provider != CloudPostProcessingProvider.Custom) return null;
        if (request.CustomEndpoint is not { } custom) return "A custom endpoint configuration is required.";
        if (custom.Id == Guid.Empty) return "A custom endpoint identifier is required.";
        if (string.IsNullOrWhiteSpace(custom.Model) || custom.Model.Trim().Length > 256)
            return "A valid custom endpoint model is required.";
        if (!Uri.TryCreate(custom.EndpointUrl?.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || custom.EndpointUrl.Length > 2048)
            return "A valid HTTP or HTTPS custom endpoint URL is required.";
        return null;
    }

    private static string DisplayName(CloudPostProcessingProvider provider) => provider switch
    {
        CloudPostProcessingProvider.OpenAi => "OpenAI",
        CloudPostProcessingProvider.Anthropic => "Anthropic",
        CloudPostProcessingProvider.Groq => "Groq",
        CloudPostProcessingProvider.Grok => "Grok",
        CloudPostProcessingProvider.Gemini => "Gemini",
        CloudPostProcessingProvider.Cerebras => "Cerebras",
        CloudPostProcessingProvider.Mistral => "Mistral",
        _ => provider.ToString(),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
