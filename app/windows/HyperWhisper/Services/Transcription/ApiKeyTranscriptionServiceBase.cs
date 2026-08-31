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
// in the provider: the display Name, the Configure overload (each has its own
// default model and its own alias resolution) and TranscribeAsync.
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
    /// <param name="defaultModelId">Model id used until <see cref="Configure"/> runs.</param>
    protected ApiKeyTranscriptionServiceBase(TimeSpan timeout, string defaultModelId = "")
    {
        Http = new HttpClient
        {
            Timeout = timeout
        };
        ModelId = defaultModelId;
    }

    /// <summary>
    /// HTTP client owned by this provider. Disposed with the service.
    /// </summary>
    protected HttpClient Http { get; }

    /// <summary>
    /// API key set by <see cref="Configure"/>. Null or empty until then.
    /// </summary>
    protected string? ApiKey { get; set; }

    /// <summary>
    /// Model id set by <see cref="Configure"/>.
    /// </summary>
    protected string ModelId { get; set; }

    /// <summary>
    /// Whether the service is ready (API key is configured).
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// Display name of the provider, usually including the configured model.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Configures the service with an API key and a model.
    /// Must be called before transcription.
    /// </summary>
    public abstract void Configure(string apiKey, string modelId);

    /// <inheritdoc />
    public abstract Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the core <see cref="TranscribeParams"/> for a direct-vendor
    /// request from this service's configured <see cref="ApiKey"/> and
    /// <see cref="ModelId"/>. Every provider below this base built the same
    /// value by hand; that copy lives here once.
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
        string audioPath,
        string audioMime,
        string? language,
        IReadOnlyList<string>? vocabulary,
        string? prompt = null)
    {
        return RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: audioMime,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            // Direct-vendor request: the core cannot attach X-Latency-Opt-Out to
            // one by construction. Pass the user's real choice anyway so this site
            // stays correct if it is ever routed.
            shareAnonymousSpeedData: SettingsService.Instance.ShareAnonymousSpeedData,
            // Not null: TranscriptionPreflight.Validate runs first at every call
            // site and throws ApiKeyMissing on a null or empty key. The flow
            // analysis that gives at the call site does not reach in here.
            apiKey: ApiKey!,
            // Grok STT has no model parameter. It never writes ModelId, so its
            // value stays the base default of "" — the same empty string the
            // core's own `model` default carries.
            model: ModelId,
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
