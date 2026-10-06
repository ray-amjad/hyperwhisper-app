// API-KEY TRANSCRIPTION SERVICE BASE
// Shared plumbing for every cloud transcription provider the user configures
// with their own API key: OpenAI, Groq, Deepgram, AssemblyAI, ElevenLabs,
// Mistral, Soniox, Gemini, Gemini 3.5 Transcribe and Grok.
//
// Each of those services carried its own copy of the same four fields, the same
// IsAvailable check, the same HttpClient construction, the same idempotent
// Dispose and the same direct-vendor TranscribeParams builder. That copy lives
// here once.
//
// This base deliberately owns plumbing only. Everything provider-specific stays
// in the provider: the display Name, Configure (some trim the key), the model
// alias resolution (ResolveModelId) and the transcription itself.
//
// PER-CALL MODEL (issue #753). TranscriptionProviderFactory hands out ONE
// cached instance per provider to every caller of the process-wide
// orchestrator, and nothing locks. So the only state an instance may hold is
// per-profile state: the HttpClient and the API key. The model id travels in
// the TranscriptionRequest of each call. It used to be an instance property
// that Configure wrote, so a GUI dictation and a Local API request that
// overlapped could each send the other's model.
//
// NOT for the HyperWhisper-Cloud-routed services (HyperWhisperCloudService,
// AzureMAITranscriptionService, GoogleChirpTranscriptionService). Those take no
// API key and share one process-wide HttpClient instead of owning one. The two
// vendor-pinned ones share RoutedTranscriptionServiceBase instead.

using System.Net.Http;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Base class for cloud transcription providers that authenticate with a
/// user-supplied API key.
/// </summary>
public abstract class ApiKeyTranscriptionServiceBase : ITranscriptionProvider, IDisposable
{
    private bool _disposed;

    /// <param name="timeout">
    /// HttpClient-level timeout. Pass <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// for providers that enforce their budget per attempt instead.
    /// </param>
    /// <param name="fallbackModelId">
    /// Model id for a call made through the 4-argument <c>TranscribeAsync</c>
    /// overload, which carries no model of its own.
    /// </param>
    /// <param name="httpHandler">
    /// Test seam: a handler that fakes the vendor. Null in production. The
    /// handler is not disposed with the service; the caller owns it.
    /// </param>
    protected ApiKeyTranscriptionServiceBase(
        TimeSpan timeout,
        string fallbackModelId = "",
        HttpMessageHandler? httpHandler = null)
    {
        Http = httpHandler is null
            ? new HttpClient()
            : new HttpClient(httpHandler, disposeHandler: false);
        Http.Timeout = timeout;
        FallbackModelId = fallbackModelId;
    }

    /// <summary>
    /// HTTP client owned by this provider. Disposed with the service.
    /// </summary>
    protected HttpClient Http { get; }

    /// <summary>
    /// API key set by <see cref="Configure"/>. Null or empty until then.
    /// Per-profile, not per-call, so it may live on the shared instance.
    /// </summary>
    protected string? ApiKey { get; set; }

    /// <summary>
    /// The provider's own default model. Read-only: it is used only by the
    /// 4-argument overload, never written per call.
    /// </summary>
    protected string FallbackModelId { get; }

    /// <summary>
    /// Whether the service is ready (API key is configured).
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// Display name of the provider. It names no model: the model is per call.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Configures the service with an API key. Must be called before
    /// transcription. The model is NOT configured here; it travels in each
    /// <see cref="TranscriptionRequest"/>.
    /// </summary>
    public abstract void Configure(string apiKey);

    /// <summary>
    /// Maps the requested model id to the id this provider sends: its alias
    /// table, and its fallback for a blank id. Pure: it reads no instance state.
    /// </summary>
    protected abstract string ResolveModelId(string modelId);

    /// <summary>
    /// The old entry point, kept for callers that carry no model. It runs the
    /// provider's <see cref="FallbackModelId"/> with no custom prompt.
    /// </summary>
    public Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
        => TranscribeAsync(
            new TranscriptionRequest(audioPath, language, vocabulary, FallbackModelId, CustomPrompt: null),
            cancellationToken);

    /// <inheritdoc />
    public Task<string> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TranscribeCoreAsync(WithResolvedModel(request), cancellationToken);
    }

    /// <summary>
    /// Runs one call. <paramref name="request"/>'s model id is already
    /// resolved by <see cref="ResolveModelId"/>.
    /// </summary>
    protected abstract Task<string> TranscribeCoreAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a copy of <paramref name="request"/> whose model id has been
    /// through <see cref="ResolveModelId"/>. For a provider-specific entry
    /// point that does not go through the public request overload.
    /// </summary>
    private protected TranscriptionRequest WithResolvedModel(TranscriptionRequest request)
        => request with { ModelId = ResolveModelId(request.ModelId ?? string.Empty) };

    /// <summary>
    /// Builds the core <see cref="TranscribeParams"/> for a direct-vendor
    /// request from this service's configured <see cref="ApiKey"/> and the
    /// call's own <see cref="TranscriptionRequest.ModelId"/>. Every provider
    /// below this base built the same value by hand; that copy lives here once.
    ///
    /// Pass the RAW vocabulary list — the core trims, drops empties and builds
    /// the per-provider field itself. A null list becomes an empty one.
    ///
    /// <paramref name="audioMime"/> stays a caller argument: each provider
    /// resolves it with its own fallback and, for Gemini, Grok and Soniox, its
    /// own container map (see <see cref="TranscriptionPreflight.MimeTypeFor"/>).
    /// </summary>
    /// <param name="prompt">
    /// Extra prompt text folded into the request. Only Gemini uses one.
    /// </param>
    /// <remarks>
    /// Call this only after <see cref="TranscriptionPreflight.Validate"/>, which
    /// throws on a missing API key. Every provider does, and this method relies
    /// on it for the non-null <see cref="ApiKey"/>.
    /// </remarks>
    private protected TranscribeParams BuildDirectVendorParams(
        TranscriptionRequest request,
        string audioMime,
        string? prompt = null)
    {
        return RustCoreMapping.TranscribeParams(
            audioPath: request.AudioPath,
            audioMime: audioMime,
            language: request.Language,
            vocabulary: request.Vocabulary ?? Array.Empty<string>(),
            // Direct-vendor request: the core cannot attach X-Latency-Opt-Out to
            // one by construction. Pass the user's real choice anyway so this site
            // stays correct if it is ever routed.
            shareAnonymousSpeedData: SettingsService.Instance.ShareAnonymousSpeedData,
            // Not null: TranscriptionPreflight.Validate runs first at every call
            // site and throws ApiKeyMissing on a null or empty key. The flow
            // analysis that gives at the call site does not reach in here.
            apiKey: ApiKey!,
            // An empty ModelId is not "send no model": every provider builder in
            // the core resolves a blank id to that provider's catalog default.
            // Grok relied on the other reading until 2026-09-19, when xAI gave
            // `/v1/stt` a `model` parameter.
            model: request.ModelId,
            prompt: prompt);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Http.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
