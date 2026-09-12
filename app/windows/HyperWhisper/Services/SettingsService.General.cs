namespace HyperWhisper.Services;

public partial class SettingsService
{
    // =========================================================================
    // GENERAL SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether to automatically paste transcribed text into the focused application.
    /// When enabled, after transcription completes:
    /// 1. Text is copied to clipboard
    /// 2. Previously focused window is reactivated
    /// 3. Ctrl+V is simulated to paste
    ///
    /// When disabled, text is only copied to clipboard.
    /// Default: true
    /// </summary>
    public bool AutoPasteEnabled
    {
        get => _settings.AutoPasteEnabled ?? true;
        set
        {
            if ((_settings.AutoPasteEnabled ?? true) != value)
            {
                _settings.AutoPasteEnabled = value;
                Save();
                LoggingService.Debug($"SettingsService: AutoPasteEnabled set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to start the app minimized to the system tray.
    /// When enabled, the main window is hidden on startup.
    /// Users can show the window via the system tray icon.
    /// Default: false
    /// </summary>
    public bool LaunchMinimized
    {
        get => _settings.LaunchMinimized ?? false;
        set
        {
            if ((_settings.LaunchMinimized ?? false) != value)
            {
                _settings.LaunchMinimized = value;
                Save();
                LoggingService.Debug($"SettingsService: LaunchMinimized set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to show the recording overlay window during recording.
    /// When enabled, a floating window with audio level visualization is shown.
    /// When disabled, recording happens silently in the background.
    /// Default: true
    /// </summary>
    public bool ShowRecordingWindow
    {
        get => _settings.ShowRecordingWindow ?? true;
        set
        {
            if ((_settings.ShowRecordingWindow ?? true) != value)
            {
                _settings.ShowRecordingWindow = value;
                Save();
                LoggingService.Debug($"SettingsService: ShowRecordingWindow set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether clicking the window close button minimizes to system tray instead of exiting.
    /// When enabled (default), closing the window hides it to the tray.
    /// When disabled, closing the window exits the application.
    /// Default: true (matches typical utility app behavior)
    /// </summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray ?? true;
        set
        {
            if ((_settings.MinimizeToTray ?? true) != value)
            {
                _settings.MinimizeToTray = value;
                Save();
                LoggingService.Debug($"SettingsService: MinimizeToTray set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
