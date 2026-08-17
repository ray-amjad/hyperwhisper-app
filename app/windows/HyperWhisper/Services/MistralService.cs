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
public class MistralService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 120;

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private string _modelId = "voxtral-mini-latest";
    private bool _disposed;

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Whether the service is ready (API key is configured).
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Display name including the configured model.
    /// </summary>
    public string Name => $"Mistral {CloudTranscriptionModels.GetById(_modelId, CloudTranscriptionProvider.Mistral)?.DisplayName ?? _modelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public MistralService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds)
        };
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with API key and model.
    /// Must be called before transcription.
    /// </summary>
    /// <param name="apiKey">Mistral API key.</param>
    /// <param name="modelId">Model ID (voxtral-mini-latest).</param>
    public void Configure(string apiKey, string modelId = "voxtral-mini-latest")
    {
        _apiKey = apiKey;
        _modelId = modelId;
        LoggingService.Info($"MistralService: Configured with model {modelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using Mistral's Voxtral API.
    /// Vocabulary terms are sent as a `context_bias` list (max 100 terms).
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== MISTRAL CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {_modelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Audio path: {audioPath}");

        if (vocabulary?.Count > 0)
        {
            LoggingService.Info($"  Vocabulary sent as context_bias: {vocabulary.Count} term(s) before capping");
        }

        // STEP 1+2: Validate configuration and audio file (shared gate). Mistral
        // does not cap the file size client-side.
        TranscriptionPreflight.Validate("Mistral", _apiKey, audioPath);

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop. Pass the RAW vocabulary
        // list — the core normalizes it and caps the `context_bias` field.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            apiKey: _apiKey,
            model: _modelId);

        return await RustSingleShot.TranscribeAsync(
            _httpClient,
            "Mistral",
            buildRequest: () => HyperwhisperCoreMethods.MistralBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.MistralParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }


    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
