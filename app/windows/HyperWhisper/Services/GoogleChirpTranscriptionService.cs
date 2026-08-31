// GOOGLE CHIRP 3 TRANSCRIPTION SERVICE
// HyperWhisper Cloud only — routes through the Fly /transcribe endpoint with
// X-STT-Provider: google-chirp. No API key required; identical auth path to
// HyperWhisperCloudService (license_key or device_id).
//
// The routing itself, IsAvailable, the initialization log line and Dispose live
// in RoutedTranscriptionServiceBase.

using HyperWhisper.Services.Transcription;

namespace HyperWhisper.Services;

public class GoogleChirpTranscriptionService : RoutedTranscriptionServiceBase
{
    public override string Name => "Google Chirp 3";

    /// <summary>
    /// X-STT-Provider header value that the Fly backend dispatches to Google
    /// Speech V2. Distinct from the catalog provider identifier
    /// (<c>googleSpeech</c>) — do not conflate the two.
    /// </summary>
    protected override string SttProviderHeader => "google-chirp";
}
