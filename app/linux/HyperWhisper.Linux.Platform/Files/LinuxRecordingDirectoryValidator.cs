using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Files;

/// <summary>Validates a user-selected recording root without exposing file names or contents.</summary>
public static class LinuxRecordingDirectoryValidator
{
    public static PlatformResult<string> ValidateAndPrepare(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return PlatformResult<string>.Failure("storage.path_relative", "Choose an absolute recordings directory.");

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return PlatformResult<string>.Failure("storage.path_invalid", "The recordings directory path is invalid.");
        }
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.Ordinal))
            return PlatformResult<string>.Failure("storage.path_root", "The filesystem root cannot be used for recordings.");

        var probe = Path.Combine(fullPath, $".hyperwhisper-write-{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(fullPath)
                && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return PlatformResult<string>.Failure("storage.path_symlink", "Choose a recordings directory that is not a symbolic link.");

            Directory.CreateDirectory(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var stream = new FileStream(probe, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
                stream.WriteByte(0);
            File.Delete(probe);
            return PlatformResult<string>.Success(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            return PlatformResult<string>.Failure("storage.path_unwritable", "The recordings directory is not privately writable by this user.");
        }
    }
}
