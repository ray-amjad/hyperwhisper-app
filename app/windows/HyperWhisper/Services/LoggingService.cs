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
/// Thread Safety:
/// Uses lock-based synchronization to ensure thread-safe file writing.
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
    /// Logs a WARN level message with exception details.
    /// </summary>
    public static void Warn(string message, Exception ex)
    {
        WriteLog("WARN", $"{message}\n  Exception: {ex.GetType().Name}: {ex.Message}\n  StackTrace: {ex.StackTrace}");
    }

    /// <summary>
    /// Logs an ERROR level message. Use for error conditions.
    /// </summary>
    public static void Error(string message)
    {
        WriteLog("ERROR", message);
    }

    /// <summary>
    /// Logs an ERROR level message with full exception details.
    /// Includes inner exceptions for complete debugging information.
    /// </summary>
    public static void Error(string message, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(message);
        sb.AppendLine($"  Exception Type: {ex.GetType().FullName}");
        sb.AppendLine($"  Message: {ex.Message}");
        sb.AppendLine($"  StackTrace: {ex.StackTrace}");

        // Log inner exceptions for complete picture
        var inner = ex.InnerException;
        int depth = 1;
        while (inner != null)
        {
            sb.AppendLine($"  Inner Exception [{depth}]: {inner.GetType().FullName}");
            sb.AppendLine($"    Message: {inner.Message}");
            sb.AppendLine($"    StackTrace: {inner.StackTrace}");
            inner = inner.InnerException;
            depth++;
        }

        WriteLog("ERROR", sb.ToString());
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
        sb.AppendLine($"  Working Directory: {Environment.CurrentDirectory}");
        sb.AppendLine($"  Executable Path: {Environment.ProcessPath}");
        sb.AppendLine($"  AppDomain Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
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

        sb.AppendLine($"  Base Directory: {baseDir}");
        sb.AppendLine($"  Runtimes Directory: {runtimesDir}");
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

    /// <summary>
    /// Checks if a specific DLL can be loaded using NativeLibrary.TryLoad.
    /// Useful for diagnosing which specific DLL is failing to load.
    /// </summary>
    public static void TestDllLoading(string dllPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"========== TESTING DLL LOAD: {Path.GetFileName(dllPath)} ==========");
        sb.AppendLine($"  Full Path: {dllPath}");
        sb.AppendLine($"  File Exists: {File.Exists(dllPath)}");

        if (File.Exists(dllPath))
        {
            var fileInfo = new FileInfo(dllPath);
            sb.AppendLine($"  File Size: {fileInfo.Length:N0} bytes");
            sb.AppendLine($"  Last Modified: {fileInfo.LastWriteTime}");

            try
            {
                // Try to load the DLL using NativeLibrary
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(dllPath, out var handle))
                {
                    sb.AppendLine("  Result: SUCCESS - DLL loaded successfully");
                    sb.AppendLine($"  Handle: 0x{handle:X}");

                    // Free the handle
                    System.Runtime.InteropServices.NativeLibrary.Free(handle);
                    sb.AppendLine("  Handle freed successfully");
                }
                else
                {
                    sb.AppendLine("  Result: FAILED - NativeLibrary.TryLoad returned false");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Result: EXCEPTION - {ex.GetType().Name}");
                sb.AppendLine($"  Message: {ex.Message}");
                sb.AppendLine($"  StackTrace: {ex.StackTrace}");
            }
        }
        else
        {
            sb.AppendLine("  Result: FILE NOT FOUND");
        }

        sb.AppendLine("========================================");
        Info(sb.ToString());
    }

    // =========================================================================
    // CORE LOG WRITING
    // =========================================================================

    /// <summary>
    /// Writes a formatted log entry to the log file.
    ///
    /// Format: [YYYY-MM-DD HH:mm:ss.fff] [LEVEL] [ThreadId] Message
    ///
    /// Thread-safe via lock to prevent file corruption from concurrent writes.
    /// </summary>
    private static void WriteLog(string level, string message)
    {
        try
        {
            EnsureLogDirectory();

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var threadId = Environment.CurrentManagedThreadId;
            var logLine = $"[{timestamp}] [{level,-5}] [T{threadId:D4}] {message}{Environment.NewLine}";

            lock (_writeLock)
            {
                File.AppendAllText(CurrentLogPath, logLine);
            }
        }
        catch
        {
            // Silently fail if logging itself fails - don't crash the app
            // In production, we might want to fall back to EventLog
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
                    lock (_writeLock)
                    {
                        File.Copy(logPath, destination, overwrite: true);
                    }
                }
                else
                {
                    File.Copy(logPath, destination, overwrite: true);
                }
            }

            File.WriteAllText(Path.Combine(tempRoot, "system-info.txt"), BuildSystemInfo(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(tempRoot, "runtime-state.txt"), BuildRuntimeStateSnapshot(), Encoding.UTF8);

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            ZipFile.CreateFromDirectory(tempRoot, destinationPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            Info($"LoggingService: Exported diagnostics to {destinationPath}");
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
            Log Directory: {LogDirectory}

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

    private static bool HasTranscriptionProviderKey(ApiKeyService apiKeys, CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => apiKeys.HasApiKey(PostProcessingProvider.OpenAI),
        CloudTranscriptionProvider.Groq => apiKeys.HasApiKey(PostProcessingProvider.Groq),
        CloudTranscriptionProvider.Gemini => apiKeys.HasApiKey(PostProcessingProvider.Gemini),
        CloudTranscriptionProvider.Grok => apiKeys.HasApiKey(PostProcessingProvider.Grok),
        CloudTranscriptionProvider.Deepgram => apiKeys.HasApiKey(TranscriptionApiKeyType.Deepgram),
        CloudTranscriptionProvider.AssemblyAI => apiKeys.HasApiKey(TranscriptionApiKeyType.AssemblyAI),
        CloudTranscriptionProvider.ElevenLabs => apiKeys.HasApiKey(TranscriptionApiKeyType.ElevenLabs),
        CloudTranscriptionProvider.Mistral => apiKeys.HasApiKey(TranscriptionApiKeyType.Mistral),
        CloudTranscriptionProvider.Soniox => apiKeys.HasApiKey(TranscriptionApiKeyType.Soniox),
        CloudTranscriptionProvider.HyperWhisperCloud => true,
        CloudTranscriptionProvider.MicrosoftAzureSpeech => true,
        CloudTranscriptionProvider.GoogleSpeech => true,
        _ => false
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
    /// Gets the current log file contents.
    /// </summary>
    public static string GetCurrentLogContents()
    {
        if (File.Exists(CurrentLogPath))
        {
            lock (_writeLock)
            {
                return File.ReadAllText(CurrentLogPath);
            }
        }
        return string.Empty;
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
