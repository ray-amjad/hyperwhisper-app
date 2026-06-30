// GROK STT SERVICE
// Cloud transcription via xAI Grok speech-to-text batch HTTP endpoint.
//
// API ENDPOINT: POST https://api.x.ai/v1/stt
//
// REQUEST FORMAT: multipart/form-data
// - file: audio file (last per docs)
// - language: supported formatting code (e.g., "en") — only sent when caller
//   provides a Grok-supported language selection
// - format: "true" — only sent alongside a supported `language`
//
// NOTE: No `model` parameter (single implicit model) and no prompt/vocabulary parameter.
//
// RESPONSE FORMAT: { "text": "transcribed text", "language": "...", "duration": ..., "words": [...] }
//
// LIMITS:
// - Max file size: 500 MB
// - Supported containers (auto-detected): wav, mp3, ogg, opus, flac, aac, mp4, m4a, mkv

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
/// Cloud transcription service using xAI Grok speech-to-text batch HTTP API.
/// </summary>
public class GrokSttService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const string ApiEndpoint = "https://api.x.ai/v1/stt";
    private const long MaxFileSizeBytes = 500L * 1024 * 1024; // 500 MB

    // xAI only supports language-driven formatting for this subset. Unsupported
    // language selections should omit both `language` and `format=true`.
    private static readonly HashSet<string> SupportedFormattingLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi"
    };

    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "tl", "fil" }
    };

    // Audio MIME types Grok accepts (containers auto-detected by API)
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".wav", "audio/wav" },
        { ".mp3", "audio/mpeg" },
        { ".ogg", "audio/ogg" },
        { ".opus", "audio/opus" },
        { ".flac", "audio/flac" },
        { ".aac", "audio/aac" },
        { ".mp4", "video/mp4" },
        { ".m4a", "audio/mp4" },
        { ".mkv", "video/x-matroska" }
    };

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private bool _disposed;

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public string Name => "Grok";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public GrokSttService()
    {
        _httpClient = new HttpClient
        {
            // Finite per-request cap (G1) — every sibling STT service uses 120–300s.
            // With RustRetry.PerformAsync receiving only the cancellation token, an
            // InfiniteTimeSpan let a stalled xAI send hang forever. 300s matches the
            // longest sibling (Gemini) and suits large audio uploads; each retry
            // attempt's send inherits this cap.
            Timeout = TimeSpan.FromSeconds(300)
        };
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with an API key.
    /// `modelId` is accepted for factory signature uniformity but ignored — Grok
    /// STT has no `model` parameter (single implicit model).
    /// </summary>
    public void Configure(string apiKey, string modelId = "")
    {
        _apiKey = apiKey?.Trim();
        LoggingService.Info("GrokSttService: Configured");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== GROK CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio path: {audioPath}");

        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "Grok API key not configured",
                "Grok");
        }

        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "Grok");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        if (fileInfo.Length > MaxFileSizeBytes)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.FileTooLarge,
                $"File size ({fileInfo.Length / 1024.0 / 1024.0:F1} MB) exceeds 500 MB limit",
                "Grok");
        }

        if (vocabulary?.Count > 0)
        {
            LoggingService.Info($"  Grok STT does not support custom vocabulary — {vocabulary.Count} term(s) will be ignored");
        }

        // Build the request via the Rust shared core, then drive it through the
        // shared executor + core retry loop. The core owns the language gating
        // (`language` + `format=true`) and the multipart assembly; Grok has no
        // model and no vocabulary, so pass an empty term list.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var extension = Path.GetExtension(audioPath);
        var contentType = MimeTypes.GetValueOrDefault(extension, "application/octet-stream");

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: Array.Empty<string>(),
            apiKey: _apiKey);

        uniffi.hyperwhisper_core.HttpResponse response;
        try
        {
            response = await RustRetry.PerformAsync(
                _httpClient,
                buildRequest: () => HyperwhisperCoreMethods.GrokBuildTranscribeRequest(coreParams),
                parseError: resp => RustCoreMapping.ParseProviderError(
                    () => HyperwhisperCoreMethods.GrokParseTranscribeResponse(resp), "Grok", resp),
                cancellationToken: cancellationToken);
        }
        catch (HwTranscriptionException ex)
        {
            // Thrown by GrokBuildTranscribeRequest (request-build validation).
            throw RustCoreMapping.MapTranscriptionError(ex, "Grok");
        }

        cancellationToken.ThrowIfCancellationRequested();

        HwTranscript transcript;
        try
        {
            transcript = HyperwhisperCoreMethods.GrokParseTranscribeResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "Grok");
        }

        LoggingService.Info("========== GROK TRANSCRIPTION COMPLETE ==========");
        LoggingService.Info($"  Characters: {transcript.@text.Length}");
        LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
        return transcript.@text;
    }


    public static bool TryGetSupportedFormattingLanguageCode(string? code, out string supportedCode)
    {
        supportedCode = string.Empty;

        if (string.IsNullOrWhiteSpace(code) || code.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = NormalizeLanguageCode(code);
        if (LanguageAliases.TryGetValue(normalized, out var alias))
        {
            normalized = alias;
        }

        if (!SupportedFormattingLanguages.Contains(normalized))
        {
            return false;
        }

        supportedCode = normalized;
        return true;
    }

    private static string NormalizeLanguageCode(string code)
    {
        var trimmed = code.Trim();
        var dashIdx = trimmed.IndexOf('-');
        var normalized = dashIdx > 0 ? trimmed[..dashIdx] : trimmed;
        return normalized.ToLowerInvariant();
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
