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

    private void UpdateLastCleanupInfo()
    {
        var lastTime = _autoDeleteService.LastCleanupTime;
        var deletedCount = _autoDeleteService.LastCleanupTranscriptsDeleted;

        if (lastTime.HasValue)
        {
            LastCleanupText.Text = Loc.S("settings.storage.autoDelete.lastCleanup",
                                         lastTime.Value.ToString("g"),
                                         deletedCount);
        }
        else
        {
            LastCleanupText.Text = Loc.S("settings.storage.autoDelete.noCleanupYet");
        }
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

    private void DeleteNow_Click(object sender, RoutedEventArgs e)
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

            Task.Run(() =>
            {
                try
                {
                    int deleted = _autoDeleteService.PerformManualCleanup();

                    Dispatcher.Invoke(() =>
                    {
                        DeleteNowButton.IsEnabled = true;
                        DeleteNowButton.Content = Loc.S("settings.storage.autoDelete.deleteNow");
                        UpdateLastCleanupInfo();

                        WpfMessageBox.Show(
                            Loc.S("settings.storage.autoDelete.deleteComplete.message", deleted),
                            Loc.S("settings.storage.autoDelete.deleteComplete.title"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    });
                }
                catch (Exception ex)
                {
                    LoggingService.Error("Settings: Manual storage cleanup failed", ex);
                    Dispatcher.Invoke(() =>
                    {
                        DeleteNowButton.IsEnabled = true;
                        DeleteNowButton.Content = Loc.S("settings.storage.autoDelete.deleteNow");
                        UpdateLastCleanupInfo();

                        WpfMessageBox.Show(
                            Loc.S("settings.storage.autoDelete.deleteFailed.message", ex.InnerException?.Message ?? ex.Message),
                            Loc.S("settings.storage.autoDelete.deleteFailed.title"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    });
                }
            });
        }
    }
}
