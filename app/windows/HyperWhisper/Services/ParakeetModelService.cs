using System.IO;
using System.Net.Http;
using System.Text.Json;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// PARAKEET MODEL DOWNLOAD AND MANAGEMENT SERVICE
///
/// Downloads and manages Parakeet ONNX models from HuggingFace.
/// Each model consists of multiple ONNX files stored in a directory.
///
/// ATOMIC DOWNLOAD STRATEGY:
/// Files are downloaded to a temp directory ({model-id}.temp/) first,
/// then renamed to the final directory on success. This prevents
/// partial downloads from being mistaken as complete models.
///
/// STORAGE: %LOCALAPPDATA%\HyperWhisper\Models\Parakeet\{model-id}\
/// </summary>
public class ParakeetModelService
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const string HuggingFaceBaseUrl = "https://huggingface.co";

    // =========================================================================
    // PROPERTIES
    // =========================================================================

    /// <summary>
    /// Root directory for all Parakeet models.
    /// </summary>
    public static string ModelsDirectory => Path.Combine(
        AppPaths.ModelsDirectory,
        "Parakeet"
    );

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public ParakeetModelService()
    {
        EnsureModelsDirectory();
    }

    private void EnsureModelsDirectory()
    {
        if (!Directory.Exists(ModelsDirectory))
        {
            LoggingService.Info($"ParakeetModelService: Creating models directory: {ModelsDirectory}");
            Directory.CreateDirectory(ModelsDirectory);
        }
    }

    // =========================================================================
    // MODEL PATH MANAGEMENT
    // =========================================================================

    /// <summary>
    /// Gets the directory path for a model.
    /// Unlike WhisperModelService which returns a file path,
    /// Parakeet models are directories containing multiple ONNX files.
    /// </summary>
    public string GetModelDirectory(ParakeetModelInfo model)
    {
        return Path.Combine(ModelsDirectory, model.Id);
    }

    /// <summary>
    /// Checks if a model is fully downloaded.
    ///
    /// Parakeet (flat): verifies every file in <see cref="ParakeetModelInfo.OnnxFileNames"/>.
    /// Qwen3 (tree): the exact filenames vary by export (int8 vs fp32), so verify
    /// structurally — a conv-frontend, an encoder, a decoder ONNX, and a non-empty
    /// <c>tokenizer/</c> directory.
    /// </summary>
    public bool IsModelDownloaded(ParakeetModelInfo model)
    {
        var modelDir = GetModelDirectory(model);
        if (!Directory.Exists(modelDir)) return false;

        if (model.Engine == ParakeetEngine.Qwen3)
        {
            bool HasOnnx(string prefix) => Directory
                .EnumerateFiles(modelDir, prefix + "*.onnx", SearchOption.TopDirectoryOnly)
                .Any();

            var tokenizerDir = Path.Combine(modelDir, "tokenizer");

            return HasOnnx("conv_frontend")
                && HasOnnx("encoder")
                && HasOnnx("decoder")
                && Directory.Exists(tokenizerDir)
                && Directory.EnumerateFileSystemEntries(tokenizerDir).Any();
        }

        return model.OnnxFileNames.All(f => File.Exists(Path.Combine(modelDir, f)));
    }

    // =========================================================================
    // MODEL DOWNLOAD
    // =========================================================================

    /// <summary>
    /// Downloads all ONNX files for a model from HuggingFace.
    ///
    /// DOWNLOAD PROCESS:
    /// 1. Download each file to {model-id}.temp/ directory
    /// 2. Aggregate progress across files weighted by expected size
    /// 3. On success, rename temp dir to final dir
    /// 4. On failure/cancellation, delete temp dir
    ///
    /// URL PATTERN:
    /// https://huggingface.co/{repo}/resolve/main/{filename}
    /// </summary>
    public async Task<Result<string>> DownloadModelAsync(
        ParakeetModelInfo model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureModelsDirectory();

        var finalDir = GetModelDirectory(model);
        var tempDir = finalDir + ".temp";

        LoggingService.Info($"========== STARTING PARAKEET MODEL DOWNLOAD ==========");
        LoggingService.Info($"  Model: {model.DisplayName}");
        LoggingService.Info($"  Id: {model.Id}");
        LoggingService.Info($"  Repo: {model.HuggingFaceRepo}");
        LoggingService.Info($"  Files: {string.Join(", ", model.OnnxFileNames)}");
        LoggingService.Info($"  Destination: {finalDir}");
        LoggingService.Info($"  Expected size: {model.Size}");

        // Clean up any previous temp directory
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, true); }
            catch { /* Ignore cleanup errors */ }
        }

        Directory.CreateDirectory(tempDir);

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromHours(2);

        try
        {
            long totalBytesDownloaded = 0;
            long totalExpectedBytes = model.SizeInBytes;

            // Resolve the repo-relative paths to download. Parakeet ships a fixed
            // flat list; Qwen3 is a tree (conv_frontend/encoder/decoder + a
            // tokenizer/ directory) enumerated from the HuggingFace API so the
            // exact tokenizer contents need not be hard-coded.
            var filesToDownload = model.IsHuggingFaceTreeDownload
                ? await EnumerateRepoFilesAsync(httpClient, model.HuggingFaceRepo, cancellationToken)
                : model.OnnxFileNames.ToList();

            if (filesToDownload.Count == 0)
            {
                CleanupTempDirectory(tempDir);
                LoggingService.Error($"ParakeetModelService: No files found in repo {model.HuggingFaceRepo}");
                return Result<string>.Failure(Loc.S("settings.models.download.failed", "no files found in model repository"));
            }

            for (int i = 0; i < filesToDownload.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = filesToDownload[i];
                var downloadUrl = $"{HuggingFaceBaseUrl}/{model.HuggingFaceRepo}/resolve/main/{relativePath}";
                var filePath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

                // Tree models contain subdirectories (e.g. tokenizer/) — ensure parents exist.
                var parentDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                LoggingService.Info($"  Downloading file {i + 1}/{filesToDownload.Count}: {relativePath}");
                LoggingService.Debug($"  URL: {downloadUrl}");

                using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = File.Create(filePath);

                var buffer = new byte[81920]; // 80 KB buffer
                int bytesRead;

                while ((bytesRead = await downloadStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesDownloaded += bytesRead;

                    double progressValue = (double)totalBytesDownloaded / totalExpectedBytes;
                    progress?.Report(Math.Min(progressValue, 1.0));
                }

                LoggingService.Info($"  File complete: {relativePath} ({new FileInfo(filePath).Length:N0} bytes)");
            }

            // Atomic rename: temp -> final
            if (Directory.Exists(finalDir))
            {
                Directory.Delete(finalDir, true);
            }
            Directory.Move(tempDir, finalDir);

            LoggingService.Info($"  Download complete: {totalBytesDownloaded:N0} total bytes");
            LoggingService.Info($"========== PARAKEET MODEL DOWNLOAD COMPLETE ==========");

            return Result<string>.Success(finalDir);
        }
        catch (OperationCanceledException)
        {
            CleanupTempDirectory(tempDir);
            LoggingService.Warn($"ParakeetModelService: Download cancelled, cleaned up temp directory");
            return Result<string>.Failure(Loc.S("settings.models.download.cancelled"));
        }
        catch (Exception ex)
        {
            CleanupTempDirectory(tempDir);
            LoggingService.Error($"ParakeetModelService: Download failed", ex);
            return Result<string>.Failure(Loc.S("settings.models.download.failed", ex.Message), ex);
        }
    }

    /// <summary>
    /// Deletes a downloaded model and its directory.
    /// </summary>
    public Result DeleteModel(ParakeetModelInfo model)
    {
        var modelDir = GetModelDirectory(model);

        try
        {
            if (Directory.Exists(modelDir))
            {
                LoggingService.Info($"ParakeetModelService: Deleting model at {modelDir}");
                Directory.Delete(modelDir, true);
            }

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LoggingService.Error($"ParakeetModelService: Failed to delete model {model.DisplayName}", ex);
            return Result.Failure(ex.Message, ex);
        }
    }

    /// <summary>
    /// Enumerates every downloadable file (recursively, including subdirectories
    /// such as <c>tokenizer/</c>) in a HuggingFace model repo via the public tree
    /// API. Skips repo metadata (.gitattributes, README). Returns repo-relative
    /// forward-slash paths suitable for the <c>/resolve/main/{path}</c> endpoint.
    /// </summary>
    private static async Task<List<string>> EnumerateRepoFilesAsync(
        HttpClient httpClient, string repo, CancellationToken cancellationToken)
    {
        var files = new List<string>();

        // The HF tree API paginates via a cursor advertised in the Link: rel="next"
        // header. Follow every page so a repo larger than one page never silently
        // truncates the download.
        var nextUrl = $"{HuggingFaceBaseUrl}/api/models/{repo}/tree/main?recursive=true";

        while (!string.IsNullOrEmpty(nextUrl))
        {
            LoggingService.Info($"  Enumerating model repo tree: {nextUrl}");

            using var response = await httpClient.GetAsync(nextUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken))
            {
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "file")
                        continue;
                    if (!entry.TryGetProperty("path", out var pathProp))
                        continue;

                    var path = pathProp.GetString();
                    if (string.IsNullOrEmpty(path)) continue;

                    // Skip repo metadata and bundled sample audio that aren't part
                    // of the model (the sherpa exports ship a test_wavs/ directory).
                    if (path.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)) continue;
                    if (path.Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;
                    if (path.StartsWith("test_wavs/", StringComparison.OrdinalIgnoreCase)) continue;

                    files.Add(path);
                }
            }

            nextUrl = GetNextPageLink(response);
        }

        return files;
    }

    /// <summary>
    /// Extracts the <c>rel="next"</c> URL from an RFC 5988 <c>Link</c> header, or
    /// null when there is no next page.
    /// </summary>
    private static string? GetNextPageLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;

        foreach (var value in values)
        {
            foreach (var part in value.Split(','))
            {
                var segments = part.Split(';');
                if (segments.Length < 2) continue;
                if (!segments.Any(s => s.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var urlPart = segments[0].Trim();
                if (urlPart.StartsWith('<') && urlPart.EndsWith('>'))
                    return urlPart.Substring(1, urlPart.Length - 2);
            }
        }

        return null;
    }

    private static void CleanupTempDirectory(string tempDir)
    {
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, true); }
            catch { /* Ignore cleanup errors */ }
        }
    }
}
