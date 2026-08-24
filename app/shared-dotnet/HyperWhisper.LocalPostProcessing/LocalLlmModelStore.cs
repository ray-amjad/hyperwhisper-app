using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HyperWhisper.LocalPostProcessing;

public sealed record LocalLlmModelDescriptor(
    string Id,
    string FileName,
    Uri DownloadUri,
    long MinimumSizeBytes,
    string? Sha256 = null);

public sealed record LocalLlmModelValidation(bool IsValid, string? Failure);

public sealed record LocalLlmModelDownloadResult(
    string? ModelPath,
    LocalPostProcessingFailure? Failure)
{
    public bool IsSuccess => ModelPath is not null && Failure is null;

    public static LocalLlmModelDownloadResult Success(string path) => new(path, null);
    public static LocalLlmModelDownloadResult Failed(
        LocalPostProcessingErrorCode code,
        string message) => new(null, new(code, message));
}

public interface ILocalLlmModelSource
{
    ValueTask<Stream> OpenReadAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class HttpLocalLlmModelSource(HttpClient httpClient) : ILocalLlmModelSource
{
    public async ValueTask<Stream> OpenReadAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Local LLM downloads require HTTPS.", nameof(uri));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return new ResponseOwnedStream(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

public sealed class LocalLlmModelStore
{
    private static ReadOnlySpan<byte> GgufMagic => "GGUF"u8;
    private readonly string _modelsDirectory;
    private readonly ILocalLlmModelSource _source;

    public LocalLlmModelStore(string modelsDirectory, ILocalLlmModelSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public string GetModelPath(LocalLlmModelDescriptor model)
    {
        ValidateDescriptor(model);
        return Path.Combine(_modelsDirectory, model.FileName);
    }

    public async ValueTask<LocalLlmModelValidation> ValidateAsync(
        LocalLlmModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        var path = GetModelPath(model);
        if (!File.Exists(path))
        {
            return new(false, "The local LLM model is not downloaded.");
        }

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < Math.Max(8, model.MinimumSizeBytes))
            {
                return new(false, "The local LLM model file is truncated.");
            }

            var header = new byte[4];
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != header.Length
                || !header.AsSpan().SequenceEqual(GgufMagic))
            {
                return new(false, "The local LLM model does not have a GGUF header.");
            }

            if (!string.IsNullOrWhiteSpace(model.Sha256))
            {
                stream.Position = 0;
                var actual = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new(false, "The local LLM model checksum does not match the catalog.");
                }
            }
            return new(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, "The local LLM model could not be inspected.");
        }
    }

    public async ValueTask<LocalLlmModelDownloadResult> DownloadAsync(
        LocalLlmModelDescriptor model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDescriptor(model);
        var finalPath = GetModelPath(model);
        var temporaryPath = Path.Combine(
            _modelsDirectory, $".{model.FileName}.{Guid.NewGuid():N}.download");

        try
        {
            Directory.CreateDirectory(_modelsDirectory);
            await using var source = await _source.OpenReadAsync(
                model.DownloadUri, cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    long written = 0;
                    while (true)
                    {
                        var read = await source.ReadAsync(
                            buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }
                        await destination.WriteAsync(
                            buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        if (model.MinimumSizeBytes > 0)
                        {
                            progress?.Report(Math.Min(0.999, (double)written / model.MinimumSizeBytes));
                        }
                    }
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }

            var temporaryModel = model with { FileName = Path.GetFileName(temporaryPath) };
            var validation = await ValidatePathAsync(
                temporaryPath, temporaryModel.MinimumSizeBytes, temporaryModel.Sha256,
                cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return LocalLlmModelDownloadResult.Failed(
                    LocalPostProcessingErrorCode.ModelInvalid,
                    validation.Failure ?? "The local LLM model is invalid.");
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            progress?.Report(1);
            return LocalLlmModelDownloadResult.Success(finalPath);
        }
        catch (OperationCanceledException)
        {
            return LocalLlmModelDownloadResult.Failed(
                LocalPostProcessingErrorCode.Cancelled,
                "The local LLM model download was cancelled.");
        }
        catch (Exception)
        {
            return LocalLlmModelDownloadResult.Failed(
                LocalPostProcessingErrorCode.ModelDownloadFailed,
                "The local LLM model download failed.");
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception) { }
        }
    }

    private static async ValueTask<LocalLlmModelValidation> ValidatePathAsync(
        string path,
        long minimumSizeBytes,
        string? sha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < Math.Max(8, minimumSizeBytes))
        {
            return new(false, "The local LLM model file is truncated.");
        }
        var header = new byte[4];
        if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 4
            || !header.AsSpan().SequenceEqual(GgufMagic))
        {
            return new(false, "The local LLM model does not have a GGUF header.");
        }
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            stream.Position = 0;
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, "The local LLM model checksum does not match the catalog.");
            }
        }
        return new(true, null);
    }

    private static void ValidateDescriptor(LocalLlmModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(model.FileName)
            || model.FileName != Path.GetFileName(model.FileName)
            || model.FileName.Contains('\\'))
        {
            throw new ArgumentException("The model filename must be a plain filename.", nameof(model));
        }
        if (model.MinimumSizeBytes < 0)
        {
            throw new ArgumentException("The model minimum size cannot be negative.", nameof(model));
        }
        if (model.Sha256 is { Length: > 0 } hash
            && (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException("The model SHA-256 must contain 64 hexadecimal characters.", nameof(model));
        }
    }
}
