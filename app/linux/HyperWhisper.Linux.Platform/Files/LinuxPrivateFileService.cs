using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Files;

public sealed class LinuxPrivateFileService : IPrivateFileService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IPrivateFilePermissions _permissions;
    private readonly IAtomicReplace _atomicReplace;

    public LinuxPrivateFileService()
        : this(new UnixPrivateFilePermissions(), new AtomicReplace())
    {
    }

    internal LinuxPrivateFileService(
        IPrivateFilePermissions permissions,
        IAtomicReplace atomicReplace)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _atomicReplace = atomicReplace ?? throw new ArgumentNullException(nameof(atomicReplace));
    }

    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        var validation = UnixPrivateFilePermissions.ValidatePath(path);
        if (validation is not null)
        {
            return validation;
        }

        var temporaryPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent))
            {
                return PlatformResult.Failure("invalid_path", "The file path has no parent directory.");
            }

            Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            temporaryPath = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = UnixPrivateFilePermissions.PrivateMode,
            };

            using (var stream = new FileStream(temporaryPath, options))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            var restricted = _permissions.RestrictToCurrentUser(temporaryPath);
            if (restricted.IsFailure)
            {
                return restricted;
            }

            _atomicReplace.Replace(temporaryPath, fullPath);
            temporaryPath = string.Empty;
            return PlatformResult.Success();
        }
        catch (Exception exception) when (UnixPrivateFilePermissions.IsExpectedFileFailure(exception))
        {
            return PlatformResult.Failure("private_file_write_failed", "The private file could not be written atomically.");
        }
        finally
        {
            if (temporaryPath.Length != 0)
            {
                _atomicReplace.TryDelete(temporaryPath);
            }
        }
    }

    public PlatformResult WriteAllTextAtomically(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return WriteAllBytesAtomically(path, StrictUtf8.GetBytes(contents));
    }

    public PlatformResult<byte[]?> ReadAllBytes(string path)
    {
        var validation = UnixPrivateFilePermissions.ValidatePath(path);
        if (validation is not null)
        {
            return PlatformResult<byte[]?>.Failure(validation.Error!.Code, validation.Error.Message);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt64();
            var openFilePath = $"/proc/self/fd/{descriptor}";
            var restricted = _permissions.IsRestrictedToCurrentUser(openFilePath);
            if (restricted.IsFailure)
            {
                return PlatformResult<byte[]?>.Failure(restricted.Error!.Code, restricted.Error.Message);
            }

            if (restricted.Value != true)
            {
                return PlatformResult<byte[]?>.Failure("private_file_insecure", "Refusing to read a file that is not owner-only.");
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return PlatformResult<byte[]?>.Success(buffer.ToArray());
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PlatformResult<byte[]?>.Success(null);
        }
        catch (Exception exception) when (UnixPrivateFilePermissions.IsExpectedFileFailure(exception))
        {
            return PlatformResult<byte[]?>.Failure("private_file_read_failed", "The private file could not be read.");
        }
    }

    public PlatformResult<string?> ReadAllText(string path)
    {
        var result = ReadAllBytes(path);
        if (result.IsFailure)
        {
            return PlatformResult<string?>.Failure(result.Error!.Code, result.Error.Message);
        }

        if (result.Value is null)
        {
            return PlatformResult<string?>.Success(null);
        }

        try
        {
            return PlatformResult<string?>.Success(StrictUtf8.GetString(result.Value));
        }
        catch (DecoderFallbackException)
        {
            return PlatformResult<string?>.Failure("private_file_invalid_utf8", "The private file is not valid UTF-8.");
        }
    }

    public PlatformResult Delete(string path)
    {
        var validation = UnixPrivateFilePermissions.ValidatePath(path);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            File.Delete(path);
            return PlatformResult.Success();
        }
        catch (Exception exception) when (UnixPrivateFilePermissions.IsExpectedFileFailure(exception))
        {
            return PlatformResult.Failure("private_file_delete_failed", "The private file could not be deleted.");
        }
    }

    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) =>
        _permissions.IsRestrictedToCurrentUser(path);
}

internal interface IAtomicReplace
{
    void Replace(string temporaryPath, string targetPath);
    void TryDelete(string path);
}

internal sealed class AtomicReplace : IAtomicReplace
{
    public void Replace(string temporaryPath, string targetPath) =>
        File.Move(temporaryPath, targetPath, overwrite: true);

    public void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (UnixPrivateFilePermissions.IsExpectedFileFailure(exception))
        {
        }
    }
}
