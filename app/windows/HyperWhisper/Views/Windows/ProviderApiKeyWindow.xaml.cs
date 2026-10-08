using System.Diagnostics;
using System.Windows;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Views.Windows;

public partial class ProviderApiKeyWindow : Window
{
    private readonly string _displayName;
    private readonly string _apiKeyUrl;
    private readonly Func<string?> _getKey;
    private readonly Func<string?, PlatformContracts.PlatformResult> _setKey;
    private readonly Func<string?, bool> _validateKey;
    private readonly string _invalidMessage;

    public ProviderApiKeyWindow(CloudTranscriptionProvider provider)
    {
        var pairedProvider = provider.GetApiKeyProvider();
        if (pairedProvider != PostProcessingProvider.None)
        {
            (_displayName, _apiKeyUrl, _getKey, _setKey, _validateKey, _invalidMessage) =
                CreatePostProcessingTarget(pairedProvider);
        }
        else
        {
            var keyType = ToTranscriptionKeyType(provider);
            _displayName = keyType.GetDisplayName();
            _apiKeyUrl = keyType.GetApiKeyUrl();
            _getKey = () => ApiKeyService.Instance.GetApiKey(keyType);
            _setKey = key => ApiKeyService.Instance.SetApiKey(keyType, key);
            _validateKey = key => ApiKeyService.IsValidKeyFormat(keyType, key);
            _invalidMessage = Loc.S("providerApiKey.invalid.message", _displayName);
        }

        InitializeComponent();
        Configure();
    }

    public ProviderApiKeyWindow(PostProcessingProvider provider)
    {
        (_displayName, _apiKeyUrl, _getKey, _setKey, _validateKey, _invalidMessage) =
            CreatePostProcessingTarget(provider);

        InitializeComponent();
        Configure();
    }

    private void Configure()
    {
        Title = Loc.S("providerApiKey.title", _displayName);
        TitleText.Text = Title;
        SubtitleText.Text = Loc.S("providerApiKey.subtitle", _displayName);
        ProviderNameText.Text = _displayName;

        var existingKey = _getKey();
        CurrentStatusText.Text = string.IsNullOrWhiteSpace(existingKey)
            ? Loc.S("providerApiKey.currentStatus.none")
            : Loc.S("providerApiKey.currentStatus.configured", ApiKeyService.MaskKeyForDisplay(existingKey));
        ApiKeyBox.Password = existingKey ?? string.Empty;
        ClearButton.IsEnabled = !string.IsNullOrWhiteSpace(existingKey);
        GetKeyButton.Visibility = string.IsNullOrWhiteSpace(_apiKeyUrl) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GetKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_apiKeyUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _apiKeyUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Error($"ProviderApiKeyWindow: Failed to open key page: {ex.Message}");
            WpfMessageBox.Show(
                Loc.S("settings.general.support.openFailed", ex.Message),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!TryWriteKey(null)) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            DialogResult = false;
            Close();
            return;
        }

        if (!_validateKey(key))
        {
            WpfMessageBox.Show(_invalidMessage, Loc.S("providerApiKey.invalid.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // On failure the window stays open with the typed key, so the user can retry.
        if (!TryWriteKey(key)) return;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Writes (or clears, for null) the key. A Credential Manager failure is shown
    /// instead of being swallowed (#742); the key is never logged.
    /// </summary>
    private bool TryWriteKey(string? key)
    {
        var result = _setKey(key);
        if (result.IsSuccess) return true;

        LoggingService.Warn(
            $"ProviderApiKeyWindow: Could not {(key == null ? "clear" : "save")} {_displayName} API key ({result.Error?.Code})");
        WpfMessageBox.Show(
            Loc.S("onboarding.setup.provider.saveFailed"),
            Loc.S("common.error"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private static (string displayName, string apiKeyUrl, Func<string?> getKey, Func<string?, PlatformContracts.PlatformResult> setKey, Func<string?, bool> validateKey, string invalidMessage)
        CreatePostProcessingTarget(PostProcessingProvider provider)
    {
        var displayName = provider.ToDisplayName();
        return (
            displayName,
            GetPostProcessingApiKeyUrl(provider),
            () => ApiKeyService.Instance.GetApiKey(provider),
            key => ApiKeyService.Instance.SetApiKey(provider, key),
            key => ApiKeyService.IsValidKeyFormat(provider, key),
            Loc.S("providerApiKey.invalid.message", displayName)
        );
    }

    private static TranscriptionApiKeyType ToTranscriptionKeyType(CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.Deepgram => TranscriptionApiKeyType.Deepgram,
        CloudTranscriptionProvider.AssemblyAI => TranscriptionApiKeyType.AssemblyAI,
        CloudTranscriptionProvider.ElevenLabs => TranscriptionApiKeyType.ElevenLabs,
        CloudTranscriptionProvider.Mistral => TranscriptionApiKeyType.Mistral,
        CloudTranscriptionProvider.Soniox => TranscriptionApiKeyType.Soniox,
        CloudTranscriptionProvider.Grok => TranscriptionApiKeyType.Grok,
        CloudTranscriptionProvider.GeminiTranscribe => TranscriptionApiKeyType.GeminiTranscribe,
        CloudTranscriptionProvider.Meta => TranscriptionApiKeyType.Meta,
        _ => throw new InvalidOperationException($"{provider} does not have a separate transcription API key.")
    };

    private static string GetPostProcessingApiKeyUrl(PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.OpenAI => "https://platform.openai.com/api-keys",
        PostProcessingProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        PostProcessingProvider.Groq => "https://console.groq.com/keys",
        PostProcessingProvider.Grok => "https://console.x.ai/",
        PostProcessingProvider.Gemini => "https://aistudio.google.com/apikey",
        PostProcessingProvider.Cerebras => "https://cloud.cerebras.ai/",
        PostProcessingProvider.Mistral => "https://console.mistral.ai/api-keys",
        _ => ""
    };
}
