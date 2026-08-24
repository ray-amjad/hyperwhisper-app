using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HyperWhisper.ModelReadiness;

/// <summary>
/// Authenticates with official provider metadata endpoints without submitting inference content.
/// The fixed request table is intentionally conservative: providers without a documented,
/// content-free endpoint are reported as unsupported.
/// </summary>
public sealed class ProviderMetadataHealthProbe(HttpMessageInvoker transport) : IProviderHealthProbe
{
    private const int MaximumResponseBytes = ProviderHealthResponse.MaximumDetailBytes;
    private readonly HttpMessageInvoker _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<ProviderHealthResponse> CheckAsync(
        ProviderHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCreateRequest(request, out var message))
            return new(ProviderHealthOutcome.Unsupported,
                "This provider has no safe content-free metadata endpoint.");

        using (message)
        using (var response = await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(ProviderHealthOutcome.Unauthorized, "Provider rejected the credential.");
            if ((int)response.StatusCode == 429)
                return new(ProviderHealthOutcome.RateLimited, "Provider readiness check was rate limited.");
            if (!response.IsSuccessStatusCode)
                return new(ProviderHealthOutcome.Unreachable, "Provider metadata endpoint returned an unexpected status.");

            if (response.Content is null) return new(ProviderHealthOutcome.Healthy);
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            // A successful model list can legitimately exceed the diagnostics bound. The 2xx
            // status already proves authentication; do not buffer or expose the remainder.
            if (bytes is null) return new(ProviderHealthOutcome.Healthy);
            if (bytes.Length == 0) return new(ProviderHealthOutcome.Healthy);
            try
            {
                using var _ = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
                return new(ProviderHealthOutcome.Healthy);
            }
            catch (JsonException)
            {
                return new(ProviderHealthOutcome.Malformed, "Provider returned malformed metadata.");
            }
        }
    }

    private static bool TryCreateRequest(ProviderHealthRequest request, out HttpRequestMessage message)
    {
        message = null!;
        var provider = request.ProviderId.Trim().ToLowerInvariant();
        var endpoint = provider switch
        {
            "openai" => "https://api.openai.com/v1/models",
            "groq" => "https://api.groq.com/openai/v1/models",
            "grok" or "xai" => "https://api.x.ai/v1/models",
            "mistral" => "https://api.mistral.ai/v1/models",
            "cerebras" => "https://api.cerebras.ai/v1/models",
            "anthropic" => "https://api.anthropic.com/v1/models?limit=1",
            "gemini" => "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1",
            "deepgram" => "https://api.deepgram.com/v1/projects?limit=1",
            "elevenlabs" => "https://api.elevenlabs.io/v1/models",
            _ => null,
        };
        if (endpoint is null) return false;

        message = new(HttpMethod.Get, endpoint);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.UserAgent.ParseAdd("HyperWhisper-Linux/1.0");
        switch (provider)
        {
            case "anthropic":
                message.Headers.TryAddWithoutValidation("x-api-key", request.Credential.Value);
                message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            case "gemini":
                message.Headers.TryAddWithoutValidation("x-goog-api-key", request.Credential.Value);
                break;
            case "deepgram":
                message.Headers.Authorization = new("Token", request.Credential.Value);
                break;
            case "elevenlabs":
                message.Headers.TryAddWithoutValidation("xi-api-key", request.Credential.Value);
                break;
            default:
                message.Headers.Authorization = new("Bearer", request.Credential.Value);
                break;
        }
        return true;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total > MaximumResponseBytes) return null;
        return buffer.AsSpan(0, total).ToArray();
    }
}
