using System;

namespace HyperWhisper.Services;

public partial class SettingsService
{
    // =========================================================================
    // PUBLIC PROPERTIES
    // =========================================================================

    /// <summary>
    /// Gets or sets the folder where recordings are stored.
    /// Defaults to Documents\\HyperWhisper\\recordings for new installs,
    /// but retains legacy %LOCALAPPDATA%\\HyperWhisper\\Audio for existing users.
    /// </summary>
    public string RecordingsFolder
    {
        get => string.IsNullOrWhiteSpace(_settings.RecordingsFolder)
            ? GetDefaultRecordingsFolder()
            : _settings.RecordingsFolder!;
        set
        {
            if (_settings.RecordingsFolder != value)
            {
                _settings.RecordingsFolder = value;
                Save();
                LoggingService.Debug($"SettingsService: RecordingsFolder set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to compress WAV recordings to M4A after transcription.
    /// When enabled, completed WAV recordings are converted to AAC M4A files
    /// using Windows Media Foundation to reduce disk usage.
    /// Default: true for new installs; existing settings files missing this key keep legacy WAV storage.
    /// </summary>
    public bool StoreAsM4A
    {
        get => _settings.StoreAsM4A ?? (!_settingsFileExists);
        set
        {
            if (StoreAsM4A != value)
            {
                _settings.StoreAsM4A = value;
                Save();
                LoggingService.Debug($"SettingsService: StoreAsM4A set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether recorded audio is kept on disk after a transcription completes.
    /// Cross-platform setting: <c>storage.keepAudioFiles</c> in the universal
    /// backup, <c>SettingsManager.keepAudioFiles</c> on macOS,
    /// <c>storage.keepAudioFiles</c> on Linux. Default: true.
    /// </summary>
    /// <remarks>
    /// SCOPE, stated plainly: Windows PERSISTS and round-trips this value but does
    /// not yet act on it — retention on Windows is driven by
    /// <see cref="AutoDeleteEnabled"/> / <see cref="AutoDeleteDaysOld"/> and there
    /// is no Storage-page toggle for it. Before this property existed the value a
    /// macOS or Linux backup carried was discarded outright (issue #288), which is
    /// the fidelity bug being closed here. Making an imported backup start DELETING
    /// a user's recordings is a separate, user-visible feature that needs its own
    /// UI and its own review; it is deliberately not bundled into a restore-fidelity
    /// change.
    /// </remarks>
    public bool KeepAudioFiles
    {
        get => _settings.KeepAudioFiles ?? true;
        set
        {
            if (KeepAudioFiles != value)
            {
                _settings.KeepAudioFiles = value;
                Save();
                LoggingService.Debug($"SettingsService: KeepAudioFiles set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Tracks if the user explicitly selected an alternate storage location.
    /// Used to avoid repeatedly prompting when the default location is unavailable.
    /// </summary>
    public bool UserChoseAlternateStorage
    {
        get => _settings.UserChoseAlternateStorage ?? false;
        set
        {
            if ((_settings.UserChoseAlternateStorage ?? false) != value)
            {
                _settings.UserChoseAlternateStorage = value;
                Save();
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the selected mode ID.
    /// Setting this property automatically saves to disk.
    /// </summary>
    public Guid? SelectedModeId
    {
        get => _settings.SelectedModeId;
        set
        {
            if (_settings.SelectedModeId != value)
            {
                _settings.SelectedModeId = value;
                Save();
                LoggingService.Debug($"SettingsService: SelectedModeId set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the last selected model file name (e.g., "ggml-base.bin").
    /// Setting this property automatically saves to disk.
    /// </summary>
    public string? LastSelectedModel
    {
        get => _settings.LastSelectedModel;
        set
        {
            if (_settings.LastSelectedModel != value)
            {
                _settings.LastSelectedModel = value;
                Save();
                LoggingService.Debug($"SettingsService: LastSelectedModel set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Persisted base language code for the Model Library language filter.
    /// Empty string = "Any language" (no filtering). Restored on next open.
    /// Setting this property automatically saves to disk.
    /// </summary>
    public string ModelLibraryLanguageFilter
    {
        get => _settings.ModelLibraryLanguageFilter ?? "";
        set
        {
            var normalized = value ?? "";
            if (_settings.ModelLibraryLanguageFilter != normalized)
            {
                _settings.ModelLibraryLanguageFilter = normalized;
                Save();
            }
        }
    }

    /// <summary>
    /// Gets or sets the last selected microphone device ID.
    /// Setting this property automatically saves to disk.
    /// </summary>
    public string? LastSelectedMicrophone
    {
        get => _settings.LastSelectedMicrophone;
        set
        {
            if (_settings.LastSelectedMicrophone != value)
            {
                _settings.LastSelectedMicrophone = value;
                Save();
                LoggingService.Debug($"SettingsService: LastSelectedMicrophone set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
