using System.Net.Http.Headers;
using System.Text.Json;
using HyperWhisper.SharedCore;
// Rust shared-core binding: the provider enum the health table is keyed on.
using HwProvider = uniffi.hyperwhisper_core.HwProvider;

namespace HyperWhisper.ModelReadiness;

/// <summary>
/// Authenticates with official provider metadata endpoints without submitting inference content.
/// The Rust core (<c>hw_net::health</c>) owns every speech provider's endpoint, auth scheme and
/// status grade, exactly as on Windows; this probe only performs the request on its own bounded
/// transport and maps the answer onto the Linux readiness states. Providers the core has no probe
/// for are reported as unsupported.
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
        var provider = request.ProviderId.Trim().ToLowerInvariant();
        var coreProvider = CoreProvider(provider);
        using var message = coreProvider is { } hw
            ? SharedCoreBridge.BuildHealthRequestMessage(hw, request.Credential.Value)
            : TryCreatePostProcessingRequest(provider, request.Credential.Value);
        if (message is null)
            return new(ProviderHealthOutcome.Unsupported,
                "This provider has no safe content-free metadata endpoint.");
        message.Headers.UserAgent.ParseAdd("HyperWhisper-Linux/1.0");

        using var response = await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var healthy = coreProvider is { } graded
            ? SharedCoreBridge.ParseHealthResponse(graded, status)
            : response.IsSuccessStatusCode;
        if (!healthy) return Failure(coreProvider, status);

        // ElevenLabs probes STT with an empty body: the core grades a 400/422 for that body as
        // healthy, because only a key that can reach STT gets that far. There is no metadata to read.
        if (!response.IsSuccessStatusCode) return new(ProviderHealthOutcome.Healthy);
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

    /// <summary>
    /// Why a probe the core graded unhealthy failed. <c>RateLimited</c> is a Linux-only state the
    /// core does not carry, so the raw status still decides it here.
    /// </summary>
    private static ProviderHealthResponse Failure(HwProvider? provider, int status)
    {
        if (status is 401 or 403 || (status == 400 && RejectsKeyWith400(provider)))
            return new(ProviderHealthOutcome.Unauthorized, "Provider rejected the credential.");
        if (status == 429)
            return new(ProviderHealthOutcome.RateLimited, "Provider readiness check was rate limited.");
        return new(ProviderHealthOutcome.Unreachable, "Provider metadata endpoint returned an unexpected status.");
    }

    /// <summary>
    /// The Gemini models endpoint and xAI answer an invalid key with HTTP 400. The core folds that
    /// 400 into "not healthy" but leaves the reason to the host (hw_net::health module note).
    /// Only the providers <see cref="CoreProvider"/> returns are listed: every Gemini id probes
    /// as <c>GeminiTranscribe</c>. Remove this once the core's verdict carries the reason
    /// (issue #779) and read it from there.
    /// </summary>
    private static bool RejectsKeyWith400(HwProvider? provider) => provider is
        HwProvider.GeminiTranscribe or HwProvider.Grok;

    /// <summary>
    /// The core provider for a catalog provider id, or null when the core has no vendor probe
    /// for it. The HW-Cloud-routed providers (<c>hyperwhisper</c>, <c>azure-mai</c>,
    /// <c>google-chirp</c>) and <c>meta</c> stay unsupported: their key is the licence or has no
    /// content-free endpoint, and the core builds no vendor request for them.
    /// </summary>
    private static HwProvider? CoreProvider(string provider) => provider switch
    {
        "openai" => HwProvider.Openai,
        "groq" => HwProvider.Groq,
        "grok" or "xai" => HwProvider.Grok,
        "mistral" => HwProvider.Mistral,
        "deepgram" => HwProvider.Deepgram,
        "elevenlabs" => HwProvider.Elevenlabs,
        "soniox" => HwProvider.Soniox,
        "assemblyai" => HwProvider.Assemblyai,
        // A Gemini key is a Google AI Studio key, the same kind Gemini 3.5 Transcribe uses, and
        // the core probes the same models endpoint for both. HwProvider.Gemini puts the key in the
        // URL query (?key=); the Transcribe probe sends it in x-goog-api-key, so the secret never
        // lands in a URI that an exception or a proxy log can echo.
        "gemini" or "geminitranscribe" or "gemini-transcribe" => HwProvider.GeminiTranscribe,
        _ => null,
    };

    /// <summary>
    /// Post-processing-only vendors. The core's health table covers speech providers, and these two
    /// LLM vendors have no <see cref="HwProvider"/>, so their content-free probe stays here.
    /// </summary>
    private static HttpRequestMessage? TryCreatePostProcessingRequest(string provider, string credential)
    {
        HttpRequestMessage message;
        switch (provider)
        {
            case "anthropic":
                message = new(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1");
                message.Headers.TryAddWithoutValidation("x-api-key", credential);
                message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            case "cerebras":
                message = new(HttpMethod.Get, "https://api.cerebras.ai/v1/models");
                message.Headers.Authorization = new("Bearer", credential);
                break;
            default:
                return null;
        }
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
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
