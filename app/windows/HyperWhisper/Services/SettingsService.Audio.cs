namespace HyperWhisper.Services;

public partial class SettingsService
{
    /// <summary>
    /// Whether to automatically increase low mic volume to 90% when recording starts.
    /// Restores the original level when recording stops if HyperWhisper changed it.
    /// Default: true
    /// </summary>
    public bool AutoIncreaseMicVolume
    {
        get => _settings.AutoIncreaseMicVolume ?? true;
        set
        {
            if ((_settings.AutoIncreaseMicVolume ?? true) != value)
            {
                _settings.AutoIncreaseMicVolume = value;
                Save();
                LoggingService.Debug($"SettingsService: AutoIncreaseMicVolume set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
