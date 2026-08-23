using System.IO;

namespace HyperWhisper.Platform.Abstractions;

/// <summary>
/// Returns the canonical path represented by an already-open file. Resolving the
/// open handle, rather than a user-supplied string, prevents path-swap races.
/// The caller retains ownership of the supplied stream.
/// </summary>
public interface IFileCanonicalizer
{
    PlatformResult<string> GetCanonicalPath(FileStream openFile);
}

/// <summary>Applies and verifies current-user-only access to a file.</summary>
public interface IPrivateFilePermissions
{
    PlatformResult RestrictToCurrentUser(string path);
    PlatformResult<bool> IsRestrictedToCurrentUser(string path);
}

/// <summary>
/// Creates or replaces sensitive files without a window in which another user
/// can read permissive default permissions. Implementations use an owner-only
/// temporary file and an atomic replacement where the platform supports it.
/// </summary>
public interface IPrivateFileService
{
    PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents);
    PlatformResult WriteAllTextAtomically(string path, string contents);
    PlatformResult<byte[]?> ReadAllBytes(string path);
    PlatformResult<string?> ReadAllText(string path);
    PlatformResult Delete(string path);
    PlatformResult<bool> IsRestrictedToCurrentUser(string path);
}

/// <summary>
/// Stores small secrets at current-user scope. Implementations must use an OS
/// credential facility or owner-only private storage; errors must not include values.
/// </summary>
public interface ICredentialStore
{
    PlatformResult<byte[]?> Read(string resource, string account);
    PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value);
    PlatformResult Delete(string resource, string account);
}

public enum DeviceIdentitySource
{
    Unknown,
    PlatformMachineId,
    StoredFallback,
    GeneratedFallback
}

public sealed record DeviceIdentity(string Id, DeviceIdentitySource Source);

/// <summary>
/// Supplies the already privacy-preserving identifier used for device binding.
/// Raw machine identifiers must never cross this interface.
/// </summary>
public interface IDeviceIdentityProvider
{
    PlatformResult<DeviceIdentity> GetDeviceIdentity();
}

public interface IAppPaths
{
    string DataDirectory { get; }
    string ConfigDirectory { get; }
    string CacheDirectory { get; }
    string StateDirectory { get; }
    string LogsDirectory { get; }
    string ModelsDirectory { get; }
    string RecordingsDirectory { get; }
    string RuntimeDirectory { get; }
    string TemporaryDirectory { get; }
}
