// CLOUD ACCOUNT SETTINGS PAGE
// One combined "HyperWhisper Cloud" panel replacing the old split License + Credits
// sections. Your account key is your wallet — one key = one identity = one credit
// pool. Toggles between a licensed view (balance + account info + masked account key)
// and an unlicensed view (activation + Get Credits CTA). Local transcription is free
// & unlimited (open source) — no trial usage UI.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using LicenseManager = HyperWhisper.Services.LicenseManager;
using LicenseStatus = HyperWhisper.Models.LicenseStatus;

namespace HyperWhisper.Views.Pages.Settings;

public partial class CloudAccountSettingsPage : Page
{
    // Segoe MDL2 Assets glyphs for the account-key row.
    private const string RevealGlyph = "\uE7B3"; // RedEye — click to reveal
    private const string HideGlyph = "\uED1A";   // Hide — click to mask
    private const string CopyGlyph = "\uE8C8";   // Copy
    private const string CheckGlyph = "\uE73E";  // CheckMark — brief copy confirmation

    private bool _keyRevealed;

    public CloudAccountSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HyperWhisperCloudManager.Instance.PropertyChanged += OnCreditsPropertyChanged;
        LicenseManager.Instance.LicenseStatusChanged += OnLicenseStatusChanged;

        RefreshUI();

        if (LicenseManager.Instance.LicenseStatus == LicenseStatus.Active)
        {
            LoggingService.Debug("CloudAccountSettingsPage: Loaded (licensed), fetching credits...");
            await FetchAndDisplayCreditsAsync(forceRefresh: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HyperWhisperCloudManager.Instance.PropertyChanged -= OnCreditsPropertyChanged;
        LicenseManager.Instance.LicenseStatusChanged -= OnLicenseStatusChanged;
    }

    // =========================================================================
    // VIEW TOGGLING
    // =========================================================================

    private void RefreshUI()
    {
        var isLicensed = LicenseManager.Instance.LicenseStatus == LicenseStatus.Active;

        LicensedView.Visibility = isLicensed ? Visibility.Visible : Visibility.Collapsed;
        UnlicensedView.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
        CreditsRefreshButton.Visibility = isLicensed ? Visibility.Visible : Visibility.Collapsed;

        LicenseErrorText.Visibility = Visibility.Collapsed;

        if (isLicensed)
        {
            UpdateKeyRow();
        }

        LoggingService.Debug($"CloudAccountSettingsPage: UI refreshed (licensed: {isLicensed})");
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

    private void OnLicenseStatusChanged(object? sender, EventArgs e)
    {
        // Marshal to UI thread — license status may change on a background thread
        // (activation / deactivation). Activating flips us to the licensed layout
        // and triggers a credits fetch.
        Dispatcher.Invoke(() =>
        {
            _keyRevealed = false;
            RefreshUI();

            if (LicenseManager.Instance.LicenseStatus == LicenseStatus.Active)
            {
                LoggingService.Debug("CloudAccountSettingsPage: License status changed, refreshing credits...");
                _ = FetchAndDisplayCreditsAsync(forceRefresh: true);
            }
        });
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
            LoggingService.Error($"CloudAccountSettingsPage: Failed to fetch credits: {ex.Message}");
            UpdateCreditsErrorState();
        }
    }

    // =========================================================================
    // CREDITS UI UPDATE METHODS
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

        // Keep the account-key row in sync with the current identifier.
        UpdateKeyRow();

        LoggingService.Debug($"CloudAccountSettingsPage: Display updated - {credits.FormattedBalance}");
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
    // ACCOUNT KEY ROW
    // =========================================================================

    private void UpdateKeyRow()
    {
        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();

        if (!isLicensed || string.IsNullOrEmpty(identifier))
        {
            CloudKeyValueText.Text = string.Empty;
            return;
        }

        CloudKeyValueText.Text = _keyRevealed ? identifier : MaskKey(identifier);
        CloudKeyRevealGlyph.Text = _keyRevealed ? HideGlyph : RevealGlyph;
        ToggleRevealKeyButton.ToolTip = Loc.S(_keyRevealed ? "settings.cloud.key.hide" : "settings.cloud.key.reveal");
    }

    private void ToggleRevealKey_Click(object sender, RoutedEventArgs e)
    {
        _keyRevealed = !_keyRevealed;
        UpdateKeyRow();
    }

    private async void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();

        if (!isLicensed || string.IsNullOrEmpty(identifier))
            return;

        try
        {
            System.Windows.Clipboard.SetText(identifier);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"CloudAccountSettingsPage: Failed to copy account key: {ex.Message}");
            return;
        }

        CloudKeyCopyGlyph.Text = CheckGlyph;
        await Task.Delay(1500);
        CloudKeyCopyGlyph.Text = CopyGlyph;
    }

    /// <summary>
    /// Masks every character with a bullet while keeping dashes, e.g.
    /// <c>HW-7F3K-9QXM</c> → <c>••-••••-••••</c>.
    /// </summary>
    private static string MaskKey(string key)
        => new string(key.Select(c => c == '-' ? '-' : '•').ToArray());

    // =========================================================================
    // BUTTON HANDLERS
    // =========================================================================

    private async void CreditsRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Debug("CloudAccountSettingsPage: Refresh clicked");
        await FetchAndDisplayCreditsAsync(forceRefresh: true);
    }

    private void CreditsAddCredits_Click(object sender, RoutedEventArgs e)
    {
        var url = LicenseManager.Instance.GetCreditsPurchaseUrl();

        LoggingService.Info("CloudAccountSettingsPage: Opening credits purchase page");

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
            LoggingService.Error($"CloudAccountSettingsPage: Failed to open credits page: {ex.Message}");
            WpfMessageBox.Show(
                Loc.S("settings.general.support.openFailed", ex.Message),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ManageBilling_Click(object sender, RoutedEventArgs e)
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

    private async void ActivateLicense_Click(object sender, RoutedEventArgs e)
    {
        var licenseKey = LicenseKeyBox.Text?.Trim();

        if (string.IsNullOrEmpty(licenseKey))
        {
            ShowLicenseError(Loc.S("license.error.enterKey"));
            return;
        }

        ActivateLicenseButton.IsEnabled = false;
        ActivateLicenseButton.Content = Loc.S("license.button.activating");
        LicenseErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await LicenseManager.Instance.ActivateLicenseAsync(licenseKey);

            if (result.IsValid)
            {
                LicenseKeyBox.Text = "";
                // LicenseStatusChanged flips us to the licensed layout and fetches credits.
                RefreshUI();
                LoggingService.Info("CloudAccountSettingsPage: License activated successfully");
            }
            else
            {
                ShowLicenseError(result.ErrorMessage ?? "License validation failed.");
            }
        }
        catch (Exception ex)
        {
            ShowLicenseError($"Activation failed: {ex.Message}");
            LoggingService.Error($"CloudAccountSettingsPage: License activation failed: {ex.Message}");
        }
        finally
        {
            ActivateLicenseButton.IsEnabled = true;
            ActivateLicenseButton.Content = Loc.S("license.button.activate");
        }
    }

    private void DeactivateLicense_Click(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            Loc.S("settings.license.deactivate.confirm.message"),
            Loc.S("settings.license.deactivate.confirm.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            LicenseManager.Instance.DeactivateLicense();
            RefreshUI();
            LoggingService.Info("CloudAccountSettingsPage: License deactivated");
        }
    }

    private void ShowLicenseError(string message)
    {
        LicenseErrorText.Text = message;
        LicenseErrorText.Visibility = Visibility.Visible;
    }
}
