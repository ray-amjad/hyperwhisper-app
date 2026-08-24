using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.ModelManagement;

public sealed class PortableModelManager
{
    private const int MaximumTreePages = 32;
    private const int MaximumTreeArtifacts = 4_096;
    // Windows catalog sizes for Parakeet and local LLMs are decimal display
    // estimates rather than byte-exact manifests. Ten percent permits normal
    // upstream packaging drift while remaining a finite per-artifact and
    // aggregate disk-use ceiling when no trustworthy length is available.
    private const double ApproximateSizeHeadroom = 1.10;
    private static readonly byte[] GgmlMagic = "lmgg"u8.ToArray();
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();
    private readonly string _modelsRoot;
    private readonly HttpClient _http;
    private readonly Action<string, string>? _beforeDirectoryPromotion;

    public PortableModelManager(IAppPaths paths, HttpClient httpClient)
        : this(paths, httpClient, null)
    {
    }

    internal PortableModelManager(IAppPaths paths, HttpClient httpClient, Action<string, string>? beforeDirectoryPromotion)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _beforeDirectoryPromotion = beforeDirectoryPromotion;
        _modelsRoot = Path.GetFullPath(paths.ModelsDirectory);
        EnsurePrivateDirectory(_modelsRoot);
        EnsureNoSymbolicLinks(_modelsRoot, _modelsRoot);
    }

    public string GetInstalledPath(ManagedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var kindRoot = model.Kind switch
        {
            ManagedModelKind.Whisper => _modelsRoot,
            ManagedModelKind.Parakeet => Contained(_modelsRoot, "Parakeet"),
            ManagedModelKind.LocalLlm => Contained(_modelsRoot, "LLM"),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var installedPath = Contained(kindRoot, model.StorageName);
        EnsureNoSymbolicLinks(_modelsRoot, installedPath);
        return installedPath;
    }

    public bool IsInstalled(ManagedModel model)
    {
        try
        {
            var path = GetInstalledPath(model);
            return model.Layout switch
            {
                ManagedModelLayout.SingleFile => ValidateSingleFile(model, path),
                ManagedModelLayout.FixedFiles => model.Artifacts.Count > 0 &&
                    model.Artifacts.All(a => ValidateArtifactFile(a, Contained(path, a.RelativePath))),
                ManagedModelLayout.HuggingFaceTree => ValidateTree(path),
                _ => false
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public async Task<ModelManagementResult<string>> DownloadAsync(
        ManagedModel model,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateDescriptor(model);
            if (IsInstalled(model)) return ModelManagementResult<string>.Success(GetInstalledPath(model));

            var finalPath = GetInstalledPath(model);
            var parent = Path.GetDirectoryName(finalPath)!;
            EnsurePrivateDirectory(parent);
            var partialPath = Contained(parent, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial");
            var tracker = new DownloadProgressTracker(model, progress);

            try
            {
                if (model.Layout == ManagedModelLayout.SingleFile)
                {
                    await DownloadArtifactAsync(model, model.Artifacts.Single(), partialPath, tracker, cancellationToken);
                    if (!ValidateSingleFile(model, partialPath))
                        return ModelManagementResult<string>.Fail(ModelManagementError.Validation, "Downloaded model failed format or size validation.");
                    File.Move(partialPath, finalPath, true);
                    RestrictFile(finalPath);
                }
                else
                {
                    EnsurePrivateDirectory(partialPath);
                    var artifacts = model.Layout == ManagedModelLayout.HuggingFaceTree
                        ? await ResolveTreeArtifactsAsync(model, cancellationToken)
                        : model.Artifacts;
                    if (artifacts.Count == 0)
                        return ModelManagementResult<string>.Fail(ModelManagementError.Validation, "Model repository contained no downloadable model files.");

                    foreach (var artifact in artifacts)
                    {
                        var destination = Contained(partialPath, artifact.RelativePath);
                        EnsurePrivateDirectory(Path.GetDirectoryName(destination)!);
                        await DownloadArtifactAsync(model, artifact, destination, tracker, cancellationToken);
                    }
                    if (model.Layout == ManagedModelLayout.HuggingFaceTree && !ValidateTree(partialPath))
                        return ModelManagementResult<string>.Fail(ModelManagementError.Validation, "Downloaded model tree is incomplete.");
                    PromoteDirectoryWithRollback(partialPath, finalPath);
                }

                if (!IsInstalled(model))
                {
                    DeletePath(finalPath);
                    return ModelManagementResult<string>.Fail(ModelManagementError.Validation, "Finalized model failed installed-state validation.");
                }
                tracker.Complete();
                return ModelManagementResult<string>.Success(finalPath);
            }
            finally
            {
                DeletePath(partialPath);
            }
        }
        catch (OperationCanceledException)
        {
            return ModelManagementResult<string>.Fail(ModelManagementError.Cancelled, "Model download was cancelled.");
        }
        catch (HttpRequestException)
        {
            return ModelManagementResult<string>.Fail(ModelManagementError.Network, "The model download service could not be reached.");
        }
        catch (JsonException)
        {
            return ModelManagementResult<string>.Fail(ModelManagementError.Network, "The model repository returned an invalid response.");
        }
        catch (InvalidDataException ex)
        {
            return ModelManagementResult<string>.Fail(ModelManagementError.Validation, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModelManagementResult<string>.Fail(ModelManagementError.Storage, "The model could not be stored securely.");
        }
        catch (ArgumentException ex)
        {
            return ModelManagementResult<string>.Fail(ModelManagementError.InvalidRequest, ex.Message);
        }
    }

    public ModelManagementResult<bool> Delete(ManagedModel model)
    {
        try
        {
            ValidateDescriptor(model);
            DeletePath(GetInstalledPath(model));
            return ModelManagementResult<bool>.Success(true);
        }
        catch (ArgumentException ex)
        {
            return ModelManagementResult<bool>.Fail(ModelManagementError.InvalidRequest, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModelManagementResult<bool>.Fail(ModelManagementError.Storage, "The model could not be deleted.");
        }
    }

    private async Task DownloadArtifactAsync(ManagedModel model, ModelArtifact artifact, string destination,
        DownloadProgressTracker tracker, CancellationToken cancellationToken)
    {
        if (artifact.DownloadUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Model downloads must use HTTPS.", nameof(model));
        using var response = await _http.GetAsync(artifact.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Model host returned an unsuccessful status.", null, response.StatusCode);
        var declared = response.Content.Headers.ContentLength;
        var artifactCeiling = artifact.ExactSizeBytes ?? Ceiling(model.ApproximateSizeBytes);
        if (artifact.ExactSizeBytes is { } exact && declared is { } headerSize && headerSize != exact)
            throw new InvalidDataException("Model size did not match the catalog.");
        if (declared is { } declaredSize && declaredSize > artifactCeiling)
            throw new InvalidDataException("Model artifact exceeded the allowed size.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(destination, fileOptions);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (received > artifactCeiling - read)
                throw new InvalidDataException("Model artifact exceeded the allowed size.");
            tracker.Add(read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            received += read;
        }
        await output.FlushAsync(cancellationToken);
        if (declared is { } completedDeclaredSize && received != completedDeclaredSize)
            throw new InvalidDataException("Model transfer was incomplete.");
        if (artifact.ExactSizeBytes is { } expected && received != expected)
            throw new InvalidDataException("Model size did not match the catalog.");
        if (artifact.Sha256 is { } expectedHash &&
            !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model checksum did not match the catalog.");
        RestrictFile(destination);
    }

    private async Task<IReadOnlyList<ModelArtifact>> ResolveTreeArtifactsAsync(ManagedModel model,
        CancellationToken cancellationToken)
    {
        var repo = model.HuggingFaceRepository!;
        var artifacts = new List<ModelArtifact>();
        Uri? uri = new($"https://huggingface.co/api/models/{repo}/tree/main?recursive=true&expand=false");
        var allowedPath = $"/api/models/{repo}/tree/main";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        long aggregateDeclaredSize = 0;
        var aggregateCeiling = Ceiling(model.ApproximateSizeBytes);
        var pages = 0;
        while (uri is not null)
        {
            if (++pages > MaximumTreePages)
                throw new InvalidDataException("Model repository pagination exceeded the allowed limit.");
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.Equals(allowedPath, StringComparison.Ordinal) || !visited.Add(uri.AbsoluteUri))
                throw new InvalidDataException("Model repository pagination was invalid.");
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Model catalog unavailable.");
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "file") continue;
                if (!item.TryGetProperty("path", out var pathValue)) continue;
                var path = pathValue.GetString();
                if (string.IsNullOrWhiteSpace(path) || IsRepositoryMetadata(path)) continue;
                ValidateRelativePath(path);
                long? size = item.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : null;
                if (size is <= 0) size = null;
                if (size is { } declaredArtifactSize)
                {
                    if (aggregateDeclaredSize > aggregateCeiling - declaredArtifactSize)
                        throw new InvalidDataException("Model repository exceeded the allowed aggregate size.");
                    aggregateDeclaredSize += declaredArtifactSize;
                }
                artifacts.Add(new(path, new Uri($"https://huggingface.co/{repo}/resolve/main/{Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}"), size));
                if (artifacts.Count > MaximumTreeArtifacts)
                    throw new InvalidDataException("Model repository contained too many artifacts.");
            }
            uri = NextLink(response);
        }
        return artifacts;
    }

    private static Uri? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in values.SelectMany(value => value.Split(',')))
        {
            var sections = part.Split(';', StringSplitOptions.TrimEntries);
            if (!sections.Skip(1).Any(section => section.Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase))) continue;
            var target = sections[0].Trim();
            if (target.Length > 2 && target[0] == '<' && target[^1] == '>' &&
                Uri.TryCreate(target[1..^1], UriKind.Absolute, out var next) && next.Scheme == Uri.UriSchemeHttps)
                return next;
        }
        return null;
    }

    private void PromoteDirectoryWithRollback(string partialPath, string finalPath)
    {
        if (!Directory.Exists(finalPath))
        {
            Directory.Move(partialPath, finalPath);
            return;
        }

        var parent = Path.GetDirectoryName(finalPath)!;
        var backupPath = Contained(parent, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.replaced");
        Directory.Move(finalPath, backupPath);
        var promoted = false;
        try
        {
            _beforeDirectoryPromotion?.Invoke(partialPath, finalPath);
            Directory.Move(partialPath, finalPath);
            promoted = true;
        }
        finally
        {
            if (!promoted && !Directory.Exists(finalPath) && Directory.Exists(backupPath))
                Directory.Move(backupPath, finalPath);
            if (promoted && Directory.Exists(backupPath))
                Directory.Delete(backupPath, true);
        }
    }

    private static long Ceiling(long approximateSize)
    {
        if (approximateSize > long.MaxValue / 2) return long.MaxValue;
        return checked((long)Math.Ceiling(approximateSize * ApproximateSizeHeadroom));
    }

    private static bool IsRepositoryMetadata(string path) =>
        path.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("test_wavs/", StringComparison.OrdinalIgnoreCase);

    private static bool ValidateSingleFile(ManagedModel model, string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < (long)(model.ApproximateSizeBytes * 0.95)) return false;
        if (!ValidateArtifactFile(model.Artifacts.Single(), path)) return false;
        Span<byte> header = stackalloc byte[4];
        using var input = File.OpenRead(path);
        return input.Read(header) == 4 && header.SequenceEqual(model.Kind == ManagedModelKind.Whisper ? GgmlMagic : GgufMagic);
    }

    private static bool ValidateArtifactFile(ModelArtifact artifact, string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        if (artifact.ExactSizeBytes is { } expected && new FileInfo(path).Length != expected) return false;
        if (artifact.Sha256 is null) return true;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateTree(string path)
    {
        if (!Directory.Exists(path)) return false;
        bool Has(string prefix) => Directory.EnumerateFiles(path, prefix + "*.onnx", SearchOption.TopDirectoryOnly).Any(f => new FileInfo(f).Length > 0);
        var tokenizer = Contained(path, "tokenizer");
        return Has("conv_frontend") && Has("encoder") && Has("decoder") && Directory.Exists(tokenizer) &&
            Directory.EnumerateFiles(tokenizer, "*", SearchOption.AllDirectories).Any(f => new FileInfo(f).Length > 0);
    }

    private static void ValidateDescriptor(ManagedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidatePathSegment(model.StorageName);
        if (model.ApproximateSizeBytes <= 0) throw new ArgumentException("Model size must be positive.", nameof(model));
        if (model.Layout == ManagedModelLayout.SingleFile && model.Artifacts.Count != 1)
            throw new ArgumentException("A single-file model must have one artifact.", nameof(model));
        if (model.Layout == ManagedModelLayout.HuggingFaceTree)
        {
            var repositoryParts = model.HuggingFaceRepository?.Split('/') ?? [];
            if (repositoryParts.Length != 2 || repositoryParts.Any(part =>
                    string.IsNullOrWhiteSpace(part)
                    || part is "." or ".."
                    || part.Any(character => !char.IsAsciiLetterOrDigit(character)
                        && character is not '-' and not '_' and not '.')))
                throw new ArgumentException("A tree model must identify one valid Hugging Face repository.", nameof(model));
        }
        foreach (var artifact in model.Artifacts)
        {
            ValidateRelativePath(artifact.RelativePath);
            if (artifact.Sha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
                throw new ArgumentException("Artifact SHA-256 is invalid.", nameof(model));
        }
    }

    private static void ValidatePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(value))
            throw new ArgumentException("Model storage name is invalid.");
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Model artifact path is invalid.");
    }

    private static string Contained(string root, string relative)
    {
        ValidateRelativePath(relative);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.Ordinal))
            throw new ArgumentException("Model path escapes the model directory.");
        return candidate;
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void DeletePath(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static void EnsureNoSymbolicLinks(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(rootFull) && new DirectoryInfo(rootFull).LinkTarget is not null)
            throw new ArgumentException("The model directory must not be a symbolic link.");

        var relative = Path.GetRelativePath(rootFull, Path.GetFullPath(candidate));
        if (relative == ".") return;
        var current = rootFull;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (info?.LinkTarget is not null)
                throw new ArgumentException("Model paths must not contain symbolic links.");
            if (info is null) break;
        }
    }

    private sealed class DownloadProgressTracker
    {
        private readonly ManagedModel _model;
        private readonly IProgress<ModelDownloadProgress>? _progress;
        private readonly long _ceiling;
        private long _received;

        public DownloadProgressTracker(ManagedModel model, IProgress<ModelDownloadProgress>? progress)
        {
            _model = model;
            _progress = progress;
            _ceiling = Ceiling(model.ApproximateSizeBytes);
        }

        public void Add(int bytes)
        {
            if (_received > _ceiling - bytes)
                throw new InvalidDataException("Model download exceeded the allowed aggregate size.");
            _received += bytes;
            _progress?.Report(new(_model.Id, _received, _model.ApproximateSizeBytes,
                Math.Min(0.999, (double)_received / _model.ApproximateSizeBytes)));
        }

        public void Complete() => _progress?.Report(new(_model.Id, _received, _received, 1));
    }
}
