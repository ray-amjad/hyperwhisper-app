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
    : SettingsSectionViewModel(settings);

public sealed class StorageSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class OutputSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class LocalApiSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class ShortcutsSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);

public sealed class AppearanceSettingsViewModel(SettingsViewModel settings)
    : SettingsSectionViewModel(settings);
