using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Services.LocalApi;

using Brush = System.Windows.Media.Brush;

namespace HyperWhisper.Views.Pages.Settings;

public partial class LocalApiSettingsPage : Page
{
    private const string DocsUrl = "https://hyperwhisper.com/docs/api-reference/local-api/overview";
    private const string McpDocsUrl = "https://hyperwhisper.com/docs/api-reference/local-api/mcp-setup";

    private enum Tab { Connection, Mcp, Curl }

    private Tab _selectedTab = Tab.Connection;
    private bool _tokenRevealed;
    private bool _suppressToggleEvent;

    public LocalApiSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LocalApiServer.Instance.PropertyChanged += OnServerPropertyChanged;

        _suppressToggleEvent = true;
        EnabledToggle.IsChecked = SettingsService.Instance.LocalApiServerEnabled;
        _suppressToggleEvent = false;

        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LocalApiServer.Instance.PropertyChanged -= OnServerPropertyChanged;
    }

    // ---------------------------------------------------------------- events

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnServerPropertyChanged(sender, e));
            return;
        }
        Refresh();
    }

    private void EnabledToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvent) return;
        SettingsService.Instance.LocalApiServerEnabled = true;
        LocalApiServer.Instance.Start();
        Refresh();
    }

    private void EnabledToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvent) return;
        SettingsService.Instance.LocalApiServerEnabled = false;
        LocalApiServer.Instance.Stop();
        Refresh();
    }

    private void DocsButton_Click(object sender, RoutedEventArgs e) => OpenUrl(DocsUrl);
    private void McpDocs_Click(object sender, RoutedEventArgs e) => OpenUrl(McpDocsUrl);

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        _selectedTab = btn.Tag?.ToString() switch
        {
            "mcp" => Tab.Mcp,
            "curl" => Tab.Curl,
            _ => Tab.Connection
        };
        Refresh();
    }

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        var port = LocalApiServer.Instance.ListeningPort;
        if (port > 0)
        {
            TrySetClipboardText(port.ToString());
        }
        else
        {
            ShowActionStatus(Loc.S("settings.localApi.action.portUnavailable"), isError: true);
        }
    }

    private void RevealToken_Click(object sender, RoutedEventArgs e)
    {
        _tokenRevealed = !_tokenRevealed;
        Refresh();
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        var token = LocalApiServer.Instance.BearerToken;
        if (!string.IsNullOrEmpty(token))
        {
            TrySetClipboardText(token);
        }
        else
        {
            ShowActionStatus(Loc.S("settings.localApi.action.tokenUnavailable"), isError: true);
        }
    }

    private void RegenerateToken_Click(object sender, RoutedEventArgs e)
    {
        LocalApiServer.Instance.RegenerateBearerToken();
        Refresh();
        ShowActionStatus(Loc.S("settings.localApi.action.tokenRegenerated"), isError: false);
    }

    private void ShowPortFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(LocalApiDiscoveryFile.FilePath))
        {
            ShowActionStatus(Loc.S("settings.localApi.action.fileMissing"), isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LocalApiDiscoveryFile.FilePath}\"")
            {
                UseShellExecute = true
            });
            ShowActionStatus(Loc.S("settings.localApi.action.revealed"), isError: false);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiSettingsPage: failed to reveal port file: {ex.Message}");
            ShowActionStatus(Loc.S("settings.localApi.action.revealFailed", ex.Message), isError: true);
        }
    }

    private void CopyMcp_Click(object sender, RoutedEventArgs e) => TrySetClipboardText(McpSnippetBox.Text);
    private void CopyCurl_Click(object sender, RoutedEventArgs e) => TrySetClipboardText(CurlSnippetBox.Text);

    // ---------------------------------------------------------------- render

    private void Refresh()
    {
        var enabled = SettingsService.Instance.LocalApiServerEnabled;
        var server = LocalApiServer.Instance;

        EnabledLabel.Text = enabled
            ? Loc.S("settings.localApi.toggle.enabled")
            : Loc.S("settings.localApi.toggle.disabled");

        StatusLine.Text = StatusLineText(enabled, server);

        TabsRoot.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyTabSelection();

        PortValue.Text = server.ListeningPort > 0 ? server.ListeningPort.ToString() : "—";
        TokenValue.Text = FormatTokenDisplay(server.BearerToken, _tokenRevealed);
        RevealTokenButton.Content = _tokenRevealed
            ? Loc.S("settings.localApi.hide")
            : Loc.S("settings.localApi.reveal");
        PortFileValue.Text = LocalApiDiscoveryFile.FilePath;
        ShowPortFileButton.IsEnabled = File.Exists(LocalApiDiscoveryFile.FilePath);

        var tokenEmpty = string.IsNullOrEmpty(server.BearerToken);
        RevealTokenButton.IsEnabled = !tokenEmpty;
        CopyTokenButton.IsEnabled = !tokenEmpty;
        RegenerateTokenButton.IsEnabled = !tokenEmpty;
        CopyPortButton.IsEnabled = server.ListeningPort > 0;

        if (!string.IsNullOrEmpty(server.LastError))
        {
            ErrorRow.Visibility = Visibility.Visible;
            ErrorText.Text = server.LastError;
        }
        else
        {
            ErrorRow.Visibility = Visibility.Collapsed;
        }

        McpSnippetBox.Text = BuildMcpSnippet();
        CurlSnippetBox.Text = BuildCurlSnippet(server);
    }

    private void ApplyTabSelection()
    {
        ConnectionTab.Visibility = _selectedTab == Tab.Connection ? Visibility.Visible : Visibility.Collapsed;
        McpTab.Visibility = _selectedTab == Tab.Mcp ? Visibility.Visible : Visibility.Collapsed;
        CurlTab.Visibility = _selectedTab == Tab.Curl ? Visibility.Visible : Visibility.Collapsed;

        var accent = (Brush)FindResource("AccentBrush");
        var primary = (Brush)FindResource("TextPrimaryBrush");
        var secondary = (Brush)FindResource("TextSecondaryBrush");
        var transparent = WpfBrushes.Transparent;

        ConnectionTabUnderline.Fill = _selectedTab == Tab.Connection ? accent : transparent;
        McpTabUnderline.Fill = _selectedTab == Tab.Mcp ? accent : transparent;
        CurlTabUnderline.Fill = _selectedTab == Tab.Curl ? accent : transparent;

        ConnectionTabLabel.Foreground = _selectedTab == Tab.Connection ? primary : secondary;
        McpTabLabel.Foreground = _selectedTab == Tab.Mcp ? primary : secondary;
        CurlTabLabel.Foreground = _selectedTab == Tab.Curl ? primary : secondary;

        ConnectionTabLabel.FontWeight = _selectedTab == Tab.Connection ? FontWeights.SemiBold : FontWeights.Normal;
        McpTabLabel.FontWeight = _selectedTab == Tab.Mcp ? FontWeights.SemiBold : FontWeights.Normal;
        CurlTabLabel.FontWeight = _selectedTab == Tab.Curl ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static string StatusLineText(bool enabled, LocalApiServer server)
    {
        if (!enabled) return Loc.S("settings.localApi.status.idle");
        if (!server.IsRunning && !string.IsNullOrWhiteSpace(server.LastError))
            return Loc.S("status.failed", server.LastError);
        if (!server.IsRunning) return Loc.S("settings.localApi.status.starting");

        var last4 = server.BearerToken.Length >= 4
            ? server.BearerToken[^4..]
            : server.BearerToken;
        return Loc.S("settings.localApi.status.running", server.ListeningPort, last4);
    }

    private static string FormatTokenDisplay(string token, bool revealed)
    {
        if (string.IsNullOrEmpty(token)) return "<no token yet>";
        if (revealed) return token;
        var masked = new string('•', Math.Max(token.Length - 4, 0));
        return masked + token[^Math.Min(4, token.Length)..];
    }

    private static string BuildMcpSnippet()
    {
        return "{\n  \"mcpServers\": {\n    \"hyperwhisper\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@hyperwhisper/mcp\"]\n    }\n  }\n}";
    }

    private static string BuildCurlSnippet(LocalApiServer server)
    {
        // When the server is up, embed live values for one-step copy/paste.
        // Otherwise fall back to the discovery-file Get-Content form so the
        // snippet keeps working even before the user flips the toggle.
        if (server.IsRunning && server.ListeningPort > 0 && !string.IsNullOrEmpty(server.BearerToken))
        {
            return $"$PORT = {server.ListeningPort}\n" +
                   $"$TOKEN = \"{server.BearerToken}\"\n" +
                   "curl http://127.0.0.1:$PORT/health\n" +
                   "curl -H \"Authorization: Bearer $TOKEN\" http://127.0.0.1:$PORT/models";
        }

        return "$discovery = Get-Content \"$env:LOCALAPPDATA\\HyperWhisper\\local-api.json\" | ConvertFrom-Json\n" +
               "$PORT = $discovery.port\n" +
               "$TOKEN = $discovery.token\n" +
               "curl http://127.0.0.1:$PORT/health\n" +
               "curl -H \"Authorization: Bearer $TOKEN\" http://127.0.0.1:$PORT/models";
    }

    // ---------------------------------------------------------------- helpers

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiSettingsPage: failed to open url '{url}': {ex.Message}");
            ShowActionStatus(Loc.S("settings.localApi.action.openFailed", ex.Message), isError: true);
        }
    }

    private void TrySetClipboardText(string text)
    {
        try
        {
            WpfClipboard.SetText(text);
            ShowActionStatus(Loc.S("settings.localApi.action.copied"), isError: false);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiSettingsPage: clipboard write failed: {ex.Message}");
            ShowActionStatus(Loc.S("settings.localApi.action.copyFailed", ex.Message), isError: true);
        }
    }

    private void ShowActionStatus(string message, bool isError)
    {
        ActionStatusText.Text = message;
        ActionStatusText.Foreground = isError
            ? System.Windows.Media.Brushes.OrangeRed
            : TryFindResource("TextSecondaryBrush") as Brush
              ?? System.Windows.Media.Brushes.Gray;
        ActionStatusText.Visibility = Visibility.Visible;
    }
}
