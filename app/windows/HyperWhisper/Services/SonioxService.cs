using System.Diagnostics;
using System.IO;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HttpRequest / HttpResponse / HwTranscript /
// HwTranscriptionException / SonioxPollStatus collide with System types; qualify
// uniffi.hyperwhisper_core.* where ambiguous.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using Soniox async/file transcription APIs.
/// Workflow: upload file -> create transcription -> poll -> fetch transcript -> best-effort delete.
/// </summary>
public class SonioxService : ITranscriptionProvider, IDisposable
{
    private const int DefaultTimeoutSeconds = 180;
    private const int MaxPollAttempts = 180;
    private const int PollIntervalMs = 1000;

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".aac", "audio/aac" },
        { ".aiff", "audio/aiff" },
        { ".amr", "audio/amr" },
        { ".asf", "audio/x-ms-asf" },
        { ".flac", "audio/flac" },
        { ".mp3", "audio/mpeg" },
        { ".ogg", "audio/ogg" },
        { ".wav", "audio/wav" },
        { ".webm", "audio/webm" },
        { ".m4a", "audio/mp4" },
        { ".mp4", "audio/mp4" }
    };

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private string _modelId = "stt-async-v4";
    private bool _disposed;

    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public string Name => $"Soniox {CloudTranscriptionModels.GetById(_modelId, CloudTranscriptionProvider.Soniox)?.DisplayName ?? _modelId}";

    public SonioxService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds)
        };
    }

    public void Configure(string apiKey, string modelId = "stt-async-v4")
    {
        _apiKey = apiKey?.Trim();
        _modelId = string.IsNullOrWhiteSpace(modelId) ? "stt-async-v4" : modelId;
        LoggingService.Info($"SonioxService: Configured with model {_modelId}");
    }

    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        LoggingService.Info("========== SONIOX CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {_modelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio path: {audioPath}");

        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                "Soniox API key not configured",
                "Soniox");
        }

        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "Soniox");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        var maxFileSize = CloudTranscriptionProvider.Soniox.GetMaxFileSizeBytes();
        if (fileInfo.Length > maxFileSize)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.FileTooLarge,
                $"File size ({fileInfo.Length / 1024.0 / 1024.0:F1} MB) exceeds {maxFileSize / 1024 / 1024 / 1024} GB limit",
                "Soniox");
        }

        // Build core params once. Pass the RAW vocab list (boost terms) — the core
        // builds the `context` CSV and gates `language_hints`. The model/auth are
        // baked by the per-step core builders.
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

        string? transcriptionId = null;
        string? fileId = null;
        try
        {
            // STEP 1: Upload (through retry) -> parse file id.
            LoggingService.Info("  Step 1: Uploading audio...");
            var uploadResp = await PerformAsync(
                () => HyperwhisperCoreMethods.SonioxBuildUploadRequest(coreParams),
                resp => MapError(resp, "upload"),
                cancellationToken);
            fileId = ParseStep(() => HyperwhisperCoreMethods.SonioxParseUploadResponse(uploadResp));

            // STEP 2: Create transcription (through retry) -> parse transcription id.
            LoggingService.Info("  Step 2: Creating transcription...");
            var createResp = await PerformAsync(
                () => HyperwhisperCoreMethods.SonioxBuildCreateRequest(coreParams, fileId),
                resp => MapError(resp, "create transcription"),
                cancellationToken);
            transcriptionId = ParseStep(() => HyperwhisperCoreMethods.SonioxParseCreateResponse(createResp));

            // STEP 3: Status poll loop (NO retry; per-poll core build/parse via the
            // executor directly). Switches on SonioxPollStatus. Mirrors macOS:
            // 180 attempts @ 1s. Transient 5xx/network during a poll → wait+retry.
            LoggingService.Info("  Step 3: Polling for completion...");
            await PollUntilCompleteAsync(coreParams, transcriptionId, cancellationToken);

            // STEP 4: Fetch transcript (through retry) -> parse text.
            LoggingService.Info("  Step 4: Fetching transcript...");
            var transcriptResp = await PerformAsync(
                () => HyperwhisperCoreMethods.SonioxBuildTranscriptRequest(coreParams, transcriptionId),
                resp => MapError(resp, "get transcript"),
                cancellationToken);
            HwTranscript transcript;
            try
            {
                transcript = HyperwhisperCoreMethods.SonioxParseTranscriptResponse(transcriptResp);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "Soniox", (int)transcriptResp.@status);
            }
            var text = transcript.@text;

            // STEP 5: Best-effort cleanup (fire-and-forget). The delete build fns
            // are non-throwing in the binding (RustCall, not RustCallWithError).
            FireDeleteTranscription(coreParams, transcriptionId);
            FireDeleteFile(coreParams, fileId);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new TranscriptionException(
                    TranscriptionErrorCode.NoSpeechDetected,
                    "No speech detected in audio",
                    "Soniox");
            }

            LoggingService.Info("========== SONIOX TRANSCRIPTION COMPLETE ==========");
            LoggingService.Info($"  Characters: {text.Length}");
            LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
            return text;
        }
        catch
        {
            // Cleanup on the failure path too — same fire-and-forget deletes.
            if (!string.IsNullOrWhiteSpace(transcriptionId))
            {
                FireDeleteTranscription(coreParams, transcriptionId);
            }
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                FireDeleteFile(coreParams, fileId);
            }
            throw;
        }
    }

    /// <summary>
    /// Poll the transcription status until SonioxPollStatus.Completed (or timeout).
    /// Direct executor calls (NOT through the retry wrapper). Transient network /
    /// 5xx errors during a poll are non-fatal — wait and retry the poll.
    /// </summary>
    private async Task PollUntilCompleteAsync(
        TranscribeParams coreParams,
        string transcriptionId,
        CancellationToken cancellationToken)
    {
        var pollSw = Stopwatch.StartNew();
        for (int attempt = 1; attempt <= MaxPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uniffi.hyperwhisper_core.HttpResponse pollResp;
            try
            {
                var pollReq = HyperwhisperCoreMethods.SonioxBuildStatusRequest(coreParams, transcriptionId);
                pollResp = await RustHttpExecutor.ExecuteAsync(pollReq, _httpClient, cancellationToken);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "Soniox");
            }
            catch (HttpRequestException ex)
            {
                LoggingService.Warn($"  Soniox poll network error on attempt {attempt}: {ex.Message}");
                await Task.Delay(PollIntervalMs, cancellationToken);
                continue;
            }

            // Transient server errors (5xx) during polling are non-fatal.
            if (pollResp.@status >= 500)
            {
                LoggingService.Warn($"  Soniox poll server error {pollResp.@status} on attempt {attempt}, retrying...");
                await Task.Delay(PollIntervalMs, cancellationToken);
                continue;
            }

            SonioxPollStatus status;
            try
            {
                status = HyperwhisperCoreMethods.SonioxParseStatusResponse(pollResp);
            }
            catch (HwTranscriptionException ex)
            {
                throw RustCoreMapping.MapTranscriptionError(ex, "Soniox", (int)pollResp.@status);
            }

            if (status == SonioxPollStatus.Completed)
            {
                return;
            }

            // Pending — wait and poll again.
            LoggingService.Debug($"  Soniox poll attempt {attempt}: pending (elapsed: {pollSw.ElapsedMilliseconds}ms)");
            await Task.Delay(PollIntervalMs, cancellationToken);
        }

        throw new TranscriptionException(
            TranscriptionErrorCode.NetworkError,
            "Soniox transcription polling timed out",
            "Soniox");
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
            throw RustCoreMapping.MapTranscriptionError(ex, "Soniox");
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
            throw RustCoreMapping.MapTranscriptionError(ex, "Soniox");
        }
    }

    /// <summary>Map a non-2xx step response to a TranscriptionException (retry give-up).</summary>
    private static TranscriptionException MapError(uniffi.hyperwhisper_core.HttpResponse resp, string operation)
    {
        try
        {
            // The status parser surfaces the same status/body-based classification
            // on a non-2xx; only the thrown error matters here.
            HyperwhisperCoreMethods.SonioxParseStatusResponse(resp);
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, $"Unexpected non-error response ({operation})", "Soniox", (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            return RustCoreMapping.MapTranscriptionError(ex, "Soniox", (int)resp.@status);
        }
    }

    /// <summary>
    /// Fire-and-forget delete of the transcription. The build fn is non-throwing
    /// (RustCall); execution errors are swallowed.
    /// </summary>
    private void FireDeleteTranscription(TranscribeParams coreParams, string transcriptionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var req = HyperwhisperCoreMethods.SonioxBuildDeleteTranscriptionRequest(coreParams, transcriptionId);
                await RustHttpExecutor.ExecuteAsync(req, _httpClient, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Soniox cleanup (transcription) failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Fire-and-forget delete of the uploaded file. The build fn is non-throwing
    /// (RustCall); execution errors are swallowed.
    /// </summary>
    private void FireDeleteFile(TranscribeParams coreParams, string fileId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var req = HyperwhisperCoreMethods.SonioxBuildDeleteFileRequest(coreParams, fileId);
                await RustHttpExecutor.ExecuteAsync(req, _httpClient, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Soniox file cleanup failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
