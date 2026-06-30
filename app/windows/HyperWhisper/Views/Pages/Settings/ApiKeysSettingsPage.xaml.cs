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

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace HyperWhisper.Views.Pages.Settings;

public partial class ApiKeysSettingsPage : Page
{
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

    public ApiKeysSettingsPage()
    {
        InitializeComponent();
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

    private void OpenAITranscriptionSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = OpenAITranscriptionKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.OpenAI, null);
            LoggingService.Info("ApiKeys: Cleared OpenAI API key");
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.OpenAI, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.openai"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.OpenAI, key);
            LoggingService.Info("ApiKeys: Saved OpenAI API key");
        }

        OpenAITranscriptionKeyBox.Password = "";
        OpenAIPostKeyBox.Password = "";
        _openAIKeyVisible = false;
        OpenAITranscriptionShowButton.Content = Loc.S("settings.api.show");
        OpenAIPostShowButton.Content = Loc.S("settings.api.show");
        UpdateOpenAIStatus();
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

    private void OpenAIPostSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = OpenAIPostKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.OpenAI, null);
            LoggingService.Info("ApiKeys: Cleared OpenAI API key");
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.OpenAI, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.openai"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.OpenAI, key);
            LoggingService.Info("ApiKeys: Saved OpenAI API key");
        }

        OpenAITranscriptionKeyBox.Password = "";
        OpenAIPostKeyBox.Password = "";
        _openAIKeyVisible = false;
        OpenAITranscriptionShowButton.Content = Loc.S("settings.api.show");
        OpenAIPostShowButton.Content = Loc.S("settings.api.show");
        UpdateOpenAIStatus();
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
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Anthropic, null);
            LoggingService.Info("ApiKeys: Cleared Anthropic API key");
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

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Anthropic, key);
            LoggingService.Info("ApiKeys: Saved Anthropic API key");
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

    private void GroqTranscriptionSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = GroqTranscriptionKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Groq, null);
            LoggingService.Info("ApiKeys: Cleared Groq API key");
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.Groq, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.groq"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Groq, key);
            LoggingService.Info("ApiKeys: Saved Groq API key");
        }

        GroqTranscriptionKeyBox.Password = "";
        GroqPostKeyBox.Password = "";
        _groqKeyVisible = false;
        GroqTranscriptionShowButton.Content = Loc.S("settings.api.show");
        GroqPostShowButton.Content = Loc.S("settings.api.show");
        UpdateGroqStatus();
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

    private void GroqPostSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = GroqPostKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Groq, null);
            LoggingService.Info("ApiKeys: Cleared Groq API key");
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.Groq, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.groq"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Groq, key);
            LoggingService.Info("ApiKeys: Saved Groq API key");
        }

        GroqTranscriptionKeyBox.Password = "";
        GroqPostKeyBox.Password = "";
        _groqKeyVisible = false;
        GroqTranscriptionShowButton.Content = Loc.S("settings.api.show");
        GroqPostShowButton.Content = Loc.S("settings.api.show");
        UpdateGroqStatus();
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

    private void GeminiTranscriptionSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = GeminiTranscriptionKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Gemini, null);
            LoggingService.Info("ApiKeys: Cleared Gemini API key");
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.Gemini, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.gemini"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Gemini, key);
            LoggingService.Info("ApiKeys: Saved Gemini API key");
        }

        GeminiTranscriptionKeyBox.Password = "";
        GeminiKeyBox.Password = "";
        _geminiKeyVisible = false;
        GeminiTranscriptionShowButton.Content = Loc.S("settings.api.show");
        GeminiShowButton.Content = Loc.S("settings.api.show");
        UpdateGeminiStatus();
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

    private void GeminiSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = GeminiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Gemini, null);
            LoggingService.Info("ApiKeys: Cleared Gemini API key");
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.Gemini, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.gemini"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Gemini, key);
            LoggingService.Info("ApiKeys: Saved Gemini API key");
        }

        GeminiTranscriptionKeyBox.Password = "";
        GeminiKeyBox.Password = "";
        _geminiKeyVisible = false;
        GeminiTranscriptionShowButton.Content = Loc.S("settings.api.show");
        GeminiShowButton.Content = Loc.S("settings.api.show");
        UpdateGeminiStatus();
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
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Cerebras, null);
            LoggingService.Info("ApiKeys: Cleared Cerebras API key");
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

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Cerebras, key);
            LoggingService.Info("ApiKeys: Saved Cerebras API key");
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
            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Deepgram, null);
            LoggingService.Info("ApiKeys: Cleared Deepgram API key");
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

            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Deepgram, key);
            LoggingService.Info("ApiKeys: Saved Deepgram API key");
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
            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.AssemblyAI, null);
            LoggingService.Info("ApiKeys: Cleared AssemblyAI API key");
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

            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.AssemblyAI, key);
            LoggingService.Info("ApiKeys: Saved AssemblyAI API key");
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
            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.ElevenLabs, null);
            LoggingService.Info("ApiKeys: Cleared ElevenLabs API key");
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

            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.ElevenLabs, key);
            LoggingService.Info("ApiKeys: Saved ElevenLabs API key");
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
            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Mistral, null);
            LoggingService.Info("ApiKeys: Cleared Mistral API key");
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

            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Mistral, key);
            LoggingService.Info("ApiKeys: Saved Mistral API key");
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
            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Soniox, null);
            LoggingService.Info("ApiKeys: Cleared Soniox API key");
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

            ApiKeyService.Instance.SetApiKey(TranscriptionApiKeyType.Soniox, key);
            LoggingService.Info("ApiKeys: Saved Soniox API key");
        }

        SonioxKeyBox.Password = "";
        _sonioxKeyVisible = false;
        SonioxShowButton.Content = Loc.S("settings.api.show");
        UpdateKeyStatus(TranscriptionApiKeyType.Soniox, SonioxStatusText);
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
        SaveGrokApiKey(GrokKeyBox.Password);
    }

    private void GrokPostSaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveGrokApiKey(GrokPostKeyBox.Password);
    }

    private void SaveGrokApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Grok, null);
            LoggingService.Info("ApiKeys: Cleared Grok API key");
        }
        else
        {
            if (!ApiKeyService.IsValidKeyFormat(PostProcessingProvider.Grok, key))
            {
                WpfMessageBox.Show(
                    Loc.S("settings.api.invalidKey.grok"),
                    Loc.S("settings.api.invalidKey.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ApiKeyService.Instance.SetApiKey(PostProcessingProvider.Grok, key);
            LoggingService.Info("ApiKeys: Saved Grok API key");
        }

        GrokKeyBox.Password = "";
        GrokPostKeyBox.Password = "";
        _grokKeyVisible = false;
        GrokShowButton.Content = Loc.S("settings.api.show");
        GrokPostShowButton.Content = Loc.S("settings.api.show");
        UpdateGrokStatus();
    }

    // =========================================================================
    // UTILITIES
    // =========================================================================

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
