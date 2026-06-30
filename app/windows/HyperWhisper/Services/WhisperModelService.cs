using System.IO;
using System.Net.Http;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// WHISPER MODEL DOWNLOAD AND MANAGEMENT SERVICE
///
/// Purpose:
/// Downloads and manages Whisper GGML models from Hugging Face.
/// Models are stored in %LOCALAPPDATA%\HyperWhisper\Models\.
///
/// DOWNLOAD SOURCE:
/// Hugging Face repository: https://huggingface.co/ggerganov/whisper.cpp
/// Direct download URL pattern: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{type}.bin
///
/// NOTE: This file was rewritten to remove dependency on Whisper.net.
/// The old version used WhisperGgmlDownloader from Whisper.net.
/// Now we download directly from Hugging Face using HttpClient.
/// </summary>
public class WhisperModelService
{
    // Whisper GGML files store the "ggml" magic as a little-endian uint.
    private static readonly byte[] GgmlMagic = "lmgg"u8.ToArray();

    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Base URL for downloading GGML models from Hugging Face.
    /// </summary>
    private const string HuggingFaceBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    // =========================================================================
    // PROPERTIES
    // =========================================================================

    /// <summary>
    /// Directory where models are stored.
    /// </summary>
    public static string ModelsDirectory => AppPaths.ModelsDirectory;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public WhisperModelService()
    {
        EnsureModelsDirectory();
    }

    private void EnsureModelsDirectory()
    {
        if (!Directory.Exists(ModelsDirectory))
        {
            LoggingService.Info($"WhisperModelService: Creating models directory: {ModelsDirectory}");
            Directory.CreateDirectory(ModelsDirectory);
        }
    }

    // =========================================================================
    // MODEL PATH MANAGEMENT
    // =========================================================================

    /// <summary>
    /// Gets the local file path for a model.
    ///
    /// FILENAME PATTERN:
    /// Models are named as: ggml-{type}.bin
    /// Examples: ggml-base.bin, ggml-medium.en.bin, ggml-large-v3.bin
    /// </summary>
    public string GetModelPath(WhisperModelInfo model)
    {
        return Path.Combine(ModelsDirectory, $"ggml-{model.Type}.bin");
    }

    /// <summary>
    /// Checks if a model is downloaded.
    /// </summary>
    public bool IsModelDownloaded(WhisperModelInfo model)
    {
        var modelPath = GetModelPath(model);
        if (!File.Exists(modelPath))
        {
            return false;
        }

        try
        {
            var actualSize = new FileInfo(modelPath).Length;
            var minimumExpectedSize = (long)(model.SizeInBytes * 0.95);
            return actualSize >= minimumExpectedSize && HasGgmlHeader(modelPath);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"WhisperModelService: Failed to inspect {model.DisplayName}: {ex.Message}");
            return false;
        }
    }

    // =========================================================================
    // MODEL DOWNLOAD
    // =========================================================================

    /// <summary>
    /// Downloads a model from Hugging Face.
    ///
    /// DOWNLOAD PROCESS:
    /// 1. Construct URL: {HuggingFaceBaseUrl}/ggml-{type}.bin
    /// 2. Download with progress reporting
    /// 3. Save to local models directory
    ///
    /// PROGRESS CALCULATION:
    /// Uses the model's SizeInBytes property to calculate download progress.
    /// This is approximate since actual file sizes may vary slightly.
    ///
    /// RESULT PATTERN:
    /// Returns Result<string> containing the model path on success.
    /// On failure, returns a Result with a descriptive error message.
    /// Cancellation is treated as a failure with a specific message.
    /// </summary>
    /// <param name="model">The model to download.</param>
    /// <param name="progress">Progress reporter (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the model file path on success, or error message on failure.</returns>
    public async Task<Result<string>> DownloadModelAsync(
        WhisperModelInfo model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureModelsDirectory();

        var modelPath = GetModelPath(model);
        var tempPath = modelPath + ".download";
        var downloadUrl = $"{HuggingFaceBaseUrl}/ggml-{model.Type}.bin";

        if (IsModelDownloaded(model))
        {
            LoggingService.Info($"WhisperModelService: Model already downloaded: {model.DisplayName}");
            return Result<string>.Success(modelPath);
        }

        CleanupFile(tempPath);

        LoggingService.Info($"========== STARTING MODEL DOWNLOAD ==========");
        LoggingService.Info($"  Model: {model.DisplayName}");
        LoggingService.Info($"  Type: {model.Type}");
        LoggingService.Info($"  URL: {downloadUrl}");
        LoggingService.Info($"  Destination: {modelPath}");
        LoggingService.Info($"  Expected size: {model.Size}");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromHours(2); // Large models can take a while

        try
        {
            // Get the file with streaming to avoid loading entire file into memory
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // Try to get actual content length from headers
            long? contentLength = response.Content.Headers.ContentLength;
            long expectedSize = contentLength ?? model.SizeInBytes;

            LoggingService.Info($"  Actual content length: {contentLength?.ToString("N0") ?? "unknown"}");

            // Download and save to file
            using var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var buffer = new byte[81920]; // 80 KB buffer
            long totalBytesRead = 0;
            int bytesRead;

            using (var fileStream = File.Create(tempPath))
            {
                while ((bytesRead = await downloadStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    // Report progress
                    double progressValue = (double)totalBytesRead / expectedSize;
                    progress?.Report(Math.Min(progressValue, 1.0)); // Cap at 100%

                    // Log progress every 10%
                    int currentPercent = (int)(progressValue * 100);
                    if (currentPercent % 10 == 0)
                    {
                        LoggingService.Debug($"  Download progress: {currentPercent}% ({totalBytesRead:N0} / {expectedSize:N0} bytes)");
                    }
                }
            }

            LoggingService.Info($"  Download complete: {totalBytesRead:N0} bytes written");
            LoggingService.Info($"========== MODEL DOWNLOAD COMPLETE ==========");

            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
            }
            File.Move(tempPath, modelPath);

            if (!IsModelDownloaded(model))
            {
                CleanupFile(modelPath);
                LoggingService.Warn($"WhisperModelService: Downloaded file failed GGML validation for {model.DisplayName}");
                return Result<string>.Failure(Loc.S("settings.models.download.failed", Loc.S("settings.models.localLlm.invalidModelFile")));
            }

            // SUCCESS: Return the model path
            return Result<string>.Success(modelPath);
        }
        catch (OperationCanceledException)
        {
            // Clean up partial download
            CleanupFile(tempPath);
            LoggingService.Warn($"WhisperModelService: Download cancelled, cleaned up partial file");

            // CANCELLATION: Return specific failure message
            return Result<string>.Failure(Loc.S("settings.models.download.cancelled"));
        }
        catch (Exception ex)
        {
            // Clean up partial download
            CleanupFile(tempPath);
            LoggingService.Error($"WhisperModelService: Download failed", ex);

            // FAILURE: Return error with exception details
            return Result<string>.Failure(Loc.S("settings.models.download.failed", ex.Message), ex);
        }
    }

    /// <summary>
    /// Deletes a downloaded model.
    /// </summary>
    public Result DeleteModel(WhisperModelInfo model)
    {
        var path = GetModelPath(model);

        try
        {
            if (File.Exists(path))
            {
                LoggingService.Info($"WhisperModelService: Deleting model at {path}");
                File.Delete(path);
            }

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LoggingService.Error($"WhisperModelService: Failed to delete model {model.DisplayName}", ex);
            return Result.Failure(ex.Message, ex);
        }
    }

    private static void CleanupFile(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    private static bool HasGgmlHeader(string path)
    {
        Span<byte> header = stackalloc byte[GgmlMagic.Length];
        using var stream = File.OpenRead(path);
        var bytesRead = stream.Read(header);
        return bytesRead == GgmlMagic.Length && header.SequenceEqual(GgmlMagic);
    }
}
