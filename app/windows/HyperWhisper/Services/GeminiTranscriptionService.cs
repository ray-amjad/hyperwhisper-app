// GEMINI TRANSCRIPTION SERVICE
// Cloud transcription via Google Gemini generateContent + Files API.
//
// API WORKFLOW (Files API only — audio cannot cross the FFI inline, so the legacy
// inline-base64 generateContent path is removed; Files API is now used for all
// sizes):
// 1. POST https://generativelanguage.googleapis.com/upload/v1beta/files?key={apiKey}
//    Starts a resumable upload session and returns X-Goog-Upload-URL.
// 2. POST {uploadUrl}  — uploads raw audio bytes, finalizes the Gemini file.
// 3. GET  https://generativelanguage.googleapis.com/v1beta/{file.name}?key={apiKey}
//    Polls until the file state is ACTIVE.
// 4. POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}
//    Sends prompt text + file_data.file_uri.
// 5. DELETE https://generativelanguage.googleapis.com/v1beta/{file.name}?key={apiKey}
//    Best-effort cleanup after each attempt.
//
// AUTHENTICATION: API key as query parameter (?key=), shared with Gemini post-processing.
// NOTE: Supports custom vocabulary + a user-defined custom prompt (geminiCustomPrompt
//       on Mode) — both are folded into the prompt by the Rust core.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HttpRequest / HttpResponse / HwTranscript /
// HwTranscriptionException / GeminiFile / GeminiFilePollOutcome collide with
// System types; qualify uniffi.hyperwhisper_core.* where ambiguous.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using Google Gemini generateContent + Files API.
/// Uses multimodal audio understanding (not a dedicated STT endpoint).
/// </summary>
public class GeminiTranscriptionService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 300;
    private const int FilePollIntervalMs = 300;
    private const int MaxFilePollAttempts = 500;

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
        { ".opus", "audio/ogg" },
        { ".flac", "audio/flac" },
        { ".aac", "audio/aac" },
        { ".aiff", "audio/aiff" }
    };

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private string _modelId = "gemini-2.5-flash";
    private string? _customPrompt;
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
    public string Name => $"Gemini {CloudTranscriptionModels.GetById(_modelId, CloudTranscriptionProvider.Gemini)?.DisplayName ?? _modelId}";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public GeminiTranscriptionService()
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
    /// <param name="apiKey">Google Gemini API key.</param>
    /// <param name="modelId">Model ID (e.g., gemini-2.5-flash).</param>
    public void Configure(string apiKey, string modelId = "gemini-2.5-flash")
    {
        _apiKey = apiKey?.Trim();
        _modelId = modelId;
        LoggingService.Info($"GeminiTranscriptionService: Configured with model {modelId}");
    }

    /// <summary>
    /// Sets the optional custom transcription prompt.
    /// Called by the orchestrator before transcription.
    /// </summary>
    public void SetCustomPrompt(string? customPrompt)
    {
        _customPrompt = string.IsNullOrWhiteSpace(customPrompt) ? null : customPrompt.Trim();
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using the Gemini Files API (upload -> poll ACTIVE ->
    /// generateContent -> delete). All request building / response parsing / prompt
    /// assembly is owned by the Rust shared core.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== GEMINI CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {_modelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Audio path: {audioPath}");
        LoggingService.Info($"  Custom prompt: {(_customPrompt != null ? "yes" : "no")}");

        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "Gemini API key not configured",
                "Gemini");
        }

        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "Gemini");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        // Build core params once. Pass ALL raw vocab terms — the core's prompt
        // builder folds base + language hint + vocabulary + custom prompt into the
        // generateContent request. The custom prompt rides in `prompt`.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var extension = Path.GetExtension(audioPath);
        var contentType = MimeTypes.GetValueOrDefault(extension, "audio/wav");
        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            apiKey: _apiKey,
            model: _modelId,
            prompt: _customPrompt);

        GeminiFile? uploadedFile = null;
        try
        {
            // STEP 1: Start the resumable upload (through retry) -> parse upload URL.
            LoggingService.Info("  Step 1: Starting Gemini resumable upload...");
            var startResp = await PerformAsync(
                () =>
                {
                    var request = HyperwhisperCoreMethods.GeminiBuildUploadStartRequest(coreParams);
                    // The core's upload-start builder intentionally omits
                    // X-Goog-Upload-Header-Content-Length (only the platform can stat
                    // the file across FFI). Append it from the size we already stat'd,
                    // re-applied each retry attempt — mirrors macOS
                    // GeminiTranscriptionProvider.
                    request.@headers.Add(new Header(
                        "X-Goog-Upload-Header-Content-Length",
                        fileInfo.Length.ToString()));
                    return request;
                },
                resp => MapError(resp, "start upload"),
                cancellationToken);
            var uploadUrl = ParseStringStep(() => HyperwhisperCoreMethods.GeminiParseUploadStartResponse(startResp));

            // STEP 2: Upload audio bytes (through retry) -> parse GeminiFile.
            LoggingService.Info("  Step 2: Uploading Gemini audio bytes...");
            var uploadResp = await PerformAsync(
                () => HyperwhisperCoreMethods.GeminiBuildUploadBytesRequest(coreParams, uploadUrl),
                resp => MapError(resp, "upload audio"),
                cancellationToken);
            uploadedFile = ParseFileStep(() => HyperwhisperCoreMethods.GeminiParseUploadBytesResponse(uploadResp));

            // STEP 3: Poll until the file is ACTIVE (NO retry; per-poll core
            // build/parse via the executor directly). Short-circuit if already
            // ACTIVE. Mirrors macOS: 500 attempts @ 300ms.
            var activeFile = await WaitForFileActiveAsync(coreParams, uploadedFile, cancellationToken);

            // STEP 4: Generate the transcript (through retry) -> parse text.
            LoggingService.Info($"  Step 3: Requesting Gemini transcript for {activeFile.@name} via Files API...");
            var generateResp = await PerformAsync(
                () => HyperwhisperCoreMethods.GeminiBuildGenerateRequest(coreParams, activeFile),
                resp => MapError(resp, "generate content"),
                cancellationToken);
            HwTranscript transcript;
            try
            {
                transcript = HyperwhisperCoreMethods.GeminiParseGenerateResponse(generateResp);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "Gemini", (int)generateResp.@status);
            }

            LoggingService.Info("========== GEMINI TRANSCRIPTION COMPLETE ==========");
            LoggingService.Info($"  Characters: {transcript.@text.Length}");
            LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
            return transcript.@text;
        }
        finally
        {
            // STEP 5: Best-effort cleanup (fire-and-forget; success + failure).
            // The delete build fn DOES throw on validation, so guard it inside the
            // detached task.
            var fileName = uploadedFile?.@name;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                FireDeleteFile(coreParams, fileName!);
            }
        }
    }

    /// <summary>
    /// Poll until the uploaded file reaches the ACTIVE state. Direct executor calls
    /// (NOT through the retry wrapper), switching on GeminiFilePollOutcome.
    /// Short-circuits if the upload response already reported ACTIVE.
    /// </summary>
    private async Task<GeminiFile> WaitForFileActiveAsync(
        TranscribeParams coreParams,
        GeminiFile uploadedFile,
        CancellationToken cancellationToken)
    {
        // Short-circuit: already ACTIVE.
        if (string.Equals(uploadedFile.@state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return uploadedFile;
        }

        var fileName = uploadedFile.@name;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.InvalidRequest,
                "Gemini upload returned a file with no name",
                "Gemini");
        }

        for (int attempt = 1; attempt <= MaxFilePollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LoggingService.Info($"  Waiting for Gemini file activation... attempt={attempt}/{MaxFilePollAttempts}");
            await Task.Delay(FilePollIntervalMs, cancellationToken);

            uniffi.hyperwhisper_core.HttpResponse pollResp;
            try
            {
                var pollReq = HyperwhisperCoreMethods.GeminiBuildPollRequest(coreParams, fileName!);
                pollResp = await RustHttpExecutor.ExecuteAsync(pollReq, _httpClient, cancellationToken);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "Gemini");
            }
            catch (HttpRequestException ex)
            {
                // Transient network error during poll — wait + retry the poll.
                LoggingService.Warn($"  Gemini poll network error: {ex.Message}, retrying...");
                continue;
            }

            GeminiFilePollOutcome outcome;
            try
            {
                outcome = HyperwhisperCoreMethods.GeminiParsePollResponse(pollResp);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "Gemini", (int)pollResp.@status);
            }

            if (outcome is GeminiFilePollOutcome.Active active)
            {
                return active.@file;
            }
            // Pending — loop (the delay runs at the top of the next iteration).
        }

        throw new TranscriptionException(
            TranscriptionErrorCode.ProviderUnavailable,
            "Timed out waiting for Gemini file processing",
            "Gemini");
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
            throw RustCoreMapping.MapTranscriptionError(ex, "Gemini");
        }
    }

    /// <summary>Run a core string-parse step, mapping the classified error.</summary>
    private static string ParseStringStep(Func<string> parse)
    {
        try
        {
            return parse();
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "Gemini");
        }
    }

    /// <summary>Run a core GeminiFile-parse step, mapping the classified error.</summary>
    private static GeminiFile ParseFileStep(Func<GeminiFile> parse)
    {
        try
        {
            return parse();
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "Gemini");
        }
    }

    /// <summary>Map a non-2xx step response to a TranscriptionException (retry give-up).</summary>
    private static TranscriptionException MapError(uniffi.hyperwhisper_core.HttpResponse resp, string operation)
    {
        try
        {
            // The generate parser surfaces the same status/body-based classification
            // on a non-2xx; only the thrown error matters here.
            HyperwhisperCoreMethods.GeminiParseGenerateResponse(resp);
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, $"Unexpected non-error response ({operation})", "Gemini", (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            return RustCoreMapping.MapTranscriptionError(ex, "Gemini", (int)resp.@status);
        }
    }

    /// <summary>
    /// Fire-and-forget delete of the uploaded Gemini file. The build fn throws on
    /// validation (RustCallWithError), so it is guarded inside the detached task.
    /// </summary>
    private void FireDeleteFile(TranscribeParams coreParams, string fileName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var req = HyperwhisperCoreMethods.GeminiBuildDeleteRequest(coreParams, fileName);
                await RustHttpExecutor.ExecuteAsync(req, _httpClient, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Gemini cleanup failed for {fileName}: {ex.Message}");
            }
        });
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
