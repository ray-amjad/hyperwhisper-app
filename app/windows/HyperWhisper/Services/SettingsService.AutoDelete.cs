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

    /// <summary>
    /// When the last cleanup sweep finished, in UTC, or null if no sweep has ever run
    /// on this profile. This is the value behind the Storage page's "last cleanup"
    /// line, which is a statement about the profile: while it was a field on
    /// AutoDeleteService it was forgotten on every launch and the line reverted to
    /// "No cleanup has run yet" (issue #514).
    /// </summary>
    public DateTime? AutoDeleteLastCleanupUtc =>
        _settings.AutoDeleteLastCleanupUtc is { } stamp ? AsUtc(stamp) : null;

    /// <summary>
    /// How many transcripts the last cleanup sweep deleted. Zero when no sweep has run,
    /// which is why it must be read together with <see cref="AutoDeleteLastCleanupUtc"/>:
    /// "0 deleted just now" and "never run" are different facts.
    /// </summary>
    public int AutoDeleteLastCleanupDeleted => _settings.AutoDeleteLastCleanupDeleted ?? 0;

    /// <summary>
    /// Records that a cleanup sweep finished. The two values move together and are
    /// written in one Save.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT raise SettingsChanged, unlike every preference setter here.
    /// The sweep runs hourly on a timer thread, and the one subscriber
    /// (MainViewModel.OnSettingsChanged) re-registers the global hotkeys and reconfigures
    /// the microphone keep-warm — an hourly hotkey re-registration would be a side effect
    /// of recording a statistic. Nothing binds to these two values; the Storage page reads
    /// them when it loads and again after Delete Now.
    /// </remarks>
    public void RecordAutoDeleteCleanup(DateTime when, int transcriptsDeleted)
    {
        _settings.AutoDeleteLastCleanupUtc = AsUtc(when);
        _settings.AutoDeleteLastCleanupDeleted = transcriptsDeleted;
        Save();
        LoggingService.Debug(
            $"SettingsService: AutoDelete last cleanup recorded at {AsUtc(when):O} ({transcriptsDeleted} deleted)");
    }

    /// <summary>
    /// A DateTime read back from settings.json can arrive Utc, Local or Unspecified
    /// depending on how the instant was written. Only this profile writes the value and
    /// it always writes UTC, so an Unspecified stamp IS UTC and must be labelled, never
    /// converted — <c>ToUniversalTime()</c> on Unspecified would assume local and shift
    /// it by the machine's offset.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
