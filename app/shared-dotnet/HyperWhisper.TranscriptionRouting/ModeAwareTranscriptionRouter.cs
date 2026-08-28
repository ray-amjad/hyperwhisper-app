using HyperWhisper.Data.Entities;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;

namespace HyperWhisper.TranscriptionRouting;

public interface IBatchCloudTranscriptionClient
{
    Task<CloudTranscriptionResult> TranscribeAsync(
        CloudTranscriptionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SharedCoreBatchCloudClient(CloudTranscriptionService service) :
    IBatchCloudTranscriptionClient, IDisposable
{
    private readonly CloudTranscriptionService _service = service ?? throw new ArgumentNullException(nameof(service));
    public Task<CloudTranscriptionResult> TranscribeAsync(
        CloudTranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        _service.TranscribeAsync(request, cancellationToken);
    public void Dispose() => _service.Dispose();
}

/// <summary>Routes one completed audio file using the selected persisted mode.</summary>
public sealed class ModeAwareTranscriptionRouter : IRecordedAudioTranscriber, IDisposable
{
    private readonly IRecordedAudioTranscriber _whisper;
    private readonly IRecordedAudioTranscriber _parakeet;
    private readonly IBatchCloudTranscriptionClient _cloud;
    private readonly bool _ownsDependencies;

    public ModeAwareTranscriptionRouter(
        IRecordedAudioTranscriber whisper,
        IRecordedAudioTranscriber parakeet,
        IBatchCloudTranscriptionClient cloud,
        bool ownsDependencies = false)
    {
        _whisper = whisper ?? throw new ArgumentNullException(nameof(whisper));
        _parakeet = parakeet ?? throw new ArgumentNullException(nameof(parakeet));
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _ownsDependencies = ownsDependencies;
    }

    public TranscriptionBackendCapability Capability => new(
        true,
        "Mode-selected transcription",
        "The selected transcription provider is validated when transcription starts.");

    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? language,
        CancellationToken cancellationToken = default) =>
        _whisper.TranscribeAsync(audioPath, language, cancellationToken);

    public async Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = request.SelectedMode;
        if (!string.Equals(mode?.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            var local = string.Equals(mode?.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
                ? _parakeet
                : _whisper;
            return await local.TranscribeAsync(audioPath, request, cancellationToken).ConfigureAwait(false);
        }

        if (!TryMapProvider(mode!.CloudProvider, out var provider))
            return PortableTranscriptionResult.Failed(
                PortableTranscriptionErrorCode.InvalidRequest,
                "The selected cloud transcription provider is not supported.",
                "Cloud transcription");

        var cloudRequest = BuildCloudRequest(audioPath, request, mode, provider);
        var result = await _cloud.TranscribeAsync(cloudRequest, cancellationToken).ConfigureAwait(false);
        var attribution = ProviderDisplayName(provider);
        if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Transcript!.Text))
        {
            var raw = string.IsNullOrWhiteSpace(result.Transcript.RawProvider)
                ? attribution
                : $"{attribution} · {result.Transcript.RawProvider}";
            return PortableTranscriptionResult.Success(result.Transcript.Text.Trim(), raw);
        }

        var failure = result.Failure;
        return PortableTranscriptionResult.Failed(
            MapCloudError(failure?.Code),
            failure?.Message ?? "The cloud provider returned no transcription.",
            attribution);
    }

    public static CloudTranscriptionRequest BuildCloudRequest(
        string audioPath,
        TranscriptionWorkflowRequest request,
        Mode mode,
        CloudTranscriptionProvider provider)
    {
        var language = NormalizeLanguage(request.Language ?? mode.Language);
        var model = string.IsNullOrWhiteSpace(mode.CloudTranscriptionModel)
            ? DefaultModel(provider)
            : mode.CloudTranscriptionModel.Trim();
        // The shared core owns sanitize -> drop-empty -> case-insensitive dedupe.
        // The 1000-term cap is this route's own budget and stays here.
        var vocabulary = SharedCoreBridge.NormalizeVocabularyTerms(
            [.. (request.Vocabulary ?? []), .. (mode.CustomVocabulary ?? [])],
            1000);

        if (provider != CloudTranscriptionProvider.HyperWhisperCloud)
            return new(provider, audioPath, model, language, vocabulary,
                provider == CloudTranscriptionProvider.Gemini ? Normalize(mode.GeminiCustomPrompt) : null);

        var tier = SharedCoreBridge.CanonicalCloudSttTier(mode.CloudAccuracyTier);
        // Dictation is the PRE-RECORDED route, so a live-only model id falls back
        // to the tier default instead of being forwarded. There is no picker
        // filtering it out on Linux — the model box is a bare text field — so a
        // mode carrying `gemini-3.5-transcribe-live` would otherwise POST it and
        // take an HTTP 400 on every dictation.
        var routedModel = !string.IsNullOrWhiteSpace(mode.CloudTranscriptionModel)
            && SharedCoreBridge.CloudSttContainsDictationModel(tier, mode.CloudTranscriptionModel)
                ? mode.CloudTranscriptionModel
                : SharedCoreBridge.CloudSttDefaultModel(tier);
        return new(
            provider,
            audioPath,
            string.Empty,
            language,
            vocabulary,
            RoutedProvider: SharedCoreBridge.CloudSttProvider(tier) ?? "deepgram",
            RoutedModel: routedModel,
            RoutedDomain: NormalizeDomain(mode.CloudTranscriptionDomain));
    }

    public static bool TryMapProvider(string? value, out CloudTranscriptionProvider provider)
    {
        provider = value?.Trim().ToLowerInvariant() switch
        {
            "openai" => CloudTranscriptionProvider.OpenAi,
            "groq" => CloudTranscriptionProvider.Groq,
            "elevenlabs" => CloudTranscriptionProvider.ElevenLabs,
            "mistral" => CloudTranscriptionProvider.Mistral,
            "grok" => CloudTranscriptionProvider.Grok,
            "deepgram" => CloudTranscriptionProvider.Deepgram,
            "assemblyai" => CloudTranscriptionProvider.AssemblyAi,
            "soniox" => CloudTranscriptionProvider.Soniox,
            "gemini" => CloudTranscriptionProvider.Gemini,
            "geminitranscribe" or "gemini-transcribe" => CloudTranscriptionProvider.GeminiTranscribe,
            "microsoftazurespeech" or "azure-mai" => CloudTranscriptionProvider.AzureMai,
            "googlespeech" or "google-chirp" => CloudTranscriptionProvider.GoogleChirp,
            "hyperwhisper" => CloudTranscriptionProvider.HyperWhisperCloud,
            _ => default,
        };
        return value?.Trim().ToLowerInvariant() is
            "openai" or "groq" or "elevenlabs" or "mistral" or "grok" or "deepgram"
            or "assemblyai" or "soniox" or "gemini" or "geminitranscribe"
            or "gemini-transcribe" or "microsoftazurespeech"
            or "azure-mai" or "googlespeech" or "google-chirp" or "hyperwhisper";
    }

    public void Dispose()
    {
        if (!_ownsDependencies) return;
        if (_whisper is IDisposable whisper) whisper.Dispose();
        if (_parakeet is IDisposable parakeet) parakeet.Dispose();
        if (_cloud is IDisposable cloud) cloud.Dispose();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? null : value.Trim();
    private static string? NormalizeDomain(string? value) =>
        string.Equals(value?.Trim(), "medical", StringComparison.OrdinalIgnoreCase) ? "medical" : null;

    private static string DefaultModel(CloudTranscriptionProvider provider) => provider switch
    {
        // Chirp 3 lost its catalog entry in v8 (geminiTranscribe took Google's
        // tier slot) but the standalone BYOK provider stays, so its default has
        // to be pinned here — a catalog lookup on the dead id returns null and
        // we would post a request with an empty model.
        CloudTranscriptionProvider.GoogleChirp => "chirp_3",
        _ => SharedCoreBridge.CloudSttDefaultModel(CatalogTier(provider)) ?? string.Empty,
    };

    private static string CatalogTier(CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAi => "openaiWhisper",
        CloudTranscriptionProvider.Groq => "groqWhisper",
        CloudTranscriptionProvider.ElevenLabs => "elevenLabsScribeV2",
        CloudTranscriptionProvider.Mistral => "mistralVoxtral",
        CloudTranscriptionProvider.Grok => "grokStt",
        CloudTranscriptionProvider.Deepgram => "deepgramNova3",
        CloudTranscriptionProvider.AssemblyAi => "assemblyAI",
        CloudTranscriptionProvider.Soniox => "soniox",
        CloudTranscriptionProvider.Gemini => "gemini",
        CloudTranscriptionProvider.AzureMai => "azureMaiTranscribe",
        CloudTranscriptionProvider.GeminiTranscribe => "geminiTranscribe",
        // GoogleChirp is deliberately absent: catalog v8 retired `googleChirp3`,
        // so it has no entry to look up. `DefaultModel` pins its model directly.
        CloudTranscriptionProvider.HyperWhisperCloud => "deepgramNova3",
        _ => "deepgramNova3",
    };

    private static string ProviderDisplayName(CloudTranscriptionProvider provider) =>
        CloudTranscriptionService.Providers.First(item => item.Provider == provider).DisplayName;

    private static PortableTranscriptionErrorCode MapCloudError(CloudTranscriptionErrorCode? code) => code switch
    {
        CloudTranscriptionErrorCode.Cancelled => PortableTranscriptionErrorCode.Cancelled,
        CloudTranscriptionErrorCode.InvalidRequest or CloudTranscriptionErrorCode.FileTooLarge
            or CloudTranscriptionErrorCode.Unsupported => PortableTranscriptionErrorCode.InvalidRequest,
        CloudTranscriptionErrorCode.Unauthorized => PortableTranscriptionErrorCode.BackendUnavailable,
        _ => PortableTranscriptionErrorCode.TranscriptionFailed,
    };
}
