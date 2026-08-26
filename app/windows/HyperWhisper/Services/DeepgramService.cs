// DEEPGRAM SERVICE
// Cloud transcription via Deepgram's Speech-to-Text API.
// Nova models offer best-in-class accuracy for speech recognition.
//
// API ENDPOINT: POST https://api.deepgram.com/v1/listen?model=MODEL&...
//
// REQUEST FORMAT: Binary POST (raw audio data, NOT multipart)
// - Content-Type: audio/wav (or appropriate MIME type)
// - Body: Raw audio bytes
// - Query params: model, language, detect_language, smart_format, keyterm/keywords
//
// RESPONSE FORMAT: JSON with results.channels[0].alternatives[0].transcript
//
// VOCABULARY BOOSTING:
// - Nova-3 monolingual: Use "keyterm" parameter (up to 90% KRR improvement)
// - Nova-2/Nova-1/Enhanced: Use "keywords" parameter (multilingual support)
// - Nova-3 with auto-detect: No vocabulary support (keyterm silently ignored)
//
// LIMITS:
// - No explicit file size limit (streaming supported)
// - Most audio formats supported
//
// ERROR HANDLING:
// - 401: Invalid API key
// - 403: Forbidden (key doesn't have permission)
// - 429: Rate limited
// - 400: Invalid request
//
// NOTE: Uses TranscriptionApiKeyType.Deepgram (separate from post-processing)

using System.Diagnostics;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using Deepgram's Speech-to-Text API.
/// Nova models provide industry-leading accuracy with vocabulary boosting.
/// </summary>
public class DeepgramService : ApiKeyTranscriptionServiceBase
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 180; // 3 minutes for larger files

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Display name including the configured model.
    /// </summary>
    public override string Name => $"Deepgram {CloudTranscriptionModels.GetById(ModelId, CloudTranscriptionProvider.Deepgram)?.DisplayName ?? ModelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public DeepgramService()
        : base(TimeSpan.FromSeconds(DefaultTimeoutSeconds), "nova-3-general")
    {
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with API key and model.
    /// Must be called before transcription.
    /// </summary>
    /// <param name="apiKey">Deepgram API key.</param>
    /// <param name="modelId">Model ID (nova-3-general, nova-2-medical, enhanced-general, base-general, whisper-*).</param>
    public override void Configure(string apiKey, string modelId = "nova-3-general")
    {
        ApiKey = apiKey;
        ModelId = CloudTranscriptionModels.ResolveDeepgramModelAlias(modelId);
        LoggingService.Info($"DeepgramService: Configured with model {ModelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using Deepgram's API.
    /// </summary>
    public override async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== DEEPGRAM CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {ModelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");

        // STEP 1+2: Validate configuration and audio file (shared gate). Deepgram
        // has no explicit file size limit, so no cap is passed.
        TranscriptionPreflight.Validate("Deepgram", ApiKey, audioPath);

        // STEP 3: Build the request via the Rust shared core (URL + query
        // params model/smart_format/keyterm/keywords/language, Content-Type, and a
        // Body.FileStream binary stream — audio never crosses FFI), then drive it
        // through the shared executor + core retry loop. The core owns the
        // keyterm-vs-keywords + auto-detect vocab gating per model.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            // Direct-vendor request: the core cannot attach X-Latency-Opt-Out to
            // one by construction. Pass the user's real choice anyway so this site
            // stays correct if it is ever routed.
            shareAnonymousSpeedData: SettingsService.Instance.ShareAnonymousSpeedData,
            apiKey: ApiKey,
            model: ModelId);

        return await RustSingleShot.TranscribeAsync(
            Http,
            "Deepgram",
            buildRequest: () => HyperwhisperCoreMethods.DeepgramBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.DeepgramParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }
}
