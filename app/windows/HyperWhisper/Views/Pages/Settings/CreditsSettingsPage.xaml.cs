// CREDITS SETTINGS PAGE
// Handles HyperWhisper Cloud credit balance display and management.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using LicenseManager = HyperWhisper.Services.LicenseManager;

namespace HyperWhisper.Views.Pages.Settings;

public partial class CreditsSettingsPage : Page
{
    public CreditsSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HyperWhisperCloudManager.Instance.PropertyChanged += OnCreditsPropertyChanged;
        LicenseManager.Instance.LicenseStatusChanged += OnLicenseStatusChanged;

        LoggingService.Debug("CreditsSettingsPage: Loaded, fetching credits...");
        await FetchAndDisplayCreditsAsync(forceRefresh: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HyperWhisperCloudManager.Instance.PropertyChanged -= OnCreditsPropertyChanged;
        LicenseManager.Instance.LicenseStatusChanged -= OnLicenseStatusChanged;
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    private void OnCreditsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(HyperWhisperCloudManager.IsFetchingCredits):
                    UpdateCreditsLoadingState();
                    break;
                case nameof(HyperWhisperCloudManager.Credits):
                case nameof(HyperWhisperCloudManager.HasCredits):
                    UpdateCreditsDisplay();
                    break;
                case nameof(HyperWhisperCloudManager.LastError):
                case nameof(HyperWhisperCloudManager.HasError):
                    UpdateCreditsErrorState();
                    break;
            }
        });
    }

    private async void OnLicenseStatusChanged(object? sender, EventArgs e)
    {
        LoggingService.Debug("CreditsSettingsPage: License status changed, refreshing credits...");
        await FetchAndDisplayCreditsAsync(forceRefresh: true);
    }

    // =========================================================================
    // CREDITS FETCHING
    // =========================================================================

    private async Task FetchAndDisplayCreditsAsync(bool forceRefresh = false)
    {
        UpdateCreditsLoadingState();

        try
        {
            var credits = await HyperWhisperCloudManager.Instance.FetchCreditsAsync(forceRefresh);

            if (credits != null)
            {
                UpdateCreditsDisplay();
            }
            else
            {
                UpdateCreditsErrorState();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error($"CreditsSettingsPage: Failed to fetch credits: {ex.Message}");
            UpdateCreditsErrorState();
        }
    }

    // =========================================================================
    // UI UPDATE METHODS
    // =========================================================================

    private void UpdateCreditsLoadingState()
    {
        var manager = HyperWhisperCloudManager.Instance;
        var isLoading = manager.IsFetchingCredits;
        var hasCredits = manager.HasCredits;

        CreditsLoadingPanel.Visibility = isLoading && !hasCredits ? Visibility.Visible : Visibility.Collapsed;
        CreditsRefreshButton.IsEnabled = !isLoading;
    }

    private void UpdateCreditsDisplay()
    {
        var manager = HyperWhisperCloudManager.Instance;
        var credits = manager.Credits;

        if (credits == null)
        {
            CreditsDisplayPanel.Visibility = Visibility.Collapsed;
            CreditsAccountCard.Visibility = Visibility.Collapsed;
            return;
        }

        CreditsDisplayPanel.Visibility = Visibility.Visible;
        CreditsAccountCard.Visibility = Visibility.Visible;
        CreditsErrorPanel.Visibility = Visibility.Collapsed;
        CreditsLoadingPanel.Visibility = Visibility.Collapsed;

        CreditsMinutesText.Text = $"~{credits.MinutesRemaining}";
        CreditsDollarsText.Text = $"(${credits.DollarBalance:F2})";

        if (credits.IsExhausted)
        {
            CreditsMinutesText.Foreground = (Brush)FindResource("ErrorForegroundBrush");
        }
        else if (credits.IsLow)
        {
            CreditsMinutesText.Foreground = (Brush)FindResource("WarningForegroundBrush");
        }
        else
        {
            CreditsMinutesText.Foreground = FindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        }

        CreditsLowWarning.Visibility = credits.IsLow && !credits.IsExhausted ? Visibility.Visible : Visibility.Collapsed;
        CreditsExhaustedWarning.Visibility = credits.IsExhausted ? Visibility.Visible : Visibility.Collapsed;

        CreditsRemainingText.Text = $"{credits.CreditsRemaining:F1}";
        CreditsCostPerMinuteText.Text = $"~{credits.CreditsPerMinute:F1} credits";
        CreditsAccountTypeText.Text = credits.AccountType;

        if (credits.IsLicensed && !string.IsNullOrEmpty(credits.CustomerId))
        {
            CreditsCustomerIdPanel.Visibility = Visibility.Visible;
            var displayId = credits.CustomerId.Length > 20
                ? credits.CustomerId[..17] + "..."
                : credits.CustomerId;
            CreditsCustomerIdText.Text = displayId;
        }
        else
        {
            CreditsCustomerIdPanel.Visibility = Visibility.Collapsed;
        }

        if (credits.IsAnonymous && !string.IsNullOrEmpty(credits.FormattedResetTime))
        {
            CreditsDailyResetPanel.Visibility = Visibility.Visible;
            CreditsDailyResetText.Text = credits.FormattedResetTime;
        }
        else
        {
            CreditsDailyResetPanel.Visibility = Visibility.Collapsed;
        }

        LoggingService.Debug($"CreditsSettingsPage: Display updated - {credits.FormattedBalance}");
    }

    private void UpdateCreditsErrorState()
    {
        var manager = HyperWhisperCloudManager.Instance;

        if (manager.HasError && !manager.HasCredits)
        {
            CreditsErrorPanel.Visibility = Visibility.Visible;
            CreditsErrorText.Text = manager.LastError ?? "Failed to load credits";
            CreditsDisplayPanel.Visibility = Visibility.Collapsed;
            CreditsAccountCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            CreditsErrorPanel.Visibility = Visibility.Collapsed;
        }

        CreditsLoadingPanel.Visibility = Visibility.Collapsed;
        CreditsRefreshButton.IsEnabled = true;
    }

    // =========================================================================
    // BUTTON HANDLERS
    // =========================================================================

    private async void CreditsRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Debug("CreditsSettingsPage: Refresh clicked");
        await FetchAndDisplayCreditsAsync(forceRefresh: true);
    }

    private void CreditsAddCredits_Click(object sender, RoutedEventArgs e)
    {
        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();
        var paramName = isLicensed ? "license_key" : "device_id";
        var url = $"https://www.hyperwhisper.com/credits?{paramName}={Uri.EscapeDataString(identifier)}";

        LoggingService.Info($"CreditsSettingsPage: Opening credits purchase page (licensed: {isLicensed})");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Error($"CreditsSettingsPage: Failed to open credits page: {ex.Message}");
            WpfMessageBox.Show(
                Loc.S("settings.general.support.openFailed", ex.Message),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CreditsManageAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!LicenseManager.Instance.OpenUserPortal(out var errorMessage))
        {
            WpfMessageBox.Show(
                Loc.S("settings.general.support.openFailed", errorMessage ?? ""),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
