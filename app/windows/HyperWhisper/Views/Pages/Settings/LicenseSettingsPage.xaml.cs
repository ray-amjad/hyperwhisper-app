// LICENSE SETTINGS PAGE
// Handles license activation, deactivation, and trial usage display.

using System;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace HyperWhisper.Views.Pages.Settings;

public partial class LicenseSettingsPage : Page
{
    public LicenseSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to events
        LicenseManager.Instance.LicenseStatusChanged += OnLicenseStatusChanged;
        LicenseUsageTracker.Instance.UsageChanged += OnUsageChanged;

        // Initial UI update
        RefreshLicenseUI();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LicenseManager.Instance.LicenseStatusChanged -= OnLicenseStatusChanged;
        LicenseUsageTracker.Instance.UsageChanged -= OnUsageChanged;
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    private void OnLicenseStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RefreshLicenseUI);
    }

    private void OnUsageChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(UpdateTrialUsageUI);
    }

    // =========================================================================
    // UI REFRESH
    // =========================================================================

    private void RefreshLicenseUI()
    {
        var manager = LicenseManager.Instance;
        var status = manager.LicenseStatus;

        UpdateLicenseStatusUI(status, manager.CustomerEmail);

        var isLicensed = status == LicenseStatus.Active;

        LicenseActivationDivider.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
        LicenseActivationPanel.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
        TrialUsageCard.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
        LicensedUserActions.Visibility = isLicensed ? Visibility.Visible : Visibility.Collapsed;

        if (!isLicensed)
        {
            UpdateTrialUsageUI();
        }

        LicenseErrorText.Visibility = Visibility.Collapsed;
        LoggingService.Debug($"LicenseSettingsPage: UI refreshed (status: {status})");
    }

    private void UpdateLicenseStatusUI(LicenseStatus status, string? email)
    {
        LicenseStatusText.Text = status switch
        {
            LicenseStatus.Trial => Loc.S("license.status.trial.description"),
            LicenseStatus.Active => Loc.S("license.status.active.description"),
            LicenseStatus.Expired => Loc.S("license.status.expired.description"),
            LicenseStatus.Invalid => Loc.S("license.status.invalid.description"),
            _ => Loc.S("license.status.unknown")
        };

        if (!string.IsNullOrEmpty(email) && status == LicenseStatus.Active)
        {
            LicenseEmailText.Text = email;
            LicenseEmailText.Visibility = Visibility.Visible;
        }
        else
        {
            LicenseEmailText.Visibility = Visibility.Collapsed;
        }

        var (badgeText, backgroundBrushKey, foregroundBrushKey) = status switch
        {
            LicenseStatus.Active => ("ACTIVE", "SuccessBackgroundBrush", "SuccessForegroundBrush"),
            LicenseStatus.Trial => ("TRIAL", "WarningBackgroundBrush", "WarningForegroundBrush"),
            LicenseStatus.Expired => ("EXPIRED", "ErrorBackgroundBrush", "ErrorForegroundBrush"),
            LicenseStatus.Invalid => ("INVALID", "ErrorBackgroundBrush", "ErrorForegroundBrush"),
            _ => ("UNKNOWN", "BadgeGrayBackgroundBrush", "BadgeGrayForegroundBrush")
        };

        LicenseStatusBadgeText.Text = badgeText;
        LicenseStatusBadge.Background = (Brush)FindResource(backgroundBrushKey);
        LicenseStatusBadgeText.Foreground = (Brush)FindResource(foregroundBrushKey);
    }

    private void UpdateTrialUsageUI()
    {
        var tracker = LicenseUsageTracker.Instance;

        var dailyUsed = tracker.DailyUsageSeconds;
        var dailyLimit = LicenseUsageTracker.TrialDailyLimitSeconds;
        DailyUsageText.Text = $"{dailyUsed} / {dailyLimit} seconds";
        DailyUsageProgress.Maximum = dailyLimit;
        DailyUsageProgress.Value = dailyUsed;

        if (tracker.IsDailyLimitReached)
        {
            DailyUsageProgress.Foreground = (Brush)FindResource("ErrorForegroundBrush");
        }
        else
        {
            DailyUsageProgress.Foreground = FindResource("AccentBrush") as Brush ?? Brushes.Blue;
        }

        var modelsDownloaded = tracker.ModelsDownloaded;
        var modelLimit = LicenseUsageTracker.TrialModelLimit;
        ModelDownloadsText.Text = $"{modelsDownloaded} / {modelLimit} models";
        ModelDownloadsProgress.Maximum = modelLimit;
        ModelDownloadsProgress.Value = modelsDownloaded;

        if (tracker.IsModelLimitReached)
        {
            ModelDownloadsProgress.Foreground = (Brush)FindResource("ErrorForegroundBrush");
        }
        else
        {
            ModelDownloadsProgress.Foreground = FindResource("AccentBrush") as Brush ?? Brushes.Blue;
        }
    }

    // =========================================================================
    // BUTTON HANDLERS
    // =========================================================================

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
                RefreshLicenseUI();
                LoggingService.Info("LicenseSettingsPage: License activated successfully");
            }
            else
            {
                ShowLicenseError(result.ErrorMessage ?? "License validation failed.");
            }
        }
        catch (Exception ex)
        {
            ShowLicenseError($"Activation failed: {ex.Message}");
            LoggingService.Error($"LicenseSettingsPage: License activation failed: {ex.Message}");
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
            RefreshLicenseUI();
            LoggingService.Info("LicenseSettingsPage: License deactivated");
        }
    }

    private void ManageSubscription_Click(object sender, RoutedEventArgs e)
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

    private void PurchaseLicense_Click(object sender, RoutedEventArgs e)
    {
        if (!LicenseManager.Instance.OpenPurchasePage(out var errorMessage))
        {
            WpfMessageBox.Show(
                Loc.S("settings.general.support.openFailed", errorMessage ?? ""),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowLicenseError(string message)
    {
        LicenseErrorText.Text = message;
        LicenseErrorText.Visibility = Visibility.Visible;
    }
}
