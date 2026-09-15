// HYPERWHISPER CLOUD SERVICE
// Cloud transcription via HyperWhisper's built-in cloud transcription service.
// Routes to the selected backend STT provider with integrated credit management.
//
// API ENDPOINT: POST https://transcribe-prod-v2.hyperwhisper.com/transcribe
//
// REQUEST FORMAT: Binary streaming POST (raw audio)
// - Content-Type: audio/wav (or appropriate MIME type)
// - Query params: device_id OR license_key, language, mode, initial_prompt
//
// RESPONSE FORMAT: JSON with original and corrected text
// { "original": "...", "corrected": "..." }
//
// AUTHENTICATION:
// - Trial users: device_id query parameter (150 device credits)
// - Licensed users: license_key query parameter (Polar meter billing)
//
// RESPONSE HEADERS:
// - X-Credits-Used: Credits deducted for this request
// - X-Device-Credits-Remaining: Device balance (trial users)
// - X-IP-RateLimit-Remaining: IP quota remaining (trial users)
// - X-Total-Cost-Usd: Actual API cost
//
// ERROR CODES:
// - 401 Unauthorized: No identifier provided or invalid license
// - 402 Payment Required: Insufficient device credits
// - 429 Too Many Requests: IP rate limit exceeded
//
// NOTE: Does NOT require API key - uses device credits or license
//
// This file holds the class shape: state, the ITranscriptionProvider surface,
// construction and disposal. The rest is one partial file per responsibility:
// - HyperWhisperCloudService.Connection.cs     — HttpClient, warmup, DNS rebuild
// - HyperWhisperCloudService.Transcription.cs  — the POST /transcribe send path
// - HyperWhisperCloudService.Responses.cs      — credit headers + error mapping
// - HyperWhisperCloudService.PostProcessing.cs — the POST /post-process path

using System.Net.Http;
using System.Threading;
using HyperWhisper.Services.Transcription;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using HyperWhisper's built-in cloud service.
/// Uses device credits for trial users, license key for paid users.
/// </summary>
public partial class HyperWhisperCloudService : ITranscriptionProvider, ITranscriptionDiagnosticsSource, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 180; // 3 minutes for larger files
    // Retained for the native /post-process retry loop only. The transcribe path
    // now uses the core's RetryMaxAttempts() via RustRetry.
    private const int MaxRetries = 4; // Matches macOS implementation

    // =========================================================================
    // STATE
    // =========================================================================

    private HttpClient _httpClient;
    private bool _disposed;

    // Credit tracking
    private int? _lastCreditsUsed;
    private int? _remainingCredits;

    /// <summary>
    /// Provider diagnostics from the most recent transcription attempt.
    /// Cleared at the start of each request.
    /// </summary>
    public TranscriptionProviderDiagnostics? LastDiagnostics { get; private set; }

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Whether the service is ready. Always available since device ID is generated automatically.
    /// </summary>
    public bool IsAvailable => true;

    /// <summary>
    /// Display name for HyperWhisper Cloud.
    /// </summary>
    public string Name => "HyperWhisper Cloud";

    /// <summary>
    /// Gets the remaining device credits from the last request.
    /// Returns null if unknown or using license key.
    /// </summary>
    public int? RemainingCredits => _remainingCredits;

    /// <summary>
    /// Gets the credits used in the last request.
    /// </summary>
    public int? LastCreditsUsed => _lastCreditsUsed;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public HyperWhisperCloudService()
    {
        _httpClient = CreateHttpClient();
        LoggingService.Info("HyperWhisperCloudService: Initialized");
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configuration method retained for API compatibility.
    /// Credentials are now fetched fresh on each request from LicenseManager.
    /// </summary>
    public void Configure(string? licenseKey = null)
    {
        // No-op: credentials are fetched at request time to ensure
        // license deactivation is immediately reflected (matches macOS behavior)
        LoggingService.Debug("HyperWhisperCloudService: Configure called (credentials fetched at request time)");
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try { Volatile.Read(ref _httpClient)?.Dispose(); }
            catch (Exception ex) { LoggingService.Warn($"Dispose failed for HttpClient: {ex.Message}"); }
        }
        GC.SuppressFinalize(this);
    }
}
