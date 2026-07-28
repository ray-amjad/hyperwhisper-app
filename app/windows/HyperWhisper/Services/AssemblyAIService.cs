// ASSEMBLYAI SERVICE
// Cloud transcription via AssemblyAI's Speech-to-Text API.
// Uses a 3-step async workflow: upload -> create transcript -> poll for completion.
// Clips under the sync API's duration cap try a one-request sync fast path first
// (see SYNC FAST PATH below) and fall back to the async workflow on any failure.
//
// API WORKFLOW:
// 1. POST https://api.assemblyai.com/v2/upload (upload audio, get upload_url)
// 2. POST https://api.assemblyai.com/v2/transcript (create transcript job, get id)
// 3. GET https://api.assemblyai.com/v2/transcript/{id} (poll until status="completed")
//
// REQUEST FORMAT:
// - Upload: Binary POST with raw audio
// - Create: JSON with audio_url, speech_model, language_code, keyterms_prompt
//
// RESPONSE FORMAT:
// - Upload: { "upload_url": "..." }
// - Create: { "id": "...", "status": "queued" }
// - Poll: { "id": "...", "status": "completed|processing|error", "text": "..." }
//
// SYNC FAST PATH (clips < AssemblyaiSyncMaxDurationSecs(), currently 120s):
// - POST https://sync.assemblyai.com/v1/transcribe returns the finished
//   transcript in the SAME response (~134ms p50) — no upload_url, no job id,
//   no polling. Built/parsed by the Rust core (AssemblyaiBuildSyncRequest /
//   AssemblyaiParseSyncResponse); see hw-net's assemblyai.rs sync section for
//   the verified request/response contract.
// - Falls back to the 3-step async workflow above when: the exact NAudio
//   duration is unavailable, duration >= the sync cap, the sync HTTP call
//   errors/times out, or the response doesn't parse. A NoSpeech result is NOT
//   a fallback trigger — it's a legitimate terminal outcome, surfaced exactly
//   like the async poll path does.
//
// MODELS (as of 2026-04):
// - universal-2: Multi-language (99 languages), auto-detection, $0.15/hr (default)
// - universal-3-pro: 6 languages (EN/ES/DE/FR/PT/IT), highest accuracy, $0.21/hr
// Legacy "universal" / "slam-1" IDs are resolved via CloudTranscriptionModels.ResolveAssemblyAIModelAlias.
//
// VOCABULARY BOOSTING:
// - keyterms_prompt: Array of terms (max 6 words per phrase).
//   Caps: 200 for universal-2, 1000 for universal-3-pro.
// - The legacy word_boost/boost_param fields are deprecated by AssemblyAI on 2026-05-11.
//
// ERROR HANDLING:
// - 401: Invalid API key
// - 429: Rate limited
// - 400: Invalid request
// - Transcript error: status="error" with error message
//
// NOTE: Uses TranscriptionApiKeyType.AssemblyAI (separate from post-processing)

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HttpRequest / HttpResponse / HwTranscript /
// HwTranscriptionException / AssemblyaiPollOutcome collide with System types;
// qualify uniffi.hyperwhisper_core.* where ambiguous.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using AssemblyAI's async transcription API.
/// Three-step workflow: upload -> create transcript -> poll for completion.
/// </summary>
public class AssemblyAIService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const string ApiBaseUrl = "https://api.assemblyai.com/v2";
    private const int DefaultTimeoutSeconds = 30; // Per request timeout
    private const int MaxPollAttempts = 120; // 2 minutes max at 1s intervals
    private const int PollIntervalMs = 1000; // 1 second between polls
    private const int MaxRetries = 3;

    // Sync fast path: the AssemblyAI Python SDK keeps its own client timeout at
    // 60s "above the server's 30s deadline so the client doesn't race it" — we
    // use a tighter-but-still-safe margin above that same 30s server deadline so
    // a stalled sync call still falls back to async promptly.
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(40);

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

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private string _modelId = "universal-2";
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
    public string Name => $"AssemblyAI {CloudTranscriptionModels.GetById(_modelId, CloudTranscriptionProvider.AssemblyAI)?.DisplayName ?? _modelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public AssemblyAIService()
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
    /// <param name="apiKey">AssemblyAI API key.</param>
    /// <param name="modelId">Model ID (universal-2, universal-3-pro). Legacy IDs are canonicalized automatically.</param>
    public void Configure(string apiKey, string modelId = "universal-2")
    {
        _apiKey = apiKey;
        _modelId = CloudTranscriptionModels.ResolveAssemblyAIModelAlias(modelId);
        LoggingService.Info($"AssemblyAIService: Configured with model {_modelId}");
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using AssemblyAI's async API.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== ASSEMBLYAI CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {_modelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio path: {audioPath}");

        // STEP 1: Validate configuration
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "AssemblyAI API key not configured",
                "AssemblyAI");
        }

        // STEP 2: Validate audio file
        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "AssemblyAI");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        // STEP 3: Build core params (model/keyterms/language/domain owned by core).
        // Pass the RAW vocab list (keyterms_prompt is built + capped by the core).
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var extension = Path.GetExtension(audioPath);
        var contentType = MimeTypes.GetValueOrDefault(extension, "application/octet-stream");
        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            apiKey: _apiKey,
            model: _modelId);

        // STEP 3.5: Try the sync fast path for short clips. Uses the EXACT NAudio
        // duration (not a byte-size estimate) since we have the file on disk.
        // Unknown duration or >= the sync cap skips straight to the async workflow.
        var durationResult = FileTranscriptionService.GetAudioDuration(audioPath);
        var syncMaxDurationSeconds = HyperwhisperCoreMethods.AssemblyaiSyncMaxDurationSecs();
        if (IsSyncEligible(durationResult, syncMaxDurationSeconds))
        {
            LoggingService.Info($"  Duration {durationResult.Value:F1}s < {syncMaxDurationSeconds:F0}s sync cap — trying sync fast path");
            var syncText = await TryTranscribeSyncAsync(coreParams, durationResult.Value, cancellationToken);
            if (syncText != null)
            {
                LoggingService.Info("========== ASSEMBLYAI TRANSCRIPTION COMPLETE (sync) ==========");
                LoggingService.Info($"  Characters: {syncText.Length}");
                LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
                return syncText;
            }
            LoggingService.Info("  Sync fast path unavailable — falling back to async upload/create/poll");
        }
        else
        {
            var reason = durationResult.IsSuccess
                ? $"duration {durationResult.Value:F1}s >= {syncMaxDurationSeconds:F0}s sync cap"
                : $"duration unknown ({durationResult.Error})";
            LoggingService.Debug($"  Skipping sync fast path: {reason}");
        }

        // STEP 4: Upload (through retry) -> parse upload URL.
        LoggingService.Info("  Step 1: Uploading audio...");
        var uploadResp = await PerformAsync(
            () => HyperwhisperCoreMethods.AssemblyaiBuildUploadRequest(coreParams),
            resp => MapError(resp, "upload", r => HyperwhisperCoreMethods.AssemblyaiParseUploadResponse(r)),
            cancellationToken);
        var uploadUrl = ParseStep(() => HyperwhisperCoreMethods.AssemblyaiParseUploadResponse(uploadResp));
        LoggingService.Info("  Upload complete");

        // STEP 5: Create transcript job (through retry) -> parse id.
        LoggingService.Info("  Step 2: Creating transcript...");
        var createResp = await PerformAsync(
            () => HyperwhisperCoreMethods.AssemblyaiBuildCreateRequest(coreParams, uploadUrl),
            resp => MapError(resp, "create transcript", r => HyperwhisperCoreMethods.AssemblyaiParseCreateResponse(r)),
            cancellationToken);
        var transcriptId = ParseStep(() => HyperwhisperCoreMethods.AssemblyaiParseCreateResponse(createResp));
        LoggingService.Info($"  Transcript ID: {transcriptId}");

        // STEP 6: Poll for completion. The poll loop is driven natively and does
        // NOT go through the retry wrapper — the core build/parse is invoked per
        // poll, switching on AssemblyaiPollOutcome (.Pending -> sleep+continue;
        // .Done(transcript) -> return). Mirrors macOS: 120 attempts @ 1s.
        LoggingService.Info("  Step 3: Polling for completion...");
        var pollSw = Stopwatch.StartNew();
        for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uniffi.hyperwhisper_core.HttpResponse pollResp;
            try
            {
                var pollReq = HyperwhisperCoreMethods.AssemblyaiBuildPollRequest(coreParams, transcriptId);
                pollResp = await RustHttpExecutor.ExecuteAsync(pollReq, _httpClient, cancellationToken);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "AssemblyAI");
            }
            catch (HttpRequestException ex)
            {
                // Transient network error during poll — wait + retry the poll.
                LoggingService.Warn($"  Poll network error: {ex.Message}, retrying...");
                await Task.Delay(PollIntervalMs, cancellationToken);
                continue;
            }

            AssemblyaiPollOutcome outcome;
            try
            {
                outcome = HyperwhisperCoreMethods.AssemblyaiParsePollResponse(pollResp);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "AssemblyAI", (int)pollResp.@status);
            }

            if (outcome is AssemblyaiPollOutcome.Done done)
            {
                LoggingService.Info("========== ASSEMBLYAI TRANSCRIPTION COMPLETE ==========");
                LoggingService.Info($"  Characters: {done.@transcript.@text.Length}");
                LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
                return done.@transcript.@text;
            }

            // Pending — wait and poll again.
            LoggingService.Debug($"  Poll attempt {attempt + 1}: pending (elapsed: {pollSw.ElapsedMilliseconds}ms)");
            await Task.Delay(PollIntervalMs, cancellationToken);
        }

        throw new TranscriptionException(
            TranscriptionErrorCode.ProviderUnavailable,
            $"Transcription timed out after {MaxPollAttempts} seconds",
            "AssemblyAI");
    }

    /// <summary>
    /// Pure duration-gate decision for the sync fast path: eligible only when
    /// the duration probe succeeded AND the exact duration is strictly under
    /// the sync cap. An unknown duration (probe failure) or a duration at/over
    /// the cap both fall back to async — this must fail closed, never open,
    /// since a false "eligible" wastes a round-trip AssemblyAI will reject as
    /// too long (>= the cap) and a false "ineligible" only costs the (larger)
    /// async latency, never correctness.
    /// </summary>
    // internal for SmokeTests.
    internal static bool IsSyncEligible(Result<double> durationResult, double syncMaxDurationSeconds)
    {
        return durationResult.IsSuccess && durationResult.Value < syncMaxDurationSeconds;
    }

    /// <summary>
    /// Attempt AssemblyAI's sync transcription API (one blocking request — no
    /// upload/create/poll) for a clip already confirmed to be under the sync
    /// duration cap. Returns the transcript text on success, or <c>null</c> on
    /// any failure that should fall back to the async workflow (HTTP/transport
    /// error, non-2xx, malformed response, or a sync-specific timeout). Does
    /// NOT go through <see cref="RustRetry"/> — the sync product is meant to be
    /// a single fast call, and retrying a deterministic rejection (e.g. "too
    /// long") would just delay the async fallback.
    ///
    /// A genuine <see cref="OperationCanceledException"/> from the caller's
    /// <paramref name="cancellationToken"/> propagates uncaught (never treated
    /// as a fallback signal). A <c>NoSpeech</c> parse result is a legitimate
    /// terminal outcome — mirrors the async poll path by throwing the same
    /// mapped <see cref="TranscriptionException"/> instead of falling back.
    /// </summary>
    private async Task<string?> TryTranscribeSyncAsync(
        uniffi.hyperwhisper_core.TranscribeParams coreParams,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SyncTimeout);

        uniffi.hyperwhisper_core.HttpResponse response;
        try
        {
            var request = HyperwhisperCoreMethods.AssemblyaiBuildSyncRequest(coreParams);
            response = await RustHttpExecutor.ExecuteAsync(request, _httpClient, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own sync timeout fired, not caller cancellation.
            LoggingService.Warn($"  Sync transcription timed out after {SyncTimeout.TotalSeconds:F0}s ({durationSeconds:F1}s clip) — falling back to async");
            return null;
        }
        catch (HttpRequestException ex)
        {
            LoggingService.Warn($"  Sync transcription network error ({ex.Message}) — falling back to async");
            return null;
        }
        catch (HwTranscriptionException ex)
        {
            // Defense: ExecuteAsync itself never throws this, but a future
            // change funneling build errors through here shouldn't crash the
            // caller — treat it like any other sync failure.
            LoggingService.Warn($"  Sync transcription request build error ({ex.Message}) — falling back to async");
            return null;
        }

        try
        {
            var transcript = HyperwhisperCoreMethods.AssemblyaiParseSyncResponse(response);
            return transcript.@text;
        }
        catch (HwTranscriptionException ex) when (ex is HwTranscriptionException.NoSpeech)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "AssemblyAI", (int)response.@status);
        }
        catch (HwTranscriptionException ex)
        {
            LoggingService.Warn($"  Sync transcription failed ({ex.GetType().Name}: {ex.Message}) — falling back to async");
            return null;
        }
    }

    /// <summary>Run a build/RustRetry step, mapping a builder validation error.</summary>
    private async Task<uniffi.hyperwhisper_core.HttpResponse> PerformAsync(
        Func<uniffi.hyperwhisper_core.HttpRequest> buildRequest,
        Func<uniffi.hyperwhisper_core.HttpResponse, TranscriptionException> parseError,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RustRetry.PerformAsync(_httpClient, buildRequest, parseError, cancellationToken);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "AssemblyAI");
        }
    }

    /// <summary>Run a core parse step, mapping the classified error.</summary>
    private static string ParseStep(Func<string> parse)
    {
        try
        {
            return parse();
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "AssemblyAI");
        }
    }

    /// <summary>
    /// Map a non-2xx step response to a TranscriptionException (retry give-up).
    /// <paramref name="classify"/> must be the parser MATCHING the step (upload →
    /// AssemblyaiParseUploadResponse, create → AssemblyaiParseCreateResponse) —
    /// both throw the classified error via classify_http on any non-2xx. The poll
    /// parser is NOT interchangeable: it reads the transcript `status` field and
    /// would misclassify upload/create error bodies.
    /// </summary>
    // internal for SmokeTests.
    internal static TranscriptionException MapError(
        uniffi.hyperwhisper_core.HttpResponse resp,
        string operation,
        Action<uniffi.hyperwhisper_core.HttpResponse> classify)
    {
        try
        {
            classify(resp);
            // Defense: the step parsers throw on every non-2xx, so this is
            // unreachable for the responses the retry wrapper hands us.
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, $"Unexpected non-error response ({operation})", "AssemblyAI", (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            return RustCoreMapping.MapTranscriptionError(ex, "AssemblyAI", (int)resp.@status);
        }
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
