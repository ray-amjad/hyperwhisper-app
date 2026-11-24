using System.ComponentModel;

namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// The Windows settings pane is ten sections, each its own page. The portable shell picks a page
/// template from the view model type, so each section needs a type of its own. Every value still
/// lives on <see cref="SettingsViewModel"/>; these types only re-present it.
/// </summary>
public abstract class SettingsSectionViewModel(SettingsViewModel settings)
{
    public SettingsViewModel Settings { get; } = settings
        ?? throw new ArgumentNullException(nameof(settings));
}

public sealed class GeneralSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class SoundSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings)
{
    /// <summary>The microphone picker and its level test, at the top of the Microphone card.
    /// Null on a shell without a recording workflow.</summary>
    public MicrophonePickerViewModel? Microphone { get; internal set; }
}

public sealed class StorageSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings), INotifyPropertyChanged
{
    private string _lastCleanupText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The page's "Last cleanup" line, written by the shell after each sweep. It lives on
    /// the page's view model because the page is a template built only while shown and rebuilt on
    /// every visit (#1371), and not on <see cref="SettingsViewModel"/>, whose every change arms the
    /// settings auto-save: a save runs a sweep, so the line would loop save -> sweep -> save.
    /// Display only: never persisted.</summary>
    public string LastCleanupText
    {
        get => _lastCleanupText;
        set
        {
            value ??= string.Empty;
            if (value == _lastCleanupText) return;
            _lastCleanupText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastCleanupText)));
        }
    }
}

public sealed class OutputSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class LocalApiSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class ShortcutsSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class AppearanceSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);
