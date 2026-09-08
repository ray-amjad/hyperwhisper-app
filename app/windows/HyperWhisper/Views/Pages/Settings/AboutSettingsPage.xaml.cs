// ABOUT SETTINGS PAGE
// Displays application version and provides access to logs.

using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace HyperWhisper.Views.Pages.Settings;

public partial class AboutSettingsPage : Page
{
    public AboutSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // menu.version.label rather than a third "Version {0}" key: it is the
        // same one-argument version line the tray menu already renders
        // (MainWindow.xaml.cs), and the macOS catalog carries the same key, so
        // the two cannot drift apart. The number is an identifier and stays as
        // it is; only the word around it is translated (issue #516).
        //
        // ToString(3) with the "Unknown" fallback matches both other sites. The
        // interpolation this replaced printed ".." for a null Version.
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";
        VersionText.Text = Loc.S("menu.version.label", version);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoggingService.OpenLogDirectory();
        }
        catch (Exception ex)
        {
            LoggingService.Error("AboutSettingsPage: Failed to open log folder", ex);

            WpfMessageBox.Show(
                Loc.S("settings.about.openLogFolder.failed.message", ex.Message),
                Loc.S("settings.about.openLogFolder.failed.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.S("settings.about.exportDiagnostics.dialogTitle"),
            Filter = Loc.S("settings.about.exportDiagnostics.fileFilter"),
            FileName = $"HyperWhisper-Diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}",
            DefaultExt = ".zip"
        };

        if (dialog.ShowDialog() != true)
            return;

        ExportDiagnosticsButton.IsEnabled = false;
        var originalContent = ExportDiagnosticsButton.Content;
        ExportDiagnosticsButton.Content = Loc.S("settings.about.exportDiagnostics.exporting");

        try
        {
            await Task.Run(() => LoggingService.ExportDiagnostics(dialog.FileName));

            WpfMessageBox.Show(
                Loc.S("settings.about.exportDiagnostics.success.message"),
                Loc.S("settings.about.exportDiagnostics.success.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LoggingService.Error("AboutSettingsPage: Diagnostics export failed", ex);

            WpfMessageBox.Show(
                Loc.S("settings.about.exportDiagnostics.failed.message", ex.Message),
                Loc.S("settings.about.exportDiagnostics.failed.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
            ExportDiagnosticsButton.Content = originalContent;
        }
    }

    /// <summary>
    /// Manually check for updates when user clicks button.
    /// Disables button during check and shows loading state.
    /// </summary>
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        // Disable button and show loading state
        CheckForUpdatesButton.IsEnabled = false;
        var originalContent = CheckForUpdatesButton.Content;
        CheckForUpdatesButton.Content = Loc.S("settings.about.checkingForUpdates");

        try
        {
            // Trigger manual update check
            // NetSparkle will show its own dialogs for results
            await UpdateService.CheckForUpdatesNow();
        }
        catch (Exception ex)
        {
            LoggingService.Error("AboutSettingsPage: Manual update check failed", ex);

            // Show error dialog to user
            WpfMessageBox.Show(
                Loc.S("settings.about.updateCheckFailed.message", ex.Message),
                Loc.S("settings.about.updateCheckFailed.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            // Re-enable button and restore original text
            CheckForUpdatesButton.IsEnabled = true;
            CheckForUpdatesButton.Content = originalContent;
        }
    }
}
