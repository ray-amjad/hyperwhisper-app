using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Views.Windows;

namespace HyperWhisper.Views.Pages.Settings;

public partial class StreamingSettingsPage : Page
{
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly VocabularyService _vocabularyService = VocabularyService.Instance;
    private bool _isInitializing;

    public StreamingSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DataContext = Window.GetWindow(this)?.DataContext;

        _vocabularyService.VocabularyChanged -= OnVocabularyChanged;
        _vocabularyService.VocabularyChanged += OnVocabularyChanged;
        _isInitializing = true;

        StreamingEnabledCheckbox.IsChecked = _settings.StreamingEnabled;
        StreamingShortcutBox.Text = _settings.StreamingShortcut.ToDisplayString();
        UpdateStreamingShortcutConflict();

        LanguageBox.ItemsSource = LanguageInfo.AllLanguages;
        LanguageBox.SelectedValue = _settings.StreamingLanguage;

        SelectComboBoxItemByTag(ProviderBox, _settings.StreamingProvider);
        SelectComboBoxItemByTag(DeepgramModelBox, _settings.StreamingDeepgramModel);
        FastFormattingCheckbox.IsChecked = _settings.StreamingFastFormatting;

        _isInitializing = false;
        UpdateStreamingOptionsVisibility();
        UpdateProviderPanels();

        LoggingService.Debug($"StreamingSettingsPage: Initialized (enabled={_settings.StreamingEnabled}, provider={_settings.StreamingProvider}, language={_settings.StreamingLanguage})");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vocabularyService.VocabularyChanged -= OnVocabularyChanged;
    }

    private void StreamingEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.StreamingEnabled = StreamingEnabledCheckbox.IsChecked == true;
        UpdateStreamingOptionsVisibility();
        LoggingService.Info($"StreamingSettingsPage: Streaming enabled set to {_settings.StreamingEnabled}");
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if ((ProviderBox.SelectedItem as ComboBoxItem)?.Tag is string provider)
        {
            _settings.StreamingProvider = provider;
            UpdateProviderPanels();
            LoggingService.Info($"StreamingSettingsPage: Streaming provider set to {provider}");
        }
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (LanguageBox.SelectedValue is string language)
        {
            _settings.StreamingLanguage = language;
            UpdateVocabularyWarning();
            LoggingService.Info($"StreamingSettingsPage: Streaming language set to {language}");
        }
    }

    private void DeepgramModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if ((DeepgramModelBox.SelectedItem as ComboBoxItem)?.Tag is string model)
        {
            _settings.StreamingDeepgramModel = model;
            LoggingService.Info($"StreamingSettingsPage: Deepgram streaming model set to {model}");
        }
    }

    private void OnVocabularyChanged(object? sender, System.EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnVocabularyChanged(sender, e));
            return;
        }

        UpdateVocabularyWarning();
    }

    private void FastFormatting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.StreamingFastFormatting = FastFormattingCheckbox.IsChecked == true;
        LoggingService.Info($"StreamingSettingsPage: Fast formatting set to {_settings.StreamingFastFormatting}");
    }

    private void StreamingShortcutBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        e.Handled = true;

        var shortcut = BuildShortcutFromKeyEvent(e);
        if (shortcut == null) return;

        var validationError = ShortcutValidationService.ValidateDuplicate(
            shortcut,
            "Streaming",
            _settings.ToggleShortcut,
            _settings.CancelShortcut,
            _settings.ChangeModeShortcut,
            _settings.StreamingShortcut);

        if (validationError != null)
        {
            ShowStreamingShortcutError(validationError);
            LoggingService.Warn($"StreamingSettingsPage: Shortcut validation failed - {validationError}");
            return;
        }

        _settings.StreamingShortcut = shortcut;
        StreamingShortcutBox.Text = shortcut.ToDisplayString();
        ClearStreamingShortcutError();
        LoggingService.Info($"StreamingSettingsPage: Streaming shortcut set to {shortcut.ToDisplayString()}");
    }

    private void StreamingShortcutBox_PreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        e.Handled = true;
    }

    private KeyboardShortcut? BuildShortcutFromKeyEvent(WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return null;

        var shortcut = new KeyboardShortcut
        {
            Control = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || key is Key.LeftCtrl or Key.RightCtrl,
            Alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) || key is Key.LeftAlt or Key.RightAlt,
            Shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) || key is Key.LeftShift or Key.RightShift,
            Win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin) || key is Key.LWin or Key.RWin
        };

        if (!IsModifierKey(key))
        {
            shortcut.Key = key;
        }

        return shortcut;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void UpdateStreamingShortcutConflict()
    {
        var validationError = ShortcutValidationService.ValidateDuplicate(
            _settings.StreamingShortcut,
            "Streaming",
            _settings.ToggleShortcut,
            _settings.CancelShortcut,
            _settings.ChangeModeShortcut,
            _settings.StreamingShortcut);

        if (validationError != null)
        {
            ShowStreamingShortcutError(validationError);
        }
        else
        {
            ClearStreamingShortcutError();
        }
    }

    private void ShowStreamingShortcutError(string errorMessage)
    {
        StreamingShortcutErrorText.Text = errorMessage;
        StreamingShortcutErrorText.Visibility = Visibility.Visible;
        StreamingShortcutBox.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
        StreamingShortcutBox.BorderThickness = new Thickness(2);
    }

    private void ClearStreamingShortcutError()
    {
        StreamingShortcutErrorText.Text = "";
        StreamingShortcutErrorText.Visibility = Visibility.Collapsed;
        StreamingShortcutBox.ClearValue(Border.BorderBrushProperty);
        StreamingShortcutBox.ClearValue(Border.BorderThicknessProperty);
    }

    private void FocusStreamingShortcut_Click(object sender, RoutedEventArgs e)
    {
        StreamingShortcutBox.Focus();
        StreamingShortcutBox.SelectAll();
    }

    private void OpenShortcutSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.NavigateToSettingsSection("Shortcuts");
        }
    }

    private void UpdateProviderPanels()
    {
        var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(_settings.StreamingProvider);
        DeepgramPanel.Visibility = provider == StreamingTranscriptionProvider.Deepgram
            ? Visibility.Visible
            : Visibility.Collapsed;

        ProviderStatusText.Text = provider switch
        {
            StreamingTranscriptionProvider.HyperWhisperCloud => Loc.S("settings.streaming.providerStatus.hyperwhisperCloud"),
            StreamingTranscriptionProvider.Deepgram => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Deepgram)
                ? Loc.S("settings.streaming.providerStatus.deepgram.configured")
                : Loc.S("settings.streaming.providerStatus.deepgram.missingKey"),
            StreamingTranscriptionProvider.ElevenLabs => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.ElevenLabs)
                ? Loc.S("settings.streaming.providerStatus.elevenLabs.configured")
                : Loc.S("settings.streaming.providerStatus.elevenLabs.missingKey"),
            StreamingTranscriptionProvider.OpenAI => ApiKeyService.Instance.HasApiKey(PostProcessingProvider.OpenAI)
                ? Loc.S("settings.streaming.providerStatus.openAI.configured")
                : Loc.S("settings.streaming.providerStatus.openAI.missingKey"),
            StreamingTranscriptionProvider.Xai => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Grok)
                ? Loc.S("settings.streaming.providerStatus.xai.configured")
                : Loc.S("settings.streaming.providerStatus.xai.missingKey"),
            _ => Loc.S("settings.streaming.providerStatus.hyperwhisperCloud")
        };

        UpdateVocabularyWarning();
    }

    private void UpdateStreamingOptionsVisibility()
    {
        var visibility = _settings.StreamingEnabled ? Visibility.Visible : Visibility.Collapsed;
        ShortcutSeparator.Visibility = visibility;
        ShortcutRow.Visibility = visibility;
        StreamingOptionsPanel.Visibility = visibility;
    }

    private void UpdateVocabularyWarning()
    {
        bool hasVocabulary;
        try
        {
            hasVocabulary = _vocabularyService.GetVocabularyWords(1).Count > 0;
        }
        catch (System.Exception ex)
        {
            LoggingService.Warn($"StreamingSettingsPage: Failed to load vocabulary warning state - {ex.Message}");
            VocabularyWarningPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!hasVocabulary)
        {
            VocabularyWarningPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(_settings.StreamingProvider);
        if (provider is StreamingTranscriptionProvider.ElevenLabs or StreamingTranscriptionProvider.OpenAI or StreamingTranscriptionProvider.Xai)
        {
            VocabularyWarningText.Text = Loc.S("settings.streaming.warning.vocabularyUnsupported");
            VocabularyWarningPanel.Visibility = Visibility.Visible;
            return;
        }

        if (string.Equals(_settings.StreamingLanguage, "auto", System.StringComparison.OrdinalIgnoreCase))
        {
            VocabularyWarningText.Text = Loc.S("settings.streaming.warning.vocabularyAutoDetect");
            VocabularyWarningPanel.Visibility = Visibility.Visible;
            return;
        }

        VocabularyWarningPanel.Visibility = Visibility.Collapsed;
    }

    private static void SelectComboBoxItemByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, System.StringComparison.Ordinal));
    }
}
