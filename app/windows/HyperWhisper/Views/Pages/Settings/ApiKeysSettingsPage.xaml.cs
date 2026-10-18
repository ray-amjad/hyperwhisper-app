// API KEYS SETTINGS PAGE
// Handles API key configuration for transcription and post-processing providers.
// Keys are stored encrypted using Windows DPAPI via ApiKeyService.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Utilities;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace HyperWhisper.Views.Pages.Settings;

public partial class ApiKeysSettingsPage : Page
{
    private sealed class SharedApiKeyCard
    {
        public required PostProcessingProvider Provider { get; init; }
        public required string InvalidKeyLocalizationKey { get; init; }
        public required string LogLabel { get; init; }
        public required PasswordBox FirstKeyBox { get; init; }
        public required PasswordBox SecondKeyBox { get; init; }
        public required WpfButton FirstSaveButton { get; init; }
        public required WpfButton SecondSaveButton { get; init; }
        public required Action ResetVisibility { get; init; }
        public required Action SyncShowButtons { get; init; }

        public PasswordBox KeyBoxFor(object sender)
        {
            if (ReferenceEquals(sender, FirstSaveButton))
            {
                return FirstKeyBox;
            }

            if (ReferenceEquals(sender, SecondSaveButton))
            {
                return SecondKeyBox;
            }

            throw new ArgumentException("The save button does not belong to this API key card.", nameof(sender));
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    // Track which password boxes are showing plain text
    private bool _openAIKeyVisible;
    private bool _anthropicKeyVisible;
    private bool _groqKeyVisible;
    private bool _geminiKeyVisible;
    private bool _cerebrasKeyVisible;
    private bool _deepgramKeyVisible;
    private bool _assemblyAIKeyVisible;
    private bool _elevenLabsKeyVisible;
    private bool _mistralKeyVisible;
    private bool _sonioxKeyVisible;
    private bool _grokKeyVisible;
    private bool _geminiTranscribeKeyVisible;
    private bool _metaKeyVisible;
    private readonly SharedApiKeyCard _openAIApiKeyCard;
    private readonly SharedApiKeyCard _groqApiKeyCard;
    private readonly SharedApiKeyCard _geminiApiKeyCard;
    private readonly SharedApiKeyCard _grokApiKeyCard;

    public ApiKeysSettingsPage()
    {
        InitializeComponent();
        _openAIApiKeyCard = new SharedApiKeyCard
        {
            Provider = PostProcessingProvider.OpenAI,
            InvalidKeyLocalizationKey = "settings.api.invalidKey.openai",
            LogLabel = "OpenAI",
            FirstKeyBox = OpenAITranscriptionKeyBox,
            SecondKeyBox = OpenAIPostKeyBox,
            FirstSaveButton = OpenAITranscriptionSaveButton,
            SecondSaveButton = OpenAIPostSaveButton,
            ResetVisibility = () => _openAIKeyVisible = false,
            SyncShowButtons = SyncOpenAIShowButtons
        };
        _groqApiKeyCard = new SharedApiKeyCard
        {
            Provider = PostProcessingProvider.Groq,
            InvalidKeyLocalizationKey = "settings.api.invalidKey.groq",
            LogLabel = "Groq",
            FirstKeyBox = GroqTranscriptionKeyBox,
            SecondKeyBox = GroqPostKeyBox,
            FirstSaveButton = GroqTranscriptionSaveButton,
            SecondSaveButton = GroqPostSaveButton,
            ResetVisibility = () => _groqKeyVisible = false,
            SyncShowButtons = SyncGroqShowButtons
        };
        _geminiApiKeyCard = new SharedApiKeyCard
        {
            Provider = PostProcessingProvider.Gemini,
            InvalidKeyLocalizationKey = "settings.api.invalidKey.gemini",
            LogLabel = "Gemini",
            FirstKeyBox = GeminiTranscriptionKeyBox,
            SecondKeyBox = GeminiKeyBox,
            FirstSaveButton = GeminiTranscriptionSaveButton,
            SecondSaveButton = GeminiSaveButton,
            ResetVisibility = () => _geminiKeyVisible = false,
            SyncShowButtons = SyncGeminiShowButtons
        };
        _grokApiKeyCard = new SharedApiKeyCard
        {
            Provider = PostProcessingProvider.Grok,
            InvalidKeyLocalizationKey = "settings.api.invalidKey.grok",
            LogLabel = "Grok",
            FirstKeyBox = GrokKeyBox,
            SecondKeyBox = GrokPostKeyBox,
            FirstSaveButton = GrokSaveButton,
            SecondSaveButton = GrokPostSaveButton,
            ResetVisibility = () => _grokKeyVisible = false,
            SyncShowButtons = SyncGrokShowButtons
        };
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LocalLlmApiKeySection.Visibility = PlatformHelper.SupportsLocalLlmPostProcessing
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Load current API key states
        // OpenAI, Groq, Gemini, and Grok appear in both Transcription and Post-Processing cards
        UpdateKeyStatus(PostProcessingProvider.OpenAI, OpenAITranscriptionStatusText);
        UpdateKeyStatus(PostProcessingProvider.OpenAI, OpenAIPostStatusText);
        UpdateKeyStatus(PostProcessingProvider.Groq, GroqTranscriptionStatusText);
        UpdateKeyStatus(PostProcessingProvider.Groq, GroqPostStatusText);
        UpdateGeminiStatus();

        // Post-processing only providers
        UpdateKeyStatus(PostProcessingProvider.Anthropic, AnthropicStatusText);
        UpdateKeyStatus(PostProcessingProvider.Cerebras, CerebrasStatusText);

        // Transcription-only provider API keys
        UpdateKeyStatus(TranscriptionApiKeyType.Deepgram, DeepgramStatusText);
        UpdateKeyStatus(TranscriptionApiKeyType.AssemblyAI, AssemblyAIStatusText);
        UpdateKeyStatus(TranscriptionApiKeyType.ElevenLabs, ElevenLabsStatusText);
        UpdateKeyStatus(TranscriptionApiKeyType.Mistral, MistralStatusText);
        UpdateKeyStatus(TranscriptionApiKeyType.Soniox, SonioxStatusText);
        // Gemini 3.5 Transcribe has its own key slot, so it is a transcription-only
        // entry here even though the vendor is the same as the Gemini card below.
        UpdateKeyStatus(TranscriptionApiKeyType.GeminiTranscribe, GeminiTranscribeStatusText);
        UpdateKeyStatus(TranscriptionApiKeyType.Meta, MetaStatusText);
        UpdateGrokStatus();

        LoggingService.Info("ApiKeysSettingsPage: Initialized");
    }

    private void OpenModelsSettings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new Window
        {
            Title = Loc.S("settings.section.models"),
            Width = 720,
            Height = 760,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ModelsSettingsPage()
        };

        // 760 DIP is taller than a 1080p work area at 175% (issue #1500).
        WindowWorkAreaFit.Attach(settingsWindow);
        settingsWindow.ShowDialog();
    }

    // =========================================================================
    // API KEY STATUS
    // =========================================================================

    private void UpdateKeyStatus(PostProcessingProvider provider, TextBlock statusText)
    {
        if (ApiKeyService.Instance.HasApiKey(provider))
        {
            var masked = ApiKeyService.Instance.GetMaskedApiKey(provider);
            statusText.Text = Loc.S("provider.status.configured", masked);
            statusText.Foreground = FindResource("SuccessBrush") as Brush ?? Brushes.Green;
        }
        else
        {
            statusText.Text = Loc.S("provider.status.notConfigured");
            statusText.Foreground = FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
        }
    }

    private void UpdateKeyStatus(TranscriptionApiKeyType keyType, TextBlock statusText)
    {
        if (ApiKeyService.Instance.HasApiKey(keyType))
        {
            var masked = ApiKeyService.Instance.GetMaskedApiKey(keyType);
            statusText.Text = Loc.S("provider.status.configured", masked);
            statusText.Foreground = FindResource("SuccessBrush") as Brush ?? Brushes.Green;
        }
        else
        {
            statusText.Text = Loc.S("provider.status.notConfigured");
            statusText.Foreground = FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
        }
    }

    private void SaveSharedApiKey(SharedApiKeyCard card, object sender)
    {
        var key = card.KeyBoxFor(sender).Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(card.Provider, null), card.LogLabel, clearing: true))
            {
                ShowKeyWriteFailed();
                card.SyncShowButtons();
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(card.Provider, key))
            {
                WpfMessageBox.Show(
                    Loc.S(card.InvalidKeyLocalizationKey),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(card.Provider, key), card.LogLabel, clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                card.SyncShowButtons();
                return;
            }
        }

        card.FirstKeyBox.Password = "";
        card.SecondKeyBox.Password = "";
        card.ResetVisibility();
        card.SyncShowButtons();
    }

    // =========================================================================
    // OPENAI (appears in both Transcription and Post-Processing cards)
    // =========================================================================

    private void UpdateOpenAIStatus()
    {
        UpdateKeyStatus(PostProcessingProvider.OpenAI, OpenAITranscriptionStatusText);
        UpdateKeyStatus(PostProcessingProvider.OpenAI, OpenAIPostStatusText);
    }

    private void SyncOpenAIShowButtons()
    {
        var content = _openAIKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");
        OpenAITranscriptionShowButton.Content = content;
        OpenAIPostShowButton.Content = content;

        if (_openAIKeyVisible && ApiKeyService.Instance.HasApiKey(PostProcessingProvider.OpenAI))
        {
            var key = ApiKeyService.Instance.GetApiKey(PostProcessingProvider.OpenAI);
            OpenAITranscriptionStatusText.Text = key ?? "";
            OpenAIPostStatusText.Text = key ?? "";
        }
        else
        {
            UpdateOpenAIStatus();
        }
    }

    private void OpenAITranscriptionKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void OpenAITranscriptionShowButton_Click(object sender, RoutedEventArgs e)
    {
        _openAIKeyVisible = !_openAIKeyVisible;
        SyncOpenAIShowButtons();
    }

    private void OpenAISaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSharedApiKey(_openAIApiKeyCard, sender);
    }

    private void OpenAIPostKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void OpenAIPostShowButton_Click(object sender, RoutedEventArgs e)
    {
        _openAIKeyVisible = !_openAIKeyVisible;
        SyncOpenAIShowButtons();
    }

    // =========================================================================
    // ANTHROPIC
    // =========================================================================

    private void AnthropicKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void AnthropicShowButton_Click(object sender, RoutedEventArgs e)
    {
        _anthropicKeyVisible = !_anthropicKeyVisible;
        AnthropicShowButton.Content = _anthropicKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_anthropicKeyVisible && ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Anthropic))
        {
            var key = ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Anthropic);
            AnthropicStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(PostProcessingProvider.Anthropic, AnthropicStatusText);
        }
    }

    private void AnthropicSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = AnthropicKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Anthropic, null), "Anthropic", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(PostProcessingProvider.Anthropic, AnthropicStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.Anthropic, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.anthropic"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Anthropic, key), "Anthropic", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(PostProcessingProvider.Anthropic, AnthropicStatusText);
                return;
            }
        }

        AnthropicKeyBox.Password = "";
        _anthropicKeyVisible = false;
        AnthropicShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(PostProcessingProvider.Anthropic, AnthropicStatusText);
    }

    // =========================================================================
    // GROQ (appears in both Transcription and Post-Processing cards)
    // =========================================================================

    private void UpdateGroqStatus()
    {
        UpdateKeyStatus(PostProcessingProvider.Groq, GroqTranscriptionStatusText);
        UpdateKeyStatus(PostProcessingProvider.Groq, GroqPostStatusText);
    }

    private void SyncGroqShowButtons()
    {
        var content = _groqKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");
        GroqTranscriptionShowButton.Content = content;
        GroqPostShowButton.Content = content;

        if (_groqKeyVisible && ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Groq))
        {
            var key = ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Groq);
            GroqTranscriptionStatusText.Text = key ?? "";
            GroqPostStatusText.Text = key ?? "";
        }
        else
        {
            UpdateGroqStatus();
        }
    }

    private void GroqTranscriptionKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void GroqTranscriptionShowButton_Click(object sender, RoutedEventArgs e)
    {
        _groqKeyVisible = !_groqKeyVisible;
        SyncGroqShowButtons();
    }

    private void GroqSaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSharedApiKey(_groqApiKeyCard, sender);
    }

    private void GroqPostKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void GroqPostShowButton_Click(object sender, RoutedEventArgs e)
    {
        _groqKeyVisible = !_groqKeyVisible;
        SyncGroqShowButtons();
    }

    // =========================================================================
    // GEMINI
    // =========================================================================

    private void UpdateGeminiStatus()
    {
        UpdateKeyStatus(PostProcessingProvider.Gemini, GeminiTranscriptionStatusText);
        UpdateKeyStatus(PostProcessingProvider.Gemini, GeminiStatusText);
    }

    private void SyncGeminiShowButtons()
    {
        var content = _geminiKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");
        GeminiTranscriptionShowButton.Content = content;
        GeminiShowButton.Content = content;

        if (_geminiKeyVisible && ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Gemini))
        {
            var key = ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Gemini);
            GeminiTranscriptionStatusText.Text = key ?? "";
            GeminiStatusText.Text = key ?? "";
        }
        else
        {
            UpdateGeminiStatus();
        }
    }

    private void GeminiTranscriptionKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void GeminiTranscriptionShowButton_Click(object sender, RoutedEventArgs e)
    {
        _geminiKeyVisible = !_geminiKeyVisible;
        SyncGeminiShowButtons();
    }

    private void GeminiSharedSaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSharedApiKey(_geminiApiKeyCard, sender);
    }

    private void GeminiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void GeminiShowButton_Click(object sender, RoutedEventArgs e)
    {
        _geminiKeyVisible = !_geminiKeyVisible;
        SyncGeminiShowButtons();
    }

    // =========================================================================
    // CEREBRAS
    // =========================================================================

    private void CerebrasKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void CerebrasShowButton_Click(object sender, RoutedEventArgs e)
    {
        _cerebrasKeyVisible = !_cerebrasKeyVisible;
        CerebrasShowButton.Content = _cerebrasKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_cerebrasKeyVisible && ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Cerebras))
        {
            var key = ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Cerebras);
            CerebrasStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(PostProcessingProvider.Cerebras, CerebrasStatusText);
        }
    }

    private void CerebrasSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = CerebrasKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Cerebras, null), "Cerebras", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(PostProcessingProvider.Cerebras, CerebrasStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.Cerebras, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.cerebras"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Cerebras, key), "Cerebras", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(PostProcessingProvider.Cerebras, CerebrasStatusText);
                return;
            }
        }

        CerebrasKeyBox.Password = "";
        _cerebrasKeyVisible = false;
        CerebrasShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(PostProcessingProvider.Cerebras, CerebrasStatusText);
    }

    // =========================================================================
    // DEEPGRAM
    // =========================================================================

    private void DeepgramKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void DeepgramShowButton_Click(object sender, RoutedEventArgs e)
    {
        _deepgramKeyVisible = !_deepgramKeyVisible;
        DeepgramShowButton.Content = _deepgramKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_deepgramKeyVisible && ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Deepgram))
        {
            var key = ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Deepgram);
            DeepgramStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(TranscriptionApiKeyType.Deepgram, DeepgramStatusText);
        }
    }

    private void DeepgramSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = DeepgramKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Deepgram, null), "Deepgram", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Deepgram, DeepgramStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(TranscriptionApiKeyType.Deepgram, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.deepgram"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Deepgram, key), "Deepgram", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Deepgram, DeepgramStatusText);
                return;
            }
        }

        DeepgramKeyBox.Password = "";
        _deepgramKeyVisible = false;
        DeepgramShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.Deepgram, DeepgramStatusText);
    }

    // =========================================================================
    // ASSEMBLYAI
    // =========================================================================

    private void AssemblyAIKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void AssemblyAIShowButton_Click(object sender, RoutedEventArgs e)
    {
        _assemblyAIKeyVisible = !_assemblyAIKeyVisible;
        AssemblyAIShowButton.Content = _assemblyAIKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_assemblyAIKeyVisible && ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.AssemblyAI))
        {
            var key = ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.AssemblyAI);
            AssemblyAIStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(TranscriptionApiKeyType.AssemblyAI, AssemblyAIStatusText);
        }
    }

    private void AssemblyAISaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = AssemblyAIKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.AssemblyAI, null), "AssemblyAI", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.AssemblyAI, AssemblyAIStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(TranscriptionApiKeyType.AssemblyAI, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.assemblyai"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.AssemblyAI, key), "AssemblyAI", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.AssemblyAI, AssemblyAIStatusText);
                return;
            }
        }

        AssemblyAIKeyBox.Password = "";
        _assemblyAIKeyVisible = false;
        AssemblyAIShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.AssemblyAI, AssemblyAIStatusText);
    }

    // =========================================================================
    // ELEVENLABS
    // =========================================================================

    private void ElevenLabsKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void ElevenLabsShowButton_Click(object sender, RoutedEventArgs e)
    {
        _elevenLabsKeyVisible = !_elevenLabsKeyVisible;
        ElevenLabsShowButton.Content = _elevenLabsKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_elevenLabsKeyVisible && ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.ElevenLabs))
        {
            var key = ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.ElevenLabs);
            ElevenLabsStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(TranscriptionApiKeyType.ElevenLabs, ElevenLabsStatusText);
        }
    }

    private void ElevenLabsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ElevenLabsKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.ElevenLabs, null), "ElevenLabs", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.ElevenLabs, ElevenLabsStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(TranscriptionApiKeyType.ElevenLabs, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.elevenlabs"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.ElevenLabs, key), "ElevenLabs", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.ElevenLabs, ElevenLabsStatusText);
                return;
            }
        }

        ElevenLabsKeyBox.Password = "";
        _elevenLabsKeyVisible = false;
        ElevenLabsShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.ElevenLabs, ElevenLabsStatusText);
    }

    // =========================================================================
    // MISTRAL
    // =========================================================================

    private void MistralKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void MistralShowButton_Click(object sender, RoutedEventArgs e)
    {
        _mistralKeyVisible = !_mistralKeyVisible;
        MistralShowButton.Content = _mistralKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_mistralKeyVisible && ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Mistral))
        {
            var key = ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Mistral);
            MistralStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(TranscriptionApiKeyType.Mistral, MistralStatusText);
        }
    }

    private void MistralSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = MistralKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Mistral, null), "Mistral", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Mistral, MistralStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(TranscriptionApiKeyType.Mistral, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.mistral"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Mistral, key), "Mistral", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Mistral, MistralStatusText);
                return;
            }
        }

        MistralKeyBox.Password = "";
        _mistralKeyVisible = false;
        MistralShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.Mistral, MistralStatusText);
    }

    // =========================================================================
    // SONIOX
    // =========================================================================

    private void SonioxKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void SonioxShowButton_Click(object sender, RoutedEventArgs e)
    {
        _sonioxKeyVisible = !_sonioxKeyVisible;
        SonioxShowButton.Content = _sonioxKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_sonioxKeyVisible && ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Soniox))
        {
            var key = ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Soniox);
            SonioxStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(TranscriptionApiKeyType.Soniox, SonioxStatusText);
        }
    }

    private void SonioxSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = SonioxKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Soniox, null), "Soniox", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Soniox, SonioxStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(TranscriptionApiKeyType.Soniox, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.soniox"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Soniox, key), "Soniox", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Soniox, SonioxStatusText);
                return;
            }
        }

        SonioxKeyBox.Password = "";
        _sonioxKeyVisible = false;
        SonioxShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.Soniox, SonioxStatusText);
    }

    // =========================================================================
    // META MUSE
    // =========================================================================

    private void MetaKeyBox_PasswordChanged(object sender, RoutedEventArgs e) { }

    private void MetaShowButton_Click(object sender, RoutedEventArgs e)
    {
        _metaKeyVisible = !_metaKeyVisible;
        MetaShowButton.Content = _metaKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_metaKeyVisible && ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Meta))
        {
            MetaStatusText.Text = ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Meta) ?? "";
        }
        else
        {
            UpdateKeyStatus(TranscriptionApiKeyType.Meta, MetaStatusText);
        }
    }

    private void MetaSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = MetaKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Meta, null), "Meta", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Meta, MetaStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(TranscriptionApiKeyType.Meta, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.meta"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Meta, key), "Meta", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.Meta, MetaStatusText);
                return;
            }
        }

        MetaKeyBox.Password = "";
        _metaKeyVisible = false;
        MetaShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.Meta, MetaStatusText);
    }

    // =========================================================================
    // GEMINI 3.5 TRANSCRIBE
    // =========================================================================

    private void GeminiTranscribeKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void GeminiTranscribeShowButton_Click(object sender, RoutedEventArgs e)
    {
        _geminiTranscribeKeyVisible = !_geminiTranscribeKeyVisible;
        GeminiTranscribeShowButton.Content = _geminiTranscribeKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");

        if (_geminiTranscribeKeyVisible && ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.GeminiTranscribe))
        {
            var key = ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.GeminiTranscribe);
            GeminiTranscribeStatusText.Text = key ?? "";
        }
        else
        {
            UpdateKeyStatus(TranscriptionApiKeyType.GeminiTranscribe, GeminiTranscribeStatusText);
        }
    }

    private void GeminiTranscribeSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = GeminiTranscribeKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.GeminiTranscribe, null), "Gemini 3.5 Transcribe", clearing: true))
            {
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.GeminiTranscribe, GeminiTranscribeStatusText);
                return;
            }
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(TranscriptionApiKeyType.GeminiTranscribe, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.geminiTranscribe"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!WriteKeyAndLog(() => ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.GeminiTranscribe, key), "Gemini 3.5 Transcribe", clearing: false))
            {
                // Keep the typed key in the box so the user can retry without retyping.
                ShowKeyWriteFailed();
                UpdateKeyStatus(TranscriptionApiKeyType.GeminiTranscribe, GeminiTranscribeStatusText);
                return;
            }
        }

        GeminiTranscribeKeyBox.Password = "";
        _geminiTranscribeKeyVisible = false;
        GeminiTranscribeShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.GeminiTranscribe, GeminiTranscribeStatusText);
    }

    // =========================================================================
    // GROK
    // =========================================================================

    private void UpdateGrokStatus()
    {
        UpdateKeyStatus(PostProcessingProvider.Grok, GrokStatusText);
        UpdateKeyStatus(PostProcessingProvider.Grok, GrokPostStatusText);
    }

    private void SyncGrokShowButtons()
    {
        var content = _grokKeyVisible ? Loc.S("settings.api.hide") : Loc.S("settings.api.show");
        GrokShowButton.Content = content;
        GrokPostShowButton.Content = content;

        if (_grokKeyVisible && ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Grok))
        {
            var key = ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Grok);
            GrokStatusText.Text = key ?? "";
            GrokPostStatusText.Text = key ?? "";
        }
        else
        {
            UpdateGrokStatus();
        }
    }

    private void GrokKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void GrokShowButton_Click(object sender, RoutedEventArgs e)
    {
        _grokKeyVisible = !_grokKeyVisible;
        SyncGrokShowButtons();
    }

    private void GrokPostKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Reserved for future dirty-state tracking
    }

    private void GrokPostShowButton_Click(object sender, RoutedEventArgs e)
    {
        _grokKeyVisible = !_grokKeyVisible;
        SyncGrokShowButtons();
    }

    private void GrokSaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSharedApiKey(_grokApiKeyCard, sender);
    }

    // =========================================================================
    // UTILITIES
    // =========================================================================

    /// <summary>
    /// Runs one key write and logs its outcome. "ApiKeys: Saved/Cleared" is
    /// written ONLY when Credential Manager took the change: it used to be logged
    /// unconditionally, so support read "Saved" for a key that was never stored
    /// (#742). Never logs the key or any masked form of it.
    /// </summary>
    /// <returns>True when the write succeeded.</returns>
    internal static bool WriteKeyAndLog(
        Func<PlatformContracts.PlatformResult> write,
        string logLabel,
        bool clearing)
    {
        var result = write();
        if (result.IsFailure)
        {
            LoggingService.Warn(
                $"ApiKeys: Could not {(clearing ? "clear" : "save")} {logLabel} API key ({result.Error?.Code})");
            return false;
        }

        LoggingService.Info(clearing
            ? $"ApiKeys: Cleared {logLabel} API key"
            : $"ApiKeys: Saved {logLabel} API key");
        return true;
    }

    private static void ShowKeyWriteFailed()
    {
        WpfMessageBox.Show(
            Loc.S("onboarding.setup.provider.saveFailed"),
            Loc.S("common.error"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }
}
