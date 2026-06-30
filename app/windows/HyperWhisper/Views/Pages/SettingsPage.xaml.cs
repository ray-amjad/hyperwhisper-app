// SETTINGS PAGE
// Main settings page with sidebar navigation.
// Uses Frame navigation to display individual section pages.

using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Services;
using HyperWhisper.Views.Pages.Settings;

using LicenseStatus = HyperWhisper.Models.LicenseStatus;

namespace HyperWhisper.Views.Pages;

public partial class SettingsPage : Page
{
    private readonly string _initialSection;

    public SettingsPage()
        : this("General")
    {
    }

    public SettingsPage(string initialSection)
    {
        _initialSection = string.IsNullOrWhiteSpace(initialSection) ? "General" : initialSection;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to license status changes to update Credits visibility in real-time
        LicenseManager.Instance.LicenseStatusChanged += OnLicenseStatusChanged;

        // Update Credits visibility based on license status
        UpdateCreditsVisibility();

        SelectSection(_initialSection);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unsubscribe to prevent memory leaks
        LicenseManager.Instance.LicenseStatusChanged -= OnLicenseStatusChanged;
    }

    private void OnLicenseStatusChanged(object? sender, System.EventArgs e)
    {
        // Ensure we're on the UI thread
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnLicenseStatusChanged(sender, e));
            return;
        }

        UpdateCreditsVisibility();
    }

    private void UpdateCreditsVisibility()
    {
        // Only show Credits for licensed users (similar to macOS behavior)
        var isLicensed = LicenseManager.Instance.LicenseStatus == LicenseStatus.Active;
        CreditsNavItem.Visibility = isLicensed ? Visibility.Visible : Visibility.Collapsed;

        if (!isLicensed && SectionList.SelectedItem == CreditsNavItem)
        {
            SelectSection("General");
        }
    }

    /// <summary>
    /// Handles section navigation when a sidebar item is selected.
    /// Navigates to the corresponding section page in the content frame.
    /// </summary>
    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListBoxItem selectedItem)
            return;

        var sectionTag = selectedItem.Tag?.ToString();
        if (string.IsNullOrEmpty(sectionTag))
            return;

        NavigateToSection(sectionTag);
    }

    private void NavigateToSection(string sectionTag)
    {
        // Guard: ContentFrame may be null during InitializeComponent
        if (ContentFrame == null)
            return;

        Page targetPage = sectionTag switch
        {
            "General" => new GeneralSettingsPage(),
            "Appearance" => new AppearanceSettingsPage(),
            "Sound" => new SoundSettingsPage(),
            "License" => new LicenseSettingsPage(),
            "Credits" => new CreditsSettingsPage(),
            "Storage" => new StorageSettingsPage(),
            "Output" => new OutputSettingsPage(),
            "LocalApi" => new LocalApiSettingsPage(),
            "Shortcuts" => new ShortcutsSettingsPage(),
            "Backup" => new BackupExportSettingsPage(),
            "About" => new AboutSettingsPage(),
            _ => new GeneralSettingsPage()
        };

        ContentFrame.Navigate(targetPage);
        LoggingService.Debug($"Settings: Navigated to {sectionTag} section");
    }

    public void SelectSection(string sectionTag)
    {
        foreach (var item in SectionList.Items)
        {
            if (item is ListBoxItem listBoxItem && listBoxItem.Tag?.ToString() == sectionTag)
            {
                SectionList.SelectedItem = listBoxItem;
                return;
            }
        }

        SectionList.SelectedIndex = 0;
    }
}
