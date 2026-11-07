// UPDATE MESSAGE WINDOW: ONE DIALOG, OWNED BY THE MAIN WINDOW (issue #2594)
//
// A manual Check for Updates finishes in about 0.2 s, so the button is back almost
// at once. Each press used to open another non-modal "You're Up to Date" window
// with no Owner, and clicking the main window buried the extra one behind it.
// These checks drive the real HyperWhisperUIFactory on this STA thread: a repeat
// press reuses the open dialog, a closed dialog is not reused, and the dialog is
// owned by the main window only while it is on screen.

using System.Windows;
using HyperWhisper.Services;
using HyperWhisper.Views.Windows;

namespace HyperWhisper.SmokeTests;

internal static class UpdateMessageWindowChecks
{
    public static void RunAll(Action<string, Action> run)
    {
        run("update dialog: two Check for Updates presses leave one up-to-date window (#2594)", () =>
        {
            var app = EnsureApplication();
            var shutdownMode = app.ShutdownMode;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // WPF makes the first window MainWindow when none is set: restore it after.
            var previousMain = app.MainWindow;
            var factory = new HyperWhisperUIFactory();
            try
            {
                factory.ShowVersionIsUpToDate();
                var first = OpenDialogs(app).Single();
                factory.ShowVersionIsUpToDate();

                var open = OpenDialogs(app);
                Assert(open.Length == 1, $"two presses left {open.Length} update dialogs open");
                Assert(ReferenceEquals(open[0], first), "the second press replaced the dialog instead of reusing it");
                Assert(first.IsVisible, "the reused dialog is not visible");

                // A different message lands in the same dialog, with its own text and icon.
                factory.ShowCannotDownloadAppcast(null);
                open = OpenDialogs(app);
                Assert(open.Length == 1, $"an error after up-to-date left {open.Length} dialogs open");
                Assert(first.MessageText.Text == HyperWhisper.Localization.Loc.S("update.error.appcast"),
                    $"the open dialog kept its old text ({first.MessageText.Text})");
                Assert(first.ErrorIcon.Visibility == Visibility.Visible
                       && first.SuccessIcon.Visibility == Visibility.Collapsed,
                    "the open dialog did not swap to the error icon");

                // Once closed, the next press opens a fresh dialog.
                first.Close();
                Assert(OpenDialogs(app).Length == 0, "closing the dialog left one open");
                factory.ShowVersionIsUpToDate();
                open = OpenDialogs(app);
                Assert(open.Length == 1 && !ReferenceEquals(open[0], first),
                    "a press after OK did not open a new dialog");
            }
            finally
            {
                CloseDialogs(app);
                app.MainWindow = previousMain;
                app.ShutdownMode = shutdownMode;
            }
        });

        run("update dialog: owned by an on-screen main window, never by a hidden or minimised one (#2594)", () =>
        {
            var app = EnsureApplication();
            var shutdownMode = app.ShutdownMode;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var previousMain = app.MainWindow;
            var host = new Window { Width = 200, Height = 120, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                host.Show();
                app.MainWindow = host;

                var owned = HyperWhisperUIFactory.ShowMessageWindow("t", "m", UpdateMessageWindow.MessageIcon.Success);
                Assert(ReferenceEquals(owned.Owner, host), "the dialog is not owned by the visible main window");
                var again = HyperWhisperUIFactory.ShowMessageWindow("t2", "m2", UpdateMessageWindow.MessageIcon.Success);
                Assert(ReferenceEquals(again, owned), "a repeat with the same owner did not reuse the dialog");

                // Minimised: an owned dialog would be hidden with it, so the open one is
                // replaced by an unowned one, still only one dialog.
                host.WindowState = WindowState.Minimized;
                var whileMinimised = HyperWhisperUIFactory.ShowMessageWindow("t", "m", UpdateMessageWindow.MessageIcon.Info);
                Assert(whileMinimised.Owner == null, "the dialog was owned by a minimised main window");
                Assert(!ReferenceEquals(whileMinimised, owned), "the dialog hidden under the minimised owner was reused");
                Assert(OpenDialogs(app).Length == 1, $"{OpenDialogs(app).Length} dialogs open after the owner change");

                // Hidden in the tray: no owner either, and the unowned dialog is reused.
                host.WindowState = WindowState.Normal;
                host.Hide();
                var whileHidden = HyperWhisperUIFactory.ShowMessageWindow("t", "m", UpdateMessageWindow.MessageIcon.Info);
                Assert(ReferenceEquals(whileHidden, whileMinimised), "an unowned dialog was not reused while unowned");
                Assert(whileHidden.Owner == null && whileHidden.IsVisible, "the tray-time dialog is owned or not visible");

                // Main window back on screen: the unowned dialog could fall behind it, so
                // the next message comes in a dialog it owns.
                host.Show();
                var afterShow = HyperWhisperUIFactory.ShowMessageWindow("t", "m", UpdateMessageWindow.MessageIcon.Success);
                Assert(ReferenceEquals(afterShow.Owner, host), "the dialog stayed unowned after the main window came back");
                Assert(OpenDialogs(app).Length == 1, $"{OpenDialogs(app).Length} dialogs open after the main window came back");
            }
            finally
            {
                CloseDialogs(app);
                app.MainWindow = previousMain;
                host.Close();
                app.ShutdownMode = shutdownMode;
            }
        });
    }

    private static UpdateMessageWindow[] OpenDialogs(Application app) =>
        app.Windows.OfType<UpdateMessageWindow>().ToArray();

    private static void CloseDialogs(Application app)
    {
        foreach (var dialog in OpenDialogs(app))
        {
            dialog.Close();
        }
    }

    // Same as Program.EnsureSmokeApplication: one Application per AppDomain, and an
    // earlier case may already own it. App.xaml's resources give the dialog its styles.
    private static Application EnsureApplication()
    {
        if (Application.Current is { } existing)
            return existing;

        var application = new App();
        application.InitializeComponent();
        return application;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
