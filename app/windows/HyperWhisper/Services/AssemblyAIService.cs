// ASSEMBLYAI SERVICE
// Cloud transcription via AssemblyAI's Speech-to-Text API.
// Uses a 3-step async workflow: upload -> create transcript -> poll for completion.
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

        // STEP 4: Upload (through retry) -> parse upload URL.
        LoggingService.Info("  Step 1: Uploading audio...");
        var uploadResp = await PerformAsync(
            () => HyperwhisperCoreMethods.AssemblyaiBuildUploadRequest(coreParams),
            resp => MapError(resp, "upload"),
            cancellationToken);
        var uploadUrl = ParseStep(() => HyperwhisperCoreMethods.AssemblyaiParseUploadResponse(uploadResp));
        LoggingService.Info("  Upload complete");

        // STEP 5: Create transcript job (through retry) -> parse id.
        LoggingService.Info("  Step 2: Creating transcript...");
        var createResp = await PerformAsync(
            () => HyperwhisperCoreMethods.AssemblyaiBuildCreateRequest(coreParams, uploadUrl),
            resp => MapError(resp, "create transcript"),
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

    /// <summary>Map a non-2xx step response to a TranscriptionException (retry give-up).</summary>
    private static TranscriptionException MapError(uniffi.hyperwhisper_core.HttpResponse resp, string operation)
    {
        // Re-run the matching parser to obtain the classified error. The upload/
        // create parsers throw the classified HwTranscriptionException on non-2xx.
        try
        {
            // Use the poll parser as a generic classifier: it surfaces the same
            // status/body-based classification. (Any parser would do — only the
            // thrown error matters on a non-2xx.)
            HyperwhisperCoreMethods.AssemblyaiParsePollResponse(resp);
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
