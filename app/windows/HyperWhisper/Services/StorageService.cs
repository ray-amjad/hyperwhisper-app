using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace HyperWhisper.Services;

/// <summary>
/// STORAGE SERVICE
///
/// Owns the recordings folder path, validation, fallbacks, and optional M4A compression.
/// Mirrors macOS behavior: prefer Documents for new installs, keep legacy LocalAppData
/// for existing users, and fall back to other writable locations when needed.
/// </summary>
public class StorageService
{
    private static StorageService? _instance;
    private static readonly object _lock = new();
    private readonly SettingsService _settingsService;
    private readonly object _folderLock = new();

    private readonly string _documentsRecordingFolder = AppPaths.IsAppDataRootOverridden
        ? AppPaths.ProfileRecordingsDirectory
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "HyperWhisper",
            "recordings");

    private readonly string _legacyRecordingFolder = SettingsService.GetLegacyAudioFolder();

    private readonly string _downloadsRecordingFolder = AppPaths.IsAppDataRootOverridden
        ? AppPaths.ProfileDownloadsRecordingsDirectory
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "HyperWhisper",
            "recordings");

    private readonly string _tempRecordingFolder = AppPaths.IsAppDataRootOverridden
        ? AppPaths.ProfileTempRecordingsDirectory
        : Path.Combine(
            Path.GetTempPath(),
            "HyperWhisper",
            "recordings");

    public static StorageService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new StorageService();
                }
            }

            return _instance;
        }
    }

    public string? ValidationError { get; private set; }

    public bool StoreAsM4A => _settingsService.StoreAsM4A;

    private StorageService()
    {
        _settingsService = SettingsService.Instance;
        EnsureRecordingsFolder();
    }

    /// <summary>
    /// Returns the active recordings folder, ensuring it exists and is writable.
    /// Falls back to alternate locations if the configured folder is unavailable.
    /// </summary>
    public string GetRecordingsFolder()
    {
        lock (_folderLock)
        {
            return EnsureRecordingsFolder();
        }
    }

    /// <summary>
    /// Attempts to change the recordings folder to a user-selected path.
    /// Validates writability and creates the directory if needed.
    /// </summary>
    public bool TryChangeRecordingsFolder(string newFolder, out string? error)
    {
        lock (_folderLock)
        {
            if (!EnsureFolderWritable(newFolder, create: true, out error))
            {
                ValidationError = error;
                return false;
            }

            _settingsService.RecordingsFolder = newFolder;
            _settingsService.UserChoseAlternateStorage = true;
            ValidationError = null;
            return true;
        }
    }

    /// <summary>
    /// Opens the recordings folder in Explorer, creating it if needed.
    /// </summary>
    public void OpenRecordingsFolder()
    {
        TryOpenRecordingsFolder(out _);
    }

    /// <summary>
    /// Attempts to open the recordings folder in Explorer, creating it if needed.
    /// </summary>
    public bool TryOpenRecordingsFolder(out string? error)
    {
        var path = GetRecordingsFolder();
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"StorageService: Failed to open recordings folder: {ex.Message}");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Converts a WAV recording to M4A (AAC) if enabled in settings.
    /// Keeps the WAV file when conversion fails; deletes it on success.
    /// </summary>
    /// <returns>The M4A path if conversion succeeded, otherwise null.</returns>
    public string? TryConvertWavToM4A(string wavPath)
    {
        if (!_settingsService.StoreAsM4A) return null;
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath)) return null;
        if (!string.Equals(Path.GetExtension(wavPath), ".wav", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var outputPath = Path.ChangeExtension(wavPath, ".m4a");
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            MediaFoundationApi.Startup();
            try
            {
                using var reader = new AudioFileReader(wavPath);
                MediaFoundationEncoder.EncodeToAac(reader, outputPath);
            }
            finally
            {
                MediaFoundationApi.Shutdown();
            }

            try
            {
                File.Delete(wavPath);
            }
            catch (Exception deleteEx)
            {
                LoggingService.Warn($"StorageService: Failed to delete WAV after M4A conversion: {deleteEx.Message}");
            }

            LoggingService.Info($"StorageService: Compressed recording to {outputPath}");
            return outputPath;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"StorageService: M4A conversion failed for {wavPath}: {ex.Message}");
            return null;
        }
    }

    private string EnsureRecordingsFolder()
    {
        var configured = _settingsService.RecordingsFolder;
        if (EnsureFolderWritable(configured, create: true, out var error))
        {
            ValidationError = null;
            return configured;
        }

        ValidationError = error;

        foreach (var candidate in GetFallbackFolders())
        {
            if (EnsureFolderWritable(candidate, create: true, out _))
            {
                LoggingService.Warn($"StorageService: Falling back to {candidate} (reason: {error})");
                _settingsService.RecordingsFolder = candidate;
                _settingsService.UserChoseAlternateStorage = true;
                ValidationError = null;
                return candidate;
            }
        }

        // Last resort: return configured even if not writable so callers can surface the error.
        return configured;
    }

    private IEnumerable<string> GetFallbackFolders()
    {
        yield return _documentsRecordingFolder;      // Preferred safe location
        yield return _legacyRecordingFolder;         // Legacy location from earlier builds
        yield return _downloadsRecordingFolder;      // User-visible fallback
        yield return _tempRecordingFolder;           // Last resort
    }

    private static bool EnsureFolderWritable(string path, bool create, out string? error)
    {
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Folder path is empty.";
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var directory = new DirectoryInfo(fullPath);
            if (create && !directory.Exists)
            {
                directory.Create();
            }

            var testFile = Path.Combine(fullPath, $".hw_write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Cannot access folder: {ex.Message}";
            return false;
        }
    }
}
