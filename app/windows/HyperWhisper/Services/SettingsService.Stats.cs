namespace HyperWhisper.Services;

public partial class SettingsService
{
    // =========================================================================
    // HOME STATS BAR
    // =========================================================================

    /// <summary>
    /// Assumed typing speed (words per minute) used by the Home stats bar
    /// to compute "minutes saved this week". Default: 40 WPM.
    /// </summary>
    public int TypingSpeedWPM
    {
        get => _settings.TypingSpeedWPM ?? 40;
        set
        {
            if ((_settings.TypingSpeedWPM ?? 40) != value)
            {
                _settings.TypingSpeedWPM = value;
                Save();
                LoggingService.Debug($"SettingsService: TypingSpeedWPM set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
