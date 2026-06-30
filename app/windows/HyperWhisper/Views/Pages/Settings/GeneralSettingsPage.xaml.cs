// GENERAL SETTINGS PAGE
// Handles general application settings including startup behavior, window behavior,
// error logging (Sentry), and auto-update (NetSparkle).

using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Pages.Settings;

public partial class GeneralSettingsPage : Page
{
    public GeneralSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeSettings();
    }

    /// <summary>
    /// Initializes all settings with current values.
    /// </summary>
    private void InitializeSettings()
    {
        // Load launch at startup state from registry
        LaunchAtStartupCheckbox.Checked -= LaunchAtStartupCheckbox_Checked;
        LaunchAtStartupCheckbox.Unchecked -= LaunchAtStartupCheckbox_Unchecked;
        LaunchAtStartupCheckbox.IsChecked = StartupService.Instance.IsEnabled;
        LaunchAtStartupCheckbox.Checked += LaunchAtStartupCheckbox_Checked;
        LaunchAtStartupCheckbox.Unchecked += LaunchAtStartupCheckbox_Unchecked;

        // Load launch minimized state from settings
        LaunchMinimizedCheckbox.Checked -= LaunchMinimizedCheckbox_Checked;
        LaunchMinimizedCheckbox.Unchecked -= LaunchMinimizedCheckbox_Unchecked;
        LaunchMinimizedCheckbox.IsChecked = SettingsService.Instance.LaunchMinimized;
        LaunchMinimizedCheckbox.Checked += LaunchMinimizedCheckbox_Checked;
        LaunchMinimizedCheckbox.Unchecked += LaunchMinimizedCheckbox_Unchecked;

        // Load minimize to tray state from settings
        MinimizeToTrayCheckbox.Checked -= MinimizeToTrayCheckbox_Checked;
        MinimizeToTrayCheckbox.Unchecked -= MinimizeToTrayCheckbox_Unchecked;
        MinimizeToTrayCheckbox.IsChecked = SettingsService.Instance.MinimizeToTray;
        MinimizeToTrayCheckbox.Checked += MinimizeToTrayCheckbox_Checked;
        MinimizeToTrayCheckbox.Unchecked += MinimizeToTrayCheckbox_Unchecked;

        // Load show recording window state from settings
        ShowRecordingWindowCheckbox.Checked -= ShowRecordingWindowCheckbox_Checked;
        ShowRecordingWindowCheckbox.Unchecked -= ShowRecordingWindowCheckbox_Unchecked;
        ShowRecordingWindowCheckbox.IsChecked = SettingsService.Instance.ShowRecordingWindow;
        ShowRecordingWindowCheckbox.Checked += ShowRecordingWindowCheckbox_Checked;
        ShowRecordingWindowCheckbox.Unchecked += ShowRecordingWindowCheckbox_Unchecked;

        // Load error logging state from settings
        ErrorLoggingCheckbox.Checked -= ErrorLoggingCheckbox_Checked;
        ErrorLoggingCheckbox.Unchecked -= ErrorLoggingCheckbox_Unchecked;
        ErrorLoggingCheckbox.IsChecked = SettingsService.Instance.EnableErrorLogging;
        ErrorLoggingCheckbox.Checked += ErrorLoggingCheckbox_Checked;
        ErrorLoggingCheckbox.Unchecked += ErrorLoggingCheckbox_Unchecked;

        // Load auto-update state from settings
        AutoUpdateCheckbox.Checked -= AutoUpdateCheckbox_Checked;
        AutoUpdateCheckbox.Unchecked -= AutoUpdateCheckbox_Unchecked;
        AutoUpdateCheckbox.IsChecked = SettingsService.Instance.CheckForUpdatesAutomatically;
        AutoUpdateCheckbox.Checked += AutoUpdateCheckbox_Checked;
        AutoUpdateCheckbox.Unchecked += AutoUpdateCheckbox_Unchecked;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var shortVersion = version?.ToString(3) ?? "Unknown";
        var buildVersion = version?.Revision.ToString() ?? "0";
        VersionText.Text = Loc.S("settings.version.detail", shortVersion, buildVersion);

        LoggingService.Debug($"GeneralSettingsPage: Initialized (startup={StartupService.Instance.IsEnabled}, launchMinimized={SettingsService.Instance.LaunchMinimized}, minimizeToTray={SettingsService.Instance.MinimizeToTray}, showRecordingWindow={SettingsService.Instance.ShowRecordingWindow}, errorLogging={SettingsService.Instance.EnableErrorLogging}, autoUpdate={SettingsService.Instance.CheckForUpdatesAutomatically})");
    }

    // =========================================================================
    // LAUNCH AT STARTUP
    // =========================================================================

    private void LaunchAtStartupCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        var success = StartupService.Instance.Enable();
        if (!success)
        {
            LaunchAtStartupCheckbox.Checked -= LaunchAtStartupCheckbox_Checked;
            LaunchAtStartupCheckbox.IsChecked = false;
            LaunchAtStartupCheckbox.Checked += LaunchAtStartupCheckbox_Checked;

            WpfMessageBox.Show(
                Loc.S("settings.general.startup.enableFailed"),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            LoggingService.Info("GeneralSettingsPage: Enabled launch at startup");
        }
    }

    private void LaunchAtStartupCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        var success = StartupService.Instance.Disable();
        if (!success)
        {
            LaunchAtStartupCheckbox.Unchecked -= LaunchAtStartupCheckbox_Unchecked;
            LaunchAtStartupCheckbox.IsChecked = true;
            LaunchAtStartupCheckbox.Unchecked += LaunchAtStartupCheckbox_Unchecked;

            WpfMessageBox.Show(
                Loc.S("settings.general.startup.disableFailed"),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            LoggingService.Info("GeneralSettingsPage: Disabled launch at startup");
        }
    }

    // =========================================================================
    // LAUNCH MINIMIZED
    // =========================================================================

    private void LaunchMinimizedCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.LaunchMinimized = true;
        LoggingService.Info("GeneralSettingsPage: Enabled launch minimized");
    }

    private void LaunchMinimizedCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.LaunchMinimized = false;
        LoggingService.Info("GeneralSettingsPage: Disabled launch minimized");
    }

    // =========================================================================
    // MINIMIZE TO TRAY
    // =========================================================================

    private void MinimizeToTrayCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.MinimizeToTray = true;
        LoggingService.Info("GeneralSettingsPage: Enabled minimize to tray");
    }

    private void MinimizeToTrayCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.MinimizeToTray = false;
        LoggingService.Info("GeneralSettingsPage: Disabled minimize to tray");
    }

    // =========================================================================
    // SHOW RECORDING WINDOW
    // =========================================================================

    private void ShowRecordingWindowCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.ShowRecordingWindow = true;
        LoggingService.Info("GeneralSettingsPage: Enabled show recording window");
    }

    private void ShowRecordingWindowCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.ShowRecordingWindow = false;
        LoggingService.Info("GeneralSettingsPage: Disabled show recording window");
    }

    // =========================================================================
    // ERROR LOGGING (SENTRY)
    // =========================================================================

    private void ErrorLoggingCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.EnableErrorLogging = true;
        SentryService.Initialize();
        LoggingService.Info("GeneralSettingsPage: Enabled error logging (Sentry)");
    }

    private void ErrorLoggingCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.EnableErrorLogging = false;
        SentryService.Shutdown();
        LoggingService.Info("GeneralSettingsPage: Disabled error logging (Sentry)");
    }

    // =========================================================================
    // AUTO-UPDATE
    // =========================================================================

    private void AutoUpdateCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.CheckForUpdatesAutomatically = true;
        UpdateService.StartBackgroundCheck();
        LoggingService.Info("GeneralSettingsPage: Enabled automatic update checks");
    }

    private void AutoUpdateCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.CheckForUpdatesAutomatically = false;
        UpdateService.StopBackgroundCheck();
        LoggingService.Info("GeneralSettingsPage: Disabled automatic update checks");
    }

    private void ContactSupport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.hyperwhisper.com/support",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Error("GeneralSettingsPage: Failed to open support page", ex);
            WpfMessageBox.Show(
                Loc.S("settings.general.support.openFailed", ex.Message),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
