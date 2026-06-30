// APPEARANCE SETTINGS PAGE
// Handles theme mode selection (Light, Dark, System Default) for the application.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HyperWhisper.Localization;
using HyperWhisper.Services;

using ThemeMode = HyperWhisper.Models.ThemeMode;

namespace HyperWhisper.Views.Pages.Settings;

public partial class AppearanceSettingsPage : Page
{
    private bool _isInitializing;
    private ThemeMode _previousThemeMode;

    public AppearanceSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeSettings();
    }

    /// <summary>
    /// Initializes the Appearance section.
    /// Sets the radio button state based on current theme setting.
    /// </summary>
    private void InitializeSettings()
    {
        _isInitializing = true;

        try
        {
            var currentMode = SettingsService.Instance.ThemeMode;
            _previousThemeMode = currentMode;

            switch (currentMode)
            {
                case ThemeMode.System:
                    ThemeSystemRadio.IsChecked = true;
                    break;
                case ThemeMode.Light:
                    ThemeLightRadio.IsChecked = true;
                    break;
                case ThemeMode.Dark:
                    ThemeDarkRadio.IsChecked = true;
                    break;
            }

            LoggingService.Debug($"AppearanceSettingsPage: Initialized with theme mode: {currentMode}");
        }
        finally
        {
            _isInitializing = false;
        }
    }

    // =========================================================================
    // RADIO BUTTON HANDLERS
    // =========================================================================

    private void ThemeSystemRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ApplyThemeMode(ThemeMode.System);
    }

    private void ThemeLightRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ApplyThemeMode(ThemeMode.Light);
    }

    private void ThemeDarkRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ApplyThemeMode(ThemeMode.Dark);
    }

    // =========================================================================
    // BORDER CLICK HANDLERS
    // =========================================================================

    private void ThemeSystem_Click(object sender, MouseButtonEventArgs e)
    {
        ThemeSystemRadio.IsChecked = true;
    }

    private void ThemeLight_Click(object sender, MouseButtonEventArgs e)
    {
        ThemeLightRadio.IsChecked = true;
    }

    private void ThemeDark_Click(object sender, MouseButtonEventArgs e)
    {
        ThemeDarkRadio.IsChecked = true;
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    private void ApplyThemeMode(ThemeMode mode)
    {
        // Don't show dialog if selecting the same theme
        if (mode == _previousThemeMode)
            return;

        LoggingService.Info($"AppearanceSettingsPage: User selected theme mode: {mode}");

        var result = System.Windows.MessageBox.Show(
            Loc.S("settings.appearance.theme.restart.message"),
            Loc.S("settings.appearance.theme.restart.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            // User chose to restart - save the theme and restart
            SettingsService.Instance.ThemeMode = mode;
            _previousThemeMode = mode;
            RestartApplication();
        }
        else
        {
            // User cancelled - revert the radio button selection
            RevertToTheme(_previousThemeMode);
        }
    }

    private static void RestartApplication()
    {
        LoggingService.Info("AppearanceSettingsPage: Restarting application for theme change");

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
            WpfApplication.Current.Shutdown();
        }
    }

    /// <summary>
    /// Reverts the radio button selection to the specified theme mode.
    /// </summary>
    private void RevertToTheme(ThemeMode mode)
    {
        _isInitializing = true;
        try
        {
            switch (mode)
            {
                case ThemeMode.System:
                    ThemeSystemRadio.IsChecked = true;
                    break;
                case ThemeMode.Light:
                    ThemeLightRadio.IsChecked = true;
                    break;
                case ThemeMode.Dark:
                    ThemeDarkRadio.IsChecked = true;
                    break;
            }
            LoggingService.Debug($"AppearanceSettingsPage: Reverted to theme mode: {mode}");
        }
        finally
        {
            _isInitializing = false;
        }
    }

}
