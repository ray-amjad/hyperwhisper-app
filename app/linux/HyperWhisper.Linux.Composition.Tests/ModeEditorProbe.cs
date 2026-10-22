using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HyperWhisper.Linux;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;
using Microsoft.Data.Sqlite;

// The child half every mode editor render test shares: it runs on the X server XvfbProbe started.
// A scratch database, the real App, and the real ModeEditorWindow opened as Modes -> Create Mode
// does; the test's check drives the dialog and returns its failures. Exit 0 only if there are none.
static class ModeEditorProbe
{
    public static async Task<int> RunAsync(string tag, Func<ModesViewModel, ModeEditorWindow, List<string>> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hw-{tag}-probe-{Guid.NewGuid():N}");
        try
        {
            // Finish every await BEFORE Avalonia installs its synchronization context: nothing pumps
            // the dispatcher here except Pump, so a continuation posted to it would never run. That
            // is why check is synchronous.
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

            var failures = check(modes, window);

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

    /// <summary>Run the queued dispatcher jobs and a layout pass, so what the check reads is what the window draws.</summary>
    public static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
