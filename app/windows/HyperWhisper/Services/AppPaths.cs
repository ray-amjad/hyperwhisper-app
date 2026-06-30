using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HyperWhisper.Services;

/// <summary>
/// Centralizes HyperWhisper's Windows app-data paths.
/// </summary>
public static class AppPaths
{
    public const string AppDataRootOverrideEnvironmentVariable = "HYPERWHISPER_WINDOWS_APPDATA_ROOT";
    private const string ProductionCredentialResource = "HyperWhisper";

    public static bool IsAppDataRootOverridden =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataRootOverrideEnvironmentVariable));

    /// <summary>
    /// Root for persistent app data. Production uses %LOCALAPPDATA%\HyperWhisper.
    /// Verification harnesses may set HYPERWHISPER_WINDOWS_APPDATA_ROOT before
    /// process start to point the app at a disposable profile.
    /// </summary>
    public static string AppDataRoot
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable(AppDataRootOverrideEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideRoot));
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HyperWhisper");
        }
    }

    public static string LogsDirectory => Path.Combine(AppDataRoot, "Logs");

    public static string ModelsDirectory => Path.Combine(AppDataRoot, "Models");

    public static string LegacyAudioDirectory => Path.Combine(AppDataRoot, "Audio");

    public static string ProfileRecordingsDirectory => Path.Combine(AppDataRoot, "recordings");

    public static string ProfileDownloadsRecordingsDirectory => Path.Combine(AppDataRoot, "Downloads", "HyperWhisper", "recordings");

    public static string ProfileTempRecordingsDirectory => Path.Combine(AppDataRoot, "Temp", "HyperWhisper", "recordings");

    public static string CredentialResource
    {
        get
        {
            if (!IsAppDataRootOverridden)
            {
                return ProductionCredentialResource;
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppDataRoot))).ToLowerInvariant();
            return $"{ProductionCredentialResource}.Test.{hash[..16]}";
        }
    }

    public static string Combine(params string[] segments)
    {
        return Path.Combine(new[] { AppDataRoot }.Concat(segments).ToArray());
    }

    /// <summary>
    /// Best-effort clear of the read-only file attribute on an existing file.
    /// External actors (backup/restore, sync utilities, some security software)
    /// can stamp it on; callers use this before replacing or deleting app-owned
    /// state files so stale discovery/token files do not survive indefinitely.
    /// </summary>
    public static void ClearReadOnlyAttribute(string path, string logContext)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"{logContext}: could not clear read-only attribute: {ex.Message}");
        }
    }
}
