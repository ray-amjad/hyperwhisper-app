// OPENAI WHISPER SERVICE
// Cloud transcription via OpenAI's Whisper API.
// Supports whisper-1, gpt-4o-transcribe, and gpt-4o-mini-transcribe models.
//
// API ENDPOINT: POST https://api.openai.com/v1/audio/transcriptions
//
// REQUEST FORMAT: multipart/form-data
// - file: Audio file (WAV, MP3, M4A, etc.)
// - model: Model ID (whisper-1, gpt-4o-transcribe, gpt-4o-mini-transcribe)
// - language: ISO 639-1 language code (optional, for better accuracy)
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

using System.Diagnostics;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using OpenAI's Whisper API.
/// Implements ITranscriptionProvider for unified provider abstraction.
/// </summary>
public class OpenAIWhisperService : ApiKeyTranscriptionServiceBase
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB
    private const int DefaultTimeoutSeconds = 120; // 2 minutes for large files

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Display name. It names no model: the model is per call (issue #753).
    /// </summary>
    public override string Name => "OpenAI";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public OpenAIWhisperService(HttpMessageHandler? httpHandler = null)
        : base(TimeSpan.FromSeconds(DefaultTimeoutSeconds), "whisper-1", httpHandler: httpHandler)
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
    /// Transcribes audio using OpenAI's Whisper API.
    /// </summary>
    protected override async Task<string> TranscribeCoreAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var (audioPath, language, vocabulary) = (request.AudioPath, request.Language, request.Vocabulary);
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== OPENAI CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {request.ModelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");

        // STEP 1+2: Validate configuration and audio file (shared gate).
        TranscriptionPreflight.Validate("OpenAI", ApiKey, audioPath, MaxFileSizeBytes, "25 MB");

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        var coreParams = BuildDirectVendorParams(request, contentType);

        return await RustSingleShot.TranscribeAsync(
            Http,
            "OpenAI",
            buildRequest: () => HyperwhisperCoreMethods.OpenaiBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.OpenaiParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }
}
