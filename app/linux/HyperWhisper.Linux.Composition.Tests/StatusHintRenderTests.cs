using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #2599. The real MainWindow's StatusText, the line the user reads: no record hint while anything
// is in flight (an open stream, a shell Busy line) or on an error line, and the hint back as soon as the
// session ends, even when the stop wrote its own line while still finishing. History's empty-state
// hotkey line is out of the issue's scope and keeps its hint throughout.
//
// The probe never calls EnsureInitializedAsync itself and never trusts a fixed delay: it lets the
// window's own Opened run its start-up to the end, then waits for each value it reads to stay the same
// for a second. Racing OnOpened is what made the #2188 probe flake on CI.
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
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    window.Show();
                    await StartUpFinished(window);
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
        var hint = $"Press {shortcut} to record";
        var resting = $"Ready - {hint}";

        shell.ShowReadyStatus();
        await Expect("at rest", window, shell, resting, resting, failures);

        // The sandbox has no parec, so no real stream starts: turn live capture on the way StartAsync's
        // success does. The session is open (HasOpenSession), so the shortcut would stop it.
        SetLiveCapture(session, true);
        shell.Status.Success("Live transcription recording…");
        await Expect("while a live stream is open", window, shell, "Live transcription recording…",
            $"Live transcription recording… - {hint}", failures);

        // A stop writes its result while it is still finishing; the hint must come back once it is done.
        shell.Status.Success("Live transcription pasted and saved");
        await Expect("with the stop's line written while still open", window, shell, "Live transcription pasted and saved",
            $"Live transcription pasted and saved - {hint}", failures);
        SetLiveCapture(session, false);
        await Expect("once the session ended", window, shell, $"Live transcription pasted and saved - {hint}",
            $"Live transcription pasted and saved - {hint}", failures);

        shell.Status.Busy("Importing universal backup…");
        await Expect("on a shell Busy line", window, shell, "Importing universal backup…",
            $"Importing universal backup… - {hint}", failures);

        shell.Status.Failure("provider.credential_rejected", "The provider rejected the supplied credential.");
        await Expect("after a failure", window, shell, "The provider rejected the supplied credential.",
            $"The provider rejected the supplied credential. - {hint}", failures);

        shell.ShowReadyStatus();
        await Expect("back at rest", window, shell, resting, resting, failures);
    }

    private static void SetLiveCapture(LinuxInteractionRecordingSession session, bool value) =>
        typeof(LinuxInteractionRecordingSession).GetMethod("SetLiveCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [value]);

    // statusBar is what StatusText must read; history is History's empty-state line, which keeps the
    // hint in every state, exactly as on main.
    private static async Task Expect(string step, Window window, ApplicationShellViewModel shell,
        string statusBar, string history, List<string> failures)
    {
        var status = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(control => control.Name == "StatusText")
            ?? throw new InvalidOperationException("the window has no StatusText");
        var (text, instruction) = await Quiet(() => (status.Text, shell.History.HotkeyInstruction));
        Console.WriteLine($"{step}: status bar \"{text}\", History \"{instruction}\"");
        if (text != statusBar) failures.Add($"{step}: the status bar reads \"{text}\", expected \"{statusBar}\"");
        if (instruction != history) failures.Add($"{step}: History's hotkey line reads \"{instruction}\", expected \"{history}\"");
    }

    /// <summary>The window's own Opened has run its start-up chain to the end (LinuxWindowStartupGate:
    /// attempted and no longer running), so nothing it does can land after the probe's writes.</summary>
    private static async Task StartUpFinished(MainWindow window)
    {
        var gate = typeof(MainWindow).GetField("_startup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        bool Field(string name) => (bool)gate.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!;
        var clock = Stopwatch.StartNew();
        while (!(Field("_attempted") && !Field("_running")))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("the window's start-up never finished");
            await Task.Delay(50);
        }
    }

    /// <summary>Polls until the value has stayed the same for a second (at most 20 s), then returns it.</summary>
    private static async Task<T> Quiet<T>(Func<T> read)
    {
        var clock = Stopwatch.StartNew();
        var value = read();
        var stableSince = clock.Elapsed;
        while (clock.Elapsed - stableSince < TimeSpan.FromSeconds(1))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) return value;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(50);
            var next = read();
            if (!EqualityComparer<T>.Default.Equals(next, value)) { value = next; stableSince = clock.Elapsed; }
        }
        return value;
    }
}
