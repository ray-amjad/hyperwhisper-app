using System.Collections.Specialized;
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

// Issue #2099. The History page template is rebuilt on every visit and its period combo selects
// "All" while it is built, which re-ran a full-table query on the UI thread per click. This builds
// the real MainWindow under the real App, goes Home -> History -> Home -> History, and counts the
// list reloads: every load (a success or a failure) replaces History.Items, which raises a Reset.
static class HistoryRevisitRenderTests
{
    public const string ProbeArgument = "--probe-history-revisit";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #2099 History revisit probe", "en", "1440x900");

    /// <summary>The child half. Exit 0 only if an unchanged revisit loads nothing and a real change still loads.</summary>
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
                    await Run(window, (ApplicationShellViewModel)window.DataContext!, services, failures);
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

    private static async Task Run(MainWindow window, ApplicationShellViewModel shell, LinuxDesktopServices services, List<string> failures)
    {
        // A row written the way the workflow writes one: through its own repository, with no
        // History reload. The first visit has to show it.
        var writer = new HistoryRepository(new ApplicationDb(services.Paths), services.Paths);
        await writer.AddAsync(new Transcript { Text = "seeded", Status = TranscriptStatus.Completed, Date = DateTime.UtcNow });
        // The rest of the Opened handler (recovery, storage maintenance) finishes first.
        await Settle(1500);

        var loads = 0;
        NotifyCollectionChangedEventHandler count = (_, e) =>
        { if (e.Action == NotifyCollectionChangedAction.Reset) loads++; };
        shell.History.Items.CollectionChanged += count;
        try
        {
            await Expect("first History visit after a write", expectedLoads: 1, () => Visit(shell));
            await Expect("Home -> History, nothing changed", expectedLoads: 0, () => Visit(shell));
            await Expect("Home -> History again, nothing changed", expectedLoads: 0, () => Visit(shell));
            await Expect("choosing Today", expectedLoads: 1, () =>
            {
                Named<ComboBox>(window, "HistoryPeriod").SelectedIndex = 1;
                return Task.CompletedTask;
            });
            await Expect("Home -> History from Today (the combo shows All again)", expectedLoads: 1, () => Visit(shell));
            await Expect("Home -> History after that", expectedLoads: 0, () => Visit(shell));
            await Expect("Home -> History after MarkStale (storage cleanup)", expectedLoads: 1, () =>
            {
                shell.History.MarkStale();
                return Visit(shell);
            });
        }
        finally { shell.History.Items.CollectionChanged -= count; }

        async Task Expect(string step, int expectedLoads, Func<Task> act)
        {
            var before = loads;
            await act();
            // Past the search box's 250 ms debounce, so its query (if any) is counted here.
            await Settle(800);
            var ran = loads - before;
            var shown = window.GetVisualDescendants().OfType<ComboBox>().Any(control => control.Name == "HistoryPeriod");
            Console.WriteLine($"{step}: {ran} History loads, {shell.History.Items.Count} rows, status \"{shell.History.Status.Message}\"");
            if (ran != expectedLoads) failures.Add($"{step}: {ran} History loads, expected {expectedLoads}");
            if (!shown) failures.Add($"{step}: the History page is not on screen");
            if (shell.History.Items.Count != 1 || shell.History.Selected?.Text != "seeded")
                failures.Add($"{step}: History shows {shell.History.Items.Count} rows, selected \"{shell.History.Selected?.Text}\"");
        }
    }

    private static async Task Visit(ApplicationShellViewModel shell)
    {
        shell.Navigate("home");
        await Settle(100);
        shell.Navigate("history");
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"the window has no {name}");

    // The same task the window's Opened handler started; it loads settings and the database.
    private static Task Initialized(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("EnsureInitializedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private static async Task Settle(int milliseconds)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(milliseconds);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
