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
            var isParakeet = string.Equals(mode?.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase);
            var local = isParakeet ? _parakeet : _whisper;
            var localResult = await local.TranscribeAsync(audioPath, request, cancellationToken)
                .ConfigureAwait(false);
            return ApplyLocalVocabulary(localResult, request, isParakeet);
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

    /// <summary>
    /// The two on-device vocabulary passes, over the raw local engine result
    /// (issue #283). NEW ON LINUX: this head had no phonetic matching and no
    /// substring pass at all — <c>grep -rn "Phonetic|BeiderMorse" app/linux
    /// app/shared-dotnet</c> returned nothing, and the Windows copy lived in
    /// <c>app/windows</c> where Linux could not reach it.
    ///
    /// This is the LOCAL branch only, which is where macOS and Windows run them:
    /// inside the on-device provider, over its own raw output, BEFORE the
    /// pipeline's <c>\b</c>-anchored pass in <c>SpeechOutputProcessor</c>. A
    /// cloud transcription never sees either pass on any platform.
    ///
    /// 1. The phonetic (Beider-Morse) pass, for parakeet only. macOS runs it in
    ///    ParakeetProvider and NemotronProvider and NOT in Qwen3AsrProvider or
    ///    AppleSpeechAnalyzerProvider; whisper.cpp is this head's equivalent of
    ///    the latter, so it is gated the same way.
    /// 2. The unanchored, diacritic-insensitive substring pass, for every local
    ///    engine — which is what all four macOS local providers do.
    /// </summary>
    private static PortableTranscriptionResult ApplyLocalVocabulary(
        PortableTranscriptionResult result,
        TranscriptionWorkflowRequest request,
        bool isParakeet)
    {
        if (!result.IsSuccess) return result;

        var entries = BuildVocabularyEntries(request);
        if (entries.Count == 0) return result;

        var text = result.Text!;
        if (isParakeet) text = SharedCoreBridge.ApplyPhoneticVocabulary(text, entries).Text;
        text = SharedCoreBridge.ApplySubstringVocabulary(text, entries);

        return string.Equals(text, result.Text, StringComparison.Ordinal)
            ? result
            : result with { Text = text };
    }

    /// <summary>
    /// Rebuild the whole vocabulary rows — word plus its optional replacement —
    /// from the two lists the request already carries.
    ///
    /// <c>Vocabulary</c> is every word in list order; <c>VocabularyReplacements</c>
    /// is the subset that has a replacement. macOS and Windows hand the core
    /// whole rows because they read them straight out of Core Data / EF; this
    /// head reassembles the same shape rather than growing a third field that
    /// could go stale against the two above.
    ///
    /// A duplicate word keeps its LAST replacement, matching the dictionary
    /// build rather than throwing on it — legacy rows can carry duplicates.
    ///
    /// <c>ModeVocabularyReplacements</c> is deliberately NOT folded in. macOS
    /// and Windows run both local passes over the GLOBAL vocabulary only, and
    /// this head passes an empty mode list anyway; merging it here would invent
    /// a precedence rule neither platform has.
    /// </summary>
    private static List<PortableVocabularyEntry> BuildVocabularyEntries(
        TranscriptionWorkflowRequest request)
    {
        var words = request.Vocabulary ?? [];
        if (words.Count == 0) return [];

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in request.VocabularyReplacements ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Word)) continue;
            replacements[entry.Word] = entry.Replacement;
        }

        return [.. words
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Select(word => new PortableVocabularyEntry(
                word,
                replacements.TryGetValue(word, out var replacement) ? replacement : null))];
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
                provider == CloudTranscriptionProvider.Gemini ? Normalize(mode.GeminiCustomPrompt) : null,
                // `Model` is what a BYOK adapter puts in its own request body. It
                // is NOT what the HW-Cloud-routed providers send: their wire model
                // is `X-STT-Model`, which hw-net builds from `routed_model` alone
                // (`azure_mai::build_transcribe_request` → `hyperwhisper_cloud::
                // build_routed_request`). Leaving it null let the backend pick its
                // own default, so a mode pinned to `mai-transcribe-1.5` ran — and
                // billed — as `mai-transcribe-2`. See RoutedModelFor.
                RoutedModel: RoutedModelFor(provider, mode.CloudTranscriptionModel));

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
            "meta" => CloudTranscriptionProvider.Meta,
            _ => default,
        };
        return value?.Trim().ToLowerInvariant() is
            "openai" or "groq" or "elevenlabs" or "mistral" or "grok" or "deepgram"
            or "assemblyai" or "soniox" or "gemini" or "geminitranscribe"
            or "gemini-transcribe" or "microsoftazurespeech"
            or "azure-mai" or "googlespeech" or "google-chirp" or "hyperwhisper" or "meta";
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

    /// <summary>
    /// The <c>X-STT-Model</c> value for a provider that has NO direct vendor
    /// route and always terminates at the HyperWhisper Cloud <c>/transcribe</c>
    /// proxy. Null for every BYOK provider, whose model travels in its own
    /// request body instead — those keep today's behaviour exactly.
    ///
    /// Azure MAI is the reachable case: <c>TryMapProvider</c> accepts a mode
    /// carrying <c>cloudProvider = "microsoftazurespeech"</c> (the Linux Modes
    /// screen is a free-text field, and a backup restore or Local API write can
    /// set it on any head), and both MAI models are served by the same proxy.
    /// Without a routed model the backend applies its own default, so picking
    /// 1.5 silently ran v2 — a different transcribeStyle and a different price.
    ///
    /// Validation mirrors the HyperWhisper Cloud branch below: a model that is
    /// not a pre-recorded model of this provider's catalog entry (a stale id, a
    /// BYOK id left in the shared field, a live-only id) falls back to the
    /// entry's default rather than being forwarded into a backend 400.
    ///
    /// GoogleChirp is the one other provider routed this way and it has the
    /// same gap, but NOT the same fix: catalog v8 retired its `googleChirp3`
    /// entry, so there is no model list to validate against and no default to
    /// fall back to (see <see cref="CatalogTier"/>). Left as-is deliberately.
    /// </summary>
    private static string? RoutedModelFor(CloudTranscriptionProvider provider, string? storedModel)
    {
        if (provider != CloudTranscriptionProvider.AzureMai) return null;

        var tier = CatalogTier(provider);
        var trimmed = storedModel?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && SharedCoreBridge.CloudSttContainsDictationModel(tier, trimmed)
                ? trimmed
                : SharedCoreBridge.CloudSttDefaultModel(tier);
    }

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
        CloudTranscriptionProvider.Meta => "metaMuse",
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
