using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using HyperWhisper.PortableApplication.ViewModels;

namespace HyperWhisper.Linux;

/// <summary>
/// The Linux port of the 760x760 modal Windows opens from the Model Library's "API keys" button.
/// Windows hosts its whole ApiKeysSettingsPage inside a plain Window with Owner = MainWindow and
/// CenterOwner; this hosts the credentials view model through the app-level DataTemplate, so the
/// same surface the credentials page shows appears over the library instead of replacing it.
/// </summary>
public partial class CredentialsWindow : Window
{
    public CredentialsWindow() => AvaloniaXamlLoader.Load(this);

    public CredentialsWindow(CredentialManagementViewModel credentials) : this()
        => DataContext = credentials ?? throw new ArgumentNullException(nameof(credentials));

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
