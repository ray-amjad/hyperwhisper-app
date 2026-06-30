using System;
using System.Windows;
using Microsoft.Win32;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// THEME SERVICE
///
/// Manages application theming (light/dark mode) for HyperWhisper Windows app.
///
/// RESPONSIBILITIES:
/// 1. Detect Windows system theme (light/dark) from registry
/// 2. Apply the appropriate theme based on user setting (System/Light/Dark)
/// 3. Listen for system theme changes when in System mode
/// 4. Swap ResourceDictionaries at runtime for seamless theme switching
///
/// HOW THEME SWITCHING WORKS:
/// 1. Theme colors are defined in LightColors.xaml and DarkColors.xaml
/// 2. Brushes.xaml uses DynamicResource to reference these colors
/// 3. When theme changes, we swap the color ResourceDictionary in App.Resources
/// 4. All UI elements using DynamicResource update automatically
///
/// SYSTEM THEME DETECTION:
/// Windows stores the app theme preference in the registry at:
/// HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
/// Key: AppsUseLightTheme (0 = dark, 1 = light)
/// </summary>
public class ThemeService
{
    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static ThemeService? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of ThemeService.
    /// </summary>
    public static ThemeService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new ThemeService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Registry path where Windows stores theme preferences.
    /// </summary>
    private const string ThemeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Registry key for app theme setting.
    /// 0 = Dark mode, 1 = Light mode
    /// </summary>
    private const string AppsUseLightThemeKey = "AppsUseLightTheme";

    /// <summary>
    /// URI for the light theme color ResourceDictionary.
    /// </summary>
    private static readonly Uri LightThemeUri = new("pack://application:,,,/Themes/LightColors.xaml");

    /// <summary>
    /// URI for the dark theme color ResourceDictionary.
    /// </summary>
    private static readonly Uri DarkThemeUri = new("pack://application:,,,/Themes/DarkColors.xaml");

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>
    /// Raised when the actual applied theme changes (not the setting, but the visual theme).
    /// This occurs when:
    /// - User changes theme setting
    /// - System theme changes while in System mode
    /// </summary>
    public event EventHandler<bool>? ThemeChanged;

    // =========================================================================
    // PROPERTIES
    // =========================================================================

    /// <summary>
    /// Gets whether dark mode is currently active.
    /// </summary>
    public bool IsDarkMode { get; private set; }

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private ThemeService()
    {
        // Subscribe to system theme changes via SystemEvents
        // This fires when Windows theme changes in Settings
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Initializes the theme service and applies the initial theme.
    /// Call this during app startup after SettingsService is initialized.
    ///
    /// STARTUP FLOW:
    /// 1. Read user's theme preference from SettingsService
    /// 2. Determine actual theme to apply (considering System mode)
    /// 3. Load and apply the appropriate color ResourceDictionary
    /// </summary>
    public void Initialize()
    {
        LoggingService.Info("ThemeService: Initializing...");
        ApplyTheme(SettingsService.Instance.ThemeMode);
        LoggingService.Info($"ThemeService: Initialized with {(IsDarkMode ? "dark" : "light")} theme");
    }

    /// <summary>
    /// Applies the specified theme mode.
    ///
    /// THEME RESOLUTION:
    /// - System: Check Windows registry for current system theme
    /// - Light: Always use light theme
    /// - Dark: Always use dark theme
    /// </summary>
    /// <param name="mode">The theme mode to apply</param>
    public void ApplyTheme(Models.ThemeMode mode)
    {
        bool shouldBeDark = mode switch
        {
            Models.ThemeMode.Light => false,
            Models.ThemeMode.Dark => true,
            Models.ThemeMode.System => IsSystemDarkMode(),
            _ => false
        };

        ApplyDarkMode(shouldBeDark);
    }

    /// <summary>
    /// Gets whether the Windows system is currently in dark mode.
    ///
    /// REGISTRY CHECK:
    /// Reads HKCU\...\Themes\Personalize\AppsUseLightTheme
    /// - 0 = Dark mode
    /// - 1 = Light mode (default if key doesn't exist)
    /// </summary>
    /// <returns>True if system is in dark mode, false otherwise</returns>
    public bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemeRegistryPath);
            if (key != null)
            {
                var value = key.GetValue(AppsUseLightThemeKey);
                if (value is int intValue)
                {
                    // 0 = dark mode, 1 = light mode
                    return intValue == 0;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ThemeService: Failed to read system theme from registry: {ex.Message}");
        }

        // Default to light mode if we can't read the registry
        return false;
    }

    // =========================================================================
    // PRIVATE METHODS
    // =========================================================================

    /// <summary>
    /// Applies dark or light mode by swapping the color ResourceDictionary.
    ///
    /// RESOURCE DICTIONARY SWAPPING:
    /// 1. Find the current color dictionary in App.Resources
    /// 2. Remove it
    /// 3. Add the new color dictionary
    /// 4. WPF automatically updates all DynamicResource bindings
    /// </summary>
    /// <param name="dark">True to apply dark mode, false for light mode</param>
    private void ApplyDarkMode(bool dark)
    {
        if (IsDarkMode == dark && WpfApplication.Current?.Resources != null)
        {
            // Theme is already applied, no need to swap
            // (unless this is initialization)
            var hasThemeLoaded = FindColorDictionary() != null;
            if (hasThemeLoaded)
            {
                return;
            }
        }

        IsDarkMode = dark;

        try
        {
            var app = WpfApplication.Current;
            if (app == null)
            {
                LoggingService.Warn("ThemeService: WpfApplication.Current is null, cannot apply theme");
                return;
            }

            // Find and remove the current color dictionary
            var currentColorDict = FindColorDictionary();
            if (currentColorDict != null)
            {
                app.Resources.MergedDictionaries.Remove(currentColorDict);
            }

            // Load and insert the new color dictionary at the beginning
            // (so it's available before Brushes.xaml references it)
            var newColorDict = new ResourceDictionary
            {
                Source = dark ? DarkThemeUri : LightThemeUri
            };
            app.Resources.MergedDictionaries.Insert(0, newColorDict);

            LoggingService.Info($"ThemeService: Applied {(dark ? "dark" : "light")} theme");
            ThemeChanged?.Invoke(this, dark);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"ThemeService: Failed to apply theme: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the color ResourceDictionary in App.Resources.
    /// Identifies it by checking if the Source URI contains "Colors.xaml".
    /// </summary>
    /// <returns>The color dictionary, or null if not found</returns>
    private ResourceDictionary? FindColorDictionary()
    {
        var app = WpfApplication.Current;
        if (app?.Resources?.MergedDictionaries == null)
        {
            return null;
        }

        foreach (var dict in app.Resources.MergedDictionaries)
        {
            if (dict.Source?.ToString().Contains("Colors.xaml") == true)
            {
                return dict;
            }
        }

        return null;
    }

    /// <summary>
    /// Handles Windows user preference changes (including theme changes).
    ///
    /// SYSTEM THEME CHANGE FLOW:
    /// 1. User changes Windows theme in Settings
    /// 2. SystemEvents.UserPreferenceChanged fires
    /// 3. If we're in System mode, re-apply theme based on new system setting
    /// </summary>
    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Only respond to General category changes (which includes theme)
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        // Only update if we're in System mode
        if (SettingsService.Instance.ThemeMode == Models.ThemeMode.System)
        {
            LoggingService.Debug("ThemeService: System theme changed, updating...");

            // Must dispatch to UI thread for WPF resource changes
            WpfApplication.Current?.Dispatcher.Invoke(() =>
            {
                ApplyTheme(Models.ThemeMode.System);
            });
        }
    }

    // =========================================================================
    // CLEANUP
    // =========================================================================

    /// <summary>
    /// Unsubscribes from system events.
    /// Call this during app shutdown.
    /// </summary>
    public void Shutdown()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
    }
}
