using System.IO;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Shortcut and push-to-talk settings split out for readability.
/// </summary>
public partial class SettingsService
{
    private static readonly KeyboardShortcut DefaultToggleShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Alt");
    private static readonly KeyboardShortcut DefaultCancelShortcut = KeyboardShortcut.FromPersistedString("Esc");
    private static readonly KeyboardShortcut DefaultChangeModeShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Shift+.");
    private static readonly KeyboardShortcut DefaultStreamingShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Shift+Space");

    public KeyboardShortcut ToggleShortcut
    {
        get => GetShortcutOrDefault(_settings.ToggleShortcut, DefaultToggleShortcut);
        set
        {
            string normalized = value.ToPersistedString();
            if (_settings.ToggleShortcut != normalized)
            {
                _settings.ToggleShortcut = normalized;
                Save();
                NotifySettingsChanged();
            }
        }
    }

    public KeyboardShortcut CancelShortcut
    {
        get => GetShortcutOrDefault(_settings.CancelShortcut, DefaultCancelShortcut);
        set
        {
            string normalized = value.ToPersistedString();
            if (_settings.CancelShortcut != normalized)
            {
                _settings.CancelShortcut = normalized;
                Save();
                NotifySettingsChanged();
            }
        }
    }

    public KeyboardShortcut ChangeModeShortcut
    {
        get => GetShortcutOrDefault(_settings.ChangeModeShortcut, DefaultChangeModeShortcut);
        set
        {
            string normalized = value.ToPersistedString();
            if (_settings.ChangeModeShortcut != normalized)
            {
                _settings.ChangeModeShortcut = normalized;
                Save();
                NotifySettingsChanged();
            }
        }
    }

    public KeyboardShortcut StreamingShortcut
    {
        get => GetShortcutOrDefault(_settings.StreamingShortcut, DefaultStreamingShortcut);
        set
        {
            string normalized = value.ToPersistedString();
            if (_settings.StreamingShortcut != normalized)
            {
                _settings.StreamingShortcut = normalized;
                Save();
                NotifySettingsChanged();
            }
        }
    }

    public PushToTalkSettings PushToTalk
    {
        get => _settings.PushToTalk ?? new PushToTalkSettings();
        set
        {
            _settings.PushToTalk = new PushToTalkSettings
            {
                Mode = value.Mode,
                Modifier = value.Modifier,
                DoublePressLock = value.DoublePressLock,
                CustomShortcut = value.CustomShortcut?.Clone()
            };
            Save();
            NotifySettingsChanged();
        }
    }

    private void ApplyDefaults()
    {
        _settings.Version = Math.Max(_settings.Version, 1);

        // STORAGE DEFAULTS
        if (string.IsNullOrWhiteSpace(_settings.RecordingsFolder))
        {
            // Existing users keep legacy LocalAppData\Audio; new installs go to Documents
            var legacyFolder = GetLegacyAudioFolder();
            _settings.RecordingsFolder = _settingsFileExists || Directory.Exists(legacyFolder)
                ? legacyFolder
                : GetDefaultRecordingsFolder();
        }

        // v1.6 -> next-release one-time migration: 1.6 hard-disabled M4A (its getter returned false)
        // but still persisted "StoreAsM4A": true. HEAD now honors the stored value, which would
        // silently enable AAC re-encoding + original-WAV deletion for every upgrader. Existing installs
        // written by settings version < 3 never made a real choice here (the 1.6 setter was a no-op),
        // so reset them to WAV; users can still opt in afterward. New installs keep the M4A-on default.
        if (_settingsFileExists && _settings.Version < 3)
        {
            _settings.StoreAsM4A = false;
        }
        _settings.StoreAsM4A ??= _settingsFileExists ? false : true;
        _settings.UserChoseAlternateStorage ??= false;

        // GENERAL DEFAULTS
        _settings.AutoPasteEnabled ??= true;
        _settings.KeepMicrophoneWarm ??= false;
        _settings.MediaControlMode = NormalizeMediaControlMode(_settings.MediaControlMode);

        // OUTPUT DEFAULTS
        _settings.RemoveFillerWords ??= true;

        // APPEARANCE DEFAULTS
        _settings.ThemeMode ??= (int)Models.ThemeMode.System;

        _settings.ToggleShortcut ??= DefaultToggleShortcut.ToPersistedString();
        _settings.CancelShortcut ??= DefaultCancelShortcut.ToPersistedString();
        _settings.ChangeModeShortcut ??= DefaultChangeModeShortcut.ToPersistedString();
        _settings.StreamingShortcut ??= DefaultStreamingShortcut.ToPersistedString();

        // STREAMING DEFAULTS
        _settings.StreamingEnabled ??= false;
        _settings.StreamingProvider = Models.StreamingTranscriptionProviderExtensions.IsValidStorageValue(_settings.StreamingProvider)
            ? _settings.StreamingProvider
            : Models.StreamingTranscriptionProvider.HyperWhisperCloud.StorageValue();
        _settings.StreamingLanguage = string.IsNullOrWhiteSpace(_settings.StreamingLanguage) ? "en" : _settings.StreamingLanguage;

        // Migrate removed Deepgram model IDs (2026-05 catalog cleanup) to
        // nova-3-general. Anything that isn't nova-3-medical collapses to
        // nova-3-general — log when we actually rewrite a known legacy ID
        // (i.e. the alias map maps it to something different from itself).
        var storedDeepgram = _settings.StreamingDeepgramModel;
        var migratedDeepgram = storedDeepgram is "nova-3-medical" ? "nova-3-medical" : "nova-3-general";
        if (!string.IsNullOrWhiteSpace(storedDeepgram)
            && !string.Equals(storedDeepgram, migratedDeepgram, StringComparison.Ordinal)
            && !string.Equals(
                CloudTranscriptionModels.ResolveDeepgramModelAlias(storedDeepgram!),
                storedDeepgram,
                StringComparison.OrdinalIgnoreCase))
        {
            LoggingService.Info(
                $"SettingsService: Migrated removed Deepgram streaming model '{storedDeepgram}' to '{migratedDeepgram}'");
        }
        _settings.StreamingDeepgramModel = migratedDeepgram;
        _settings.StreamingFastFormatting ??= true;

        // Migrate old defaults to Ctrl+Shift+. (Ctrl+Shift+M conflicts with Teams, etc.)
        if (_settings.ChangeModeShortcut is "Ctrl+Alt+M" or "Ctrl+Shift+M")
        {
            _settings.ChangeModeShortcut = DefaultChangeModeShortcut.ToPersistedString();
        }
        _settings.PushToTalk ??= new PushToTalkSettings();

        // Migrate Ctrl PTT modifier — Ctrl conflicts with too many system shortcuts
        if (string.Equals(_settings.PushToTalk.Modifier, "Ctrl", StringComparison.OrdinalIgnoreCase))
            _settings.PushToTalk.Modifier = "LeftAlt";

        _settings.Version = LatestSettingsVersion;
    }

    private static KeyboardShortcut GetShortcutOrDefault(string? stored, KeyboardShortcut fallback)
    {
        return string.IsNullOrWhiteSpace(stored) ? fallback.Clone() : KeyboardShortcut.FromPersistedString(stored);
    }

    public void ResetShortcutsToDefaults()
    {
        _settings.ToggleShortcut = DefaultToggleShortcut.ToPersistedString();
        _settings.CancelShortcut = DefaultCancelShortcut.ToPersistedString();
        _settings.ChangeModeShortcut = DefaultChangeModeShortcut.ToPersistedString();
        _settings.StreamingShortcut = DefaultStreamingShortcut.ToPersistedString();
        _settings.PushToTalk = new PushToTalkSettings();
        Save();
        NotifySettingsChanged();
    }
}
