using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.AppClassification;
using HyperWhisper.Services.Streaming;
using HyperWhisper.Views.Controls;
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
        // Seed BEFORE the conflict check, and never the other way round: writing
        // DisplayText runs the recorder's OnDisplayTextChanged, which withdraws the
        // StandingError this page is about to draw. The verdict is about the chord
        // that is stored, so a re-seeded field is exactly what makes it stale.
        StreamingShortcutBox.DisplayText = _settings.StreamingShortcut.ToDisplayString();
        UpdateStreamingShortcutConflict();

        LanguageBox.ItemsSource = LanguageInfo.AllLanguages;
        LanguageBox.SelectedValue = _settings.StreamingLanguage;

        SelectComboBoxItemByTag(ProviderBox, _settings.StreamingProvider);
        SelectComboBoxItemByTag(DeepgramModelBox, _settings.StreamingDeepgramModel);
        FastFormattingCheckbox.IsChecked = _settings.StreamingFastFormatting;

        // Catalog-driven, so a third live vendor needs no edit here or in the XAML.
        CloudTierBox.ItemsSource = CloudSttCatalog.Shared
            .StreamingCloudTierEntries()
            .Select(entry => new CloudTierChoice(entry.Id, TierLabel(entry.Id)))
            .ToList();
        CloudTierBox.SelectedValue = _settings.StreamingCloudTier;

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

    /// <summary>
    /// A recorder captured a chord. It has already rejected single bare modifiers
    /// and duplicates against the other three global shortcuts, exactly as
    /// ShortcutsSettingsPage's own handler relies on, so all that is left here is
    /// storing it.
    ///
    /// The capture, the validation and the error rendering used to live on this
    /// page, in a key handler that committed on every key-down - so reaching for
    /// Ctrl+Shift+X persisted the bare Ctrl+Shift prefix as a global hotkey that
    /// survived a restart (#794, the same defect #539 fixed in the recorder).
    /// </summary>
    private void ShortcutBox_Captured(object sender, ShortcutCapturedEventArgs e)
    {
        _settings.StreamingShortcut = e.Shortcut;
        LoggingService.Info($"StreamingSettingsPage: Streaming shortcut set to {e.Shortcut.ToDisplayString()}");
    }

    /// <summary>
    /// The on-load conflict render. The recorder validates on CAPTURE, not on load,
    /// so a conflict that was already in settings.json when the page opened still
    /// needs saying - and this is the only thing in the app that says it.
    ///
    /// It goes on the recorder's StandingError, NOT its ShowError/ClearError. Those
    /// two are the recorder's own seam for the verdict about the chord the user just
    /// typed, and the recorder clears that verdict on focus and on a re-seeded
    /// DisplayText - by design, and for good reasons of its own. Rendering a
    /// load-time conflict through them meant the sentence and the red border both
    /// vanished the moment the user clicked into the field to fix the duplicate, and
    /// nothing repainted them: they were gone for the rest of the page visit, with
    /// the duplicate still stored. StandingError is about what is STORED, so it
    /// survives focus and is withdrawn by the one thing that makes it untrue - a new
    /// value in the field.
    /// </summary>
    private void UpdateStreamingShortcutConflict()
    {
        StreamingShortcutBox.StandingError = ShortcutValidationService.ValidateDuplicate(
            _settings.StreamingShortcut,
            "Streaming",
            _settings.ToggleShortcut,
            _settings.CancelShortcut,
            _settings.ChangeModeShortcut,
            _settings.StreamingShortcut);
    }

    private void FocusStreamingShortcut_Click(object sender, RoutedEventArgs e)
    {
        // Not StreamingShortcutBox.Focus(): the UserControl is not focusable, so it
        // would silently do nothing. The recorder reaches its own inner field.
        StreamingShortcutBox.FocusForCapture();
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
        HyperWhisperCloudPanel.Visibility = provider == StreamingTranscriptionProvider.HyperWhisperCloud
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
            StreamingTranscriptionProvider.GeminiTranscribe => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.GeminiTranscribe)
                ? Loc.S("settings.streaming.providerStatus.geminiTranscribe.configured")
                : Loc.S("settings.streaming.providerStatus.geminiTranscribe.missingKey"),
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
        // Ask the shared core, never a second provider list — see
        // StreamingTranscriptionSessionFactory.SupportsVocabulary, which reads
        // hw_net::live::supports_vocabulary (issue #281) with no credential and
        // no session.
        if (!StreamingTranscriptionSessionFactory.SupportsVocabulary(provider))
        {
            VocabularyWarningText.Text = Loc.S("settings.streaming.warning.vocabularyUnsupported");
            VocabularyWarningPanel.Visibility = Visibility.Visible;
            return;
        }

        // Auto-detect drops the terms on Deepgram (Nova-3's monolingual gate) and
        // therefore on HyperWhisper Cloud too — but only while the cloud live tier
        // is a Deepgram one. xAI and Gemini accept vocabulary either way, so the
        // answer comes from the factory rather than a provider list kept here:
        // a list here is what made xAI's keyterm support dead on arrival.
        if (!StreamingTranscriptionSessionFactory.SupportsVocabularyWithoutLanguage(provider)
            && string.Equals(_settings.StreamingLanguage, "auto", System.StringComparison.OrdinalIgnoreCase))
        {
            VocabularyWarningText.Text = Loc.S("settings.streaming.warning.vocabularyAutoDetect");
            VocabularyWarningPanel.Visibility = Visibility.Visible;
            return;
        }

        VocabularyWarningPanel.Visibility = Visibility.Collapsed;
    }

    private void CloudTierBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (CloudTierBox.SelectedValue is string tier)
        {
            _settings.StreamingCloudTier = tier;
            // The Deepgram tier drops vocabulary in auto-detect and the Gemini tier
            // does not, so the warning below the picker changes with this selection.
            UpdateVocabularyWarning();
            LoggingService.Info($"StreamingSettingsPage: Streaming cloud tier set to {tier}");
        }
    }

    /// <summary>
    /// The picker's row labels are the EXISTING per-tier strings the Mode editor
    /// already ships (<c>modes.cloudAccuracy.&lt;id&gt;.label</c>) — reusing
    /// CloudAccuracyTier's value space is exactly what buys that. Only the
    /// picker's own heading is a new string, so this is one key per platform and
    /// not 40 files of vendor names. Falls back to the catalog display name if a
    /// future catalog id ever lands before its label does.
    /// </summary>
    private static string TierLabel(string tierId)
    {
        var localized = Loc.S($"modes.cloudAccuracy.{tierId}.label");
        if (!string.IsNullOrWhiteSpace(localized) &&
            !string.Equals(localized, $"modes.cloudAccuracy.{tierId}.label", System.StringComparison.Ordinal))
        {
            return localized;
        }
        return CloudSttCatalog.Shared.GetById(tierId)?.DisplayName ?? tierId;
    }

    private sealed record CloudTierChoice(string Id, string Label);

    private static void SelectComboBoxItemByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, System.StringComparison.Ordinal));
    }
}
