// GEMINI 3.5 TRANSCRIBE SERVICE
// Cloud transcription via Google's dedicated speech endpoint,
// POST https://generativelanguage.googleapis.com/v1beta/interactions
//
// NOT the same thing as GeminiTranscriptionService. That one is the multimodal
// Files API + `:generateContent` path. `:generateContent` ACCEPTS the
// gemini-3.5-transcribe model, BILLS the audio, and returns empty text with no
// error — a silent, paid no-op. The transcribe models only work here.
//
// AUTHENTICATION: `x-goog-api-key` header (not `?key=`), plus a pinned
// `Api-Revision` header. Its own BYOK key slot
// (TranscriptionApiKeyType.GeminiTranscribe) — it does NOT share the Gemini key.
//
// TRANSPORT: the endpoint has no file-reference form, so the audio rides inline
// as base64 in the JSON body. The core emits Body.JsonWithBase64File (prefix,
// path, suffix) and RustHttpExecutor does the read+encode, so the bytes still
// never cross the FFI boundary. Base64 inflates ~33%, hence the 14 MB raw cap on
// CloudTranscriptionProvider.GeminiTranscribe.
//
// VOCABULARY: sent as `custom_vocabulary`. The core owns the mutual-exclusion
// rule against diarization/timestamps; nothing to do here.

using System.Diagnostics;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using Google Gemini 3.5 Transcribe
/// (/v1beta/interactions). Single-shot: build, send, parse.
/// </summary>
public class GeminiTranscribeService : ApiKeyTranscriptionServiceBase
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 300;
    private const string DefaultModelId = "gemini-3.5-transcribe";

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    public override string Name =>
        $"Gemini {CloudTranscriptionModels.GetById(ModelId, CloudTranscriptionProvider.GeminiTranscribe)?.DisplayName ?? ModelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public GeminiTranscribeService()
        : base(TimeSpan.FromSeconds(DefaultTimeoutSeconds), DefaultModelId)
    {
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with an API key and a model.
    /// </summary>
    /// <param name="apiKey">Google AI Studio API key ("AIza…").</param>
    /// <param name="modelId">Model ID (only gemini-3.5-transcribe today).</param>
    public override void Configure(string apiKey, string modelId = DefaultModelId)
    {
        ApiKey = apiKey?.Trim();
        // No alias table for this provider yet — an empty id falls back to the
        // single pre-recorded model rather than reaching the core's own default.
        ModelId = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId;
        LoggingService.Info($"GeminiTranscribeService: Configured with model {ModelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    public override async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== GEMINI 3.5 TRANSCRIBE CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {ModelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");

        // Validate configuration and audio file (shared gate). The cap is on the
        // RAW file: the request carries it base64-encoded, ~33% larger.
        var maxFileSize = CloudTranscriptionProvider.GeminiTranscribe.GetMaxFileSizeBytes();
        TranscriptionPreflight.Validate(
            "Gemini 3.5 Transcribe", ApiKey, audioPath, maxFileSize, $"{maxFileSize / 1024 / 1024} MB");

        // The accepted containers are the Whisper-style standard set, so this
        // provider uses TranscriptionPreflight's shared map unchanged.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        // Build core params once. Pass the RAW vocab list — the core normalizes
        // it into `custom_vocabulary` and owns the exclusion rule against
        // diarization/timestamps.
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
            "Gemini 3.5 Transcribe",
            buildRequest: () => HyperwhisperCoreMethods.GeminiTranscribeBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.GeminiTranscribeParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }
}
