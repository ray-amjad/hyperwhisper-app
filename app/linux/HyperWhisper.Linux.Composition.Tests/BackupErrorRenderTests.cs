using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #2124. Every Backup failure (inspect, preview, import, export) lands in Backup.Status, and
// the Linux page bound none of it: choosing a file the core rejects closed the selection panel with
// no word why. This builds the real MainWindow under the real App, exports a real backup, breaks
// modes[0].id the way the issue does, inspects it the way Choose File… does, and reads the page.
static class BackupErrorRenderTests
{
    public const string ProbeArgument = "--probe-backup-error";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #2124 Backup error probe", "en", "1440x900");

    /// <summary>The child half. Exit 0 only if the page shows the failure and hides it again.</summary>
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
                    await Run(window, (ApplicationShellViewModel)window.DataContext!, services.Paths.ConfigDirectory, failures);
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

    private static async Task Run(MainWindow window, ApplicationShellViewModel shell, string scratch, List<string> failures)
    {
        shell.Navigate("backup");
        await Settle();
        Expect("on opening the page", window, shown: null, failures);

        // A real export from this profile, then the one bad field the issue names.
        var good = Path.Combine(scratch, "good.hwbackup");
        shell.Backup.Path = good;
        await shell.Backup.ExportAsync();
        await Settle();
        Expect("after a good export", window, shown: null, failures);
        var json = JsonNode.Parse(File.ReadAllText(good))!;
        var modes = json["modes"] as JsonArray;
        if (modes is not { Count: > 0 }) { failures.Add("the exported backup holds no modes to break"); return; }
        modes[0]!["id"] = 5;
        var bad = Path.Combine(scratch, "bad.hwbackup");
        File.WriteAllText(bad, json.ToJsonString());

        // What OnChooseBackup does once the picker returns a file.
        shell.Backup.Path = bad;
        await shell.Backup.InspectAsync();
        await Settle();
        Expect("after choosing a backup with modes[0].id 5", window, shown: "modes[0].id", failures);

        shell.Backup.Path = Path.Combine(scratch, "missing.hwbackup");
        await shell.Backup.InspectAsync();
        await Settle();
        Expect("after choosing a file that is gone", window, shown: "Could not read", failures);

        shell.Backup.Path = good;
        await shell.Backup.InspectAsync();
        await Settle();
        Expect("after choosing the good backup again", window, shown: null, failures);
    }

    // shown null: the banner must be hidden. Otherwise it must be visible and its text contain shown.
    private static void Expect(string step, Window window, string? shown, List<string> failures)
    {
        var banner = Named<Border>(window, "BackupErrorBanner");
        var text = Named<TextBlock>(window, "BackupErrorText");
        var visible = banner.IsEffectivelyVisible && banner.Bounds.Height > 0;
        Console.WriteLine($"{step}: banner {(visible ? "shown" : "hidden")}, text \"{text.Text}\"");
        if (shown is null)
        {
            if (visible) failures.Add($"{step}: the error banner is shown with \"{text.Text}\"");
            return;
        }
        if (!visible) failures.Add($"{step}: no error is shown on the page");
        else if (text.Text?.Contains(shown, StringComparison.Ordinal) != true)
            failures.Add($"{step}: the banner reads \"{text.Text}\", expected it to name {shown}");
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"the Backup page has no {name}");

    // The same task the window's Opened handler started; it loads settings and the database.
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
