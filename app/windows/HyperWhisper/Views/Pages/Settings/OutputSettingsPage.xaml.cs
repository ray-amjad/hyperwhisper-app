// OUTPUT SETTINGS PAGE
// Handles text output settings including auto-paste and clipboard restoration.

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Pages.Settings;

public partial class OutputSettingsPage : Page
{
    public OutputSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeSettings();
    }

    private void InitializeSettings()
    {
        // Load auto-paste state
        AutoPasteCheckbox.Checked -= AutoPasteCheckbox_Checked;
        AutoPasteCheckbox.Unchecked -= AutoPasteCheckbox_Unchecked;
        AutoPasteCheckbox.IsChecked = SettingsService.Instance.AutoPasteEnabled;
        AutoPasteCheckbox.Checked += AutoPasteCheckbox_Checked;
        AutoPasteCheckbox.Unchecked += AutoPasteCheckbox_Unchecked;

        // Load text cleanup settings
        RemoveFillerWordsCheckbox.Checked -= RemoveFillerWordsCheckbox_Checked;
        RemoveFillerWordsCheckbox.Unchecked -= RemoveFillerWordsCheckbox_Unchecked;
        RemoveFillerWordsCheckbox.IsChecked = SettingsService.Instance.RemoveFillerWords;
        RemoveFillerWordsCheckbox.Checked += RemoveFillerWordsCheckbox_Checked;
        RemoveFillerWordsCheckbox.Unchecked += RemoveFillerWordsCheckbox_Unchecked;

        AutocapitalizeInsertCheckbox.Checked -= AutocapitalizeInsertCheckbox_Checked;
        AutocapitalizeInsertCheckbox.Unchecked -= AutocapitalizeInsertCheckbox_Unchecked;
        AutocapitalizeInsertCheckbox.IsChecked = SettingsService.Instance.AutocapitalizeInsert;
        AutocapitalizeInsertCheckbox.Checked += AutocapitalizeInsertCheckbox_Checked;
        AutocapitalizeInsertCheckbox.Unchecked += AutocapitalizeInsertCheckbox_Unchecked;

        // Load clipboard restoration settings
        RestoreClipboardCheckbox.Checked -= RestoreClipboardCheckbox_Checked;
        RestoreClipboardCheckbox.Unchecked -= RestoreClipboardCheckbox_Unchecked;
        RestoreClipboardCheckbox.IsChecked = SettingsService.Instance.RestoreClipboardAfterPaste;
        RestoreClipboardCheckbox.Checked += RestoreClipboardCheckbox_Checked;
        RestoreClipboardCheckbox.Unchecked += RestoreClipboardCheckbox_Unchecked;

        HideClipboardHistoryCheckbox.Checked -= HideClipboardHistoryCheckbox_Checked;
        HideClipboardHistoryCheckbox.Unchecked -= HideClipboardHistoryCheckbox_Unchecked;
        HideClipboardHistoryCheckbox.IsChecked = SettingsService.Instance.HideFromClipboardHistory;
        HideClipboardHistoryCheckbox.Checked += HideClipboardHistoryCheckbox_Checked;
        HideClipboardHistoryCheckbox.Unchecked += HideClipboardHistoryCheckbox_Unchecked;

        UpdateRestoreDelayUI();

        LoggingService.Debug($"OutputSettingsPage: Initialized (autoPaste={SettingsService.Instance.AutoPasteEnabled}, removeFillerWords={SettingsService.Instance.RemoveFillerWords}, autocapitalizeInsert={SettingsService.Instance.AutocapitalizeInsert}, restoreClipboard={SettingsService.Instance.RestoreClipboardAfterPaste}, hideClipboardHistory={SettingsService.Instance.HideFromClipboardHistory}, delay={SettingsService.Instance.ClipboardRestoreDelaySeconds}s)");
    }

    // =========================================================================
    // AUTO-PASTE
    // =========================================================================

    private void AutoPasteCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.AutoPasteEnabled = true;
        LoggingService.Info("OutputSettingsPage: Enabled auto-paste");
    }

    private void AutoPasteCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.AutoPasteEnabled = false;
        LoggingService.Info("OutputSettingsPage: Disabled auto-paste");
    }

    // =========================================================================
    // TEXT CLEANUP
    // =========================================================================

    private void RemoveFillerWordsCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.RemoveFillerWords = true;
        LoggingService.Info("OutputSettingsPage: Enabled filler word removal");
    }

    private void RemoveFillerWordsCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.RemoveFillerWords = false;
        LoggingService.Info("OutputSettingsPage: Disabled filler word removal");
    }

    // =========================================================================
    // AUTOCAPITALIZE INSERT
    // =========================================================================

    private void AutocapitalizeInsertCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.AutocapitalizeInsert = true;
        LoggingService.Info("OutputSettingsPage: Enabled autocapitalize insert");

        // Quick UIA self-test: if we can't even read FocusedElement on our own
        // settings window, UIA is likely broken in this process and the feature
        // won't work in most apps. Show an informational dialog — unlike macOS
        // (which has a system-wide AX permission), Windows UIA failure is
        // per-app, so we DO NOT revert the toggle.
        try
        {
            _ = AutomationElement.FocusedElement;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"OutputSettingsPage: UIA self-test failed: {ex.Message}");
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                Loc.S("settings.output.autocapitalizeInsert.permissionMessage"),
                Loc.S("settings.output.autocapitalizeInsert.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void AutocapitalizeInsertCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.AutocapitalizeInsert = false;
        LoggingService.Info("OutputSettingsPage: Disabled autocapitalize insert");
    }

    // =========================================================================
    // CLIPBOARD RESTORATION
    // =========================================================================

    private void RestoreClipboardCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.RestoreClipboardAfterPaste = true;
        UpdateRestoreDelayUI();
        LoggingService.Info("OutputSettingsPage: Enabled clipboard restoration");
    }

    private void RestoreClipboardCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.RestoreClipboardAfterPaste = false;
        UpdateRestoreDelayUI();
        LoggingService.Info("OutputSettingsPage: Disabled clipboard restoration");
    }

    private void UpdateRestoreDelayUI()
    {
        var isEnabled = SettingsService.Instance.RestoreClipboardAfterPaste;
        RestoreDelayPanel.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;

        var delay = (int)SettingsService.Instance.ClipboardRestoreDelaySeconds;
        RestoreDelayText.Text = $"{delay} sec";

        RestoreDelayDecrease.IsEnabled = delay > 1;
        RestoreDelayIncrease.IsEnabled = delay < 60;
    }

    private void RestoreDelayDecrease_Click(object sender, RoutedEventArgs e)
    {
        var currentDelay = SettingsService.Instance.ClipboardRestoreDelaySeconds;
        if (currentDelay > 1)
        {
            SettingsService.Instance.ClipboardRestoreDelaySeconds = currentDelay - 1;
            UpdateRestoreDelayUI();
            LoggingService.Debug($"OutputSettingsPage: Clipboard restore delay decreased to {currentDelay - 1}s");
        }
    }

    private void RestoreDelayIncrease_Click(object sender, RoutedEventArgs e)
    {
        var currentDelay = SettingsService.Instance.ClipboardRestoreDelaySeconds;
        if (currentDelay < 60)
        {
            SettingsService.Instance.ClipboardRestoreDelaySeconds = currentDelay + 1;
            UpdateRestoreDelayUI();
            LoggingService.Debug($"OutputSettingsPage: Clipboard restore delay increased to {currentDelay + 1}s");
        }
    }

    private void HideClipboardHistoryCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.HideFromClipboardHistory = true;
        LoggingService.Info("OutputSettingsPage: Enabled clipboard history hiding");
    }

    private void HideClipboardHistoryCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.HideFromClipboardHistory = false;
        LoggingService.Info("OutputSettingsPage: Disabled clipboard history hiding");
    }
}
