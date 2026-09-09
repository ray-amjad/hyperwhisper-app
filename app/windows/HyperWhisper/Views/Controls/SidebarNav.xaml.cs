using System;
using System.Windows;

namespace HyperWhisper.Views.Controls;

/// <summary>
/// The main window's left navigation rail.
/// </summary>
/// <remarks>
/// Lifted out of MainWindow.xaml for issue #570 so that the rail's own markup can be
/// constructed and laid out on its own. The smoke suite has to PROVE that a translated
/// nav label is not cut off, and MainWindow itself cannot be built headlessly: its
/// DataContext is a MainViewModel, whose constructor opens the audio stack and creates
/// the tray icon.
///
/// This control deliberately sets no DataContext of its own. It inherits MainWindow's,
/// which is what keeps every Navigate command binding below working unchanged.
/// </remarks>
public partial class SidebarNav : System.Windows.Controls.UserControl
{
    /// <summary>
    /// Raised when the Cloud credits call to action at the foot of the rail is clicked.
    /// The rail knows nothing about navigation; MainWindow owns that.
    /// </summary>
    public event EventHandler? CloudCreditsRequested;

    public SidebarNav()
    {
        InitializeComponent();
    }

    private void CloudCreditsSidebar_Click(object sender, RoutedEventArgs e)
        => CloudCreditsRequested?.Invoke(this, EventArgs.Empty);
}
