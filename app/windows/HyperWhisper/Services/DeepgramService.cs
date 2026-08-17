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
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using Deepgram's Speech-to-Text API.
/// Nova models provide industry-leading accuracy with vocabulary boosting.
/// </summary>
public class DeepgramService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 180; // 3 minutes for larger files

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private string _modelId = "nova-3-general";
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
    public string Name => $"Deepgram {CloudTranscriptionModels.GetById(_modelId, CloudTranscriptionProvider.Deepgram)?.DisplayName ?? _modelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public DeepgramService()
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
    /// <param name="apiKey">Deepgram API key.</param>
    /// <param name="modelId">Model ID (nova-3-general, nova-2-medical, enhanced-general, base-general, whisper-*).</param>
    public void Configure(string apiKey, string modelId = "nova-3-general")
    {
        _apiKey = apiKey;
        _modelId = CloudTranscriptionModels.ResolveDeepgramModelAlias(modelId);
        LoggingService.Info($"DeepgramService: Configured with model {_modelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using Deepgram's API.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== DEEPGRAM CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {_modelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio path: {audioPath}");

        // STEP 1+2: Validate configuration and audio file (shared gate). Deepgram
        // has no explicit file size limit, so no cap is passed.
        TranscriptionPreflight.Validate("Deepgram", _apiKey, audioPath);

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
            apiKey: _apiKey,
            model: _modelId);

        return await RustSingleShot.TranscribeAsync(
            _httpClient,
            "Deepgram",
            buildRequest: () => HyperwhisperCoreMethods.DeepgramBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.DeepgramParseTranscribeResponse,
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
