// SETTINGS SERVICE - SOUND SETTINGS
// Stores user preferences for sound effects during recording.

namespace HyperWhisper.Services;

public partial class SettingsService
{
    /// <summary>
    /// Whether to play sound effects when recording starts and stops.
    /// Default: true
    /// </summary>
    public bool EnableSoundEffects
    {
        get => _settings.EnableSoundEffects ?? true;
        set
        {
            if ((_settings.EnableSoundEffects ?? true) != value)
            {
                _settings.EnableSoundEffects = value;
                Save();
                LoggingService.Debug($"SettingsService: EnableSoundEffects set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
