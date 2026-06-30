using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace HyperWhisper.Services;

/// <summary>
/// STARTUP SERVICE
///
/// Manages Windows startup registration for HyperWhisper using the Registry Run key.
/// This allows the app to launch automatically when the user logs in to Windows.
///
/// REGISTRY LOCATION:
/// HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
///
/// WHY HKCU INSTEAD OF HKLM:
/// - HKCU doesn't require admin privileges
/// - Per-user setting (appropriate for a desktop app)
/// - Survives user profile roaming
///
/// ALTERNATIVE APPROACHES NOT USED:
/// - Task Scheduler: More complex, overkill for simple startup
/// - Startup folder shortcut: Less reliable, can be cleared by cleanup tools
/// - HKLM Run key: Requires admin, affects all users
/// </summary>
public class StartupService
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Registry path for user-specific startup programs.
    /// Programs listed here run when the current user logs in.
    /// </summary>
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// The name used for the registry value.
    /// This appears in Task Manager's Startup tab.
    /// </summary>
    private const string AppName = "HyperWhisper";

    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static StartupService? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of StartupService.
    /// Thread-safe lazy initialization.
    /// </summary>
    public static StartupService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new StartupService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private StartupService()
    {
        // Private constructor for singleton pattern
    }

    // =========================================================================
    // PUBLIC PROPERTIES
    // =========================================================================

    /// <summary>
    /// Gets whether the app is currently registered to start with Windows.
    /// Reads directly from the registry to ensure accuracy.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                if (key == null)
                {
                    return false;
                }

                var value = key.GetValue(AppName);
                return value != null;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"StartupService: Failed to read registry: {ex.Message}");
                return false;
            }
        }
    }

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Enables launch at startup by adding the app to the registry Run key.
    /// </summary>
    /// <returns>True if successful, false otherwise.</returns>
    public bool Enable()
    {
        try
        {
            // Get the path to the current executable
            var exePath = GetExecutablePath();
            if (string.IsNullOrEmpty(exePath))
            {
                LoggingService.Error("StartupService: Could not determine executable path");
                return false;
            }

            // Open the Run key with write access
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                LoggingService.Error("StartupService: Could not open Run registry key for writing");
                return false;
            }

            // Set the value - path in quotes to handle spaces
            // The value is the full path to the executable, which Windows will run at login
            key.SetValue(AppName, $"\"{exePath}\"");

            LoggingService.Info($"StartupService: Enabled launch at startup: {exePath}");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            LoggingService.Error($"StartupService: Access denied to registry: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"StartupService: Failed to enable startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables launch at startup by removing the app from the registry Run key.
    /// </summary>
    /// <returns>True if successful, false otherwise.</returns>
    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                // Key doesn't exist, nothing to disable
                LoggingService.Debug("StartupService: Run key doesn't exist, nothing to disable");
                return true;
            }

            // Check if our value exists before trying to delete
            if (key.GetValue(AppName) != null)
            {
                key.DeleteValue(AppName, false);
                LoggingService.Info("StartupService: Disabled launch at startup");
            }
            else
            {
                LoggingService.Debug("StartupService: App was not registered for startup");
            }

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            LoggingService.Error($"StartupService: Access denied to registry: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"StartupService: Failed to disable startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the startup state based on a boolean value.
    /// Convenience method for binding to settings.
    /// </summary>
    /// <param name="enabled">True to enable, false to disable.</param>
    /// <returns>True if the operation succeeded.</returns>
    public bool SetEnabled(bool enabled)
    {
        return enabled ? Enable() : Disable();
    }

    // =========================================================================
    // PRIVATE METHODS
    // =========================================================================

    /// <summary>
    /// Gets the full path to the current executable.
    /// </summary>
    private static string? GetExecutablePath()
    {
        try
        {
            // Environment.ProcessPath is the recommended way in .NET 6+
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            // Fallback to Process.GetCurrentProcess().MainModule.FileName
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"StartupService: Error getting executable path: {ex.Message}");
            return null;
        }
    }
}
