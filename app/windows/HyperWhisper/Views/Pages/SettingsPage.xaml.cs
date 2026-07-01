// SETTINGS PAGE
// Main settings page with sidebar navigation.
// Uses Frame navigation to display individual section pages.

using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Services;
using HyperWhisper.Views.Pages.Settings;

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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SelectSection(_initialSection);
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
            // "License" / "Credits" are legacy aliases for the unified Cloud panel.
            "Cloud" or "License" or "Credits" => new CloudAccountSettingsPage(),
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
        // Normalize legacy deep-links ("License" / "Credits") onto the unified Cloud tag.
        var normalized = sectionTag is "License" or "Credits" ? "Cloud" : sectionTag;

        foreach (var item in SectionList.Items)
        {
            if (item is ListBoxItem listBoxItem && listBoxItem.Tag?.ToString() == normalized)
            {
                SectionList.SelectedItem = listBoxItem;
                return;
            }
        }

        SectionList.SelectedIndex = 0;
    }
}
