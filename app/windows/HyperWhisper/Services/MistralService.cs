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
// IMPORTANT: Does NOT support custom vocabulary/prompts
//
// ERROR HANDLING:
// - 401: Invalid API key
// - 429: Rate limited
// - 400: Invalid request
//
// NOTE: Uses TranscriptionApiKeyType.Mistral (separate from post-processing)

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
/// Cloud transcription service using Mistral's Voxtral API.
/// Note: Does NOT support custom vocabulary.
/// </summary>
public class MistralService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const string ApiEndpoint = "https://api.mistral.ai/v1/audio/transcriptions";
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
    /// Note: vocabulary parameter is ignored as Mistral doesn't support it.
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

        // Warn if vocabulary was provided (not supported)
        if (vocabulary?.Count > 0)
        {
            LoggingService.Warn($"  Warning: Vocabulary ignored - Mistral does not support custom vocabulary");
        }

        // STEP 1: Validate configuration
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "Mistral API key not configured",
                "Mistral");
        }

        // STEP 2: Validate audio file
        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "Mistral");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop. Mistral does not support
        // custom vocabulary; pass an empty term list.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var extension = Path.GetExtension(audioPath);
        var contentType = MimeTypes.GetValueOrDefault(extension, "audio/wav");

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: Array.Empty<string>(),
            apiKey: _apiKey,
            model: _modelId);

        uniffi.hyperwhisper_core.HttpResponse response;
        try
        {
            response = await RustRetry.PerformAsync(
                _httpClient,
                buildRequest: () => HyperwhisperCoreMethods.MistralBuildTranscribeRequest(coreParams),
                parseError: resp => RustCoreMapping.ParseProviderError(
                    () => HyperwhisperCoreMethods.MistralParseTranscribeResponse(resp), "Mistral", resp),
                cancellationToken: cancellationToken);
        }
        catch (HwTranscriptionException ex)
        {
            // Thrown by MistralBuildTranscribeRequest (request-build validation).
            throw RustCoreMapping.MapTranscriptionError(ex, "Mistral");
        }

        cancellationToken.ThrowIfCancellationRequested();

        HwTranscript transcript;
        try
        {
            transcript = HyperwhisperCoreMethods.MistralParseTranscribeResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "Mistral");
        }

        LoggingService.Info("========== MISTRAL TRANSCRIPTION COMPLETE ==========");
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
