// SOUND SETTINGS PAGE
// Handles sound effect preferences for recording start/stop audio feedback.

using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Pages.Settings;

public partial class SoundSettingsPage : Page
{
    public SoundSettingsPage()
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
        EnableSoundEffectsCheckbox.Checked -= EnableSoundEffectsCheckbox_Checked;
        EnableSoundEffectsCheckbox.Unchecked -= EnableSoundEffectsCheckbox_Unchecked;
        EnableSoundEffectsCheckbox.IsChecked = SettingsService.Instance.EnableSoundEffects;
        EnableSoundEffectsCheckbox.Checked += EnableSoundEffectsCheckbox_Checked;
        EnableSoundEffectsCheckbox.Unchecked += EnableSoundEffectsCheckbox_Unchecked;

        AutoIncreaseMicVolumeCheckbox.Checked -= AutoIncreaseMicVolumeCheckbox_Checked;
        AutoIncreaseMicVolumeCheckbox.Unchecked -= AutoIncreaseMicVolumeCheckbox_Unchecked;
        AutoIncreaseMicVolumeCheckbox.IsChecked = SettingsService.Instance.AutoIncreaseMicVolume;
        AutoIncreaseMicVolumeCheckbox.Checked += AutoIncreaseMicVolumeCheckbox_Checked;
        AutoIncreaseMicVolumeCheckbox.Unchecked += AutoIncreaseMicVolumeCheckbox_Unchecked;

        KeepMicrophoneWarmCheckbox.Checked -= KeepMicrophoneWarmCheckbox_Checked;
        KeepMicrophoneWarmCheckbox.Unchecked -= KeepMicrophoneWarmCheckbox_Unchecked;
        KeepMicrophoneWarmCheckbox.IsChecked = SettingsService.Instance.KeepMicrophoneWarm;
        KeepMicrophoneWarmCheckbox.Checked += KeepMicrophoneWarmCheckbox_Checked;
        KeepMicrophoneWarmCheckbox.Unchecked += KeepMicrophoneWarmCheckbox_Unchecked;

        MediaControlModeComboBox.SelectionChanged -= MediaControlModeComboBox_SelectionChanged;
        SelectMediaControlMode(SettingsService.Instance.MediaControlMode);
        MediaControlModeComboBox.SelectionChanged += MediaControlModeComboBox_SelectionChanged;

        LoggingService.Debug($"SoundSettingsPage: Initialized (enableSoundEffects={SettingsService.Instance.EnableSoundEffects}, autoIncreaseMicVolume={SettingsService.Instance.AutoIncreaseMicVolume}, keepMicrophoneWarm={SettingsService.Instance.KeepMicrophoneWarm}, mediaControlMode={SettingsService.Instance.MediaControlMode})");
    }

    private void EnableSoundEffectsCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.EnableSoundEffects = true;
        LoggingService.Info("SoundSettingsPage: Enabled sound effects");
    }

    private void EnableSoundEffectsCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.EnableSoundEffects = false;
        LoggingService.Info("SoundSettingsPage: Disabled sound effects");
    }

    private void AutoIncreaseMicVolumeCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.AutoIncreaseMicVolume = true;
        LoggingService.Info("SoundSettingsPage: Enabled auto-increase mic volume");
    }

    private void AutoIncreaseMicVolumeCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.AutoIncreaseMicVolume = false;
        LoggingService.Info("SoundSettingsPage: Disabled auto-increase mic volume");
    }

    private void KeepMicrophoneWarmCheckbox_Checked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.KeepMicrophoneWarm = true;
        LoggingService.Info("SoundSettingsPage: Enabled microphone keep-warm");
    }

    private void KeepMicrophoneWarmCheckbox_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.KeepMicrophoneWarm = false;
        LoggingService.Info("SoundSettingsPage: Disabled microphone keep-warm");
    }

    private void MediaControlModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaControlModeComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string mode)
        {
            return;
        }

        SettingsService.Instance.MediaControlMode = mode;
        LoggingService.Info($"SoundSettingsPage: Media control mode set to {SettingsService.Instance.MediaControlMode}");
    }

    private void SelectMediaControlMode(string mode)
    {
        foreach (var item in MediaControlModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
            {
                MediaControlModeComboBox.SelectedItem = item;
                return;
            }
        }

        MediaControlModeComboBox.SelectedIndex = 0;
    }

}
