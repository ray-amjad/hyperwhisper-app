using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #2199. Settings -> Storage's "delete recordings older than N days" box was a TwoWay binding,
// so each keystroke wrote AutoDeleteDaysOld, the 500 ms auto-save saved it, and the save raised
// StorageSettingsChanged, which runs a cleanup: a pause after the "1" of "14" deleted everything
// older than one day. This builds the real MainWindow under the real App, types into the real box
// a key at a time with a pause past the auto-save, and reads the view model, every storage-change
// event and settings.json before and after the box loses focus.
static class AutoDeleteDaysCommitRenderTests
{
    public const string ProbeArgument = "--probe-auto-delete-days-commit";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #2199 auto-delete days commit probe", "en", "1440x900");

    /// <summary>The child half. Exit 0 only if nothing is applied before the box loses focus.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var services = new LinuxDesktopServices();
            var settingsPath = Path.Combine(services.Paths.ConfigDirectory, "settings.json");
            // A profile that has finished onboarding, so the window opens on Home with no wizard.
            Directory.CreateDirectory(services.Paths.ConfigDirectory);
            File.WriteAllText(settingsPath, """{"selectedModeId":"00000000-0000-0000-0000-000000000001","onboarding.skipped":true,"onboarding.completed":true}""");

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
                    var shell = (ApplicationShellViewModel)window.DataContext!;
                    await Run(window, shell, settingsPath, failures);
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

    private static async Task Run(MainWindow window, ApplicationShellViewModel shell, string settingsPath, List<string> failures)
    {
        // Every save raises StorageSettingsChanged, and the window answers it with a cleanup at the
        // days stored at that moment; record that figure for each one.
        var cleanups = new List<int>();
        shell.Settings.StorageSettingsChanged += (_, _) => cleanups.Add(shell.Settings.AutoDeleteDaysOld);

        // Turn Automatic Cleanup on, at its default 30 days, and let that save land first.
        shell.Navigate("storage");
        shell.Settings.AutoDeleteEnabled = true;
        await Settle();
        Console.WriteLine($"cleanup on: enabled {shell.Settings.AutoDeleteEnabled}, days {shell.Settings.AutoDeleteDaysOld}");
        cleanups.Clear();
        var box = Named<TextBox>(window, "SettingsAutoDeleteDays");
        Expect("the box on opening", box.Text, "30", failures);

        box.Focus();
        box.SelectAll();
        await Type(box, "1");
        Report("after typing 1 and pausing", "30", shell, settingsPath, cleanups, failures);
        Expect("the box after typing 1", box.Text, "1", failures);

        await Type(box, "4");
        Report("after typing 4 and pausing", "30", shell, settingsPath, cleanups, failures);
        Expect("the box after typing 4", box.Text, "14", failures);

        // A letter never reaches the box (Windows' NumericOnly_PreviewTextInput). The filter has to
        // run on the tunnel: the box's own class handler inserts the text before a bubbling one.
        await Type(box, "a");
        Expect("the box after typing a", box.Text, "14", failures);

        // Focus moves to the next control, as a click or Tab does: the edit commits now.
        Named<CheckBox>(window, "SettingsAutoDeleteEnabled").Focus();
        await Settle();
        Report("after the box lost focus", "14", shell, settingsPath, cleanups, failures);
        if (!cleanups.Contains(14)) failures.Add("losing focus saved no storage change at 14 days");

        // The commit still clamps and rewrites the box, as Windows' DaysOld_LostFocus does.
        box.Focus();
        box.SelectAll();
        await Type(box, "999");
        Named<CheckBox>(window, "SettingsAutoDeleteEnabled").Focus();
        await Settle();
        Report("after committing 999", "365", shell, settingsPath, cleanups, failures);
        Expect("the box after committing 999", box.Text, "365", failures);

        // The write-back must leave the OneWay binding in place: a value set elsewhere (a backup
        // import, a reset) still has to reach the box.
        shell.Settings.AutoDeleteDaysOld = 60;
        await Settle();
        Expect("the box after the view model changed to 60", box.Text, "60", failures);

        // Quitting with an edit still focused commits it and flushes the save at once (OnClosing).
        box.Focus();
        box.SelectAll();
        foreach (var character in "7")
            box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = character.ToString() });
        typeof(MainWindow).GetMethod("CommitPendingSettingsEdits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Report("after quitting with 7 still focused", "7", shell, settingsPath, cleanups, failures);
    }

    // What a key press does once the IME has turned it into text: a TextInput event on the focused
    // box. It runs the box's own digit filter and inserts over the selection exactly as typing does.
    // The pause is past the 500 ms auto-save, the gap that let a half-typed number reach a cleanup.
    private static async Task Type(TextBox box, string text)
    {
        foreach (var character in text)
            box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = character.ToString() });
        await Settle();
    }

    private static void Report(string step, string expected, ApplicationShellViewModel shell, string settingsPath, List<int> cleanups, List<string> failures)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var savedDays = saved.RootElement.TryGetProperty("autoDeleteDaysOld", out var value) ? value.ToString() : "<absent>";
        var viewModel = shell.Settings.AutoDeleteDaysOld.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"{step}: view model {viewModel}, settings.json {savedDays}, cleanups at [{string.Join(", ", cleanups)}]");
        if (viewModel != expected) failures.Add($"{step}: the view model holds {viewModel} days, expected {expected}");
        if (savedDays != expected) failures.Add($"{step}: settings.json holds {savedDays} days, expected {expected}");
        if (cleanups.Any(days => days is 1 or 4)) failures.Add($"{step}: a cleanup ran at a half-typed figure [{string.Join(", ", cleanups)}]");
    }

    private static void Expect(string label, string? actual, string expected, List<string> failures)
    {
        Console.WriteLine($"{label}: \"{actual}\"");
        if (actual != expected) failures.Add($"{label} reads \"{actual}\", expected \"{expected}\"");
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible)
        ?? throw new InvalidOperationException($"Storage shows no {name}");

    // The same task the window's Opened handler started; it loads settings and arms the auto-save.
    private static Task Initialized(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("EnsureInitializedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
