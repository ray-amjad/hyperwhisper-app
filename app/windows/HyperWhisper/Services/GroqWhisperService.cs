// GROQ WHISPER SERVICE
// Cloud transcription via Groq's OpenAI-compatible Whisper API.
// Uses Groq's LPU hardware for extremely fast inference.
// Supports whisper-large-v3-turbo and whisper-large-v3 models.
//
// API ENDPOINT: POST https://api.groq.com/openai/v1/audio/transcriptions
//
// REQUEST FORMAT: multipart/form-data (OpenAI-compatible)
// - file: Audio file (WAV, MP3, M4A, etc.)
// - model: Model ID (whisper-large-v3-turbo, whisper-large-v3)
// - language: ISO 639-1 language code (optional)
// - prompt: Vocabulary/context hints (optional)
// - response_format: "json" for structured response
//
// RESPONSE FORMAT: { "text": "transcribed text" }
//
// LIMITS:
// - Max file size: 25 MB
// - Supported formats: mp3, mp4, mpeg, mpga, m4a, wav, webm
//
// ERROR HANDLING:
// - 401: Invalid API key
// - 429: Rate limited or quota exceeded
// - 413: File too large
// - 400/422: Invalid request
//
// NOTE: Shares API key with Groq post-processing (PostProcessingProvider.Groq)

using System.Diagnostics;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using Groq's Whisper API.
/// OpenAI-compatible API with Groq's fast LPU inference.
/// </summary>
public class GroqWhisperService : ApiKeyTranscriptionServiceBase
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB
    private const int DefaultTimeoutSeconds = 120;

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Display name including the configured model.
    /// </summary>
    public override string Name => $"Groq {CloudTranscriptionModels.GetById(ModelId, CloudTranscriptionProvider.Groq)?.DisplayName ?? ModelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public GroqWhisperService()
        : base(TimeSpan.FromSeconds(DefaultTimeoutSeconds), "whisper-large-v3-turbo")
    {
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with API key and model.
    /// Must be called before transcription.
    /// </summary>
    /// <param name="apiKey">Groq API key (starts with "gsk_").</param>
    /// <param name="modelId">Model ID (whisper-large-v3-turbo, whisper-large-v3).</param>
    public override void Configure(string apiKey, string modelId = "whisper-large-v3-turbo")
    {
        ApiKey = apiKey;
        ModelId = modelId;
        LoggingService.Info($"GroqWhisperService: Configured with model {modelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using Groq's Whisper API.
    /// </summary>
    public override async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== GROQ CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {ModelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio path: {audioPath}");

        // STEP 1+2: Validate configuration and audio file (shared gate).
        TranscriptionPreflight.Validate("Groq", ApiKey, audioPath, MaxFileSizeBytes, "25 MB");

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop.
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
            "Groq",
            buildRequest: () => HyperwhisperCoreMethods.GroqBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.GroqParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }
}
