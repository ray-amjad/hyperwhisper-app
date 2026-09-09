using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Linux;

/// <summary>
/// The Linux port of Windows' CustomEndpointWindow (500x560, NoResize, CenterOwner). "+ Add
/// Endpoint" in the Model Library opens this modal; it used to navigate to the Modes page and
/// open the mode editor, which is a different screen answering a different question.
///
/// The three tabs prefill the name and the base URL the way Windows does
/// (CustomEndpointWindow.xaml.cs:109-143): LMStudio → http://localhost:1234/v1,
/// Ollama → http://localhost:11434, Custom → cleared.
/// </summary>
public partial class CustomEndpointWindow : Window
{
    private const string LmStudioName = "LMStudio";
    private const string LmStudioUrl = "http://localhost:1234/v1";
    private const string OllamaName = "Ollama";
    private const string OllamaUrl = "http://localhost:11434";

    private readonly Func<string, string, string, string, Task<bool>>? _save;
    private bool _isLoading = true;

    public CustomEndpointWindow() => AvaloniaXamlLoader.Load(this);

    /// <param name="save">name, url, model, apiKey → true when it was stored.</param>
    public CustomEndpointWindow(Func<string, string, string, string, Task<bool>> save) : this()
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _isLoading = false;
    }

    private static string L(string key) =>
        (Avalonia.Application.Current as App)?.Localization[key] ?? key;

    private static string LF(string key, params object[] args) =>
        (Avalonia.Application.Current as App)?.Localization.Format(key, args) ?? key;

    private TextBox? Field(string name) => this.FindControl<TextBox>(name);
    private bool Checked(string name) => this.FindControl<RadioButton>(name)?.IsChecked == true;

    /// <summary>
    /// Windows only overwrites the name and URL when they still hold a default, so a value the
    /// user typed survives a tab change (CustomEndpointWindow.xaml.cs:113-133).
    /// </summary>
    private void OnTabChanged(object? sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not RadioButton { IsChecked: true }) return;
        var name = Field("EndpointNameInput");
        var url = Field("EndpointUrlInput");
        if (name is null || url is null) return;

        var nameIsDefault = string.IsNullOrWhiteSpace(name.Text)
            || name.Text == OllamaName || name.Text == LmStudioName;
        var urlIsDefault = string.IsNullOrWhiteSpace(url.Text)
            || url.Text == OllamaUrl || url.Text == LmStudioUrl;

        if (Checked("EndpointTabLmStudio"))
        {
            if (nameIsDefault) name.Text = LmStudioName;
            if (urlIsDefault) url.Text = LmStudioUrl;
        }
        else if (Checked("EndpointTabOllama"))
        {
            if (nameIsDefault) name.Text = OllamaName;
            if (urlIsDefault) url.Text = OllamaUrl;
        }
        else
        {
            if (nameIsDefault) name.Text = string.Empty;
            if (urlIsDefault) url.Text = string.Empty;
        }
        ResetTestResult();
    }

    private void ResetTestResult()
    {
        SetVisible("EndpointTestSuccess", false);
        SetVisible("EndpointTestFail", false);
        SetVisible("EndpointTesting", false);
        SetVisible("EndpointTestResultPanel", false);
    }

    private void SetVisible(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control) control.IsVisible = visible;
    }

    /// <summary>
    /// Windows probes the endpoint before you commit it. The Linux probe is a plain GET of the
    /// OpenAI-compatible /models route, which is the call LMStudio, Ollama and every
    /// OpenAI-compatible server answer.
    /// </summary>
    private async void OnTestConnection(object? sender, RoutedEventArgs e)
    {
        var url = (Field("EndpointUrlInput")?.Text ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            ShowTestResult(false, L("linux.endpoint.validation.url"));
            return;
        }
        ResetTestResult();
        SetVisible("EndpointTesting", true);
        if (this.FindControl<Button>("EndpointTestButton") is { } button) button.IsEnabled = false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var apiKey = (Field("EndpointApiKeyInput")?.Text ?? string.Empty).Trim();
            if (apiKey.Length > 0)
                client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
            var probe = url.TrimEnd('/') + "/models";
            using var response = await client.GetAsync(probe);
            ShowTestResult(response.IsSuccessStatusCode, LF("linux.endpoint.test.status", (int)response.StatusCode, probe));
        }
        catch (Exception exception)
        {
            ShowTestResult(false, exception.Message);
        }
        finally
        {
            SetVisible("EndpointTesting", false);
            if (this.FindControl<Button>("EndpointTestButton") is { } testButton) testButton.IsEnabled = true;
        }
    }

    private void ShowTestResult(bool ok, string detail)
    {
        SetVisible("EndpointTesting", false);
        SetVisible("EndpointTestSuccess", ok);
        SetVisible("EndpointTestFail", !ok);
        if (this.FindControl<TextBlock>("EndpointTestResultText") is { } text) text.Text = detail;
        SetVisible("EndpointTestResultPanel", true);
    }

    /// <summary>
    /// Windows validates in three OK boxes titled "Validation" before saving
    /// (CustomEndpointWindow.xaml.cs). The URL and model rules come from the shared core, the
    /// same strict verdict the runtime uses, rather than a fourth hand-written variant.
    /// </summary>
    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_save is null) { Close(); return; }
        var name = (Field("EndpointNameInput")?.Text ?? string.Empty).Trim();
        var url = (Field("EndpointUrlInput")?.Text ?? string.Empty).Trim();
        var model = (Field("EndpointModelInput")?.Text ?? string.Empty).Trim();
        var apiKey = (Field("EndpointApiKeyInput")?.Text ?? string.Empty).Trim();

        string? failure = null;
        if (name.Length == 0) failure = L("linux.endpoint.validation.name");
        else if (url.Length == 0) failure = L("linux.endpoint.validation.url");
        else if (model.Length == 0) failure = L("linux.endpoint.validation.model");
        else if (LlmPostProcessing.NormalizeCustomEndpoint(url, model).Status != PortableEndpointStatus.Valid)
            failure = L("linux.endpoint.validation.url");

        if (failure is not null)
        {
            await ConfirmWindow.ShowNoticeAsync(this, L("linux.endpoint.validation.title"), failure);
            return;
        }

        if (await _save(name, url, model, apiKey)) Close();
        else await ConfirmWindow.ShowNoticeAsync(this, L("linux.endpoint.validation.title"),
            L("linux.endpoint.validation.saveFailed"));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Windows marks Add Endpoint IsDefault; Esc closes.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter) { e.Handled = true; OnSave(sender, e); }
    }
}
