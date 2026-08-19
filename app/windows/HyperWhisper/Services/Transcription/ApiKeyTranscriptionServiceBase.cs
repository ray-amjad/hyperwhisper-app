// API-KEY TRANSCRIPTION SERVICE BASE
// Shared plumbing for every cloud transcription provider the user configures
// with their own API key: OpenAI, Groq, Deepgram, AssemblyAI, ElevenLabs,
// Mistral, Soniox, Gemini and Grok.
//
// Each of those services carried its own copy of the same four fields, the same
// IsAvailable check, the same HttpClient construction and the same idempotent
// Dispose. That copy lives here once.
//
// This base deliberately owns plumbing only. Everything provider-specific stays
// in the provider: the display Name, the Configure overload (each has its own
// default model and its own alias resolution) and TranscribeAsync.
//
// NOT for the HyperWhisper-Cloud-routed services (HyperWhisperCloudService,
// AzureMAITranscriptionService, GoogleChirpTranscriptionService). Those take no
// API key and share one process-wide HttpClient instead of owning one.

using System.Net.Http;

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
