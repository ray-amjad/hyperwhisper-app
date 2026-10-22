using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HyperWhisper.PortableApplication.ViewModels;

namespace HyperWhisper.Linux;

/// <summary>
/// The Linux port of Windows' ProviderApiKeyWindow (460x360, NoResize, CenterOwner). The Model
/// Library's "Connect" / "Needs attention" row action opens this modal over the library, instead
/// of navigating the whole app to the credentials page and leaving the user to walk back.
///
/// It drives the same <see cref="CredentialManagementViewModel"/> the credentials page uses, so
/// there is one credential path on Linux, not two.
/// </summary>
public partial class ProviderApiKeyWindow : Window
{
    private readonly CredentialManagementViewModel? _credentials;
    private readonly string _account = string.Empty;
    private readonly string _displayName = string.Empty;
    private readonly string _apiKeyUrl = string.Empty;

    public ProviderApiKeyWindow() => AvaloniaXamlLoader.Load(this);

    public ProviderApiKeyWindow(CredentialManagementViewModel credentials, string account,
        string displayName, string apiKeyUrl) : this()
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _account = account;
        _displayName = displayName;
        _apiKeyUrl = apiKeyUrl ?? string.Empty;

        credentials.SelectAccount(account);
        var configured = credentials.Items.FirstOrDefault(item =>
            string.Equals(item.Account, account, StringComparison.OrdinalIgnoreCase))?.IsPresent == true;

        Title = LF("providerApiKey.title", displayName);
        SetText("ProviderKeyTitle", Title);
        SetText("ProviderKeySubtitle", LF("providerApiKey.subtitle", displayName));
        SetText("ProviderKeyName", displayName);
        // Windows shows the masked key it already holds; the Linux store never hands a stored
        // secret back to the UI, so a configured provider reports only that it is configured.
        SetText("ProviderKeyStatus", configured
            ? LF("providerApiKey.currentStatus.configured", MaskedPlaceholder)
            : L("providerApiKey.currentStatus.none"));
        if (this.FindControl<Button>("ProviderKeyClearButton") is { } clear) clear.IsEnabled = configured;
        if (this.FindControl<Button>("ProviderKeyGetKeyButton") is { } getKey)
            getKey.IsVisible = _apiKeyUrl.Length > 0;
    }

    private const string MaskedPlaceholder = "••••";

    private void SetText(string name, string? value)
    {
        if (this.FindControl<TextBlock>(name) is { } block) block.Text = value;
    }

    private static string L(string key) =>
        (Avalonia.Application.Current as App)?.Localization[key] ?? key;

    private static string LF(string key, params object[] args) =>
        (Avalonia.Application.Current as App)?.Localization.Format(key, args) ?? key;

    private void OnGetKey(object? sender, RoutedEventArgs e)
    {
        if (_apiKeyUrl.Length == 0) return;
        try { _ = Process.Start(new ProcessStartInfo(_apiKeyUrl) { UseShellExecute = true }); }
        catch (Exception) { SetText("ProviderKeyStatus", L("settings.general.support.openFailed")); }
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        if (_credentials is null) return;
        _credentials.SelectAccount(_account);
        if (_credentials.DeleteCommand.CanExecute(null)) _credentials.DeleteCommand.Execute(null);
        Close();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_credentials is null) { Close(); return; }
        var secret = (this.FindControl<TextBox>("ProviderKeyInput")?.Text ?? string.Empty).Trim();

        // Windows treats an empty field as "I changed my mind": DialogResult=false and close, with
        // the stored key left alone (ProviderApiKeyWindow.xaml.cs:104-109). Linux ran DeleteCommand
        // instead, so tabbing through the modal and pressing Enter silently destroyed a working
        // key. Clearing is what the Clear button is for, and it is right next to Save.
        if (string.IsNullOrWhiteSpace(secret)) { Close(); return; }

        // No shape check after this: a key only has to be non-blank. Vendors change their key
        // formats (Google's newer Gemini keys do not start with "AIza", which a prefix rule here
        // refused), and a wrong key fails at the provider, which says so.
        _credentials.SelectAccount(_account);
        _credentials.Secret = secret;
        if (_credentials.SaveCommand.CanExecute(null)) _credentials.SaveCommand.Execute(null);
        if (_credentials.Status.HasError)
        {
            // A failed write leaves the typed key in Secret so the credentials page can offer it
            // again (#1388). This modal keeps its own ProviderKeyInput, and Secret lives on the
            // shared view model, so clearing it here stops a cancelled modal from pre-filling the
            // credentials page, where a Save under another account would store this key there.
            _credentials.Secret = string.Empty;
            SetText("ProviderKeyStatus", _credentials.Status.Message);
            return;
        }
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Windows marks Save IsDefault and Cancel closes the dialog.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter) { e.Handled = true; OnSave(sender, e); }
    }
}
