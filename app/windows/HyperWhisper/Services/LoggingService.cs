using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HyperWhisper.Models;
using HyperWhisper.Services.LocalApi;

namespace HyperWhisper.Services;

/// <summary>
/// FILE-BASED LOGGING SERVICE FOR WINDOWS APPLICATION
///
/// Purpose:
/// Provides comprehensive file-based logging to diagnose runtime issues,
/// especially native library loading failures (like "Fail to load native Wispr library").
///
/// Log File Location:
/// %LOCALAPPDATA%\HyperWhisper\Logs\hyperwhisper-{date}.log
/// Example: C:\Users\{Username}\AppData\Local\HyperWhisper\Logs\hyperwhisper-2024-01-15.log
///
/// Log Levels:
/// - DEBUG: Detailed diagnostic information for development
/// - INFO: General operational information
/// - WARN: Warning conditions that might need attention
/// - ERROR: Error conditions that prevented an operation
///
/// Thread Safety and Buffering (#985):
/// A log call formats its line and picks its file on the calling thread, then
/// queues the pair; one background thread appends the queue to a single open
/// file and flushes it each time the queue runs dry, so a line is on disk within
/// milliseconds without the caller paying for a file open and close per line.
/// Every level is still written, Debug included, in every build.
/// Writes happen only under one lock, so lines never interleave.
///
/// Durability: an Error line, <see cref="Flush"/>, process exit and an unhandled
/// exception drain the queue on the CALLING thread, never waiting on the
/// background one, so the last lines before a crash reach the file. After exit
/// or a terminating exception every line is written that way.
/// The file is opened shared (read, write, delete) so the user, a test or log
/// cleanup can read or remove it while the app runs, and it is closed after a
/// few idle seconds.
///
/// Usage:
/// LoggingService.Info("Application started");
/// LoggingService.Error("Failed to load library", ex);
/// LoggingService.Debug($"Model path: {path}");
/// </summary>
public static class LoggingService
{
    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Directory where log files are stored.
    /// Uses %LOCALAPPDATA%\HyperWhisper\Logs\ to keep logs with other app data.
    /// </summary>
    public static string LogDirectory => AppPaths.LogsDirectory;

    /// <summary>
    /// Current log file path. Creates a new file each day for easier log management.
    /// Format: hyperwhisper-YYYY-MM-DD.log
    /// </summary>
    public static string CurrentLogPath => Path.Combine(
        LogDirectory,
        $"hyperwhisper-{DateTime.Now:yyyy-MM-dd}.log"
    );

    // Thread synchronization lock for file writes
    private static readonly object _writeLock = new();

    // =========================================================================
    // BUFFERED WRITER STATE (#985)
    // =========================================================================

    /// <summary>
    /// One formatted line and the file it belongs to. The path is fixed when the
    /// line is logged, so a line logged just before midnight, or before a
    /// harness moves <see cref="AppPaths"/>, still lands in the file it would
    /// have gone to when every line was written at once.
    /// </summary>
    private readonly record struct PendingLine(string Path, string Text);

    private static readonly ConcurrentQueue<PendingLine> _pending = new();
    private static readonly AutoResetEvent _wake = new(initialState: false);

    // 1 while a wake is already signalled and not yet taken by the writer, so a
    // burst of lines sets the event once instead of once per line.
    private static int _wakePending;
    private static int _writerStarted;

    // Set by process exit, a terminating exception, or a writer thread that
    // could not start: the background thread may never run again, so each line
    // is written by its caller.
    private static volatile bool _writeThrough;

    // Guarded by _writeLock. File.AppendAllText wrote UTF-8 without a BOM.
    private static StreamWriter? _writer;
    private static string? _writerPath;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// How long the file stays open with nothing to write. A dictation logs in
    /// bursts, so the handle is reused inside one, and an idle app holds no
    /// handle on its log.
    /// </summary>
    private static readonly TimeSpan IdleCloseDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Describes a file for a log line without naming it.
    ///
    /// Recordings, models and user-picked media all live under the user's
    /// profile, so a full path carries their Windows account name, and a
    /// user-picked file names their document as well. Neither belongs in a log
    /// that ships with a support bundle. The extension is what a diagnosis
    /// actually needs, so that is what this returns.
    /// </summary>
    /// <returns><c>"(none)"</c>, <c>"(no extension)"</c>, or e.g. <c>"*.wav"</c>.</returns>
    public static string DescribePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(none)";

        // Path.GetExtension does not throw on .NET Core and later: the
        // invalid-path-character check was removed in 2.1. It only ever reads
        // back from the last separator, so it cannot fault on a hostile string.
        var extension = Path.GetExtension(path);

        return string.IsNullOrEmpty(extension) ? "(no extension)" : $"*{extension}";
    }

    // =========================================================================
    // INITIALIZATION
    // =========================================================================

    /// <summary>
    /// Ensures the log directory exists. Called automatically on first log write.
    /// </summary>
    private static void EnsureLogDirectory()
    {
        if (!Directory.Exists(LogDirectory))
        {
            Directory.CreateDirectory(LogDirectory);
        }
    }

    // =========================================================================
    // LOGGING METHODS
    // =========================================================================

    /// <summary>
    /// Logs a DEBUG level message. Use for detailed diagnostic information.
    /// </summary>
    public static void Debug(string message)
    {
        WriteLog("DEBUG", message);
    }

    /// <summary>
    /// Logs an INFO level message. Use for general operational information.
    /// </summary>
    public static void Info(string message)
    {
        WriteLog("INFO", message);
    }

    /// <summary>
    /// Logs a WARN level message. Use for warning conditions.
    /// </summary>
    public static void Warn(string message)
    {
        WriteLog("WARN", message);
    }

    /// <summary>
    /// Logs a WARN level message with exception details. The exception message and
    /// stack trace go through <see cref="SentryService.RedactUserIdentifiers(string?)"/>:
    /// a .NET IO exception names the full path, which sits under the user's profile.
    /// </summary>
    public static void Warn(string message, Exception ex)
    {
        WriteLog("WARN", $"{message}\n  Exception: {ex.GetType().Name}: {SentryService.RedactUserIdentifiers(ex.Message)}\n  StackTrace: {SentryService.RedactUserIdentifiers(ex.StackTrace)}");
    }

    /// <summary>
    /// Logs an ERROR level message. Use for error conditions.
    /// Written to disk before this returns, with every line logged before it.
    /// </summary>
    public static void Error(string message)
    {
        WriteLog("ERROR", message, flushNow: true);
    }

    /// <summary>
    /// Logs an ERROR level message with full exception details.
    /// Includes inner exceptions for complete debugging information. Each message and
    /// stack trace is redacted the way <see cref="Warn(string, Exception)"/> does it.
    /// Written to disk before this returns, like <see cref="Error(string)"/>.
    /// </summary>
    public static void Error(string message, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(message);
        sb.AppendLine($"  Exception Type: {ex.GetType().FullName}");
        sb.AppendLine($"  Message: {SentryService.RedactUserIdentifiers(ex.Message)}");
        sb.AppendLine($"  StackTrace: {SentryService.RedactUserIdentifiers(ex.StackTrace)}");

        // Log inner exceptions for complete picture
        var inner = ex.InnerException;
        int depth = 1;
        while (inner != null)
        {
            sb.AppendLine($"  Inner Exception [{depth}]: {inner.GetType().FullName}");
            sb.AppendLine($"    Message: {SentryService.RedactUserIdentifiers(inner.Message)}");
            sb.AppendLine($"    StackTrace: {SentryService.RedactUserIdentifiers(inner.StackTrace)}");
            inner = inner.InnerException;
            depth++;
        }

        WriteLog("ERROR", sb.ToString(), flushNow: true);
    }

    // =========================================================================
    // SYSTEM DIAGNOSTICS
    // =========================================================================

    /// <summary>
    /// Logs comprehensive system information for debugging native library issues.
    ///
    /// CRITICAL FOR DIAGNOSING NATIVE LIBRARY FAILURES:
    /// - Architecture (x64/x86/ARM64) determines which DLL to load
    /// - OS version can affect DLL compatibility
    /// - Process architecture must match DLL architecture
    /// - Working directory affects relative path DLL loading
    /// </summary>
    public static void LogSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== SYSTEM INFORMATION ==========");
        sb.AppendLine($"  OS: {Environment.OSVersion}");
        sb.AppendLine($"  OS Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"  Process Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"  Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"  CLR Version: {Environment.Version}");
        sb.AppendLine($"  64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"  64-bit Process: {Environment.Is64BitProcess}");
        sb.AppendLine($"  Machine Name: {Environment.MachineName}");
        sb.AppendLine($"  Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine($"  Working Directory: {SentryService.RedactUserIdentifiers(Environment.CurrentDirectory)}");
        sb.AppendLine($"  Executable Path: {SentryService.RedactUserIdentifiers(Environment.ProcessPath)}");
        sb.AppendLine($"  AppDomain Base Directory: {SentryService.RedactUserIdentifiers(AppDomain.CurrentDomain.BaseDirectory)}");
        sb.AppendLine("=========================================");

        Info(sb.ToString());
    }

    /// <summary>
    /// Logs the contents of runtimes directory to verify native DLLs are present.
    ///
    /// WHISPER.NET NATIVE LIBRARY STRUCTURE:
    /// The Whisper.net library loads native DLLs from runtimes/{rid}/ subdirectories.
    /// This method enumerates all files to verify:
    /// 1. The runtimes folder exists
    /// 2. Platform-specific DLLs are present (win-x64, win-x86, win-arm64)
    /// 3. Files have correct extensions (.dll for Windows)
    ///
    /// Expected structure for Windows x64:
    ///   runtimes/win-x64/whisper.dll
    ///   runtimes/win-x64/ggml-whisper.dll
    ///   runtimes/win-x64/ggml-base-whisper.dll
    ///   runtimes/win-x64/ggml-cpu-whisper.dll
    /// </summary>
    public static void LogRuntimesDirectory()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== RUNTIMES DIRECTORY SCAN ==========");

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var runtimesDir = Path.Combine(baseDir, "runtimes");

        sb.AppendLine($"  Base Directory: {SentryService.RedactUserIdentifiers(baseDir)}");
        sb.AppendLine($"  Runtimes Directory: {SentryService.RedactUserIdentifiers(runtimesDir)}");
        sb.AppendLine($"  Runtimes Exists: {Directory.Exists(runtimesDir)}");

        if (Directory.Exists(runtimesDir))
        {
            sb.AppendLine("  Contents:");
            try
            {
                // List all subdirectories (platform-specific folders)
                foreach (var dir in Directory.GetDirectories(runtimesDir, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(runtimesDir, dir);
                    sb.AppendLine($"    [DIR] {relativePath}");
                }

                // List all DLL files
                foreach (var file in Directory.GetFiles(runtimesDir, "*.dll", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(runtimesDir, file);
                    var fileInfo = new FileInfo(file);
                    sb.AppendLine($"    [FILE] {relativePath} ({fileInfo.Length:N0} bytes)");
                }

                // Also check for .so files (Linux) just in case
                foreach (var file in Directory.GetFiles(runtimesDir, "*.so", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(runtimesDir, file);
                    var fileInfo = new FileInfo(file);
                    sb.AppendLine($"    [FILE] {relativePath} ({fileInfo.Length:N0} bytes)");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"    ERROR scanning directory: {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine("  WARNING: runtimes directory does not exist!");
            sb.AppendLine("  This is likely the cause of native library loading failures.");
        }

        sb.AppendLine("=============================================");
        Info(sb.ToString());
    }

    /// <summary>
    /// Logs all loaded assemblies in the current AppDomain.
    /// Useful for debugging assembly loading issues.
    /// </summary>
    public static void LogLoadedAssemblies() => LogLoadedAssemblies(null);

    /// <summary>
    /// Logs loaded assemblies, optionally filtered by name.
    /// </summary>
    /// <param name="filter">Optional filter - only assemblies containing this string (case-insensitive) will be logged.</param>
    public static void LogLoadedAssemblies(string? filter)
    {
        var sb = new StringBuilder();
        var hasFilter = !string.IsNullOrEmpty(filter);
        sb.AppendLine(hasFilter
            ? $"========== LOADED ASSEMBLIES (filter: {filter}) =========="
            : "========== LOADED ASSEMBLIES ==========");

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .OrderBy(a => a.FullName);

        var count = 0;
        foreach (var asm in assemblies)
        {
            var name = asm.GetName().Name ?? "";
            if (hasFilter && !name.Contains(filter!, StringComparison.OrdinalIgnoreCase))
                continue;

            count++;
            sb.AppendLine($"  {name} v{asm.GetName().Version}");
            sb.AppendLine($"    Location: {(string.IsNullOrEmpty(asm.Location) ? "(dynamic)" : asm.Location)}");
        }

        if (count == 0 && hasFilter)
        {
            sb.AppendLine($"  No assemblies matching '{filter}' found");
        }

        sb.AppendLine("=======================================");
        Debug(sb.ToString());
    }

    /// <summary>
    /// Logs PATH environment variable entries.
    /// Native DLLs may be loaded from PATH directories.
    /// </summary>
    public static void LogPathEnvironment()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== PATH ENVIRONMENT ==========");

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathVar.Split(Path.PathSeparator);

        foreach (var path in paths)
        {
            var exists = Directory.Exists(path);
            sb.AppendLine($"  [{(exists ? "OK" : "MISSING")}] {path}");
        }

        sb.AppendLine("======================================");
        Debug(sb.ToString());
    }

    /// <summary>
    /// Logs GPU/graphics adapter information using DirectX diagnostics.
    /// Useful for diagnosing GPU-related transcription hangs.
    ///
    /// DIAGNOSING GPU HANGS:
    /// - GPU memory exhaustion can cause hangs
    /// - Driver issues can cause compute shader failures
    /// - Multi-GPU systems may have adapter selection issues
    /// </summary>
    public static void LogGpuDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== GPU DIAGNOSTICS ==========");

        try
        {
            // Log basic GPU info from environment
            sb.AppendLine($"  Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"  Thread ID: {Environment.CurrentManagedThreadId}");

            // Try to get GPU info via WMI (Windows Management Instrumentation)
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                var gpuIndex = 0;
                foreach (var obj in searcher.Get())
                {
                    sb.AppendLine($"  GPU #{gpuIndex}:");
                    sb.AppendLine($"    Name: {obj["Name"]}");
                    sb.AppendLine($"    Driver Version: {obj["DriverVersion"]}");
                    sb.AppendLine($"    Status: {obj["Status"]}");

                    // AdapterRAM is in bytes, convert to MB
                    if (obj["AdapterRAM"] != null)
                    {
                        var ramBytes = Convert.ToUInt64(obj["AdapterRAM"]);
                        sb.AppendLine($"    Adapter RAM: {ramBytes / 1024 / 1024} MB");
                    }

                    sb.AppendLine($"    Video Processor: {obj["VideoProcessor"]}");
                    sb.AppendLine($"    Current Resolution: {obj["CurrentHorizontalResolution"]}x{obj["CurrentVerticalResolution"]}");
                    gpuIndex++;
                }
            }
            catch (Exception wmiEx)
            {
                sb.AppendLine($"  WMI GPU query failed: {wmiEx.Message}");
            }

            // Log process memory usage (can indicate GPU memory pressure)
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                sb.AppendLine($"  Process Memory:");
                sb.AppendLine($"    Working Set: {process.WorkingSet64 / 1024 / 1024} MB");
                sb.AppendLine($"    Private Memory: {process.PrivateMemorySize64 / 1024 / 1024} MB");
                sb.AppendLine($"    Virtual Memory: {process.VirtualMemorySize64 / 1024 / 1024} MB");
            }
            catch (Exception memEx)
            {
                sb.AppendLine($"  Memory query failed: {memEx.Message}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  GPU diagnostics failed: {ex.Message}");
        }

        sb.AppendLine("======================================");
        Info(sb.ToString());
    }

    /// <summary>
    /// Logs a performance marker with timestamp for tracking long-running operations.
    /// Use this to identify where hangs occur.
    /// </summary>
    public static void LogPerformanceMarker(string operation, string state)
    {
        Info($"[PERF] {operation}: {state} at {DateTime.Now:HH:mm:ss.fff}");
    }

    /// <summary>
    /// Logs current hypotheses (with rough priors) for intermittent "Transcribing..." hangs.
    /// This is written once per session so we can align future evidence to these priors.
    /// </summary>
    public static void LogHangHypotheses()
    {
        Info("========== INTERMITTENT TRANSCRIBING HANG HYPOTHESES ==========");
        Info("  [35%] Cloud/post-processing HTTP waits (120s timeout + retries) keeping UI in transcribing state");
        Info("  [30%] WhisperNet GPU inference occasional stalls (D3D11/driver contention or large audio)");
        Info("  [15%] UI thread backlog from synchronous work (history save, overlay/paste) delaying dispatcher updates");
        Info("  [10%] Audio handoff/file lock contention between NAudio writer and SaveAudioFile retries");
        Info("  [10%] Hotkey/state re-entry causing IsRecording/IsTranscribing to desynchronize overlay hide");
        Info("================================================================");
    }

    // =========================================================================
    // CORE LOG WRITING
    // =========================================================================

    /// <summary>
    /// Formats a log entry and queues it for the background writer.
    ///
    /// Format: [YYYY-MM-DD HH:mm:ss.fff] [LEVEL] [ThreadId] Message
    ///
    /// The timestamp, thread id and target file are taken here, on the caller's
    /// thread, so they are what they were when every line was written at once.
    /// <paramref name="flushNow"/> (Error lines) writes the queue out before
    /// returning.
    /// </summary>
    private static void WriteLog(string level, string message, bool flushNow = false)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var threadId = Environment.CurrentManagedThreadId;
            var logLine = $"[{timestamp}] [{level,-5}] [T{threadId:D4}] {message}{Environment.NewLine}";

            _pending.Enqueue(new PendingLine(CurrentLogPath, logLine));
            EnsureWriterStarted();

            if (flushNow || _writeThrough)
            {
                Flush();
                return;
            }

            if (Interlocked.Exchange(ref _wakePending, 1) == 0)
            {
                _wake.Set();
            }
        }
        catch
        {
            // Silently fail if logging itself fails - don't crash the app
            // In production, we might want to fall back to EventLog
        }
    }

    /// <summary>
    /// Writes every queued line to disk on the calling thread and returns when
    /// they are there (in the OS file cache, as File.AppendAllText left them).
    ///
    /// It takes the write lock and drains the queue itself instead of waiting
    /// for the background thread, so it is safe from a crash or exit handler
    /// where that thread may never run again. Never throws.
    ///
    /// If it leaves the file open, it wakes the background thread: that thread
    /// may be parked with no timeout because it last saw no file open, and only
    /// it closes an idle file.
    /// </summary>
    public static void Flush()
    {
        try
        {
            bool leftOpen;
            lock (_writeLock)
            {
                DrainLocked();
                leftOpen = _writer is not null;
            }

            if (leftOpen && Interlocked.Exchange(ref _wakePending, 1) == 0)
            {
                _wake.Set();
            }
        }
        catch
        {
            // Logging never crashes the app.
        }
    }

    /// <summary>
    /// Starts the writer thread and hooks process exit and unhandled exceptions,
    /// once, on the first line logged.
    /// </summary>
    private static void EnsureWriterStarted()
    {
        if (Volatile.Read(ref _writerStarted) == 1 || Interlocked.CompareExchange(ref _writerStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            var thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "HyperWhisper.LogWriter"
            };
            thread.Start();
        }
        catch
        {
            // No writer thread: every caller writes its own line, as before #985.
            _writeThrough = true;
        }
    }

    /// <summary>
    /// The process is ending: write what is queued, and write each later line
    /// (OnExit's last lines, other exit handlers) at once, since the background
    /// thread may not be scheduled again.
    /// </summary>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        _writeThrough = true;
        Flush();
    }

    /// <summary>
    /// An unhandled exception: write what is queued now. When it terminates the
    /// process (.NET raises no ProcessExit then), every later line - App's own
    /// handler logs after this one - is written by its caller.
    /// </summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.IsTerminating)
        {
            _writeThrough = true;
        }

        Flush();
    }

    /// <summary>
    /// The background writer: waits for lines, writes the whole queue, and
    /// flushes the file when it runs dry. With nothing to write for
    /// <see cref="IdleCloseDelay"/> it closes the file.
    /// </summary>
    private static void WriterLoop()
    {
        while (true)
        {
            try
            {
                // Racy read on purpose: a stale answer costs one extra wake-up or
                // keeps an idle file open until the next line.
                var timeout = Volatile.Read(ref _writer) is null ? Timeout.InfiniteTimeSpan : IdleCloseDelay;
                var woken = _wake.WaitOne(timeout);

                // Cleared BEFORE draining: a line queued after this point sets the
                // event again, and one queued before it is drained below.
                Interlocked.Exchange(ref _wakePending, 0);

                lock (_writeLock)
                {
                    DrainLocked();
                    if (!woken && _pending.IsEmpty)
                    {
                        CloseWriterLocked();
                    }
                }
            }
            catch
            {
                // Logging never crashes the app; the next wake-up tries again.
            }
        }
    }

    /// <summary>
    /// Writes the whole queue, then flushes the file. Caller holds _writeLock.
    /// A line whose write fails is dropped, as a failed File.AppendAllText was,
    /// and the file is closed so the next line opens it afresh.
    ///
    /// Once per batch it checks that the open file still has its name. If it
    /// was deleted or renamed (a user, a support script), the batch goes to a
    /// new file at that path, as the old open-append-close per line did,
    /// instead of an orphaned one.
    /// </summary>
    private static void DrainLocked()
    {
        if (_writer is not null && !_pending.IsEmpty && !File.Exists(_writerPath))
        {
            CloseWriterLocked();
        }

        while (_pending.TryDequeue(out var line))
        {
            try
            {
                if (_writer is null || !string.Equals(_writerPath, line.Path, StringComparison.OrdinalIgnoreCase))
                {
                    CloseWriterLocked();
                    OpenWriterLocked(line.Path);
                }

                _writer!.Write(line.Text);
            }
            catch
            {
                CloseWriterLocked();
            }
        }

        try
        {
            _writer?.Flush();
        }
        catch
        {
            CloseWriterLocked();
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for append, creating its directory. Shared
    /// read, write and delete: a reader of the live file, a test, or
    /// <see cref="CleanupOldLogs"/> must not be refused while it is open.
    /// Caller holds _writeLock.
    /// </summary>
    private static void OpenWriterLocked(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // bufferSize 1 turns off FileStream's own buffer: the StreamWriter is the
        // one buffer, and its Flush goes straight to the OS.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        try
        {
            _writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 16 * 1024);
            _writerPath = path;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Flushes and closes the open file, if any. Never throws. Caller holds _writeLock.
    /// </summary>
    private static void CloseWriterLocked()
    {
        var writer = _writer;
        _writer = null;
        _writerPath = null;

        try
        {
            writer?.Dispose();
        }
        catch
        {
            // The stream is closed by Dispose even when its flush fails.
        }
    }

    // =========================================================================
    // UTILITY METHODS
    // =========================================================================

    /// <summary>
    /// Opens the log directory in Windows Explorer.
    /// Useful for the user to access logs.
    /// </summary>
    public static void OpenLogDirectory()
    {
        EnsureLogDirectory();
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{LogDirectory}\"",
            UseShellExecute = true
        });

        if (process == null)
        {
            throw new InvalidOperationException("Windows Explorer did not start.");
        }
    }

    /// <summary>
    /// Exports privacy-safe diagnostic files for support.
    /// Includes app logs and system information, but no transcripts, audio, or settings.
    /// </summary>
    public static string ExportDiagnostics(string destinationPath)
    {
        EnsureLogDirectory();

        // Queued lines reach their files first, including a file other than
        // today's when the export runs just after midnight.
        Flush();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"hyperwhisper-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}");
        var logsRoot = Path.Combine(tempRoot, "logs");

        try
        {
            Directory.CreateDirectory(logsRoot);

            foreach (var logPath in Directory.GetFiles(LogDirectory, "hyperwhisper-*.log"))
            {
                var destination = Path.Combine(logsRoot, Path.GetFileName(logPath));
                if (string.Equals(logPath, CurrentLogPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Under the lock, with the queue drained: the copy ends on a
                    // whole line and holds every line logged before the export.
                    lock (_writeLock)
                    {
                        DrainLocked();
                        CopyLogFile(logPath, destination);
                    }
                }
                else
                {
                    CopyLogFile(logPath, destination);
                }
            }

            File.WriteAllText(Path.Combine(tempRoot, "system-info.txt"), BuildSystemInfo(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(tempRoot, "runtime-state.txt"), BuildRuntimeStateSnapshot(), Encoding.UTF8);

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            ZipFile.CreateFromDirectory(tempRoot, destinationPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            Info($"LoggingService: Exported diagnostics to {SentryService.RedactUserIdentifiers(destinationPath)}");
            return destinationPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Warn($"LoggingService: Failed to clean diagnostics temp directory: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Copies a log file that the writer may hold open. Not File.Copy: the
    /// source is opened sharing read, write and delete, so a handle open for
    /// writing on it cannot make the copy fail with a sharing violation.
    /// </summary>
    private static void CopyLogFile(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Dispose();

        // File.Copy kept the source's time, which the zip entry carries.
        File.SetLastWriteTime(destination, File.GetLastWriteTime(source));
    }

    private static string BuildSystemInfo()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";
        var process = System.Diagnostics.Process.GetCurrentProcess();

        return $"""
            HyperWhisper Diagnostic Report
            ==============================
            Date: {DateTimeOffset.Now:O}
            App Version: {version}
            Windows Version: {Environment.OSVersion.VersionString}
            OS Architecture: {RuntimeInformation.OSArchitecture}
            Process Architecture: {RuntimeInformation.ProcessArchitecture}
            Framework: {RuntimeInformation.FrameworkDescription}
            Machine Name: {Environment.MachineName}
            Processor Count: {Environment.ProcessorCount}
            Working Set MB: {process.WorkingSet64 / 1024 / 1024}
            Log Directory: {SentryService.RedactUserIdentifiers(LogDirectory)}

            Included Files:
            - logs/hyperwhisper-*.log
            - system-info.txt
            - runtime-state.txt

            Note: Transcripts, audio recordings, app settings, and API keys are not added to this export.
            Update events are included in the app logs when the Windows updater runs.
            """;
    }

    private static string BuildRuntimeStateSnapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("HyperWhisper Runtime State Snapshot");
        sb.AppendLine("===================================");
        sb.AppendLine($"Date: {DateTimeOffset.Now:O}");
        sb.AppendLine();

        AppendSettingsSnapshot(sb);
        AppendLocalApiSnapshot(sb);
        AppendProviderKeySnapshot(sb);
        AppendModelSnapshot(sb);
        AppendCustomEndpointSnapshot(sb);

        sb.AppendLine();
        sb.AppendLine("Privacy note: This file records feature state, counts, and API-key presence only.");
        sb.AppendLine("It does not include API key values, custom endpoint URLs, transcripts, audio, prompts, or raw app settings.");
        return sb.ToString();
    }

    private static void AppendSettingsSnapshot(StringBuilder sb)
    {
        try
        {
            var settings = SettingsService.Instance;
            var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(settings.StreamingProvider);

            sb.AppendLine("Settings");
            sb.AppendLine("--------");
            sb.AppendLine($"Streaming enabled: {settings.StreamingEnabled}");
            sb.AppendLine($"Streaming provider: {provider.DisplayName()} ({provider.StorageValue()})");
            sb.AppendLine($"Streaming language: {settings.StreamingLanguage}");
            sb.AppendLine($"Streaming Deepgram model: {settings.StreamingDeepgramModel}");
            sb.AppendLine($"Streaming fast formatting: {settings.StreamingFastFormatting}");
            sb.AppendLine($"Parakeet enabled: {settings.ParakeetEnabled}");
            sb.AppendLine($"Store recordings as M4A: {settings.StoreAsM4A}");
            sb.AppendLine($"Keep microphone warm: {settings.KeepMicrophoneWarm}");
            sb.AppendLine($"Media control mode: {settings.MediaControlMode}");
            sb.AppendLine($"Auto paste enabled: {settings.AutoPasteEnabled}");
            sb.AppendLine($"Restore clipboard after paste: {settings.RestoreClipboardAfterPaste}");
            sb.AppendLine($"Hide from clipboard history: {settings.HideFromClipboardHistory}");
            sb.AppendLine($"Error logging enabled: {settings.EnableErrorLogging}");
            sb.AppendLine($"Automatic update checks: {settings.CheckForUpdatesAutomatically}");
            sb.AppendLine($"Theme mode: {settings.ThemeMode}");
            sb.AppendLine($"Selected mode configured: {settings.SelectedModeId.HasValue}");
            sb.AppendLine($"Last selected local model configured: {!string.IsNullOrWhiteSpace(settings.LastSelectedModel)}");
            sb.AppendLine($"Recording folder kind: {DescribeRecordingFolder(settings.RecordingsFolder)}");
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Settings snapshot failed: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
        }
    }

    private static void AppendLocalApiSnapshot(StringBuilder sb)
    {
        try
        {
            var settings = SettingsService.Instance;
            var server = LocalApiServer.Instance;

            sb.AppendLine("Local API");
            sb.AppendLine("---------");
            sb.AppendLine($"Setting enabled: {settings.LocalApiServerEnabled}");
            sb.AppendLine($"Running: {server.IsRunning}");
            sb.AppendLine($"Listening port: {server.ListeningPort}");
            sb.AppendLine($"Persisted port: {settings.LocalApiServerPersistedPort}");
            sb.AppendLine($"Bearer token present: {!string.IsNullOrWhiteSpace(server.BearerToken)}");
            sb.AppendLine($"Last error present: {!string.IsNullOrWhiteSpace(server.LastError)}");
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Local API snapshot failed: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
        }
    }

    private static void AppendProviderKeySnapshot(StringBuilder sb)
    {
        try
        {
            var apiKeys = ApiKeyService.Instance;

            sb.AppendLine("Provider Configuration");
            sb.AppendLine("----------------------");
            sb.AppendLine("Transcription API keys:");
            foreach (var provider in Enum.GetValues<CloudTranscriptionProvider>())
            {
                if (provider == CloudTranscriptionProvider.None)
                    continue;

                sb.AppendLine($"- {provider.GetDisplayName()} ({provider.GetIdentifier()}): key required={provider.RequiresApiKey()}, key present={HasTranscriptionProviderKey(apiKeys, provider)}");
            }

            sb.AppendLine();
            sb.AppendLine("Post-processing API keys:");
            foreach (var provider in Enum.GetValues<PostProcessingProvider>())
            {
                if (provider == PostProcessingProvider.None)
                    continue;

                sb.AppendLine($"- {provider.ToDisplayName()} ({provider.ToStringValue()}): key required={provider.RequiresApiKey()}, key present={HasPostProcessingProviderKey(apiKeys, provider)}");
            }

            sb.AppendLine();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Provider configuration snapshot failed: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
        }
    }

    private static void AppendModelSnapshot(StringBuilder sb)
    {
        try
        {
            var whisperService = new WhisperModelService();
            var parakeetService = new ParakeetModelService();
            var localLlmService = new LocalLlmModelService();

            var whisperInstalled = WhisperModelInfo.AllModels.Count(whisperService.IsModelDownloaded);
            var parakeetInstalled = ParakeetModelInfo.AllModels.Count(parakeetService.IsModelDownloaded);
            var localLlmInstalled = LocalLlmModelInfo.AllModels.Count(localLlmService.IsModelDownloaded);

            sb.AppendLine("Local Models");
            sb.AppendLine("------------");
            sb.AppendLine($"Whisper installed: {whisperInstalled} of {WhisperModelInfo.AllModels.Length}");
            sb.AppendLine($"Whisper model directory exists: {Directory.Exists(WhisperModelService.ModelsDirectory)}");
            sb.AppendLine($"Parakeet installed: {parakeetInstalled} of {ParakeetModelInfo.AllModels.Length}");
            sb.AppendLine($"Parakeet model directory exists: {Directory.Exists(ParakeetModelService.ModelsDirectory)}");
            sb.AppendLine($"Local LLM installed: {localLlmInstalled} of {LocalLlmModelInfo.AllModels.Length}");
            sb.AppendLine($"Local LLM model directory exists: {Directory.Exists(LocalLlmModelService.ModelsDirectory)}");
            sb.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Local model snapshot failed: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
        }
    }

    private static void AppendCustomEndpointSnapshot(StringBuilder sb)
    {
        try
        {
            var endpoints = SettingsService.Instance.CustomEndpoints;
            var verified = endpoints.Count(e => e.LastTestSuccess == true);
            var local = endpoints.Count(e => IsLocalEndpoint(e.EndpointURL));

            sb.AppendLine("Custom OpenAI-Compatible Endpoints");
            sb.AppendLine("----------------------------------");
            sb.AppendLine($"Configured endpoints: {endpoints.Count}");
            sb.AppendLine($"Verified endpoints: {verified}");
            sb.AppendLine($"Local/private-network endpoints: {local}");
            sb.AppendLine($"Remote endpoints: {endpoints.Count - local}");
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Custom endpoint snapshot failed: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
        }
    }

    // The HW-Cloud-routed three report true here on purpose (they take no BYOK
    // key); every other provider answers from the shared key map (issue #1220),
    // and None has no slot, so false.
    private static bool HasTranscriptionProviderKey(ApiKeyService apiKeys, CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.HyperWhisperCloud => true,
        CloudTranscriptionProvider.MicrosoftAzureSpeech => true,
        CloudTranscriptionProvider.GoogleSpeech => true,
        _ => apiKeys.HasApiKey(provider)
    };

    private static bool HasPostProcessingProviderKey(ApiKeyService apiKeys, PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.HyperWhisperCloud => true,
        PostProcessingProvider.LocalLlm => false,
        _ => provider.RequiresApiKey() && apiKeys.HasApiKey(provider)
    };

    private static string DescribeRecordingFolder(string folder)
    {
        var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HyperWhisper", "recordings");
        var legacyFolder = SettingsService.GetLegacyAudioFolder();

        if (string.Equals(folder, defaultFolder, StringComparison.OrdinalIgnoreCase))
            return "default-documents";

        if (string.Equals(folder, legacyFolder, StringComparison.OrdinalIgnoreCase))
            return "legacy-local-app-data";

        return "custom";
    }

    private static bool IsLocalEndpoint(string endpointUrl)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
            return false;

        if (uri.IsLoopback)
            return true;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!System.Net.IPAddress.TryParse(host, out var address))
            return false;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        return false;
    }

    /// <summary>
    /// Cleans up old log files (older than specified days).
    /// </summary>
    public static void CleanupOldLogs(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "hyperwhisper-*.log"))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                        Info($"Deleted old log file: {fileInfo.Name}");
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
