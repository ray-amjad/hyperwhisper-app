// LOGGING SERVICE: THE BUFFERED WRITER STILL GETS EVERY LINE TO DISK (issue #985)
//
// LoggingService queues each line and one background thread writes the queue.
// These checks hold it to what the old write-per-line logger gave for free:
// Flush() and an Error line are on disk when they return, a line goes to the
// file of the root it was logged under, and a crash keeps its last line.
//
// The crash checks run this executable again as a child (Main calls
// RunCrashChildIfRequested first). The child logs one Debug line and dies of a
// real unhandled exception on a background thread; the parent reads the
// child's log. In the "writer-held" mode the child first holds the logger's
// write lock on another thread, so the background writer cannot write the line
// before the runtime kills the process. Only LoggingService's own
// unhandled-exception flush, which waits for that lock, can get the line to
// disk. Without it the line is lost, so that case fails.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HyperWhisper.Services;

namespace HyperWhisper.SmokeTests;

internal static class LoggingServiceFlushChecks
{
    private const string CrashChildEnvironmentVariable = "HYPERWHISPER_SMOKE_LOG_CRASH_CHILD";
    private const string CrashChildMarkerEnvironmentVariable = "HYPERWHISPER_SMOKE_LOG_CRASH_MARKER";
    private const string CrashMessage = "percy985 deliberate unhandled exception";

    // How long the writer-held child keeps the write lock. Far longer than the
    // runtime takes to report an unhandled exception and end the process.
    private static readonly TimeSpan WriterHeldFor = TimeSpan.FromSeconds(2);

    // Child exit codes that mean the child went wrong, not that it crashed.
    private const int ChildSetupFailed = 3;
    private const int ChildSurvivedCrash = 4;

    public static void RunAll(Action<string, Action> run)
    {
        run("logging: Info and Debug lines are on disk in order after Flush(), an Error line without one (#985)", () =>
        {
            var tag = $"percy985flush{Guid.NewGuid():N}";
            LoggingService.Info($"{tag} one");
            LoggingService.Debug($"{tag} two");
            LoggingService.Warn($"{tag} three");
            LoggingService.Flush();
            var afterFlush = ReadCurrentLog();
            AssertInOrder(afterFlush, $"{tag} one", $"{tag} two", $"{tag} three");

            LoggingService.Debug($"{tag} four");
            LoggingService.Error($"{tag} five");
            // No Flush: Error writes the queue, its own line last, before it returns.
            var afterError = ReadCurrentLog();
            AssertInOrder(afterError, $"{tag} one", $"{tag} two", $"{tag} three", $"{tag} four", $"{tag} five");
        });

        run("logging: a line logged after the app-data root changes goes to the new root's log (#985)", () =>
        {
            var previousRoot = Environment.GetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable);
            var oldLog = LoggingService.CurrentLogPath;
            var newRoot = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"log-root-{Guid.NewGuid():N}");
            var tag = $"percy985root{Guid.NewGuid():N}";
            try
            {
                LoggingService.Info($"{tag} before");
                Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, newRoot);
                LoggingService.Info($"{tag} after");
                LoggingService.Flush();

                var newLog = Path.Combine(newRoot, "Logs", Path.GetFileName(oldLog));
                Assert(File.Exists(newLog), "no log file was created under the new app-data root");
                var newText = ReadShared(newLog);
                var oldText = ReadShared(oldLog);
                Assert(newText.Contains($"{tag} after") && !newText.Contains($"{tag} before"),
                    "the new root's log does not hold exactly the line logged after the change");
                Assert(oldText.Contains($"{tag} before") && !oldText.Contains($"{tag} after"),
                    "the old root's log does not hold exactly the line logged before the change");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, previousRoot);
                // Moves the writer's open file back to the suite's own log, so the
                // scratch root can be deleted.
                LoggingService.Info($"{tag} restored");
                LoggingService.Flush();
                try { Directory.Delete(newRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });

        run("logging: a crash by unhandled exception still leaves its last log line on disk (#985)", () =>
        {
            AssertCrashChildLeavesLastLine("plain");
        });

        run("logging: a crash while the background writer cannot run still leaves its last log line on disk (#985)", () =>
        {
            AssertCrashChildLeavesLastLine("writer-held");
        });
    }

    /// <summary>
    /// Main's first call. Returns at once unless this process was started as a
    /// crash child; a crash child never returns.
    /// </summary>
    internal static void RunCrashChildIfRequested()
    {
        var mode = Environment.GetEnvironmentVariable(CrashChildEnvironmentVariable);
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // No "has stopped working" dialog to wait on: the crash must end the process.
                SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
            }

            var marker = Environment.GetEnvironmentVariable(CrashChildMarkerEnvironmentVariable)
                ?? throw new InvalidOperationException("no marker given");

            // The first line starts the writer thread and hooks the crash handler.
            LoggingService.Info("crash child: started");
            LoggingService.Flush();

            if (mode == "writer-held")
            {
                var writeLock = typeof(LoggingService)
                    .GetField("_writeLock", BindingFlags.NonPublic | BindingFlags.Static)?
                    .GetValue(null)
                    ?? throw new InvalidOperationException("LoggingService has no _writeLock field to hold");
                using var held = new ManualResetEventSlim();
                new Thread(() =>
                {
                    lock (writeLock)
                    {
                        held.Set();
                        Thread.Sleep(WriterHeldFor);
                    }
                })
                { IsBackground = true }.Start();
                held.Wait();
            }
            else if (mode != "plain")
            {
                throw new InvalidOperationException($"unknown crash child mode '{mode}'");
            }

            Console.Out.WriteLine("crash child: crashing");
            Console.Out.Flush();

            // Debug, the level nothing flushes by itself, then the crash at once.
            LoggingService.Debug(marker);
            var crasher = new Thread(() => throw new InvalidOperationException(CrashMessage)) { IsBackground = true };
            crasher.Start();
            crasher.Join();
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(ChildSetupFailed);
        }

        Console.Error.WriteLine("crash child: the process outlived its unhandled exception");
        Environment.Exit(ChildSurvivedCrash);
    }

    private static void AssertCrashChildLeavesLastLine(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.SmokeTests", $"log-crash-{Guid.NewGuid():N}");
        var marker = $"percy985crash {mode} {Guid.NewGuid():N}";
        try
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Environment[AppPaths.AppDataRootOverrideEnvironmentVariable] = root;
            startInfo.Environment[CrashChildEnvironmentVariable] = mode;
            startInfo.Environment[CrashChildMarkerEnvironmentVariable] = marker;

            using var child = Process.Start(startInfo)
                ?? throw new InvalidOperationException("the crash child did not start");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(60_000))
            {
                child.Kill(entireProcessTree: true);
                throw new InvalidOperationException("the crash child did not exit within 60 s");
            }
            child.WaitForExit();

            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            Assert(child.ExitCode is not (0 or ChildSetupFailed or ChildSurvivedCrash),
                $"the crash child exited {child.ExitCode} instead of crashing: {errors}");
            Assert(output.Contains("crash child: crashing"), $"the crash child never reached its crash: {output}");
            Assert(errors.Contains(CrashMessage),
                $"the child died, but not of the unhandled exception (exit {child.ExitCode}): {errors}");

            var logs = Path.Combine(root, "Logs");
            var lines = Directory.Exists(logs)
                ? Directory.GetFiles(logs, "*.log").OrderBy(f => f, StringComparer.Ordinal)
                    .SelectMany(f => File.ReadAllLines(f)).Where(l => l.Length > 0).ToList()
                : new List<string>();
            Assert(lines.Count > 0 && lines[^1].Contains(marker),
                $"the line logged just before the crash is not the last line on disk; last line: '{(lines.Count > 0 ? lines[^1] : "(no log)")}'");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string ReadCurrentLog() => ReadShared(LoggingService.CurrentLogPath);

    /// <summary>Reads a log the writer may still hold open.</summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertInOrder(string text, params string[] needles)
    {
        var from = 0;
        foreach (var needle in needles)
        {
            var at = text.IndexOf(needle, from, StringComparison.Ordinal);
            Assert(at >= 0, $"'{needle}' is not on disk (in order) when it should be");
            from = at + needle.Length;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
