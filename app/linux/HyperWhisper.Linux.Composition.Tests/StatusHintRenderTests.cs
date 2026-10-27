using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #2599. The real MainWindow's StatusText, the line the user reads: the record hint only with the
// resting line, never while a recording or a live stream is in flight, never after a failure, and back
// as soon as the session ends, even when the stop wrote its own line while still finishing.
static class StatusHintRenderTests
{
    public const string ProbeArgument = "--probe-status-hint";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #2599 status-bar hint probe", "en", "1440x900");

    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var services = new LinuxDesktopServices();
            Directory.CreateDirectory(services.Paths.ConfigDirectory);
            File.WriteAllText(Path.Combine(services.Paths.ConfigDirectory, "settings.json"),
                """{"selectedModeId":"00000000-0000-0000-0000-000000000001","onboarding.skipped":true,"onboarding.completed":true}""");

            var window = new MainWindow(services);
            var failures = new List<string>();
            var exit = 2;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    window.Show();
                    await Initialized(window);
                    await Run(window, (ApplicationShellViewModel)window.DataContext!, failures);
                    exit = failures.Count == 0 ? 0 : 1;
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); exit = 2; }
                finally { done.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return exit;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 2; }
    }

    private static async Task Run(MainWindow window, ApplicationShellViewModel shell, List<string> failures)
    {
        var session = (LinuxInteractionRecordingSession)typeof(MainWindow)
            .GetField("_recordingSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var shortcut = shell.Settings.ToggleShortcutDisplay;
        if (string.IsNullOrEmpty(shortcut)) { failures.Add("the profile has no toggle shortcut to hint"); return; }
        var resting = $"Ready - Press {shortcut} to record";

        shell.ShowReadyStatus();
        await Settle();
        Expect("at rest", window, resting, failures);
        if (shell.History.HotkeyInstruction != resting)
            failures.Add($"the History empty state reads \"{shell.History.HotkeyInstruction}\" at rest, expected \"{resting}\"");

        // The sandbox has no parec, so no real stream starts: turn live capture on the way StartAsync's
        // success does. The session is open (HasOpenSession), so the shortcut would stop it.
        SetLiveCapture(session, true);
        shell.Status.Success("Live transcription recording…");
        await Settle();
        Expect("while a live stream is open", window, "Live transcription recording…", failures);

        // A stop writes its result while it is still finishing; the hint must come back once it is done.
        shell.Status.Success("Live transcription pasted and saved");
        await Settle();
        Expect("with the stop's line written while still open", window, "Live transcription pasted and saved", failures);
        SetLiveCapture(session, false);
        await Settle();
        Expect("once the session ended", window, $"Live transcription pasted and saved - Press {shortcut} to record", failures);

        shell.Status.Failure("provider.credential_rejected", "The provider rejected the supplied credential.");
        await Settle();
        Expect("after a failure", window, "The provider rejected the supplied credential.", failures);

        shell.ShowReadyStatus();
        await Settle();
        Expect("back at rest", window, resting, failures);
    }

    private static void SetLiveCapture(LinuxInteractionRecordingSession session, bool value) =>
        typeof(LinuxInteractionRecordingSession).GetMethod("SetLiveCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [value]);

    private static void Expect(string step, Window window, string expected, List<string> failures)
    {
        var status = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(control => control.Name == "StatusText")
            ?? throw new InvalidOperationException("the window has no StatusText");
        Console.WriteLine($"{step}: \"{status.Text}\"");
        if (status.Text != expected) failures.Add($"{step}: the status bar reads \"{status.Text}\", expected \"{expected}\"");
    }

    private static Task Initialized(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("EnsureInitializedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
