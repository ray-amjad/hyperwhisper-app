using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// Writes %LOCALAPPDATA%\HyperWhisper\local-api.json so MCP wrappers and CLI
/// scripts on the same machine can discover the port and bearer token without
/// the user copy-pasting them anywhere. NTFS ACL is locked down to the
/// running user — Windows equivalent of macOS's `chmod 600`.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LocalApiDiscoveryFile
{
    public static string FilePath { get; } = Path.Combine(
        AppPaths.AppDataRoot,
        "local-api.json");

    public static string? Write(int port, string token, string appVersion)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new LocalApiPortFile
            {
                Port = port,
                Pid = Environment.ProcessId,
                StartedAt = DateTime.UtcNow.ToString("o"),
                ApiVersion = LocalApiVersion.Current,
                AppVersion = appVersion,
                Token = token
            };

            var json = JsonSerializer.Serialize(payload, LocalApiResponder.JsonOptions);
            // Defend against a pre-existing read-only discovery file: WriteAllText
            // throws UnauthorizedAccessException on a file with the read-only
            // attribute set (Windows analogue of macOS's `uchg`). We never set it
            // ourselves, but backup/restore and sync tools can stamp it on; without
            // this we'd keep publishing the previous launch's now-dead port/token.
            AppPaths.ClearReadOnlyAttribute(FilePath, "LocalApiDiscoveryFile");
            File.WriteAllText(FilePath, json);
            return ApplyOwnerOnlyAcl(FilePath);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiDiscoveryFile: write failed: {ex.Message}");
            DeleteExistingDiscoveryFileIfStale(port, token);
            return $"Local API is running, but the discovery file could not be written: {ex.Message}";
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // Clear read-only first — File.Delete throws on a read-only file.
                AppPaths.ClearReadOnlyAttribute(FilePath, "LocalApiDiscoveryFile");
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiDiscoveryFile: delete failed: {ex.Message}");
        }
    }

    private static void DeleteExistingDiscoveryFileIfStale(int expectedPort, string expectedToken)
    {
        if (!ExistingDiscoveryFileIsStale(expectedPort, expectedToken))
        {
            LoggingService.Warn("LocalApiDiscoveryFile: leaving existing discovery file after write failure because it still matches this server");
            return;
        }

        Delete();
        if (File.Exists(FilePath))
        {
            LoggingService.Warn("LocalApiDiscoveryFile: stale discovery file remains after cleanup attempt");
        }
    }

    private static bool ExistingDiscoveryFileIsStale(int expectedPort, string expectedToken)
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var existing = JsonSerializer.Deserialize<LocalApiPortFile>(json, LocalApiResponder.JsonOptions);
            if (existing == null)
            {
                LoggingService.Warn("LocalApiDiscoveryFile: existing discovery file is invalid; treating it as stale");
                return true;
            }

            return existing.Port != expectedPort
                || existing.Pid != Environment.ProcessId
                || existing.Token != expectedToken;
        }
        catch (JsonException ex)
        {
            LoggingService.Warn($"LocalApiDiscoveryFile: existing discovery file is invalid; treating it as stale: {ex.Message}");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiDiscoveryFile: could not inspect existing discovery file; leaving it in place: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Replace the inherited ACL on the discovery file with one that grants
    /// FullControl only to the current user. A second account reading this
    /// file must get Access Denied. Tested with `icacls` after Write().
    /// </summary>
    private static string? ApplyOwnerOnlyAcl(string path)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null)
            {
                LoggingService.Warn("LocalApiDiscoveryFile: WindowsIdentity has no SID; leaving default ACL");
                return "Local API discovery file was written, but permissions could not be restricted because the current Windows user SID was unavailable.";
            }

            var info = new FileInfo(path);
            var security = info.GetAccessControl();

            // Drop inheritance and clear inherited rules outright.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRuleAll(rule);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            info.SetAccessControl(security);
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiDiscoveryFile: ACL hardening failed: {ex.Message}");
            return $"Local API discovery file was written, but permissions could not be restricted: {ex.Message}";
        }
    }
}
