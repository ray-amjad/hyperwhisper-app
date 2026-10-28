using System.Windows;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Interfaces;
using HyperWhisper.Localization;
using HyperWhisper.Views.Windows;

namespace HyperWhisper.Services;

/// <summary>
/// CUSTOM UI FACTORY FOR NETSPARKLE
///
/// Replaces the default NetSparkleUpdater.UI.WPF.UIFactory with themed windows
/// that match HyperWhisper's design language (dark/light themed dialogs, pill overlays).
///
/// All methods dispatch to the UI thread via Application.Current.Dispatcher.
/// </summary>
public class HyperWhisperUIFactory : IUIFactory
{
    // =========================================================================
    // IUIFACTORY PROPERTIES
    // =========================================================================

    public bool HideReleaseNotes { get; set; }
    public bool HideSkipButton { get; set; }
    public bool HideRemindMeLaterButton { get; set; }
    public string? ReleaseNotesHTMLTemplate { get; set; }
    public string? AdditionalReleaseNotesHeaderHTML { get; set; }

    // =========================================================================
    // FACTORY METHODS
    // =========================================================================

    public IUpdateAvailable CreateUpdateAvailableWindow(
        List<AppCastItem> updates,
        ISignatureVerifier? signatureVerifier,
        string currentVersion = "",
        string appName = "the application",
        bool isUpdateAlreadyDownloaded = false)
    {
        return DispatchToUISync(() =>
        {
            var window = new UpdateAvailableWindow(updates, currentVersion, isUpdateAlreadyDownloaded);

            IUpdateAvailable updateAvailable = window;
            if (HideReleaseNotes) updateAvailable.HideReleaseNotes();
            if (HideSkipButton) updateAvailable.HideSkipButton();
            if (HideRemindMeLaterButton) updateAvailable.HideRemindMeLaterButton();

            return window;
        });
    }

    public IDownloadProgress CreateProgressWindow(
        string downloadTitle,
        string actionButtonTitleAfterDownload)
    {
        return DispatchToUISync(() =>
        {
            return new UpdateDownloadProgressWindow(downloadTitle, actionButtonTitleAfterDownload);
        });
    }

    public ICheckingForUpdates ShowCheckingForUpdates()
    {
        return DispatchToUISync(() =>
        {
            var window = new UpdateCheckingWindow();
            window.Show();
            return window;
        });
    }

    // =========================================================================
    // NOTIFICATION METHODS
    // =========================================================================

    public void ShowVersionIsUpToDate()
    {
        DispatchToUI(() => ShowMessageWindow(
            Loc.S("update.upToDate.title"),
            Loc.S("update.upToDate.message", GetCurrentVersion()),
            UpdateMessageWindow.MessageIcon.Success));
    }

    public void ShowVersionIsSkippedByUserRequest()
    {
        DispatchToUI(() => ShowMessageWindow(
            Loc.S("update.skipped.title"),
            Loc.S("update.skipped.message"),
            UpdateMessageWindow.MessageIcon.Info));
    }

    public void ShowCannotDownloadAppcast(string? appcastUrl)
    {
        DispatchToUI(() => ShowMessageWindow(
            Loc.S("common.error"),
            Loc.S("update.error.appcast"),
            UpdateMessageWindow.MessageIcon.Error));
    }

    public void ShowDownloadErrorMessage(string message, string? appcastUrl)
    {
        DispatchToUI(() => ShowMessageWindow(
            Loc.S("common.error"),
            Loc.S("update.error.download", message),
            UpdateMessageWindow.MessageIcon.Error));
    }

    public void ShowUnknownInstallerFormatMessage(string downloadFileName)
    {
        DispatchToUI(() => ShowMessageWindow(
            Loc.S("common.error"),
            Loc.S("update.error.unknownFormat", downloadFileName),
            UpdateMessageWindow.MessageIcon.Error));
    }

    /// <summary>
    /// The one open update message dialog, or null. UI thread only.
    /// </summary>
    private static UpdateMessageWindow? _openMessageWindow;

    /// <summary>
    /// Shows an update message in ONE non-modal dialog (#2594). A manual check
    /// finishes in a fraction of a second, so a second press used to stack a second
    /// dialog that then hid behind the main window. Now an open dialog takes the new
    /// text and comes to the front instead. Owned by the main window while that is
    /// visible, so it cannot fall behind it. Never ShowDialog(): NetSparkle calls
    /// these from its own flows and a modal there can hang (see UpdateService).
    /// </summary>
    internal static UpdateMessageWindow ShowMessageWindow(
        string title,
        string message,
        UpdateMessageWindow.MessageIcon icon)
    {
        if (_openMessageWindow is { } open)
        {
            LoggingService.Debug("HyperWhisperUIFactory: Reusing the open update message dialog");
            open.SetMessage(title, message, icon);
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }
            open.Activate();
            return open;
        }

        var window = new UpdateMessageWindow(title, message, icon);

        var mainWindow = System.Windows.Application.Current?.MainWindow;
        if (mainWindow != null && !ReferenceEquals(mainWindow, window) && mainWindow.IsVisible)
        {
            window.Owner = mainWindow;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMessageWindow, window))
            {
                _openMessageWindow = null;
            }
        };
        _openMessageWindow = window;
        window.Show();
        return window;
    }

    // =========================================================================
    // TOAST SUPPORT
    // =========================================================================

    public bool CanShowToastMessages() => true;

    public void ShowToast(Action clickHandler)
    {
        DispatchToUI(() =>
        {
            var toast = new UpdateToastWindow(clickHandler);
            toast.Show();
        });
    }

    // =========================================================================
    // LIFECYCLE
    // =========================================================================

    public void Shutdown()
    {
        LoggingService.Debug("HyperWhisperUIFactory: Shutdown called");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static string GetCurrentVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "unknown";
    }

    /// <summary>
    /// Synchronously dispatches to the UI thread and returns a result.
    /// Used for factory methods that must return an object.
    /// </summary>
    private static T DispatchToUISync<T>(Func<T> func)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            throw new InvalidOperationException("Cannot create UI - dispatcher unavailable");
        }

        if (dispatcher.CheckAccess())
        {
            return func();
        }

        return dispatcher.Invoke(func);
    }

    /// <summary>
    /// Asynchronously dispatches to the UI thread.
    /// Used for fire-and-forget notification methods.
    /// </summary>
    private static void DispatchToUI(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            LoggingService.Debug("HyperWhisperUIFactory: Dispatcher unavailable, skipping UI");
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
