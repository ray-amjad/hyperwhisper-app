// PARAKEET TRANSCRIPTION SERVICE — INITIALIZE
// Starts the daemon for a model and waits for its READY line.

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
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

        var paths = ParakeetDaemonLaunchPlan.ValidatePaths(modelDirectory);

        // The daemon THIS load starts. Every kill below targets it, never whatever
        // _daemonProcess holds by then: a failed or cancelled load must not stop a
        // daemon someone else started (#1608 review).
        Process? process = null;
        try
        {
            // STEP 1: Spawn the daemon process
            LoggingService.Info("Step 1: Spawning parakeet-engine daemon...");

            var engine = ParakeetDaemonLaunchPlan.ResolveEngine(modelDirectory);
            _isQwen3 = engine.IsQwen3;
            _isOnline = engine.IsOnline;
            var launch = ParakeetDaemonLaunchPlan.Build(paths, modelDirectory, language, engine);

            process = new Process { StartInfo = launch.StartInfo, EnableRaisingEvents = true };
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
                    launch.Engine.EngineArg,
                    launch.Engine.ModelId,
                    launch.SupportsLanguageHint ? launch.DaemonLanguage : null,
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
                    _lastLanguage = launch.LoadedLanguage;

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
