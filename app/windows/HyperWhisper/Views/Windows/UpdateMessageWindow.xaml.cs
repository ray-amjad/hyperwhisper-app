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

        TitleText.Text = title;
        MessageText.Text = message;

        // Show appropriate icon
        switch (icon)
        {
            case MessageIcon.Success:
                SuccessIcon.Visibility = Visibility.Visible;
                break;
            case MessageIcon.Error:
                ErrorIcon.Visibility = Visibility.Visible;
                break;
            case MessageIcon.Info:
                InfoIcon.Visibility = Visibility.Visible;
                break;
        }

        LoggingService.Debug($"UpdateMessageWindow: Showing '{title}' - {icon}");
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
