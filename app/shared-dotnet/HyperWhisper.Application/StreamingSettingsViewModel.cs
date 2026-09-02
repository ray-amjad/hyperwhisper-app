namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// Live transcription as its own page. The Windows app gives streaming a sidebar entry of its
/// own, and the shell picks a page template from the view model type, so streaming needs a type
/// the settings page does not also match. Every value still lives on <see cref="SettingsViewModel"/>;
/// this only re-presents it.
/// </summary>
public sealed class StreamingSettingsViewModel(SettingsViewModel settings)
{
    public SettingsViewModel Settings { get; } = settings
        ?? throw new ArgumentNullException(nameof(settings));
}
