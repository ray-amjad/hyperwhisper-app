using System.Windows;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// UPDATE MESSAGE WINDOW
///
/// Simple themed message dialog used for update status messages:
/// - Version is up to date (success icon)
/// - Version skipped by user (info icon)
/// - Cannot download appcast (error icon)
/// - Download error (error icon)
/// - Unknown installer format (error icon)
/// </summary>
public partial class UpdateMessageWindow : Window
{
    public enum MessageIcon
    {
        Success,
        Error,
        Info
    }

    public UpdateMessageWindow(string title, string message, MessageIcon icon)
    {
        InitializeComponent();
        SetMessage(title, message, icon);
    }

    /// <summary>
    /// Sets the title, message and icon. HyperWhisperUIFactory calls this again on an
    /// open dialog instead of stacking a second one (#2594).
    /// </summary>
    public void SetMessage(string title, string message, MessageIcon icon)
    {
        TitleText.Text = title;
        MessageText.Text = message;

        // Show appropriate icon
        SuccessIcon.Visibility = icon == MessageIcon.Success ? Visibility.Visible : Visibility.Collapsed;
        ErrorIcon.Visibility = icon == MessageIcon.Error ? Visibility.Visible : Visibility.Collapsed;
        InfoIcon.Visibility = icon == MessageIcon.Info ? Visibility.Visible : Visibility.Collapsed;

        LoggingService.Debug($"UpdateMessageWindow: Showing '{title}' - {icon}");
    }

    /// <summary>
    /// Hands focus back to the owner before closing. Windows may otherwise give it
    /// to another app when an owned window closes, and the main window drops behind
    /// it (#2594). Only when this dialog has focus, and never to a minimised owner.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && IsActive && Owner is { IsVisible: true } owner
            && owner.WindowState != WindowState.Minimized)
        {
            owner.Activate();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
