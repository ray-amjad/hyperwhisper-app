// PARAKEET TRANSCRIPTION SERVICE — INITIALIZE
// Starts the daemon for a model and waits for its READY line.

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    // =========================================================================
    // DAEMON PATHS
    // =========================================================================

    /// <summary>
    /// Resolves the absolute path to the parakeet-engine.exe daemon binary.
    /// </summary>
    private static string GetDaemonPath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "parakeet-engine", "parakeet-engine.exe");
    }

    /// <summary>
    /// Resolves the absolute path to the Silero VAD ONNX model used by the daemon.
    /// </summary>
    private static string GetVadModelPath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "parakeet-engine", "silero_vad.onnx");
    }

    /// <param name="advanceTeardownGeneration">
    /// False for the auto-restart in <see cref="TranscribeAsync"/> (reloading the same model
    /// after a crash is not a teardown, so requests queued behind it stay current) and for
    /// <see cref="ReloadWhenIdleAsync"/>, which advanced it already when it found the daemon idle.
    /// </param>
    /// <param name="transcriptionLockHeld">
    /// True only from <see cref="ReloadWhenIdleAsync"/>, which holds the lock: the teardown
    /// must not wait on it or cancel anyone.
    /// </param>
    /// <param name="idleReloadCancellation">
    /// Only from <see cref="ReloadWhenIdleAsync"/>: cancelled by a GUI teardown. It cuts the
    /// old daemon's graceful-exit wait and the READY wait short; the load then stops the
    /// daemon it started and throws <see cref="OperationCanceledException"/>.
    /// </param>
    private async Task InitializeCoreAsync(
        string modelDirectory,
        string? language,
        bool advanceTeardownGeneration,
        bool transcriptionLockHeld = false,
        CancellationToken idleReloadCancellation = default)
    {
        LoggingService.Info("========== INITIALIZING PARAKEET TRANSCRIPTION SERVICE ==========");
        // The model id, not the directory. Models live under
        // %LOCALAPPDATA%\HyperWhisper\Models, so the directory carries the user's
        // Windows account name; the leaf folder IS the model id and is the only part
        // that answers "which model was this".
        LoggingService.Info($"  Model: {Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}");

        // Dispose any existing daemon first
        DisposeModelCore(advanceTeardownGeneration, transcriptionLockHeld, idleReloadCancellation);
        idleReloadCancellation.ThrowIfCancellationRequested();

        var daemonPath = GetDaemonPath();
        var vadModelPath = GetVadModelPath();

        // Guard: validate daemon binary exists
        if (!File.Exists(daemonPath))
        {
            LoggingService.Error("ParakeetTranscriptionService: Daemon binary not found in the app directory");
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                // The path is out of the message too, not just the log line: this
                // message travels one frame up and is logged again as ex.Message by
                // TranscriptionRetryHandler and MainViewModel. GetUserMessage() is a
                // fixed sentence for this code, so nothing the user sees changes.
                "Parakeet engine binary not found in the app directory",
                "Parakeet");
        }

        // Guard: validate model directory exists
        if (!Directory.Exists(modelDirectory))
        {
            LoggingService.Error("ParakeetTranscriptionService: Model directory not found");
            throw new TranscriptionException(
                TranscriptionErrorCode.OnnxModelFileMissing,
                // Same reason as above - GetUserMessage() covers this code too.
                "Model directory not found",
                "Parakeet");
        }

        // Guard: validate VAD model exists
        if (!File.Exists(vadModelPath))
        {
            LoggingService.Warn("ParakeetTranscriptionService: VAD model not found in the app directory, proceeding without VAD");
        }

        // The daemon THIS load starts. Every kill below targets it, never whatever
        // _daemonProcess holds by then: a failed or cancelled load must not stop a
        // daemon someone else started (#1608 review).
        Process? process = null;
        try
        {
            // STEP 1: Spawn the daemon process
            LoggingService.Info("Step 1: Spawning parakeet-engine daemon...");

            // Resolve which engine the daemon should load from the model catalog
            // (the model directory's leaf name is the model Id). Falls back to the
            // Parakeet transducer for anything not in the catalog.
            var modelId = Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var modelInfo = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
            var engineArg = modelInfo?.DaemonEngineArg ?? "nemo_transducer";
            _isQwen3 = modelInfo?.Engine == ParakeetEngine.Qwen3;
            _isOnline = modelInfo?.Engine == ParakeetEngine.NemotronMl;
            LoggingService.Info($"  Engine: {engineArg}");

            // The caller passes null for "auto-detect" (MainViewModel converts the
            // "auto" UI value to null). Qwen3 and Nemotron have real auto language
            // handling, so forward "auto" instead of defaulting those engines to English.
            var supportsLanguageHint = _isOnline || _isQwen3;
            var loadedLanguage = language ?? "auto";
            var daemonLanguage = supportsLanguageHint ? loadedLanguage : "auto";
            if (supportsLanguageHint)
            {
                LoggingService.Info($"  Language: {daemonLanguage}");
            }
            else
            {
                LoggingService.Info($"  Requested language '{loadedLanguage}' is auto-detected by Parakeet TDT (not applied)");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = daemonPath,
                // Pin the daemon's working directory to its own folder. The child inherits
                // HyperWhisper.exe's CWD by default (often C:\Users\<user> when launched from
                // the Start Menu), and the Windows DLL search order probes the CWD before %PATH%.
                // Pinning it to the engine folder — where the legit ONNX Runtime / DirectML
                // native DLLs are installed — prevents DLL planting from a user-writable CWD.
                WorkingDirectory = Path.GetDirectoryName(daemonPath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The daemon speaks raw UTF-8 on stdin/stdout (it puts both in _O_BINARY and
                // passes non-ASCII bytes through unescaped). Without these, redirected-stream
                // encoding defaults to the console code page — a legacy ANSI/OEM page on most
                // non-US Windows locales — which mojibakes accented/Cyrillic/Greek/CJK output
                // from multilingual Parakeet. UTF8Encoding(false) suppresses the BOM so the
                // stdin writer never prepends EF BB BF to the first JSON request line.
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };

            // Build arguments via ArgumentList so .NET applies correct Windows
            // quoting/escaping. String concatenation corrupts paths containing a
            // quote or a trailing backslash (e.g. a UNC path like \\server\share\),
            // where CommandLineToArgvW treats the trailing \" as an escaped quote.
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelDirectory);
            if (supportsLanguageHint)
            {
                startInfo.ArgumentList.Add("--language");
                startInfo.ArgumentList.Add(daemonLanguage);
            }
            else if (!string.IsNullOrWhiteSpace(language))
            {
                // Read only by a daemon older than --segment-join below: the
                // committed x64 parakeet-engine.exe predates it, and skips an
                // unknown flag and its value. Drop it once that exe is rebuilt.
                startInfo.ArgumentList.Add("--join-language");
                startInfo.ArgumentList.Add(language);
            }
            // The host, not the daemon, decides the segment join (#902). Nothing is
            // sent for auto: the daemon resolves an auto join itself.
            var segmentJoin = ResolveSegmentJoinArgument(language);
            if (segmentJoin != null)
            {
                startInfo.ArgumentList.Add("--segment-join");
                startInfo.ArgumentList.Add(segmentJoin);
            }
            startInfo.ArgumentList.Add("--vad-model");
            startInfo.ArgumentList.Add(vadModelPath);
            startInfo.ArgumentList.Add("--engine");
            startInfo.ArgumentList.Add(engineArg);

            // The argument SHAPE, not the command line. ArgumentList holds the model
            // directory and the VAD model path, both under the user's profile, so
            // joining it printed their Windows account name into the log file. The
            // engine, the language hint and the argument count are what a failed
            // start actually needs.
            LoggingService.Debug(
                $"ParakeetTranscriptionService: Starting daemon (engine={engineArg}, " +
                $"language_hint={(supportsLanguageHint ? daemonLanguage : "n/a")}, " +
                $"join_language={(!supportsLanguageHint && !string.IsNullOrWhiteSpace(language) ? language : "n/a")}, " +
                $"segment_join={segmentJoin ?? "n/a"}, " +
                $"arg_count={startInfo.ArgumentList.Count})");

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _daemonProcess = process;

            // Register crash detection before starting
            process.Exited += OnDaemonExited;

            if (!process.Start())
            {
                LoggingService.Error("ParakeetTranscriptionService: Failed to start daemon process");
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    "Failed to start parakeet-engine process",
                    "Parakeet");
            }
            var sinceStart = Stopwatch.StartNew();

            _stdinWriter = process.StandardInput;
            _stdoutReader = process.StandardOutput;

            LoggingService.Info($"  Daemon PID: {process.Id}");

            // STEP 2: Start background stderr reader for diagnostics
            LoggingService.Debug("Step 2: Starting stderr reader thread...");
            var stderrCapture = StartStderrReader(process);

            // STEP 3: Wait for READY signal on stdout.
            // Qwen3 loads ~1.2 GB of ONNX sessions on a cold cache — give it longer.
            var readySeconds = _isQwen3 ? 90 : (_isOnline ? 45 : 30);
            LoggingService.Info($"Step 3: Waiting for daemon READY signal ({readySeconds}s timeout)...");

            var readyTimeout = TimeSpan.FromSeconds(readySeconds);
            // An idle reload's teardown token ends the wait too (#1608 review); the catch
            // below tells it apart from the READY timeout.
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(idleReloadCancellation);
            readyCts.CancelAfter(readyTimeout);

            // The cause of a start failure, for its ERROR line and its one Sentry
            // report (#854). Called after the branch has its exit code and has
            // stopped the daemon, so the stderr tail is complete.
            IReadOnlyList<KeyValuePair<string, object>> StartFailureFields(string failure, int? exitCode, long elapsedMilliseconds)
            {
                var (lineCount, tail) = stderrCapture.TailSnapshot();
                return BuildStartFailureFields(
                    failure,
                    exitCode,
                    engineArg,
                    modelId,
                    supportsLanguageHint ? daemonLanguage : null,
                    readySeconds,
                    elapsedMilliseconds,
                    lineCount,
                    tail,
                    SentryService.RedactUserIdentifiers);
            }

            // WaitAsync, not only the read's token: a read on the daemon's pipe is not
            // guaranteed to observe cancellation, and a cancelled idle reload must hand the
            // lock back at once. The abandoned read ends when the daemon is killed below.
            var readLineTask = _stdoutReader.ReadLineAsync(readyCts.Token).AsTask();
            try
            {
                // ConfigureAwait(false): ReloadWhenIdleAsync holds the transcription lock
                // across this wait, and DisposeModel can block the UI thread on that lock
                // until this load has seen its cancel, so the continuation must not need
                // the UI thread (#1608).
                var line = await readLineTask.WaitAsync(readyCts.Token).ConfigureAwait(false);

                if (line == null)
                {
                    var elapsedMs = sinceStart.ElapsedMilliseconds;
                    var earlyExitCode = TryGetEarlyExitCode(process);
                    KillDaemonProcess(process);
                    await stderrCapture.WaitForEndAsync(TimeSpan.FromMilliseconds(StartFailureDiagnosticWaitMilliseconds)).ConfigureAwait(false);
                    ReportStartFailure(
                        "ParakeetTranscriptionService: Daemon closed stdout before sending READY",
                        StartFailureFields(StartFailureStdoutClosed, earlyExitCode, elapsedMs));

                    // The daemon died while ONNX Runtime parsed the model: a damaged
                    // ONNX file throws a native exception nothing catches, and the
                    // engine fail-fasts with 0xC0000409 before READY (#1598). Mark the
                    // model broken so it stops counting as installed and Model Library
                    // offers it for download again (the only way out for Qwen3, whose
                    // file sizes are not pinned). Any other exit (a missing DLL, an
                    // access violation, OOM, a kill, a bad argument, still running)
                    // is not something a re-download fixes, so it is left alone.
                    if (IsModelLoadCrashExitCode(earlyExitCode))
                    {
                        LocalModelHealth.MarkBroken(modelDirectory, "Parakeet engine crashed while loading the model");
                    }
                    else
                    {
                        LoggingService.Warn($"ParakeetTranscriptionService: Daemon exit code {FormatExitCode(earlyExitCode)} is not a model-load crash; the model is not marked broken");
                    }
                    throw new TranscriptionException(
                        TranscriptionErrorCode.DaemonStartFailed,
                        "Parakeet daemon closed stdout before sending READY signal",
                        "Parakeet");
                }

                LoggingService.Debug($"ParakeetTranscriptionService: Received from daemon: {DescribeDaemonResponse(line)}");

                // Parse the READY JSON
                using var readyDoc = JsonDocument.Parse(line);
                var root = readyDoc.RootElement;

                if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "ready")
                {
                    _activeProvider = root.TryGetProperty("provider", out var providerProp)
                        ? providerProp.GetString()
                        : "cpu";

                    _isReady = true;
                    _loadedModelId = Path.GetFileName(modelDirectory);
                    _lastModelDirectory = modelDirectory;
                    _lastLanguage = loadedLanguage;

                    LoggingService.Info($"  Daemon is READY (provider: {_activeProvider})");
                }
                else
                {
                    var errorMsg = root.TryGetProperty("error", out var errorProp)
                        ? errorProp.GetString() ?? "Unknown error"
                        : $"Unexpected response: {line}";

                    var elapsedMs = sinceStart.ElapsedMilliseconds;
                    var errorExitCode = TryGetEarlyExitCode(process, StartFailureDiagnosticWaitMilliseconds);
                    KillDaemonProcess(process);

                    // "Failed to load model" alone does not say WHY (#1598 review round 2):
                    // the daemon sends it for a missing sherpa-onnx/onnxruntime DLL, a
                    // type-initializer fault, OOM, a provider failure and a bad model file
                    // alike. Its stderr carries the cause, written before this line, so
                    // read that to its end (the daemon is gone) and mark the model broken
                    // only when it names a fault in the model's own files.
                    await stderrCapture.WaitForEndAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    ReportStartFailure(
                        $"ParakeetTranscriptionService: Daemon reported error: {errorMsg}",
                        StartFailureFields(StartFailureDaemonError, errorExitCode, elapsedMs));
                    if (IsModelFileLoadFailure(errorMsg, stderrCapture.Snapshot(), modelDirectory))
                    {
                        LocalModelHealth.MarkBroken(modelDirectory, "Parakeet engine could not load the model");
                    }
                    else if (IsModelLoadErrorResponse(errorMsg))
                    {
                        LoggingService.Warn("ParakeetTranscriptionService: The daemon's stderr names no fault in the model files (a runtime, provider or memory failure); the model is not marked broken");
                    }
                    throw new TranscriptionException(
                        TranscriptionErrorCode.DaemonStartFailed,
                        $"Parakeet daemon failed to initialize: {errorMsg}",
                        "Parakeet");
                }
            }
            catch (OperationCanceledException) when (idleReloadCancellation.IsCancellationRequested)
            {
                // A GUI teardown cancelled this idle reload: stop the daemon it started and
                // hand the lock back at once (ReloadWhenIdleAsync reports Cancelled).
                LoggingService.Info("ParakeetTranscriptionService: Idle reload cancelled while waiting for READY; stopping its daemon");
                StopDaemonInstance(process);
                _ = ObserveInFlightReadAsync(readLineTask);
                throw;
            }
            catch (OperationCanceledException)
            {
                var elapsedMs = sinceStart.ElapsedMilliseconds;
                var timeoutExitCode = TryGetEarlyExitCode(process, StartFailureDiagnosticWaitMilliseconds);
                KillDaemonProcess(process);
                _ = ObserveInFlightReadAsync(readLineTask);
                await stderrCapture.WaitForEndAsync(TimeSpan.FromMilliseconds(StartFailureDiagnosticWaitMilliseconds)).ConfigureAwait(false);
                ReportStartFailure(
                    $"ParakeetTranscriptionService: Daemon did not send READY within {readySeconds} seconds",
                    StartFailureFields(StartFailureReadyTimeout, timeoutExitCode, elapsedMs));
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    $"Parakeet daemon timed out waiting for READY signal ({readySeconds}s)",
                    "Parakeet");
            }
            catch (JsonException ex)
            {
                var elapsedMs = sinceStart.ElapsedMilliseconds;
                var invalidJsonExitCode = TryGetEarlyExitCode(process, StartFailureDiagnosticWaitMilliseconds);
                KillDaemonProcess(process);
                await stderrCapture.WaitForEndAsync(TimeSpan.FromMilliseconds(StartFailureDiagnosticWaitMilliseconds)).ConfigureAwait(false);
                ReportStartFailure(
                    "ParakeetTranscriptionService: Failed to parse daemon READY response",
                    StartFailureFields(StartFailureInvalidJson, invalidJsonExitCode, elapsedMs),
                    ex);
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    "Parakeet daemon sent invalid JSON on startup",
                    "Parakeet",
                    ex);
            }

            LoggingService.Info("========== PARAKEET TRANSCRIPTION SERVICE READY ==========");
        }
        catch (TranscriptionException)
        {
            // Re-throw TranscriptionExceptions as-is
            throw;
        }
        catch (OperationCanceledException) when (idleReloadCancellation.IsCancellationRequested)
        {
            // The idle reload's cancel, already handled above; not a start failure.
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ParakeetTranscriptionService: Unexpected error during initialization", ex);
            // Only what this load started; null when it failed before the spawn.
            KillDaemonProcess(process);
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                $"Failed to start Parakeet daemon: {ex.Message}",
                "Parakeet",
                ex);
        }
    }
}
