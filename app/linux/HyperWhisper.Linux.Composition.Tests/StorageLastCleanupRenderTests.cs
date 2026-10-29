using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Data.Entities;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;
using Microsoft.EntityFrameworkCore;

// Issue #1371. Settings -> Storage is a page template, built only while it is shown and rebuilt on
// every visit, and the window wrote the "Last cleanup" line straight into the page's TextBlock. The
// start-up cleanup runs on Home, so it found no TextBlock and the line stayed blank until Delete
// Now; leaving Settings and coming back built a fresh, blank page. This builds the real MainWindow
// under the real App on a profile whose only transcript is older than its 1-day auto-delete, lets
// the start-up cleanup remove it, then reads the line the page shows on a first visit and a revisit.
static class StorageLastCleanupRenderTests
{
    public const string ProbeArgument = "--probe-storage-last-cleanup";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #1371 storage last-cleanup probe", "en", "1440x900");

    /// <summary>The child half. Exit 0 only if both visits show the start-up cleanup's line.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var services = new LinuxDesktopServices();
            // A profile that has finished onboarding, so the window opens on Home with no wizard,
            // with Automatic Cleanup on at one day.
            Directory.CreateDirectory(services.Paths.ConfigDirectory);
            var settingsPath = Path.Combine(services.Paths.ConfigDirectory, "settings.json");
            File.WriteAllText(settingsPath,
                """{"selectedModeId":"00000000-0000-0000-0000-000000000001","onboarding.skipped":true,"onboarding.completed":true,"autoDeleteEnabled":true,"autoDeleteDaysOld":1}""");
            // Settings refuses a file that is not owner-only, and a refused file leaves cleanup off.
            File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var database = new ApplicationDb(services.Paths);
            database.InitializeAsync().GetAwaiter().GetResult();
            new HistoryRepository(database, services.Paths).AddAsync(new Transcript
            {
                Text = "ten days old",
                Status = TranscriptStatus.Completed,
                Date = DateTime.UtcNow.AddDays(-10),
            }).GetAwaiter().GetResult();

            var window = new MainWindow(services);
            var failures = new List<string>();
            var exit = 2;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    window.Show();
                    await Initialized(window);
                    var shell = (ApplicationShellViewModel)window.DataContext!;
                    await Run(window, shell, database, failures);
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

    private static async Task Run(MainWindow window, ApplicationShellViewModel shell, ApplicationDb database, List<string> failures)
    {
        // The start-up cleanup runs later in the Opened handler, on Home. Wait until it has removed
        // the row, then for the status update it posts after the inventory read.
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (await Remaining(database) > 0)
        {
            if (DateTime.UtcNow > deadline) { failures.Add("the start-up cleanup never removed the 10-day-old transcript"); return; }
            await Task.Delay(200);
        }
        await Settle();
        Console.WriteLine("start-up cleanup removed the 10-day-old transcript");

        shell.Navigate("storage");
        await Settle();
        Expect("Storage, first visit after the start-up cleanup", Line(window), failures);

        shell.Navigate("home");
        await Settle();
        shell.Navigate("storage");
        await Settle();
        Expect("Storage, after leaving Settings and coming back", Line(window), failures);
    }

    private static async Task<int> Remaining(ApplicationDb database)
    {
        await using var context = database.CreateContext();
        return await context.Transcripts.CountAsync();
    }

    private static void Expect(string label, string? actual, List<string> failures)
    {
        Console.WriteLine($"{label}: \"{actual}\"");
        // The line names the newest sweep, as Windows' does; the settings saves right after launch
        // each run one more, so the count can already be 0 here. What must never show is no line.
        if (actual is null || !actual.StartsWith("Last cleanup:", StringComparison.Ordinal))
            failures.Add($"{label} reads \"{actual}\", expected a \"Last cleanup: …\" line");
    }

    private static string? Line(Window window) =>
        (window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(control => control.Name == "StoragePathText" && control.IsEffectivelyVisible)
         ?? throw new InvalidOperationException("Storage shows no StoragePathText")).Text;

    // The same task the window's Opened handler started; it loads settings and arms the auto-save.
    private static Task Initialized(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("EnsureInitializedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(600);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
