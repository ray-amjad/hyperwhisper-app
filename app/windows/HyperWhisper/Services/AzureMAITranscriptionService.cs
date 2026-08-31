// MICROSOFT MAI-TRANSCRIBE 1.5 TRANSCRIPTION SERVICE
// HyperWhisper Cloud only — routes through the Fly /transcribe endpoint with
// X-STT-Provider: azure-mai. No API key required; identical auth path to
// HyperWhisperCloudService (license_key or device_id).
//
// The routing itself, IsAvailable, the initialization log line and Dispose live
// in RoutedTranscriptionServiceBase.

using HyperWhisper.Services.Transcription;

namespace HyperWhisper.Services;

public class AzureMAITranscriptionService : RoutedTranscriptionServiceBase
{
    public override string Name => "Microsoft MAI-Transcribe";

    /// <summary>
    /// X-STT-Provider header value that the Fly backend dispatches to Azure
    /// Speech. Distinct from the catalog provider identifier
    /// (<c>microsoftAzureSpeech</c>) — do not conflate the two.
    /// </summary>
    protected override string SttProviderHeader => "azure-mai";
}
