using System;

namespace HyperWhisper.Services;

public partial class SettingsService
{
    // =========================================================================
    // OUTPUT SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether to remove common filler words from raw transcripts when AI post-processing is disabled.
    /// Default: true (preserves historical behavior)
    /// </summary>
    public bool RemoveFillerWords
    {
        get => _settings.RemoveFillerWords ?? true;
        set
        {
            if ((_settings.RemoveFillerWords ?? true) != value)
            {
                _settings.RemoveFillerWords = value;
                Save();
                LoggingService.Debug($"SettingsService: RemoveFillerWords set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// When enabled, lowercases the first letter of inserted transcript text
    /// if the caret is mid-sentence in the focused field. Leaves the text
    /// untouched at sentence start. Falls back to pass-through when the UIA
    /// probe can't read the focused element (e.g. some Electron/web apps).
    /// Default: true
    /// </summary>
    public bool AutocapitalizeInsert
    {
        get => _settings.AutocapitalizeInsert ?? true;
        set
        {
            if ((_settings.AutocapitalizeInsert ?? true) != value)
            {
                _settings.AutocapitalizeInsert = value;
                Save();
                LoggingService.Debug($"SettingsService: AutocapitalizeInsert set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // CLIPBOARD RESTORATION SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether to restore the original clipboard content after pasting transcription.
    /// When enabled, the clipboard content that existed before recording starts
    /// is automatically restored after a configurable delay following the paste.
    ///
    /// FLOW:
    /// 1. User starts recording → original clipboard is captured
    /// 2. Transcription completes → text is pasted (overwrites clipboard)
    /// 3. After delay → original clipboard content is restored
    ///
    /// This matches macOS HyperWhisper behavior where users can paste again
    /// with their original clipboard content after the transcription is inserted.
    /// Default: true
    /// </summary>
    public bool RestoreClipboardAfterPaste
    {
        get => _settings.RestoreClipboardAfterPaste ?? true;
        set
        {
            if ((_settings.RestoreClipboardAfterPaste ?? true) != value)
            {
                _settings.RestoreClipboardAfterPaste = value;
                Save();
                LoggingService.Debug($"SettingsService: RestoreClipboardAfterPaste set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Delay in seconds before restoring the original clipboard content.
    /// This delay allows users to:
    /// - Paste the transcription multiple times if needed
    /// - Complete any clipboard operations before restoration
    ///
    /// Range: 1-60 seconds
    /// Default: 10 seconds (matches macOS default)
    /// </summary>
    public double ClipboardRestoreDelaySeconds
    {
        get => _settings.ClipboardRestoreDelaySeconds ?? 10.0;
        set
        {
            // Clamp value to valid range (1-60 seconds)
            var clampedValue = Math.Max(1.0, Math.Min(60.0, value));
            if ((_settings.ClipboardRestoreDelaySeconds ?? 10.0) != clampedValue)
            {
                _settings.ClipboardRestoreDelaySeconds = clampedValue;
                Save();
                LoggingService.Debug($"SettingsService: ClipboardRestoreDelaySeconds set to: {clampedValue}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to hide transcription text from Windows clipboard history (Win+V).
    /// Uses the ExcludeClipboardContentFromMonitorProcessing clipboard format
    /// to prevent transcriptions from appearing in clipboard history and
    /// third-party clipboard managers.
    ///
    /// Matches macOS behavior where org.nspasteboard.ConcealedType is used.
    /// Default: true
    /// </summary>
    public bool HideFromClipboardHistory
    {
        get => _settings.HideFromClipboardHistory ?? true;
        set
        {
            if ((_settings.HideFromClipboardHistory ?? true) != value)
            {
                _settings.HideFromClipboardHistory = value;
                Save();
                LoggingService.Debug($"SettingsService: HideFromClipboardHistory set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
