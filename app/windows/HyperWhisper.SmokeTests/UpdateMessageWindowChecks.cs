// UPDATE MESSAGE WINDOW: ONE DIALOG, OWNED BY THE MAIN WINDOW (issue #2594)
//
// A manual Check for Updates finishes in about 0.2 s, so the button is back almost
// at once. Each press used to open another non-modal "You're Up to Date" window
// with no Owner, and clicking the main window buried the extra one behind it.
// These checks drive the real HyperWhisperUIFactory on this STA thread: a repeat
// press reuses the open dialog, a closed dialog is not reused, and the dialog is
// owned by the main window only while it is on screen.

using System.IO;
using System.Windows;
using HyperWhisper.Services;
using HyperWhisper.Views.Windows;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;

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

        // UNSIGNED INSTALLER REFUSED (issue #2903). With the production key set,
        // NetSparkle's OnlyVerifySoftwareDownloads mode reports a missing signature
        // as Unchecked, not Invalid; the launch gate must refuse it. None of these
        // cases launch a process or touch the updater's launch state.
        run("updater: launch gate refuses Unchecked with a public key, allows it only without one (#2903)", () =>
        {
            Assert(!UpdateService.IsInstallerLaunchAllowed(ValidationResult.Unchecked, hasPublicKey: true),
                "Unchecked with a public key was allowed to launch");
            Assert(UpdateService.IsInstallerLaunchAllowed(ValidationResult.Valid, hasPublicKey: true),
                "Valid with a public key was refused");
            Assert(!UpdateService.IsInstallerLaunchAllowed(ValidationResult.Invalid, hasPublicKey: true),
                "Invalid with a public key was allowed to launch");
            Assert(UpdateService.IsInstallerLaunchAllowed(ValidationResult.Unchecked, hasPublicKey: false),
                "Unchecked without a public key (development) was refused");
            Assert(!UpdateService.IsInstallerLaunchAllowed(ValidationResult.Invalid, hasPublicKey: false),
                "Invalid without a public key was allowed to launch");
            Assert(!UpdateService.IsInstallerLaunchAllowed((ValidationResult)999, hasPublicKey: true),
                "an unknown ValidationResult was allowed to launch");
        });

        run("updater: real production verifier answers Unchecked for a blank signature, and the gate refuses it (#2903)", () =>
        {
            Assert(UpdateService.HasPublicKey, "this build has no Ed25519 public key; the production path is not under test");
            var verifier = UpdateService.CreateSignatureVerifier();
            Assert(verifier.SecurityMode == SecurityMode.OnlyVerifySoftwareDownloads,
                $"production verifier runs in {verifier.SecurityMode}, expected OnlyVerifySoftwareDownloads");

            var installer = Path.Combine(Path.GetTempPath(), $"hw-smoke-unsigned-{Guid.NewGuid():N}.exe");
            File.WriteAllBytes(installer, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03 });
            try
            {
                foreach (var signature in new[] { "", "   " })
                {
                    var result = verifier.VerifySignatureOfFile(signature, installer);
                    Assert(result == ValidationResult.Unchecked,
                        $"blank signature '{signature}' verified as {result}, expected Unchecked (NetSparkle behaviour changed?)");
                    Assert(!UpdateService.IsInstallerLaunchAllowed(result, UpdateService.HasPublicKey),
                        $"the gate allowed a {result} installer with the production key");
                }
            }
            finally
            {
                try { File.Delete(installer); } catch (IOException) { }
            }
        });

        run("updater: an appcast item with no signature is refused before launch when a key is set (#2903)", () =>
        {
            Assert(!UpdateService.IsDownloadSignaturePresentOrNotRequired(null, hasPublicKey: true),
                "a null DownloadSignature passed the early gate with a public key");
            Assert(!UpdateService.IsDownloadSignaturePresentOrNotRequired("", hasPublicKey: true),
                "an empty DownloadSignature passed the early gate with a public key");
            Assert(!UpdateService.IsDownloadSignaturePresentOrNotRequired("  \t", hasPublicKey: true),
                "a whitespace DownloadSignature passed the early gate with a public key");
            Assert(UpdateService.IsDownloadSignaturePresentOrNotRequired("c2lnbmF0dXJl", hasPublicKey: true),
                "a present DownloadSignature was refused by the early gate");
            Assert(UpdateService.IsDownloadSignaturePresentOrNotRequired(null, hasPublicKey: false),
                "a missing DownloadSignature was refused without a public key (development)");

            // A real appcast item with sparkle:edSignature removed has a null signature.
            var item = new AppCastItem { Version = "99.0.0" };
            Assert(!UpdateService.IsDownloadSignaturePresentOrNotRequired(item.DownloadSignature, UpdateService.HasPublicKey),
                "an unsigned AppCastItem passed the early gate in this (production-key) build");
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
