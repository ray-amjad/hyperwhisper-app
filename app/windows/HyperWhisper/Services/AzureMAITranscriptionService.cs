// MICROSOFT MAI-TRANSCRIBE 1.5 TRANSCRIPTION SERVICE
// HyperWhisper Cloud only — routes through the Fly /transcribe endpoint with
// X-STT-Provider: azure-mai. No API key required; identical auth path to
// HyperWhisperCloudService (license_key or device_id).

using System.Threading;

namespace HyperWhisper.Services;

public class AzureMAITranscriptionService : ITranscriptionProvider, IDisposable
{
    public bool IsAvailable => true;
    public string Name => "Microsoft MAI-Transcribe";

    /// <summary>
    /// X-STT-Provider header value that the Fly backend dispatches to Azure
    /// Speech. Distinct from the catalog provider identifier
    /// (<c>microsoftAzureSpeech</c>) — do not conflate the two.
    /// </summary>
    private const string SttProviderHeader = "azure-mai";

    public AzureMAITranscriptionService()
    {
        LoggingService.Info("AzureMAITranscriptionService: Initialized");
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
