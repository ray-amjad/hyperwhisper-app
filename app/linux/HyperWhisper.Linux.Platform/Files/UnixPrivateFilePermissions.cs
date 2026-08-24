using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Files;

public sealed class UnixPrivateFilePermissions : IPrivateFilePermissions
{
    internal const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public PlatformResult RestrictToCurrentUser(string path)
    {
        var validation = ValidatePath(path);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            File.SetUnixFileMode(path, PrivateMode);
            return File.GetUnixFileMode(path) == PrivateMode
                ? PlatformResult.Success()
                : PlatformResult.Failure("permissions_not_private", "The file mode is not owner-only.");
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return PlatformResult.Failure("permissions_update_failed", "Owner-only permissions could not be applied.");
        }
    }

    public PlatformResult<bool> IsRestrictedToCurrentUser(string path)
    {
        var validation = ValidatePath(path);
        if (validation is not null)
        {
            return PlatformResult<bool>.Failure(validation.Error!.Code, validation.Error.Message);
        }

        try
        {
            return PlatformResult<bool>.Success(File.GetUnixFileMode(path) == PrivateMode);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return PlatformResult<bool>.Failure("permissions_read_failed", "The file mode could not be read.");
        }
    }

    internal static PlatformResult? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return PlatformResult.Failure("invalid_path", "A fully qualified file path is required.");
        }

        if (!OperatingSystem.IsLinux())
        {
            return PlatformResult.Failure("platform_unsupported", "Unix file permissions require Linux.");
        }

        return null;
    }

    internal static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException;
}
