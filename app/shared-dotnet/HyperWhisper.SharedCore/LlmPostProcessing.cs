using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// Portable facade over the Rust-owned LLM post-processing request contract
/// (<c>hw_net::providers::llm</c>, issue #282).
/// </summary>
/// <remarks>
/// The endpoint table, both JSON bodies, the auth headers, the
/// <c>api.groq.com</c> host sniff, the <c>--TRANSCRIPT--</c> wrapper and
/// custom-endpoint validation all live in Rust now. This class only marshals
/// across the UniFFI boundary and materializes an <see cref="HttpRequestMessage"/>;
/// timeouts, retry and response-size limits stay with the caller, exactly as
/// they do for <see cref="CloudTranscriptionService"/>.
///
/// The generated binding's types are <c>internal</c> to this assembly, so every
/// other .NET head talks to this <c>Portable*</c> surface rather than to
/// <c>uniffi.hyperwhisper_core</c> directly.
/// </remarks>
public static class LlmPostProcessing
{
    /// <summary>
    /// HyperWhisper Cloud base URL for heads with no <c>NetworkConfig</c> of
    /// their own (the Linux head).
    /// </summary>
    /// <remarks>
    /// Before #282, <c>CloudPostProcessingService</c> hardcoded the production
    /// host with no DEBUG switch, so every Linux dev run billed production
    /// credits. A head that has its own environment switch — Windows
    /// <c>NetworkConfig.HyperWhisperCloudBaseUrl</c>, which points DEBUG at
    /// <c>transcribe-dev-v2</c> rather than staging — should keep passing that
    /// through <see cref="PortableLlmRequest.BaseUrl"/> instead.
    /// </remarks>
    public static string DefaultHyperWhisperCloudBaseUrl =>
#if DEBUG
        HyperwhisperCoreMethods.LlmHwCloudStagingBase();
#else
        HyperwhisperCoreMethods.LlmHwCloudProdBase();
#endif

    /// <summary>Max output tokens requested from any post-processing LLM.</summary>
    public static uint MaxOutputTokens => HyperwhisperCoreMethods.LlmMaxOutputTokens();

    /// <summary>Output-token cap sent to Groq (lower than <see cref="MaxOutputTokens"/>).</summary>
    public static uint GroqMaxCompletionTokens =>
        HyperwhisperCoreMethods.LlmGroqMaxCompletionTokens();

    /// <summary>Max custom endpoint URL length.</summary>
    public static uint MaxCustomEndpointUrlChars =>
        HyperwhisperCoreMethods.LlmMaxCustomEndpointUrlChars();

    /// <summary>Max custom endpoint model-name length.</summary>
    public static uint MaxCustomEndpointModelChars =>
        HyperwhisperCoreMethods.LlmMaxCustomEndpointModelChars();

    /// <summary>
    /// Build the post-processing request. The caller sends it with its own
    /// timeout and retry policy.
    /// </summary>
    /// <exception cref="PortableLlmRequestException">
    /// The inputs cannot produce a request — a missing transcript/prompt/model,
    /// a custom endpoint that fails validation, or HyperWhisper Cloud with no
    /// identity.
    /// </exception>
    public static HttpRequestMessage BuildRequest(PortableLlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var native = new HwLlmParams(
            MapProvider(request.Provider),
            request.Model ?? string.Empty,
            request.ApiKey ?? string.Empty,
            request.SystemPrompt ?? string.Empty,
            request.SystemInfo ?? string.Empty,
            request.Transcript ?? string.Empty,
            request.CustomEndpoint,
            request.BaseUrl,
            request.LocalLlamaPort,
            request.LicenseKey,
            request.DeviceId,
            request.LlmProviderHeader,
            request.LlmModelHeader,
            request.Stream);
        return Materialize(() => HyperwhisperCoreMethods.LlmBuildRequest(native));
    }

    /// <summary>
    /// The "Hello world" probe the Add/Edit endpoint sheet sends. Same body
    /// shape as a real call, so a pass means the real call will work.
    /// </summary>
    /// <exception cref="PortableLlmRequestException">The endpoint is invalid.</exception>
    public static HttpRequestMessage BuildCustomEndpointTestRequest(
        string endpointUrl,
        string model,
        string? apiKey) =>
        Materialize(() => HyperwhisperCoreMethods.LlmBuildCustomEndpointTestRequest(
            endpointUrl ?? string.Empty, model ?? string.Empty, apiKey));

    /// <summary>
    /// The <c>systemInfo</c> + <c>--TRANSCRIPT--</c> user message.
    /// </summary>
    /// <remarks>
    /// <see cref="BuildRequest"/> already applies this to every HTTP provider.
    /// It is exposed for the one caller that needs the string itself: an
    /// in-process local LLM, which never builds an HTTP request but must send
    /// the identical user message.
    /// </remarks>
    public static string WrapTranscript(string systemInfo, string transcript) =>
        HyperwhisperCoreMethods.LlmWrapTranscript(
            systemInfo ?? string.Empty, transcript ?? string.Empty);

    /// <summary>Which parser reads this provider's 200 body.</summary>
    public static PortableLlmWireProtocol WireProtocolFor(PortableLlmProvider provider) =>
        HyperwhisperCoreMethods.LlmWireProtocolFor(MapProvider(provider)) switch
        {
            HwLlmWireProtocol.AnthropicMessages => PortableLlmWireProtocol.AnthropicMessages,
            _ => PortableLlmWireProtocol.OpenAiChat,
        };

    /// <summary>
    /// Read the hosted <c>/post-process</c> 200 body.
    /// </summary>
    /// <exception cref="PortableLlmRequestException">
    /// The body carried no usable <c>corrected</c> text.
    /// </exception>
    public static string ParseHyperWhisperCloudResponse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ParseHyperWhisperCloudResponse(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <inheritdoc cref="ParseHyperWhisperCloudResponse(string)"/>
    public static string ParseHyperWhisperCloudResponse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var response = new HttpResponse(200, [], body);
        try
        {
            return HyperwhisperCoreMethods.LlmParseHwCloudPostProcess(response);
        }
        catch (HwLlmException exception)
        {
            throw new PortableLlmRequestException(Describe(exception), exception);
        }
    }

    /// <summary>
    /// The one custom-endpoint validation rule, replacing the four the platforms
    /// each had. Use <paramref name="lenient"/> for an endpoint that is already
    /// saved — see <see cref="ValidateExistingCustomEndpoint"/>.
    /// </summary>
    public static PortableEndpointVerdict NormalizeCustomEndpoint(
        string endpointUrl,
        string model,
        bool lenient = false) =>
        Map(HyperwhisperCoreMethods.LlmNormalizeCustomEndpoint(
            endpointUrl ?? string.Empty,
            model ?? string.Empty,
            lenient ? HwEndpointValidationMode.Lenient : HwEndpointValidationMode.Strict));

    /// <summary>
    /// Validate an endpoint that is already saved, or arriving in a backup.
    /// </summary>
    /// <remarks>
    /// Lenient on purpose: an endpoint that fails the tightened rules comes back
    /// as <see cref="PortableEndpointStatus.NeedsRepair"/> with a concrete
    /// <see cref="PortableEndpointVerdict.Suggestion"/>, and keeps a callable
    /// <see cref="PortableEndpointVerdict.Url"/> wherever calling it is still
    /// safe. Tightening validation must never silently delete a user's endpoint
    /// or stop their post-processing.
    /// </remarks>
    public static PortableEndpointVerdict ValidateExistingCustomEndpoint(
        string endpointUrl,
        string model) =>
        Map(HyperwhisperCoreMethods.LlmValidateExistingCustomEndpoint(
            endpointUrl ?? string.Empty, model ?? string.Empty));

    /// <summary>
    /// The endpoint id inside a Mode's <c>"custom:&lt;uuid&gt;"</c> provider
    /// string, or <c>null</c> when the string does not name one.
    /// </summary>
    public static Guid? ParseCustomProviderString(string? providerString)
    {
        if (string.IsNullOrEmpty(providerString)) return null;
        var parsed = HyperwhisperCoreMethods.LlmParseCustomProviderString(providerString);
        return parsed is null ? null : Guid.Parse(parsed);
    }

    /// <summary>Whether a Mode's stored provider string names a custom endpoint.</summary>
    public static bool IsCustomProviderString(string? providerString) =>
        !string.IsNullOrEmpty(providerString)
        && HyperwhisperCoreMethods.LlmIsCustomProviderString(providerString);

    /// <summary>
    /// The next name when the user duplicates an endpoint:
    /// <c>"Name"</c> → <c>"Name (copy)"</c> → <c>"Name (copy 2)"</c>.
    /// </summary>
    public static string NextCopyName(string originalName) =>
        HyperwhisperCoreMethods.LlmNextCopyName(originalName ?? string.Empty);

    private static HttpRequestMessage Materialize(Func<HttpRequest> build)
    {
        HttpRequest request;
        try
        {
            request = build();
        }
        catch (HwLlmException exception)
        {
            throw new PortableLlmRequestException(Describe(exception), exception);
        }
        return RustHttpTransport.BuildRequestMessage(request);
    }

    /// <summary>
    /// A readable message for a binding error. The generated exception's own
    /// <c>Message</c> is the field dump <c>"@message=..."</c>, which is not
    /// something to show a user or write to a log.
    /// </summary>
    private static string Describe(HwLlmException exception) => exception switch
    {
        HwLlmException.MissingField missing => $"missing {missing.@field}",
        HwLlmException.InvalidEndpoint invalid => $"invalid custom endpoint: {invalid.@message}",
        HwLlmException.MissingIdentity => "no HyperWhisper Cloud identity",
        HwLlmException.Parse parse => $"response parse error: {parse.@message}",
        _ => "the post-processing request could not be built",
    };

    private static HwLlmProvider MapProvider(PortableLlmProvider provider) => provider switch
    {
        PortableLlmProvider.HyperWhisperCloud => HwLlmProvider.HyperWhisperCloud,
        PortableLlmProvider.OpenAi => HwLlmProvider.OpenAi,
        PortableLlmProvider.Anthropic => HwLlmProvider.Anthropic,
        PortableLlmProvider.Gemini => HwLlmProvider.Gemini,
        PortableLlmProvider.Groq => HwLlmProvider.Groq,
        PortableLlmProvider.Grok => HwLlmProvider.Grok,
        PortableLlmProvider.Cerebras => HwLlmProvider.Cerebras,
        PortableLlmProvider.Mistral => HwLlmProvider.Mistral,
        PortableLlmProvider.LocalLlama => HwLlmProvider.LocalLlama,
        PortableLlmProvider.Custom => HwLlmProvider.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private static PortableEndpointVerdict Map(HwEndpointVerdict verdict) => new(
        verdict.@status switch
        {
            HwEndpointStatus.Valid => PortableEndpointStatus.Valid,
            HwEndpointStatus.NeedsRepair => PortableEndpointStatus.NeedsRepair,
            _ => PortableEndpointStatus.Invalid,
        },
        verdict.@url,
        verdict.@model,
        verdict.@issue?.ToString(),
        verdict.@message,
        verdict.@suggestion);
}

/// <summary>Every post-processing provider the apps offer.</summary>
public enum PortableLlmProvider
{
    HyperWhisperCloud,
    OpenAi,
    Anthropic,
    Gemini,
    Groq,
    Grok,
    Cerebras,
    Mistral,
    LocalLlama,
    Custom,
}

/// <summary>What <see cref="LlmPostProcessing.NormalizeCustomEndpoint"/> decided.</summary>
public enum PortableEndpointStatus
{
    /// <summary>Passes every rule.</summary>
    Valid,

    /// <summary>
    /// Wrong, but not fatal: show the user
    /// <see cref="PortableEndpointVerdict.Suggestion"/>.
    /// </summary>
    NeedsRepair,

    /// <summary>Not an endpoint at all.</summary>
    Invalid,
}

/// <summary>The verdict on one custom endpoint configuration.</summary>
/// <param name="Status">Valid, repairable, or not an endpoint.</param>
/// <param name="Url">
/// The URL to actually call. <b>Empty means do not call it</b> — the single
/// check a runtime caller needs, in either validation mode.
/// </param>
/// <param name="Model">The trimmed model name.</param>
/// <param name="Issue">The rule that failed, as a stable identifier.</param>
/// <param name="Message">The human-readable form of <paramref name="Issue"/>.</param>
/// <param name="Suggestion">A repaired URL to offer the user, when one exists.</param>
public sealed record PortableEndpointVerdict(
    PortableEndpointStatus Status,
    string Url,
    string Model,
    string? Issue,
    string? Message,
    string? Suggestion)
{
    /// <summary>True when the endpoint can be called right now.</summary>
    public bool IsUsable => !string.IsNullOrEmpty(Url);
}

/// <summary>Inputs for <see cref="LlmPostProcessing.BuildRequest"/>.</summary>
/// <param name="Provider">Which provider to call.</param>
/// <param name="Model">Model id. Ignored for HyperWhisper Cloud, which routes on headers.</param>
/// <param name="ApiKey">BYO key. Empty sends no auth header, which is correct for a keyless endpoint.</param>
/// <param name="SystemPrompt">The static, cacheable system prompt, from the shared prompt builder.</param>
/// <param name="SystemInfo">The dynamic per-request context, from the shared prompt builder.</param>
/// <param name="Transcript">The raw transcript.</param>
/// <param name="CustomEndpoint">The user-supplied URL, for <see cref="PortableLlmProvider.Custom"/>.</param>
/// <param name="BaseUrl">HyperWhisper Cloud base override; null means production.</param>
/// <param name="LicenseKey">HyperWhisper Cloud identity; preferred over <paramref name="DeviceId"/>.</param>
/// <param name="DeviceId">HyperWhisper Cloud fallback identity.</param>
/// <param name="LlmProviderHeader"><c>X-LLM-Provider</c>, from the cloud-PP catalog.</param>
/// <param name="LlmModelHeader"><c>X-LLM-Model</c>, from the cloud-PP catalog.</param>
/// <param name="Stream">Ask for an SSE stream. The caller still owns the line reader.</param>
/// <param name="LocalLlamaPort">llama-server port; null uses the default.</param>
public sealed record PortableLlmRequest(
    PortableLlmProvider Provider,
    string Model,
    string ApiKey,
    string SystemPrompt,
    string SystemInfo,
    string Transcript,
    string? CustomEndpoint = null,
    string? BaseUrl = null,
    string? LicenseKey = null,
    string? DeviceId = null,
    string? LlmProviderHeader = null,
    string? LlmModelHeader = null,
    bool Stream = false,
    ushort? LocalLlamaPort = null);

/// <summary>
/// The inputs could not produce a post-processing request, or a hosted response
/// could not be read. Never a transport failure — the caller owns those.
/// </summary>
public sealed class PortableLlmRequestException : Exception
{
    public PortableLlmRequestException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
