// PARAKEET: A RELOAD'S QUIT IS NOT A CRASH (#3040), AND /transcribe REPORTS THE LOAD (#3041)
//
// #3040: a graceful reload the app asked for (quit the old daemon, start a new one)
// logged "Daemon process exited unexpectedly (exit code: 0)" at WARN, and the exit
// handler read whatever daemon was current by then, not the one that exited. The
// requested-exit mark now belongs to the one Process, so a new daemon never inherits
// it; a daemon that exits on its own still logs WARN with its exit code.
//
// #3041: a Local API /transcribe with engine=parakeet that loaded the model, or fully
// reloaded the daemon, answered timings.load_ms 0. The load now lands in the same
// AsyncLocal scope the Whisper worker's cold start uses (#1932).
//
// The service checks drive the real ParakeetTranscriptionService with a stand-in
// daemon (cmd.exe, through the StartDaemonProcess seam): it waits about a second,
// prints READY, then reads one stdin line. The service's quit command makes it exit;
// "die" makes it exit on its own with code 7.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using HyperWhisper.Data.Entities;
using HyperWhisper.Services;
using HyperWhisper.Services.LocalApi;
using HyperWhisper.Services.Transcription;

namespace HyperWhisper.SmokeTests;

internal static class ParakeetDaemonExitAndLoadTimingChecks
{
    /// <summary>The stand-in's start-to-READY pause is ~1 s (ping -n 2); a load must count at least this.</summary>
    private static readonly TimeSpan MinimumLoad = TimeSpan.FromMilliseconds(500);

    public static void RunAll(Action<string, Action> run)
    {
        run("Parakeet: a requested quit or kill logs Info, an exit on its own logs WARN with its exit code (#3040)",
            DescribeDaemonExitLevels);
        run("Parakeet: the requested-exit mark belongs to one daemon Process; a new one does not inherit it (#3040)",
            RequestedExitMarkIsPerProcess);
        run("Parakeet: a graceful reload logs the old daemon's exit at Info, and its late exit event leaves the new daemon ready (#3040)",
            () => OffContext(GracefulReloadIsNotACrashAsync));
        run("Parakeet: a daemon that exits on its own still logs WARN with its exit code (#3040)",
            () => OffContext(UnrequestedExitWarnsAsync));
        run("Parakeet: a Local API load or full daemon reload reports a non-zero load_ms; a warm request reports 0 (#3041)",
            () => OffContext(LoadAndReloadReportLoadMsAsync));
    }

    private static void DescribeDaemonExitLevels()
    {
        var (quitWarn, quitMessage) = ParakeetTranscriptionService.DescribeDaemonExit(0, "quit");
        Assert(!quitWarn, $"a quit the app asked for logged at WARN: {quitMessage}");
        Assert(!quitMessage.Contains("unexpectedly", StringComparison.Ordinal), $"a requested quit still reads as unexpected: {quitMessage}");
        Assert(quitMessage.Contains("quit", StringComparison.Ordinal) && quitMessage.Contains("exit code: 0", StringComparison.Ordinal),
            $"the requested-quit line lost how or its exit code: {quitMessage}");

        var (killWarn, killMessage) = ParakeetTranscriptionService.DescribeDaemonExit(-1, "kill");
        Assert(!killWarn && killMessage.Contains("kill", StringComparison.Ordinal), $"a kill the app made logged as a crash: {killMessage}");

        // Exit code 0 is no excuse: a daemon nobody asked to quit that leaves is still a WARN.
        var (crashWarn, crashMessage) = ParakeetTranscriptionService.DescribeDaemonExit(0, null);
        Assert(crashWarn, $"a daemon that exited on its own was not a WARN: {crashMessage}");
        var (crash7Warn, crash7Message) = ParakeetTranscriptionService.DescribeDaemonExit(7, null);
        Assert(crash7Warn && crash7Message.Contains("exited unexpectedly", StringComparison.Ordinal)
               && crash7Message.Contains("exit code: 7", StringComparison.Ordinal),
            $"the unexpected-exit WARN lost its wording or exit code: {crash7Message}");
    }

    private static void RequestedExitMarkIsPerProcess()
    {
        using var oldDaemon = new Process();
        using var newDaemon = new Process();
        ParakeetTranscriptionService.MarkExitRequested(oldDaemon, "quit");

        Assert(ParakeetTranscriptionService.ExitRequestedBy(oldDaemon) == "quit", "the quit mark was not kept on the daemon it was set on");
        Assert(ParakeetTranscriptionService.ExitRequestedBy(newDaemon) == null, "a new daemon inherited the old daemon's quit mark");

        // A quit that times out is followed by a kill: the later one is what happened.
        ParakeetTranscriptionService.MarkExitRequested(oldDaemon, "kill");
        Assert(ParakeetTranscriptionService.ExitRequestedBy(oldDaemon) == "kill", "the kill after a quit did not update the mark");
    }

    private static async Task GracefulReloadIsNotACrashAsync()
    {
        using var standIn = new StandInDaemons("reload");
        var service = standIn.CreateService();
        try
        {
            await service.InitializeAsync(standIn.ModelDirectoryA, null).WaitAsync(TimeSpan.FromSeconds(30));
            var oldDaemon = standIn.Started.Single();
            var oldPid = oldDaemon.Id;

            // The Local API's reload for another model: quit the old daemon, start a new one.
            using (var lease = await service.ReloadWhenIdleAsync("stand-in-b", standIn.ModelDirectoryB, null, CancellationToken.None)
                       .WaitAsync(TimeSpan.FromSeconds(30)))
            {
                Assert(lease.Reloaded, "the second model did not reload the daemon");
            }

            Assert(HasExited(oldPid), "the reload did not stop the old daemon");
            Assert(standIn.Started.Count == 2, $"expected two daemon starts, got {standIn.Started.Count}");

            // The old daemon's exit event can land after the reload has already started the
            // new one (the event runs on the thread pool). Raise it as that late event would.
            service.OnDaemonExited(oldDaemon, EventArgs.Empty);

            var warns = standIn.ExitLogs.Where(entry => entry.Warn).Select(entry => entry.Message).ToList();
            Assert(warns.Count == 0, $"a graceful reload logged a WARN: {string.Join(" | ", warns)}");
            Assert(standIn.ExitLogs.Any(entry => !entry.Warn && entry.Message.Contains("(quit;", StringComparison.Ordinal)),
                $"the requested quit was not logged at Info: {string.Join(" | ", standIn.ExitLogs.Select(e => e.Message))}");
            Assert(service.IsAvailable, "the old daemon's late exit event marked the new daemon not ready");
        }
        finally
        {
            service.DisposeModel();
        }
    }

    private static async Task UnrequestedExitWarnsAsync()
    {
        using var standIn = new StandInDaemons("crash");
        var service = standIn.CreateService();
        try
        {
            await service.InitializeAsync(standIn.ModelDirectoryA, null).WaitAsync(TimeSpan.FromSeconds(30));
            var daemon = standIn.Started.Single();
            // The daemon exits on its own, with code 7: nobody in the app asked it to.
            daemon.StandardInput.WriteLine("die");
            daemon.StandardInput.Flush();

            const string expected = "exit code: 7";
            var clock = Stopwatch.StartNew();
            while (!standIn.ExitLogs.Any(entry => entry.Message.Contains(expected, StringComparison.Ordinal)))
            {
                Assert(clock.Elapsed < TimeSpan.FromSeconds(15), "the daemon's own exit was never logged");
                await Task.Delay(50);
            }

            var line = standIn.ExitLogs.First(entry => entry.Message.Contains(expected, StringComparison.Ordinal));
            Assert(line.Warn, $"a daemon that exited on its own was logged below WARN: {line.Message}");
            Assert(line.Message.Contains("exited unexpectedly", StringComparison.Ordinal), $"the crash line lost its wording: {line.Message}");
            Assert(!service.IsAvailable, "a crashed current daemon still reads as available");
        }
        finally
        {
            service.DisposeModel();
        }
    }

    private static async Task LoadAndReloadReportLoadMsAsync()
    {
        using var standIn = new StandInDaemons("timing");
        var service = standIn.CreateService();
        try
        {
            // A cold load, as a /transcribe that found no daemon pays it.
            TimeSpan coldLoad;
            using (var scope = LocalModelLoadTiming.Begin())
            {
                using var lease = await service.ReloadWhenIdleAsync("stand-in-a", standIn.ModelDirectoryA, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                coldLoad = scope.Elapsed;
                Assert(lease.Reloaded, "the cold request did not load the daemon");
            }
            Assert(coldLoad >= MinimumLoad, $"a cold load counted {coldLoad.TotalMilliseconds:0} ms of load, expected at least {MinimumLoad.TotalMilliseconds:0}");

            // A full daemon reload for another model: INITIALIZING -> READY again.
            TimeSpan reload;
            using (var scope = LocalModelLoadTiming.Begin())
            {
                using var lease = await service.ReloadWhenIdleAsync("stand-in-b", standIn.ModelDirectoryB, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                reload = scope.Elapsed;
                Assert(lease.Reloaded, "the other model did not reload the daemon");
            }
            Assert(reload >= MinimumLoad, $"a full reload counted {reload.TotalMilliseconds:0} ms of load, expected at least {MinimumLoad.TotalMilliseconds:0}");

            // Warm: the daemon fits, nothing is loaded.
            TimeSpan warm;
            using (var scope = LocalModelLoadTiming.Begin())
            {
                using var lease = await service.ReloadWhenIdleAsync("stand-in-b", standIn.ModelDirectoryB, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                warm = scope.Elapsed;
                Assert(!lease.Reloaded, "the warm request reloaded the daemon");
            }
            Assert(warm == TimeSpan.Zero, $"a warm request counted {warm.TotalMilliseconds:0} ms of load");

            // What the response carries: the reload paid before the transcription counts
            // in latency_ms and load_ms; decode_ms is the transcription.
            var mode = new Mode { Name = "Local API Parakeet timings", ProviderType = "local", Language = "en" };
            var result = new TranscriptionResult(RawText: "hello", FinalText: "hello", TranscriptionProvider: "Parakeet");
            var transcription = TimeSpan.FromMilliseconds(300);

            var (coldLatency, coldLoadMs) = WindowsLocalApiBackend.MeasureTimings(transcription, reload, reload);
            var reloaded = WindowsLocalApiBackend.BuildResponse(result, mode, coldLatency, coldLoadMs);
            Assert(reloaded.LoadMs == (int)Math.Round(reload.TotalMilliseconds) && reloaded.LoadMs > 0,
                $"a reloading /transcribe answered load_ms {reloaded.LoadMs}, expected {reload.TotalMilliseconds:0}");
            Assert(reloaded.DecodeMs == 300 && reloaded.LatencyMs == reloaded.LoadMs + reloaded.DecodeMs,
                $"reload: load {reloaded.LoadMs}, decode {reloaded.DecodeMs}, latency {reloaded.LatencyMs}");

            var (warmLatency, warmLoadMs) = WindowsLocalApiBackend.MeasureTimings(transcription, warm, warm);
            var warmResponse = WindowsLocalApiBackend.BuildResponse(result, mode, warmLatency, warmLoadMs);
            Assert(warmResponse.LoadMs == 0 && warmResponse.DecodeMs == 300 && warmResponse.LatencyMs == 300,
                $"warm: load {warmResponse.LoadMs}, decode {warmResponse.DecodeMs}, latency {warmResponse.LatencyMs}");

            // An auto-restart inside the transcription is in the total load and already in the latency.
            var (restartLatency, restartLoadMs) = WindowsLocalApiBackend.MeasureTimings(
                TimeSpan.FromMilliseconds(1300), TimeSpan.Zero, TimeSpan.FromMilliseconds(1000));
            Assert(restartLatency == 1300 && restartLoadMs == 1000,
                $"auto-restart: latency {restartLatency}, load {restartLoadMs}");
        }
        finally
        {
            service.DisposeModel();
        }
    }

    /// <summary>
    /// On the thread pool: an earlier WPF case can leave a SynchronizationContext nothing
    /// pumps, and a blocking wait on an await captured by it hangs the whole suite.
    /// </summary>
    private static void OffContext(Func<Task> check) => Task.Run(check).GetAwaiter().GetResult();

    /// <summary>
    /// Stand-in parakeet-engine daemons for a real load: cmd.exe, which ValidatePaths
    /// accepts as the daemon binary. Each waits about a second (the load), prints the
    /// READY line, then reads one stdin line: the service's quit command makes it exit
    /// (code 0), "die" makes it exit on its own with code 7.
    /// </summary>
    private sealed class StandInDaemons : IDisposable
    {
        private readonly string _root;
        private readonly List<int> _pids = new();

        public StandInDaemons(string name)
        {
            _root = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"parakeet-3040-{name}-{Guid.NewGuid():N}");
            ModelDirectoryA = Path.Combine(_root, "stand-in-a");
            ModelDirectoryB = Path.Combine(_root, "stand-in-b");
            Directory.CreateDirectory(ModelDirectoryA);
            Directory.CreateDirectory(ModelDirectoryB);
            File.WriteAllText(Path.Combine(_root, "ready.json"), "{\"status\":\"ready\",\"provider\":\"cpu\"}\r\n");
        }

        public string ModelDirectoryA { get; }

        public string ModelDirectoryB { get; }

        /// <summary>Every daemon the service started, in order.</summary>
        public List<Process> Started { get; } = new();

        /// <summary>Every daemon-exit line the service wrote: true is WARN.</summary>
        public ConcurrentQueue<(bool Warn, string Message)> ExitLogs { get; } = new();

        public ParakeetTranscriptionService CreateService() => new()
        {
            DaemonPathOverride = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            StartDaemonProcess = process =>
            {
                var startInfo = process.StartInfo;
                startInfo.ArgumentList.Clear();
                startInfo.WorkingDirectory = _root;
                startInfo.Arguments = "/d /q /v:on /c ping -n 2 127.0.0.1 >nul & type ready.json & set /p line= & if \"!line!\"==\"die\" exit 7";
                var started = process.Start();
                if (started)
                {
                    lock (Started)
                    {
                        Started.Add(process);
                        _pids.Add(process.Id);
                    }
                }
                return started;
            },
            LogDaemonExit = (warn, message) => ExitLogs.Enqueue((warn, message)),
        };

        public void Dispose()
        {
            // By id: the service disposes the Process objects it tears down.
            List<int> pids;
            lock (Started)
            {
                pids = _pids.ToList();
            }

            foreach (var pid in pids)
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

    /// <summary>By id: the service disposes the Process objects it tears down.</summary>
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
