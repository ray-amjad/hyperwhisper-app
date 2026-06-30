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
/// Cloud transcription service using Groq's Whisper API.
/// OpenAI-compatible API with Groq's fast LPU inference.
/// </summary>
public class GroqWhisperService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const string ApiEndpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB
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
    private string _modelId = "whisper-large-v3-turbo";
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
    public string Name => $"Groq {CloudTranscriptionModels.GetById(_modelId, CloudTranscriptionProvider.Groq)?.DisplayName ?? _modelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public GroqWhisperService()
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
    /// <param name="apiKey">Groq API key (starts with "gsk_").</param>
    /// <param name="modelId">Model ID (whisper-large-v3-turbo, whisper-large-v3).</param>
    public void Configure(string apiKey, string modelId = "whisper-large-v3-turbo")
    {
        _apiKey = apiKey;
        _modelId = modelId;
        LoggingService.Info($"GroqWhisperService: Configured with model {modelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using Groq's Whisper API.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== GROQ CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {_modelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio path: {audioPath}");

        // STEP 1: Validate configuration
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "Groq API key not configured",
                "Groq");
        }

        // STEP 2: Validate audio file
        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "Groq");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        if (fileInfo.Length > MaxFileSizeBytes)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.FileTooLarge,
                $"File size ({fileInfo.Length / 1024.0 / 1024.0:F1} MB) exceeds 25 MB limit",
                "Groq");
        }

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop.
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
                buildRequest: () => HyperwhisperCoreMethods.GroqBuildTranscribeRequest(coreParams),
                parseError: resp => RustCoreMapping.ParseProviderError(
                    () => HyperwhisperCoreMethods.GroqParseTranscribeResponse(resp), "Groq", resp),
                cancellationToken: cancellationToken);
        }
        catch (HwTranscriptionException ex)
        {
            // Thrown by GroqBuildTranscribeRequest (request-build validation).
            throw RustCoreMapping.MapTranscriptionError(ex, "Groq");
        }

        cancellationToken.ThrowIfCancellationRequested();

        HwTranscript transcript;
        try
        {
            transcript = HyperwhisperCoreMethods.GroqParseTranscribeResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "Groq");
        }

        LoggingService.Info("========== GROQ TRANSCRIPTION COMPLETE ==========");
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
