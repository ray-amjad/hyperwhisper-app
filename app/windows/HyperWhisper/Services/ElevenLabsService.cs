// ELEVENLABS SERVICE
// Cloud transcription via ElevenLabs' Scribe Speech-to-Text API.
//
// API ENDPOINT: POST https://api.elevenlabs.io/v1/speech-to-text
//
// REQUEST FORMAT: multipart/form-data
// - file: Audio file
// - model_id: Model ID (scribe_v2, scribe_v1)
// - language_code: ISO 639-1 language code (optional, auto-detect if not specified)
// - keyterms: Array of strings for vocabulary boosting (scribe_v2 only, up to 100 terms)
//
// RESPONSE FORMAT: { "text": "transcribed text", ... }
//
// AUTHENTICATION: xi-api-key header (NOT Bearer token)
//
// LIMITS:
// - Supported formats: mp3, mp4, m4a, wav, webm, ogg, flac
// - Keyterms: max 100 terms, each < 50 characters, max 5 words per term
//
// VOCABULARY SUPPORT:
// - scribe_v2: Supports keyterms for custom vocabulary boosting
// - scribe_v1: Does NOT support custom vocabulary
//
// ERROR HANDLING:
// - 401: Invalid API key
// - 429: Rate limited
// - 400: Invalid request
//
// NOTE: Uses TranscriptionApiKeyType.ElevenLabs (separate from post-processing)

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HwTranscript / HwTranscriptionException / HttpResponse
// collide with System / HyperWhisper types; qualify with
// `uniffi.hyperwhisper_core.` where ambiguous (HttpResponse below).
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using ElevenLabs' Scribe API.
/// Scribe V2 supports custom vocabulary via keyterms (up to 100 terms).
/// Scribe V1 does NOT support custom vocabulary.
/// </summary>
public class ElevenLabsService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const string ApiEndpoint = "https://api.elevenlabs.io/v1/speech-to-text";
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxRetries = 3;

    // Supported audio MIME types
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

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private string _modelId = "scribe_v2";
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
    public string Name => $"ElevenLabs {CloudTranscriptionModels.GetById(_modelId, CloudTranscriptionProvider.ElevenLabs)?.DisplayName ?? _modelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public ElevenLabsService()
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
    /// <param name="apiKey">ElevenLabs API key.</param>
    /// <param name="modelId">Model ID (scribe_v2 for keyterm support, scribe_v1 for legacy).</param>
    public void Configure(string apiKey, string modelId = "scribe_v2")
    {
        _apiKey = apiKey;
        _modelId = modelId;
        LoggingService.Info($"ElevenLabsService: Configured with model {modelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using ElevenLabs' Scribe API.
    /// Scribe V2: vocabulary is sent as keyterms (up to 100 terms, each &lt; 50 chars).
    /// Scribe V1: vocabulary is ignored (not supported).
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        var isScribeV2 = _modelId == "scribe_v2";

        LoggingService.Info("========== ELEVENLABS CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {_modelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Audio path: {audioPath}");

        // Handle vocabulary based on model
        if (vocabulary?.Count > 0)
        {
            if (isScribeV2)
            {
                LoggingService.Info($"  Vocabulary terms: {vocabulary.Count} (will be sent as keyterms)");
            }
            else
            {
                LoggingService.Warn($"  Warning: Vocabulary ignored - Scribe V1 does not support keyterms");
            }
        }

        // STEP 1: Validate configuration
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "ElevenLabs API key not configured",
                "ElevenLabs");
        }

        // STEP 2: Validate audio file
        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "ElevenLabs");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop. The core owns language
        // normalization, keyterms (scribe_v2), tag_audio_events, and the
        // multi-format ({text} / {transcripts} / {words}) response parsing.
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
                buildRequest: () => HyperwhisperCoreMethods.ElevenlabsBuildTranscribeRequest(coreParams),
                parseError: resp => RustCoreMapping.ParseProviderError(
                    () => HyperwhisperCoreMethods.ElevenlabsParseTranscribeResponse(resp), "ElevenLabs", resp),
                cancellationToken: cancellationToken);
        }
        catch (HwTranscriptionException ex)
        {
            // Thrown by ElevenlabsBuildTranscribeRequest (request-build validation).
            throw RustCoreMapping.MapTranscriptionError(ex, "ElevenLabs");
        }

        cancellationToken.ThrowIfCancellationRequested();

        HwTranscript transcript;
        try
        {
            transcript = HyperwhisperCoreMethods.ElevenlabsParseTranscribeResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "ElevenLabs");
        }

        LoggingService.Info("========== ELEVENLABS TRANSCRIPTION COMPLETE ==========");
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
