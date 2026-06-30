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
using System.IO;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HwTranscript / HwTranscriptionException / HttpResponse
// collide with System types; qualify uniffi.hyperwhisper_core.HttpResponse below.
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

    private const string ApiBaseUrl = "https://api.deepgram.com/v1/listen";
    private const int DefaultTimeoutSeconds = 180; // 3 minutes for larger files
    private const int MaxRetries = 3;

    // MIME types for audio content
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".wav", "audio/wav" },
        { ".mp3", "audio/mpeg" },
        { ".mp4", "audio/mp4" },
        { ".m4a", "audio/mp4" },
        { ".mpeg", "audio/mpeg" },
        { ".mpga", "audio/mpeg" },
        { ".webm", "audio/webm" },
        { ".ogg", "audio/ogg" },
        { ".flac", "audio/flac" }
    };

    // Models that support keyterm parameter (Nova-3)
    private static readonly HashSet<string> KeytermSupportedModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "nova-3-general",
        "nova-3-medical"
    };

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

        // STEP 1: Validate configuration
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "Deepgram API key not configured",
                "Deepgram");
        }

        // STEP 2: Validate audio file
        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "Deepgram");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        // STEP 3: Build the request via the Rust shared core (URL + query
        // params model/smart_format/keyterm/keywords/language, Content-Type, and a
        // Body.FileStream binary stream — audio never crosses FFI), then drive it
        // through the shared executor + core retry loop. The core owns the
        // keyterm-vs-keywords + auto-detect vocab gating per model.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var extension = Path.GetExtension(audioPath);
        var contentType = MimeTypes.GetValueOrDefault(extension, "audio/wav");

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            apiKey: _apiKey,
            model: _modelId);

        uniffi.hyperwhisper_core.HttpResponse response;
        try
        {
            response = await RustRetry.PerformAsync(
                _httpClient,
                buildRequest: () => HyperwhisperCoreMethods.DeepgramBuildTranscribeRequest(coreParams),
                parseError: resp => RustCoreMapping.ParseProviderError(
                    () => HyperwhisperCoreMethods.DeepgramParseTranscribeResponse(resp), "Deepgram", resp),
                cancellationToken: cancellationToken);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "Deepgram");
        }

        cancellationToken.ThrowIfCancellationRequested();

        HwTranscript transcript;
        try
        {
            transcript = HyperwhisperCoreMethods.DeepgramParseTranscribeResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "Deepgram");
        }

        LoggingService.Info("========== DEEPGRAM TRANSCRIPTION COMPLETE ==========");
        LoggingService.Info($"  Characters: {transcript.@text.Length}");
        LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
        return transcript.@text;
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
