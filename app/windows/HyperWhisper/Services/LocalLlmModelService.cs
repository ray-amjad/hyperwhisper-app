using System.IO;
using System.Net.Http;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Downloads and manages single-file GGUF local LLM models.
/// Storage: %LOCALAPPDATA%\HyperWhisper\Models\LLM\{filename}
/// </summary>
public class LocalLlmModelService
{
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    public static string ModelsDirectory => Path.Combine(
        AppPaths.ModelsDirectory,
        "LLM"
    );

    public LocalLlmModelService()
    {
        EnsureModelsDirectory();
    }

    public string GetModelPath(LocalLlmModelInfo model)
    {
        return Path.Combine(ModelsDirectory, model.FileName);
    }

    public LocalLlmModelInfo[] GetDownloadedModels()
    {
        return LocalLlmModelInfo.AllModels.Where(IsModelDownloaded).ToArray();
    }

    public bool IsModelDownloaded(LocalLlmModelInfo model)
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
            return actualSize >= minimumExpectedSize && HasGgufHeader(modelPath);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmModelService: Failed to inspect {model.DisplayName}: {ex.Message}");
            return false;
        }
    }

    public async Task<Result<string>> DownloadModelAsync(
        LocalLlmModelInfo model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureModelsDirectory();

        var finalPath = GetModelPath(model);
        var tempPath = finalPath + ".download";

        if (IsModelDownloaded(model))
        {
            LoggingService.Info($"LocalLlmModelService: Model already downloaded: {model.DisplayName}");
            return Result<string>.Success(finalPath);
        }

        var diskCheck = EnsureDiskSpace(model);
        if (diskCheck.IsFailure)
        {
            return Result<string>.Failure(diskCheck.Error!);
        }

        CleanupFile(tempPath);

        LoggingService.Info("========== STARTING LOCAL LLM MODEL DOWNLOAD ==========");
        LoggingService.Info($"  Model: {model.DisplayName}");
        LoggingService.Info($"  URL: {model.DownloadUrl}");
        LoggingService.Info($"  Destination: {finalPath}");
        LoggingService.Info($"  Expected size: {model.Size}");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromHours(12);

        try
        {
            using var response = await httpClient.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            var expectedSize = contentLength ?? model.SizeInBytes;

            using var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var buffer = new byte[81920];
            long totalBytesRead = 0;
            int bytesRead;
            var lastLoggedPercent = -1;

            await using (var fileStream = File.Create(tempPath))
            {
                while ((bytesRead = await downloadStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    var progressValue = expectedSize > 0 ? (double)totalBytesRead / expectedSize : 0;
                    progress?.Report(Math.Min(progressValue, 0.999));

                    var currentPercent = (int)(progressValue * 100);
                    if (currentPercent / 10 != lastLoggedPercent / 10)
                    {
                        lastLoggedPercent = currentPercent;
                        LoggingService.Debug($"  Download progress: {currentPercent}% ({totalBytesRead:N0} / {expectedSize:N0} bytes)");
                    }
                }

                await fileStream.FlushAsync(cancellationToken);
            }

            // Guard against a silently-truncated body: when the server declared a
            // length, require the full payload before persisting. Without Content-Length
            // (chunked / some CDNs) a connection can close mid-body without throwing, so
            // the read loop exits normally on a partial file; this assertion is the only
            // place that detects it. Guard on the header value (contentLength), not on
            // expectedSize, which falls back to the catalog estimate.
            if (contentLength.HasValue && totalBytesRead != contentLength.Value)
            {
                CleanupFile(tempPath);
                LoggingService.Warn(
                    $"LocalLlmModelService: Truncated download for {model.DisplayName} " +
                    $"({totalBytesRead:N0} / {contentLength.Value:N0} bytes)");
                return Result<string>.Failure(Loc.S("settings.models.download.failed", $"incomplete transfer ({totalBytesRead:N0} / {contentLength.Value:N0} bytes)"));
            }

            await MoveDownloadedFileAsync(tempPath, finalPath, cancellationToken);

            if (!IsModelDownloaded(model))
            {
                CleanupFile(finalPath);
                LoggingService.Warn($"LocalLlmModelService: Downloaded file failed GGUF validation for {model.DisplayName}");
                return Result<string>.Failure(Loc.S("settings.models.download.failed", Loc.S("settings.models.localLlm.invalidModelFile")));
            }

            progress?.Report(1.0);
            LoggingService.Info($"  Download complete: {totalBytesRead:N0} bytes written");
            LoggingService.Info("========== LOCAL LLM MODEL DOWNLOAD COMPLETE ==========");

            return Result<string>.Success(finalPath);
        }
        catch (OperationCanceledException)
        {
            CleanupFile(tempPath);
            LoggingService.Warn("LocalLlmModelService: Download cancelled, cleaned up partial file");
            return Result<string>.Failure(Loc.S("settings.models.download.cancelled"));
        }
        catch (Exception ex)
        {
            CleanupFile(tempPath);
            LoggingService.Error("LocalLlmModelService: Download failed", ex);
            return Result<string>.Failure(Loc.S("settings.models.download.failed", ex.Message), ex);
        }
    }

    public Result DeleteModel(LocalLlmModelInfo model)
    {
        var path = GetModelPath(model);

        try
        {
            if (File.Exists(path))
            {
                LoggingService.Info($"LocalLlmModelService: Deleting model at {path}");
                File.Delete(path);
            }

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LoggingService.Error($"LocalLlmModelService: Failed to delete model {model.DisplayName}", ex);
            return Result.Failure(ex.Message, ex);
        }
    }

    private static Result EnsureDiskSpace(LocalLlmModelInfo model)
    {
        try
        {
            var root = Path.GetPathRoot(ModelsDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return Result.Success();
            }

            var driveInfo = new DriveInfo(root);
            var requiredBytes = (long)(model.SizeInBytes * 1.1);
            if (driveInfo.AvailableFreeSpace >= requiredBytes)
            {
                return Result.Success();
            }

            var needed = FormatBytes(requiredBytes);
            var available = FormatBytes(driveInfo.AvailableFreeSpace);
            return Result.Failure(Loc.S("settings.models.localLlm.diskSpace", model.DisplayName, needed, available));
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalLlmModelService: Disk space check failed: {ex.Message}");
            return Result.Success();
        }
    }

    private static void EnsureModelsDirectory()
    {
        if (!Directory.Exists(ModelsDirectory))
        {
            LoggingService.Info($"LocalLlmModelService: Creating models directory: {ModelsDirectory}");
            Directory.CreateDirectory(ModelsDirectory);
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

    private static async Task MoveDownloadedFileAsync(
        string tempPath,
        string finalPath,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 6;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Move(tempPath, finalPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                attempt < maxAttempts &&
                ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Warn($"LocalLlmModelService: Model finalization attempt {attempt} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }
    }

    private static bool HasGgufHeader(string path)
    {
        Span<byte> header = stackalloc byte[GgufMagic.Length];
        using var stream = File.OpenRead(path);
        var bytesRead = stream.Read(header);
        return bytesRead == GgufMagic.Length && header.SequenceEqual(GgufMagic);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F0} MB";
        }

        return $"{bytes / 1024.0:F0} KB";
    }
}
