using System.Net;
using System.Text;
using System.Text.Json;
using HyperWhisper.SharedCore;

namespace HyperWhisper.CloudPostProcessing;

/// <summary>
/// Portable I/O shell over the Rust-owned LLM post-processing contract.
/// </summary>
/// <remarks>
/// Since issue #282 the endpoint table, both JSON bodies, the auth headers, the
/// <c>api.groq.com</c> host sniff, the <c>--TRANSCRIPT--</c> wrapper and
/// custom-endpoint validation all live in <c>hw-net</c> and reach this class
/// through <see cref="LlmPostProcessing"/>. What is left here is HTTP: the send
/// loop, the response-size cap, cancellation and failure classification.
/// </remarks>
public sealed class CloudPostProcessingService : IDisposable
{
    private const int MaxRequestCharacters = 1_000_000;
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    private readonly IPostProcessingCredentialSource _credentials;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HyperWhisperCloudCatalog _cloudCatalog;
    private readonly string _hyperWhisperCloudBaseUrl;
    private bool _disposed;

    /// <param name="credentials">Where API keys, license keys and the device id come from.</param>
    /// <param name="httpClient">Injected for tests; the service owns one otherwise.</param>
    /// <param name="hyperWhisperCloudBaseUrl">
    /// Cloud host override. Null uses <see cref="LlmPostProcessing.DefaultHyperWhisperCloudBaseUrl"/>,
    /// which is DEBUG-aware. Before #282 this class hardcoded the production host
    /// with no switch at all, so every dev run on the Linux head billed
    /// production credits. A head with its own environment switch (Windows
    /// <c>NetworkConfig</c>) should pass it here.
    /// </param>
    public CloudPostProcessingService(
        IPostProcessingCredentialSource credentials,
        HttpClient? httpClient = null,
        string? hyperWhisperCloudBaseUrl = null)
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
        _hyperWhisperCloudBaseUrl = string.IsNullOrWhiteSpace(hyperWhisperCloudBaseUrl)
            ? LlmPostProcessing.DefaultHyperWhisperCloudBaseUrl
            : hyperWhisperCloudBaseUrl;
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

        try
        {
            return request.Provider switch
            {
                CloudPostProcessingProvider.HyperWhisperCloud =>
                    await ProcessHyperWhisperCloudAsync(request, transcript, cancellationToken),
                CloudPostProcessingProvider.Custom =>
                    await ProcessCustomAsync(request, transcript, cancellationToken),
                _ => await ProcessByokAsync(request, transcript, cancellationToken),
            };
        }
        catch (PortableLlmRequestException exception)
        {
            // The inputs could not produce a request at all. This is the same
            // class of problem `Validate` catches, so report it the same way —
            // and never as a transport failure.
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.InvalidRequest, exception.Message);
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
        CancellationToken cancellationToken)
    {
        var provider = MapProvider(request.Provider);
        if (provider is null)
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

        using var message = LlmPostProcessing.BuildRequest(new PortableLlmRequest(
            provider.Value,
            model,
            credential.ApiKey,
            request.SystemPrompt,
            request.SystemInfo,
            transcript));

        var responseJson = await SendAsync(message, cancellationToken);
        return Evaluate(
            responseJson,
            LlmPostProcessing.WireProtocolFor(provider.Value),
            transcript,
            $"{DisplayName(request.Provider)} · {model}");
    }

    private async Task<CloudPostProcessingResult> ProcessCustomAsync(
        CloudPostProcessingRequest request,
        string transcript,
        CancellationToken cancellationToken)
    {
        var custom = request.CustomEndpoint!;
        var credential = await _credentials.GetCredentialAsync(
            CloudPostProcessingProvider.Custom, custom.Id, cancellationToken);

        using var message = LlmPostProcessing.BuildRequest(new PortableLlmRequest(
            PortableLlmProvider.Custom,
            custom.Model.Trim(),
            credential?.ApiKey ?? string.Empty,
            request.SystemPrompt,
            request.SystemInfo,
            transcript,
            CustomEndpoint: custom.EndpointUrl));

        var responseJson = await SendAsync(message, cancellationToken);
        return Evaluate(responseJson, PortableLlmWireProtocol.OpenAiChat, transcript,
            $"Custom endpoint · {custom.Model.Trim()}");
    }

    private async Task<CloudPostProcessingResult> ProcessHyperWhisperCloudAsync(
        CloudPostProcessingRequest request,
        string transcript,
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

        using var message = LlmPostProcessing.BuildRequest(new PortableLlmRequest(
            PortableLlmProvider.HyperWhisperCloud,
            Model: string.Empty,
            ApiKey: string.Empty,
            request.SystemPrompt,
            request.SystemInfo,
            transcript,
            BaseUrl: _hyperWhisperCloudBaseUrl,
            LicenseKey: credential!.LicenseKey,
            DeviceId: credential.DeviceId,
            LlmProviderHeader: route.Value.ProviderHeader,
            LlmModelHeader: route.Value.ModelHeader));

        var responseJson = await SendAsync(message, cancellationToken);
        string corrected;
        try
        {
            // The hosted contract already validates provider termination and
            // strips the wrapper markers. Do NOT apply the provider-native
            // wrapper contract a second time on this normalized response.
            corrected = LlmPostProcessing.ParseHyperWhisperCloudResponse(responseJson);
        }
        catch (PortableLlmRequestException)
        {
            return CloudPostProcessingResult.Failed(
                transcript, CloudPostProcessingFailureCode.RejectedResponse,
                "HyperWhisper Cloud returned an invalid response.");
        }
        return CloudPostProcessingResult.Applied(corrected, route.Value.Label);
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

    private static PortableLlmProvider? MapProvider(CloudPostProcessingProvider provider) => provider switch
    {
        CloudPostProcessingProvider.OpenAi => PortableLlmProvider.OpenAi,
        CloudPostProcessingProvider.Anthropic => PortableLlmProvider.Anthropic,
        CloudPostProcessingProvider.Groq => PortableLlmProvider.Groq,
        CloudPostProcessingProvider.Grok => PortableLlmProvider.Grok,
        CloudPostProcessingProvider.Gemini => PortableLlmProvider.Gemini,
        CloudPostProcessingProvider.Cerebras => PortableLlmProvider.Cerebras,
        CloudPostProcessingProvider.Mistral => PortableLlmProvider.Mistral,
        _ => null,
    };

    /// <summary>
    /// Reject a request the builder could not sensibly serve. The URL and model
    /// rules are no longer written here — <see cref="LlmPostProcessing"/> owns
    /// the one rule that all platforms share, and this method just surfaces its
    /// verdict.
    /// </summary>
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

        var verdict = LlmPostProcessing.NormalizeCustomEndpoint(
            custom.EndpointUrl ?? string.Empty, custom.Model ?? string.Empty);
        if (verdict.Status != PortableEndpointStatus.Valid)
        {
            return verdict.Message ?? "A valid HTTP or HTTPS custom endpoint URL is required.";
        }
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
