using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly PortableSettingsService _settings;
    private string _language = "auto";
    private string _localLlmBackend = "cpu";
    private bool _allowLocalLlmCpuFallback = true;
    private bool _localApiEnabled;
    private int _localApiPort = 51671;
    private string _toggleShortcutModifiers = "Control, Alt";
    private string _toggleShortcutKey = string.Empty;
    private string _cancelShortcutModifiers = "None";
    private string _cancelShortcutKey = "Escape";
    private string _changeModeShortcutModifiers = "Control, Shift";
    private string _changeModeShortcutKey = "Period";
    private string _streamingShortcutModifiers = "Control, Shift";
    private string _streamingShortcutKey = "Space";
    private string _pushToTalkMode = "Disabled";
    private string _pushToTalkModifier = "LeftAlt";
    private string _pushToTalkShortcutModifiers = "None";
    private string _pushToTalkShortcutKey = string.Empty;
    private bool _pushToTalkDoublePressLock;
    private bool _pasteResultText = true;
    private bool _removeFillerWords = true;
    private bool _autocapitalizeInsert = true;
    private bool _restoreClipboardAfterPaste = true;
    private bool _hideFromClipboardHistory = true;
    private double _clipboardRestoreDelaySeconds = 10;
    private bool _storeWordTimestamps = true;
    private bool _streamingEnabled;
    private string _streamingProvider = "deepgram";
    private string _streamingLanguage = "auto";
    private string _streamingModel = "nova-3-general";
    private string _streamingCloudTier = "deepgramNova3";
    private bool _streamingFastFormatting;
    private bool _autostartEnabled;
    private bool _launchMinimized;
    private bool _minimizeToTray = true;
    private bool _enableSoundEffects = true;
    private double _soundEffectsVolume = 1;
    private bool _showRecordingWindow = true;
    private string _themeMode = "system";
    private bool _autoIncreaseMicVolume;
    private bool _keepMicrophoneWarm;
    private bool _keepAudioFiles = true;
    private bool _enableVoiceActivityTrimming = true;
    private bool _storeAsM4A;
    private string _recordingsDirectory = string.Empty;
    private bool _autoDeleteEnabled;
    private int _autoDeleteDaysOld = 30;
    private string _audioEnvironmentPolicy = "unchanged";
    private string _desktopContextStatus = "Desktop context capability not checked";
    private string _clipboardHistoryPrivacyStatus = "Clipboard-history privacy capability not checked";
    private bool _enableErrorLogging = true;
    private bool _shareAnonymousSpeedData = true;
    private string _localWhisperBackend = "auto";
    private bool _allowLocalWhisperCpuFallback = true;
    private string _processWhisperBackend = "auto";
    private bool _processWhisperCpuFallback = true;
    private bool _whisperBaselineCaptured;
    private readonly string _currentWhisperRuntimeStatus;
    public SettingsViewModel(
        PortableSettingsService settings,
        string localLlmRuntimeStatus = "Local LLM runtime not connected",
        string localWhisperRuntimeStatus = "Local Whisper runtime not connected")
    {
        _settings = settings;
        SaveCommand = new AsyncCommand(_ => { Save(); return Task.CompletedTask; });
        ResetShortcutsCommand = new AsyncCommand(_ => { ResetShortcuts(); return Task.CompletedTask; });
        LocalLlmRuntimeStatus = localLlmRuntimeStatus;
        _currentWhisperRuntimeStatus = localWhisperRuntimeStatus;
    }
    public string Language { get => _language; set => Set(ref _language, value); }
    public string LocalLlmBackend { get => _localLlmBackend; set => Set(ref _localLlmBackend, NormalizeBackend(value)); }
    public bool AllowLocalLlmCpuFallback { get => _allowLocalLlmCpuFallback; set => Set(ref _allowLocalLlmCpuFallback, value); }
    public bool LocalApiEnabled { get => _localApiEnabled; set => Set(ref _localApiEnabled, value); }
    public int LocalApiPort { get => _localApiPort; set => Set(ref _localApiPort, Math.Clamp(value, 0, 65535)); }
    public string ToggleShortcutModifiers
    {
        get => _toggleShortcutModifiers;
        set { if (Set(ref _toggleShortcutModifiers, value ?? string.Empty)) Notify(nameof(ToggleShortcutDisplay)); }
    }
    public string ToggleShortcutKey
    {
        get => _toggleShortcutKey;
        set { if (Set(ref _toggleShortcutKey, value ?? string.Empty)) Notify(nameof(ToggleShortcutDisplay)); }
    }

    /// <summary>
    /// The record shortcut as one label, for the status bar and the Home shortcut chip.
    /// Both apps show the shortcut where the user looks for it, not only in Settings.
    /// </summary>
    public string ToggleShortcutDisplay
    {
        get
        {
            var parts = (_toggleShortcutModifiers ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(_toggleShortcutKey ?? string.Empty)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..])
                // Windows writes the modifier short, so the two apps read the same.
                .Select(part => part is "Control" ? "Ctrl" : part);
            return string.Join("+", parts);
        }
    }
    public string CancelShortcutModifiers { get => _cancelShortcutModifiers; set => Set(ref _cancelShortcutModifiers, value ?? string.Empty); }
    public string CancelShortcutKey { get => _cancelShortcutKey; set => Set(ref _cancelShortcutKey, value ?? string.Empty); }
    public string ChangeModeShortcutModifiers { get => _changeModeShortcutModifiers; set => Set(ref _changeModeShortcutModifiers, value ?? string.Empty); }
    public string ChangeModeShortcutKey { get => _changeModeShortcutKey; set => Set(ref _changeModeShortcutKey, value ?? string.Empty); }
    public string StreamingShortcutModifiers { get => _streamingShortcutModifiers; set => Set(ref _streamingShortcutModifiers, value ?? string.Empty); }
    public string StreamingShortcutKey { get => _streamingShortcutKey; set => Set(ref _streamingShortcutKey, value ?? string.Empty); }
    public string PushToTalkMode
    {
        get => _pushToTalkMode;
        set
        {
            if (!Set(ref _pushToTalkMode, value ?? "Disabled")) return;
            Notify(nameof(PushToTalkUsesModifier));
            Notify(nameof(PushToTalkUsesCustomShortcut));
            Notify(nameof(PushToTalkIsEnabled));
        }
    }

    /// <summary>
    /// Windows shows the modifier, the custom shortcut and the double press rows only once push to
    /// talk is on, and only the row the chosen mode uses.
    /// </summary>
    public bool PushToTalkIsEnabled => !string.Equals(PushToTalkMode, "Disabled", StringComparison.Ordinal);
    public bool PushToTalkUsesModifier => string.Equals(PushToTalkMode, "Modifier", StringComparison.Ordinal);
    public bool PushToTalkUsesCustomShortcut => string.Equals(PushToTalkMode, "CustomShortcut", StringComparison.Ordinal);
    public string PushToTalkModifier { get => _pushToTalkModifier; set => Set(ref _pushToTalkModifier, value ?? "LeftAlt"); }
    public string PushToTalkShortcutModifiers { get => _pushToTalkShortcutModifiers; set => Set(ref _pushToTalkShortcutModifiers, value ?? "None"); }
    public string PushToTalkShortcutKey { get => _pushToTalkShortcutKey; set => Set(ref _pushToTalkShortcutKey, value ?? string.Empty); }
    public bool PushToTalkDoublePressLock { get => _pushToTalkDoublePressLock; set => Set(ref _pushToTalkDoublePressLock, value); }
    public bool PasteResultText { get => _pasteResultText; set => Set(ref _pasteResultText, value); }
    public bool RemoveFillerWords { get => _removeFillerWords; set => Set(ref _removeFillerWords, value); }
    public bool AutocapitalizeInsert { get => _autocapitalizeInsert; set => Set(ref _autocapitalizeInsert, value); }
    public bool RestoreClipboardAfterPaste { get => _restoreClipboardAfterPaste; set => Set(ref _restoreClipboardAfterPaste, value); }
    public bool HideFromClipboardHistory { get => _hideFromClipboardHistory; set => Set(ref _hideFromClipboardHistory, value); }
    public double ClipboardRestoreDelaySeconds { get => _clipboardRestoreDelaySeconds; set => Set(ref _clipboardRestoreDelaySeconds, Math.Clamp(value, 0, 60)); }
    public bool StoreWordTimestamps { get => _storeWordTimestamps; set => Set(ref _storeWordTimestamps, value); }
    public bool StreamingEnabled
    {
        get => _streamingEnabled;
        set { if (Set(ref _streamingEnabled, value)) Notify(nameof(StreamingCloudTierRowVisible)); }
    }
    public string StreamingProvider
    {
        get => _streamingProvider;
        set
        {
            if (Set(ref _streamingProvider, NormalizeStreamingProvider(value)))
            {
                Notify(nameof(StreamingUsesHyperWhisperCloud));
                Notify(nameof(StreamingCloudTierRowVisible));
            }
        }
    }

    /// <summary>Gates the cloud live-vendor picker; the tier is meaningless for every other provider.</summary>
    public bool StreamingUsesHyperWhisperCloud =>
        string.Equals(_streamingProvider, "hyperwhisper", StringComparison.Ordinal);

    /// <summary>The Streaming page hides every provider row until streaming is on, as Windows does.</summary>
    public bool StreamingCloudTierRowVisible => _streamingEnabled && StreamingUsesHyperWhisperCloud;
    public string StreamingLanguage { get => _streamingLanguage; set => Set(ref _streamingLanguage, string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim()); }
    public string StreamingModel { get => _streamingModel; set => Set(ref _streamingModel, value?.Trim() ?? string.Empty); }
    public string StreamingCloudTier { get => _streamingCloudTier; set => Set(ref _streamingCloudTier, NormalizeStreamingCloudTier(value)); }
    public bool StreamingFastFormatting { get => _streamingFastFormatting; set => Set(ref _streamingFastFormatting, value); }
    public bool AutostartEnabled { get => _autostartEnabled; set => Set(ref _autostartEnabled, value); }
    public bool LaunchMinimized { get => _launchMinimized; set => Set(ref _launchMinimized, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }
    public bool EnableSoundEffects { get => _enableSoundEffects; set => Set(ref _enableSoundEffects, value); }
    public double SoundEffectsVolume { get => _soundEffectsVolume; set => Set(ref _soundEffectsVolume, Math.Clamp(double.IsFinite(value) ? value : 1, 0, 1)); }
    public bool ShowRecordingWindow { get => _showRecordingWindow; set => Set(ref _showRecordingWindow, value); }
    public string ThemeMode
    {
        get => _themeMode;
        set
        {
            if (!Set(ref _themeMode, NormalizeThemeMode(value))) return;
            Notify(nameof(ThemeIsSystem));
            Notify(nameof(ThemeIsLight));
            Notify(nameof(ThemeIsDark));
        }
    }

    /// <summary>
    /// Windows offers the theme as three rows, each with a title and a description, not as a list.
    /// These three make that shape bindable without a converter.
    /// </summary>
    public bool ThemeIsSystem
    {
        get => ThemeMode == "system";
        set { if (value) ThemeMode = "system"; }
    }

    public bool ThemeIsLight
    {
        get => ThemeMode == "light";
        set { if (value) ThemeMode = "light"; }
    }

    public bool ThemeIsDark
    {
        get => ThemeMode == "dark";
        set { if (value) ThemeMode = "dark"; }
    }
    public IReadOnlyList<string> ThemeModes { get; } = ["system", "light", "dark"];
    public bool AutoIncreaseMicVolume { get => _autoIncreaseMicVolume; set => Set(ref _autoIncreaseMicVolume, value); }
    public bool KeepMicrophoneWarm { get => _keepMicrophoneWarm; set => Set(ref _keepMicrophoneWarm, value); }
    public bool KeepAudioFiles { get => _keepAudioFiles; set => Set(ref _keepAudioFiles, value); }
    public bool EnableVoiceActivityTrimming { get => _enableVoiceActivityTrimming; set => Set(ref _enableVoiceActivityTrimming, value); }
    public bool StoreAsM4A { get => _storeAsM4A; set => Set(ref _storeAsM4A, value); }
    public string RecordingsDirectory
    {
        get => _recordingsDirectory;
        set => Set(ref _recordingsDirectory, value?.Trim() ?? string.Empty);
    }
    public bool AutoDeleteEnabled { get => _autoDeleteEnabled; set => Set(ref _autoDeleteEnabled, value); }
    public int AutoDeleteDaysOld { get => _autoDeleteDaysOld; set => Set(ref _autoDeleteDaysOld, Math.Clamp(value, 1, 365)); }
    public string AudioEnvironmentPolicy { get => _audioEnvironmentPolicy; set => Set(ref _audioEnvironmentPolicy, NormalizeAudioPolicy(value)); }
    public string DesktopContextStatus { get => _desktopContextStatus; set => Set(ref _desktopContextStatus, value ?? string.Empty); }
    public string ClipboardHistoryPrivacyStatus { get => _clipboardHistoryPrivacyStatus; set => Set(ref _clipboardHistoryPrivacyStatus, value ?? string.Empty); }
    public bool EnableErrorLogging { get => _enableErrorLogging; set => Set(ref _enableErrorLogging, value); }
    public bool ShareAnonymousSpeedData { get => _shareAnonymousSpeedData; set => Set(ref _shareAnonymousSpeedData, value); }
    public string LocalWhisperBackend
    {
        get => _localWhisperBackend;
        set
        {
            if (Set(ref _localWhisperBackend, NormalizeWhisperBackend(value)))
            {
                Notify(nameof(WhisperRestartRequired));
                Notify(nameof(LocalWhisperRuntimeStatus));
            }
        }
    }
    public bool AllowLocalWhisperCpuFallback
    {
        get => _allowLocalWhisperCpuFallback;
        set
        {
            if (Set(ref _allowLocalWhisperCpuFallback, value))
            {
                Notify(nameof(WhisperRestartRequired));
                Notify(nameof(LocalWhisperRuntimeStatus));
            }
        }
    }
    public string LocalLlmRuntimeStatus { get; }
    public bool WhisperRestartRequired => _whisperBaselineCaptured
        && (LocalWhisperBackend != _processWhisperBackend
            || AllowLocalWhisperCpuFallback != _processWhisperCpuFallback);
    public string LocalWhisperRuntimeStatus => WhisperRestartRequired
        ? $"Current process capability: {_currentWhisperRuntimeStatus}. Restart HyperWhisper to activate this Whisper backend change."
        : $"Current process capability: {_currentWhisperRuntimeStatus}.";
    public IReadOnlyList<string> LocalWhisperBackends { get; } = ["auto", "cpu", "vulkan", "cuda12"];
    public IReadOnlyList<string> LocalLlmBackends { get; } = ["cpu", "vulkan", "cuda"];
    public IReadOnlyList<string> PushToTalkModes { get; } = ["Disabled", "Modifier", "CustomShortcut"];
    public IReadOnlyList<string> PushToTalkModifiers { get; } = Enum.GetNames<ModifierSide>();
    // NOTE the pre-existing casing wart: "grok" here is spelled "xai" on macOS
    // and Windows. NormalizeStreamingProvider accepts both spellings, and
    // LiveStreamingModeRouter.TryProvider accepts both too — keep it that way.
    public IReadOnlyList<string> StreamingProviders { get; } =
        ["deepgram", "elevenlabs", "openai", "grok", "geminiTranscribe", "hyperwhisper", "parakeetLocal", "nemotronLocal"];

    /// <summary>
    /// Which vendor HyperWhisper Cloud's live route uses. Catalog-derived, so a
    /// future third live vendor is a catalog change and no code change here.
    /// Only meaningful while <see cref="StreamingProvider"/> is "hyperwhisper".
    /// </summary>
    public IReadOnlyList<string> StreamingCloudTiers { get; } = SharedCoreBridge.StreamingCloudSttTiers();
    public IReadOnlyList<string> AudioEnvironmentPolicies { get; } = ["unchanged", "duck", "mute"];
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public ICommand ResetShortcutsCommand { get; }
    public event EventHandler? LocalApiSettingsChanged;
    public event EventHandler? DesktopSettingsChanged;
    public event EventHandler? TelemetrySettingsChanged;
    public event EventHandler? StorageSettingsChanged;
    public void Load()
    {
        var result = _settings.Load();
        if (result.IsFailure) { Status.Failure(result.Error!.Code, result.Error.Message); return; }
        Language = _settings.Get("language", "auto") ?? "auto";
        LocalLlmBackend = _settings.Get("localLlmBackend", "cpu") ?? "cpu";
        AllowLocalLlmCpuFallback = _settings.Get("allowLocalLlmCpuFallback", true);
        LocalApiEnabled = _settings.Get("localApiEnabled", false);
        LocalApiPort = _settings.Get("localApiPort", 51671);
        ToggleShortcutModifiers = _settings.Get("toggleShortcutModifiers", "Control, Alt") ?? "Control, Alt";
        ToggleShortcutKey = _settings.Get("toggleShortcutKey", string.Empty) ?? string.Empty;
        CancelShortcutModifiers = _settings.Get("cancelShortcutModifiers", "None") ?? "None";
        CancelShortcutKey = _settings.Get("cancelShortcutKey", "Escape") ?? "Escape";
        ChangeModeShortcutModifiers = _settings.Get("changeModeShortcutModifiers", "Control, Shift") ?? "Control, Shift";
        ChangeModeShortcutKey = _settings.Get("changeModeShortcutKey", "Period") ?? "Period";
        StreamingShortcutModifiers = _settings.Get("streamingShortcutModifiers", "Control, Shift") ?? "Control, Shift";
        StreamingShortcutKey = _settings.Get("streamingShortcutKey", "Space") ?? "Space";
        PushToTalkMode = _settings.Get("pushToTalkMode", "Disabled") ?? "Disabled";
        PushToTalkModifier = _settings.Get("pushToTalkModifier", "LeftAlt") ?? "LeftAlt";
        PushToTalkShortcutModifiers = _settings.Get("pushToTalkShortcutModifiers", "None") ?? "None";
        PushToTalkShortcutKey = _settings.Get("pushToTalkShortcutKey", string.Empty) ?? string.Empty;
        PushToTalkDoublePressLock = _settings.Get("pushToTalkDoublePressLock", false);
        PasteResultText = _settings.Get("textOutput.pasteResultText", true);
        RemoveFillerWords = _settings.Get("textOutput.removeFillerWords", true);
        AutocapitalizeInsert = _settings.Get("textOutput.autocapitalizeInsert", true);
        RestoreClipboardAfterPaste = _settings.Get("textOutput.restoreClipboardAfterPaste", true);
        HideFromClipboardHistory = _settings.Get("textOutput.hideFromClipboardHistory", true);
        ClipboardRestoreDelaySeconds = _settings.Get("textOutput.clipboardRestoreDelaySeconds", 10d);
        StoreWordTimestamps = _settings.Get("textOutput.storeWordTimestamps", true);
        StreamingEnabled = _settings.Get("streaming.enabled", false);
        StreamingProvider = _settings.Get("streaming.provider", "deepgram") ?? "deepgram";
        StreamingLanguage = _settings.Get("streaming.language", "auto") ?? "auto";
        StreamingModel = _settings.Get("streaming.deepgramModel", "nova-3-general") ?? "nova-3-general";
        StreamingCloudTier = _settings.Get("streaming.cloudTier", "deepgramNova3") ?? "deepgramNova3";
        StreamingFastFormatting = _settings.Get("streaming.fastFormatting", false);
        AutostartEnabled = _settings.Get("autostartEnabled", false);
        LaunchMinimized = _settings.Get("general.launchMinimized", false);
        MinimizeToTray = _settings.Get("minimizeToTray", true);
        EnableSoundEffects = _settings.Get("general.enableSoundEffects", true);
        SoundEffectsVolume = _settings.Get("soundEffectsVolume", 1d);
        ShowRecordingWindow = _settings.Get("general.showRecordingWindow", true);
        ThemeMode = _settings.Get("themeMode", "system") ?? "system";
        AutoIncreaseMicVolume = _settings.Get("autoIncreaseMicVolume", false);
        KeepMicrophoneWarm = _settings.Get("keepMicrophoneWarm", false);
        KeepAudioFiles = _settings.Get("storage.keepAudioFiles", true);
        EnableVoiceActivityTrimming = _settings.Get("audio.enableVoiceActivityTrimming", true);
        StoreAsM4A = _settings.Get("storage.storeAsM4A", false);
        RecordingsDirectory = _settings.Get("storage.recordingsDirectory", string.Empty) ?? string.Empty;
        AutoDeleteEnabled = _settings.Get("autoDeleteEnabled", false);
        AutoDeleteDaysOld = _settings.Get("autoDeleteDaysOld", 30);
        AudioEnvironmentPolicy = _settings.Get("audioEnvironmentPolicy", "unchanged") ?? "unchanged";
        EnableErrorLogging = _settings.Get("general.enableErrorLogging", true);
        ShareAnonymousSpeedData = _settings.Get("general.shareAnonymousSpeedData", true);
        LocalWhisperBackend = _settings.Get("localWhisperBackend", "auto") ?? "auto";
        AllowLocalWhisperCpuFallback = _settings.Get("allowLocalWhisperCpuFallback", true);
        if (!_whisperBaselineCaptured)
        {
            _processWhisperBackend = LocalWhisperBackend;
            _processWhisperCpuFallback = AllowLocalWhisperCpuFallback;
            _whisperBaselineCaptured = true;
            Notify(nameof(WhisperRestartRequired));
            Notify(nameof(LocalWhisperRuntimeStatus));
        }
        Status.Success("Settings loaded");
    }
    public void Save()
    {
        if (RecordingsDirectory.Length > 0 && !Path.IsPathFullyQualified(RecordingsDirectory))
        {
            Status.Failure("settings.recordings_directory_relative", "Choose an absolute recordings directory.");
            return;
        }
        var shortcutValidation = ValidateShortcuts();
        if (shortcutValidation.IsFailure)
        {
            Status.Failure(shortcutValidation.Error!.Code, shortcutValidation.Error.Message);
            return;
        }
        _settings.Set("language", Language);
        _settings.Set("localLlmBackend", NormalizeBackend(LocalLlmBackend));
        _settings.Set("allowLocalLlmCpuFallback", AllowLocalLlmCpuFallback);
        _settings.Set("localApiEnabled", LocalApiEnabled);
        _settings.Set("localApiPort", LocalApiPort);
        _settings.Set("toggleShortcutModifiers", ToggleShortcutModifiers);
        _settings.Set("toggleShortcutKey", ToggleShortcutKey);
        _settings.Set("cancelShortcutModifiers", CancelShortcutModifiers);
        _settings.Set("cancelShortcutKey", CancelShortcutKey);
        _settings.Set("changeModeShortcutModifiers", ChangeModeShortcutModifiers);
        _settings.Set("changeModeShortcutKey", ChangeModeShortcutKey);
        _settings.Set("streamingShortcutModifiers", StreamingShortcutModifiers);
        _settings.Set("streamingShortcutKey", StreamingShortcutKey);
        _settings.Set("pushToTalkMode", PushToTalkMode);
        _settings.Set("pushToTalkModifier", PushToTalkModifier);
        _settings.Set("pushToTalkShortcutModifiers", PushToTalkShortcutModifiers);
        _settings.Set("pushToTalkShortcutKey", PushToTalkShortcutKey);
        _settings.Set("pushToTalkDoublePressLock", PushToTalkDoublePressLock);
        _settings.Set("textOutput.pasteResultText", PasteResultText);
        _settings.Set("textOutput.removeFillerWords", RemoveFillerWords);
        _settings.Set("textOutput.autocapitalizeInsert", AutocapitalizeInsert);
        _settings.Set("textOutput.restoreClipboardAfterPaste", RestoreClipboardAfterPaste);
        _settings.Set("textOutput.hideFromClipboardHistory", HideFromClipboardHistory);
        _settings.Set("textOutput.clipboardRestoreDelaySeconds", ClipboardRestoreDelaySeconds);
        _settings.Set("textOutput.storeWordTimestamps", StoreWordTimestamps);
        _settings.Set("streaming.enabled", StreamingEnabled);
        _settings.Set("streaming.provider", NormalizeStreamingProvider(StreamingProvider));
        _settings.Set("streaming.language", StreamingLanguage);
        _settings.Set("streaming.deepgramModel", StreamingModel);
        _settings.Set("streaming.cloudTier", NormalizeStreamingCloudTier(StreamingCloudTier));
        _settings.Set("streaming.fastFormatting", StreamingFastFormatting);
        _settings.Set("autostartEnabled", AutostartEnabled);
        _settings.Set("general.launchMinimized", LaunchMinimized);
        _settings.Set("minimizeToTray", MinimizeToTray);
        _settings.Set("general.enableSoundEffects", EnableSoundEffects);
        _settings.Set("soundEffectsVolume", SoundEffectsVolume);
        _settings.Set("general.showRecordingWindow", ShowRecordingWindow);
        _settings.Set("themeMode", NormalizeThemeMode(ThemeMode));
        _settings.Set("autoIncreaseMicVolume", AutoIncreaseMicVolume);
        _settings.Set("keepMicrophoneWarm", KeepMicrophoneWarm);
        _settings.Set("storage.keepAudioFiles", KeepAudioFiles);
        _settings.Set("audio.enableVoiceActivityTrimming", EnableVoiceActivityTrimming);
        _settings.Set("storage.storeAsM4A", StoreAsM4A);
        _settings.Set("storage.recordingsDirectory", RecordingsDirectory);
        _settings.Set("autoDeleteEnabled", AutoDeleteEnabled);
        _settings.Set("autoDeleteDaysOld", Math.Clamp(AutoDeleteDaysOld, 1, 365));
        _settings.Set("audioEnvironmentPolicy", NormalizeAudioPolicy(AudioEnvironmentPolicy));
        _settings.Set("general.enableErrorLogging", EnableErrorLogging);
        _settings.Set("general.shareAnonymousSpeedData", ShareAnonymousSpeedData);
        _settings.Set("localWhisperBackend", NormalizeWhisperBackend(LocalWhisperBackend));
        _settings.Set("allowLocalWhisperCpuFallback", AllowLocalWhisperCpuFallback);
        var result = _settings.Save();
        if (result.IsSuccess) { Status.Success("Settings saved"); LocalApiSettingsChanged?.Invoke(this, EventArgs.Empty); DesktopSettingsChanged?.Invoke(this, EventArgs.Empty); TelemetrySettingsChanged?.Invoke(this, EventArgs.Empty); StorageSettingsChanged?.Invoke(this, EventArgs.Empty); }
        else Status.Failure(result.Error!.Code, result.Error.Message);
    }

    public void ResetShortcuts()
    {
        ToggleShortcutModifiers = "Control, Alt";
        ToggleShortcutKey = string.Empty;
        CancelShortcutModifiers = "None";
        CancelShortcutKey = "Escape";
        ChangeModeShortcutModifiers = "Control, Shift";
        ChangeModeShortcutKey = "Period";
        StreamingShortcutModifiers = "Control, Shift";
        StreamingShortcutKey = "Space";
        PushToTalkMode = "Disabled";
        PushToTalkModifier = "LeftAlt";
        PushToTalkShortcutModifiers = "None";
        PushToTalkShortcutKey = string.Empty;
        PushToTalkDoublePressLock = false;
        Status.Success("Shortcut defaults restored; save settings to apply them");
    }

    private PlatformResult ValidateShortcuts()
    {
        if (PushToTalkMode is not ("Disabled" or "Modifier" or "CustomShortcut"))
            return PlatformResult.Failure("settings.push_to_talk_mode_invalid", "Select a valid push-to-talk mode.");
        var configured = new List<(string Name, GlobalShortcut Shortcut)>();
        foreach (var item in new[]
        {
            ("toggle", ToggleShortcutModifiers, ToggleShortcutKey),
            ("cancel", CancelShortcutModifiers, CancelShortcutKey),
            ("change mode", ChangeModeShortcutModifiers, ChangeModeShortcutKey),
            ("streaming", StreamingShortcutModifiers, StreamingShortcutKey),
        })
        {
            var parsed = ParseShortcut(item.Item2, item.Item3);
            if (parsed.IsFailure) return PlatformResult.Failure(parsed.Error!.Code, $"{item.Item1}: {parsed.Error.Message}");
            if (parsed.Value is { } shortcut) configured.Add((item.Item1, shortcut));
        }

        if (PushToTalkMode == "CustomShortcut")
        {
            var parsed = ParseShortcut(PushToTalkShortcutModifiers, PushToTalkShortcutKey);
            if (parsed.IsFailure || parsed.Value is null)
                return PlatformResult.Failure("settings.shortcut_invalid", "push-to-talk: enter a valid assigned shortcut.");
            configured.Add(("push-to-talk", parsed.Value));
        }
        else if (PushToTalkMode == "Modifier")
        {
            if (!Enum.TryParse<ModifierSide>(PushToTalkModifier, true, out var modifier))
                return PlatformResult.Failure("settings.push_to_talk_modifier_invalid", "Select a valid push-to-talk modifier.");
            configured.Add(("push-to-talk", modifier switch
            {
                ModifierSide.Control => new GlobalShortcut(ShortcutModifiers.Control),
                ModifierSide.Alt => new GlobalShortcut(ShortcutModifiers.Alt),
                ModifierSide.Shift => new GlobalShortcut(ShortcutModifiers.Shift),
                ModifierSide.Meta => new GlobalShortcut(ShortcutModifiers.Meta),
                _ => new GlobalShortcut(ShortcutModifiers.None, new ShortcutKeyCode(modifier.ToString())),
            }));
        }

        for (var left = 0; left < configured.Count; left++)
        for (var right = left + 1; right < configured.Count; right++)
            if (configured[left].Shortcut.Modifiers == configured[right].Shortcut.Modifiers
                && string.Equals(configured[left].Shortcut.Key.Value, configured[right].Shortcut.Key.Value, StringComparison.OrdinalIgnoreCase))
                return PlatformResult.Failure("settings.shortcut_conflict",
                    $"{configured[left].Name} and {configured[right].Name} must use different shortcuts.");
        return PlatformResult.Success();
    }

    private static PlatformResult<GlobalShortcut?> ParseShortcut(string? modifiersText, string? keyText)
    {
        var text = string.IsNullOrWhiteSpace(modifiersText) ? "None" : modifiersText.Trim();
        if (!Enum.TryParse<ShortcutModifiers>(text, true, out var modifiers)
            || (modifiers & ~(ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Meta)) != 0)
            return PlatformResult<GlobalShortcut?>.Failure("settings.shortcut_modifiers_invalid", "Use only Control, Alt, Shift, or Meta modifiers.");
        var key = keyText?.Trim() ?? string.Empty;
        if (modifiers == ShortcutModifiers.None && key.Length == 0)
            return PlatformResult<GlobalShortcut?>.Success(null);
        if (key.Length == 0 && CountModifiers(modifiers) == 1)
            return PlatformResult<GlobalShortcut?>.Failure("settings.shortcut_bare_modifier", "A modifier-only shortcut needs at least two modifiers.");
        return PlatformResult<GlobalShortcut?>.Success(key.Length == 0
            ? new GlobalShortcut(modifiers)
            : new GlobalShortcut(modifiers, new ShortcutKeyCode(key)));
    }

    private static int CountModifiers(ShortcutModifiers modifiers)
    {
        var value = (uint)modifiers;
        var count = 0;
        while (value != 0) { count += (int)(value & 1); value >>= 1; }
        return count;
    }

    private static string NormalizeBackend(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vulkan" => "vulkan",
        "cuda" => "cuda",
        _ => "cpu",
    };
    private static string NormalizeThemeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system",
    };

    private static string NormalizeWhisperBackend(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cpu" => "cpu",
        "vulkan" => "vulkan",
        "cuda" or "cuda12" => "cuda12",
        _ => "auto",
    };

    // Fails OPEN to "deepgram" — an id this switch does not know is silently
    // rewritten on the next Save(). LiveStreamingModeRouter.TryProvider is the
    // second, independent normalization of the same string and fails CLOSED.
    // Both must learn a new provider; updating one is a silent-drift bug.
    private static string NormalizeStreamingProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "elevenlabs" => "elevenlabs", "openai" => "openai", "grok" or "xai" => "grok",
        "geminitranscribe" or "gemini-transcribe" or "gemini_transcribe" => "geminiTranscribe",
        "hyperwhisper" or "hyperwhispercloud" => "hyperwhisper",
        "parakeetlocal" or "parakeet_local" => "parakeetLocal",
        "nemotronlocal" or "nemotron_local" => "nemotronLocal",
        _ => "deepgram",
    };

    /// <summary>
    /// Canonicalise through the shared core (so a retired id like
    /// <c>googleChirp3</c> migrates rather than resetting), then hold the result
    /// to the live-eligible set. A tier that is cloud-tier eligible but has no
    /// backend WebSocket route would 404 at dictation time, so anything outside
    /// the set falls back to Deepgram.
    /// </summary>
    private string NormalizeStreamingCloudTier(string? value)
    {
        var canonical = SharedCoreBridge.CanonicalCloudSttTier(value);
        return StreamingCloudTiers.Contains(canonical, StringComparer.Ordinal)
            ? canonical
            : SharedCoreBridge.CanonicalCloudSttTier("deepgramNova3");
    }

    private static string NormalizeAudioPolicy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "duck" => "duck", "mute" => "mute", _ => "unchanged",
    };
}
