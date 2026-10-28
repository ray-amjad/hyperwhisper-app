// PARAKEET: A GUI MODEL LOAD AND ITS FILE JOB COUNT AS BUSY (issue #1618)
//
// #1610 made a Local API / History Retry / onboarding Parakeet reload wait until
// nobody is counted in on the shared daemon (ReloadWhenIdleAsync). A GUI load
// through InitializeAsync waits for the daemon's READY line (30-90 s) outside the
// transcription lock, and counted nothing, so an idle reload in that window took
// the free lock and stopped the daemon the GUI was still starting; the housekeeping
// unload did the same. A Transcribe File job also left a gap between its load and
// its TranscribeAsync. These checks drive the real service: the GUI load starts a
// stand-in daemon (cmd.exe, through the StartDaemonProcess seam) that prints READY
// only when the check says so, and the job lease runs on a fake warm daemon.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.SmokeTests;

internal static class ParakeetGuiLoadReloadChecks
{
    /// <summary>How long a check gives the service to act wrongly before it asserts it did not.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(800);

    public static void RunAll(Action<string, Action> run)
    {
        run("Parakeet: an idle reload waits for a GUI model load still waiting for READY, then runs (#1618)",
            () => OffContext(IdleReloadWaitsForGuiLoadAsync));
        run("Parakeet: the housekeeping unload leaves a GUI model load in progress alone (#1618)",
            () => OffContext(HousekeepingUnloadSparesGuiLoadAsync));
        run("Parakeet: a GUI job's ReserveForJob lease keeps another model's idle reload waiting until disposed (#1618)",
            () => OffContext(JobLeaseHoldsIdleReloadAsync));
    }

    /// <summary>
    /// On the thread pool: an earlier WPF case can leave a SynchronizationContext nothing
    /// pumps, and a blocking wait on an await captured by it hangs the whole suite.
    /// </summary>
    private static void OffContext(Func<Task> check) => Task.Run(check).GetAwaiter().GetResult();

    private static async Task IdleReloadWaitsForGuiLoadAsync()
    {
        using var standIn = new StandInDaemon("reload");
        var service = standIn.CreateService();
        try
        {
            // The GUI's mode switch / Transcribe File load, now waiting for READY.
            var load = Task.Run(() => service.InitializeAsync(standIn.ModelDirectory, null));
            var pid = await standIn.WaitStartedAsync();

            // The Local API's request for another model, during that wait.
            var reload = service.ReloadWhenIdleAsync(
                "other-parakeet", standIn.MissingModelDirectory, null, CancellationToken.None);
            await Task.Delay(Settle);

            Assert(!reload.IsCompleted, $"the idle reload ran while a GUI load waited for READY (#1618): {Describe(reload)}");
            Assert(IsRunning(pid), "the idle reload stopped the daemon the GUI load is starting (#1618)");
            Assert(!load.IsCompleted, $"the GUI load ended before its daemon sent READY: {Describe(load)}");

            standIn.SendReady();
            await load.WaitAsync(TimeSpan.FromSeconds(20)); // READY: the load succeeded

            // The load has ended: the reload goes ahead, stops the GUI's daemon and starts
            // its own model, which fails here only because that model is not installed.
            await AssertReloadTriedMissingModelAsync(reload);
            Assert(HasExited(pid), "the reload did not stop the old daemon once the GUI load had ended");
            Assert(PendingRequests(service) == 0, $"{PendingRequests(service)} request(s) left counted in after the load and the reload");
        }
        finally
        {
            service.DisposeModel();
        }
    }

    private static async Task HousekeepingUnloadSparesGuiLoadAsync()
    {
        using var standIn = new StandInDaemon("unload");
        var service = standIn.CreateService();
        try
        {
            var load = Task.Run(() => service.InitializeAsync(standIn.ModelDirectory, null));
            var pid = await standIn.WaitStartedAsync();

            Assert(!service.TryDisposeModelIfIdle(), "the housekeeping unload stopped the daemon a GUI load is starting (#1618)");
            await Task.Delay(Settle);
            Assert(IsRunning(pid), "the housekeeping unload killed the starting daemon");
            Assert(!load.IsCompleted, $"the GUI load ended before its daemon sent READY: {Describe(load)}");

            standIn.SendReady();
            await load.WaitAsync(TimeSpan.FromSeconds(20));

            // As for a busy daemon (#1544): the unload it deferred runs once the load has left.
            var drained = Stopwatch.StartNew();
            while (service.IsInitialized && drained.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
            }

            Assert(!service.IsInitialized, "the unload deferred during the GUI load never ran after it");
            Assert(HasExited(pid), "the deferred unload did not stop the daemon");
            Assert(PendingRequests(service) == 0, $"{PendingRequests(service)} request(s) left counted in after the load");
        }
        finally
        {
            service.DisposeModel();
        }
    }

    private static async Task JobLeaseHoldsIdleReloadAsync()
    {
        var root = NewScratchFolder("lease");
        var service = new ParakeetTranscriptionService();
        using var daemon = AttachFakeWarmDaemon(service, "fake-parakeet");
        var pid = daemon.Id;
        try
        {
            // Transcribe File counted in before its load; it is converting the file now,
            // with nothing inside TranscribeAsync yet.
            var lease = service.ReserveForJob();
            var reload = service.ReloadWhenIdleAsync(
                "other-parakeet", Path.Combine(root, "no-such-model"), null, CancellationToken.None);
            await Task.Delay(Settle);

            Assert(!reload.IsCompleted, $"the idle reload ran while a GUI job held its lease (#1618): {Describe(reload)}");
            Assert(IsRunning(pid), "the idle reload stopped the daemon a GUI job holds");
            Assert(PendingRequests(service) == 1, $"expected only the job counted in (a waiting reload is not), got {PendingRequests(service)}");

            lease.Dispose();
            lease.Dispose(); // twice is harmless

            await AssertReloadTriedMissingModelAsync(reload);
            Assert(HasExited(pid), "the reload did not stop the old daemon once the lease was disposed");
            Assert(PendingRequests(service) == 0, $"{PendingRequests(service)} request(s) left counted in after the lease and the reload");
        }
        finally
        {
            try { if (!daemon.HasExited) daemon.Kill(entireProcessTree: true); } catch { }
            service.DisposeModel();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The reload stopped the old daemon and tried to start a model with no directory.</summary>
    private static async Task AssertReloadTriedMissingModelAsync(Task<ParakeetTranscriptionService.ModelLease> reload)
    {
        try
        {
            (await reload.WaitAsync(TimeSpan.FromSeconds(20))).Dispose();
            Assert(false, "the reload should have tried to start the missing model");
        }
        catch (TranscriptionException ex) when (ex.Code is TranscriptionErrorCode.OnnxModelFileMissing or TranscriptionErrorCode.DaemonStartFailed)
        {
        }
    }

    /// <summary>
    /// A stand-in parakeet-engine for a real InitializeAsync: cmd.exe, which ValidatePaths
    /// accepts as the daemon binary. It waits for one stdin line and prints the READY line
    /// only when that line is "go" (the service's quit command makes it exit instead, so a
    /// teardown closes its stdout as a killed daemon would), then waits for the quit.
    /// </summary>
    private sealed class StandInDaemon : IDisposable
    {
        private readonly string _root;
        private Process? _process;
        private int _pid;

        public StandInDaemon(string name)
        {
            _root = NewScratchFolder(name);
            ModelDirectory = Path.Combine(_root, "stand-in-parakeet");
            Directory.CreateDirectory(ModelDirectory);
            File.WriteAllText(Path.Combine(_root, "ready.json"), "{\"status\":\"ready\",\"provider\":\"cpu\"}\r\n");
        }

        public string ModelDirectory { get; }

        public string MissingModelDirectory => Path.Combine(_root, "no-such-model");

        public ParakeetTranscriptionService CreateService() => new()
        {
            DaemonPathOverride = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            StartDaemonProcess = process =>
            {
                var startInfo = process.StartInfo;
                startInfo.ArgumentList.Clear();
                startInfo.WorkingDirectory = _root;
                startInfo.Arguments = "/d /q /v:on /c set /p line= & if \"!line!\"==\"go\" (type ready.json & set /p quit=)";
                var started = process.Start();
                if (started)
                {
                    Volatile.Write(ref _pid, process.Id);
                    Volatile.Write(ref _process, process);
                }
                return started;
            },
        };

        /// <summary>The stand-in's process id, once the load has started it.</summary>
        public async Task<int> WaitStartedAsync()
        {
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref _process) == null)
            {
                Assert(clock.Elapsed < TimeSpan.FromSeconds(15), "the GUI load never started its daemon");
                await Task.Delay(20);
            }

            return Volatile.Read(ref _pid);
        }

        public void SendReady()
        {
            var input = Volatile.Read(ref _process)!.StandardInput;
            input.WriteLine("go");
            input.Flush();
        }

        public void Dispose()
        {
            var pid = Volatile.Read(ref _pid);
            if (pid != 0 && IsRunning(pid))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch { }
            }

            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A fake ready daemon, as Program.cs's #1608 checks attach one: a sacrificial child
    /// process so IsAvailable holds and a teardown has something to stop, and null stdio.
    /// </summary>
    private static Process AttachFakeWarmDaemon(ParakeetTranscriptionService service, string loadedModelId)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            ArgumentList = { "-n", "600", "127.0.0.1" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("could not start the fake daemon process");

        SetField(service, "_daemonProcess", process);
        SetField(service, "_stdinWriter", new StreamWriter(Stream.Null));
        SetField(service, "_stdoutReader", new StreamReader(Stream.Null));
        SetField(service, "_loadedModelId", loadedModelId);
        SetField(service, "_lastLanguage", "auto");
        SetField(service, "_isReady", true);
        return process;
    }

    private static FieldInfo Field(string name) =>
        typeof(ParakeetTranscriptionService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"ParakeetTranscriptionService.{name} is gone; update this check");

    private static void SetField(ParakeetTranscriptionService service, string name, object? value) =>
        Field(name).SetValue(service, value);

    private static int PendingRequests(ParakeetTranscriptionService service) =>
        (int)Field("_pendingRequests").GetValue(service)!;

    private static string NewScratchFolder(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"parakeet-1618-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>By id: the service disposes the Process objects it tears down.</summary>
    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string Describe(Task task) =>
        task.Exception?.GetBaseException() is { } ex ? $"{task.Status}: {ex.GetType().Name}: {ex.Message}" : task.Status.ToString();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
