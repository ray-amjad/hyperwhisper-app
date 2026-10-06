// MISTRAL SERVICE
// Cloud transcription via Mistral's Voxtral Speech-to-Text API.
//
// API ENDPOINT: POST https://api.mistral.ai/v1/audio/transcriptions
//
// REQUEST FORMAT: multipart/form-data
// - file: Audio file
// - model: Model ID (voxtral-mini-latest)
// - language: ISO 639-1 language code (optional)
//
// RESPONSE FORMAT: { "text": "transcribed text" }
//
// AUTHENTICATION: x-api-key: {api_key}
//
// LIMITS:
// - Supported formats: mp3, mp4, m4a, wav, webm, ogg, flac
//
// IMPORTANT: No free-text prompt; vocabulary goes out as `context_bias`
//
// ERROR HANDLING:
// - 401: Invalid API key
// - 429: Rate limited
// - 400: Invalid request
//
// NOTE: Uses TranscriptionApiKeyType.Mistral (separate from post-processing)

using System.Diagnostics;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using Mistral's Voxtral API.
/// Vocabulary is sent as a `context_bias` list (max 100 terms).
/// </summary>
public class MistralService : ApiKeyTranscriptionServiceBase
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 120;

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Display name. It names no model: the model is per call (issue #753).
    /// </summary>
    public override string Name => "Mistral";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public MistralService(HttpMessageHandler? httpHandler = null)
        : base(TimeSpan.FromSeconds(DefaultTimeoutSeconds), "voxtral-mini-latest", httpHandler: httpHandler)
    {
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with an API key. The model is not configured
    /// here: it travels in each call's request (issue #753).
    /// </summary>
    public override void Configure(string apiKey)
    {
        ApiKey = apiKey;
    }

    /// <inheritdoc />
    protected override string ResolveModelId(string modelId)
        => modelId;

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using Mistral's Voxtral API.
    /// Vocabulary terms are sent as a `context_bias` list (max 100 terms).
    /// </summary>
    protected override async Task<string> TranscribeCoreAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var (audioPath, language, vocabulary) = (request.AudioPath, request.Language, request.Vocabulary);
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== MISTRAL CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {request.ModelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");

        if (vocabulary?.Count > 0)
        {
            LoggingService.Info($"  Vocabulary sent as context_bias: {vocabulary.Count} term(s) before capping");
        }

        // STEP 1+2: Validate configuration and audio file (shared gate). Mistral
        // does not cap the file size client-side.
        TranscriptionPreflight.Validate("Mistral", ApiKey, audioPath);

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop. Pass the RAW vocabulary
        // list — the core normalizes it and caps the `context_bias` field.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        var coreParams = BuildDirectVendorParams(request, contentType);

        return await RustSingleShot.TranscribeAsync(
            Http,
            "Mistral",
            buildRequest: () => HyperwhisperCoreMethods.MistralBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.MistralParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }
}
