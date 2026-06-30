// GOOGLE CHIRP 3 TRANSCRIPTION SERVICE
// HyperWhisper Cloud only — routes through the Fly /transcribe endpoint with
// X-STT-Provider: google-chirp. No API key required; identical auth path to
// HyperWhisperCloudService (license_key or device_id).

using System.Threading;

namespace HyperWhisper.Services;

public class GoogleChirpTranscriptionService : ITranscriptionProvider, IDisposable
{
    public bool IsAvailable => true;
    public string Name => "Google Chirp 3";

    /// <summary>
    /// X-STT-Provider header value that the Fly backend dispatches to Google
    /// Speech V2. Distinct from the catalog provider identifier
    /// (<c>googleSpeech</c>) — do not conflate the two.
    /// </summary>
    private const string SttProviderHeader = "google-chirp";

    public GoogleChirpTranscriptionService()
    {
        LoggingService.Info("GoogleChirpTranscriptionService: Initialized");
    }

    public Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        // Uses the shared HttpClient so all HW-Cloud-routed providers
        // (HW Cloud, Azure-MAI, Google-Chirp) coalesce HTTP/2 connections
        // to transcribe.hyperwhisper.com. See HyperWhisperRoutedTranscriptionClient.
        return HyperWhisperRoutedTranscriptionClient.TranscribeAsync(
            HyperWhisperRoutedTranscriptionClient.SharedClient,
            SttProviderHeader,
            Name,
            audioPath,
            language,
            vocabulary,
            cancellationToken);
    }

    // Implements IDisposable to satisfy TranscriptionProviderFactory's
    // SafeDispose<T> where T : IDisposable constraint. The HttpClient is
    // process-wide shared and must not be disposed here.
    public void Dispose() => GC.SuppressFinalize(this);
}
