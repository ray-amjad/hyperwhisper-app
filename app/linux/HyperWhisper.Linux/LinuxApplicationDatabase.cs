using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.Linux;

/// <summary>The Linux head's transcript store, owner-only before SQLite first touches it (#2573).</summary>
internal static class LinuxApplicationDatabase
{
    public static ApplicationDb Open(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // A chmod that fails is a warning, never a reason to refuse to start.
        var secured = LinuxDatabaseFilePermissions.Secure(Path.Combine(paths.StateDirectory, "hyperwhisper.db"));
        if (secured.IsFailure)
        {
            try { Console.Error.WriteLine($"HyperWhisper storage: the transcript database could not be made owner-only ({secured.Error!.Code})."); } catch { }
        }
        return new ApplicationDb(paths);
    }
}
