using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NetSparkleUpdater;
using NetSparkleUpdater.Events;
using NetSparkleUpdater.Interfaces;
using HyperWhisper.Localization;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// UPDATE DOWNLOAD PROGRESS WINDOW
///
/// Pill-shaped overlay showing download progress with animated progress bar.
/// Implements IDownloadProgress for NetSparkle integration.
///
/// Non-activating (WS_EX_NOACTIVATE) so it doesn't steal focus.
/// On completion, the cancel button changes to "Install and Relaunch".
/// </summary>
public partial class UpdateDownloadProgressWindow : Window, IDownloadProgress
{
    // =========================================================================
    // WIN32 API - NON-ACTIVATING WINDOW
    // =========================================================================

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly string _downloadTitle;
    private readonly string _actionButtonTitle;
    private bool _isDownloadComplete;
    private double _progressBarWidth;

    public event DownloadInstallEventHandler? DownloadProcessCompleted;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public UpdateDownloadProgressWindow(string downloadTitle, string actionButtonTitleAfterDownload)
    {
        InitializeComponent();

        _downloadTitle = downloadTitle;
        _actionButtonTitle = actionButtonTitleAfterDownload;

        StatusText.Text = downloadTitle;

        Loaded += (s, e) => MakeNonActivating();

        ProgressBarTrack.SizeChanged += (s, e) =>
        {
            _progressBarWidth = ProgressBarTrack.ActualWidth;
        };

        LoggingService.Debug("UpdateDownloadProgressWindow: Created");
    }

    // =========================================================================
    // IDOWNLOADPROGRESS INTERFACE
    // =========================================================================

    public void OnDownloadProgressChanged(object sender, ItemDownloadProgressEventArgs args)
    {
        Dispatcher.Invoke(() =>
        {
            int percentage = args.ProgressPercentage;

            // Update progress bar
            if (_progressBarWidth > 0)
            {
                ProgressFill.Width = _progressBarWidth * (percentage / 100.0);
            }

            PercentageText.Text = $"{percentage}%";
        });
    }

    public void FinishedDownloadingFile(bool isDownloadedFileValid)
    {
        Dispatcher.Invoke(() =>
        {
            if (isDownloadedFileValid)
            {
                LoggingService.Info("UpdateDownloadProgressWindow: Download complete, showing install button");
                _isDownloadComplete = true;
                StatusText.Text = Loc.S("update.available.installReady");
                PercentageText.Text = "100%";

                if (_progressBarWidth > 0)
                {
                    ProgressFill.Width = _progressBarWidth;
                }

                // Change button to install action
                var textBlock = FindActionButtonText();
                if (textBlock != null)
                {
                    textBlock.Text = _actionButtonTitle;
                }
            }
            else
            {
                LoggingService.Warn("UpdateDownloadProgressWindow: Download invalid");
                StatusText.Text = Loc.S("update.error.download", "");
            }
        });
    }

    public void SetDownloadAndInstallButtonEnabled(bool shouldBeEnabled)
    {
        Dispatcher.Invoke(() =>
        {
            ActionButton.IsEnabled = shouldBeEnabled;
        });
    }

    public bool DisplayErrorMessage(string errorMessage)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = errorMessage;
            LoggingService.Error($"UpdateDownloadProgressWindow: {errorMessage}");
        });
        return true;
    }

    // =========================================================================
    // BUTTON HANDLER
    // =========================================================================

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloadComplete)
        {
            LoggingService.Info("UpdateDownloadProgressWindow: User chose to install");
            DownloadProcessCompleted?.Invoke(this, new DownloadInstallEventArgs(true));
        }
        else
        {
            LoggingService.Info("UpdateDownloadProgressWindow: User cancelled download");
            DownloadProcessCompleted?.Invoke(this, new DownloadInstallEventArgs(false));
            Close();
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void MakeNonActivating()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            LoggingService.Debug("UpdateDownloadProgressWindow: Set WS_EX_NOACTIVATE");
        }
    }

    /// <summary>
    /// Finds the TextBlock inside the action button's template.
    /// </summary>
    private System.Windows.Controls.TextBlock? FindActionButtonText()
    {
        ActionButton.ApplyTemplate();
        return ActionButton.Template.FindName("ActionButtonText", ActionButton) as System.Windows.Controls.TextBlock;
    }

    protected override void OnClosed(EventArgs e)
    {
        LoggingService.Debug("UpdateDownloadProgressWindow: Closed");
        base.OnClosed(e);
    }
}
