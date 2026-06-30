using System.Windows;
using NetSparkleUpdater.Interfaces;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// UPDATE CHECKING WINDOW
///
/// Small pill-shaped overlay with spinning indicator shown while checking for updates.
/// Implements ICheckingForUpdates for NetSparkle integration.
/// </summary>
public partial class UpdateCheckingWindow : Window, ICheckingForUpdates
{
    public event EventHandler? UpdatesUIClosing;

    public UpdateCheckingWindow()
    {
        InitializeComponent();
        LoggingService.Debug("UpdateCheckingWindow: Created");
    }

    protected override void OnClosed(EventArgs e)
    {
        LoggingService.Debug("UpdateCheckingWindow: Closed");
        UpdatesUIClosing?.Invoke(this, EventArgs.Empty);
        base.OnClosed(e);
    }
}
