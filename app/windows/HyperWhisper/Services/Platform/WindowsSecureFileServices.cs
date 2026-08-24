using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

public sealed class WindowsFileCanonicalizer : PlatformContracts.IFileCanonicalizer
{
    private const uint FileNameNormalized = 0;
    private const uint VolumeNameDos = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    public PlatformContracts.PlatformResult<string> GetCanonicalPath(FileStream openFile)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        if (openFile.SafeFileHandle.IsInvalid || openFile.SafeFileHandle.IsClosed)
            return PlatformContracts.PlatformResult<string>.Failure(
                "file_canonicalizer.invalid_handle", "The supplied file handle is not open.");

        try
        {
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(
                openFile.SafeFileHandle, buffer, (uint)buffer.Capacity, FileNameNormalized | VolumeNameDos);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)length + 1));
                length = GetFinalPathNameByHandle(
                    openFile.SafeFileHandle, buffer, (uint)buffer.Capacity, FileNameNormalized | VolumeNameDos);
                if (length == 0 || length >= buffer.Capacity)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return PlatformContracts.PlatformResult<string>.Success(
                Path.GetFullPath(StripExtendedPrefix(buffer.ToString())));
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsFileCanonicalizer: handle resolution failed", ex);
            return PlatformContracts.PlatformResult<string>.Failure(
                "file_canonicalizer.failed", "Windows could not resolve the open file handle.");
        }
    }

    private static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string prefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
    }
}

public sealed class WindowsPrivateFilePermissions : PlatformContracts.IPrivateFilePermissions
{
    public PlatformContracts.PlatformResult RestrictToCurrentUser(string path)
    {
        ValidatePath(path);
        try
        {
            var security = CreateOwnerOnlySecurity();
            new FileInfo(path).SetAccessControl(security);
            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsPrivateFilePermissions: ACL restriction failed", ex);
            return PlatformContracts.PlatformResult.Failure(
                "private_file.permissions_failed", "Windows could not restrict the file to the current user.");
        }
    }

    public PlatformContracts.PlatformResult<bool> IsRestrictedToCurrentUser(string path)
    {
        ValidatePath(path);
        try
        {
            if (!File.Exists(path))
                return PlatformContracts.PlatformResult<bool>.Failure("private_file.not_found", "The private file does not exist.");

            var currentUser = GetCurrentUser();
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            if (!security.AreAccessRulesProtected)
                return PlatformContracts.PlatformResult<bool>.Success(false);

            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            var hasCurrentUserAllow = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                    continue;
                if (rule.IdentityReference is not SecurityIdentifier sid || sid != currentUser)
                    return PlatformContracts.PlatformResult<bool>.Success(false);
                hasCurrentUserAllow |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            return PlatformContracts.PlatformResult<bool>.Success(hasCurrentUserAllow);
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsPrivateFilePermissions: ACL verification failed", ex);
            return PlatformContracts.PlatformResult<bool>.Failure(
                "private_file.permissions_check_failed", "Windows could not verify the file permissions.");
        }
    }

    internal static FileSecurity CreateOwnerOnlySecurity()
    {
        var user = GetCurrentUser();
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static SecurityIdentifier GetCurrentUser()
        => WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A file path is required.", nameof(path));
    }
}

public sealed class WindowsPrivateFileService : PlatformContracts.IPrivateFileService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly WindowsPrivateFilePermissions _permissions = new();

    public PlatformContracts.PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        WindowsPrivateFilePermissions.ValidatePath(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("The file path must have a parent directory.", nameof(path));

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var info = new FileInfo(tempPath);
            using (var stream = info.Create(
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                4096,
                FileOptions.WriteThrough,
                WindowsPrivateFilePermissions.CreateOwnerOnlySecurity()))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                // Harden the destination before replacing its contents. Windows
                // replacement can preserve destination metadata, so this closes
                // the only possible exposure window for a previously permissive file.
                var restricted = _permissions.RestrictToCurrentUser(fullPath);
                if (restricted.IsFailure)
                    return restricted;
                File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            }
            else
                File.Move(tempPath, fullPath);

            var check = _permissions.IsRestrictedToCurrentUser(fullPath);
            return check.IsSuccess && check.Value
                ? PlatformContracts.PlatformResult.Success()
                : PlatformContracts.PlatformResult.Failure(
                    "private_file.permissions_verification_failed", "The private file was written but its owner-only permissions could not be verified.");
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsPrivateFileService: atomic write failed", ex);
            return PlatformContracts.PlatformResult.Failure(
                "private_file.write_failed", "Windows could not atomically write the private file.");
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) { LoggingService.Warn($"WindowsPrivateFileService: temporary-file cleanup failed: {ex.Message}"); }
        }
    }

    public PlatformContracts.PlatformResult WriteAllTextAtomically(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return WriteAllBytesAtomically(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
    }

    public PlatformContracts.PlatformResult<byte[]?> ReadAllBytes(string path)
        => ReadPrivate(path, File.ReadAllBytes);

    public PlatformContracts.PlatformResult<string?> ReadAllText(string path)
        => ReadPrivate(path, value => File.ReadAllText(value, StrictUtf8));

    public PlatformContracts.PlatformResult Delete(string path)
    {
        WindowsPrivateFilePermissions.ValidatePath(path);
        try
        {
            File.Delete(path);
            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsPrivateFileService: delete failed", ex);
            return PlatformContracts.PlatformResult.Failure("private_file.delete_failed", "Windows could not delete the private file.");
        }
    }

    public PlatformContracts.PlatformResult<bool> IsRestrictedToCurrentUser(string path)
        => _permissions.IsRestrictedToCurrentUser(path);

    private PlatformContracts.PlatformResult<T?> ReadPrivate<T>(string path, Func<string, T> reader)
    {
        WindowsPrivateFilePermissions.ValidatePath(path);
        try
        {
            if (!File.Exists(path))
                return PlatformContracts.PlatformResult<T?>.Success(default);

            var restricted = _permissions.IsRestrictedToCurrentUser(path);
            if (restricted.IsFailure)
                return PlatformContracts.PlatformResult<T?>.Failure(
                    restricted.Error!.Code, restricted.Error.Message);
            if (!restricted.Value)
                return PlatformContracts.PlatformResult<T?>.Failure(
                    "private_file.insecure", "Refusing to read a file that is not restricted to the current user.");

            return PlatformContracts.PlatformResult<T?>.Success(reader(path));
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsPrivateFileService: read failed", ex);
            return PlatformContracts.PlatformResult<T?>.Failure("private_file.read_failed", "Windows could not read the private file.");
        }
    }
}
