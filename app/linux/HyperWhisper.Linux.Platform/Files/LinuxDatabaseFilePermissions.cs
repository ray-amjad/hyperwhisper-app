using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Files;

/// <summary>
/// The transcript store (hyperwhisper.db) is created by SQLite with the process umask, so on a
/// default umask 022 it was 0644 in a 0755 folder while every other private file is owner-only
/// (#2573). This makes its folder 0700 and the database plus any -wal, -shm or -journal 0600.
/// It runs before SQLite first opens the file and again on every launch, which repairs old installs.
/// </summary>
public static class LinuxDatabaseFilePermissions
{
    internal const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    public static PlatformResult Secure(string databasePath)
    {
        var validation = UnixPrivateFilePermissions.ValidatePath(databasePath);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var directory = Path.GetDirectoryName(databasePath)!;
            Directory.CreateDirectory(directory, PrivateDirectoryMode);
            File.SetUnixFileMode(directory, PrivateDirectoryMode);

            // An empty file is a valid empty SQLite database. Creating it here at 0600 means it is
            // never world-readable, and SQLite gives the -wal and -shm it makes the database's mode.
            if (!File.Exists(databasePath))
            {
                using var _ = new FileStream(databasePath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixPrivateFilePermissions.PrivateMode,
                });
            }

            foreach (var path in SidecarSuffixes.Select(suffix => databasePath + suffix).Prepend(databasePath))
            {
                if (File.Exists(path))
                {
                    File.SetUnixFileMode(path, UnixPrivateFilePermissions.PrivateMode);
                }
            }

            return PlatformResult.Success();
        }
        catch (Exception exception) when (UnixPrivateFilePermissions.IsExpectedFileFailure(exception))
        {
            return PlatformResult.Failure("permissions_update_failed", "Owner-only database permissions could not be applied.");
        }
    }
}
