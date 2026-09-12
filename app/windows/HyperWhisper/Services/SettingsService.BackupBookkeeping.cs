namespace HyperWhisper.Services;

public partial class SettingsService
{
    // =========================================================================
    // BACKUP ROUND-TRIP BOOKKEEPING
    // =========================================================================

    /// <summary>
    /// Raw JSON mirror of the universal <c>settings</c> tree holding only the keys
    /// the last imported backup carried that this build has no property for —
    /// <c>{"textOutput":{"storeWordTimestamps":true}}</c>. Section-keyed, so a key
    /// is re-emitted at its ORIGINAL nesting level and never at the root.
    /// </summary>
    /// <remarks>
    /// Written by <c>UniversalBackupMapper.ApplySettings</c> (REPLACE, not merge —
    /// the blob describes the last imported file) and read back by
    /// <c>UniversalBackupMapper.MapSettings</c>. No <c>NotifySettingsChanged()</c>:
    /// no UI observes this, and raising the event would re-register global
    /// shortcuts on a bookkeeping write.
    /// </remarks>
    public string? BackupUnknownSettings
    {
        get => _settings.BackupUnknownSettings;
        set
        {
            if (_settings.BackupUnknownSettings != value)
            {
                _settings.BackupUnknownSettings = value;
                Save();
            }
        }
    }

    /// <summary>
    /// Raw JSON map of the NON-<c>"windows"</c> top-level <c>platformExtensions</c>
    /// slices of the last imported backup — <c>{"macos":{…},"linux":{…}}</c>.
    /// </summary>
    /// <remarks>
    /// Separate state from <see cref="BackupUnknownSettings"/>, with a different
    /// shape and a different merge point (<c>BuildPlatformExtensions</c> rather
    /// than <c>MapSettings</c>). Reusing one field for both would re-emit
    /// <c>storeWordTimestamps</c> under <c>platformExtensions</c>. No
    /// <c>NotifySettingsChanged()</c>, for the same reason as above.
    /// </remarks>
    public string? BackupForeignPlatformExtensions
    {
        get => _settings.BackupForeignPlatformExtensions;
        set
        {
            if (_settings.BackupForeignPlatformExtensions != value)
            {
                _settings.BackupForeignPlatformExtensions = value;
                Save();
            }
        }
    }

    /// <summary>
    /// Raw JSON object of TOP-LEVEL backup keys the last imported file carried that
    /// this build has no property for. Never holds <c>platformExtensions</c> —
    /// that has <see cref="BackupForeignPlatformExtensions"/>.
    /// </summary>
    public string? BackupUnknownRootKeys
    {
        get => _settings.BackupUnknownRootKeys;
        set
        {
            if (_settings.BackupUnknownRootKeys != value)
            {
                _settings.BackupUnknownRootKeys = value;
                Save();
            }
        }
    }

}
