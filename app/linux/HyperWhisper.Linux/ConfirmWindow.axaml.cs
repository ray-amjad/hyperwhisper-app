using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace HyperWhisper.Linux;

/// <summary>
/// The Linux stand-in for the native Windows MessageBox. Windows guards every destructive action
/// behind one (delete a mode, delete a model, delete recordings, clear stored audio, deactivate a
/// licence); Linux used to act on the first click. Callers pass the Windows catalog KEY, not the
/// English text, so the two apps cannot drift apart in wording.
/// </summary>
public partial class ConfirmWindow : Window
{
    private bool _result;

    public ConfirmWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Set here, not in the XAML: the localization test matches (Title|Text|Content|...)="…"
        // lexically, so a literal SizeToContent="Height" reads to it as Content="Height".
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>A YesNo prompt. Returns true when the user chose Yes.</summary>
    public static Task<bool> ShowAsync(Window owner, string title, string message)
        => ShowAsync(owner, title, message, notice: false);

    /// <summary>An OK-only notice, the Windows MessageBoxButton.OK + Information shape.</summary>
    public static async Task ShowNoticeAsync(Window owner, string title, string message)
        => await ShowAsync(owner, title, message, notice: true);

    private static async Task<bool> ShowAsync(Window owner, string title, string message, bool notice)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new ConfirmWindow { Title = title };
        if (window.FindControl<TextBlock>("ConfirmMessage") is { } body) body.Text = message;
        if (notice)
        {
            // OK + Information: one button, and it reads OK rather than Yes.
            if (window.FindControl<Button>("ConfirmNoButton") is { } no) no.IsVisible = false;
            if (window.FindControl<Button>("ConfirmYesButton") is { } yes)
                yes.Content = (Avalonia.Application.Current as App)?.Localization["common.ok"] ?? "OK";
        }
        await window.ShowDialog(owner);
        return window._result;
    }

    private void OnYes(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnNo(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc is No and Enter is Yes, the MessageBox defaults.
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter) { e.Handled = true; _result = true; Close(); }
    }
}
