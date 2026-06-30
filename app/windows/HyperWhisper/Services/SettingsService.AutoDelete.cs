// SETTINGS SERVICE - AUTO DELETE SETTINGS
// Stores user preferences for automatic cleanup of old transcripts.

using System;

namespace HyperWhisper.Services;

public partial class SettingsService
{
    /// <summary>
    /// Whether automatic deletion of old transcripts is enabled.
    /// Default: false (opt-in feature)
    /// </summary>
    public bool AutoDeleteEnabled
    {
        get => _settings.AutoDeleteEnabled ?? false;
        set
        {
            if ((_settings.AutoDeleteEnabled ?? false) != value)
            {
                _settings.AutoDeleteEnabled = value;
                Save();
                LoggingService.Debug($"SettingsService: AutoDeleteEnabled set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Automatically delete transcripts older than this many days.
    /// Default: 30 days
    /// Range: 1-365 days
    /// </summary>
    public int AutoDeleteDaysOld
    {
        get => _settings.AutoDeleteDaysOld ?? 30;
        set
        {
            var clampedValue = Math.Max(1, Math.Min(365, value));
            if ((_settings.AutoDeleteDaysOld ?? 30) != clampedValue)
            {
                _settings.AutoDeleteDaysOld = clampedValue;
                Save();
                LoggingService.Debug($"SettingsService: AutoDeleteDaysOld set to: {clampedValue}");
                NotifySettingsChanged();
            }
        }
    }
}
