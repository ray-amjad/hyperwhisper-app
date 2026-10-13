using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;
using Microsoft.Data.Sqlite;

// Issue #635: the mode editor's Source segmented control drew "HyperWhisper Cloud" as
// "HyperWhisper Clouc" — the label was wider than its third of the control, and the segment cut
// it at the boundary. A text clip only shows in a real layout with the real font, so this opens
// the real ModeEditorWindow under the real App on a 1440x900 X server (the size the fuzz round
// saw), checks every segment of both Source controls, checked and unchecked, and fails when a
// label's full text is wider than the box it was given. It runs once per UI culture, because a
// translation can be longer than the English.
static class ModeEditorSourceSegmentRenderTests
{
    public const string ProbeArgument = "--probe-mode-editor-source-segments";

    // English, plus the cultures whose segment labels are the longest in the catalog.
    // Arabic lays the row out right to left.
    private static readonly string[] Cultures = ["en", "bg", "ru", "th", "hi", "el", "ar"];

    private static readonly string[] Segments =
    [
        "ModeSourceOnDevice", "ModeSourceHwCloud", "ModeSourceYourProvider",
        "ModePpSourceOnDevice", "ModePpSourceHwCloud", "ModePpSourceYourProvider",
    ];

    public static async Task RunAsync()
    {
        const string xvfbRun = "/usr/bin/xvfb-run";
        if (!File.Exists(xvfbRun)) throw new InvalidOperationException("xvfb-run is required to render the mode editor");
        var self = Path.Combine(AppContext.BaseDirectory, "HyperWhisper.Linux.Composition.Tests");
        if (!File.Exists(self)) throw new InvalidOperationException($"the harness executable is missing: {self}");

        foreach (var culture in Cultures)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hw-635-mode-editor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var start = new ProcessStartInfo(xvfbRun) { RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var argument in (string[])["-a", "-s", "-screen 0 1440x900x24", self, ProbeArgument])
                    start.ArgumentList.Add(argument);
                start.Environment["HOME"] = root;
                start.Environment["XDG_RUNTIME_DIR"] = Path.Combine(root, "run");
                start.Environment["XDG_SESSION_TYPE"] = "x11";
                start.Environment["HYPERWHISPER_UI_CULTURE"] = culture;
                foreach (var name in (string[])["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "DBUS_SESSION_BUS_ADDRESS", "WAYLAND_DISPLAY"])
                    start.Environment.Remove(name);
                Directory.CreateDirectory(Path.Combine(root, "run"));

                using var process = Process.Start(start)!;
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException($"the mode editor probe ({culture}) did not exit within 60 s");
                }
                var text = (await output + await error).Trim();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"the mode editor probe ({culture}) exited {process.ExitCode}:\n{text}");
                foreach (var line in text.Split('\n')) Console.WriteLine($"  [{culture}] {line}");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>The child half: runs on the X server xvfb-run started. Exit 0 only if every label fits.</summary>
    public static async Task<int> RunProbeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hw-635-probe-{Guid.NewGuid():N}");
        try
        {
            // Finish every await BEFORE Avalonia installs its synchronization context: nothing pumps
            // the dispatcher here except this method, so a continuation posted to it would never run.
            Directory.CreateDirectory(root);
            var database = new ApplicationDb(new StaticPaths(root));
            await database.MigrateAsync();
            var modes = new ModesViewModel(new ModeRepository(database));

            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();

            // Modes -> Create Mode, exactly as MainWindow opens it, at the dialog's default size.
            modes.NewCommand.Execute(null);
            var window = new ModeEditorWindow(modes, isCreate: true, snapshot: null);
            window.Show();
            Pump(window);

            var failures = new List<string>();
            var radios = Segments.Select(name =>
                window.FindControl<RadioButton>(name) ?? throw new InvalidOperationException($"ModeEditorWindow has no {name}")).ToList();
            // A checked segment draws SemiBold, which is wider than Medium, so measure every label
            // in both states: check each segment of a group in turn.
            foreach (var selected in radios)
            {
                selected.IsChecked = true;
                Pump(window);
                foreach (var (radio, name) in radios.Zip(Segments))
                {
                    var labels = radio.GetVisualDescendants().OfType<TextBlock>().Where(block => !string.IsNullOrEmpty(block.Text)).ToList();
                    if (labels.Count == 0) { failures.Add($"{name} draws no label"); continue; }
                    foreach (var label in labels)
                    {
                        // TextLayout.Width is the widest line the layout drew; a label that does not
                        // fit and cannot wrap reports more than the box it was arranged into.
                        var needed = label.TextLayout.Width;
                        var given = label.Bounds.Width;
                        var lines = label.TextLayout.TextLines.Count;
                        var state = radio.IsChecked == true ? "checked" : "unchecked";
                        if (selected == radios[0])
                            Console.WriteLine($"{name} ({state}): '{label.Text}' needs {needed:F1} px, given {given:F1} px, {lines} line(s)");
                        if (needed > given + 0.5)
                            failures.Add($"{name} ({state}): '{label.Text}' needs {needed:F1} px but its box is {given:F1} px, so it is clipped");
                        // Transform the whole box, not its origin: under a right-to-left culture the
                        // label's (0,0) maps to its RIGHT edge in the segment's coordinates.
                        var placed = label.TransformToVisual(radio) is { } transform
                            ? new Rect(label.Bounds.Size).TransformToAABB(transform)
                            : default;
                        if (placed.Left < -0.5 || placed.Right > radio.Bounds.Width + 0.5)
                            failures.Add($"{name} ({state}): '{label.Text}' spills outside its segment");
                    }
                }
            }

            window.Close();
            Pump(window);
            foreach (var failure in failures.Distinct()) Console.Error.WriteLine($"FAIL {failure}");
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
        finally
        {
            // ApplicationDb disposes each context it opens, but the SQLite pool keeps the file
            // handle open; drain it first so the delete never races a live handle on the database.
            // Cleanup must never mask the exit code that ended the probe.
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"could not delete {root}: {exception.Message}"); }
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
