using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// Modal window for adding or editing a custom OpenAI-compatible post-processing endpoint.
/// Supports three tabs: LMStudio, Ollama, Custom.
/// </summary>
public partial class CustomEndpointWindow : Window
{
    private readonly CustomPostProcessingEndpoint? _existingEndpoint;
    private bool _isLoading = true;
    private bool _isTesting;

    /// <summary>The saved endpoint after successful save.</summary>
    public CustomPostProcessingEndpoint? SavedEndpoint { get; private set; }

    // =========================================================================
    // CONSTRUCTORS
    // =========================================================================

    /// <summary>Create window for adding a new endpoint.</summary>
    public CustomEndpointWindow()
    {
        InitializeComponent();
        _existingEndpoint = null;
        Loaded += OnLoaded;
    }

    /// <summary>Create window for editing an existing endpoint.</summary>
    public CustomEndpointWindow(CustomPostProcessingEndpoint endpoint)
    {
        InitializeComponent();
        _existingEndpoint = endpoint;
        Loaded += OnLoaded;
    }

    // =========================================================================
    // INITIALIZATION
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;

        if (_existingEndpoint != null)
        {
            TitleText.Text = "Edit Endpoint";
            SaveButton.Content = "Save Changes";

            // Load existing data
            NameTextBox.Text = _existingEndpoint.Name;
            ModelTextBox.Text = _existingEndpoint.ModelName;

            // Load API key
            var apiKey = ApiKeyService.Instance.GetCustomEndpointApiKey(_existingEndpoint.Id);
            if (!string.IsNullOrEmpty(apiKey))
                ApiKeyBox.Password = apiKey;

            // Detect tab from URL
            if (_existingEndpoint.EndpointURL.Contains("localhost:1234"))
            {
                TabLMStudio.IsChecked = true;
                // Strip /chat/completions to get base URL
                var url = _existingEndpoint.EndpointURL;
                if (url.EndsWith("/chat/completions"))
                    url = url[..^"/chat/completions".Length];
                UrlTextBox.Text = url;
            }
            else if (_existingEndpoint.EndpointURL.Contains("localhost:11434"))
            {
                TabOllama.IsChecked = true;
                // Strip /v1/chat/completions to get base URL
                var url = _existingEndpoint.EndpointURL;
                if (url.EndsWith("/v1/chat/completions"))
                    url = url[..^"/v1/chat/completions".Length];
                UrlTextBox.Text = url;
            }
            else
            {
                TabCustom.IsChecked = true;
                // Strip /chat/completions to show base URL
                var url = _existingEndpoint.EndpointURL;
                if (url.EndsWith("/chat/completions"))
                    url = url[..^"/chat/completions".Length];
                UrlTextBox.Text = url;
            }
        }
        else
        {
            // New endpoint - start on Custom tab
            TabCustom.IsChecked = true;
        }

        _isLoading = false;
        UpdateTabUI();
    }

    // =========================================================================
    // TAB HANDLING
    // =========================================================================

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var currentNameIsDefault = string.IsNullOrWhiteSpace(NameTextBox.Text) ||
                                   NameTextBox.Text == "Ollama" ||
                                   NameTextBox.Text == "LMStudio";
        var currentUrlIsDefault = string.IsNullOrWhiteSpace(UrlTextBox.Text) ||
                                  UrlTextBox.Text == "http://localhost:11434" ||
                                  UrlTextBox.Text == "http://localhost:1234/v1";

        if (TabLMStudio.IsChecked == true)
        {
            if (currentNameIsDefault) NameTextBox.Text = "LMStudio";
            if (currentUrlIsDefault) UrlTextBox.Text = "http://localhost:1234/v1";
        }
        else if (TabOllama.IsChecked == true)
        {
            if (currentNameIsDefault) NameTextBox.Text = "Ollama";
            if (currentUrlIsDefault) UrlTextBox.Text = "http://localhost:11434";
        }
        else
        {
            if (currentNameIsDefault) NameTextBox.Text = "";
            if (currentUrlIsDefault) UrlTextBox.Text = "";
        }

        ClearTestResult();
        UpdateTabUI();

        // Fetch models for LMStudio/Ollama
        if (TabLMStudio.IsChecked == true || TabOllama.IsChecked == true)
        {
            _ = FetchModelsAsync();
        }
    }

    private void UpdateTabUI()
    {
        if (TabCustom == null) return; // Not yet initialized

        var isCustom = TabCustom.IsChecked == true;

        // URL label and hint
        if (isCustom)
        {
            UrlLabel.Text = "Base URL";
            UrlHintText.Text = "Base URL of your OpenAI-compatible API (e.g. https://openrouter.ai/api/v1)";
        }
        else
        {
            UrlLabel.Text = "Base URL";
            UrlHintText.Text = "Base URL of your local server";
        }

        // Model: show ComboBox for LMStudio/Ollama, TextBox for Custom
        if (isCustom)
        {
            ModelCombo.Visibility = Visibility.Collapsed;
            ModelTextBox.Visibility = Visibility.Visible;
            RefreshModelsButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Show combo if models are loaded, otherwise show textbox as fallback
            RefreshModelsButton.Visibility = Visibility.Visible;
            if (ModelCombo.Items.Count > 0)
            {
                ModelCombo.Visibility = Visibility.Visible;
                ModelTextBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                ModelCombo.Visibility = Visibility.Collapsed;
                ModelTextBox.Visibility = Visibility.Visible;
            }
        }
    }

    // =========================================================================
    // MODEL FETCHING
    // =========================================================================

    private void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        _ = FetchModelsAsync();
    }

    private async Task FetchModelsAsync()
    {
        var baseUrl = UrlTextBox.Text.Trim();
        if (string.IsNullOrEmpty(baseUrl)) return;

        FetchingPanel.Visibility = Visibility.Visible;
        ModelFetchErrorText.Visibility = Visibility.Collapsed;
        ModelCombo.Items.Clear();

        List<string> models;

        if (TabOllama.IsChecked == true)
        {
            models = await LocalModelFetcher.FetchOllamaModelsAsync(baseUrl);
        }
        else if (TabLMStudio.IsChecked == true)
        {
            models = await LocalModelFetcher.FetchLMStudioModelsAsync(baseUrl);
        }
        else
        {
            FetchingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        FetchingPanel.Visibility = Visibility.Collapsed;

        if (models.Count == 0)
        {
            var providerName = TabOllama.IsChecked == true ? "Ollama" : "LMStudio";
            ModelFetchErrorText.Text = $"Could not fetch models. Ensure {providerName} is running.";
            ModelFetchErrorText.Visibility = Visibility.Visible;

            // Show textbox as fallback
            ModelCombo.Visibility = Visibility.Collapsed;
            ModelTextBox.Visibility = Visibility.Visible;
            return;
        }

        // Populate ComboBox
        foreach (var model in models)
        {
            ModelCombo.Items.Add(new ComboBoxItem { Content = model, Tag = model });
        }

        // Auto-select existing model or first model
        var existingModel = _existingEndpoint?.ModelName ?? ModelTextBox.Text;
        var matchIndex = -1;
        for (int i = 0; i < ModelCombo.Items.Count; i++)
        {
            if (ModelCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == existingModel)
            {
                matchIndex = i;
                break;
            }
        }
        ModelCombo.SelectedIndex = matchIndex >= 0 ? matchIndex : 0;

        // Show ComboBox, hide TextBox
        ModelCombo.Visibility = Visibility.Visible;
        ModelTextBox.Visibility = Visibility.Collapsed;
        ModelFetchErrorText.Visibility = Visibility.Collapsed;
    }

    // =========================================================================
    // TEST CONNECTION
    // =========================================================================

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting) return;
        _isTesting = true;

        TestButton.IsEnabled = false;
        ClearTestResult();
        TestingPanel.Visibility = Visibility.Visible;

        var (url, model) = GetEndpointUrlAndModel();

        var result = await CustomEndpointManager.Instance.TestEndpointAsync(
            url, model, ApiKeyBox.Password);

        TestingPanel.Visibility = Visibility.Collapsed;

        if (result.success)
        {
            TestSuccessPanel.Visibility = Visibility.Visible;
            TestResultPanel.Visibility = Visibility.Visible;
            TestResultPanel.Background = FindResource("SuccessBackgroundBrush") as System.Windows.Media.Brush;
            TestResultText.Text = $"Response: {result.message}";
        }
        else
        {
            TestFailPanel.Visibility = Visibility.Visible;
            TestResultPanel.Visibility = Visibility.Visible;
            TestResultPanel.Background = FindResource("ErrorBackgroundBrush") as System.Windows.Media.Brush;
            TestResultText.Text = result.message;
        }

        TestButton.IsEnabled = true;
        _isTesting = false;
    }

    private void ClearTestResult()
    {
        TestSuccessPanel.Visibility = Visibility.Collapsed;
        TestFailPanel.Visibility = Visibility.Collapsed;
        TestingPanel.Visibility = Visibility.Collapsed;
        TestResultPanel.Visibility = Visibility.Collapsed;
    }

    // =========================================================================
    // SAVE / CANCEL
    // =========================================================================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        var (endpointUrl, modelName) = GetEndpointUrlAndModel();
        var apiKey = ApiKeyBox.Password;

        // Validate
        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show("Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            System.Windows.MessageBox.Show("Endpoint URL is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(modelName))
        {
            System.Windows.MessageBox.Show("Model name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_existingEndpoint != null)
        {
            // Update existing
            var success = CustomEndpointManager.Instance.UpdateEndpoint(
                _existingEndpoint.Id,
                name: name,
                endpointURL: endpointUrl,
                modelName: modelName,
                apiKey: apiKey);

            if (success)
            {
                SavedEndpoint = CustomEndpointManager.Instance.GetEndpoint(_existingEndpoint.Id);
                DialogResult = true;
            }
        }
        else
        {
            // Create new
            SavedEndpoint = CustomEndpointManager.Instance.AddEndpoint(
                name, endpointUrl, modelName,
                string.IsNullOrEmpty(apiKey) ? null : apiKey);

            if (SavedEndpoint != null)
            {
                DialogResult = true;
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Build the full endpoint URL and get the model name based on the current tab.
    /// </summary>
    private (string url, string model) GetEndpointUrlAndModel()
    {
        var baseUrl = UrlTextBox.Text.Trim().TrimEnd('/');

        if (TabOllama.IsChecked == true)
        {
            var model = GetSelectedModel();
            return ($"{baseUrl}/v1/chat/completions", model);
        }
        else if (TabLMStudio.IsChecked == true)
        {
            var model = GetSelectedModel();
            return ($"{baseUrl}/chat/completions", model);
        }
        else
        {
            // Custom - append /chat/completions if not already present
            var url = baseUrl;
            if (!url.EndsWith("/chat/completions"))
                url = $"{url}/chat/completions";
            return (url, ModelTextBox.Text.Trim());
        }
    }

    /// <summary>
    /// Get the selected model from either the ComboBox or TextBox.
    /// </summary>
    private string GetSelectedModel()
    {
        if (ModelCombo.Visibility == Visibility.Visible && ModelCombo.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString() ?? "";
        }
        return ModelTextBox.Text.Trim();
    }
}
