// MICROSOFT MAI-TRANSCRIBE TRANSCRIPTION SERVICE
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
    /// <param name="requestedModelId">
    /// The mode's <c>cloudTranscriptionModel</c> for this one request. Passed at
    /// construction rather than set on a shared instance: this provider serves
    /// two models at two prices, so a model that could be overwritten by a
    /// concurrent request is a billing bug. See
    /// <c>RoutedTranscriptionServiceBase.RequestedModelId</c>.
    /// </param>
    public AzureMAITranscriptionService(string? requestedModelId = null)
        : base(requestedModelId)
    {
    }

    public override string Name => "Microsoft MAI-Transcribe";

    /// <summary>
    /// X-STT-Provider header value that the Fly backend dispatches to Azure
    /// Speech. Distinct from the catalog provider identifier
    /// (<c>microsoftAzureSpeech</c>) — do not conflate the two.
    /// </summary>
    protected override string SttProviderHeader => "azure-mai";

    /// <summary>
    /// Catalog entry this provider's models live under. The entry id is NOT the
    /// <c>X-STT-Provider</c> value above — that is <c>azure-mai</c>, the backend
    /// dispatch key.
    /// </summary>
    internal const string CatalogEntryId = "azureMaiTranscribe";

    /// <summary>
    /// Azure MAI serves TWO models through one route — <c>mai-transcribe-2</c>
    /// (the default, 1.67 credits/min) and <c>mai-transcribe-1.5</c> (6.0) —
    /// and the only place the choice can travel is <c>X-STT-Model</c>. Sending
    /// nothing means the backend applies its own default, so a mode pinned to
    /// 1.5 would transcribe and bill as 2, with a different transcribeStyle.
    ///
    /// Reuses <c>HyperWhisperCloudService.ResolveDictationModelId</c> — the same
    /// validation the HyperWhisper Cloud tier path runs, so a stale id, a BYOK
    /// id left in the shared field, or a live-only id all degrade to this
    /// entry's catalog default instead of earning a backend 400.
    /// </summary>
    public override string? ResolveRoutedModel()
    {
        var resolved = HyperWhisperCloudService.ResolveDictationModelId(
            CatalogEntryId, RequestedModelId);
        return string.IsNullOrEmpty(resolved) ? null : resolved;
    }
}
