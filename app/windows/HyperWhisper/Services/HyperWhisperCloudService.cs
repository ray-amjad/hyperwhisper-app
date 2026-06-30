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

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using HyperWhisper.Configuration;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HwTranscript / HwTranscriptionException / HttpResponse
// collide with System types; qualify uniffi.hyperwhisper_core.HttpResponse below.
using uniffi.hyperwhisper_core;
using HttpMethod = System.Net.Http.HttpMethod;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using HyperWhisper's built-in cloud service.
/// Uses device credits for trial users, license key for paid users.
/// </summary>
public class HyperWhisperCloudService : ITranscriptionProvider, ITranscriptionDiagnosticsSource, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 180; // 3 minutes for larger files
    // Retained for the native /post-process retry loop only. The transcribe path
    // now uses the core's RetryMaxAttempts() via RustRetry.
    private const int MaxRetries = 4; // Matches macOS implementation

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

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
        };
    }

    // HTTP version preference applied per-request. We prefer HTTP/2 so the
    // keepalive HEAD /warmup and a concurrent POST /transcribe multiplex as
    // parallel streams on the same pooled connection instead of queuing
    // (H1.1) or opening a second connection and paying another TCP+TLS
    // handshake. Fly's edge supports H2 via ALPN; RequestVersionOrLower
    // means we gracefully fall back to H1.1 if the server drops it.
    //
    // Why per-request and not `HttpClient.DefaultRequestVersion`:
    // `SendAsync(HttpRequestMessage)` does NOT honor the client-level
    // defaults — the HttpRequestMessage's own `Version` / `VersionPolicy`
    // wins. Setting it per-request is the only reliable way to opt in.
    // (macOS URLSession auto-negotiates H2 — no equivalent opt-in.)
    private static readonly Version PreferredHttpVersion = HttpVersion.Version20;
    private const HttpVersionPolicy PreferredVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
        => new HttpRequestMessage(method, url)
        {
            Version = PreferredHttpVersion,
            VersionPolicy = PreferredVersionPolicy,
        };

    // =========================================================================
    // CONNECTION PRE-WARM
    // =========================================================================

    private DateTime _lastWarmupAt = DateTime.MinValue;
    private static readonly TimeSpan WarmupMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fires a HEAD /warmup to pre-establish the TLS/HTTP2 connection to Fly.
    /// Call on hotkey-down paths so the handshake runs in parallel with the user
    /// starting to speak. Fire-and-forget — never throws, never blocks. Routes
    /// through the same <see cref="_httpClient"/> as /transcribe so the pooled
    /// connection is reused for the subsequent POST.
    /// </summary>
    public void PrewarmConnection()
    {
        if (DateTime.UtcNow - _lastWarmupAt < WarmupMinInterval)
            return;

        SendWarmup();
    }

    /// <summary>
    /// Bypasses the 60s warmup debounce. Used by the foreground keepalive
    /// ticker, which fires on its own ~45s cadence and would otherwise be
    /// absorbed into the debounce and throttled back to 60s — defeating the
    /// purpose of ticking faster than SocketsHttpHandler's pool-idle window.
    /// </summary>
    public void PrewarmConnectionForced()
    {
        SendWarmup();
    }

    private void SendWarmup()
    {
        _lastWarmupAt = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            // Snapshot the current client so a concurrent rebuild (DNS recovery)
            // can't dispose it out from under us mid-flight.
            var client = Volatile.Read(ref _httpClient);
            try
            {
                var url = NetworkConfig.HyperWhisperCloudBaseUrl + "/warmup";
                using var req = CreateRequest(HttpMethod.Head, url);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var region = response.Headers.TryGetValues("fly-region", out var vals)
                    ? string.Join(",", vals) : "?";
                LoggingService.Debug($"Cloud warmup ok · status={(int)response.StatusCode} · region={region} · httpVersion={response.Version}");
            }
            catch (Exception ex)
            {
                // Failed attempt shouldn't burn the debounce window — clear so the next hotkey retries.
                _lastWarmupAt = DateTime.MinValue;
                LoggingService.Debug($"Cloud warmup failed · {ex.Message}");
                if (IsDnsError(ex))
                {
                    RebuildHttpClient();
                }
            }
        });
    }

    /// <summary>
    /// True for errors that look like a stale/poisoned DNS cache — typical
    /// after a network flip (captive portal, VPN toggle, tether swap). Used
    /// to gate a one-shot HttpClient rebuild in the warmup callback and the
    /// transcribe retry.
    /// </summary>
    private static bool IsDnsError(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is SocketException se &&
                (se.SocketErrorCode == SocketError.HostNotFound ||
                 se.SocketErrorCode == SocketError.TryAgain))
            {
                return true;
            }
        }
        return false;
    }

    private DateTime _lastRebuildAt = DateTime.MinValue;
    private static readonly TimeSpan MinRebuildInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Atomically swaps in a fresh <see cref="HttpClient"/> (and the
    /// underlying <see cref="SocketsHttpHandler"/>) so the next request
    /// re-resolves DNS and reopens TCP/TLS. Reactive: only called when an
    /// error looks DNS-shaped, so the cost is paid only when it would help.
    ///
    /// DOES NOT dispose the old client. In-flight sends that already
    /// snapshotted the old reference (via Volatile.Read) would otherwise see
    /// ObjectDisposedException when the handler's socket is yanked, which
    /// the HttpRequestException-only catches above would miss — killing a
    /// user's active transcription instead of recovering it. The old client
    /// is released for GC; its SocketsHttpHandler drains via
    /// PooledConnectionIdleTimeout (5 min) in the background.
    ///
    /// Gated by:
    ///   - _disposed: prevents resurrecting a torn-down service via a
    ///     fire-and-forget warmup completing post-Dispose.
    ///   - MinRebuildInterval: coarse cross-call gate so a flapping network
    ///     or an unaware caller (warmup + transcribe both seeing DNS errors
    ///     back-to-back) can't churn the pool.
    /// </summary>
    private void RebuildHttpClient()
    {
        if (_disposed) return;
        if (DateTime.UtcNow - _lastRebuildAt < MinRebuildInterval) return;
        _lastRebuildAt = DateTime.UtcNow;

        var fresh = CreateHttpClient();
        Interlocked.Exchange(ref _httpClient, fresh);
        LoggingService.Info("HyperWhisperCloudService: HttpClient rebuilt (DNS recovery)");
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
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using HyperWhisper Cloud.
    /// Implements ITranscriptionProvider interface with default accuracy tier (Deepgram Nova-3).
    /// </summary>
    public Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        // Use default accuracy tier (Deepgram Nova-3)
        return TranscribeAsync(audioPath, language, vocabulary, cloudAccuracyTier: null,
            cloudTranscriptionModel: null, cloudTranscriptionDomain: null, cancellationToken);
    }

    /// <summary>
    /// Transcribes audio using HyperWhisper Cloud with accuracy tier selection.
    /// </summary>
    /// <param name="audioPath">Path to the audio file.</param>
    /// <param name="language">Language code or "auto" for auto-detect.</param>
    /// <param name="vocabulary">Custom vocabulary terms for better accuracy.</param>
    /// <param name="cloudAccuracyTier">Accuracy route (X-STT-Provider): catalog tier id e.g. "deepgramNova3" (default), "groqWhisper", "elevenLabsScribeV2", "grokStt". Legacy tier labels are also accepted.</param>
    /// <param name="cloudTranscriptionModel">Per-tier model id (X-STT-Model). Empty/null → backend uses the provider default.</param>
    /// <param name="cloudTranscriptionDomain">Domain (X-STT-Domain), e.g. "medical". Null → no domain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language,
        IReadOnlyList<string>? vocabulary,
        string? cloudAccuracyTier,
        string? cloudTranscriptionModel,
        string? cloudTranscriptionDomain,
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        LastDiagnostics = null;

        // Parse accuracy tier (defaults to Deepgram Nova-3)
        var accuracyTier = CloudAccuracyTierExtensions.FromString(cloudAccuracyTier);

        // Resolve the model: an empty/null stored model — OR a stale value that
        // doesn't belong to this tier (the field is shared with the BYOK path,
        // so a mode can carry e.g. "whisper-1") — means "use the catalog default
        // for this tier". Validating the id keeps the X-STT-Model header
        // consistent with the picker and avoids a backend 400 on a mismatched
        // model. Falls back to empty (backend default) when the catalog has no
        // models for the tier.
        var tierStorageId = accuracyTier.ToStorageValue();
        var modelBelongsToTier = !string.IsNullOrEmpty(cloudTranscriptionModel)
            && Services.AppClassification.CloudSttCatalog.Shared.GetModel(tierStorageId, cloudTranscriptionModel) != null;
        var resolvedModel = modelBelongsToTier
            ? cloudTranscriptionModel!
            : (Services.AppClassification.CloudSttCatalog.Shared.DefaultModelIdForId(tierStorageId) ?? "");

        var domain = string.IsNullOrEmpty(cloudTranscriptionDomain) ? null : cloudTranscriptionDomain;

        // Get fresh credentials at request time (matches macOS behavior)
        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();

        LoggingService.Info("========== HYPERWHISPER CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Auth: {(isLicensed ? "License Key" : "Device Credits")}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Accuracy tier: {accuracyTier} ({accuracyTier.ToSttProvider()})");
        LoggingService.Info($"  Model: {(string.IsNullOrEmpty(resolvedModel) ? "(provider default)" : resolvedModel)}");
        LoggingService.Info($"  Domain: {domain ?? "(none)"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio path: {audioPath}");

        // STEP 1: Validate audio file
        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "HyperWhisper Cloud");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        // STEP 2: Build the request via the Rust shared core and drive it through
        // the shared executor + core retry loop. The core builds the URL + query
        // (license_key/device_id, language, initial_prompt), the X-STT-* routed
        // headers (from routedProvider/Model/Domain), the Content-Type, and the
        // @raw raw-stream body. We pass the RAW vocab list — the core builds the
        // CSV (trim + drop-empty, no lowercase/dedup) AND owns the per-model
        // customVocabulary gating, so the native catalog gating is dropped.
        // KEEP native: credit-header extraction, no-speech diagnostics, the DNS
        // HttpClient rebuild (via onTransportError), and the /post-process path.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var extension = Path.GetExtension(audioPath);
        var contentType = MimeTypes.GetValueOrDefault(extension, "audio/wav");

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            // Core appends `/transcribe` itself — pass the BASE, not the endpoint.
            baseUrl: NetworkConfig.HyperWhisperCloudBaseUrl,
            licenseKey: isLicensed ? identifier : null,
            deviceId: isLicensed ? null : identifier,
            routedProvider: accuracyTier.ToSttProvider(),
            routedModel: string.IsNullOrEmpty(resolvedModel) ? null : resolvedModel,
            routedDomain: domain);

        var rebuiltThisSequence = false;

        uniffi.hyperwhisper_core.HttpResponse response;
        try
        {
            response = await RustRetry.PerformAsync(
                Volatile.Read(ref _httpClient),
                buildRequest: () => HyperwhisperCoreMethods.HyperwhisperCloudBuildTranscribeRequest(coreParams),
                parseError: MapCloudError,
                cancellationToken: cancellationToken,
                onTransportError: ex =>
                {
                    // One-shot HttpClient rebuild per retry sequence: a DNS-shaped
                    // error (network flip → stale cache) drops the pool so the next
                    // attempt re-resolves. Gated to one rebuild per sequence.
                    if (!rebuiltThisSequence && IsDnsError(ex))
                    {
                        RebuildHttpClient();
                        rebuiltThisSequence = true;
                    }
                    return Task.CompletedTask;
                });
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "HyperWhisper Cloud");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Credit balances + routed diagnostics come from the captured response
        // headers; the core's Transcript doesn't carry them. (Read once here on
        // the final response — error responses are handled by the retry wrapper.)
        ExtractCreditHeaders(response);
        var requestId = HeaderValue(response, "X-Request-ID");
        var sttProvider = HeaderValue(response, "X-STT-Provider");

        HwTranscript transcript;
        try
        {
            transcript = HyperwhisperCoreMethods.HyperwhisperCloudParseTranscribeResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            // 200-but-no-speech surfaces here as a NoSpeech error.
            LastDiagnostics = new TranscriptionProviderDiagnostics(
                Name, requestId, sttProvider,
                BackendNoSpeechDetected: ex is HwTranscriptionException.NoSpeech,
                (int)response.@status, totalSw.ElapsedMilliseconds, false);
            throw RustCoreMapping.MapTranscriptionError(ex, "HyperWhisper Cloud");
        }

        LastDiagnostics = new TranscriptionProviderDiagnostics(
            Name, requestId, sttProvider, false, (int)response.@status,
            totalSw.ElapsedMilliseconds,
            string.IsNullOrWhiteSpace(transcript.@text));

        LoggingService.Info("========== HYPERWHISPER CLOUD COMPLETE ==========");
        LoggingService.Info($"  Characters: {transcript.@text.Length}");
        LoggingService.Info($"  Credits used: {_lastCreditsUsed}");
        LoggingService.Info($"  Credits remaining: {_remainingCredits}");
        LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
        return transcript.@text;
    }

    /// <summary>
    /// Map a non-2xx HW-Cloud response into a TranscriptionException, enriching the
    /// 402 credit context + 413 size context from the body. Called by the retry
    /// wrapper on give-up.
    /// </summary>
    private static TranscriptionException MapCloudError(uniffi.hyperwhisper_core.HttpResponse resp)
    {
        try
        {
            HyperwhisperCoreMethods.HyperwhisperCloudParseTranscribeResponse(resp);
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, "Unexpected non-error response", "HyperWhisper Cloud", (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            var (remaining, required) = RustCoreMapping.CreditContext(resp);
            var (tooBigBytes, tooBigLimit) = RustCoreMapping.FileTooLargeContext(resp);
            return RustCoreMapping.MapTranscriptionError(
                ex,
                "HyperWhisper Cloud",
                httpStatusCode: (int)resp.@status,
                insufficientCredits: resp.@status == 402,
                creditsRemaining: remaining,
                creditsRequired: required,
                fileTooLargeBytes: tooBigBytes,
                fileTooLargeLimit: tooBigLimit);
        }
    }

    /// <summary>Read a single header value from a captured binding response.</summary>
    private static string? HeaderValue(uniffi.hyperwhisper_core.HttpResponse response, string headerName)
    {
        foreach (var header in response.@headers)
        {
            if (string.Equals(header.@name, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.@value;
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts credit information from the captured binding response headers
    /// (transcribe path — response captured by the executor).
    /// </summary>
    private void ExtractCreditHeaders(uniffi.hyperwhisper_core.HttpResponse response)
    {
        var used = HeaderValue(response, "X-Credits-Used");
        if (int.TryParse(used, out var usedVal))
        {
            _lastCreditsUsed = usedVal;
        }

        var remaining = HeaderValue(response, "X-Device-Credits-Remaining");
        if (int.TryParse(remaining, out var remainingVal))
        {
            _remainingCredits = remainingVal;
        }
    }

    /// <summary>
    /// Extracts credit information from a raw <see cref="HttpResponseMessage"/>
    /// (post-process path — still native HttpClient I/O, kept out of the core).
    /// </summary>
    private void ExtractCreditHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Credits-Used", out var usedValues)
            && int.TryParse(usedValues.FirstOrDefault(), out var used))
        {
            _lastCreditsUsed = used;
        }

        if (response.Headers.TryGetValues("X-Device-Credits-Remaining", out var remainingValues)
            && int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
        {
            _remainingCredits = remaining;
        }
    }

    /// <summary>
    /// Handles error responses from HyperWhisper Cloud API.
    /// </summary>
    private async Task HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        LoggingService.Error($"HyperWhisper Cloud API error: {statusCode} · httpVersion={response.Version}");
        LoggingService.Error($"  Response: {responseBody}");

        // Extract credit info even from error responses
        ExtractCreditHeaders(response);

        // Try to parse error message
        string? errorMessage = null;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                errorMessage = errorElement.GetString();
            }
            else if (doc.RootElement.TryGetProperty("message", out var msgElement))
            {
                errorMessage = msgElement.GetString();
            }
        }
        catch { }

        // Map status code to error type
        // HyperWhisper Cloud specific codes: 401 (no auth), 402 (no credits), 429 (rate limit)
        var (code, message) = (HttpStatusCode)statusCode switch
        {
            HttpStatusCode.Unauthorized => (
                TranscriptionErrorCode.Unauthorized,
                errorMessage ?? "No device ID or invalid license key"),

            (HttpStatusCode)402 => (
                TranscriptionErrorCode.QuotaExceeded,
                errorMessage ?? $"Insufficient credits. Remaining: {_remainingCredits ?? 0}"),

            HttpStatusCode.TooManyRequests => (
                TranscriptionErrorCode.RateLimited,
                errorMessage ?? "IP rate limit exceeded. Try again later."),

            HttpStatusCode.BadRequest => (
                TranscriptionErrorCode.InvalidRequest,
                errorMessage ?? "Invalid request"),

            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout => (
                TranscriptionErrorCode.ProviderUnavailable,
                errorMessage ?? "HyperWhisper Cloud service unavailable"),

            _ => (TranscriptionErrorCode.Unknown, errorMessage ?? $"HTTP {statusCode}")
        };

        // Get retry-after header if present
        int? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var retryValues))
        {
            if (int.TryParse(retryValues.FirstOrDefault(), out var seconds))
            {
                retryAfter = seconds;
            }
        }

        throw new TranscriptionException(code, message, "HyperWhisper Cloud", statusCode, retryAfter);
    }

    // =========================================================================
    // POST-PROCESSING
    // =========================================================================

    /// <summary>
    /// Calls the /post-process endpoint for AI text correction.
    /// This is a standalone endpoint separate from transcription.
    /// Matches macOS implementation in HyperWhisperCloudProvider.performPostProcess().
    /// </summary>
    /// <param name="text">Raw transcription text to correct.</param>
    /// <param name="prompt">System prompt for AI processing instructions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>AI-corrected text.</returns>
    /// <exception cref="TranscriptionException">Thrown on API errors.</exception>
    public async Task<string> PostProcessAsync(
        string text,
        string prompt,
        string? llmProviderHeader = null,
        string? llmModelHeader = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Get fresh credentials at request time (matches macOS behavior)
        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();

        LoggingService.Info("========== HYPERWHISPER CLOUD POST-PROCESS ==========");
        LoggingService.Info($"  Auth: {(isLicensed ? "License Key" : "Device Credits")}");
        LoggingService.Info($"  Text length: {text.Length} chars");
        LoggingService.Debug($"  Prompt length: {prompt.Length} chars");

        // Build JSON body with fresh credentials
        var body = new Dictionary<string, string>
        {
            ["text"] = text,
            ["prompt"] = prompt
        };

        // Add authentication using fresh credentials
        if (isLicensed)
        {
            body["license_key"] = identifier;
        }
        else
        {
            body["device_id"] = identifier;
        }

        var jsonBody = JsonSerializer.Serialize(body);

        // Send request with retry logic
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                LoggingService.Info($"  Attempt {attempt}/{MaxRetries}...");

                using var request = CreateRequest(HttpMethod.Post, NetworkConfig.PostProcessEndpoint);
                // Fresh content per attempt: `using var request` disposes its Content,
                // so a shared instance would be disposed after attempt 1 and the next
                // SendAsync would throw ObjectDisposedException — killing retries.
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                if (!string.IsNullOrEmpty(llmProviderHeader))
                {
                    request.Headers.TryAddWithoutValidation("X-LLM-Provider", llmProviderHeader);
                }
                if (!string.IsNullOrEmpty(llmModelHeader))
                {
                    request.Headers.TryAddWithoutValidation("X-LLM-Model", llmModelHeader);
                }

                // Snapshot the current client so a concurrent rebuild can't dispose it mid-flight.
                var client = Volatile.Read(ref _httpClient);
                var response = await client.SendAsync(request, cancellationToken);
                LoggingService.Debug($"  Post-process response: status={(int)response.StatusCode} · httpVersion={response.Version}");

                if (!response.IsSuccessStatusCode)
                {
                    await HandleErrorResponseAsync(response, cancellationToken);
                }

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(responseJson);

                // Extract corrected text
                if (doc.RootElement.TryGetProperty("corrected", out var correctedElement))
                {
                    var corrected = correctedElement.GetString();

                    // Log cost if present
                    if (doc.RootElement.TryGetProperty("cost", out var costElement))
                    {
                        if (costElement.TryGetProperty("credits", out var creditsEl))
                        {
                            LoggingService.Info($"  Post-process credits used: {creditsEl.GetDouble():F1}");
                        }
                    }

                    LoggingService.Info("========== POST-PROCESS COMPLETE ==========");
                    LoggingService.Info($"  Output length: {corrected?.Length ?? 0} chars");

                    return corrected ?? text;
                }

                throw new TranscriptionException(
                    TranscriptionErrorCode.InvalidRequest,
                    "Invalid response format from post-process endpoint",
                    "HyperWhisper Cloud");
            }
            catch (TranscriptionException ex) when (ex.Code == TranscriptionErrorCode.RateLimited && attempt < MaxRetries)
            {
                var delay = ex.RetryAfterSeconds ?? (int)Math.Pow(2, attempt);
                LoggingService.Warn($"  Rate limited, waiting {delay}s before retry...");
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                lastException = ex;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                var delay = (int)Math.Pow(2, attempt);
                LoggingService.Warn($"  Network error: {ex.Message}, retrying in {delay}s...");
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                lastException = ex;
            }
        }

        throw lastException ?? new TranscriptionException(
            TranscriptionErrorCode.Unknown,
            "Post-processing failed after max retries",
            "HyperWhisper Cloud");
    }

    /// <summary>
    /// Masks the URL to hide sensitive query params.
    /// </summary>
    private static string MaskUrl(string url)
    {
        // Replace device_id and license_key values with ***
        var masked = System.Text.RegularExpressions.Regex.Replace(
            url,
            @"(device_id|license_key)=[^&]+",
            "$1=***");
        return masked;
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
