// ROUTED TRANSCRIPTION SERVICE BASE
// Shared plumbing for the cloud transcription providers that carry no API key
// and route through the Fly /transcribe endpoint, pinning one upstream vendor
// with the X-STT-Provider header: AzureMAITranscriptionService and
// GoogleChirpTranscriptionService.
//
// Each of those services carried its own copy of the same IsAvailable, the same
// "Initialized" log line, the same TranscribeAsync body over
// HyperWhisperRoutedTranscriptionClient and the same no-op Dispose. That copy
// lives here once.
//
// This base deliberately owns plumbing only. Everything provider-specific stays
// in the provider: the display Name and the X-STT-Provider header value.
//
// NOT for HyperWhisperCloudService. It pins no upstream vendor, owns its own
// HttpClient and also implements ITranscriptionDiagnosticsSource.
//
// NOT for the API-key services either — see ApiKeyTranscriptionServiceBase.

using System.Threading;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Base class for cloud transcription providers that route through
/// HyperWhisper Cloud with a pinned <c>X-STT-Provider</c> upstream.
/// </summary>
public abstract class RoutedTranscriptionServiceBase : ITranscriptionProvider, IDisposable
{
    protected RoutedTranscriptionServiceBase()
    {
        LoggingService.Info($"{GetType().Name}: Initialized");
    }

    /// <summary>
    /// Always true: these providers need no API key, and the license_key or
    /// device_id auth is resolved per request by the routed client.
    /// </summary>
    public bool IsAvailable => true;

    /// <summary>
    /// Display name of the provider. Also sent on to the routed client, which
    /// uses it in log lines and error messages.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// X-STT-Provider header value that the Fly backend dispatches on. This is
    /// distinct from the catalog provider identifier — do not conflate the two.
    /// </summary>
    protected abstract string SttProviderHeader { get; }

    /// <summary>
    /// The mode's stored <c>cloudTranscriptionModel</c>, set by
    /// <c>TranscriptionProviderFactory.GetConfiguredCloudProvider</c> before
    /// every request — the same per-request configuration
    /// <c>ApiKeyTranscriptionServiceBase</c> takes through <c>Configure</c>.
    /// Null means "no selection"; <see cref="ResolveRoutedModel"/> decides what
    /// that means for the concrete provider.
    /// </summary>
    public string? RequestedModelId { get; set; }

    /// <summary>
    /// The <c>X-STT-Model</c> value for this request, or null to let the
    /// backend choose. Null by default: a routed provider that serves exactly
    /// one model has nothing to say here, and a provider whose catalog entry
    /// was retired has nothing to validate against (GoogleChirp). Overridden by
    /// <c>AzureMAITranscriptionService</c>, which serves two models at two
    /// different prices, so an unsent model silently swaps the user's choice.
    /// </summary>
    public virtual string? ResolveRoutedModel() => null;

    public Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        // Uses the shared HttpClient so all HW-Cloud-routed providers
        // (HW Cloud, Azure-MAI, Google-Chirp) coalesce HTTP/2 connections
        // to the transcribe endpoint. See HyperWhisperRoutedTranscriptionClient.
        return HyperWhisperRoutedTranscriptionClient.TranscribeAsync(
            HyperWhisperRoutedTranscriptionClient.SharedClient,
            SttProviderHeader,
            Name,
            audioPath,
            language,
            vocabulary,
            cancellationToken,
            model: ResolveRoutedModel());
    }

    // Implements IDisposable to satisfy TranscriptionProviderFactory's
    // SafeDispose<T> where T : IDisposable constraint. The HttpClient is
    // process-wide shared and must not be disposed here.
    public void Dispose() => GC.SuppressFinalize(this);
}
