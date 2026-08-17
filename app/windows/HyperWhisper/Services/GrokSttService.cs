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
// - keyterm: repeated once per vocabulary term (max 100 terms, 50 chars each)
//
// NOTE: No `model` parameter (single implicit model) and no free-text `prompt`
// parameter — vocabulary goes through `keyterm` instead.
//
// RESPONSE FORMAT: { "text": "transcribed text", "language": "...", "duration": ..., "words": [...] }
//
// LIMITS:
// - Max file size: 500 MB
// - Supported containers (auto-detected): wav, mp3, ogg, opus, flac, aac, mp4, m4a, mkv

using System.Diagnostics;
using System.Net.Http;
using HyperWhisper.Services.Transcription;
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

    private const long MaxFileSizeBytes = 500L * 1024 * 1024; // 500 MB

    // Per-attempt request timeout, scaled to file size (see GetRequestTimeout):
    // a fixed cap can't cover a 500 MB upload on a slow uplink.
    private static readonly TimeSpan BaseRequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(30);

    // Audio MIME types Grok accepts (containers auto-detected by API). A
    // different container set from the shared standard map, so Grok keeps its
    // own and passes it to TranscriptionPreflight.MimeTypeFor.
    private static readonly IReadOnlyDictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
            // No HttpClient-level cap: the request bound is the size-scaled
            // per-attempt timeout that TranscribeAsync below passes to
            // RustSingleShot.TranscribeAsync (GetRequestTimeout — 5 min base +
            // 3 s/MB, capped at 30 min). A fixed 300 s cap here killed large-file
            // uploads that legitimately need longer than 5 minutes to send.
            Timeout = Timeout.InfiniteTimeSpan
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

        // Validate configuration and audio file (shared gate).
        var fileInfo = TranscriptionPreflight.Validate("Grok", _apiKey, audioPath, MaxFileSizeBytes, "500 MB");

        if (vocabulary?.Count > 0)
        {
            LoggingService.Info($"  Vocabulary sent as keyterm fields (max 100 terms, 50 chars each)");
        }

        // Build the request via the Rust shared core, then drive it through the
        // shared executor + core retry loop. The core owns the language gating
        // (`language` + `format=true`), the keyterm cap and the multipart
        // assembly; Grok has no model, so only the vocabulary is passed on.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "application/octet-stream", MimeTypes);

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            apiKey: _apiKey);

        var requestTimeout = GetRequestTimeout(fileInfo.Length);
        LoggingService.Info($"  Request timeout: {requestTimeout.TotalMinutes:F1} minutes (per attempt)");

        return await RustSingleShot.TranscribeAsync(
            _httpClient,
            "Grok",
            buildRequest: () => HyperwhisperCoreMethods.GrokBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.GrokParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken,
            perAttemptTimeout: requestTimeout);
    }


    public static bool TryGetSupportedFormattingLanguageCode(string? code, out string supportedCode) =>
        XaiFormattingLanguages.TryGetSupportedCode(code, out supportedCode);

    // internal for SmokeTests.
    internal static TimeSpan GetRequestTimeout(long fileSizeBytes)
    {
        var fileSizeMb = fileSizeBytes / (1024.0 * 1024.0);

        // Budget extra time for large uploads instead of using a fixed timeout.
        var scaledTimeout = BaseRequestTimeout + TimeSpan.FromSeconds(fileSizeMb * 3);
        return scaledTimeout <= MaxRequestTimeout ? scaledTimeout : MaxRequestTimeout;
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
