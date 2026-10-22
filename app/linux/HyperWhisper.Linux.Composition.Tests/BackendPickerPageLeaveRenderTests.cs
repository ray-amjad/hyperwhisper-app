using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #1967. The Model Library's Whisper and Local LLM backend pickers bind past the page to the
// shell's Settings. Leaving the page took the ComboBoxes out of the tree, and they wrote a null
// selection back through the two-way binding; the setters normalised that null to "auto" and "cpu"
// and the auto-save wrote it to settings.json. Only a real window shows it: this builds the real
// MainWindow under the real App on the X server xvfb-run started, picks CUDA 12 and Vulkan the way
// a user does, clicks Home, reopens Model Library, and reads the view model, the combos and the file.
static class BackendPickerPageLeaveRenderTests
{
    public const string ProbeArgument = "--probe-backend-picker-page-leave";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #1967 backend picker page-leave probe", "en", "1440x900");

    /// <summary>The child half. Exit 0 only if both choices survive leaving and reopening the page.</summary>
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
        shell.Navigate("models");
        await Settle();
        var whisper = Combo(window, "SettingsLocalWhisperBackend");
        var llm = Combo(window, "SettingsLocalLlmBackend");
        // What a click on a list item does: the combo's own SelectedItem changes.
        whisper.SelectedItem = "cuda12";
        llm.SelectedItem = "vulkan";
        await Settle();
        Report("picked on Model Library", shell, settingsPath, failures);

        // Click Home. Settle waits past the 500 ms settings auto-save.
        shell.Navigate("home");
        await Settle();
        Report("after leaving for Home", shell, settingsPath, failures);

        shell.Navigate("models");
        await Settle();
        Report("after reopening Model Library", shell, settingsPath, failures);
        whisper = Combo(window, "SettingsLocalWhisperBackend");
        llm = Combo(window, "SettingsLocalLlmBackend");
        ExpectShown("the reopened Whisper picker", whisper, "cuda12", failures);
        ExpectShown("the reopened LLM picker", llm, "vulkan", failures);
    }

    private static void Report(string step, ApplicationShellViewModel shell, string settingsPath, List<string> failures)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
        string Saved(string key) => saved.RootElement.TryGetProperty(key, out var value) ? value.ToString() : "<absent>";
        var savedWhisper = Saved("localWhisperBackend");
        var savedLlm = Saved("localLlmBackend");
        Console.WriteLine($"{step}: view model whisper {shell.Settings.LocalWhisperBackend}, llm {shell.Settings.LocalLlmBackend}; settings.json whisper {savedWhisper}, llm {savedLlm}");
        if (shell.Settings.LocalWhisperBackend != "cuda12") failures.Add($"{step}: the view model holds Whisper backend {shell.Settings.LocalWhisperBackend}, expected cuda12");
        if (shell.Settings.LocalLlmBackend != "vulkan") failures.Add($"{step}: the view model holds LLM backend {shell.Settings.LocalLlmBackend}, expected vulkan");
        if (savedWhisper != "cuda12") failures.Add($"{step}: settings.json holds localWhisperBackend {savedWhisper}, expected cuda12");
        if (savedLlm != "vulkan") failures.Add($"{step}: settings.json holds localLlmBackend {savedLlm}, expected vulkan");
    }

    // What the closed combo draws is SelectionBoxItem through the ItemTemplate, so a picker whose
    // SelectedItem is right can still draw a blank box; read both.
    private static void ExpectShown(string label, ComboBox combo, string id, List<string> failures)
    {
        var selected = combo.SelectedItem as string ?? "<blank>";
        var box = combo.SelectionBoxItem as string ?? "<blank>";
        Console.WriteLine($"{label}: SelectedItem {selected}, SelectionBoxItem {box}");
        if (selected != id) failures.Add($"{label} has SelectedItem {selected}, expected {id}");
        if (box != id) failures.Add($"{label} draws {box}, expected {id}");
    }

    private static ComboBox Combo(Window window, string name) =>
        window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(combo => combo.Name == name && combo.IsEffectivelyVisible)
        ?? throw new InvalidOperationException($"Model Library shows no {name}");

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
