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
    /// Display name including the configured model.
    /// </summary>
    public override string Name => $"OpenAI {CloudTranscriptionModels.GetById(ModelId)?.DisplayName ?? ModelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public OpenAIWhisperService()
        : base(TimeSpan.FromSeconds(DefaultTimeoutSeconds), "whisper-1")
    {
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with API key and model.
    /// Must be called before transcription.
    /// </summary>
    /// <param name="apiKey">OpenAI API key (starts with "sk-").</param>
    /// <param name="modelId">Model ID (whisper-1, gpt-4o-transcribe, gpt-4o-mini-transcribe).</param>
    public override void Configure(string apiKey, string modelId = "whisper-1")
    {
        ApiKey = apiKey;
        ModelId = modelId;
        LoggingService.Info($"OpenAIWhisperService: Configured with model {modelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using OpenAI's Whisper API.
    /// </summary>
    public override async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== OPENAI CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {ModelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");

        // STEP 1+2: Validate configuration and audio file (shared gate).
        TranscriptionPreflight.Validate("OpenAI", ApiKey, audioPath, MaxFileSizeBytes, "25 MB");

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        var coreParams = BuildDirectVendorParams(audioPath, contentType, language, vocabulary);

        return await RustSingleShot.TranscribeAsync(
            Http,
            "OpenAI",
            buildRequest: () => HyperwhisperCoreMethods.OpenaiBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.OpenaiParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }
}
