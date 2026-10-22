using System.Diagnostics;

// A real Avalonia window needs a real windowing platform, and Avalonia allows one per process, so a
// render test re-runs this harness under xvfb-run with its probe argument. This is the parent half
// every render test shares: a scratch HOME, a scrubbed session environment, a 60 s deadline, and
// the probe's output relayed on success or thrown on a non-zero exit.
static class XvfbProbe
{
    private const string XvfbRun = "/usr/bin/xvfb-run";

    public static async Task RunAsync(string probeArgument, string label, string culture, string screen)
    {
        if (!File.Exists(XvfbRun)) throw new InvalidOperationException($"xvfb-run is required to render {label}");
        var self = Path.Combine(AppContext.BaseDirectory, "HyperWhisper.Linux.Composition.Tests");
        if (!File.Exists(self)) throw new InvalidOperationException($"the harness executable is missing: {self}");

        var root = Path.Combine(Path.GetTempPath(), $"hw-xvfb-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo(XvfbRun) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in (string[])["-a", "-s", $"-screen 0 {screen}x24", self, probeArgument])
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
                // The probe can exit between the deadline and the kill; that must not hide the timeout.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new InvalidOperationException($"{label} ({culture}) did not exit within 60 s");
            }
            var text = (await output + await error).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{label} ({culture}) exited {process.ExitCode}:\n{text}");
            foreach (var line in text.Split('\n')) Console.WriteLine($"  [{culture}] {line}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
