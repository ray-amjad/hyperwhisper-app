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

        // Each step is tried on its own: a folder that cannot be chmodded (owned by root after a
        // sudo run) must not stop the user's own database file from being repaired.
        var failed = false;
        var directory = Path.GetDirectoryName(databasePath)!;
        failed |= !TryApply(() =>
        {
            // Only the app's own folder is made private: a missing parent such as ~/.local/state is
            // shared by every XDG app and keeps the umask mode.
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            Directory.CreateDirectory(directory, PrivateDirectoryMode);
            File.SetUnixFileMode(directory, PrivateDirectoryMode);
        });

        // An empty file is a valid empty SQLite database, and it is what SQLite would make here
        // anyway (StaleMigrationLock opens the connection before EF's Create, so the store is in
        // rollback-journal mode either way). Creating it at 0600 means it is never readable by
        // others, and SQLite gives the -journal, -wal and -shm it makes the database's mode.
        // A file that appears in between is not a failure: the loop below chmods it.
        failed |= !TryApply(() =>
        {
            try
            {
                using var _ = new FileStream(databasePath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixPrivateFilePermissions.PrivateMode,
                });
            }
            catch (IOException) when (File.Exists(databasePath))
            {
            }
        });

        foreach (var path in SidecarSuffixes.Select(suffix => databasePath + suffix).Prepend(databasePath))
        {
            failed |= !TryApply(() =>
            {
                if (File.Exists(path))
                {
                    File.SetUnixFileMode(path, UnixPrivateFilePermissions.PrivateMode);
                }
            });
        }

        return failed
            ? PlatformResult.Failure("permissions_update_failed", "Owner-only database permissions could not be applied.")
            : PlatformResult.Success();
    }

    private static bool TryApply(Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (Exception exception) when (UnixPrivateFilePermissions.IsExpectedFileFailure(exception))
        {
            return false;
        }
    }
}
