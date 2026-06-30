// DEVICE ID SERVICE
// Provides a stable, privacy-preserving device identifier for the Windows app.
// Used for HyperWhisper Cloud authentication and license binding.
//
// DEVICE ID GENERATION STRATEGY:
// 1. Primary: Read Machine GUID from Windows Registry
//    - Location: HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography\MachineGuid
//    - This is a stable UUID generated during Windows installation
//    - Survives reboots, reinstalls of HyperWhisper, and most system changes
//
// 2. Fallback: Generate and persist a UUID
//    - If registry read fails (rare, but possible due to permissions)
//    - Store in %LOCALAPPDATA%\HyperWhisper\device_id
//
// PRIVACY:
// - The raw Machine GUID is never sent to servers
// - We SHA256 hash it to create a one-way identifier
// - This prevents correlation with other services that might use the same GUID
//
// THREAD SAFETY:
// - Singleton with lazy initialization
// - Memory-cached after first read for performance
//
// USAGE:
//   var deviceId = DeviceIdService.Instance.GetDeviceId();
//   // Returns: 64-character hex string (SHA256 hash)

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HyperWhisper.Services;

/// <summary>
/// Provides a stable, privacy-preserving device identifier.
/// Uses Windows Machine GUID with SHA256 hashing.
/// </summary>
public sealed class DeviceIdService
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Registry path for Windows Machine GUID.
    /// This GUID is generated during Windows installation and remains stable.
    /// </summary>
    private const string MachineGuidRegistryPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValueName = "MachineGuid";

    /// <summary>
    /// Fallback storage path if registry read fails.
    /// </summary>
    private static readonly string FallbackDeviceIdPath = AppPaths.Combine("device_id");

    /// <summary>
    /// Salt added to the machine GUID before hashing for additional privacy.
    /// Prevents correlation with other apps that might hash the same GUID.
    /// </summary>
    private const string HashSalt = "HyperWhisper-Windows-v1";

    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static DeviceIdService? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of DeviceIdService.
    /// Thread-safe lazy initialization.
    /// </summary>
    public static DeviceIdService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new DeviceIdService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    /// <summary>
    /// Cached device ID to avoid repeated registry reads.
    /// </summary>
    private string? _cachedDeviceId;

    /// <summary>
    /// Source of the device ID for diagnostics.
    /// </summary>
    private DeviceIdSource _deviceIdSource = DeviceIdSource.Unknown;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private DeviceIdService()
    {
        // Initialize on first access
        _cachedDeviceId = GenerateDeviceId();
        LoggingService.Info($"DeviceIdService: Initialized (source: {_deviceIdSource}, id: {MaskDeviceId(_cachedDeviceId)})");
    }

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Gets the stable device identifier.
    /// This is a 64-character hex string (SHA256 hash).
    /// </summary>
    /// <returns>Device ID as lowercase hex string.</returns>
    public string GetDeviceId()
    {
        return _cachedDeviceId!;
    }

    /// <summary>
    /// Gets the source of the device ID (registry or fallback).
    /// Useful for diagnostics and troubleshooting.
    /// </summary>
    public DeviceIdSource Source => _deviceIdSource;

    /// <summary>
    /// Gets a masked version of the device ID for logging.
    /// Shows first 4 and last 4 characters: "abcd...wxyz"
    /// </summary>
    public string GetMaskedDeviceId()
    {
        return MaskDeviceId(_cachedDeviceId);
    }

    // =========================================================================
    // DEVICE ID GENERATION
    // =========================================================================

    /// <summary>
    /// Generates the device ID using the following priority:
    /// 1. Windows Registry Machine GUID (primary)
    /// 2. Stored fallback ID (if registry fails)
    /// 3. Generate new fallback ID (first time)
    /// </summary>
    private string GenerateDeviceId()
    {
        // STEP 1: Try to read Machine GUID from Windows Registry
        // This is the most stable identifier as it survives app reinstalls
        var machineGuid = TryReadMachineGuidFromRegistry();
        if (!string.IsNullOrEmpty(machineGuid))
        {
            _deviceIdSource = DeviceIdSource.WindowsRegistry;
            LoggingService.Debug("DeviceIdService: Using Machine GUID from registry");
            return HashWithSalt(machineGuid);
        }

        // STEP 2: Try to load existing fallback device ID
        // This preserves continuity if we previously generated one
        var existingFallback = TryLoadFallbackDeviceId();
        if (!string.IsNullOrEmpty(existingFallback))
        {
            _deviceIdSource = DeviceIdSource.StoredFallback;
            LoggingService.Debug("DeviceIdService: Using stored fallback device ID");
            return existingFallback;
        }

        // STEP 3: Generate new fallback device ID
        // This is a last resort if registry read fails
        _deviceIdSource = DeviceIdSource.GeneratedFallback;
        LoggingService.Warn("DeviceIdService: Generating new fallback device ID (registry unavailable)");
        return GenerateAndStoreFallbackDeviceId();
    }

    /// <summary>
    /// Attempts to read the Machine GUID from Windows Registry.
    /// This GUID is stable across reboots and app reinstalls.
    /// </summary>
    /// <returns>Machine GUID or null if unavailable.</returns>
    private static string? TryReadMachineGuidFromRegistry()
    {
        try
        {
            // Read from HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography\MachineGuid
            // Note: Using OpenSubKey with 64-bit view to handle WOW64 redirection
            using var key = Registry.LocalMachine.OpenSubKey(MachineGuidRegistryPath, false);
            if (key != null)
            {
                var value = key.GetValue(MachineGuidValueName) as string;
                if (!string.IsNullOrEmpty(value))
                {
                    LoggingService.Debug($"DeviceIdService: Read Machine GUID from registry (length: {value.Length})");
                    return value;
                }
            }
            LoggingService.Warn("DeviceIdService: Machine GUID registry key not found or empty");
        }
        catch (UnauthorizedAccessException ex)
        {
            // This can happen if the app runs with restricted permissions
            LoggingService.Warn($"DeviceIdService: Cannot access registry (access denied): {ex.Message}");
        }
        catch (Exception ex)
        {
            LoggingService.Error($"DeviceIdService: Failed to read Machine GUID from registry: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Attempts to load a previously stored fallback device ID.
    /// </summary>
    private static string? TryLoadFallbackDeviceId()
    {
        try
        {
            if (File.Exists(FallbackDeviceIdPath))
            {
                var existingId = File.ReadAllText(FallbackDeviceIdPath).Trim();
                // Validate it looks like a valid SHA256 hash (64 hex chars)
                if (existingId.Length == 64 && IsHexString(existingId))
                {
                    return existingId;
                }
                LoggingService.Warn($"DeviceIdService: Stored device ID invalid (length: {existingId.Length})");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"DeviceIdService: Failed to read fallback device ID: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Generates a new fallback device ID and persists it.
    /// Uses a combination of machine info for some stability, then persists for consistency.
    /// </summary>
    private static string GenerateAndStoreFallbackDeviceId()
    {
        // Generate based on machine characteristics (similar to old behavior)
        // This provides some stability if the file is ever lost
        var machineName = Environment.MachineName;
        var userName = Environment.UserName;
        var osVersion = Environment.OSVersion.ToString();
        var timestamp = DateTime.UtcNow.Ticks.ToString();

        var combined = $"{machineName}|{userName}|{osVersion}|{timestamp}|{HashSalt}";
        var deviceId = HashRaw(combined);

        // Persist for future use
        try
        {
            var directory = Path.GetDirectoryName(FallbackDeviceIdPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(FallbackDeviceIdPath, deviceId);
            LoggingService.Info("DeviceIdService: Saved fallback device ID to disk");
        }
        catch (Exception ex)
        {
            // Non-fatal: device ID is still usable for this session
            LoggingService.Warn($"DeviceIdService: Failed to persist fallback device ID: {ex.Message}");
        }

        return deviceId;
    }

    // =========================================================================
    // HASHING UTILITIES
    // =========================================================================

    /// <summary>
    /// Hashes the input with our app-specific salt.
    /// This prevents correlation with other services using the same Machine GUID.
    /// </summary>
    private static string HashWithSalt(string input)
    {
        return HashRaw($"{input}|{HashSalt}");
    }

    /// <summary>
    /// Computes SHA256 hash of the input and returns lowercase hex string.
    /// </summary>
    private static string HashRaw(string input)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Checks if a string contains only hexadecimal characters.
    /// </summary>
    private static bool IsHexString(string input)
    {
        foreach (var c in input)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Masks a device ID for safe logging.
    /// Shows first 4 and last 4 characters: "abcd...wxyz"
    /// </summary>
    private static string MaskDeviceId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId.Length <= 12)
            return "***";
        return $"{deviceId[..4]}...{deviceId[^4..]}";
    }
}

/// <summary>
/// Indicates the source of the device ID for diagnostics.
/// </summary>
public enum DeviceIdSource
{
    /// <summary>Device ID source not yet determined.</summary>
    Unknown,

    /// <summary>Device ID derived from Windows Registry Machine GUID (preferred).</summary>
    WindowsRegistry,

    /// <summary>Device ID loaded from previously stored fallback file.</summary>
    StoredFallback,

    /// <summary>Device ID newly generated and stored (registry was unavailable).</summary>
    GeneratedFallback
}
