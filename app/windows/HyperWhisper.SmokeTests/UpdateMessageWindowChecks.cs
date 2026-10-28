// UPDATE MESSAGE WINDOW: ONE DIALOG, OWNED BY THE MAIN WINDOW (issue #2594)
//
// A manual Check for Updates finishes in about 0.2 s, so the button is back almost
// at once. Each press used to open another non-modal "You're Up to Date" window
// with no Owner, and clicking the main window buried the extra one behind it.
// These checks drive the real HyperWhisperUIFactory on this STA thread: a repeat
// press reuses the open dialog, a closed dialog is not reused, and the dialog is
// owned by a visible main window only.

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
            }
        });

        run("update dialog: owned by a visible main window, unowned when it is hidden (#2594)", () =>
        {
            var app = EnsureApplication();
            var previousMain = app.MainWindow;
            var host = new Window { Width = 200, Height = 120, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                host.Show();
                app.MainWindow = host;

                var owned = HyperWhisperUIFactory.ShowMessageWindow("t", "m", UpdateMessageWindow.MessageIcon.Success);
                Assert(ReferenceEquals(owned.Owner, host), "the dialog is not owned by the visible main window");
                owned.Close();

                host.Hide();
                var unowned = HyperWhisperUIFactory.ShowMessageWindow("t", "m", UpdateMessageWindow.MessageIcon.Info);
                Assert(unowned.Owner == null, "the dialog was owned by a main window hidden in the tray");
                Assert(unowned.IsVisible, "the unowned dialog is not visible");
            }
            finally
            {
                CloseDialogs(app);
                app.MainWindow = previousMain;
                host.Close();
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
