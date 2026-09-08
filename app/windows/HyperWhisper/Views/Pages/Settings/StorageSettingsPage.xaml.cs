// STORAGE SETTINGS PAGE
// Handles recordings folder configuration and auto-delete settings.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using Forms = System.Windows.Forms;

namespace HyperWhisper.Views.Pages.Settings;

public partial class StorageSettingsPage : Page
{
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly StorageService _storageService = StorageService.Instance;
    private readonly AutoDeleteService _autoDeleteService = AutoDeleteService.Instance;

    public StorageSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadStorageSettings();
        LoadAutoDeleteSettings();
    }

    private void LoadStorageSettings()
    {
        RecordingsPathText.Text = _storageService.GetRecordingsFolder();
        StoreAsM4ACheckbox.IsChecked = _settingsService.StoreAsM4A;
        UpdateStorageError(_storageService.ValidationError);
    }

    private void LoadAutoDeleteSettings()
    {
        AutoDeleteEnabledCheckbox.IsChecked = _settingsService.AutoDeleteEnabled;
        DaysOldTextBox.Text = _settingsService.AutoDeleteDaysOld.ToString();

        UpdateAutoDeleteUI();
    }

    private void UpdateAutoDeleteUI()
    {
        bool isEnabled = _settingsService.AutoDeleteEnabled;

        AutoDeleteConfigPanel.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        LastCleanupInfoPanel.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        DeleteNowButton.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (isEnabled)
        {
            UpdateLastCleanupInfo();
        }
    }

    /// <summary>
    /// Rewrites the line under the days box from the service's recorded sweep.
    /// Internal so a smoke case can assert what the line says, the way
    /// BackupExportSettingsPage.ApplyImportSuccess is.
    /// </summary>
    internal void UpdateLastCleanupInfo()
    {
        var lastTime = _autoDeleteService.LastCleanupTime;
        var deletedCount = _autoDeleteService.LastCleanupTranscriptsDeleted;

        if (lastTime.HasValue)
        {
            LastCleanupText.Text = FormatLastCleanupLine(lastTime.Value, deletedCount, TimeZoneInfo.Local);
        }
        else
        {
            LastCleanupText.Text = Loc.S("settings.storage.autoDelete.noCleanupYet");
        }
    }

    /// <summary>
    /// The "Last cleanup: … - Deleted N recording(s)" line, with the recorded instant
    /// converted out of UTC into <paramref name="zone"/>.
    /// </summary>
    /// <remarks>
    /// The conversion is the whole point. The sweep records DateTime.UtcNow, and
    /// ToString("g") formats a UTC DateTime without converting it — so the line printed a
    /// UTC instant in a local-looking format, seven hours into the future for a user in
    /// UTC-7 (issue #504). Every other timestamp the app renders already converts:
    /// History's row times and detail header (TranscriptViewModel.FormattedTime /
    /// FormattedDate), its Today/Yesterday section headers, and its date filters all call
    /// ToLocalTime, so this line was the only one out of step.
    ///
    /// The zone is a parameter, not TimeZoneInfo.Local read inside, so a test can pin an
    /// offset instead of inheriting whatever the CI runner is set to — on a UTC runner a
    /// missing conversion is invisible.
    /// </remarks>
    internal static string FormatLastCleanupLine(DateTime lastCleanupUtc, int deletedCount, TimeZoneInfo zone)
    {
        // ConvertTimeFromUtc rejects a Local Kind and reads Unspecified as UTC, which is
        // what a value round-tripped through settings.json may arrive as.
        var utc = lastCleanupUtc.Kind == DateTimeKind.Local
            ? lastCleanupUtc.ToUniversalTime()
            : lastCleanupUtc;

        return Loc.S("settings.storage.autoDelete.lastCleanup",
                     TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("g"),
                     deletedCount);
    }

    private void UpdateStorageError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            StorageErrorText.Visibility = Visibility.Collapsed;
            StorageErrorText.Text = string.Empty;
        }
        else
        {
            StorageErrorText.Visibility = Visibility.Visible;
            StorageErrorText.Text = message;
        }
    }

    private void ChooseRecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = Loc.S("settings.storage.folderBrowser.description"),
            SelectedPath = _storageService.GetRecordingsFolder(),
            ShowNewFolderButton = true
        };

        var result = dialog.ShowDialog();
        if (result == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            if (_storageService.TryChangeRecordingsFolder(dialog.SelectedPath, out var error))
            {
                RecordingsPathText.Text = _storageService.GetRecordingsFolder();
                UpdateStorageError(null);
                LoggingService.Info($"Settings: Recordings folder changed to {dialog.SelectedPath}");
            }
            else
            {
                UpdateStorageError(error);
                WpfMessageBox.Show(
                    error ?? Loc.S("settings.storage.error.invalidFolder"),
                    Loc.S("settings.storage.error.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void ShowRecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_storageService.TryOpenRecordingsFolder(out var error))
        {
            WpfMessageBox.Show(
                Loc.S("settings.storage.error.openFolder", error ?? Loc.S("settings.storage.error.invalidFolder")),
                Loc.S("settings.storage.error.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void StoreAsM4A_Changed(object sender, RoutedEventArgs e)
    {
        _settingsService.StoreAsM4A = StoreAsM4ACheckbox.IsChecked == true;
    }

    private void AutoDeleteEnabled_Changed(object sender, RoutedEventArgs e)
    {
        _settingsService.AutoDeleteEnabled = AutoDeleteEnabledCheckbox.IsChecked == true;
        UpdateAutoDeleteUI();
    }

    private void DaysOld_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(DaysOldTextBox.Text, out int value))
        {
            _settingsService.AutoDeleteDaysOld = Math.Max(1, Math.Min(365, value));
            DaysOldTextBox.Text = _settingsService.AutoDeleteDaysOld.ToString();
        }
        else
        {
            DaysOldTextBox.Text = _settingsService.AutoDeleteDaysOld.ToString();
        }
    }

    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private async void DeleteNow_Click(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            Loc.S("settings.storage.autoDelete.confirmDelete.message"),
            Loc.S("settings.storage.autoDelete.confirmDelete.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            DeleteNowButton.IsEnabled = false;
            DeleteNowButton.Content = Loc.S("settings.storage.autoDelete.deleting");

            try
            {
                int deleted = await Task.Run(() => _autoDeleteService.PerformManualCleanup());

                DeleteNowButton.IsEnabled = true;
                DeleteNowButton.Content = Loc.S("settings.storage.autoDelete.deleteNow");
                UpdateLastCleanupInfo();

                WpfMessageBox.Show(
                    Loc.S("settings.storage.autoDelete.deleteComplete.message", deleted),
                    Loc.S("settings.storage.autoDelete.deleteComplete.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoggingService.Error("Settings: Manual storage cleanup failed", ex);

                DeleteNowButton.IsEnabled = true;
                DeleteNowButton.Content = Loc.S("settings.storage.autoDelete.deleteNow");
                UpdateLastCleanupInfo();

                WpfMessageBox.Show(
                    Loc.S("settings.storage.autoDelete.deleteFailed.message", ex.InnerException?.Message ?? ex.Message),
                    Loc.S("settings.storage.autoDelete.deleteFailed.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
