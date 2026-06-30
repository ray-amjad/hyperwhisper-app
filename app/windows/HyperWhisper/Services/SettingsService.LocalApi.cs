namespace HyperWhisper.Services;

/// <summary>
/// SETTINGS — LOCAL API SERVER
///
/// Tracks the on/off toggle and the most recent port the kernel handed us, so
/// scripts that hard-code 127.0.0.1:PORT keep working across launches. Mirrors
/// the macOS UserDefaults keys (`localAPIServerEnabled`, `localAPIServerPersistedPort`).
/// </summary>
public partial class SettingsService
{
    public bool LocalApiServerEnabled
    {
        get => _settings.LocalApiServerEnabled ?? false;
        set
        {
            if ((_settings.LocalApiServerEnabled ?? false) != value)
            {
                _settings.LocalApiServerEnabled = value;
                Save();
                LoggingService.Debug($"SettingsService: LocalApiServerEnabled set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Last bound port. 0 means "no preference yet" — the server will bind an
    /// ephemeral port and persist whatever the OS hands back. If the persisted
    /// port is taken on next launch, we clear it and fall back to ephemeral.
    /// </summary>
    public int LocalApiServerPersistedPort
    {
        get => _settings.LocalApiServerPersistedPort ?? 0;
        set
        {
            if ((_settings.LocalApiServerPersistedPort ?? 0) != value)
            {
                _settings.LocalApiServerPersistedPort = value;
                Save();
                LoggingService.Debug($"SettingsService: LocalApiServerPersistedPort set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
