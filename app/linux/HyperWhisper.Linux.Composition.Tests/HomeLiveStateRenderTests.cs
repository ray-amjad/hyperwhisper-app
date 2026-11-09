using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.LiveStreaming;

// Issue #2038. Home's "State: {0}" caption was bound to the batch workflow's State, which a live stream
// never drives, so it read "State: Idle" while a stream ran and while a failed stream was held until
// Stop. The caption now reads the window's HomeRecordingState, which the live session feeds.
static class HomeLiveStateRenderTests
{
    public const string ProbeArgument = "--probe-home-live-state";

    /// <summary>The state mapping on its own: every combination the live session can be in.</summary>
    public static Task MappingFollowsTheLiveStream()
    {
        static void Expect(string? workflow, bool capture, bool running, bool failed, string? expected)
        {
            var actual = LinuxInteractionRecordingSession.MapHomeRecordingState(workflow, capture, running, failed);
            if (actual != expected)
                throw new InvalidOperationException(
                    $"workflow {workflow ?? "null"}, capture {capture}, running {running}, failed {failed}: '{actual}', expected '{expected}'");
        }

        // No live capture: the workflow's own state, whatever the last stream left behind.
        Expect("Idle", capture: false, running: false, failed: false, "Idle");
        Expect("Idle", capture: false, running: false, failed: true, "Idle");
        Expect("Recording", capture: false, running: false, failed: false, "Recording");
        Expect("Transcribing", capture: false, running: true, failed: false, "Transcribing");
        Expect("Failed", capture: false, running: false, failed: false, "Failed");
        Expect(null, capture: false, running: false, failed: false, null);
        // A live stream that runs (connecting, connected or reconnecting) reads Recording, never Idle.
        Expect("Idle", capture: true, running: true, failed: false, "Recording");
        // Its connection failed (the overlay is red): Failed, though the worker may still be winding down.
        Expect("Idle", capture: true, running: true, failed: true, "Failed");
        // Its worker is gone and the stream is held until Stop (#1246): Failed.
        Expect("Idle", capture: true, running: false, failed: false, "Failed");
        Expect("Idle", capture: true, running: false, failed: true, "Failed");
        return Task.CompletedTask;
    }

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #2038 Home live-state probe", "en", "1440x900");

    /// <summary>The child half: the real MainWindow, its real session, and the Home caption it renders.</summary>
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
                    await Run(window, failures);
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

    private static async Task Run(MainWindow window, List<string> failures)
    {
        var session = (LinuxInteractionRecordingSession)typeof(MainWindow)
            .GetField("_recordingSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        await Settle();
        Expect("at rest", window, "State: Idle", failures);

        // The sandbox has no parec, so no real stream can start. Raise the two things a stream that
        // started and then lost its worker does: live capture turns on (StartAsync's success), and the
        // controller's worker is gone, so IsActive is false. That is the held stream (#1246).
        SetLiveCapture(session, true);
        await Settle();
        Expect("with live capture on and no live worker (held)", window, "State: Failed", failures);

        // The controller's connection error, raised from its worker thread as the real one is.
        await Task.Run(() => RaiseConnection(session, LiveStreamingConnectionState.Error));
        await Settle();
        Expect("after the connection error (overlay red)", window, "State: Failed", failures);

        // Stop and Cancel turn live capture off first: the caption goes back to the workflow's state.
        SetLiveCapture(session, false);
        await Settle();
        Expect("after Stop", window, "State: Idle", failures);
    }

    private static void SetLiveCapture(LinuxInteractionRecordingSession session, bool value) =>
        typeof(LinuxInteractionRecordingSession).GetMethod("SetLiveCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [value]);

    private static void RaiseConnection(LinuxInteractionRecordingSession session, LiveStreamingConnectionState state) =>
        typeof(LinuxInteractionRecordingSession).GetMethod("OnStreamingConnectionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [null, state]);

    private static void Expect(string step, Window window, string expected, List<string> failures)
    {
        var caption = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(control => control.Name == "HomeRecordingStateText")
            ?? throw new InvalidOperationException("Home has no HomeRecordingStateText");
        Console.WriteLine($"{step}: \"{caption.Text}\"");
        if (caption.Text != expected) failures.Add($"{step}: Home reads \"{caption.Text}\", expected \"{expected}\"");
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
